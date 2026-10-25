using Corvid.Models.Privacy;
using Microsoft.Extensions.AI;

namespace Corvid.Models.Tests;

/// <summary>G-9's seam: the gate tells an observer each decision, with the messages exactly as sent when something leaves.</summary>
public sealed class EgressObserverTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly ModelProfile Cloud = Profiles.Cloud("claude");

    private sealed class Recorder : IEgressObserver
    {
        public List<EgressObservation> Seen { get; } = [];

        public void Observe(EgressObservation observation) => Seen.Add(observation);
    }

    private static PrivacyEgress Egress(EgressPolicy policy) =>
        new(new FakePiiExtractor(("person", "Alice Smith"), ("email", "alice@example.com")), new PrivacyEgressOptions { PolicyFor = _ => policy });

    private static ChatOptions Options(string session) => new ModelRequestContext(session, Tainted: true).Apply(new ChatOptions
    {
        Instructions = "Be brief.",
        Tools = [AIFunctionFactory.Create(() => "x", "read_file")],
    });

    [Fact]
    public async Task A_redacted_request_is_observed_as_the_provider_received_it_before_it_is_sent()
    {
        var recorder = new Recorder();
        var provider = new ScriptedChatClient(_ =>
        {
            // Observed before the provider is called.
            Assert.Single(recorder.Seen);
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"));
        });
        var client = new PrivacyEgressChatClient(provider, Cloud, Egress(EgressPolicy.Default), recorder);

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Write to Alice Smith, alice@example.com, about Alice Smith.")],
            Options("s1"), Ct);

        var seen = Assert.Single(recorder.Seen);
        Assert.Equal((EgressOutcome.Redacted, true, "s1", true), (seen.Decision.Outcome, seen.Left, seen.Context.SessionId, seen.Context.Tainted));
        Assert.Same(Cloud, seen.Profile);
        // The provider got exactly what was observed: the instructions as a leading system message, then the redacted text.
        Assert.Equal(provider.Calls[0].Select(message => message.Text), seen.Sent!.Select(message => message.Text));
        Assert.Equal(["Be brief.", "Write to <PERSON_1>, <EMAIL_1>, about <PERSON_1>."], seen.Sent!.Select(message => message.Text));
        Assert.Equal(2, seen.Placeholders);
        Assert.Equal(["read_file"], seen.Tools);
    }

    [Fact]
    public async Task A_refused_request_is_observed_without_its_text_and_nothing_is_sent()
    {
        var recorder = new Recorder();
        var provider = new ScriptedChatClient(_ => new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        var client = new PrivacyEgressChatClient(provider, Cloud, Egress(EgressPolicy.Default with { Action = EgressAction.Refuse }), recorder);

        await Assert.ThrowsAsync<PrivacyEgressRefusedException>(() =>
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "Write to Alice Smith.")], Options("s1"), Ct));

        var seen = Assert.Single(recorder.Seen);
        Assert.Equal((EgressOutcome.Refused, false, 0), (seen.Decision.Outcome, seen.Left, seen.Placeholders));
        Assert.Null(seen.Sent);
        Assert.Empty(seen.Tools);
        Assert.Empty(provider.Calls);
    }

    [Fact]
    public async Task A_streamed_request_with_nothing_to_redact_is_observed_with_no_placeholders()
    {
        var recorder = new Recorder();
        var client = new PrivacyEgressChatClient(new ScriptedChatClient(_ => new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"))),
            Cloud, Egress(EgressPolicy.Default), recorder);

        await foreach (var _ in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "What is 2 + 2?")], Options("s1"), Ct))
        {
        }

        var seen = Assert.Single(recorder.Seen);
        Assert.Equal((EgressOutcome.Redacted, 0), (seen.Decision.Outcome, seen.Placeholders));
    }
}
