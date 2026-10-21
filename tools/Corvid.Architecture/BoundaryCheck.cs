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
        var composition = CompositionProjects.Contains(project);
        var surface = SurfacePackages.TryGetValue(project, out var surfacePackages);

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
            // Packages a composition project resolves only because an adapter or a surface brings them (Starling, ONNX Runtime,
            // the MCP SDK).
            var adapterPackages = composition ? AdapterPackages(assets, target.Value) : null;
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
                    "package" when composition => CorePackages.Contains(name) || CompositionPackages.Contains(name)
                        || project == "Corvid.Cli" && CliPackages.Contains(name) || adapterPackages!.Contains(name),
                    "package" when surface => CorePackages.Contains(name) || surfacePackages!.Contains(name),
                    "package" => buildTool
                        ? BuildToolPackages.Contains(name)
                        : !independent && !vault && (adapter || CorePackages.Contains(name)),
                    // Only the composition root composes a surface; a surface references core projects only.
                    "project" when SurfacePackages.ContainsKey(name) => composition,
                    "project" when surface => name != "Corvid.Vault" && !name.StartsWith("Corvid.Models.", StringComparison.Ordinal)
                        && !CompositionProjects.Contains(name) && !BuildToolProjects.Contains(name) && !LauncherProjects.Contains(name),
                    // A build tool in another project's graph means its assembly would ship; reference it as an analyzer.
                    "project" when buildTool || BuildToolProjects.Contains(name) => false,
                    // Executables are started by path; a reference would load one into another process.
                    "project" when LauncherProjects.Contains(name) => false,
                    // Nothing composes the composition root but the CLI; nothing references the CLI.
                    "project" when CompositionProjects.Contains(name) => project == "Corvid.Cli" && name == "Corvid.Host",
                    "project" => !independent && (vault
                        ? name == "Corvid.Vault.Contracts"
                        : name != "Corvid.Vault" && (adapter || composition || !name.StartsWith("Corvid.Models.", StringComparison.Ordinal))),
                    _ => false
                };

                if (!allowed)
                    errors.Add($"{project}: forbidden {type} dependency {dependency.Name} in {target.Name} (direct or transitive). " +
                        (buildTool ? "Build tooling may use only Roslyn and its netstandard2.0 dependencies, and no projects."
                        : type == "project" && BuildToolProjects.Contains(name) ? "Reference build tooling as an analyzer: OutputItemType=\"Analyzer\" ReferenceOutputAssembly=\"false\"."
                        : launcher ? "The corvid-sandbox launcher must have no dependencies; Vault and Corvid start it by path."
                        : type == "project" && LauncherProjects.Contains(name) ? "Start the corvid-sandbox launcher by path; do not reference it."
                        : type == "project" && CompositionProjects.Contains(name) ? "Only the CLI may reference the composition root, and nothing may reference the CLI."
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
