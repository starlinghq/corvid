using System.Text;
using System.Text.RegularExpressions;

namespace Corvid.Host.Sessions;

/// <summary>How a turn's last answer ends, as the Board reads it (C-043, docs/plan/ui.md §4.13).</summary>
public enum TurnEnding
{
    /// <summary>No answer yet, or one that neither asks the person anything nor reads as finished work.</summary>
    None,

    /// <summary>The answer asks the person for something ("PR #12 is up. Want me to merge it?"): it is their turn.</summary>
    AsksYou,

    /// <summary>The answer reads as a finished handoff ("All tests pass and PR #12 is merged."): the Board suggests Done.</summary>
    Finished,
}

/// <summary>
/// C-043: a deterministic reading of a turn's last answer, so the Board can tell "your turn" from finished or parked work
/// without a model call. It reads the answer's last paragraph after dropping fenced code, quoted blocks and a trailing
/// list (a summary or links), and matches a question to the person or a short request list, else a short list of closing
/// phrases. A request beats a closing phrase ("All tests pass. Merge it now?" asks).
/// <para>
/// English only, and a heuristic: a false "your turn" is put aside with the Board's "Not now", a missed one stays in Parked
/// with its status line. C-045 replaces it with Kev's calibrated <c>handoff</c> question where that clears its floor, and
/// keeps this as the degraded rule. Pure, so it runs on read for every session, old and read-only ones included.
/// </para>
/// </summary>
public static partial class HandoffRule
{
    /// <summary>How <paramref name="lastAnswer"/> ends; <see cref="TurnEnding.None"/> for no answer.</summary>
    public static TurnEnding Classify(string? lastAnswer)
    {
        if (string.IsNullOrWhiteSpace(lastAnswer))
            return TurnEnding.None;
        var (paragraph, list) = LastParagraph(lastAnswer);
        if (paragraph.Length == 0 && list is null)
            return TurnEnding.None;
        if (AsksYou(paragraph) || list is { } item && AsksYou(item))
            return TurnEnding.AsksYou;
        // A list-only answer ("- PR #12 is merged\n- docs updated") is read over its list, never over what the parser dropped.
        return Closes(paragraph) || paragraph.Length == 0 && Closes(Eligible(lastAnswer))
            ? TurnEnding.Finished
            : TurnEnding.None;
    }

    /// <summary>
    /// The sentence a your-turn answer asks with, for the Board's card when no generated status line is current: in the
    /// paragraph the rule reads (or the dropped list's last item), a closing question, else the last sentence with a request
    /// ("Should I merge it? Why am I asking? The branches overlap." shows the first), else the last question that addresses
    /// the person; null when the answer does not ask (<see cref="Classify"/> is not AsksYou).
    /// </summary>
    public static string? Question(string? lastAnswer)
    {
        if (Classify(lastAnswer) != TurnEnding.AsksYou)
            return null;
        var (paragraph, list) = LastParagraph(lastAnswer!);
        foreach (var text in (string?[])[paragraph, list])
        {
            if (string.IsNullOrEmpty(text))
                continue;
            var sentences = Sentence().Split(text.Trim()).Where(sentence => sentence.Length > 0).ToList();
            // The same tests AsksYou applies, so the sentence shown is one that made the answer ask.
            if (sentences.Count > 0 && sentences[^1].EndsWith('?'))
                return sentences[^1];
            if (sentences.LastOrDefault(sentence => Request().IsMatch(Pleasantry().Replace(sentence, ""))) is { } request)
                return request;
            if (sentences.LastOrDefault(sentence => sentence.EndsWith('?') && Addressed().IsMatch(sentence)) is { } question)
                return question;
        }

        return paragraph.Length > 0 ? paragraph : list;
    }

    /// <summary>
    /// The paragraph the rule reads, flattened to one line without markdown emphasis, and the last item of a trailing list
    /// it dropped (a question can close a list of options: "1. … 2. Ok to delete the rest?"); empty when nothing is left.
    /// </summary>
    internal static (string Paragraph, string? ListItem) LastParagraph(string answer)
    {
        var lines = WithoutFences(answer.ReplaceLineEndings("\n")).Split('\n');
        var paragraphs = new List<List<string>>();
        var current = new List<string>();
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                if (current.Count > 0)
                    paragraphs.Add(current);
                current = [];
                continue;
            }

