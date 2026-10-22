using System.Buffers;
using System.Text.Encodings.Web;
using System.Text.Json;
using Corvid.Abstractions.Fabric;
using Corvid.Abstractions.Sessions;
using Corvid.Fabric;
using Corvid.Kernel.Turns;
using Corvid.Models.Privacy;

namespace Corvid.Kernel.Jobs;

/// <param name="SessionId">The run's own session: the script's approval and the <c>wake</c> decision are logged there.</param>
/// <param name="State">The TurnState of the tick, for the approval pipeline's <c>risk</c> question.</param>
/// <param name="Tainted">
/// The job was created in a tainted session, so the script may be someone else's: the approval pipeline assesses it as a
/// tainted action (plan §10.3), and network egress asks, which at a scheduled run means it is refused.
/// </param>
public sealed record JobScriptRequest(string SessionId, string JobId, string Script, TurnState State, bool Tainted = false);

/// <param name="Ran">False when the approval pipeline or the sandbox refused the script; nothing ran.</param>
/// <param name="Refusal">Why it did not run.</param>
public sealed record JobScriptResult(bool Ran, int? ExitCode, string Output, string? Refusal = null)
{
    public static JobScriptResult Refused(string reason) => new(false, null, "", reason);
}

/// <summary>Runs a job's pre-run script. The built-in implementation authorizes it non-interactively and runs it sandboxed.</summary>
public interface IJobScriptRunner
{
    Task<JobScriptResult> RunAsync(JobScriptRequest request, CancellationToken cancellationToken);
}

/// <param name="Text">What the agent is asked: the job's prompt, with the script's output when there was one.</param>
/// <param name="Tainted">The text holds content from outside the user (script output, or a prompt written in a tainted turn).</param>
public sealed record JobAgentRequest(string SessionId, ScheduledJob Job, string Text, bool Tainted);

/// <param name="Tainted">
/// The run's turn held outside content when it answered (a fetched page, script output), so its text is not the user's
/// alone (plan §10.3).
/// </param>
public sealed record JobAgentResult(string Status, string? Text, bool Tainted = false);

