using System.Text.Json;
using Corvid.Abstractions.Events;
using Corvid.Abstractions.Extraction;
using Corvid.Abstractions.Sessions;
using Corvid.Approvals;
using Corvid.Host.Composition;
using Corvid.Host.Sessions;
using Corvid.Kernel.Events;
using Corvid.Kernel.Sessions;
using Corvid.Models;
using Microsoft.Extensions.AI;

namespace Corvid.Host.Tests;

/// <summary>
/// G-15: forking a session at a step boundary while its turn runs, without waiting for the turn: the fork copies the log
/// through the turn's last completed step and goes on alone, with its own actor and driver lock, the source's taint and its
/// session model choices; the running turn is untouched.
/// </summary>
public sealed class ForkDuringTurnTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // A failure detector, never a synchronisation: the fork must return while the turn is held, which it does at once.
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private static IReadOnlyList<StoredEvent> Stored(CorvidRuntime runtime, string sessionId) =>
        [.. runtime.Log.Read(sessionId, limit: 4096)];

    private static string Persisted(SessionEvent payload) => JsonSerializer.Serialize(payload, payload.GetType());

    /// <summary>A prompter that holds every prompt until the test answers it.</summary>
    private sealed class HeldPrompter : IApprovalPrompter
    {
        public TaskCompletionSource<ApprovalPrompt> Asked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<ApprovalAnswer> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<ApprovalAnswer> AskAsync(ApprovalPrompt prompt, CancellationToken cancellationToken)
        {
            Asked.TrySetResult(prompt);
            return await Answer.Task.WaitAsync(cancellationToken);
        }
    }

    /// <summary>Finds "Ada" as a person, so the privacy gate has something to weigh.</summary>
    private sealed class PersonExtractor : IExtractor
    {
        public Task<ExtractionResult> ExtractAsync(ExtractionRequest request, CancellationToken cancellationToken = default)
        {
            var at = request.Text.IndexOf("Ada", StringComparison.Ordinal);
            return Task.FromResult(new ExtractionResult(at < 0 || !request.Labels.Contains("person") ? []
                : [new ExtractedSpan("person", "Ada", at, at + 3, 0.99f)]));
        }
    }

    [Fact]
    public async Task A_fork_while_a_turn_runs_copies_through_its_last_step_without_waiting_and_the_turn_goes_on_untouched()
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var generator = new FakeGenerator(Reply.Call("list_notes"), Reply.Say("You have two notes."), Reply.Say("Answer in the fork."))
        {
            // The source's second generator call, after its first round's results: the turn is past a step boundary.
            Hold = (step, _) =>
            {
                if (step != 1)
                    return null;
                reached.TrySetResult();
                return release.Task;
            },
        };
        await using var host = TestHost.Create(new FakeModels(generator));
        await using var runtime = await host.StartAsync();
        var source = runtime.OpenSession(surface: "test");
        var turn = source.SendAsync("list my notes", Ct);
        await reached.Task.WaitAsync(Bound, Ct);
        var boundary = source.Runner.StepBoundary;
        Assert.NotNull(boundary);
        Assert.True(runtime.IsTurnRunning(source.Id));

        var forking = runtime.ForkAsync(source.Id, new ForkOptions { Surface = "test" }, Ct);
        var fork = await forking.WaitAsync(Bound, Ct);

        // The fork came back while the turn is still held at its second step.
        Assert.False(turn.IsCompleted);
        Assert.True(fork.DuringTurn);
        Assert.Equal(boundary, fork.Through);
        Assert.NotEqual(source.Id, fork.Session.Id);
        Assert.NotSame(source.Actor, fork.Session.Actor);
        // What the fork holds is the source's log through the step, event for event: the call and its result, no answer.
        var copied = Stored(runtime, fork.Session.Id);
        var before = Stored(runtime, source.Id).Where(item => item.Sequence <= fork.Through).ToList();
        Assert.Equal(before.Select(item => (item.Sequence, Persisted(item.Payload))), copied.Select(item => (item.Sequence, Persisted(item.Payload))));
        Assert.Contains(copied, item => item.Payload is ToolResult);
        Assert.DoesNotContain(copied, item => item.Payload is AssistantMessage);

        release.SetResult();
        var delivery = await turn.WaitAsync(Bound, Ct);
        Assert.Null(delivery.Error);
        Assert.Equal("You have two notes.", delivery.Result!.Text);
        // The source's log is its own: its turn finished as if no fork had been taken, and nothing names the fork.
        var sourceLog = Stored(runtime, source.Id);
        Assert.Equal(before.Select(item => Persisted(item.Payload)), sourceLog.Take(before.Count).Select(item => Persisted(item.Payload)));
        Assert.Equal("You have two notes.", Assert.Single(sourceLog.Select(item => item.Payload).OfType<AssistantMessage>()).Text);
        Assert.DoesNotContain(sourceLog, item => Persisted(item.Payload).Contains(fork.Session.Id, StringComparison.Ordinal));
        Assert.Equal(before.Count, Stored(runtime, fork.Session.Id).Count);

        // The fork replays to the copied point, as the source does through it.
        var sourceReplay = await SessionReplay.ReadAsync(runtime, source.Id, new ToolPresentation(runtime), through: fork.Through, cancellationToken: Ct);
        var forkReplay = await SessionReplay.ReadAsync(runtime, fork.Session.Id, new ToolPresentation(runtime), cancellationToken: Ct);
        Assert.Equal(fork.Through, forkReplay.Head);
        Assert.Equal(Describe(sourceReplay), Describe(forkReplay));
        Assert.Contains(forkReplay.Updates, update => update is PromptSent { Text: "list my notes" });
        Assert.Contains(forkReplay.Updates, update => update is ToolCallStarted);
        Assert.DoesNotContain(forkReplay.Updates, update => update is AnswerChunk);

        // And the fork takes turns of its own, on the copied context: the source's message, its call and result, no answer.
        var forked = await fork.Session.SendAsync("anything else?", Ct);
        Assert.Null(forked.Error);
        var prompt = generator.Calls[2];
        Assert.Equal([ChatRole.System, ChatRole.User, ChatRole.Assistant, ChatRole.Tool, ChatRole.User], prompt.Select(message => message.Role));
        Assert.Equal(("list my notes", "anything else?"), (prompt[1].Text, prompt[4].Text));
        Assert.DoesNotContain(Stored(runtime, source.Id), item => item.Payload is UserMessage { Text: "anything else?" });
    }

    private static List<string> Describe(SessionHistory history) =>
    [
        .. history.Updates.Select(update => update switch
        {
            PromptSent prompt => "prompt " + prompt.Text,
            ToolCallStarted call => "call " + call.Title + " " + call.Status,
            ToolCallChanged changed => "changed " + changed.Status,
            AnswerChunk answer => "answer " + answer.Text,
            _ => update.GetType().Name,
        }),
    ];

    [Fact]
    public async Task A_fork_while_the_turn_waits_at_an_approval_holds_the_turns_message_and_no_prompt()
    {
        var generator = new FakeGenerator(Reply.Call("add_note", """{"text":"water the plants"}"""), Reply.Say("Added it."));
        await using var host = TestHost.Create(new FakeModels(generator));
        await using var runtime = await host.StartAsync();
        var source = runtime.OpenSession(surface: "test");
        var prompter = new HeldPrompter();
        using var _ = runtime.AttachPrompter(source.Id, prompter);
        var turn = source.SendAsync("add a note to water the plants", Ct);
        await prompter.Asked.Task.WaitAsync(Bound, Ct);
        var head = runtime.Log.GetHead(source.Id, Ct);

        // A position inside the step is refused: the running turn's last completed step is as far as a fork goes.
        var refused = await Assert.ThrowsAsync<SessionForkException>(() => runtime.ForkAsync(source.Id, new ForkOptions { Through = head }, Ct));
        Assert.Contains("its last completed step", refused.Message, StringComparison.Ordinal);

        var fork = await runtime.ForkAsync(source.Id, new ForkOptions { Surface = "test" }, Ct).WaitAsync(Bound, Ct);

        Assert.False(turn.IsCompleted);
        var copied = Stored(runtime, fork.Session.Id).Select(item => item.Payload).ToList();
        Assert.Equal("add a note to water the plants", Assert.IsType<UserMessage>(copied[^1]).Text);
        Assert.DoesNotContain(copied, payload => payload is ApprovalRequested or ToolCallIssued);
        Assert.True(SessionProjection.Load(runtime.Log, fork.Session.Id, cancellationToken: Ct).PendingApprovals.IsEmpty);

        prompter.Answer.SetResult(ApprovalAnswer.Approve());
        var delivery = await turn.WaitAsync(Bound, Ct);
        Assert.Equal("Added it.", delivery.Result!.Text);
        Assert.Contains("water the plants", host.Models.Tools.Notes);
        Assert.Equal(copied.Count, Stored(runtime, fork.Session.Id).Count);
    }

    [Fact]
    public async Task A_fork_starts_with_the_sources_taint_and_its_session_model_choices()
    {
        await using var host = TestHost.Create(new FakeModels(new FakeGenerator(Reply.Say("noted"))));
        await using var runtime = await host.StartAsync();
        var source = runtime.OpenSession(surface: "test");
        await (await source.PostAsync("forwarded from someone", EnvelopeOrigin.External, Ct));
        var chosen = await source.SetModelAsync(ModelSlots.Generator, "fake-gen-large", SwitchScope.Session, Ct);
        Assert.Equal(SwitchStatus.Switched, chosen.Status);
        Assert.True(source.Tainted);

        var fork = await runtime.ForkAsync(source.Id, new ForkOptions { Surface = "test" }, Ct);

        Assert.False(fork.DuringTurn);
        Assert.True(fork.Tainted);
        Assert.True(fork.Session.Tainted);
        Assert.True(fork.Session.Slots.HasSessionChoice(ModelSlots.Generator));
        Assert.Equal("fake-gen-large@cpu", fork.Session.Slots.Current(ModelSlots.Generator).Profile.Id);
        // A5 (with the switch group's restore): the choice is in the copied log, so the fork opens on it and nothing more is
        // logged after the copy; the fork's own choice is that copied ModelSwitched with the reason session.
        Assert.Equal(SwitchStatus.Unchanged, Assert.Single(fork.Models).Status);
        Assert.DoesNotContain(Stored(runtime, fork.Session.Id), item => item.Sequence > fork.Through && item.Payload is ModelSwitched);
        Assert.Equal("session", Assert.IsType<ModelSwitched>(Stored(runtime, fork.Session.Id).Select(item => item.Payload)
            .OfType<ModelSwitched>().Last()).Reason);
        Assert.Contains(runtime.ModelChoices(fork.Session.Id), choice => choice.Key == ModelSlots.Generator);
        // The source keeps its own.
        Assert.Equal("fake-gen-large@cpu", source.Slots.Current(ModelSlots.Generator).Profile.Id);
    }

    [Fact]
    public async Task A_mix_picked_for_the_fork_passes_the_privacy_gate_with_the_forks_taint()
    {
        var models = new FakeModels(new FakeGenerator(Reply.Say("noted"), Reply.Say("noted")));
        models.Provider
            .Add("fake-cloud", [ModelSlots.Generator], () => new FakeGenerator(Reply.Say("from the cloud")), privacy: PrivacyClass.Cloud)
            .Add("fake-pii", [ModelSlots.Extractor], () => new PersonExtractor(), context: 512, tools: Corvid.Models.ToolCallSupport.None);
        await using var host = TestHost.Create(models, new Dictionary<string, string?>
        {
            ["Corvid:Models:Slots:extractor:Model"] = "fake-pii",
            ["Corvid:Privacy:Tainted"] = "Refuse",
        });
        await using var runtime = await host.StartAsync();
        var clean = runtime.OpenSession(surface: "test");
        await clean.SendAsync("write to Ada", Ct);
        var tainted = runtime.OpenSession(surface: "test");
        await (await tainted.PostAsync("forwarded from Ada", EnvelopeOrigin.External, Ct));
        var mix = ModelMix.Parse("""{"generator":"fake-cloud"}""");

        var allowed = await runtime.ForkAsync(clean.Id, new ForkOptions { Mix = mix }, Ct);
        var gated = await runtime.ForkAsync(tainted.Id, new ForkOptions { Mix = mix }, Ct);

        Assert.Equal(SwitchStatus.Switched, Assert.Single(allowed.Models).Status);
        Assert.Equal("fake-cloud@cpu", allowed.Session.Slots.Current(ModelSlots.Generator).Profile.Id);
        var refusal = Assert.Single(gated.Models);
        Assert.Equal(SwitchStatus.Refused, refusal.Status);
        Assert.True(gated.Tainted);
        Assert.Equal("fake-gen@cpu", gated.Session.Slots.Current(ModelSlots.Generator).Profile.Id);

        // A ladder is not a fork's to set, and a model nobody has is refused before anything is written.
        var sessions = runtime.Log.ListSessions(Ct).Count;
        await Assert.ThrowsAsync<ArgumentException>(() =>
            runtime.ForkAsync(clean.Id, new ForkOptions { Mix = ModelMix.Parse("""{"generator":{"model":"fake-cloud","escalate":false}}""") }, Ct));
        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            runtime.ForkAsync(clean.Id, new ForkOptions { Mix = ModelMix.Parse("""{"generator":"gpt-9"}""") }, Ct));
        Assert.Equal(sessions, runtime.Log.ListSessions(Ct).Count);
    }

    [Fact]
    public async Task A_source_no_actor_here_runs_forks_at_its_latest_resting_point()
    {
        await using var host = TestHost.Create();
        await using var runtime = await host.StartAsync();
        const string source = "cli-elsewhere";
        var arguments = JsonDocument.Parse("{}").RootElement;
        runtime.Log.Append(source, 0,
        [
            EventData.Create(new UserMessage("list my notes")),
            EventData.Create(new ToolCallIssued("c-1", "list_notes", arguments, 1, true)),
            EventData.Create(new ToolResult("c-1", JsonDocument.Parse("\"buy milk\"").RootElement)),
            // Another process's turn, mid-call.
            EventData.Create(new ToolCallIssued("c-2", "list_notes", arguments, 2, true)),
        ], Ct);

        var fork = await runtime.ForkAsync(source, new ForkOptions { Surface = "test" }, Ct);

        Assert.Equal(3, fork.Through);
        Assert.False(fork.DuringTurn);
        Assert.Equal(3, runtime.Log.GetHead(fork.Session.Id, Ct));
        Assert.Equal(3, SessionFork.LastRestingPoint(runtime.Log, source, cancellationToken: Ct));
    }

    [Fact]
    public async Task Slash_fork_forks_and_lets_go_of_the_fork_which_keeps_its_local_model_choices()
    {
        await using var host = TestHost.Create(new FakeModels(new FakeGenerator(Reply.Say("noted"))));
        await using var runtime = await host.StartAsync();
        var commands = Corvid.Host.Commands.CoreCommands.Register(new Corvid.Host.Commands.SlashCommandRegistry());
        var session = runtime.OpenSession(surface: "test");
        await session.SendAsync("hello", Ct);
        var context = new Corvid.Host.Commands.SlashCommandContext(runtime, session, commands);
        Assert.True(commands.TryGet("fork", out var fork) && fork.DuringTurn);

        var plain = await commands.ExecuteAsync("/fork", context, Ct);
        var picked = await commands.ExecuteAsync("/fork generator=fake-gen-large", context, Ct);
        var bad = await commands.ExecuteAsync("/fork sideways", context, Ct);

        Assert.Equal(Corvid.Host.Commands.OutputTone.Success, plain.Tone);
        var plainId = System.Text.RegularExpressions.Regex.Match(plain.Lines[0], @"Forked into (\S+) through").Groups[1].Value;
        // Let go of, so another process can drive it.
        Assert.False(runtime.TryGetSession(plainId, out _));
        Assert.Equal(runtime.Log.GetHead(session.Id, Ct), runtime.Log.GetHead(plainId, Ct));
        Assert.Contains(picked.Lines, line => line == "generator: fake-gen-large@cpu for the fork's session.");
        var pickedId = System.Text.RegularExpressions.Regex.Match(picked.Lines[0], @"Forked into (\S+) through").Groups[1].Value;
        // A5: a local choice is in the fork's log and comes back when it is opened again, so the fork is let go of too.
        Assert.False(runtime.TryGetSession(pickedId, out _));
        Assert.Contains(picked.Lines, line => line.StartsWith("Continue it with corvid chat --session", StringComparison.Ordinal));
        var kept = runtime.OpenSession(pickedId, "test");
        Assert.Equal("fake-gen-large@cpu", kept.Slots.Current(ModelSlots.Generator).Profile.Id);
        Assert.True(kept.Slots.HasSessionChoice(ModelSlots.Generator));
        Assert.Equal(Corvid.Host.Commands.OutputTone.Error, bad.Tone);
    }

    [Fact]
    public async Task A_child_session_is_not_forked()
    {
        await using var host = TestHost.Create(new FakeModels(new FakeGenerator(Reply.Say("done"))));
        await using var runtime = await host.StartAsync();
        var parent = runtime.OpenSession(surface: "test");
        var child = await runtime.RunChildAsync(parent.Id, new ChildRequest("say done"), Ct);

        var refused = await Assert.ThrowsAsync<SessionForkException>(() => runtime.ForkSessionAsync(child.ChildId, cancellationToken: Ct));

        Assert.Contains("child session", refused.Message, StringComparison.Ordinal);
    }
}
