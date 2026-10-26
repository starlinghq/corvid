using System.Globalization;
using Corvid.Abstractions.Events;
using Corvid.Kernel.Events;
using Corvid.Memory;

namespace Corvid.Host.Sessions;

/// <summary>
/// One place a search across sessions found (G-5): a session event whose text matched, where it sits in the conversation,
/// and a snippet of it. The snippet is the log's text (the user's, a model's, a tool's: untrusted), without control or
/// format characters; a surface cleans it again its own way before showing it.
/// </summary>
/// <param name="Sequence">The matched event's position in the session's log.</param>
/// <param name="Turn">The turn it falls in, as a replay numbers turns (the latest prompt at or before it; 0 before the first).</param>
/// <param name="TurnSequence">Where that turn's prompt is in the log (0 before the first), so a surface can open the session there.</param>
/// <param name="Kind">The event's discriminator (<c>user_message</c>, <c>assistant_message</c>, <c>tool_result</c>, ...).</param>
/// <param name="Title">The session's title (its rename, generated title or first message), or null.</param>
public sealed record SessionSearchHit(string SessionId, long Sequence, int Turn, long TurnSequence, string Kind, DateTimeOffset At,
    string Snippet, string? Title)
{
    /// <summary>Who wrote the matched text, in a word a list shows: "you", "Corvid", "a tool", ...</summary>
    public string Who => SessionSearch.Who(Kind);
}

/// <summary>What a search found, or why it could not search.</summary>
/// <param name="Unavailable">Why there is no index to search (memory is off); null when the search ran.</param>
public sealed record SessionSearchResult(IReadOnlyList<SessionSearchHit> Hits, string? Unavailable = null)
{
    public static SessionSearchResult Off(string why) => new([], why);
}

/// <summary>
/// G-5: search across sessions through the episodic index (plan §9.1, <see cref="EpisodicIndex"/>), shared by
/// <c>/search</c> in <c>corvid chat</c> and the app's <c>Ctrl+Shift+F</c>. Each session's index first catches up with the log,
/// so what was said a moment ago, here or in another process, is found. Only what the conversation still holds is a hit: an
/// event an undo or rollback took out of it is left out, though the log keeps it. The index never holds a <c>!!</c> line's
/// message, call or output (A4: those are the person's alone).
/// </summary>
public static class SessionSearch
{
    /// <summary>The most hits a search returns.</summary>
    public const int MaxHits = 50;

    /// <summary>How many characters of a matched event a snippet shows, around the first matched word.</summary>
    public const int SnippetChars = 160;

    /// <param name="sessions">The sessions to search, in any order; the caller decides which are conversations.</param>
    /// <param name="caughtUp">Called with the episodes each session's catch-up added (to embed them, say); may be null.</param>
    public static SessionSearchResult Search(IEventLog log, EpisodicIndex index, IEnumerable<string> sessions, string query, int limit = 20,
        Action<IReadOnlyList<Episode>>? caughtUp = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(query);
        limit = Math.Clamp(limit, 1, MaxHits);
        if (query.Trim().Length == 0)
            return new([]);
        var included = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in sessions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!included.Add(id))
                continue;
            var added = index.CatchUp(log, id, cancellationToken);
            if (added.Count > 0)
                caughtUp?.Invoke(added);
        }
        if (included.Count == 0)
            return new([]);

        // The index searches every session; hits in sessions not asked for, or no longer in the conversation, are dropped,
        // so a few times the limit is read.
        var candidates = index.Search(query, Math.Min(limit * 4, 400));
        var projections = new Dictionary<string, (SessionSnapshot Snapshot, long[] Prompts)>(StringComparer.Ordinal);
        var hits = new List<SessionSearchHit>(limit);
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var episode = candidate.Episode;
            if (!included.Contains(episode.SessionId))
                continue;
            if (!projections.TryGetValue(episode.SessionId, out var projection))
            {
                var snapshot = SessionProjection.Load(log, episode.SessionId, expandCompactions: true, cancellationToken: cancellationToken);
                var prompts = snapshot.Transcript.Where(entry => entry.Content is UserMessage).Select(entry => entry.FromSequence).Order().ToArray();
                projections[episode.SessionId] = projection = (snapshot, prompts);
            }
            if (!projection.Snapshot.IsVisible(episode.Sequence))
                continue;
            var (turn, at) = TurnAt(projection.Prompts, episode.Sequence);
            var title = SessionCatalogue.Describe(log, episode.SessionId, cancellationToken)?.Title;
            hits.Add(new SessionSearchHit(episode.SessionId, episode.Sequence, turn, at, episode.Kind, episode.RecordedAt,
                Snippet(episode.Text, query), title));
            if (hits.Count == limit)
                break;
        }
        return new(hits);
    }

    /// <summary>The turn a log position falls in and where its prompt is: the latest prompt at or before it.</summary>
    internal static (int Turn, long Prompt) TurnAt(long[] prompts, long sequence)
    {
        var turn = 0;
        while (turn < prompts.Length && prompts[turn] <= sequence)
            turn++;
        return (turn, turn == 0 ? 0 : prompts[turn - 1]);
    }

    /// <summary>
    /// One line of <paramref name="text"/> around the first word of <paramref name="query"/> it holds, at most
    /// <see cref="SnippetChars"/> long, an ellipsis where it was cut; without control or format characters.
    /// </summary>
    public static string Snippet(string text, string query)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(query);
        var line = string.Join(' ', SessionCatalogue.Visible(text).Split(['\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (line.Length <= SnippetChars)
            return line;
        var at = -1;
        foreach (var word in query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var found = line.IndexOf(word.Trim('"', '*'), StringComparison.OrdinalIgnoreCase);
            if (found >= 0 && (at < 0 || found < at))
                at = found;
        }
        var start = at < 0 ? 0 : Math.Max(0, at - SnippetChars / 3);
        var end = Math.Min(line.Length, start + SnippetChars);
        start = Math.Max(0, end - SnippetChars);
        // Not inside a surrogate pair.
        if (start > 0 && char.IsLowSurrogate(line[start]))
            start++;
        if (end < line.Length && char.IsLowSurrogate(line[end]))
            end--;
        return (start > 0 ? "…" : "") + line[start..end].Trim() + (end < line.Length ? "…" : "");
    }

    /// <summary>Who wrote an episode of the given kind, for a list.</summary>
    public static string Who(string kind) => kind switch
    {
        "user_message" => "you",
        "assistant_message" => "Corvid",
        "tool_call_issued" => "a call",
        "tool_result" => "a tool",
        "approval_requested" => "an approval",
        "compacted" => "a summary",
        "goal_set" => "the goal",
        "todos_set" => "the tasks",
        _ => kind,
    };

    /// <summary>A hit's place in words: "turn 3 · you · 25 Oct 14:02".</summary>
    public static string Where(SessionSearchHit hit, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(hit);
        ArgumentNullException.ThrowIfNull(zone);
        var when = TimeZoneInfo.ConvertTime(hit.At, zone).ToString("d MMM HH:mm", CultureInfo.InvariantCulture);
        return hit.Turn > 0 ? $"turn {hit.Turn.ToString(CultureInfo.InvariantCulture)} · {hit.Who} · {when}" : $"{hit.Who} · {when}";
    }
}
