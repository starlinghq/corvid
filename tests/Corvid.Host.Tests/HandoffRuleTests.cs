using System.Globalization;
using System.Text.Json;
using Corvid.Host.Sessions;

namespace Corvid.Host.Tests;

/// <summary>C-043: the Board's "your turn" and "looks finished" by rule, over a turn's last answer.</summary>
public sealed class HandoffRuleTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("PR #12 is up. Want me to merge it?", TurnEnding.AsksYou)]
    [InlineData("All tests pass. Merge it now?", TurnEnding.AsksYou)]
    [InlineData("The branch is pushed. Let me know when you've had a look.", TurnEnding.AsksYou)]
    [InlineData("Which one do you prefer: memory or SQLite", TurnEnding.AsksYou)]
    [InlineData("**Shall I** start with `step 1`?", TurnEnding.AsksYou)]
    [InlineData("PR #86 is merged. The backlog row is marked built.", TurnEnding.Finished)]
    [InlineData("Done. Let me know if you need anything else.", TurnEnding.Finished)]
    [InlineData("The fix is in place and all 2,114 tests pass.", TurnEnding.Finished)]
    [InlineData("Why does the rail say idle? Because a reply that asks in prose sets no state.", TurnEnding.None)]
    [InlineData("I'm reading the engine's summary code next.", TurnEnding.None)]
    [InlineData("The tests haven't finished yet, I'll check back.", TurnEnding.None)]
    [InlineData("The build is not completed. I'm looking at the linker error.", TurnEnding.None)]
    [InlineData("What do I do about the lock? I'll try a retry.", TurnEnding.None)]
    [InlineData("Should I keep the old API? I left it in for now.", TurnEnding.AsksYou)]
    [InlineData("", TurnEnding.None)]
    [InlineData(null, TurnEnding.None)]
    public void Classifies_the_last_paragraph(string? answer, TurnEnding ending) => Assert.Equal(ending, HandoffRule.Classify(answer));

    [Fact]
    public void Code_quotes_and_a_trailing_summary_list_are_not_what_it_reads()
    {
        // A question inside fenced code or a quoted draft is not put to the person.
        Assert.Equal(TurnEnding.None, HandoffRule.Classify("Here is the prompt:\n\n```\nShould I merge it?\n```\n\nIt goes in the system message."));
        Assert.Equal(TurnEnding.None, HandoffRule.Classify("> Should we keep the old API?\n\nThe maintainers said it stays."));
        // A trailing list is dropped and the paragraph before it read: a summary after a closing line reads as finished.
        Assert.Equal(TurnEnding.Finished, HandoffRule.Classify("PR #140 opened as a draft.\n\n- schema 2\n- the columns\n- tests"));
        // A question closing a list of options still asks.
        Assert.Equal(TurnEnding.AsksYou, HandoffRule.Classify("Three have changes:\n\n- flocks\n- Ok to delete the rest?"));
        // A request in an earlier paragraph does not count once the answer moved on.
        Assert.Equal(TurnEnding.Finished, HandoffRule.Classify("Should I merge it? You said yes earlier.\n\nPR #12 is merged."));
    }

    [Theory]
    [InlineData("PR #12 is up. CI is green. Want me to merge it?", "Want me to merge it?")]
    [InlineData("I tried both. Which one do you prefer? The first is faster.", "Which one do you prefer?")]
    [InlineData("The branch is pushed. Let me know when you've had a look.", "Let me know when you've had a look.")]
    [InlineData("Three have changes:\n\n- flocks\n- Ok to delete the rest?", "Ok to delete the rest?")]
    [InlineData("PR #86 is merged.", null)]
    [InlineData(null, null)]
    public void The_question_the_card_shows_is_the_one_the_answer_asks_with(string? answer, string? question) =>
        Assert.Equal(question, HandoffRule.Question(answer));

    [Fact]
    public void A_request_beats_a_closing_phrase() =>
        Assert.Equal(TurnEnding.AsksYou, HandoffRule.Classify("PR #131 is merged and the worktrees are removed. Should I start on the docs next?"));

    /// <summary>
    /// The fixture (Fixtures/handoff-replies.jsonl) is 62 labelled turn-ending replies in the style of a coding assistant's,
    /// the last two the false positives a review found (a denied closing phrase, a question the model put to itself).
    /// They are synthetic: the plan asked for scrubbed replies from Mark's own logs, which this change could not read; they
    /// should be replaced or extended with real ones, and C-045's eval rows come from the same labelled set. The bounds below
    /// are the rule's measured figures on it (your turn: precision 1.00, recall 0.96; finished: precision 1.00, recall 0.78),
    /// so a change that makes the rule worse fails here. A missed "finished" only loses the Done suggestion: it stays Parked.
    /// </summary>
    [Fact]
    public void Precision_and_recall_on_the_labelled_replies()
    {
        var rows = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "handoff-replies.jsonl"))
            .Where(line => line.Length > 0)
            .Select(line => JsonDocument.Parse(line).RootElement)
            .Select(row => (Label: Label(row.GetProperty("label").GetString()!), Text: row.GetProperty("text").GetString()!))
            .ToList();
        Assert.Equal(62, rows.Count);
        var predicted = rows.Select(row => (row.Label, Got: HandoffRule.Classify(row.Text), row.Text)).ToList();
        foreach (var (label, got, text) in predicted.Where(row => row.Label != row.Got))
            output.WriteLine($"{label} read as {got}: {text.ReplaceLineEndings(" ")}");
        var asks = Score(predicted, TurnEnding.AsksYou);
        var finished = Score(predicted, TurnEnding.Finished);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"your turn: precision {asks.Precision:0.00}, recall {asks.Recall:0.00}; finished: precision {finished.Precision:0.00}, recall {finished.Recall:0.00}"));

        Assert.True(asks.Precision >= 0.95, $"your-turn precision {asks.Precision:0.00}");
        Assert.True(asks.Recall >= 0.90, $"your-turn recall {asks.Recall:0.00}");
        Assert.True(finished.Precision >= 0.85, $"finished precision {finished.Precision:0.00}");
        Assert.True(finished.Recall >= 0.75, $"finished recall {finished.Recall:0.00}");
    }

    private static TurnEnding Label(string label) => label switch
    {
        "asks_you" => TurnEnding.AsksYou,
        "finished" => TurnEnding.Finished,
        _ => TurnEnding.None,
    };

    private static (double Precision, double Recall) Score(List<(TurnEnding Label, TurnEnding Got, string Text)> rows, TurnEnding ending)
    {
        var hits = rows.Count(row => row.Got == ending && row.Label == ending);
        var said = rows.Count(row => row.Got == ending);
        var actual = rows.Count(row => row.Label == ending);
        return (said == 0 ? 1 : (double)hits / said, actual == 0 ? 1 : (double)hits / actual);
    }
}
