using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Corvid.Tools.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Corvid.Tools.Tests;

public sealed class GeneratorTests
{
    private const string Sample = """
        using System.ComponentModel;
        using System.Threading;
        using System.Threading.Tasks;
        using Corvid.Tools;
        using Corvid.Vault.Client;

        namespace Sample;

        public enum Mode { Fast, Careful }

        public sealed partial class FileTools
        {
            [Tool("write_file", Description = "Write a \"text\" file.")]
            [SideEffect(SideEffects.Filesystem)]
            [Destructive]
            public Task<string> WriteFileAsync(
                [Description("Workspace-relative path.")] string path,
                string content,
                Mode mode = Mode.Careful,
                CancellationToken cancellationToken = default) => Task.FromResult(path);

            [Tool]
            [Description("Upload with a token.")]
            [Network]
            [SideEffect(SideEffects.Remote)]
            [SideEffect(SideEffects.Messaging)]
            private static ValueTask UploadAsync(SecretRef token, long[] ids, double? ratio = 0.5) => default;
        }
        """;

    /// <summary>Set CORVID_UPDATE_SNAPSHOTS=1 to rewrite the snapshot after an intended change, then review the diff.</summary>
    [Fact]
    public void Generated_source_matches_snapshot()
    {
        (Compilation output, ImmutableArray<Diagnostic> diagnostics, GeneratorDriverRunResult result) = Run(Sample);

        Assert.Empty(diagnostics);
        Assert.Empty(output.GetDiagnostics(TestContext.Current.CancellationToken).Where(d => d.Severity >= DiagnosticSeverity.Warning));
        GeneratedSourceResult source = Assert.Single(Assert.Single(result.Results).GeneratedSources);
        Assert.Equal("Sample.FileTools.Tools.g.cs", source.HintName);
        AssertSnapshot("FileTools.g.cs.txt", source.SourceText.ToString());
    }

