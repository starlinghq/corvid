using Corvid.App.Core.Mvvm;

namespace Corvid.App.Core.Sessions;

/// <summary>
/// The session's records (A4): its draft kept in <c>ui.db</c> across restarts and shared with the CLI (G-7), its pin, and a
/// recap on request (G-6). Renaming is <see cref="RenameCommand"/>.
/// </summary>
public sealed partial class SessionViewModel
{
    /// <summary>How long the composer is still before its draft is written to <c>ui.db</c>: never once per keystroke.</summary>
    public static readonly TimeSpan DraftSaveDelay = TimeSpan.FromMilliseconds(750);

    private ITimer? _draftTimer;
    private bool _draftsReady;
    private bool _draftDirty;
    private bool _pinned;
    private RelayCommand? _togglePinCommand;
    private RelayCommand? _recapCommand;

    /// <summary>Whether the rail keeps this session at its top (the hub sets it from the session's summary).</summary>
    public bool IsPinned
    {
        get => _pinned;
        private set
        {
            if (SetProperty(ref _pinned, value))
                OnPropertyChanged(nameof(PinLabel));
        }
    }

    /// <summary>The header menu's pin item: "Pin to the rail" or "Unpin".</summary>
    public string PinLabel => _pinned ? "Unpin from the rail" : "Pin to the rail";

    /// <summary>Whether this service keeps pins (the engine does, in <c>ui.db</c>).</summary>
    public bool CanPin => _service.CanPin;

    /// <summary>Whether this service writes recaps (the engine does, with its local generator).</summary>
    public bool CanRecap => _service.CanRecap;

    /// <summary>Pins or unpins the session; the rail follows when the service reports the change.</summary>
    public RelayCommand TogglePinCommand => _togglePinCommand ??= new RelayCommand(
        () => Run(() => _service.SetPinnedAsync(SessionId, !_pinned)), () => _service.CanPin);

    /// <summary>A recap of what happened since the last one, written by the local generator (G-6); it arrives as a notice.</summary>
    public RelayCommand RecapCommand => _recapCommand ??= new RelayCommand(() => Run(() => _service.RecapAsync(SessionId)), () => _service.CanRecap);

    /// <summary>The summary's pin, from the hub.</summary>
    internal void SetPinned(bool pinned) => IsPinned = pinned;

    /// <summary>
    /// After the session opened: the kept draft goes into an editor the person has not typed in yet, and only from then on
    /// are changes written back, so opening never overwrites what <c>ui.db</c> holds.
    /// </summary>
    private async Task LoadDraftAsync()
    {
        try
        {
            if (await _service.LoadDraftAsync(SessionId).ConfigureAwait(true) is { IsEmpty: false } kept && !_disposed
                && _draft.Length == 0 && _stash is null)
            {
                Draft = kept.Text;
                StashDraft(kept.Stash ?? "");
                // What ui.db holds already: nothing to write back.
                _draftDirty = false;
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Error = $"The kept draft could not be read: {exception.Message}";
        }
        finally
        {
            _draftsReady = true;
        }
        // Typed while it loaded: written now.
        if (_draftDirty)
            ScheduleDraftSave();
    }

    /// <summary>The draft or the stash changed: written once the composer has been still for <see cref="DraftSaveDelay"/>.</summary>
    private void DraftChanged()
    {
        _draftDirty = true;
        if (_draftsReady)
            ScheduleDraftSave();
    }

    private void ScheduleDraftSave()
    {
        if (_disposed)
            return;
        if (_draftTimer is null)
            _draftTimer = _time.CreateTimer(_ => _dispatcher.Post(SaveDraft), null, DraftSaveDelay, Timeout.InfiniteTimeSpan);
        else
            _draftTimer.Change(DraftSaveDelay, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Writes the draft now when it changed since the last write (the timer, and closing the window).</summary>
    internal void SaveDraft()
    {
        if (!_draftDirty || !_draftsReady)
            return;
        _draftDirty = false;
        var draft = new SessionDraft(_draft, _stash);
        if (_disposed)
        {
            // Closing: nobody is left to show a failure, which the service reports itself.
            _service.SaveDraftAsync(SessionId, draft).ContinueWith(task => _ = task.Exception, TaskScheduler.Default);
            return;
        }
        Run(() => _service.SaveDraftAsync(SessionId, draft));
    }

    private void DisposeRecords()
    {
        _draftTimer?.Dispose();
        _draftTimer = null;
        SaveDraft();
    }
}
