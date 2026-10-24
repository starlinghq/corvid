using Corvid.App.Core.Sessions;

namespace Corvid.App.Pages;

/// <summary>Picks the transcript's template by the block's kind: one template per <see cref="TranscriptBlockKind"/>.</summary>
public sealed partial class TranscriptTemplateSelector : DataTemplateSelector
{
    public DataTemplate? User { get; set; }
    public DataTemplate? TurnHeader { get; set; }
    public DataTemplate? Decisions { get; set; }
    public DataTemplate? Switch { get; set; }
    public DataTemplate? Tool { get; set; }
    public DataTemplate? Approval { get; set; }
    public DataTemplate? Taint { get; set; }
    public DataTemplate? Answer { get; set; }
    public DataTemplate? Thought { get; set; }
    public DataTemplate? Notice { get; set; }
    public DataTemplate? Detection { get; set; }
    public DataTemplate? Compaction { get; set; }
    public DataTemplate? Rewind { get; set; }
    public DataTemplate? Question { get; set; }
    public DataTemplate? Command { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) => item is TranscriptBlock block
        ? block.Kind switch
        {
            TranscriptBlockKind.User => User,
            TranscriptBlockKind.TurnHeader => TurnHeader,
            TranscriptBlockKind.Decisions => Decisions,
            TranscriptBlockKind.Switch => Switch,
            TranscriptBlockKind.Tool => Tool,
            TranscriptBlockKind.Approval => Approval,
            TranscriptBlockKind.Taint => Taint,
            TranscriptBlockKind.Answer => Answer,
            TranscriptBlockKind.Thought => Thought,
            TranscriptBlockKind.Notice => Notice,
            TranscriptBlockKind.Detection => Detection,
            TranscriptBlockKind.Compaction => Compaction,
            TranscriptBlockKind.Rewind => Rewind,
            TranscriptBlockKind.Question => Question,
            TranscriptBlockKind.Command => Command,
            _ => null,
        }
        : null;

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);
}
