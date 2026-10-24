using System.ComponentModel;
using Corvid.App.Core.Markdown;
using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Sessions;

namespace Corvid.App.Core.Board;

/// <summary>A line of a peek: who or what ("you", "corvid", "read_file"), and the text.</summary>
public sealed record PeekLine(string Label, string Text, bool IsCode = false)
{
    /// <summary>Two lines are the same when they read the same and their runs are styled the same.</summary>
    public bool Equals(PeekLine? other) =>
        other is not null && Label == other.Label && Text == other.Text && IsCode == other.IsCode && Runs.SequenceEqual(other.Runs);

    public override int GetHashCode() => HashCode.Combine(Label, Text, IsCode, Runs.Count);

    public string AutomationName => $"{Label}: {Text}";

    /// <summary>The text as styled runs: an answer's code spans and bold, without the markdown's backticks and stars.</summary>
    public IReadOnlyList<MarkdownRun> Runs { get; init; } = [new MarkdownRun(Text)];

    /// <summary>An answer's line: its markdown flattened to one paragraph of runs, the plain text for reading aloud.</summary>
    public static PeekLine Answer(string label, string markdown)
    {
        var runs = Flatten(MarkdownDocument.Parse(markdown).Blocks).ToList();
        if (runs.Count == 0)
            return new PeekLine(label, markdown);
        return new PeekLine(label, string.Concat(runs.Select(run => run.Text))) { Runs = runs };
    }

    private static IEnumerable<MarkdownRun> Flatten(IEnumerable<MarkdownBlock> blocks) => blocks.SelectMany(block => block switch
    {
        MarkdownParagraph paragraph => paragraph.Runs,
        MarkdownHeading heading => heading.Runs,
        MarkdownList list => list.Items.SelectMany(item => Flatten(item.Blocks)),
        MarkdownQuote quote => Flatten(quote.Blocks),
        _ => [],
    });
}

/// <summary>
/// Peek (docs/plan/ui.md §4.13, F18): a session's latest exchange, read-only and live, without attaching to it. It
/// follows the card it was opened from, which follows the session; Open attaches, Esc or Close goes back to the Board.
/// </summary>
public sealed class BoardPeekViewModel : ObservableObject, IDisposable
{
    /// <summary>How many of the latest transcript entries a peek shows.</summary>
    public const int Lines = 8;

    private const int MaxText = 280;

    private IReadOnlyList<PeekLine> _lines = [];

    public BoardPeekViewModel(SessionCardViewModel card, RelayCommand close)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(close);
        Card = card;
        CloseCommand = close;
        Card.PropertyChanged += OnCardChanged;
        Rebuild();
    }

    public SessionCardViewModel Card { get; }

    public IReadOnlyList<PeekLine> Entries { get => _lines; private set => SetProperty(ref _lines, value); }

    public bool HasEntries => _lines.Count > 0;

    /// <summary>Whether this window may attach: a session another process drives is watched only.</summary>
    public string Note => Card.IsReadOnly
        ? "Read-only live view: another process drives this session."
        : "Read-only live view: nothing here is sent. Open attaches the session to this window.";

    public RelayCommand CloseCommand { get; }

    public void Dispose() => Card.PropertyChanged -= OnCardChanged;

    /// <summary>
    /// The latest entries of a session's transcript as lines: messages, tool calls, answers, prompts and notices. Every
    /// line is what a session reported, so it is cleaned (<see cref="SafeText.Clean"/>); an answer's markdown cleans itself.
    /// </summary>
    public static IReadOnlyList<PeekLine> Build(SessionView? view)
    {
        if (view is null)
            return [];
        var lines = new List<PeekLine>();
        foreach (var entry in view.Transcript)
        {
            PeekLine? line = entry switch
            {
                UserEntry user => new PeekLine("you", Clip(user.Text)),
                ToolEntry tool => new PeekLine(SafeText.Clean(tool.Tool), Clip(BoardText.Join(tool.Target, Status(tool))), IsCode: true),
                AnswerEntry answer when answer.Text.Length > 0 => PeekLine.Answer("corvid", Clip(answer.Text)),
                ApprovalEntry approval => new PeekLine("approval", BoardText.Join(Clip(approval.Request.Text), $"{approval.Position} of {approval.Count}")),
                QuestionEntry question => new PeekLine("question", question.Answer is { } given
                    ? $"{Clip(question.Question.Text)} → {Clip(given)}"
                    : Clip(question.Question.Text)),
                NoticeEntry notice => new PeekLine("notice", Clip(notice.Notice.Text)),
                _ => null,
            };
            if (line is not null)
                lines.Add(line);
        }

        return lines.Count > Lines ? lines[^Lines..] : lines;
    }

    private static string Status(ToolEntry tool) => tool.Status switch
    {
        ToolCallStatus.Pending => tool.ApprovalId is not null ? "waiting on approval" : "pending",
        ToolCallStatus.InProgress => "running",
        ToolCallStatus.Failed => "failed",
        _ => tool.Summary is { Length: > 0 } summary ? summary : "done",
    };

    private static string Clip(string text)
    {
        var flat = string.Join(' ', SafeText.Clean(text).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length <= MaxText ? flat : string.Concat(flat.AsSpan(0, MaxText - 1), "…");
    }

    private void OnCardChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SessionCardViewModel.View) or nameof(SessionCardViewModel.IsReadOnly))
            Rebuild();
    }

    private void Rebuild()
    {
        // The note follows the card's read-only state whether or not the lines changed.
        OnPropertyChanged(nameof(Note));
        var lines = Build(Card.View);
        if (lines.SequenceEqual(_lines))
            return;
        Entries = lines;
        OnPropertyChanged(nameof(HasEntries));
    }
}
