using System.Collections.Immutable;
using Corvid.Abstractions.Extraction;
using Corvid.Host.Audit;
using Corvid.Host.Tests;
using Corvid.Models;
using Corvid.Models.Privacy;
using Microsoft.Extensions.AI;

namespace Corvid.App.Engine.Tests;

/// <summary>
/// A cloud request through the real privacy egress gate into an egress ledger (G-9), as a gated binding sends one: the fake
/// models are local, so tests send through the gate themselves, with "Alice Smith" as the one person it finds.
/// </summary>
internal static class EgressFakes
{
    public static readonly ModelProfile Cloud = new("claude@anthropic", 200_000, 200_000, ToolCallSupport.Native,
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

    /// <summary>Sends one user message for <paramref name="session"/>; a refusal is recorded and swallowed.</summary>
    public static async Task SendAsync(EgressLedger ledger, string session, string text, EgressAction action = EgressAction.Redact)
    {
        var client = new PrivacyEgressChatClient(new FakeGenerator(Reply.Say("ok")), Cloud,
            new PrivacyEgress(new Person(), new PrivacyEgressOptions { PolicyFor = _ => EgressPolicy.Default with { Action = action } }),
            ledger.For("generator", Cloud.Placement, "anthropic.key"));
        try
        {
            await client.GetResponseAsync([new ChatMessage(ChatRole.User, text)], new ModelRequestContext(session, Tainted: false).Apply(new ChatOptions()),
                TestContext.Current.CancellationToken);
        }
        catch (PrivacyEgressRefusedException)
        {
        }
    }
}
