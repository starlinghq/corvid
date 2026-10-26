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

    public string Name => SafeText.Clean(Block.Name);

    public string CountText => string.Create(CultureInfo.InvariantCulture, $"{Block.Tokens} / {Block.Budget}");

    public double Fraction => Block.Budget <= 0 ? 0 : (double)Block.Tokens / Block.Budget;

    /// <summary>At or past 90% of its budget, where consolidation is offered.</summary>
    public bool IsNearBudget => Fraction >= 0.9;

    public string Detail { get; } = MemoryText.BlockDetail(block, time);

    /// <summary>The block as stored, for reading: hidden characters removed (a staged write to it shows them).</summary>
    public string Text => SafeText.Clean(Block.Text);

    /// <summary>A staged update to this block waits in Review, where consolidation is offered.</summary>
    public bool HasPendingUpdate { get; init; }

    /// <summary>Opens Review at the waiting update.</summary>
    public System.Windows.Input.ICommand? OpenReview { get; init; }

    public bool ShowsConsolidationOffer => IsNearBudget;

    public bool CanOpenReview => IsNearBudget && HasPendingUpdate;

    /// <summary>Asks the block rewrite job for a shorter rewrite, staged in Review (plan §9.5).</summary>
    public System.Windows.Input.ICommand? ConsolidateNow { get; init; }

    /// <summary>Near its budget with nothing waiting for it: "Consolidate now" is offered.</summary>
    public bool CanConsolidateNow => IsNearBudget && !HasPendingUpdate && ConsolidateNow is not null;

    /// <summary>The Core blocks tab's line under a block near its budget (F11: "offers consolidation").</summary>
    public string ConsolidationOffer => !IsNearBudget ? ""
        : HasPendingUpdate
            ? "Near its budget: consolidation is offered with the update waiting in Review."
            : "Near its budget: consolidate it now, or with the next staged write to it.";

    public string AutomationName => $"{Name} block, {Block.Tokens} of {Block.Budget} tokens{(IsNearBudget ? ", near its budget" : "")}, {Detail}";
}

/// <summary>A fact in a table: its text, its validity, when it was recorded, and whether it no longer holds.</summary>
public sealed class FactRowView(FactRecord fact)
{
    public FactRecord Fact { get; } = fact;

    /// <summary>The fact as stored, for reading: its marks and hidden characters removed.</summary>
    public string Text { get; } = MemoryText.CleanPlain(fact.Text);

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
        .. MemoryText.CleanSpans(lesson.Rule, MemorySpanStyle.Bold),
        new(" "),
        new($"Applies when {SafeText.Clean(lesson.AppliesWhen)}.", MemorySpanStyle.Muted),
    ];

    public string Detail { get; } = lesson.SupersededBy is { } by
        ? $"superseded by “{MemoryText.CleanPlain(by)}” · project {SafeText.Clean(lesson.Project)}"
        : $"approved {MemoryText.Date(lesson.Approved)} · project {SafeText.Clean(lesson.Project)}";

    public bool IsSuperseded => Lesson.SupersededBy is not null;
}

/// <summary>An entry of the import's dry run: the file, the text, and what would happen to it.</summary>
public sealed class ImportEntryView(ImportEntry entry)
{
    public ImportEntry Entry { get; } = entry;

    /// <summary>The file it came from, any hidden character shown: the dry run is what the person stages.</summary>
    public string File => SafeText.Reveal(Entry.File, keepJoiners: true);

    /// <summary>The entry's text, any hidden character shown as its code.</summary>
    public string Text => SafeText.Reveal(Entry.Text, keepJoiners: true);

    public bool IsRefused => Entry.Refused;

    /// <summary>"would be staged", or "refused: the scanner flagged a credential".</summary>
    public string Verdict => Entry.Refused ? "refused" : "would be staged";

    public string Reason => SafeText.Clean(Entry.Reason);

    public Tone VerdictTone => Entry.Refused ? Tone.Red : Tone.Green;

    public string AutomationName => $"{File}: {Text}. {Verdict}{(Reason.Length > 0 ? ", " + Reason : "")}";
}
