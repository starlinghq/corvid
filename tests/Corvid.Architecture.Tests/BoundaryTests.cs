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
    // C-015: provider SDKs stay in their adapters; core reaches Vault only through its client.
    [InlineData("Corvid.Models.Anthropic", "Anthropic", "package", true)]
    [InlineData("Corvid.Models.Anthropic", "System.Net.ServerSentEvents", "package", true)]
    [InlineData("Corvid.Models.OpenAICompat", "Microsoft.Extensions.AI.OpenAI", "package", true)]
    [InlineData("Corvid.Models.OpenAICompat", "System.ClientModel", "package", true)]
    [InlineData("Corvid.Models", "Microsoft.Extensions.AI.OpenAI", "package", false)]
    [InlineData("Corvid.Models", "System.ClientModel", "package", false)]
    [InlineData("Corvid.Kernel", "Anthropic", "package", false)]
    [InlineData("Corvid.Models", "Corvid.Vault.Client", "project", true)]
    [InlineData("Corvid.Models", "Corvid.Models.Anthropic", "project", false)]
    [InlineData("Corvid.Kernel", "Corvid.Models.Anthropic", "project", false)]
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
    [InlineData("Corvid.Fabric", "Microsoft.Extensions.AI.Abstractions", "package", true)]
    [InlineData("Corvid.Fabric", "Corvid.Abstractions", "project", true)]
    [InlineData("Corvid.Fabric", "Starlings.Models.Kev", "package", false)]
    [InlineData("Corvid.Fabric", "Corvid.Models.Starling", "project", false)]
    [InlineData("Corvid.Fabric", "System.Numerics.Tensors", "package", false)]
    // C-007a: the sandbox launcher has no dependencies and nothing references it.
    [InlineData("Corvid.Sandbox.Launcher", "Corvid.Abstractions", "project", false)]
    [InlineData("Corvid.Sandbox.Launcher", "Corvid.Vault.Contracts", "project", false)]
    [InlineData("Corvid.Sandbox.Launcher", "Microsoft.Extensions.AI.Abstractions", "package", false)]
    [InlineData("Corvid.Sandbox.Launcher", "Microsoft.NET.ILLink.Tasks", "package", true)]
    [InlineData("Corvid.Sandbox", "Corvid.Abstractions", "project", true)]
    [InlineData("Corvid.Sandbox", "Corvid.Sandbox.Launcher", "project", false)]
    [InlineData("Corvid.Sandbox", "Starlings.Models.Kev", "package", false)]
    [InlineData("Corvid.Kernel", "Corvid.Sandbox.Launcher", "project", false)]
    [InlineData("Corvid.Vault", "Corvid.Sandbox.Launcher", "project", false)]
    [InlineData("Corvid.Models.Starling", "Corvid.Sandbox.Launcher", "project", false)]
    // C-008: the memory store is core. It uses the allowlisted SQLite bundle (FTS5 included) and a managed vector scan;
    // sqlite-vec and tensor packages are not allowed, and memory depends on Fabric, not the other way round.
    [InlineData("Corvid.Memory", "Microsoft.Data.Sqlite", "package", true)]
    [InlineData("Corvid.Memory", "SQLitePCLRaw.bundle_e_sqlite3", "package", true)]
    [InlineData("Corvid.Memory", "Microsoft.Extensions.AI.Abstractions", "package", true)]
    [InlineData("Corvid.Memory", "Corvid.Fabric", "project", true)]
    [InlineData("Corvid.Memory", "Corvid.Abstractions", "project", true)]
    [InlineData("Corvid.Memory", "sqlite-vec", "package", false)]
    [InlineData("Corvid.Memory", "System.Numerics.Tensors", "package", false)]
    [InlineData("Corvid.Memory", "Starlings.Models.EmbeddingGemma2.Extensions.AI", "package", false)]
    [InlineData("Corvid.Memory", "Corvid.Models.Starling", "project", false)]
    // C-007b: approvals are core. The bash parser is hand-written (D-5), so no tree-sitter binding or native grammar.
    [InlineData("Corvid.Approvals", "Corvid.Fabric", "project", true)]
    [InlineData("Corvid.Approvals", "Corvid.Tools", "project", true)]
    [InlineData("Corvid.Approvals", "Corvid.Vault.Client", "project", true)]
    [InlineData("Corvid.Approvals", "Corvid.Sandbox", "project", true)]
    [InlineData("Corvid.Approvals", "Corvid.Sandbox.Launcher", "project", false)]
    [InlineData("Corvid.Approvals", "Microsoft.Extensions.AI.Abstractions", "package", true)]
    [InlineData("Corvid.Approvals", "TreeSitter.DotNet", "package", false)]
    [InlineData("Corvid.Approvals", "Corvid.Models.Starling", "project", false)]
    [InlineData("Corvid.Approvals", "Corvid.Vault", "project", false)]
    // C-013: the composition root may reference adapters and its reviewed hosting and telemetry packages; the CLI adds the
    // terminal UI. Nothing references either, except the CLI the host. The core rule is unchanged.
    [InlineData("Corvid.Host", "Corvid.Models.Starling", "project", true)]
    [InlineData("Corvid.Host", "Corvid.Kernel", "project", true)]
    [InlineData("Corvid.Host", "Corvid.Vault.Client", "project", true)]
    [InlineData("Corvid.Host", "Microsoft.Extensions.Hosting", "package", true)]
    [InlineData("Corvid.Host", "Microsoft.Extensions.Configuration.Json", "package", true)]
    [InlineData("Corvid.Host", "OpenTelemetry.Exporter.OpenTelemetryProtocol", "package", true)]
    [InlineData("Corvid.Host", "Microsoft.Data.Sqlite", "package", true)]
    [InlineData("Corvid.Host", "Microsoft.Extensions.AI.Abstractions", "package", true)]
    [InlineData("Corvid.Host", "Spectre.Console", "package", false)]
    [InlineData("Corvid.Host", "OpenAI", "package", false)]
    [InlineData("Corvid.Host", "Starlings.Models.Kev", "package", false)]
    [InlineData("Corvid.Host", "PreviouslyUnknown.Package", "package", false)]
    [InlineData("Corvid.Host", "Corvid.Vault", "project", false)]
    [InlineData("Corvid.Host", "Corvid.Sandbox.Launcher", "project", false)]
    [InlineData("Corvid.Host", "Corvid.Tools.Generators", "project", false)]
    [InlineData("Corvid.Host", "Corvid.Cli", "project", false)]
    [InlineData("Corvid.Cli", "Corvid.Host", "project", true)]
    [InlineData("Corvid.Cli", "Corvid.Models.Starling", "project", true)]
    [InlineData("Corvid.Cli", "Spectre.Console", "package", true)]
    [InlineData("Corvid.Cli", "Microsoft.Extensions.Hosting", "package", true)]
    [InlineData("Corvid.Cli", "Spectre.Console.Cli", "package", false)]
    [InlineData("Corvid.Cli", "Corvid.Vault", "project", false)]
    [InlineData("Corvid.Kernel", "Corvid.Host", "project", false)]
    [InlineData("Corvid.Kernel", "Corvid.Cli", "project", false)]
    [InlineData("Corvid.Models.Starling", "Corvid.Host", "project", false)]
    [InlineData("Corvid.Vault", "Corvid.Host", "project", false)]
    [InlineData("Corvid.Sandbox.Launcher", "Corvid.Host", "project", false)]
    [InlineData("Corvid.Kernel", "Microsoft.Extensions.Hosting", "package", false)]
    [InlineData("Corvid.Kernel", "OpenTelemetry", "package", false)]
    [InlineData("Corvid.Fabric", "OpenTelemetry.Api", "package", false)]
    [InlineData("Corvid.Kernel", "Spectre.Console", "package", false)]
    [InlineData("Corvid.Models.Starling", "Spectre.Console", "package", true)]
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
    public void TheCompositionRootResolvesWhatAdaptersBringButNothingElse()
    {
        // Starling and ONNX Runtime reach the host only through the adapter; a provider SDK the host took on directly does not.
        using var assets = JsonDocument.Parse("""
            {
              "targets": { "net10.0": {
                "Corvid.Models.Starling/1.0.0": { "type": "project", "dependencies": { "Starlings.Models.Qwen35": "0.2.0", "Corvid.Models": "1.0.0" } },
                "Corvid.Models/1.0.0": { "type": "project", "dependencies": { "Microsoft.Extensions.AI.Abstractions": "10.10.1" } },
                "Starlings.Models.Qwen35/0.2.0": { "type": "package", "dependencies": { "Microsoft.ML.OnnxRuntimeGenAI": "0.17.1" } },
                "Microsoft.ML.OnnxRuntimeGenAI/0.17.1": { "type": "package" },
                "Microsoft.Extensions.AI.Abstractions/10.10.1": { "type": "package" },
                "OpenAI/2.0.0": { "type": "package" }
              } },
              "libraries": {
                "Corvid.Models.Starling/1.0.0": { "msbuildProject": "../Corvid.Models.Starling/Corvid.Models.Starling.csproj" },
                "Corvid.Models/1.0.0": { "msbuildProject": "../Corvid.Models/Corvid.Models.csproj" }
              }
            }
            """);
        Assert.Contains("OpenAI", Assert.Single(BoundaryCheck.CheckAssets("Corvid.Host", assets.RootElement)));
        Assert.Contains("OpenAI", Assert.Single(BoundaryCheck.CheckAssets("Corvid.Cli", assets.RootElement)));
        // The same graph in a core project fails on the adapter and on everything it brings.
        var core = BoundaryCheck.CheckAssets("Corvid.Kernel", assets.RootElement);
        Assert.Contains(core, error => error.Contains("Corvid.Models.Starling/1.0.0", StringComparison.Ordinal));
        Assert.Contains(core, error => error.Contains("Starlings.Models.Qwen35", StringComparison.Ordinal));
        Assert.Contains(core, error => error.Contains("Microsoft.ML.OnnxRuntimeGenAI", StringComparison.Ordinal));
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
