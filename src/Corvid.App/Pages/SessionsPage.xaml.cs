using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using Corvid.App.Controls;
using Corvid.App.Core;
using Corvid.App.Core.Composition;
using Corvid.App.Core.Markdown;
using Corvid.App.Core.Sessions;
using Corvid.App.Core.Shell;
using Corvid.App.Shell;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.System;
using Windows.UI.Core;

namespace Corvid.App.Pages;

/// <summary>
/// The Sessions area's page (Main.png, Approval.png): the conversation of the session the rail selected, and its dock
/// panels in the shell's dock. The view model does the work; this class owns what only a view can: keys, focus and the
/// scroll position.
/// <para>
/// Keys (docs/plan/ui.md §6): in the composer Enter sends (or queues while a turn runs), Shift+Enter breaks the line,
/// Ctrl+Enter steers, Esc interrupts a running turn, Esc Esc on an empty composer opens Rewind, Up on an empty composer
/// takes the last queued message back, Shift+Tab cycles the approval mode and Ctrl+S puts the draft aside. While an
/// approval card is open the first Enter focuses it; then 1 to 5 answer it, the arrows move between its answers and Esc
/// goes back to the composer. Ctrl+1 to Ctrl+7 pick the dock's tabs; Ctrl+O, Ctrl+T and Ctrl+Shift+D expand the latest
/// output, the thoughts and every checkpoint's chips.
/// </para>
/// <para>
/// Scrolling: a session opens at its latest message. The transcript follows new content only while it is scrolled to
/// the bottom; scrolled up, streaming never moves it, and a "jump to bottom" pill counts what arrived.
/// </para>
/// </summary>
public sealed partial class SessionsPage : Page, IDockProvider
{
    private static readonly TimeSpan DoubleEscape = TimeSpan.FromMilliseconds(600);

    // Made with the page: the frame raises Navigated, where the shell takes the dock, before OnNavigatedTo gives the page
    // its view model, so the dock gets the view model there.
    private readonly SessionsDock _dock = new();
    private readonly Storyboard _spinner;
    private AppComposition? _composition;
    private SessionViewModel? _session;
    private bool _follow = true;
    private bool _positioned;
    private int _newCount;
    private DateTimeOffset _lastEscape;
    private bool _swallowLineBreak;
    private bool _spinning;

    public SessionsPage()
    {
        InitializeComponent();
        _spinner = Spinner();
        Composer.AddHandler(PreviewKeyDownEvent, new KeyEventHandler(OnComposerKeyDown), true);
        Composer.BeforeTextChanging += OnComposerTextChanging;
        Root.KeyDown += OnRootKeyDown;
        for (var i = 0; i < 7; i++)
        {
            var index = i;
            AddKey(VirtualKey.Number1 + i, VirtualKeyModifiers.Control, () => SelectPanel(index));
        }

        AddKey(VirtualKey.O, VirtualKeyModifiers.Control, () => _session?.ExpandLatestOutputCommand.Execute(null));
        AddKey(VirtualKey.T, VirtualKeyModifiers.Control, () => _session?.ToggleThoughtsCommand.Execute(null));
        AddKey(VirtualKey.D, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, () => _session?.ToggleDecisionsCommand.Execute(null));
        // OnNavigatedFrom detaches; a page moved out of and back into the tree re-attaches here.
        Loaded += (_, _) => { if (_composition is not null) Attach(ViewModel.Session); };
        Unloaded += (_, _) => Detach();
    }

    public SessionsViewModel ViewModel { get; private set; } = null!;

