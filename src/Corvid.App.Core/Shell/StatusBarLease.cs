using System.ComponentModel;

namespace Corvid.App.Core.Shell;

/// <summary>
/// An area's hold on the status bar while it shows (the Board, Models, the first run): it remembers what the bar showed
/// when the area took it, writes the area's items, and when the area goes puts back what it replaced, so an area that
/// writes nothing of its own (Skills, Settings) does not inherit the last area's items. An item someone else wrote
/// meanwhile is theirs and stays. An empty list is written as a fresh instance, so it is never mistaken for another
/// writer's empty list (<c>[]</c> is one shared instance).
/// </summary>
public sealed class StatusBarLease
{
    private readonly StatusBarViewModel _bar;
    private readonly Dictionary<string, object?> _saved = new(StringComparer.Ordinal);
    private readonly Dictionary<string, object?> _mine = new(StringComparer.Ordinal);
    private bool _writing;

    public StatusBarLease(StatusBarViewModel bar)
    {
        ArgumentNullException.ThrowIfNull(bar);
        _bar = bar;
        _bar.PropertyChanged += OnBarChanged;
    }

    /// <summary>The area holds the bar: it took it and has not given it back.</summary>
    public bool IsHeld { get; private set; }

    /// <summary>
    /// Takes the bar, remembering what it shows; taking it again while held changes nothing. If another area still holds
    /// the bar (areas hear a navigation in the order they were created, so the next one can take before the last lets
    /// go), the bar passes over: this lease keeps what that one saved, so leaving puts back the bar as it was before
    /// either area, and the other lease ends without restoring.
    /// </summary>
    public void Take()
    {
        if (IsHeld)
            return;
        IsHeld = true;
        _saved.Clear();
        _mine.Clear();
        if (Holders.TryGetValue(_bar, out var holder) && holder.Lease is { IsHeld: true } other && !ReferenceEquals(other, this))
        {
            foreach (var (name, value) in other._saved)
                _saved[name] = value;
            // What the other area wrote and still owns is this lease's to put back too.
            foreach (var (name, value) in other._mine)
                _mine[name] = value;
            other.Release(restore: false);
        }
        else
        {
            foreach (var (name, value) in Read())
                _saved[name] = value;
        }

        Holders.GetOrCreateValue(_bar).Lease = this;
    }

    /// <summary>The lease holding each bar, so a lease taking a held bar can take over its baseline.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<StatusBarViewModel, Holder> Holders = new();

    private sealed class Holder
    {
        public StatusBarLease? Lease { get; set; }
    }

    /// <summary>
    /// Gives the bar back. With <paramref name="restore"/>, every item this lease wrote and nobody has written since is
    /// put back as it was when the bar was taken; without it (the next area writes the whole bar), the items stay.
    /// </summary>
    public void Release(bool restore = true)
    {
        if (!IsHeld)
            return;
        IsHeld = false;
        if (Holders.TryGetValue(_bar, out var holder) && ReferenceEquals(holder.Lease, this))
            holder.Lease = null;
        if (restore)
        {
            foreach (var (name, value) in Read())
            {
                if (_mine.TryGetValue(name, out var mine) && Equals(value, mine) && _saved.TryGetValue(name, out var saved))
                    Set(name, saved);
            }
        }

        _mine.Clear();
    }

    public string Engine { get => _bar.Engine; set => Write(nameof(StatusBarViewModel.Engine), value); }

    public string Generator { get => _bar.Generator; set => Write(nameof(StatusBarViewModel.Generator), value); }

    public string Decider { get => _bar.Decider; set => Write(nameof(StatusBarViewModel.Decider), value); }

    public string Context { get => _bar.Context; set => Write(nameof(StatusBarViewModel.Context), value); }

    public IReadOnlyList<StatusField> AreaFields { get => _bar.AreaFields; set => Write(nameof(StatusBarViewModel.AreaFields), value.Count == 0 ? new List<StatusField>() : value); }

    public ApprovalMode? Mode { get => _bar.Mode; set => Write(nameof(StatusBarViewModel.Mode), value); }

    public IReadOnlyList<StatusTag> Tags { get => _bar.Tags; set => Write(nameof(StatusBarViewModel.Tags), value.Count == 0 ? new List<StatusTag>() : value); }

    public string Scheduler { get => _bar.Scheduler; set => Write(nameof(StatusBarViewModel.Scheduler), value); }

    public string Gateway { get => _bar.Gateway; set => Write(nameof(StatusBarViewModel.Gateway), value); }

    public string Branch { get => _bar.Branch; set => Write(nameof(StatusBarViewModel.Branch), value); }

    public string SessionId { get => _bar.SessionId; set => Write(nameof(StatusBarViewModel.SessionId), value); }

    public string Trailing { get => _bar.Trailing; set => Write(nameof(StatusBarViewModel.Trailing), value); }

    private void Write(string name, object? value)
    {
        if (!IsHeld)
            return;
        _mine[name] = value;
        Set(name, value);
    }

    /// <summary>Another writer changed an item: it is no longer this lease's to put back.</summary>
    private void OnBarChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_writing && e.PropertyName is { } name)
            _mine.Remove(name);
    }

    private IEnumerable<(string Name, object? Value)> Read()
    {
        yield return (nameof(StatusBarViewModel.Engine), _bar.Engine);
        yield return (nameof(StatusBarViewModel.Generator), _bar.Generator);
        yield return (nameof(StatusBarViewModel.Decider), _bar.Decider);
        yield return (nameof(StatusBarViewModel.Context), _bar.Context);
        yield return (nameof(StatusBarViewModel.AreaFields), _bar.AreaFields);
        yield return (nameof(StatusBarViewModel.Mode), _bar.Mode);
        yield return (nameof(StatusBarViewModel.Tags), _bar.Tags);
        yield return (nameof(StatusBarViewModel.Scheduler), _bar.Scheduler);
        yield return (nameof(StatusBarViewModel.Gateway), _bar.Gateway);
        yield return (nameof(StatusBarViewModel.Branch), _bar.Branch);
        yield return (nameof(StatusBarViewModel.SessionId), _bar.SessionId);
        yield return (nameof(StatusBarViewModel.Trailing), _bar.Trailing);
    }

    private void Set(string name, object? value)
    {
        _writing = true;
        try
        {
            switch (name)
            {
                case nameof(StatusBarViewModel.Engine): _bar.Engine = (string)value!; break;
                case nameof(StatusBarViewModel.Generator): _bar.Generator = (string)value!; break;
                case nameof(StatusBarViewModel.Decider): _bar.Decider = (string)value!; break;
                case nameof(StatusBarViewModel.Context): _bar.Context = (string)value!; break;
                case nameof(StatusBarViewModel.AreaFields): _bar.AreaFields = (IReadOnlyList<StatusField>)value!; break;
                case nameof(StatusBarViewModel.Mode): _bar.Mode = (ApprovalMode?)value; break;
                case nameof(StatusBarViewModel.Tags): _bar.Tags = (IReadOnlyList<StatusTag>)value!; break;
                case nameof(StatusBarViewModel.Scheduler): _bar.Scheduler = (string)value!; break;
                case nameof(StatusBarViewModel.Gateway): _bar.Gateway = (string)value!; break;
                case nameof(StatusBarViewModel.Branch): _bar.Branch = (string)value!; break;
                case nameof(StatusBarViewModel.SessionId): _bar.SessionId = (string)value!; break;
                case nameof(StatusBarViewModel.Trailing): _bar.Trailing = (string)value!; break;
            }
        }
        finally
        {
            _writing = false;
        }
    }
}
