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
    [InlineData("Corvid.Kernel", "Microsoft.Data.Sqlite", "package", true)]
    [InlineData("Corvid.Kernel", "SQLitePCLRaw.lib.e_sqlite3", "package", true)]
    [InlineData("Corvid.Abstractions", "Microsoft.Data.Sqlite", "package", false)]
    [InlineData("Corvid.Vault", "Microsoft.Data.Sqlite", "package", false)]
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
    [InlineData("Corvid.Tools", "Microsoft.Extensions.AI.Abstractions", "package", true)]
    [InlineData("Corvid.Tools", "Corvid.Vault.Client", "project", true)]
    [InlineData("Corvid.Tools.Generators", "Microsoft.CodeAnalysis.CSharp", "package", true)]
    [InlineData("Corvid.Tools.Generators", "System.Collections.Immutable", "package", true)]
    [InlineData("Corvid.Tools.Generators", "NETStandard.Library", "package", true)]
    [InlineData("Corvid.Tools.Generators", "Microsoft.Extensions.AI.Abstractions", "package", false)]
    [InlineData("Corvid.Tools.Generators", "OpenAI", "package", false)]
    [InlineData("Corvid.Tools.Generators", "Corvid.Tools", "project", false)]
    [InlineData("Corvid.Tools", "Microsoft.CodeAnalysis.CSharp", "package", false)]
    [InlineData("Corvid.Kernel", "Microsoft.CodeAnalysis.Common", "package", false)]
    [InlineData("Corvid.Tools", "Corvid.Tools.Generators", "project", false)]
    [InlineData("Corvid.Models.Starling", "Corvid.Tools.Generators", "project", false)]
    // C-010: Starling packages and the ONNX Runtime natives they need stay in the Starling adapter.
    [InlineData("Corvid.Models.Starling", "Starlings.Models.Qwen35.Extensions.AI", "package", true)]
    [InlineData("Corvid.Models.Starling", "Microsoft.ML.OnnxRuntime", "package", true)]
    [InlineData("Corvid.Models.Starling", "Microsoft.ML.OnnxRuntimeGenAI", "package", true)]
    [InlineData("Corvid.Models.Starling", "Corvid.Models", "project", true)]
    [InlineData("Corvid.Models.Starling", "Corvid.Abstractions", "project", true)]
    [InlineData("Corvid.Models", "Microsoft.ML.OnnxRuntime", "package", false)]
    [InlineData("Corvid.Models", "Microsoft.ML.OnnxRuntime.Managed", "package", false)]
    [InlineData("Corvid.Kernel", "Microsoft.ML.OnnxRuntimeGenAI", "package", false)]
    [InlineData("Corvid.Kernel", "Starlings.Runtime.Onnx", "package", false)]
    [InlineData("Corvid.Tools", "Starlings.Acquisition", "package", false)]
    [InlineData("Corvid.Abstractions", "Starlings.Tokenizers", "package", false)]
    [InlineData("Corvid.Kernel", "Corvid.Models.Starling", "project", false)]
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

    [Theory]
    [InlineData("Microsoft.ML.OnnxRuntime", "Microsoft.ML.OnnxRuntime.Gpu.Linux")]
    [InlineData("Microsoft.ML.OnnxRuntimeGenAI", "Microsoft.ML.OnnxRuntimeGenAI.Cuda")]
    public void AdaptersResolveOneNativeFlavourPerRuntime(string cpu, string gpu)
    {
        // Both ship the native library under the same file name; whichever is copied last would load.
        using var mixed = Assets(("net10.0", cpu, "package"), ("net10.0", gpu, "package"));
        Assert.Contains("more than one flavour", Assert.Single(BoundaryCheck.CheckAssets("Corvid.Models.Starling", mixed.RootElement)));

        // The CUDA meta package and its per-platform packages are one flavour.
        using var cuda = Assets(("net10.0", "Microsoft.ML.OnnxRuntime.Gpu", "package"), ("net10.0", "Microsoft.ML.OnnxRuntime.Gpu.Linux", "package"),
            ("net10.0", "Microsoft.ML.OnnxRuntime.Gpu.Windows", "package"), ("net10.0", "Microsoft.ML.OnnxRuntimeGenAI.Cuda", "package"));
        Assert.Empty(BoundaryCheck.CheckAssets("Corvid.Models.Starling", cuda.RootElement));

        // What the adapter ships: GenAI's CPU package and the CPU ONNX Runtime it brings.
        using var cpuOnly = Assets(("net10.0", "Microsoft.ML.OnnxRuntimeGenAI", "package"), ("net10.0", "Microsoft.ML.OnnxRuntime", "package"),
            ("net10.0", "Microsoft.ML.OnnxRuntime.Managed", "package"));
        Assert.Empty(BoundaryCheck.CheckAssets("Corvid.Models.Starling", cpuOnly.RootElement));
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

    [Theory]
    [InlineData("""<ProjectReference Include="../Corvid.Tools.Generators/Corvid.Tools.Generators.csproj" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />""", true)]
    [InlineData("""<ProjectReference Include="..\Corvid.Tools.Generators\Corvid.Tools.Generators.csproj"><OutputItemType>Analyzer</OutputItemType></ProjectReference>""", true)]
    [InlineData("""<ProjectReference Include="../Corvid.Kernel/Corvid.Kernel.csproj" />""", true)]
    [InlineData("""<ProjectReference Include="../Sneaky.Analyzer/Sneaky.Analyzer.csproj" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />""", false)]
    [InlineData("""<ProjectReference Include="../Corvid.Kernel/Corvid.Kernel.csproj"><OutputItemType>analyzer</OutputItemType></ProjectReference>""", false)]
    public void AnalyzerReferencesMustNameBuildTooling(string reference, bool allowed)
    {
        // These references are absent from project.assets.json, so the checker reads them from the project file.
        var directory = Path.Combine(Path.GetTempPath(), "corvid-analyzers-" + Guid.NewGuid());
        var path = Path.Combine(directory, "Corvid.Kernel.csproj");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(path, $"<Project><ItemGroup>{reference}</ItemGroup></Project>");
            Assert.Equal(allowed, BoundaryCheck.CheckAnalyzerReferences(path).Count == 0);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
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
