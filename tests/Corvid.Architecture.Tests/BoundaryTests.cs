using System.Text.Json;
using Corvid.Architecture;

namespace Corvid.Architecture.Tests;

public sealed class BoundaryTests
{
    [Theory]
    [InlineData("Corvid.Kernel", "OpenAI", "package", false)]
    [InlineData("Corvid.Models", "Anthropic", "package", false)]
    [InlineData("Corvid.Kernel", "Starlings.Models.Kev", "package", false)]
    [InlineData("Corvid.Kernel", "PreviouslyUnknown.Provider", "package", false)]
    [InlineData("Corvid.Models.OpenAICompat", "OpenAI", "package", true)]
    [InlineData("Corvid.Models.Starling", "Starlings.Models.Kev", "package", true)]
    [InlineData("Corvid.Models", "Microsoft.Extensions.AI.Abstractions", "package", true)]
    [InlineData("Corvid.Abstractions", "Microsoft.Extensions.AI.Abstractions", "package", false)]
    [InlineData("Corvid.Abstractions", "Corvid.Kernel", "project", false)]
    [InlineData("Corvid.Kernel", "Corvid.Abstractions", "project", true)]
    [InlineData("Corvid.Kernel", "Corvid.Models", "project", true)]
    [InlineData("Corvid.Kernel", "Corvid.Models.OpenAICompat", "project", false)]
    [InlineData("Corvid.Models.OpenAICompat", "Corvid.Models", "project", true)]
    [InlineData("Corvid.Vault", "Corvid.Vault.Contracts", "project", true)]
    [InlineData("Corvid.Vault", "Corvid.Vault.Client", "project", false)]
    [InlineData("Corvid.Vault", "Corvid.Abstractions", "project", false)]
    [InlineData("Corvid.Vault", "OpenAI", "package", false)]
    [InlineData("Corvid.Vault.Contracts", "Corvid.Kernel", "project", false)]
    [InlineData("Corvid.Vault.Contracts", "OpenAI", "package", false)]
    [InlineData("Corvid.Kernel", "Corvid.Vault", "project", false)]
    [InlineData("Corvid.Models.Starling", "Corvid.Vault", "project", false)]
    [InlineData("Corvid.Vault.Contracts", "Microsoft.NET.ILLink.Tasks", "package", true)]
    [InlineData("Corvid.Vault", "Microsoft.NET.ILLink.Tasks", "package", true)]
    public void EnforcesLayerRules(string project, string dependency, string type, bool allowed)
    {
        using var assets = Assets(("net10.0", dependency, type));
        var errors = BoundaryCheck.CheckAssets(project, assets.RootElement);
        Assert.Equal(allowed, errors.Count == 0);
        if (!allowed)
            Assert.Contains(dependency, Assert.Single(errors));
    }

    [Fact]
    public void ChecksTransitivePackagesEvenWhenTheDirectDependencyIsAllowed()
    {
        // Both direct and transitive libraries are in NuGet's resolved target, regardless of depth.
        using var assets = Assets(
            ("net10.0", "Microsoft.Extensions.AI.Abstractions", "package"),
            ("net10.0", "OpenAI", "package"));
        Assert.Contains("OpenAI", Assert.Single(BoundaryCheck.CheckAssets("Corvid.Models", assets.RootElement)));
    }

    [Fact]
    public void ChecksRuntimeSpecificTargets()
    {
        using var assets = Assets(
            ("net10.0", "Corvid.Abstractions", "project"),
            ("net10.0/linux-x64", "OpenAI", "package"));
        Assert.Contains("linux-x64", Assert.Single(BoundaryCheck.CheckAssets("Corvid.Kernel", assets.RootElement)));
    }

    [Fact]
    public void UsesProjectIdentityRatherThanAssemblyName()
    {
        using var assets = JsonDocument.Parse("""
            {
              "targets": { "net10.0": { "corvid-vault/1.0.0": { "type": "project" } } },
              "libraries": { "corvid-vault/1.0.0": { "msbuildProject": "../../vault/Corvid.Vault/Corvid.Vault.csproj" } }
            }
            """);
        Assert.Contains("corvid-vault", Assert.Single(BoundaryCheck.CheckAssets("Corvid.Kernel", assets.RootElement)));
    }

    [Fact]
    public void MissingRestoreFailsClosed()
    {
        var root = Path.Combine(Path.GetTempPath(), "corvid-boundaries-" + Guid.NewGuid());
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "src", "Corvid.Kernel"));
            Directory.CreateDirectory(Path.Combine(root, "vault"));
            File.WriteAllText(Path.Combine(root, "src", "Corvid.Kernel", "Corvid.Kernel.csproj"), "<Project />");
            Assert.Contains("restore", Assert.Single(BoundaryCheck.CheckRepository(root)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static JsonDocument Assets(params (string Target, string Name, string Type)[] dependencies) =>
        JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            targets = dependencies.GroupBy(d => d.Target).ToDictionary(
                group => group.Key,
                group => group.ToDictionary(d => d.Name + "/1.0.0", d => new { type = d.Type })),
            libraries = dependencies.Where(d => d.Type == "project").DistinctBy(d => d.Name)
                .ToDictionary(d => d.Name + "/1.0.0", d => new { msbuildProject = $"../{d.Name}/{d.Name}.csproj" })
        }));
}