    public UIElement? Dock => _dock;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _composition = (AppComposition)e.Parameter;
        ViewModel = _composition.Sessions;
        _dock.ViewModel = ViewModel;
        ViewModel.PropertyChanged += OnViewModelChanged;
        ViewModel.RevealRequested += OnRevealRequested;
        _composition.Shell.PropertyChanged += OnShellChanged;
        ApplyLayout();
        Bindings.Update();
        Attach(ViewModel.Session);
        base.OnNavigatedTo(e);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.PropertyChanged -= OnViewModelChanged;
        ViewModel.RevealRequested -= OnRevealRequested;
        if (_composition is not null)
            _composition.Shell.PropertyChanged -= OnShellChanged;
        Detach();
        base.OnNavigatedFrom(e);
    }

    // ---- x:Bind helpers ----

    /// <summary>A turn's first block sits 14 px below the one before (the transcript's gap) rather than 10 (the turn's).</summary>
    public static Thickness Gap(bool startsTurn) => startsTurn ? new Thickness(0, 4, 0, 0) : default;

    /// <summary>A sure chip's solid border; an unsure one draws a dashed outline instead.</summary>
    public static Thickness SolidBorder(bool unsure) => unsure ? default : new Thickness(1);

    public static string Prefixed(string prefix, string? text) => string.IsNullOrEmpty(text) ? "" : prefix + text;

    public static string Dotted(string? text) => string.IsNullOrEmpty(text) ? "" : "· " + text;

    public static Tone TaintTone(bool cleared) => cleared ? Tone.Grey : Tone.Amber;

    public static Tone RewindTone(bool refused) => refused ? Tone.Red : Tone.Grey;

    public static Tone DecisionTone(bool allow) => allow ? Tone.Green : Tone.Red;

    /// <summary>The card title: the tool's name, its target in monospace, and the result's few words after a dot.</summary>
    public static IReadOnlyList<MarkdownRun> ToolTitle(string name, string target, string summary)
    {
        var runs = new List<MarkdownRun> { new(name) };
        if (target.Length > 0)
        {
            runs.Add(new MarkdownRun(" "));
            runs.Add(new MarkdownRun(target, InlineStyle.Code));
        }

        if (summary.Length > 0)
            runs.Add(new MarkdownRun($" · {summary}"));
        return runs;
    }

    public static Visibility ChevronVisible(bool canExpand, bool running) => canExpand && !running ? Visibility.Visible : Visibility.Collapsed;

    public static string ExpandName(bool expanded) => expanded ? "Collapse output" : "Expand output";

    /// <summary>The accent outline (.opt.focus): the answer Enter chooses, or the one whose preview is open.</summary>
    public static Visibility OutlineVisible(bool focused, bool expanded) => focused || expanded ? Visibility.Visible : Visibility.Collapsed;

    public static string ThoughtName(string label, bool expanded) => expanded ? $"{label}, expanded" : $"{label}, collapsed";

    public static string RecordLabel(bool expanded) => expanded ? "hide the record" : "show the record";

    public static Visibility Both(bool first, bool second) => first && second ? Visibility.Visible : Visibility.Collapsed;

    public static bool Not(bool value) => !value;

    /// <summary>The parsed commands side by side, or stacked under 860 px as Approval-narrow.png draws them.</summary>
    public static int ParsedColumns(bool narrow) => narrow ? 1 : 2;

    /// <summary>The phone's shorter words where it has them ("Approval 1 of 2", "4:12 left").</summary>
    public static string Pick(bool phone, string phoneText, string text) => phone ? phoneText : text;


    /// <summary>A command's table as aligned monospace columns.</summary>
    public static string Table(IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        if (columns.Count == 0)
            return "";
        var widths = columns.Select((column, i) => Math.Max(column.Length, rows.Count == 0 ? 0 : rows.Max(row => i < row.Count ? row[i].Length : 0))).ToList();
        string Line(IReadOnlyList<string> cells) => string.Join("  ", widths.Select((width, i) => (i < cells.Count ? cells[i] : "").PadRight(width))).TrimEnd();
        return string.Join("\n", new[] { Line(columns) }.Concat(rows.Select(Line)));
    }

    // ---- Width ----

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.Layout))
            ApplyLayout();
    }

    /// <summary>Under 860 px the gutters are 14 px (Main-narrow.png); the phone drops the header and the key hint.</summary>
    private void ApplyLayout()
    {
        var layout = _composition?.Shell.Layout ?? ShellLayout.Wide;
        SessionsLayout.Current.Layout = layout;
        var narrow = SessionsLayout.Current.IsNarrow;
        var side = narrow ? 14 : 24;
        TranscriptBody.Padding = new Thickness(side, narrow ? 12 : 16, side, 8);
        TurnStrip.Padding = new Thickness(side, 6, side, 6);
        ComposerArea.Padding = new Thickness(side, 10, side, 12);
        Header.Padding = new Thickness(narrow ? 14 : 20, 10, narrow ? 14 : 20, 10);
        // Set here, not bound: the page's own x:Bind paths do not follow a static source's changes.
        var phone = SessionsLayout.Current.IsPhone;
        Header.Visibility = phone ? Visibility.Collapsed : Visibility.Visible;
        HintHost.Visibility = phone ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---- The session ----

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SessionsViewModel.Session))
            Attach(ViewModel.Session);
    }

    private void Attach(SessionViewModel? session)
    {
        if (ReferenceEquals(session, _session))
            return;
        Detach();
        _session = session;
        _positioned = false;
        _follow = true;
        _newCount = 0;
        if (_session is null)
        {
            UpdateJump();
            return;
        }

        _session.PropertyChanged += OnSessionChanged;
        _session.FocusRequested += OnFocusRequested;
        _session.Blocks.CollectionChanged += OnBlocksChanged;
        UpdateSpinner();
        UpdateJump();
        WatchApproval();
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, PositionAtLatest);
    }

    private void Detach()
    {
        if (_session is null)
            return;
        _session.PropertyChanged -= OnSessionChanged;
        _session.FocusRequested -= OnFocusRequested;
        _session.Blocks.CollectionChanged -= OnBlocksChanged;
        _session = null;
        WatchApproval();
        _spinner.Stop();
        _spinning = false;
    }

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SessionViewModel.IsWorking) or nameof(SessionViewModel.HasApproval) or null or "")
        {
            UpdateSpinner();
            UpdateJump();
        }

        if (e.PropertyName is nameof(SessionViewModel.CurrentApproval) or null or "")
            WatchApproval();
    }

    private void OnFocusRequested(object? sender, SessionFocusTarget target)
    {
        if (target == SessionFocusTarget.Composer)
        {
            Composer.Focus(FocusState.Programmatic);
            // With the caret after the draft, so typing goes on from it (a prompt put back from the history).
            Composer.SelectionStart = Composer.Text.Length;
        }
        else
            FocusApproval();
    }

    /// <summary>Gives the keys to the open card's focused answer, bringing the card into view.</summary>
    private void FocusApproval()
    {
        if (_session?.CurrentApproval is not { } card)
            return;
        var index = _session.Blocks.IndexOf(card);
        if (index < 0)
            return;
        var element = Transcript.GetOrCreateElement(index);
        element.UpdateLayout();
        FocusOption(element, card);
    }

    /// <summary>Focuses the card's focused answer and scrolls it into view: the answers, not the card's top, are what the keys act on.</summary>
    private static void FocusOption(UIElement cardElement, ApprovalCardBlock card)
    {
        if (card.FocusedOption is not { } option)
            return;
        var button = Descendants(cardElement).OfType<Button>()
            .FirstOrDefault(b => ReferenceEquals(b.DataContext, option) && b.Visibility == Visibility.Visible && b.ActualHeight > 0);
        if (button is null)
            return;
        button.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0.5, AnimationDesired = false });
        button.Focus(FocusState.Keyboard);
    }

    // ---- The open card's reason field (5: deny and say why) ----

    private ApprovalCardBlock? _watched;

    private void WatchApproval()
    {
        if (_watched is not null)
            _watched.PropertyChanged -= OnApprovalChanged;
        _watched = _session?.CurrentApproval;
        if (_watched is not null)
            _watched.PropertyChanged += OnApprovalChanged;
    }

    /// <summary>"Deny and say why" opens the reason field and puts the caret in it.</summary>
    private void OnApprovalChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ApprovalCardBlock.IsAskingWhy) || sender is not ApprovalCardBlock { IsAskingWhy: true } card || _session is null)
            return;
        var index = _session.Blocks.IndexOf(card);
        if (index < 0)
            return;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            var reason = Descendants(Transcript.GetOrCreateElement(index)).OfType<TextBox>()
                .FirstOrDefault(box => AutomationProperties.GetName(box) == "Reason for the denial");
            reason?.Focus(FocusState.Keyboard);
        });
    }

    // ---- Keys ----

    private void OnComposerKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_session is not { } session)
            return;
        var ctrl = IsDown(VirtualKey.Control);
        var shift = IsDown(VirtualKey.Shift);
        switch (e.Key)
        {
            case VirtualKey.Enter when shift && !ctrl:
                return;
            case VirtualKey.Enter:
                e.Handled = true;
                SwallowLineBreak();
                if (ctrl)
                    session.SteerCommand.Execute(null);
                else
                    session.SubmitCommand.Execute(null);
                return;
            case VirtualKey.Escape:
                e.Handled = true;
                Escape(session);
                return;
            case VirtualKey.Up when session.Draft.Length == 0 && session.HasQueue:
                e.Handled = true;
                session.TakeBackLastCommand.Execute(null);
                // The message comes back to be edited: the caret goes after it.
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => Composer.SelectionStart = Composer.Text.Length);
                return;
            case VirtualKey.Tab when shift && !ctrl:
                e.Handled = true;
                _composition?.StatusBar.CycleModeCommand.Execute(null);
                return;
            case VirtualKey.S when ctrl:
                e.Handled = true;
                session.StashDraftCommand.Execute(null);
                return;
        }
    }

    /// <summary>
    /// The text box still types the line break of an Enter the page handled (its text input does not wait for the key
    /// event), so the next change that adds one is cancelled; Shift+Enter is never marked.
    /// </summary>
    private void SwallowLineBreak()
    {
        _swallowLineBreak = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => _swallowLineBreak = false);
    }

    private void OnComposerTextChanging(TextBox sender, TextBoxBeforeTextChangingEventArgs args)
    {
        if (_swallowLineBreak && LineBreaks(args.NewText) > LineBreaks(sender.Text))
        {
            args.Cancel = true;
            _swallowLineBreak = false;
        }
    }

    private static int LineBreaks(string text) => text.Count(c => c is '\r' or '\n');

    /// <summary>Esc interrupts a running turn; on an idle, empty composer a second Esc opens the rewind timeline.</summary>
    private void Escape(SessionViewModel session)
    {
        if (_composition?.Shell.ShowScrim == true)
        {
            _composition.Shell.CloseOverlaysCommand.Execute(null);
            return;
        }

        if (session.IsRunning && !session.HasApproval)
        {
            session.InterruptCommand.Execute(null);
            _lastEscape = default;
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (session.Draft.Length == 0 && now - _lastEscape < DoubleEscape)
        {
            _lastEscape = default;
            ViewModel.OpenRewindCommand.Execute(null);
            return;
        }

        _lastEscape = now;
    }

    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape && _session is { } session && !e.Handled)
        {
            e.Handled = true;
            Escape(session);
        }
    }

    /// <summary>Digits answer the focused card, the arrows move between its answers, Esc goes back to the composer.</summary>
    private void OnApprovalKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not ApprovalCardBlock card || e.OriginalSource is TextBox)
            return;
        var digit = e.Key switch
        {
            >= VirtualKey.Number1 and <= VirtualKey.Number5 => (int)(e.Key - VirtualKey.Number1) + 1,
            >= VirtualKey.NumberPad1 and <= VirtualKey.NumberPad5 => (int)(e.Key - VirtualKey.NumberPad1) + 1,
            _ => 0,
        };
        if (digit > 0)
        {
            var key = digit.ToString(CultureInfo.InvariantCulture);
            if (card.ChooseCommand.CanExecute(key))
                card.ChooseCommand.Execute(key);
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case VirtualKey.Left or VirtualKey.Right:
                card.MoveFocus(e.Key == VirtualKey.Left ? -1 : 1);
                FocusOption((UIElement)sender, card);
                e.Handled = true;
                break;
            case VirtualKey.Escape:
                Composer.Focus(FocusState.Keyboard);
                e.Handled = true;
                break;
        }
    }

    private void SelectPanel(int index)
    {
        if (index < ViewModel.Panels.Count)
            ViewModel.SelectPanel(ViewModel.Panels[index]);
    }

    private void AddKey(VirtualKey key, VirtualKeyModifiers modifiers, Action action)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        accelerator.Invoked += (_, args) =>
        {
            action();
            args.Handled = true;
        };
        KeyboardAccelerators.Add(accelerator);
    }

    private static bool IsDown(VirtualKey key) =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    // ---- Scrolling ----

    /// <summary>Opens at the latest message: its top under the header, as the mockups show a running turn.</summary>
    private void PositionAtLatest()
    {
        if (_session is not { } session || _positioned)
            return;
        _positioned = true;
        // A find (Ctrl+F) that opened the page lands on its block rather than on the latest message.
        var index = RevealIndex(session);
        for (var i = session.Blocks.Count - 1; i >= 0 && index < 0; i--)
        {
            if (session.Blocks[i].Kind == TranscriptBlockKind.User)
            {
                index = i;
                break;
            }
        }

        if (index < 0)
        {
            ScrollToBottom();
            return;
        }

        ScrollToBlock(index);
        // The repeater estimates the heights of blocks it has not drawn; once the ones around the target are drawn the
        // estimate can move it, so the position is taken again after that layout.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (_session is not null && index < _session.Blocks.Count)
                ScrollToBlock(index);
        });
    }

    /// <summary>The header's Aside: the same surface as Ctrl+;, which says what asides come with.</summary>
    private void OnAside(object sender, RoutedEventArgs e) => _composition?.Palette.Execute(Corvid.App.Core.Keys.KeyCommands.Aside);

    /// <summary>The index of the block find-in-session asked for, taken once; -1 when none was asked or it is gone.</summary>
    private int RevealIndex(SessionViewModel session)
    {
        if (ViewModel.TakeReveal() is not { } id)
            return -1;
        for (var i = 0; i < session.Blocks.Count; i++)
        {
            if (session.Blocks[i].Id == id)
                return i;
        }

        return -1;
    }

    /// <summary>Find chose a block while the page shows: scroll to it now (and once more after the repeater's layout).</summary>
    private void OnRevealRequested(object? sender, EventArgs e)
    {
        if (!_positioned || _session is not { } session)
            return;
        var index = RevealIndex(session);
        if (index < 0)
            return;
        ScrollToBlock(index);
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (_session is not null && index < _session.Blocks.Count)
                ScrollToBlock(index);
        });
    }

    private void ScrollToBlock(int index)
    {
        var element = Transcript.GetOrCreateElement(index);
        TranscriptBody.UpdateLayout();
        var top = element.TransformToVisual(TranscriptBody).TransformPoint(default).Y - TranscriptBody.Padding.Top;
        TranscriptScroll.ChangeView(null, Math.Max(0, top), null, true);
        TranscriptScroll.UpdateLayout();
        _follow = TranscriptScroll.VerticalOffset >= TranscriptScroll.ScrollableHeight - 4;
        UpdateJump();
    }

    private void OnTranscriptSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_follow && _positioned)
            ScrollToBottom();
    }

    private void OnTranscriptViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (e.IsIntermediate || !_positioned)
            return;
        _follow = TranscriptScroll.VerticalOffset >= TranscriptScroll.ScrollableHeight - 4;
        if (_follow)
            _newCount = 0;
        UpdateJump();
    }

    private void OnBlocksChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && !_follow)
        {
            _newCount += e.NewItems?.Count ?? 1;
            UpdateJump();
        }
    }

    private void OnJumpToBottom(object sender, RoutedEventArgs e)
    {
        _follow = true;
        _newCount = 0;
        ScrollToBottom();
        UpdateJump();
    }

    private void ScrollToBottom() => TranscriptScroll.ChangeView(null, TranscriptScroll.ExtentHeight, null, true);

    private void UpdateJump()
    {
        // While an approval waits, the pill stays away: it would sit over the card's answers.
        var show = _session is not null && !_follow && !_session.HasApproval && (_newCount > 0 || _session.IsWorking);
        JumpButton.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        JumpText.Text = _newCount > 0 ? $"Jump to bottom · {_newCount} new" : "Jump to bottom";
        AutomationProperties.SetName(JumpButton, JumpText.Text);
    }

    // ---- The rest ----

    /// <summary>The title gives way to the fork pill (CSS's flex shrink): it is cut with an ellipsis, never the pill.</summary>
    private void OnHeaderSizeChanged(object sender, SizeChangedEventArgs e) => FitTitle();

    private void FitTitle()
    {
        // The room left of the actions, less the pill at its natural width.
        var room = HeaderGrid.ActualWidth - HeaderActions.ActualWidth - HeaderGrid.ColumnSpacing;
        var pill = ForkPill.Visibility == Visibility.Visible ? ForkPill.ActualWidth + TitleRow.Spacing : 0;
        var width = Math.Max(40, room - pill);
        if (Math.Abs(TitleText.MaxWidth - width) > 0.5)
            TitleText.MaxWidth = width;
    }

    private void OnBranchFromHere(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is UserMessageBlock block)
            _session?.ForkCommand.Execute(block);
    }

    private void OnCompact(object sender, RoutedEventArgs e)
    {
        if (_session is { } session && session.CompactCommand.CanExecute(CompactFocus.Text))
        {
            session.CompactCommand.Execute(CompactFocus.Text);
            CompactFocus.Text = "";
            CompactFlyout.Hide();
        }
    }

    private Storyboard Spinner()
    {
        var animation = new DoubleAnimation { From = 0, To = 360, Duration = new Duration(TimeSpan.FromSeconds(0.9)), RepeatBehavior = RepeatBehavior.Forever };
        Storyboard.SetTarget(animation, SpinnerRotation);
        Storyboard.SetTargetProperty(animation, "Angle");
        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        return storyboard;
    }

    /// <summary>The spinner turns only while a turn works, so an idle window draws nothing.</summary>
    private void UpdateSpinner()
    {
        var spin = _session?.IsWorking == true;
        if (spin == _spinning)
            return;
        _spinning = spin;
        if (spin)
            _spinner.Begin();
        else
            _spinner.Stop();
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var descendant in Descendants(child))
                yield return descendant;
        }
    }
}
