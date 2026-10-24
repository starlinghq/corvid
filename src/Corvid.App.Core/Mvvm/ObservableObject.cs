using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Corvid.App.Core.Mvvm;

/// <summary>
/// The base of every view model: raises <see cref="PropertyChanged"/> when a value changes, so <c>x:Bind</c> with
/// <c>Mode=OneWay</c> redraws exactly what changed. Hand-written rather than taken from a toolkit, because this project
/// takes no package but Markdig (docs/plan/ui.md §8). Changes are made on the UI thread; a service that hears from the
/// engine on another thread posts them through <see cref="IDispatcher"/>.
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Stores the value and raises the event when it differs from the field; returns whether it did.</summary>
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    /// <summary>Raises the event for a property, including a computed one whose inputs just changed.</summary>
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
