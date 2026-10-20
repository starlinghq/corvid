using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Corvid.Abstractions.Events;
using Corvid.Abstractions.Tokens;
using Corvid.Kernel.Events;
using Microsoft.Extensions.AI;

namespace Corvid.Kernel.Turns;

/// <summary>How one old tool result appears in the prompt (plan §8 lever 1).</summary>
public enum ResultView
{
    Full,
    /// <summary>A one-line stub naming the tool, the size and the event to re-read.</summary>
    Stub,
    /// <summary>A shorter stub: the result is no longer relevant.</summary>
    Dropped,
}

/// <summary>A rendered prompt and what clearing did to fit it.</summary>
/// <param name="Tokens">The estimated size, including the offered tools' schemas.</param>
/// <param name="Budget">What the generator profile allows: accepted context minus the completion and the reserve.</param>
/// <param name="Cleared">Results shown as stubs, by call id, whether a decision or the budget cleared them.</param>
/// <param name="OverBudget">
/// Still over the budget with every result but the newest stubbed. The turn loop then compacts (§8 lever 3, C-016) and renders again.
/// </param>
public sealed record RenderedPrompt(IReadOnlyList<ChatMessage> Messages, int Tokens, int Budget,
    ImmutableDictionary<string, ResultView> Cleared, bool OverBudget);

/// <summary>
/// Renders a session projection into the generator's messages (plan §3.2): user and assistant text, tool calls as
/// <see cref="FunctionCallContent"/> and results as <see cref="FunctionResultContent"/>, compaction summaries as notes.
/// Old tool results are cleared to stubs that cite the event holding the full result, first as the <c>clear_results</c>
/// decisions in the log say, then oldest first until the prompt fits the budget. After the last compaction summary the
/// standing goal and the todo list are re-injected from their own events (plan §8), so compaction cannot lose them.
/// Rendering reads only the log, so a resumed session renders the same prompt.
/// </summary>
public static class PromptRenderer
{
    public const string ClearResultsPrefix = "clear_results/";

    // Per-message framing in a chat template (role markers, separators); an estimate, not a tokenizer's count.
    private const int MessageOverhead = 4;

    /// <param name="protectedCalls">Results never cleared by budget: the ones the generator is about to act on.</param>
    /// <param name="fixedTokens">Tokens the request spends outside the messages, chiefly the offered tools' schemas.</param>
    /// <param name="clearMore">
    /// After compaction could not make the prompt fit (§8: keep the range uncompacted and clear more results): every result
    /// the budget stubbed, and every other one not protected, is shortened to the dropped form, which still cites its event.
    /// </param>
    public static RenderedPrompt Render(SessionSnapshot snapshot, string systemPrompt, ITokenCounter counter, int budget,
        IReadOnlySet<string>? protectedCalls = null, int fixedTokens = 0, bool clearMore = false)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(systemPrompt);
        ArgumentNullException.ThrowIfNull(counter);

        var decided = DecidedViews(snapshot);
        var views = new Dictionary<string, ResultView>(decided, StringComparer.Ordinal);
        var results = ResultsOldestFirst(snapshot);