            if (Skipped(line))
                continue;
            current.Add(line);
        }

        if (current.Count > 0)
            paragraphs.Add(current);
        string? listItem = null;
        while (paragraphs.Count > 0)
        {
            var last = paragraphs[^1];
            // A trailing list (a summary of changes, links) is dropped, and the lines before it in the same paragraph kept.
            var keep = last.Count;
            while (keep > 0 && ListItem().IsMatch(last[keep - 1]))
                keep--;
            if (keep < last.Count)
                listItem ??= Text(ListItem().Replace(last[^1], ""));
            if (keep > 0)
                return (Text(string.Join(' ', last.Take(keep))), listItem);
            paragraphs.RemoveAt(paragraphs.Count - 1);
        }

        return ("", listItem);
    }

    // A quoted block is someone else's words (a drafted reply, an issue): its question is not the model's.
    // Headings, tables and rules carry no request either.
    private static bool Skipped(string line) =>
        line.StartsWith('>') || line.StartsWith('#') || line.StartsWith('|') || RuleLine().IsMatch(line);

    // The lines the rule may read at all (no code, quotes, headings, tables or rules), list marks dropped, as one line.
    private static string Eligible(string answer) =>
        Text(string.Join(' ', WithoutFences(answer.ReplaceLineEndings("\n")).Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !Skipped(line))
            .Select(line => ListItem().Replace(line, ""))));

    private static bool AsksYou(string paragraph)
    {
        if (paragraph.Length == 0)
            return false;
        var text = Pleasantry().Replace(paragraph, "");
        if (Request().IsMatch(text))
            return true;
        var sentences = Sentence().Split(text.Trim()).Where(sentence => sentence.Length > 0).ToList();
        for (var i = 0; i < sentences.Count; i++)
        {
            if (!sentences[i].EndsWith('?'))
                continue;
            // The paragraph's last sentence is a question: it is put to the person. An earlier one counts when it addresses
            // them, so a rhetorical "Why does it fail? Because …" does not, nor one the model puts to itself ("What do I do
            // about the lock? I'll retry.").
            if (i == sentences.Count - 1 || Addressed().IsMatch(sentences[i]))
                return true;
        }

        return false;
    }

    // A closing phrase in a sentence that does not deny it: "The tests haven't finished yet" is not a finished handoff.
    private static bool Closes(string text) =>
        Sentence().Split(text.Trim()).Any(sentence => Closing().IsMatch(sentence) && !Negated().IsMatch(sentence));

    // Fenced code (``` or ~~~) is the work, not what it asks.
    private static string WithoutFences(string text)
    {
        var kept = new StringBuilder(text.Length);
        string? fence = null;
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (fence is null && (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal)))
            {
                fence = trimmed[..3];
                // A blank line where the block was, so the text on either side stays in separate paragraphs.
                kept.Append('\n');
                continue;
            }

            if (fence is not null)
            {
                if (trimmed.StartsWith(fence, StringComparison.Ordinal))
                    fence = null;
                continue;
            }

            kept.Append(line).Append('\n');
        }

        return kept.ToString();
    }

    // One line, without emphasis and code marks, with straight apostrophes, so "**Want me to** merge `main`?" matches.
    private static string Text(string text) =>
        Spaces().Replace(text.Replace('\n', ' ').Replace('’', '\'').Replace("**", "", StringComparison.Ordinal).Replace("`", "", StringComparison.Ordinal)
            .Replace("__", "", StringComparison.Ordinal), " ").Trim();

    [GeneratedRegex(@"\b(want me to|shall I|should I|would you like|do you want|ok to|okay to|let me know|can you|could you|please (confirm|review|approve|check|run|test|merge|try|decide)|your call|which (one|option|do you)|once you('ve| have)|when you're ready|waiting (for|on) you|up to you)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Request();

    // A closing courtesy is not a request: "Done. Let me know if you need anything else." is a finished handoff.
    [GeneratedRegex(@"\b(let me know|feel free to ask) if (you )?(need|want|have) (anything|any|something) ?(else|more|questions?)?\b[.!]?|\bif you have any (other |more )?questions\b[^.?!]*[.!]?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Pleasantry();

    [GeneratedRegex(@"\b((is|are|now) (done|complete|merged|fixed|in place|pushed)|all (\d[\d,]* )?tests pass(ed)?|PR #\d+ (is up|merged|opened)|that's (it|everything)|nothing (else|left) to do|completed|finished)\b|^(all )?done\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Closing();

    [GeneratedRegex(@"\b(you|your|shall I|should I)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Addressed();

    [GeneratedRegex(@"\b(not|never|yet|\w+n't)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Negated();

    [GeneratedRegex(@"(?<=[.!?])\s+")]
    private static partial Regex Sentence();

    [GeneratedRegex(@"^([-*+]|\d+[.)])\s+")]
    private static partial Regex ListItem();

    [GeneratedRegex(@"^([-*_]\s*){3,}$")]
    private static partial Regex RuleLine();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
