using System.Text.Json;
using System.Xml.Linq;

namespace Corvid.Architecture;

/// <summary>Checks NuGet's resolved graph, including transitive dependencies and every restored target.</summary>
public static class BoundaryCheck
{
    // Keep this list explicit: a newly introduced infrastructure package needs architectural review.
    // Provider SDKs must never be added here; they belong in Corvid.Models.* adapters.
    private static readonly HashSet<string> CorePackages = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft.Extensions.AI.Abstractions",
        // The kernel's event log (C-002): Microsoft.Data.Sqlite and the native SQLite bundle it resolves.
        "Microsoft.Data.Sqlite",
        "Microsoft.Data.Sqlite.Core",
        "SQLitePCLRaw.bundle_e_sqlite3",
        "SQLitePCLRaw.core",
        "SQLitePCLRaw.lib.e_sqlite3",
        "SQLitePCLRaw.provider.e_sqlite3"
    };

    // Build tooling runs inside the compiler (Roslyn source generators), never in Corvid's process. It may use only
    // Roslyn and the netstandard2.0 packages Roslyn brings, and is consumed as an analyzer reference, which keeps it
    // out of every other project's restored graph.
    private static readonly HashSet<string> BuildToolProjects = new(StringComparer.Ordinal)
    {
        "Corvid.Tools.Generators"
    };

    private static readonly HashSet<string> BuildToolPackages = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft.CodeAnalysis.Analyzers",
        "Microsoft.CodeAnalysis.Common",
        "Microsoft.CodeAnalysis.CSharp",
        "Microsoft.NETCore.Platforms",
        "NETStandard.Library",
        "System.Buffers",
        "System.Collections.Immutable",
        "System.Memory",
        "System.Numerics.Vectors",
        "System.Reflection.Metadata",
        "System.Runtime.CompilerServices.Unsafe",
        "System.Text.Encoding.CodePages",
        "System.Threading.Tasks.Extensions"
    };

    // Native runtimes that adapters bring. CPU and GPU builds ship libonnxruntime (and onnxruntime-genai) under the same
    // file names, so a graph that resolves two flavours of one runtime loads whichever copy lands last. One per runtime.
    private static readonly Dictionary<string, (string Runtime, string Flavour)> NativeRuntimePackages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Microsoft.ML.OnnxRuntime"] = ("ONNX Runtime", "cpu"),
        ["Microsoft.ML.OnnxRuntime.Gpu"] = ("ONNX Runtime", "cuda"),
        ["Microsoft.ML.OnnxRuntime.Gpu.Linux"] = ("ONNX Runtime", "cuda"),
        ["Microsoft.ML.OnnxRuntime.Gpu.Windows"] = ("ONNX Runtime", "cuda"),
        ["Microsoft.ML.OnnxRuntime.DirectML"] = ("ONNX Runtime", "directml"),
        ["Microsoft.ML.OnnxRuntimeGenAI"] = ("ONNX Runtime GenAI", "cpu"),
        ["Microsoft.ML.OnnxRuntimeGenAI.Cuda"] = ("ONNX Runtime GenAI", "cuda"),
        ["Microsoft.ML.OnnxRuntimeGenAI.DirectML"] = ("ONNX Runtime GenAI", "directml"),
    };

    // Separate executables that Corvid and Vault start by path, never reference. The sandbox launcher has no
    // dependencies at all, so Vault can use it without taking on any of Corvid's code (corvid-agent.md §3.4).
    private static readonly HashSet<string> LauncherProjects = new(StringComparer.Ordinal)
    {
        "Corvid.Sandbox.Launcher"
    };

    // The composition root and the CLI executable (C-013). They are not core: they may reference adapters, and through
    // them resolve whatever an adapter brings (Starling, ONNX Runtime). Their own packages are listed here, and nothing
    // else may reference them except the CLI, which references the host. The core rule is unchanged.
    private static readonly HashSet<string> CompositionProjects = new(StringComparer.Ordinal)
    {
        "Corvid.Host",
        "Corvid.Cli"
    };

    private static readonly HashSet<string> CompositionPackages = new(StringComparer.OrdinalIgnoreCase)
    {
        // The generic host: configuration, DI, logging, options and their providers, as Microsoft.Extensions.Hosting resolves them.
        "Microsoft.Extensions.Configuration",
        "Microsoft.Extensions.Configuration.Abstractions",
        "Microsoft.Extensions.Configuration.Binder",
        "Microsoft.Extensions.Configuration.CommandLine",
        "Microsoft.Extensions.Configuration.EnvironmentVariables",
        "Microsoft.Extensions.Configuration.FileExtensions",
        "Microsoft.Extensions.Configuration.Json",
        "Microsoft.Extensions.Configuration.UserSecrets",
        "Microsoft.Extensions.DependencyInjection",
        "Microsoft.Extensions.DependencyInjection.Abstractions",
        "Microsoft.Extensions.Diagnostics",
        "Microsoft.Extensions.Diagnostics.Abstractions",
        "Microsoft.Extensions.FileProviders.Abstractions",
        "Microsoft.Extensions.FileProviders.Physical",
        "Microsoft.Extensions.FileSystemGlobbing",
        "Microsoft.Extensions.Hosting",
        "Microsoft.Extensions.Hosting.Abstractions",
        "Microsoft.Extensions.Logging",
        "Microsoft.Extensions.Logging.Abstractions",
        "Microsoft.Extensions.Logging.Configuration",
        "Microsoft.Extensions.Logging.Console",
        "Microsoft.Extensions.Logging.Debug",
        "Microsoft.Extensions.Logging.EventLog",
        "Microsoft.Extensions.Logging.EventSource",
        "Microsoft.Extensions.Options",
        "Microsoft.Extensions.Options.ConfigurationExtensions",
        "Microsoft.Extensions.Primitives",
        "System.Diagnostics.EventLog",
        // OpenTelemetry export of the spans the core libraries emit through System.Diagnostics (plan §16).
        "OpenTelemetry",
        "OpenTelemetry.Api",
        "OpenTelemetry.Api.ProviderBuilderExtensions",
        "OpenTelemetry.Exporter.OpenTelemetryProtocol",
        "OpenTelemetry.Extensions.Hosting"
    };

    // Front ends (C-019): the ACP agent and the OpenAI-compatible endpoint. Unlike the SDK surfaces below (which sit under
    // the host), they sit on the composition root like the CLI: each may reference Corvid.Host (and so resolves what it
    // resolves), only the CLI may reference a front end, and a front end never references the CLI. They bring no package of
    // their own; the HTTP front end's ASP.NET Core is a shared-framework reference, checked by FrameworkReferences below.
    private static readonly HashSet<string> FrontEndProjects = new(StringComparer.Ordinal)
    {
        "Corvid.Acp",
        "Corvid.Http"
    };

    // Shared frameworks other than Microsoft.NETCore.App, and the projects that may resolve each (their own reference or one
    // inherited from a referenced project or package). ASP.NET Core is the HTTP front end's server; the CLI inherits it by
    // referencing that front end. Anything else, anywhere, fails: a framework is as much a dependency as a package.
    private static readonly Dictionary<string, HashSet<string>> FrameworkReferences = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Microsoft.AspNetCore.App"] = new(StringComparer.Ordinal) { "Corvid.Http", "Corvid.Cli" },
    };

    // The terminal UI, in the CLI only; the host stays free of it so the gateway (C-020) can share the command registry.
    private static readonly HashSet<string> CliPackages = new(StringComparer.OrdinalIgnoreCase)
    {
        "Spectre.Console",
        "Spectre.Console.Ansi"
    };

    // Surface projects (plan §3.1) speak a protocol through its SDK. Each may reference core projects and its own reviewed
    // packages (with what they resolve), never adapters, the host or another surface; only the composition root may
    // reference one, and it may then resolve what the surface brings, as it does for adapters. The core rule is unchanged:
    // a core project that reached a surface or its SDK fails.
    private static readonly Dictionary<string, HashSet<string>> SurfacePackages = new(StringComparer.Ordinal)
    {
        // C-018: the official MCP C# SDK. The tasks extension pins the hosting package, which brings the DI, hosting,
        // caching and options abstractions; nothing in Corvid.Mcp uses the generic host.
        ["Corvid.Mcp"] = new(StringComparer.OrdinalIgnoreCase)
        {
            "ModelContextProtocol",
            "ModelContextProtocol.Core",
            "ModelContextProtocol.Extensions.Tasks",
            "Microsoft.Extensions.Caching.Abstractions",
            "Microsoft.Extensions.Configuration.Abstractions",
            "Microsoft.Extensions.DependencyInjection.Abstractions",
            "Microsoft.Extensions.Diagnostics.Abstractions",
            "Microsoft.Extensions.FileProviders.Abstractions",
            "Microsoft.Extensions.Hosting.Abstractions",
            "Microsoft.Extensions.Logging.Abstractions",
            "Microsoft.Extensions.Options",
            "Microsoft.Extensions.Primitives"
        }
    };

    // The windowed app (docs/plan/ui.md §8). Corvid.App is the Uno head: the only project that resolves Uno's packages,
    // and, in its desktop target alone, a way to the composition root (through Corvid.App.Engine); its WebAssembly target
    // runs attached to an engine elsewhere and never reaches the host. Corvid.App.Core holds the view models and takes no
    // package but Markdig and no project but the contracts, so it builds and tests without a UI framework. Corvid.App.Engine
    // adapts the composition root to the app's services, as the ACP front end adapts it to ACP; only the head references it.
    private const string AppHead = "Corvid.App";
    private const string AppCore = "Corvid.App.Core";
    private const string AppEngine = "Corvid.App.Engine";

    private static readonly HashSet<string> AppCorePackages = new(StringComparer.OrdinalIgnoreCase) { "Markdig" };

    private static readonly HashSet<string> AppCoreProjects = new(StringComparer.Ordinal) { "Corvid.Abstractions" };

    // What the head resolves in every target with Uno.Sdk 6.7.30 (UnoFeatures SkiaRenderer; Hot Design, the in-app MCP
    // endpoint and the Open Sans package turned off): Uno.Sdk's implicit packages and what they bring. Explicit like the
    // other lists, so an Uno upgrade that brings a new package gets reviewed. CommunityToolkit, Uno.Extensions (MVUX,
    // navigation, hosting), Uno.Toolkit and Uno.Themes are deliberately absent: the app's MVVM is hand-written.
    private static readonly HashSet<string> AppHeadPackages = new(StringComparer.OrdinalIgnoreCase)
    {
        // Uno Platform, its Skia runtime for each desktop OS, the dev server's client (inert in Release) and the build helpers.
        "Uno.Core.Extensions.Logging", "Uno.Core.Extensions.Logging.Singleton", "Uno.Diagnostics.Eventing",
        "Uno.Fonts.Fluent", "Uno.Foundation", "Uno.Foundation.Logging", "Uno.icu-macos", "Uno.icu-win",
        "Uno.Resizetizer", "Uno.Sdk.Extras", "Uno.Settings.DevServer", "Uno.UI.Adapter.Microsoft.Extensions.Logging",
        "Uno.Wasm.WebSockets", "Uno.WinRT", "Uno.WinUI", "Uno.WinUI.DevServer", "Uno.WinUI.DevServer.Messaging",
        "Uno.WinUI.Graphics2DSK", "Uno.WinUI.Lottie", "Uno.WinUI.Runtime.Skia",
        "Uno.WinUI.Runtime.Skia.Linux.FrameBuffer", "Uno.WinUI.Runtime.Skia.MacOS", "Uno.WinUI.Runtime.Skia.Win32",
        "Uno.WinUI.Runtime.Skia.X11",
        // The renderer: SkiaSharp and HarfBuzz with their native libraries.
        "SkiaSharp", "SkiaSharp.NativeAssets.Linux", "SkiaSharp.NativeAssets.macOS",
        "SkiaSharp.NativeAssets.WebAssembly", "SkiaSharp.NativeAssets.Win32", "SkiaSharp.Resources",
        "SkiaSharp.SceneGraph", "SkiaSharp.Skottie", "SkiaSharp.Views.Uno.WinUI", "HarfBuzzSharp",
        "HarfBuzzSharp.NativeAssets.Linux", "HarfBuzzSharp.NativeAssets.macOS", "HarfBuzzSharp.NativeAssets.Win32",
        // Logging, which Uno's adapter routes through Microsoft.Extensions.Logging.
        "Microsoft.Extensions.Configuration", "Microsoft.Extensions.Configuration.Abstractions",
        "Microsoft.Extensions.Configuration.Binder", "Microsoft.Extensions.DependencyInjection",
        "Microsoft.Extensions.DependencyInjection.Abstractions", "Microsoft.Extensions.Logging",
        "Microsoft.Extensions.Logging.Abstractions", "Microsoft.Extensions.Logging.Configuration",
        "Microsoft.Extensions.Logging.Console", "Microsoft.Extensions.Options",
        "Microsoft.Extensions.Options.ConfigurationExtensions", "Microsoft.Extensions.Primitives",
        // Win32 interop the Windows runtime generates, D-Bus for the Linux runtime, and what the rest bring.
        "Microsoft.Windows.CsWin32", "Microsoft.Windows.SDK.Win32Docs", "Microsoft.Windows.SDK.Win32Metadata",
        "Microsoft.Windows.WDK.Win32Metadata", "Tmds.DBus.Generator", "Tmds.DBus.Protocol", "CommonServiceLocator",
        "DirectNAot", "DirectNAot.Extensions", "LibVLCSharp", "System.Json", "WebView2Aot",
    };

    // What the WebAssembly target adds, with -p:CorvidAppWasm=true only.
    private static readonly HashSet<string> AppHeadWasmPackages = new(StringComparer.OrdinalIgnoreCase)
    {
        // The browser runtime, the bootstrapper and its dev server, the console logger.
        "Uno.Extensions.Logging.WebAssembly.Console", "Uno.Foundation.Runtime.WebAssembly", "Uno.icu-wasm",
        "Uno.Wasm.Bootstrap", "Uno.Wasm.Bootstrap.DevServer", "Uno.WinUI.Runtime.Skia.WebAssembly.Browser",
        "Uno.WinUI.Runtime.WebAssembly", "HarfBuzzSharp.NativeAssets.WebAssembly",
        // Microsoft.Windows.Compatibility (an implicit package of a WebAssembly head) and what it resolves, with the
        // System.Security.Cryptography pins from Directory.Packages.props over it; the WebAssembly SDK's own pack.
        "Microsoft.Extensions.ObjectPool", "Microsoft.NET.Sdk.WebAssembly.Pack",
        "Microsoft.Win32.Registry.AccessControl", "Microsoft.Win32.SystemEvents", "Microsoft.Windows.Compatibility",
        "System.CodeDom", "System.ComponentModel.Composition", "System.ComponentModel.Composition.Registration",
        "System.Configuration.ConfigurationManager", "System.Data.Odbc", "System.Data.OleDb",
        "System.Data.SqlClient", "System.Diagnostics.EventLog", "System.Diagnostics.PerformanceCounter",
        "System.DirectoryServices", "System.DirectoryServices.AccountManagement",
        "System.DirectoryServices.Protocols", "System.Drawing.Common", "System.IO.Packaging", "System.IO.Ports",
        "System.Management", "System.Reflection.Context", "System.Runtime.Caching",
        "System.Security.Cryptography.Pkcs", "System.Security.Cryptography.ProtectedData",
        "System.Security.Cryptography.Xml", "System.Security.Permissions", "System.ServiceModel.Http",
        "System.ServiceModel.NetFramingBase", "System.ServiceModel.NetTcp", "System.ServiceModel.Primitives",
        "System.ServiceModel.Syndication", "System.ServiceProcess.ServiceController", "System.Speech",
        "System.Web.Services.Description", "System.Windows.Extensions",
        "runtime.android-arm.runtime.native.System.IO.Ports", "runtime.android-arm64.runtime.native.System.IO.Ports",
        "runtime.android-x64.runtime.native.System.IO.Ports", "runtime.android-x86.runtime.native.System.IO.Ports",
        "runtime.linux-arm.runtime.native.System.IO.Ports", "runtime.linux-arm64.runtime.native.System.IO.Ports",
        "runtime.linux-bionic-arm64.runtime.native.System.IO.Ports",
        "runtime.linux-bionic-x64.runtime.native.System.IO.Ports",
        "runtime.linux-musl-arm.runtime.native.System.IO.Ports",
        "runtime.linux-musl-arm64.runtime.native.System.IO.Ports",
        "runtime.linux-musl-x64.runtime.native.System.IO.Ports", "runtime.linux-x64.runtime.native.System.IO.Ports",
        "runtime.maccatalyst-arm64.runtime.native.System.IO.Ports",
        "runtime.maccatalyst-x64.runtime.native.System.IO.Ports", "runtime.native.System.Data.SqlClient.sni",
        "runtime.native.System.IO.Ports", "runtime.osx-arm64.runtime.native.System.IO.Ports",
        "runtime.osx-x64.runtime.native.System.IO.Ports",
        "runtime.win-arm64.runtime.native.System.Data.SqlClient.sni",
        "runtime.win-x64.runtime.native.System.Data.SqlClient.sni",
        "runtime.win-x86.runtime.native.System.Data.SqlClient.sni",
    };

    public static IReadOnlyList<string> CheckRepository(string root)
    {
        var errors = new List<string>();
        var projects = new[] { "src", "vault", "sandbox" }
            .Select(folder => Path.Combine(root, folder))
            .Where(Directory.Exists)
            .SelectMany(folder => Directory.GetFiles(folder, "*.csproj", SearchOption.AllDirectories))
            .Order(StringComparer.Ordinal);
        var count = 0;
        foreach (var project in projects)
        {
            count++;
            var assetsPath = Path.Combine(Path.GetDirectoryName(project)!, "obj", "project.assets.json");
            if (!File.Exists(assetsPath))
            {
                errors.Add($"{Path.GetFileNameWithoutExtension(project)}: missing {assetsPath}; restore the solution first (include every production project).");
                continue;
            }

            using var assets = JsonDocument.Parse(File.ReadAllText(assetsPath));
            var restoredProject = assets.RootElement.GetProperty("project").GetProperty("restore").GetProperty("projectPath").GetString();
            if (!string.Equals(Path.GetFullPath(restoredProject!), Path.GetFullPath(project), StringComparison.Ordinal))
            {
                errors.Add($"{project}: assets belong to a different project; restore the solution first.");
                continue;
            }

            errors.AddRange(CheckAssets(Path.GetFileNameWithoutExtension(project), assets.RootElement));
            errors.AddRange(CheckAnalyzerReferences(project));
        }

        if (count == 0)
            errors.Add("No production projects found.");
        return errors;
    }

    public static IReadOnlyList<string> CheckAssets(string project, JsonElement assets)
    {
        var errors = new SortedSet<string>(StringComparer.Ordinal);
        var launcher = LauncherProjects.Contains(project);
        var independent = launcher || project is "Corvid.Abstractions" or "Corvid.Vault.Contracts";
        var vault = project == "Corvid.Vault";
        var adapter = project.StartsWith("Corvid.Models.", StringComparison.Ordinal);
        var buildTool = BuildToolProjects.Contains(project);
        var surface = SurfacePackages.TryGetValue(project, out var surfacePackages);
        var frontEnd = FrontEndProjects.Contains(project);
        var appHead = project == AppHead;
        var appCore = project == AppCore;
        var appEngine = project == AppEngine;
        // A front end's graph is the host's, so its packages are judged as the composition root's are; so is the app engine's.
        var composition = CompositionProjects.Contains(project) || frontEnd || appEngine;

        errors.UnionWith(CheckFrameworkReferences(project, assets));

        foreach (var target in assets.GetProperty("targets").EnumerateObject())
        {
            var natives = target.Value.EnumerateObject()
                .Where(d => d.Value.GetProperty("type").GetString() == "package")
                .Select(d => (Library: d.Name, Found: NativeRuntimePackages.TryGetValue(d.Name[..d.Name.LastIndexOf('/')], out var native), native))
                .Where(n => n.Found)
                .GroupBy(n => n.native.Runtime);
            foreach (var runtime in natives.Where(g => g.Select(n => n.native.Flavour).Distinct().Count() > 1))
                errors.Add($"{project}: {runtime.Key} natives of more than one flavour in {target.Name}: " +
                    $"{string.Join(", ", runtime.Select(n => n.Library).Order(StringComparer.Ordinal))}. Reference exactly one native package per runtime.");
        }

        foreach (var target in assets.GetProperty("targets").EnumerateObject())
        {
            // The head's desktop target may reach the composition root; any other target of it (the browser, later mobile)
            // runs attached to an engine elsewhere and may not.
            var desktopHead = appHead && IsDesktopTarget(target.Name);
            var browserHead = appHead && target.Name.Contains("browserwasm", StringComparison.OrdinalIgnoreCase);
            // Packages a composition project resolves only because an adapter or a surface brings them (Starling, ONNX Runtime,
            // the MCP SDK).
            var adapterPackages = composition || desktopHead ? AdapterPackages(assets, target.Value) : null;
            foreach (var dependency in target.Value.EnumerateObject())
            {
                var name = dependency.Name[..dependency.Name.LastIndexOf('/')];
                var type = dependency.Value.GetProperty("type").GetString();
                // AssemblyName/PackageId can differ from the project name (Vault is "corvid-vault").
                if (type == "project")
                    name = ProjectName(assets, dependency.Name);

                // The SDK injects the trim/AOT analyzer even into dependency-free contracts.
                if (type == "package" && name.Equals("Microsoft.NET.ILLink.Tasks", StringComparison.OrdinalIgnoreCase))
                    continue;

                var allowed = type switch
                {
                    // Uno's packages belong to the app's head alone, whatever else a project may take (an adapter takes any).
                    "package" when !appHead && IsUnoPackage(name) => false,
                    "package" when appHead => AppHeadPackages.Contains(name) || AppCorePackages.Contains(name)
                        || browserHead && AppHeadWasmPackages.Contains(name)
                        || desktopHead && (CorePackages.Contains(name) || CompositionPackages.Contains(name) || adapterPackages!.Contains(name)),
                    "package" when appCore => AppCorePackages.Contains(name),
                    "package" when composition => CorePackages.Contains(name) || CompositionPackages.Contains(name)
                        || project == "Corvid.Cli" && CliPackages.Contains(name) || (appEngine || project == "Corvid.Cli") && AppCorePackages.Contains(name)
                        || adapterPackages!.Contains(name),
                    "package" when surface => CorePackages.Contains(name) || surfacePackages!.Contains(name),
                    "package" => buildTool
                        ? BuildToolPackages.Contains(name)
                        : !independent && !vault && (adapter || CorePackages.Contains(name)),
                    // The app: nothing references the head; only the head's desktop target and the CLI reference the engine
                    // (A6, G-1: `corvid serve --attach` hosts the engine's session service for attached clients, so the
                    // remote seam is ISessionService itself and the approval card, queue and questions are the window's
                    // own code, never a second copy); only the head, the engine and through it the CLI reference the
                    // core, which references the contracts alone; the head's other targets reference the core and what it does.
                    "project" when name == AppHead => false,
                    "project" when name == AppEngine => desktopHead || project == "Corvid.Cli",
                    "project" when name == AppCore => appHead || appEngine || project == "Corvid.Cli",
                    "project" when appCore => AppCoreProjects.Contains(name),
                    "project" when appHead && !desktopHead => AppCoreProjects.Contains(name),
                    // Only the composition root composes a surface; a surface references core projects only.
                    "project" when SurfacePackages.ContainsKey(name) => composition || desktopHead,
                    "project" when surface => name != "Corvid.Vault" && !name.StartsWith("Corvid.Models.", StringComparison.Ordinal)
                        && !CompositionProjects.Contains(name) && !FrontEndProjects.Contains(name) && !BuildToolProjects.Contains(name) && !LauncherProjects.Contains(name),
                    // A build tool in another project's graph means its assembly would ship; reference it as an analyzer.
                    "project" when buildTool || BuildToolProjects.Contains(name) => false,
                    // Executables are started by path; a reference would load one into another process.
                    "project" when LauncherProjects.Contains(name) => false,
                    // Nothing composes the composition root but the CLI, the front ends and the app (its engine, and its
                    // desktop target through it); nothing references the CLI.
                    "project" when CompositionProjects.Contains(name) => name == "Corvid.Host" && (project == "Corvid.Cli" || frontEnd || appEngine || desktopHead),
                    // Only the CLI starts a front end.
                    "project" when FrontEndProjects.Contains(name) => project == "Corvid.Cli",
                    "project" => !independent && (vault
                        ? name == "Corvid.Vault.Contracts"
                        : name != "Corvid.Vault" && (adapter || composition || desktopHead || !name.StartsWith("Corvid.Models.", StringComparison.Ordinal))),
                    _ => false
                };

                if (!allowed)
                    errors.Add($"{project}: forbidden {type} dependency {dependency.Name} in {target.Name} (direct or transitive). " +
                        (type == "package" && !appHead && IsUnoPackage(name) ? "Uno's packages belong in Corvid.App alone; the core, the view models and the engine stay free of the UI framework."
                        : type == "project" && (name is AppHead or AppEngine || appHead) ? "Only Corvid.App's desktop target and the CLI (corvid serve --attach) reference Corvid.App.Engine and, through it, the composition root; the head's other targets run attached and reference Corvid.App.Core alone. Nothing references Corvid.App."
                        : type == "project" && name == AppCore ? "Only Corvid.App, Corvid.App.Engine and the CLI (through the engine) reference Corvid.App.Core."
                        : appCore ? "Corvid.App.Core takes no package but Markdig and no project but Corvid.Abstractions, so the view models build and test without a UI framework or the engine."
                        : appHead ? "Corvid.App takes Uno.Sdk's implicit packages (AppHeadPackages, and AppHeadWasmPackages in its WebAssembly target) and, in its desktop target, what the composition root resolves; never CommunityToolkit, Uno.Extensions, Uno.Toolkit or Uno.Themes. Add a package after review."
                        : buildTool ? "Build tooling may use only Roslyn and its netstandard2.0 dependencies, and no projects."
                        : type == "project" && BuildToolProjects.Contains(name) ? "Reference build tooling as an analyzer: OutputItemType=\"Analyzer\" ReferenceOutputAssembly=\"false\"."
                        : launcher ? "The corvid-sandbox launcher must have no dependencies; Vault and Corvid start it by path."
                        : type == "project" && LauncherProjects.Contains(name) ? "Start the corvid-sandbox launcher by path; do not reference it."
                        : type == "project" && CompositionProjects.Contains(name) ? "Only the CLI and the front ends (Corvid.Acp, Corvid.Http) may reference the composition root, and nothing may reference the CLI."
                        : type == "project" && FrontEndProjects.Contains(name) ? "Only the CLI may reference a front end (Corvid.Acp, Corvid.Http)."
                        : type == "project" && SurfacePackages.ContainsKey(name) ? "Only the composition root may reference a surface project."
                        : surface ? "A surface project may use core packages and its own reviewed SDK packages (SurfacePackages), and reference core projects only."
                        : composition ? "The composition root and the CLI may use core packages, their reviewed hosting, telemetry and UI packages, and what adapters bring; add a package to CompositionPackages after review."
                        : independent ? "This contract project must have no dependencies." : vault
                            ? "Vault may depend only on Corvid.Vault.Contracts."
                            : "Provider SDKs belong in Corvid.Models.*; core infrastructure packages require an explicit allowlist entry. The Vault executable cannot be referenced by agent projects."));
            }
        }

        return errors.ToArray();
    }

    /// <summary>
    /// Shared-framework references (ASP.NET Core and the like) are not packages, so the package rules never see them. The
    /// restored assets list a project's own (<c>project.frameworks.*.frameworkReferences</c>) and those each referenced
    /// project or package brings (<c>frameworkReferences</c> on its target entry); both are checked against
    /// <see cref="FrameworkReferences"/>.
    /// </summary>
    public static IReadOnlyList<string> CheckFrameworkReferences(string project, JsonElement assets)
    {
        var errors = new SortedSet<string>(StringComparer.Ordinal);
        void Check(string framework, string from)
        {
            if (framework.Equals("Microsoft.NETCore.App", StringComparison.OrdinalIgnoreCase))
                return;
            if (!FrameworkReferences.TryGetValue(framework, out var allowed) || !allowed.Contains(project))
                errors.Add($"{project}: forbidden framework reference {framework} ({from}). " +
                    (FrameworkReferences.TryGetValue(framework, out var owners)
                        ? $"Only {string.Join(" and ", owners.Order(StringComparer.Ordinal))} may use it."
                        : "Shared frameworks other than Microsoft.NETCore.App need an explicit, reviewed entry in FrameworkReferences."));
        }

        if (assets.TryGetProperty("project", out var own) && own.TryGetProperty("frameworks", out var frameworks))
            foreach (var target in frameworks.EnumerateObject())
                if (target.Value.TryGetProperty("frameworkReferences", out var references))
                    foreach (var reference in references.EnumerateObject())
                        Check(reference.Name, "its own");
        if (assets.TryGetProperty("targets", out var targets))
            foreach (var target in targets.EnumerateObject())
                foreach (var dependency in target.Value.EnumerateObject())
                    if (dependency.Value.TryGetProperty("frameworkReferences", out var inherited) && inherited.ValueKind == JsonValueKind.Array)
                        foreach (var reference in inherited.EnumerateArray())
                            Check(reference.GetString() ?? "", $"through {dependency.Name} in {target.Name}");
        return errors.ToArray();
    }

    /// <summary>Uno Platform's own packages, which only the app's head may resolve.</summary>
    private static bool IsUnoPackage(string name) => name.StartsWith("Uno.", StringComparison.OrdinalIgnoreCase);

    /// <summary>The head's desktop target (<c>net10.0-desktop</c>, and its runtime-specific variants).</summary>
    private static bool IsDesktopTarget(string target) => target.Contains("-desktop", StringComparison.OrdinalIgnoreCase);

    private static string ProjectName(JsonElement assets, string library) =>
        Path.GetFileNameWithoutExtension(assets.GetProperty("libraries").GetProperty(library).GetProperty("msbuildProject").GetString())
        ?? throw new InvalidOperationException($"Missing project identity for {library}; restore the solution first.");

    /// <summary>
    /// The packages reachable from an adapter (<c>Corvid.Models.*</c>) or a surface project in one restored target, following each library's
    /// <c>dependencies</c>. A package that is also reachable another way is still allowed only if that way allows it.
    /// </summary>
    private static HashSet<string> AdapterPackages(JsonElement assets, JsonElement target)
    {
        var entries = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in target.EnumerateObject())
            entries[entry.Name[..entry.Name.LastIndexOf('/')]] = entry.Value;

        var pending = new Stack<string>(target.EnumerateObject()
            .Where(entry => entry.Value.GetProperty("type").GetString() == "project"
                && ProjectName(assets, entry.Name) is var name
                && (name.StartsWith("Corvid.Models.", StringComparison.Ordinal) || SurfacePackages.ContainsKey(name)))
            .Select(entry => entry.Name[..entry.Name.LastIndexOf('/')]));
        var seen = new HashSet<string>(pending, StringComparer.OrdinalIgnoreCase);
        var packages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (pending.TryPop(out var library))
        {
            if (!entries.TryGetValue(library, out var entry) || !entry.TryGetProperty("dependencies", out var dependencies))
                continue;
            foreach (var dependency in dependencies.EnumerateObject())
            {
                if (!seen.Add(dependency.Name))
                    continue;
                pending.Push(dependency.Name);
                if (entries.TryGetValue(dependency.Name, out var resolved) && resolved.GetProperty("type").GetString() == "package")
                    packages.Add(dependency.Name);
            }
        }

        return packages;
    }

    /// <summary>
    /// Analyzer project references with ReferenceOutputAssembly="false" are not in NuGet's restored graph, so they are
    /// read from the project file: each must name a build-tool project.
    /// </summary>
    public static IReadOnlyList<string> CheckAnalyzerReferences(string projectPath)
    {
        var errors = new List<string>();
        var project = Path.GetFileNameWithoutExtension(projectPath);
        foreach (var reference in XDocument.Load(projectPath).Descendants().Where(e => e.Name.LocalName == "ProjectReference"))
        {
            var outputItemType = (string?)reference.Attribute("OutputItemType")
                ?? reference.Elements().FirstOrDefault(e => e.Name.LocalName == "OutputItemType")?.Value;
            var include = (string?)reference.Attribute("Include") ?? "";
            var target = Path.GetFileNameWithoutExtension(include.Replace('\\', '/'));
            if (string.Equals(outputItemType?.Trim(), "Analyzer", StringComparison.OrdinalIgnoreCase) && !BuildToolProjects.Contains(target))
                errors.Add($"{project}: analyzer reference to {include}, which is not a reviewed build-tool project. Add it to BuildToolProjects in the checker after review.");
        }

        return errors;
    }
}
