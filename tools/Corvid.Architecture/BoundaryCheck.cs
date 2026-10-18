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

    public static IReadOnlyList<string> CheckRepository(string root)
    {
        var errors = new List<string>();
        var projects = new[] { "src", "vault" }
            .SelectMany(folder => Directory.GetFiles(Path.Combine(root, folder), "*.csproj", SearchOption.AllDirectories))
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
        var independent = project is "Corvid.Abstractions" or "Corvid.Vault.Contracts";
        var vault = project == "Corvid.Vault";
        var adapter = project.StartsWith("Corvid.Models.", StringComparison.Ordinal);
        var buildTool = BuildToolProjects.Contains(project);

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
        foreach (var dependency in target.Value.EnumerateObject())
        {
            var name = dependency.Name[..dependency.Name.LastIndexOf('/')];
            var type = dependency.Value.GetProperty("type").GetString();
            // AssemblyName/PackageId can differ from the project name (Vault is "corvid-vault").
            if (type == "project")
                name = Path.GetFileNameWithoutExtension(assets.GetProperty("libraries")
                    .GetProperty(dependency.Name).GetProperty("msbuildProject").GetString())
                    ?? throw new InvalidOperationException($"Missing project identity for {dependency.Name}; restore the solution first.");

            // The SDK injects the trim/AOT analyzer even into dependency-free contracts.
            if (type == "package" && name.Equals("Microsoft.NET.ILLink.Tasks", StringComparison.OrdinalIgnoreCase))
                continue;

            var allowed = type switch
            {
                "package" => buildTool
                    ? BuildToolPackages.Contains(name)
                    : !independent && !vault && (adapter || CorePackages.Contains(name)),
                // A build tool in another project's graph means its assembly would ship; reference it as an analyzer.
                "project" when buildTool || BuildToolProjects.Contains(name) => false,
                "project" => !independent && (vault
                    ? name == "Corvid.Vault.Contracts"
                    : name != "Corvid.Vault" && (adapter || !name.StartsWith("Corvid.Models.", StringComparison.Ordinal))),
                _ => false
            };

            if (!allowed)
                errors.Add($"{project}: forbidden {type} dependency {dependency.Name} in {target.Name} (direct or transitive). " +
                    (buildTool ? "Build tooling may use only Roslyn and its netstandard2.0 dependencies, and no projects."
                    : type == "project" && BuildToolProjects.Contains(name) ? "Reference build tooling as an analyzer: OutputItemType=\"Analyzer\" ReferenceOutputAssembly=\"false\"."
                    : independent ? "This contract project must have no dependencies." : vault
                        ? "Vault may depend only on Corvid.Vault.Contracts."
                        : "Provider SDKs belong in Corvid.Models.*; core infrastructure packages require an explicit allowlist entry. The Vault executable cannot be referenced by agent projects."));
        }

        return errors.ToArray();
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
