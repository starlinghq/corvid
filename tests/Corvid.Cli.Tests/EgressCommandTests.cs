using System.Collections.Immutable;
using Corvid.Abstractions.Extraction;
using Corvid.Host.Audit;
using Corvid.Models;
using Corvid.Models.Privacy;
using Microsoft.Extensions.AI;

namespace Corvid.Cli.Tests;

/// <summary><c>corvid egress</c> (G-9): the egress ledger listed, and a request shown as it was sent.</summary>
public sealed class EgressCommandTests
{
    private static readonly ModelProfile Cloud = new("claude@anthropic", 200_000, 200_000, ToolCallSupport.Native,
        StructuredOutputSupport.None, PromptCachingStyle.Breakpoints, ModelCost.Free, PrivacyClass.Cloud, "api.anthropic.com");

    private sealed class Person : IExtractor
    {
        public Task<ExtractionResult> ExtractAsync(ExtractionRequest request, CancellationToken cancellationToken = default)
        {
            var spans = ImmutableArray.CreateBuilder<ExtractedSpan>();
            const string value = "Alice Smith";
            for (var at = request.Text.IndexOf(value, StringComparison.Ordinal); at >= 0; at = request.Text.IndexOf(value, at + 1, StringComparison.Ordinal))
                spans.Add(new ExtractedSpan("person", value, at, at + value.Length, 0.9f));
            return Task.FromResult(new ExtractionResult(spans.ToImmutable()));
        }
    }

    private sealed class Echo : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private static async Task SendAsync(CliRun run, string session, string text, EgressAction action = EgressAction.Redact)
    {
        var ledger = new EgressLedger(Path.Combine(run.Root, "data", "egress.db"));
        var client = new PrivacyEgressChatClient(new Echo(), Cloud,
            new PrivacyEgress(new Person(), new PrivacyEgressOptions { PolicyFor = _ => EgressPolicy.Default with { Action = action } }),
            ledger.For("generator", Cloud.Placement, "anthropic.key"));
        try
        {
            await client.GetResponseAsync([new ChatMessage(ChatRole.User, text)], new ModelRequestContext(session, false).Apply(new ChatOptions()),
                TestContext.Current.CancellationToken);
        }
        catch (PrivacyEgressRefusedException)
        {
        }
    }

    [Fact]
    public async Task An_empty_ledger_says_so_and_is_not_created()
    {
        using var run = new CliRun();

        Assert.Equal(ExitCodes.Success, await run.RunAsync(["egress"]));

        Assert.Contains("Nothing has gone to a gated model", run.Output, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(run.Root, "data", "egress.db")));
    }

    [Fact]
    public async Task The_ledger_lists_each_request_and_shows_one_as_sent_with_hidden_characters_visible()
    {
        using var run = new CliRun();
        await SendAsync(run, "cli-1", "Write to Alice Smith‮ today.");
        await SendAsync(run, "cli-2", "Tell Alice Smith.", EgressAction.Refuse);

        async Task<string> Run(params string[] arguments)
        {
            var from = run.Output.Length;
            Assert.Equal(ExitCodes.Success, await run.RunAsync(arguments));
            return run.Output[from..];
        }

        var listed = await Run("egress");
        Assert.Contains("claude@anthropic", listed, StringComparison.Ordinal);
        Assert.Contains("redacted", listed, StringComparison.Ordinal);
        Assert.Contains("refused", listed, StringComparison.Ordinal);

        var shown = await Run("egress", "show", "1");
        Assert.Contains("request 1", shown, StringComparison.Ordinal);
        Assert.Contains("[user] Write to <PERSON_1>\\u202E today.", shown, StringComparison.Ordinal);
        Assert.DoesNotContain("Alice", shown, StringComparison.Ordinal);
        Assert.DoesNotContain('‮', shown);

        Assert.Contains("Refused before it left", await Run("egress", "show", "2"), StringComparison.Ordinal);
        Assert.DoesNotContain("cli-1", await Run("egress", "--session", "cli-2"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("egress", "show")]
    [InlineData("egress", "--limit", "0")]
    [InlineData("egress", "--bogus")]
    [InlineData("egress", "show", "1", "--session", "x")]
    public async Task Bad_usage_is_refused(params string[] arguments)
    {
        using var run = new CliRun();

        Assert.Equal(ExitCodes.Usage, await run.RunAsync(arguments));
    }

    [Fact]
    public async Task A_request_the_ledger_does_not_have_fails()
    {
        using var run = new CliRun();

        Assert.Equal(ExitCodes.Failure, await run.RunAsync(["egress", "show", "7"]));
        Assert.Contains("no request 7", run.Output, StringComparison.Ordinal);
    }
}
