using System.Text.Json;

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
                "package" => !independent && !vault && (adapter || CorePackages.Contains(name)),
                "project" => !independent && (vault
                    ? name == "Corvid.Vault.Contracts"
                    : name != "Corvid.Vault" && (adapter || !name.StartsWith("Corvid.Models.", StringComparison.Ordinal))),
                _ => false
            };

            if (!allowed)
                errors.Add($"{project}: forbidden {type} dependency {dependency.Name} in {target.Name} (direct or transitive). " +
                    (independent ? "This contract project must have no dependencies." : vault
                        ? "Vault may depend only on Corvid.Vault.Contracts."
                        : "Provider SDKs belong in Corvid.Models.*; core infrastructure packages require an explicit allowlist entry. The Vault executable cannot be referenced by agent projects."));
        }

        return errors.ToArray();
    }
}