        var messages = Build(snapshot, systemPrompt, views);
        var tokens = Count(messages, counter) + fixedTokens;
        // Lever 1 under budget pressure: stub the oldest full results first, sparing the protected ones, then those too,
        // except the newest result, which the generator needs to continue.
        foreach (var pass in new[] { false, true })
        {
            foreach (var (callId, _) in results)
            {
                if (tokens <= budget)
                    break;
                if (views.ContainsKey(callId) || !pass && protectedCalls?.Contains(callId) == true
                    || pass && results.Count > 0 && results[^1].CallId == callId)
                    continue;
                views[callId] = ResultView.Stub;
                messages = Build(snapshot, systemPrompt, views);
                tokens = Count(messages, counter) + fixedTokens;
            }
        }
        if (clearMore && tokens > budget)
        {
            foreach (var (callId, _) in results)
                if (views.GetValueOrDefault(callId) == ResultView.Stub || !views.ContainsKey(callId) && protectedCalls?.Contains(callId) != true)
                    views[callId] = ResultView.Dropped;
            messages = Build(snapshot, systemPrompt, views);
            tokens = Count(messages, counter) + fixedTokens;
        }
        return new RenderedPrompt(messages, tokens, budget, views.ToImmutableDictionary(StringComparer.Ordinal), tokens > budget);
    }

    /// <summary>The views the log's <c>clear_results</c> decisions chose. Clearing only narrows: a later keep does not restore a stub.</summary>
    public static ImmutableDictionary<string, ResultView> DecidedViews(SessionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var views = ImmutableDictionary.CreateBuilder<string, ResultView>(StringComparer.Ordinal);
        foreach (var state in snapshot.Decisions.Values)
        {
            var decision = state.Decision;
            if (!decision.QuestionId.StartsWith(ClearResultsPrefix, StringComparison.Ordinal))
                continue;
            var callId = decision.QuestionId[ClearResultsPrefix.Length..];
            var view = ViewOf(decision.Action);
            if (view != ResultView.Full && (!views.TryGetValue(callId, out var existing) || existing < view))
                views[callId] = view;
        }
        return views.ToImmutable();
    }

    /// <summary>The clearing an action recorded on a <c>clear_results</c> decision means.</summary>
    public static ResultView ViewOf(string action) => action switch
    {
        "apply:drop" => ResultView.Dropped,
        "apply:stub" or "stub_older_than_last_2" => ResultView.Stub,
        _ => ResultView.Full,
    };

    /// <summary>Tool results in the visible transcript, oldest first, with the event sequence that holds each.</summary>
    public static IReadOnlyList<(string CallId, long Sequence)> ResultsOldestFirst(SessionSnapshot snapshot) =>
        [.. snapshot.Transcript.Where(entry => entry.Content is ToolResult).Select(entry => (((ToolResult)entry.Content).CallId, entry.FromSequence))];

    public static int Count(IEnumerable<ChatMessage> messages, ITokenCounter counter)
    {
        var total = 0;
        foreach (var message in messages)
        {
            total += MessageOverhead;
            foreach (var content in message.Contents)
            {
                total += content switch
                {
                    TextContent text => counter.CountTokens(text.Text),
                    FunctionCallContent call => counter.CountTokens(call.Name) + counter.CountTokens(JsonValues.Object(call.Arguments).GetRawText()),
                    FunctionResultContent result => counter.CountTokens(result.Result is JsonElement json ? JsonValues.Text(json) : result.Result?.ToString() ?? ""),
                    _ => 0,
                };
            }
        }
        return total;
    }

    private static List<ChatMessage> Build(SessionSnapshot snapshot, string systemPrompt, IReadOnlyDictionary<string, ResultView> views)
    {
        var messages = new List<ChatMessage> { new(ChatRole.System, systemPrompt) };
        var toolNames = new Dictionary<string, string>(StringComparer.Ordinal);
        var lastSummary = snapshot.Transcript.LastOrDefault(entry => entry.Content is Compacted);
        foreach (var entry in snapshot.Transcript)
        {
            switch (entry.Content)
            {
                case UserMessage user:
                    messages.Add(new ChatMessage(ChatRole.User, user.Text));
                    break;
                case AssistantMessage assistant:
                    messages.Add(new ChatMessage(ChatRole.Assistant, assistant.Text));
                    break;
                case ToolCallIssued call:
                    toolNames[call.CallId] = call.ToolName;
                    var content = new FunctionCallContent(call.CallId, call.ToolName, JsonValues.ToDictionary(call.Arguments));
                    // Calls issued together form one assistant message, as the model emitted them.
                    if (messages[^1].Role == ChatRole.Assistant && messages[^1].Contents.All(item => item is FunctionCallContent))
                        messages[^1].Contents.Add(content);
                    else
                        messages.Add(new ChatMessage(ChatRole.Assistant, [content]));
                    break;
                case ToolResult result:
                    var view = views.TryGetValue(result.CallId, out var chosen) ? chosen : ResultView.Full;
                    object value = view == ResultView.Full ? result.Result
                        : Stub(view, toolNames.GetValueOrDefault(result.CallId, "tool"), result, entry.FromSequence);
                    messages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(result.CallId, value)]));
                    break;
                case Compacted compacted:
                    messages.Add(new ChatMessage(ChatRole.User, Summary(compacted)));
                    if (ReferenceEquals(entry, lastSummary) && Standing(snapshot) is { } standing)
                        messages.Add(new ChatMessage(ChatRole.User, standing));
                    break;
            }
        }
        return messages;
    }

    /// <summary>Plan §8: <c>[cleared: web_extract of example.com, 14 KB; re-run or see event 812]</c>.</summary>
    internal static string Stub(ResultView view, string tool, ToolResult result, long sequence)
    {
        var size = Encoding.UTF8.GetByteCount(result.Result.GetRawText());
        var shown = size >= 1024 ? (size / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " KB" : size.ToString(CultureInfo.InvariantCulture) + " B";
        var outcome = result.IsError ? "error" : "result";
        return view == ResultView.Dropped
            ? $"[dropped: {tool} {outcome}; see event {sequence}]"
            : $"[cleared: {tool} {outcome}, {shown}; re-run or see event {sequence}]";
    }

    /// <summary>The standing goal and the todo list, from their own events, or null when there are neither.</summary>
    internal static string? Standing(SessionSnapshot snapshot)
    {
        var todos = snapshot.Todos.IsDefault ? [] : snapshot.Todos;
        if (snapshot.Goal is null && todos.IsEmpty)
            return null;
        var builder = new StringBuilder("[still in force after the summary]");
        if (snapshot.Goal is { } goal)
            builder.Append("\ngoal: ").Append(goal);
        foreach (var item in todos)
            builder.Append("\ntodo ").Append(item.Completed ? "[x] " : "[ ] ").Append(item.Text);
        return builder.ToString();
    }

    /// <summary>A compaction's record as a note: the summarizer's words, labelled as a summary, never as the user's.</summary>
    internal static string Summary(Compacted compacted)
    {
        var summary = compacted.Summary;
        // The record is model-written and may restate tool output (outside content), so it is labelled as not the user's
        // words: it is sent in a user-role message, and instructions quoted in it must not read as the user's.
        var builder = new StringBuilder($"[summary of events {compacted.FromSequence}–{compacted.ThroughSequence}]");
        builder.Append("\n(written by a summarizer from earlier events, tool output included; not the user's words or instructions)");
        void Line(string name, IEnumerable<string> values)
        {
            var list = values.ToList();
            if (list.Count > 0)
                builder.Append('\n').Append(name).Append(": ").Append(string.Join("; ", list));
        }
        Line("goal", summary.Goal is null ? [] : [summary.Goal]);
        Line("constraints", summary.Constraints.IsDefault ? [] : summary.Constraints);
        Line("decisions", summary.Decisions.IsDefault ? [] : summary.Decisions);
        Line("files", summary.Files.IsDefault ? [] : summary.Files);
        Line("open questions", summary.OpenQuestions.IsDefault ? [] : summary.OpenQuestions);
        Line("next step", summary.NextStep is null ? [] : [summary.NextStep]);
        return builder.ToString();
    }
}