    [Fact]
    public void Generated_code_uses_no_reflection()
    {
        (_, _, GeneratorDriverRunResult result) = Run(Sample);
        string code = Assert.Single(Assert.Single(result.Results).GeneratedSources).SourceText.ToString();

        Assert.DoesNotContain("System.Reflection", code, StringComparison.Ordinal);
        Assert.DoesNotContain("typeof(", code, StringComparison.Ordinal);
        Assert.DoesNotContain("JsonSerializer", code, StringComparison.Ordinal);
        Assert.DoesNotContain("Enum.Parse", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Probe_is_carried_into_the_risk_only_when_declared()
    {
        (_, ImmutableArray<Diagnostic> diagnostics, GeneratorDriverRunResult result) = Run("""
            using Corvid.Tools;
            public sealed partial class T
            {
                [Tool(Description = "d"), Probe] public string A() => "";
                [Tool(Description = "d")] public string B() => "";
            }
            """);
        string code = Assert.Single(Assert.Single(result.Results).GeneratedSources).SourceText.ToString();

        Assert.Empty(diagnostics);
        Assert.Equal(1, code.Split(", false, false, true), ").Length - 1);
        Assert.Equal(1, code.Split(", false, false), ").Length - 1);
    }

    [Fact]
    public void Static_class_gets_a_static_factory()
    {
        (Compilation output, ImmutableArray<Diagnostic> diagnostics, _) = Run("""
            using Corvid.Tools;
            public static partial class Clock
            {
                [Tool(Description = "The time.")] public static string Now() => "now";
            }
            public static class Use
            {
                public static System.Collections.Generic.IReadOnlyList<ToolFunction> All() => Clock.CreateTools();
            }
            """);

        Assert.Empty(diagnostics);
        Assert.Empty(output.GetDiagnostics(TestContext.Current.CancellationToken).Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    [Theory]
    [InlineData("CVTOOL001", """public sealed class T { [Tool(Description = "d")] public string A() => ""; }""")]
    [InlineData("CVTOOL001", """public sealed partial class G<X> { [Tool(Description = "d")] public string A() => ""; }""")]
    [InlineData("CVTOOL002", """public sealed partial class T { [Tool("bad name", Description = "d")] public string A() => ""; }""")]
    [InlineData("CVTOOL003", """public sealed partial class T { [Tool("a", Description = "d")] public string A() => ""; [Tool("a", Description = "d")] public string B() => ""; }""")]
    [InlineData("CVTOOL004", """public sealed partial class T { [Tool(Description = "d")] public string A(System.Version v) => ""; }""")]
    [InlineData("CVTOOL004", """public sealed partial class T { [Tool(Description = "d")] public string A(ref int v) => ""; }""")]
    [InlineData("CVTOOL004", """public sealed partial class T { [Tool(Description = "d")] public string A(Corvid.Vault.Client.SecretRef[] v) => ""; }""")]
    [InlineData("CVTOOL004", """public sealed partial class T { [Tool(Description = "d")] public string A(System.AttributeTargets v) => ""; }""")]
    [InlineData("CVTOOL004", """public sealed partial class T { [Tool(Description = "d")] public string A(double v = double.NaN) => ""; }""")]
    [InlineData("CVTOOL005", """public sealed partial class T { [Tool(Description = "d")] public System.Version A() => null!; }""")]
    [InlineData("CVTOOL005", """public sealed partial class T { [Tool(Description = "d")] public System.Threading.Tasks.Task<object> A() => null!; }""")]
    [InlineData("CVTOOL006", """public sealed partial class T { [Tool] public string A() => ""; }""")]
    [InlineData("CVTOOL007", """public sealed partial class T { [Tool(Description = "d"), Destructive] public string A() => ""; }""")]
    [InlineData("CVTOOL008", """public sealed partial class T { [Tool(Description = "d")] public string A<X>() => ""; }""")]
    [InlineData("CVTOOL009", """public sealed partial class T { [Tool(Description = "d"), Probe, SideEffect(SideEffects.Filesystem)] public string A() => ""; }""")]
    [InlineData("CVTOOL009", """public sealed partial class T { [Tool(Description = "d"), Probe, Network] public string A() => ""; }""")]
    public void Invalid_tools_are_reported_and_not_generated(string id, string source)
    {
        (_, ImmutableArray<Diagnostic> diagnostics, GeneratorDriverRunResult result) = Run("using Corvid.Tools;\n" + source);

        Diagnostic diagnostic = Assert.Single(diagnostics.DistinctBy(d => d.Id));
        Assert.Equal(id, diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.NotEqual(Location.None, diagnostic.Location);
        Assert.Empty(Assert.Single(result.Results).GeneratedSources);
    }

    [Fact]
    public void Enum_members_named_after_keywords_compile()
    {
        (Compilation output, ImmutableArray<Diagnostic> diagnostics, _) = Run("""
            using Corvid.Tools;
            public enum Choice { @default, @class, Other }
            public sealed partial class T
            {
                [Tool(Description = "d")] public string Pick(Choice choice = Choice.@class) => choice.ToString();
            }
            """);

        Assert.Empty(diagnostics);
        Assert.Empty(output.GetDiagnostics(TestContext.Current.CancellationToken).Where(d => d.Severity >= DiagnosticSeverity.Warning));
    }

    [Fact]
    public void Default_name_is_snake_case_without_async()
    {
        (_, _, GeneratorDriverRunResult result) = Run("""
            using Corvid.Tools;
            public sealed partial class T
            {
                [Tool(Description = "d")] public System.Threading.Tasks.Task<string> GetHTTPStatusAsync() => null!;
            }
            """);

        Assert.Contains("\"get_http_status\"", Assert.Single(Assert.Single(result.Results).GeneratedSources).SourceText.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Unrelated_edits_reuse_the_cached_model()
    {
        CSharpCompilation compilation = Compile(Sample);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new ToolGenerator().AsSourceGenerator()],
            driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));
        driver = driver.RunGenerators(compilation, TestContext.Current.CancellationToken);

        CSharpCompilation edited = compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText("class Unrelated { }", Parse, cancellationToken: TestContext.Current.CancellationToken));
        GeneratorRunResult second = Assert.Single(driver.RunGenerators(edited, TestContext.Current.CancellationToken).GetRunResult().Results);

        Assert.All(second.TrackedOutputSteps.SelectMany(s => s.Value).SelectMany(s => s.Outputs),
            output => Assert.Equal(IncrementalStepRunReason.Cached, output.Reason));
    }

    private static readonly CSharpParseOptions Parse = new(LanguageVersion.Latest);

    private static (Compilation Output, ImmutableArray<Diagnostic> Diagnostics, GeneratorDriverRunResult Result) Run(string source)
    {
        GeneratorDriver driver = CSharpGeneratorDriver.Create([new ToolGenerator().AsSourceGenerator()], parseOptions: Parse);
        driver = driver.RunGeneratorsAndUpdateCompilation(Compile(source), out Compilation output, out ImmutableArray<Diagnostic> diagnostics, TestContext.Current.CancellationToken);
        return (output, diagnostics, driver.GetRunResult());
    }

    private static CSharpCompilation Compile(string source)
    {
        // The running test host's own assemblies stand in for reference assemblies, plus the libraries tools build on.
        IEnumerable<string> platform = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        string[] libraries =
        [
            typeof(ToolFunction).Assembly.Location,
            typeof(Corvid.Vault.Client.SecretRef).Assembly.Location,
            typeof(Microsoft.Extensions.AI.AIFunction).Assembly.Location,
        ];
        return CSharpCompilation.Create(
            "Sample",
            [CSharpSyntaxTree.ParseText(source, Parse, path: "Sample.cs")],
            platform.Concat(libraries).Distinct().Select(path => MetadataReference.CreateFromFile(path)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
    }

    private static void AssertSnapshot(string name, string actual, [CallerFilePath] string testFile = "")
    {
        // Read from the build output (CI may map source paths); updates go to the source tree.
        actual = actual.ReplaceLineEndings("\n");
        if (Environment.GetEnvironmentVariable("CORVID_UPDATE_SNAPSHOTS") == "1")
        {
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(testFile)!, "Snapshots", name), actual);
            return;
        }

        string path = Path.Combine(AppContext.BaseDirectory, "Snapshots", name);
        Assert.True(File.Exists(path), $"Missing snapshot {path}; run with CORVID_UPDATE_SNAPSHOTS=1 and review it.");
        Assert.Equal(File.ReadAllText(path).ReplaceLineEndings("\n"), actual);
    }
}