/// <summary>Runs the agent for one job run, in the run's fresh session.</summary>
public interface IJobAgent
{
    Task<JobAgentResult> RunAsync(JobAgentRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Runs a job as one turn of the turn loop in the run's session. The services must be built for unattended work
/// (<see cref="TurnOptions.Interactive"/> false): a side effect that would ask is denied, because nobody is there to answer.
/// </summary>
public sealed class TurnJobAgent(Func<string, TurnServices> services, string surface = "scheduler") : IJobAgent
{
    public async Task<JobAgentResult> RunAsync(JobAgentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var built = services(request.SessionId);
        if (built.Options.Interactive)
            throw new InvalidOperationException("Scheduled runs have nobody to ask: build their turn services with TurnOptions.Interactive = false.");
        var runner = new TurnRunner(request.SessionId, built);
        // An external envelope taints the session, so network egress asks (and, unattended, is denied).
        var result = await runner.RunAsync(new Envelope(request.SessionId, surface, request.Text,
            request.Tainted ? EnvelopeOrigin.External : EnvelopeOrigin.User), cancellationToken).ConfigureAwait(false);
        return new JobAgentResult(result.Status.ToString().ToLowerInvariant(), result.Text, result.Tainted);
    }
}

public sealed record JobSchedulerOptions
{
    public static JobSchedulerOptions Default { get; } = new();

    /// <summary>The longest the loop sleeps before looking for due jobs again.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The TurnState's surface for a tick.</summary>
    public string Surface { get; init; } = "scheduler";

    /// <summary>The TurnState's generator description.</summary>
    public string Generator { get; init; } = "unknown";

    /// <summary>Script output beyond this many characters is cut (its end is kept) before it reaches <c>wake</c> or the agent.</summary>
    public int MaxScriptOutputChars { get; init; } = 4_000;
}

/// <summary>
/// The durable scheduler (plan §13). Each due job is claimed in the store, then run: its pre-run script (sandboxed, through
/// the approval pipeline, non-interactively), then for a no-agent job nothing more; otherwise, when there was a script,
/// <c>wake</c> at the tick checkpoint on the script's output decides whether the agent is needed (below its floor the
/// tick is skipped and logged), and the agent runs in the run's own fresh session. Every run is recorded with its status.
/// </summary>
public sealed class JobScheduler
{
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly JobStore _store;
    private readonly DecisionFabric _fabric;
    private readonly IJobScriptRunner? _scripts;
    private readonly IJobAgent? _agent;
    private readonly TurnStateBuilder _states;
    private readonly JobSchedulerOptions _options;
    private readonly TimeProvider _time;

    /// <param name="scripts">Runs pre-run scripts; without it a job with a script fails rather than skip the script.</param>
    /// <param name="agent">Runs the agent; without it agent jobs fail.</param>
    public JobScheduler(JobStore store, DecisionFabric fabric, IJobScriptRunner? scripts = null, IJobAgent? agent = null,
        TurnStateBuilder? states = null, JobSchedulerOptions? options = null, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(fabric);
        _store = store;
        _fabric = fabric;
        _scripts = scripts;
        _agent = agent;
        _states = states ?? new TurnStateBuilder();
        _options = options ?? JobSchedulerOptions.Default;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_options.PollInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.MaxScriptOutputChars);
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Called after each run is recorded; the gateway's delivery (C-020) will hang off this.</summary>
    public Func<ScheduledJob, JobRun, CancellationToken, Task>? Completed { get; init; }

    /// <summary>Marks runs a previous process left running as interrupted, then runs due jobs until cancelled.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _store.InterruptStale();
        while (!cancellationToken.IsCancellationRequested)
        {
            await RunDueAsync(cancellationToken).ConfigureAwait(false);
            var now = _time.GetUtcNow();
            // A paused job has no next run, so an overdue paused job cannot make this wait zero and spin.
            var next = _store.List().Select(job => job.NextRunAt).Where(at => at is not null).Min();
            var wait = next is { } at ? at - now : _options.PollInterval;
            wait = wait < TimeSpan.Zero ? TimeSpan.Zero : wait > _options.PollInterval ? _options.PollInterval : wait;
            try
            {
                await Task.Delay(wait, _time, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    /// <summary>Claims and runs every job due now, one at a time. Returns the runs as recorded.</summary>
    public async Task<IReadOnlyList<JobRun>> RunDueAsync(CancellationToken cancellationToken = default)
    {
        var finished = new List<JobRun>();
        foreach (var claimed in _store.ClaimDue(_time.GetUtcNow()))
        {
            var run = await ExecuteAsync(claimed, cancellationToken).ConfigureAwait(false);
            finished.Add(run);
            if (Completed is { } completed)
                await completed(claimed.Job, run, cancellationToken).ConfigureAwait(false);
        }
        return finished;
    }

    private async Task<JobRun> ExecuteAsync(ClaimedRun claimed, CancellationToken cancellationToken)
    {
        var (job, run) = (claimed.Job, claimed.Run);
        string? output = null;
        int? exitCode = null;
        string? wakeId = null;
        try
        {
            if (job.Script is { } script)
            {
                if (_scripts is null)
                    return Finish(run, JobRunStatus.Failed, detail: "no script runner is configured");
                var state = State(job, null);
                var result = await _scripts.RunAsync(new JobScriptRequest(run.SessionId, job.JobId, script, state, job.CreatedTainted), cancellationToken)
                    .ConfigureAwait(false);
                if (!result.Ran)
                    return Finish(run, JobRunStatus.ScriptRefused, detail: result.Refusal);
                output = Tail(result.Output);
                exitCode = result.ExitCode;
            }

            if (job.NoAgent)
                return Finish(run, JobRunStatus.ScriptOnly, scriptExitCode: exitCode, output: output);

            if (output is not null)
            {
                // §13: the script's output feeds wake, so "wake me only if something needs me" costs one decider request.
                var verdict = await WakeAsync(job, run, output, exitCode, cancellationToken).ConfigureAwait(false);
                wakeId = verdict.DecisionId;
                if (!verdict.Verdict.Acted)
                    return Finish(run, JobRunStatus.Skipped, wakeId, exitCode, output, $"wake: {verdict.Verdict.Action} ({verdict.Verdict.Reason})");
            }

            if (_agent is null)
                return Finish(run, JobRunStatus.Failed, wakeId, exitCode, output, "no agent is configured");
            var text = output is null ? job.Prompt : $"{job.Prompt}\n\nPre-run script output (exit {exitCode}):\n{output}";
            var answer = await _agent.RunAsync(new JobAgentRequest(run.SessionId, job, text, job.CreatedTainted || output is not null),
                cancellationToken).ConfigureAwait(false);
            return Finish(run, JobRunStatus.Ran, wakeId, exitCode, answer.Text, answer.Status);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Finish(run, JobRunStatus.Interrupted, wakeId, exitCode, output, "cancelled");
            throw;
        }
        catch (Exception exception)
        {
            return Finish(run, JobRunStatus.Failed, wakeId, exitCode, output, exception.Message);
        }
    }

    private async Task<FabricVerdict> WakeAsync(ScheduledJob job, JobRun run, string output, int? exitCode, CancellationToken cancellationToken)
    {
        var entry = _fabric.Policy.Catalogue["wake"];
        // The output goes in the question's own instructions (§5.1: content one question needs is not state).
        var instructions = Instructions(writer =>
        {
            writer.WriteString("question", entry.Instructions);
            writer.WriteString("job", job.Name);
            writer.WriteString("task", job.Prompt);
            if (exitCode is { } code)
                writer.WriteNumber("exit_code", code);
            writer.WriteString("script_output", output);
        });
        // Script output is outside content (plan §10.3): a cloud generator answering the question sees the request as tainted.
        using var scope = new ModelRequestContext(run.SessionId, Tainted: true).Enter();
        var result = await _fabric.DecideAsync(run.SessionId, Checkpoint.Tick, State(job, output),
            [KeyValuePair.Create(entry.Id, entry.ToQuestion(instructions: DecisionContent.FromJson(instructions)))],
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return result[entry.Id];
    }

    private TurnState State(ScheduledJob job, string? output) =>
        _states.Build(new TurnStateInput(job.NoAgent ? job.Name : job.Prompt, _options.Surface, _options.Generator, Goal: job.Name,
            LastToolResult: output));

    private string Tail(string output)
    {
        var max = _options.MaxScriptOutputChars;
        if (output.Length <= max)
            return output;
        var start = output.Length - max;
        // Never split a surrogate pair.
        if (char.IsLowSurrogate(output[start]))
            start++;
        return "…" + output[start..];
    }

    private JobRun Finish(JobRun run, string status, string? wakeDecisionId = null, int? scriptExitCode = null, string? output = null,
        string? detail = null) =>
        _store.Finish(run.RunId, status, _time.GetUtcNow(), wakeDecisionId, scriptExitCode, output, detail);

    private static JsonElement Instructions(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }
}
