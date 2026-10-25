using System.Collections.ObjectModel;
using Corvid.App.Core.Board;
using Corvid.App.Core.Mvvm;

namespace Corvid.App.Core.Sessions;

/// <summary>
/// A5 (plan §14, docs/plan/ui.md §4.13): the header's child sessions. A parent lists its children (goal, status, toolset,
/// budget, model mix, answer), each opening read-only and a running one stoppable; a child links its parent and says it is
/// read-only.
/// </summary>
public sealed partial class SessionViewModel
{
    private RelayCommand? _openParent;

    /// <summary>
    /// Opens another session by id, also one the rail does not list (a read-only child, its parent): the hub's
    /// <see cref="SessionHub.Show"/>. Null opens only listed sessions.
    /// </summary>
    public Action<string>? ShowSession { get; init; }

    private void OpenRelated(string sessionId)
    {
        if (ShowSession is { } show)
            show(sessionId);
        else
            OpenOtherSession(sessionId);
    }

    /// <summary>The session's children, oldest first.</summary>
    public ObservableCollection<ChildRowViewModel> Children { get; } = [];

    public bool HasChildren => Children.Count > 0;

    /// <summary>"2 children · 1 running".</summary>
    public string ChildrenText
    {
        get
        {
            var running = Children.Count(row => row.IsRunning);
            return $"{Children.Count} {(Children.Count == 1 ? "child" : "children")}" + (running > 0 ? $" · {running} running" : "");
        }
    }

    /// <summary>"child of cli-8c11"; empty for a session that is not a child.</summary>
    public string ParentText => _state.ParentId is { } parent ? $"child of {parent}" : "";

    public bool HasParent => _state.ParentId is not null;

    /// <summary>What this child was asked, for the header's tooltip.</summary>
    public string ChildGoal => _state.ChildGoal ?? "";

    public RelayCommand OpenParentCommand => _openParent ??= new RelayCommand(() =>
    {
        if (_state.ParentId is { } parent)
            OpenRelated(parent);
    }, () => _state.ParentId is not null);

    private void RaiseChildren(SessionView previous)
    {
        if (previous.ParentId != _state.ParentId || previous.ChildGoal != _state.ChildGoal)
        {
            OnPropertyChanged(nameof(ParentText));
            OnPropertyChanged(nameof(HasParent));
            OnPropertyChanged(nameof(ChildGoal));
            OpenParentCommand.NotifyCanExecuteChanged();
        }
        if (ReferenceEquals(previous.Children, _state.Children))
            return;
        var children = _state.Children;
        for (var i = 0; i < children.Count; i++)
        {
            if (i < Children.Count && Children[i].Id == children[i].ChildId)
            {
                if (Children[i].Child != children[i])
                    Children[i].Update(children[i]);
                continue;
            }
            Children.Insert(i, new ChildRowViewModel(children[i], OpenRelated, id => _service.StopChildAsync(id)));
        }
        while (Children.Count > children.Count)
            Children.RemoveAt(Children.Count - 1);
        OnPropertyChanged(nameof(HasChildren));
        OnPropertyChanged(nameof(ChildrenText));
    }
}
