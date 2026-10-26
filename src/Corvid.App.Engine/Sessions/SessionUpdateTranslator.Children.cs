using System.Collections.Immutable;
using System.Text.Json;
using Corvid.App.Core.Sessions;
using Corvid.Host.Sessions;
using Corvid.Models;
using ChildFinished = Corvid.Abstractions.Events.ChildFinished;
using ChildSpawned = Corvid.Abstractions.Events.ChildSpawned;

namespace Corvid.App.Engine.Sessions;

public sealed partial class SessionUpdateTranslator
{
    // A5: each child's start, so its end (which carries only the status and answer) is shown with its goal and toolset.
    private readonly Dictionary<string, ChildSessionChanged> _children = new(StringComparer.Ordinal);

    /// <summary>
    /// A5: the host's <see cref="ChildStarted"/> and <see cref="ChildEnded"/> as the app's <see cref="ChildSessionChanged"/>:
    /// the session header's children and the Board's tree. An end whose start this translator never saw still names the child.
    /// </summary>
    public IReadOnlyList<SessionUpdate> Child(SessionStreamUpdate update) => Child(update, replayed: false);

    /// <summary>
    /// L1 (A5 review): whether a child runs in this process now, for a replayed start: one that is not running and has no end
    /// in the log is shown unfinished, without a Stop that could not reach it. Null: every replayed start shows running.
    /// </summary>
    public Func<string, bool>? IsChildRunning { get; set; }

    private IReadOnlyList<SessionUpdate> Child(SessionStreamUpdate update, bool replayed)
    {
        ArgumentNullException.ThrowIfNull(update);
        switch (update)
        {
            case ChildStarted started:
            {
                var child = Started(started.Child) with { At = update.At };
                if (replayed && IsChildRunning is { } running && !running(child.ChildId))
                    child = child with { Status = Unfinished };
                _children[child.ChildId] = child;
                return [child];
            }
            case ChildEnded ended:
            {
                var known = _children.GetValueOrDefault(ended.Child.ChildId) ?? new ChildSessionChanged(ended.Child.ChildId, "", ChildSessionChanged.Running);
                var child = Ended(known, ended.Child) with { At = update.At };
                _children[child.ChildId] = child;
                return [child];
            }
            default:
                return [];
        }
    }

    /// <summary>The status of a child whose start the log holds without an end, and which no process here runs.</summary>
    internal const string Unfinished = "unfinished";

    /// <summary>A child as it started (the parent's <c>ChildSpawned</c>).</summary>
    internal static ChildSessionChanged Started(ChildSpawned spawned) => new(spawned.ChildId, spawned.Goal, ChildSessionChanged.Running)
    {
        Tools = spawned.Tools.IsDefault ? [] : spawned.Tools,
        Budget = new ChildBudgetInfo(spawned.Budget.MaxSteps, spawned.Budget.MaxTokens, spawned.Budget.MaxSeconds),
        Mix = MixText(spawned.Mix),
        By = spawned.By,
        Tainted = spawned.Tainted,
        HasSchema = spawned.OutputSchema is not null,
    };

    /// <summary>A child as it ended (the parent's <c>ChildFinished</c>), over what was known of its start.</summary>
    internal static ChildSessionChanged Ended(ChildSessionChanged started, ChildFinished finished) => started with
    {
        Status = finished.Status,
        Answer = finished.Text,
        Detail = finished.Detail,
        Steps = finished.Steps,
        Tokens = finished.Tokens,
        OutputValid = finished.OutputValid,
        Tainted = started.Tainted || finished.Tainted,
    };

    /// <summary>"generator qwen3.5-9b · decider kev-0.8b", from the mix's JSON shape; null when it runs on its parent's.</summary>
    internal static string? MixText(JsonElement? mix)
    {
        if (mix is not { ValueKind: JsonValueKind.Object } element)
            return null;
        try
        {
            var parsed = ModelMix.Parse(element);
            var slots = parsed.Slots.Select(pair => $"{pair.Key} {pair.Value.Model}" +
                (!pair.Value.Escalate ? " (no escalation)" : pair.Value.Ladder is { IsEmpty: false } ladder ? " → " + string.Join(" → ", ladder) : ""));
            var text = string.Join(" · ", slots);
            return text.Length == 0 ? null : text;
        }
        catch (ModelMixException)
        {
            return element.GetRawText();
        }
    }
}
