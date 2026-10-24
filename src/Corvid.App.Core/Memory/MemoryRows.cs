using System.Globalization;
using Corvid.App.Core.Mvvm;

namespace Corvid.App.Core.Memory;

/// <summary>The Memory page's tabs (Memory.png's head).</summary>
public enum MemoryTabKind
{
    Review,
    Facts,
    CoreBlocks,
    Lessons,
    Import,
}

/// <summary>A tab of the page head, with the count of what waits in it ("Review 2", "Lessons 1").</summary>
public sealed class MemoryTab : ObservableObject
{
    private bool _isSelected;
    private int _count;

    internal MemoryTab(MemoryTabKind kind, string label, Action<MemoryTabKind> open)
    {
        Kind = kind;
        Label = label;
        Open = new RelayCommand(() => open(kind));
    }

    public MemoryTabKind Kind { get; }

    public string Label { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public int Count
    {
        get => _count;
        set
        {
            if (!SetProperty(ref _count, value))
                return;
            OnPropertyChanged(nameof(BadgeText));
            OnPropertyChanged(nameof(AutomationName));
        }
    }

    /// <summary>The badge; empty hides it.</summary>
    public string BadgeText => _count > 0 ? _count.ToString(CultureInfo.InvariantCulture) : "";

    public string AutomationName => _count > 0 ? $"{Label}, {_count} waiting" : Label;

    public RelayCommand Open { get; }
}

/// <summary>A core block with its meter: "user", "318 / 350", amber from 90%, and "local-only · revision 14 · updated 14:02".</summary>
public sealed class CoreBlockView(CoreBlock block, TimeProvider time)
{
    public CoreBlock Block { get; } = block;

    public string Name => Block.Name;

    public string CountText => string.Create(CultureInfo.InvariantCulture, $"{Block.Tokens} / {Block.Budget}");

    public double Fraction => Block.Budget <= 0 ? 0 : (double)Block.Tokens / Block.Budget;

    /// <summary>At or past 90% of its budget, where consolidation is offered.</summary>
    public bool IsNearBudget => Fraction >= 0.9;

    public string Detail { get; } = MemoryText.BlockDetail(block, time);

    public string Text => Block.Text;

    public string AutomationName => $"{Name} block, {Block.Tokens} of {Block.Budget} tokens{(IsNearBudget ? ", near its budget" : "")}, {Detail}";
}

/// <summary>A fact in a table: its text, its validity, when it was recorded, and whether it no longer holds.</summary>
public sealed class FactRowView(FactRecord fact)
{
    public FactRecord Fact { get; } = fact;

    public string Text { get; } = MemoryText.Plain(fact.Text);

    public string ValidText { get; } = MemoryText.Valid(fact);

    public string RecordedText { get; } = MemoryText.Date(fact.Recorded);

    public bool IsInvalidated => Fact.Invalidated;

    public bool IsCurrent => !Fact.Invalidated;

    public string AutomationName => $"{Text}{(IsInvalidated ? ", invalidated" : "")}, valid {ValidText}, recorded {RecordedText}";
}

/// <summary>An approved lesson in the Lessons tab, struck through once superseded.</summary>
public sealed class LessonRowView(Lesson lesson)
{
    public Lesson Lesson { get; } = lesson;

    public IReadOnlyList<MemorySpan> Text { get; } =
    [
        .. MemoryText.Spans(lesson.Rule, MemorySpanStyle.Bold),
        new(" "),
        new($"Applies when {lesson.AppliesWhen}.", MemorySpanStyle.Muted),
    ];

    public string Detail { get; } = lesson.SupersededBy is { } by
        ? $"superseded by “{by}” · project {lesson.Project}"
        : $"approved {MemoryText.Date(lesson.Approved)} · project {lesson.Project}";

    public bool IsSuperseded => Lesson.SupersededBy is not null;
}

/// <summary>An entry of the import's dry run: the file, the text, and what would happen to it.</summary>
public sealed class ImportEntryView(ImportEntry entry)
{
    public ImportEntry Entry { get; } = entry;

    public string File => Entry.File;

    public string Text => Entry.Text;

    public bool IsRefused => Entry.Refused;

    /// <summary>"would be staged", or "refused: the scanner flagged a credential".</summary>
    public string Verdict => Entry.Refused ? "refused" : "would be staged";

    public string Reason => Entry.Reason;

    public Tone VerdictTone => Entry.Refused ? Tone.Red : Tone.Green;

    public string AutomationName => $"{File}: {Text}. {Verdict}{(Reason.Length > 0 ? ", " + Reason : "")}";
}
