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
    // A4 verification: the Enter that focused a card, held or followed by a digit, never answers it.
    private readonly CardKeyGate _cardKeys = new(TimeProvider.System);
    private readonly Storyboard _spinner;
    private AppComposition? _composition;
    private SessionViewModel? _session;
    private bool _follow = true;
    private bool _positioned;
    private int _newCount;
    private DateTimeOffset _lastEscape;
    private bool _swallowLineBreak;
    // Whether Ctrl is down as the page's own key events say (A4V2-1).
    private bool _ctrlHeld;
    private bool _spinning;

    public SessionsPage()
    {
        InitializeComponent();
        _spinner = Spinner();
        Composer.AddHandler(PreviewKeyDownEvent, new KeyEventHandler(OnComposerKeyDown), true);
        // A4V2-1: Ctrl as the page's own key events see it, before the composer or a card reads Enter.
        Root.AddHandler(PreviewKeyDownEvent, new KeyEventHandler((_, e) => TrackCtrl(e.Key, down: true)), true);
        Root.AddHandler(PreviewKeyUpEvent, new KeyEventHandler((_, e) => TrackCtrl(e.Key, down: false)), true);
        Root.LostFocus += (_, _) => _ctrlHeld = false;
        Root.AddHandler(PreviewKeyDownEvent, new KeyEventHandler(OnCardPreviewKeyDown), true);
        Composer.BeforeTextChanging += OnComposerTextChanging;
        // A5: with no session open while a new one starts, what is typed is kept for it.
        Composer.TextChanged += (_, _) =>
        {
            if (_session is null && ViewModel?.IsStartingSession == true)
                ViewModel.StartingDraft = Composer.Text;
        };
        Root.KeyDown += OnRootKeyDown;
        // A collapsed pill raises no SizeChanged, so the title would keep the narrow room it had beside it (seen going from a
        // fork, its pill and children shown, back to its source: the title stayed cut to a word). Fit again once laid out.
        foreach (var beside in new FrameworkElement[] { ForkPill, ModelsPill, ParentLink, ChildrenButton })
            beside.RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => DispatcherQueue.TryEnqueue(FitTitle));
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
        UpdateStarting();
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

    public static string Suffixed(string? text, string suffix) => string.IsNullOrEmpty(text) ? "" : text + suffix;

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

    /// <summary>The card's head: 8px 12px on the phone, 10px 14px otherwise.</summary>
    public static Thickness CardHeadPadding(bool phone) => phone ? new Thickness(12, 8, 12, 8) : new Thickness(14, 10, 14, 10);

    /// <summary>The card's body: 10px 12px with 8 between its parts on the phone, 12px 14px with 10 otherwise.</summary>
    public static Thickness CardBodyPadding(bool phone) => phone ? new Thickness(12, 10, 12, 10) : new Thickness(14, 12, 14, 12);

    public static double CardSpacing(bool phone) => phone ? 8 : 10;

    /// <summary>A list shown at one width class only, and only when it has items.</summary>
    public static Visibility WhenAny(bool phone, bool phoneOnly, System.Collections.IEnumerable? items) =>
        phone == phoneOnly ? Infrastructure.Ui.VisibleIfAny(items) : Visibility.Collapsed;

    /// <summary>The next prompt in the queue: the long line on a desktop, the short one on the phone.</summary>
    public static Visibility NextVisible(bool hasNext, bool phone, bool phoneLine) =>
        hasNext && phone == phoneLine ? Visibility.Visible : Visibility.Collapsed;


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

    /// <summary>
    /// Under 860 px the gutters are 14 px (Main-narrow.png). The phone (Phone.png) drops the header and the key hint, keeps
    /// the turn strip only for a queue, and has a one-line composer with a send button and no bar under it.
    /// </summary>
    private void ApplyLayout()
    {
        var layout = _composition?.Shell.Layout ?? ShellLayout.Wide;
        var wasPhone = SessionsLayout.Current.IsPhone;
        var wasNarrow = SessionsLayout.Current.IsNarrow;
        SessionsLayout.Current.Layout = layout;
        var narrow = SessionsLayout.Current.IsNarrow;
        var phone = SessionsLayout.Current.IsPhone;
        var side = narrow ? 14 : 24;
        TranscriptBody.Padding = new Thickness(side, narrow ? 12 : 16, side, 8);
        TurnStrip.Padding = new Thickness(side, 6, side, 6);
        ComposerArea.Padding = phone ? new Thickness(12, 8, 12, 8) : new Thickness(side, 10, side, 12);
        Header.Padding = new Thickness(narrow ? 14 : 20, 10, narrow ? 14 : 20, 10);
        // Set here, not bound: the page's own x:Bind paths do not follow a static source's changes.
        Header.Visibility = phone ? Visibility.Collapsed : Visibility.Visible;
        HintHost.Visibility = phone ? Visibility.Collapsed : Visibility.Visible;
        ComposerBar.Visibility = phone ? Visibility.Collapsed : Visibility.Visible;
        PhoneSendButton.Visibility = phone ? Visibility.Visible : Visibility.Collapsed;
        Composer.MinHeight = phone ? 44 : 60;
        Composer.MaxHeight = phone ? 120 : 220;
        Composer.Padding = phone ? new Thickness(12, 11, 12, 11) : new Thickness(12, 10, 12, 10);
        Composer.PlaceholderText = phone ? PhonePlaceholder : WidePlaceholder;
        UpdateTurnStrip();

        // The templates read the width class when they are made: Uno does not re-evaluate their static-rooted x:Bind paths
        // when it changes (the window starts at one width and is resized), so the transcript is made again, once. The
        // repeater would take its old elements back from the templates' recycle pools, bound as before, so the pools are
        // replaced with empty ones first.
        if ((phone != wasPhone || narrow != wasNarrow) && Transcript.ItemsSource is { } blocks)
        {
            // The repeater keeps the element that holds focus rather than letting it go, so focus leaves the transcript
            // first and comes back to the card's answer once the new elements exist.
            var focusInside = XamlRoot is { } root && FocusManager.GetFocusedElement(root) is DependencyObject focused && IsWithin(focused, Transcript);
            var refocusCard = focusInside && _session?.CurrentApproval is { IsFocused: true };
            if (focusInside)
                Composer.Focus(FocusState.Programmatic);
            Transcript.ItemsSource = null;
            // The repeater lets its elements go in its next layout pass: run it now, so they reach the old pools.
            Transcript.UpdateLayout();
            if (Resources["BlockTemplates"] is TranscriptTemplateSelector selector)
            {
                foreach (var template in selector.Templates)
                    RecyclePool.SetPoolInstance(template, new RecyclePool());
            }

            Transcript.ItemsSource = blocks;
            _positioned = false;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                // An element the repeater kept anyway (it can hold on to one it had focused) is bound again: a template's
                // x:Bind reads every path, the width class too, when its data context is set.
                for (var i = 0; i < VisualTreeHelper.GetChildrenCount(Transcript); i++)
                {
                    if (VisualTreeHelper.GetChild(Transcript, i) is FrameworkElement { DataContext: { } data } element)
                    {
                        element.DataContext = null;
                        element.DataContext = data;
                    }
                }

                PositionAtLatest();
                // After the rebound card has drawn its answers for the new width.
                if (refocusCard)
                    DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, FocusApproval);
            });
        }
    }

    private static bool IsWithin(DependencyObject element, DependencyObject ancestor)
    {
        for (var current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, ancestor))
                return true;
        }

        return false;
    }

    /// <summary>Phone.png's composer: the short placeholder, the prefixes being for a keyboard.</summary>
    private const string PhonePlaceholder = "Ask Corvid…";

    /// <summary>The composer's placeholder at desktop widths: the open session's, or the service's with none open.</summary>
    private string WidePlaceholder => _session?.Placeholder
        ?? (_composition?.SessionService.HasComposerModes == false
            ? SessionViewModel.ComposerPlaceholderWithoutModes
            : SessionViewModel.ComposerPlaceholder);

    /// <summary>
    /// The turn strip shows while a turn runs, waits or has a queue. On the phone it shows while the turn works or has a
    /// queue, but not while it waits on an answer (Phone.png: the open card says so and takes the room). The phone has
    /// no status bar, so without the strip nothing would say a turn is running, or for how long.
    /// </summary>
    private void UpdateTurnStrip()
    {
        var show = _session is { ShowTurnStrip: true } session && (!SessionsLayout.Current.IsPhone || session.HasQueue || session.IsWorking);
        TurnStrip.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// A read-only session (a job's, one another window drives) asks nothing, compacts nothing and is not rewound from
    /// here: the header's Aside, Rewind and Compact… draw disabled and say why.
    /// </summary>
    private void UpdateReadOnly()
    {
        var readOnly = _session is { IsReadOnly: true };
        foreach (var button in new Control[] { AsideButton, RewindButton, CompactButton })
        {
            button.IsEnabled = !readOnly;
            if (readOnly)
                ToolTipService.SetToolTip(button, "This session is read-only here.");
        }

        if (!readOnly)
        {
            ToolTipService.SetToolTip(AsideButton, "A side question in a scratch fork (Ctrl+;); it comes with flow F7, and this says so");
            ToolTipService.SetToolTip(RewindButton, "The session's checkpoint timeline (Esc Esc)");
            ToolTipService.SetToolTip(CompactButton, "Summarise older context now, with an optional focus");
        }
    }

    // ---- The session ----

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SessionsViewModel.Session))
        {
            // A5: opening a child (or the parent) from the header's list leaves that list behind.
            ChildrenFlyout.Hide();
            Attach(ViewModel.Session);
        }
        else if (e.PropertyName == nameof(SessionsViewModel.IsStartingSession))
            UpdateStarting();
    }

    /// <summary>
    /// A5 (A4 leftover): while a new session starts with none open (Ctrl+N as the runtime loads), the composer takes text for
    /// it, focused, and the new session opens with it as its draft.
    /// </summary>
    private void UpdateStarting()
    {
        if (_session is not null)
            return;
        Composer.IsEnabled = ViewModel.IsStartingSession;
        if (ViewModel.IsStartingSession)
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, FocusComposer);
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
        if (!SessionsLayout.Current.IsPhone)
            Composer.PlaceholderText = WidePlaceholder;
        if (_session is null)
        {
            UpdateJump();
            UpdateTurnStrip();
            UpdateReadOnly();
            ResetAnnouncements(null);
            return;
        }

        _session.PropertyChanged += OnSessionChanged;
        _session.FocusRequested += OnFocusRequested;
        _session.Blocks.CollectionChanged += OnBlocksChanged;
        ResetAnnouncements(_session);
        UpdateTurnStrip();
        UpdateReadOnly();
        UpdateSpinner();
        UpdateJump();
        WatchApproval();
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, PositionAtLatest);
        // A focus request made before this page showed the session (a new session opened from the Board).
        if (_session.TakeComposerFocusRequest())
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, FocusComposer);
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
        if (e.PropertyName is nameof(SessionViewModel.ShowTurnStrip) or nameof(SessionViewModel.HasQueue) or nameof(SessionViewModel.IsWorking) or null or "")
            UpdateTurnStrip();
        if (e.PropertyName is nameof(SessionViewModel.IsReadOnly) or null or "")
            UpdateReadOnly();
        // A5 (A4 leftover): a session's first opening positions once its history has drawn, not on the empty view before it.
        if (e.PropertyName is nameof(SessionViewModel.IsOpen) && !_positioned)
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, PositionAtLatest);
        UpdateAnnouncements(e.PropertyName);
    }

    // ---- Live regions (docs/plan/ui.md §4.15) ----

    private ApprovalCardBlock? _announcedApproval;
    private QuestionBlock? _announcedQuestion;
    private bool _wasRunning;

    /// <summary>What a screen reader hears without moving focus: the step as it changes, then an approval, a question or a finished turn.</summary>
    private void UpdateAnnouncements(string? property)
    {
        if (_session is not { } session)
            return;
        if (property is nameof(SessionViewModel.TurnStripStatus) or null or "")
            Raise(StepAnnouncer, session.TurnStripStatus);
        if (session.CurrentApproval is { } card && !ReferenceEquals(card, _announcedApproval))
            Announce($"Approval needed. {card.AutomationName}");
        _announcedApproval = session.CurrentApproval;
        if (session.PendingQuestion is { } question && !ReferenceEquals(question, _announcedQuestion))
            Announce($"Corvid asks: {question.Text}");
        _announcedQuestion = session.PendingQuestion;
        if (_wasRunning && !session.IsRunning)
            Announce("The turn finished.");
        _wasRunning = session.IsRunning;
    }

    /// <summary>Starts from the session as it is when the page opens: only what happens after is announced.</summary>
    private void ResetAnnouncements(SessionViewModel? session)
    {
        _announcedApproval = session?.CurrentApproval;
        _announcedQuestion = session?.PendingQuestion;
        _wasRunning = session?.IsRunning == true;
        StepAnnouncer.Text = session?.TurnStripStatus ?? "";
        Announcer.Text = "";
    }

    private void Announce(string text) => Raise(Announcer, text);

    private static void Raise(TextBlock region, string text)
    {
        if (region.Text == text)
            return;
        region.Text = text;
        AutomationProperties.SetName(region, text);
        Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(region)
            ?.RaiseAutomationEvent(Microsoft.UI.Xaml.Automation.Peers.AutomationEvents.LiveRegionChanged);
    }

    private void OnFocusRequested(object? sender, SessionFocusTarget target)
    {
        if (target == SessionFocusTarget.Composer)
            FocusComposer();
        else
            FocusApproval();
    }

    /// <summary>
    /// Puts the caret in the composer, after the draft so typing goes on from it (a prompt put back from the history). A
    /// request the page could not take yet (it was not loaded) stays with the session and is taken when it attaches.
    /// </summary>
    private void FocusComposer()
    {
        if (!Composer.Focus(FocusState.Programmatic))
            return;
        _session?.TakeComposerFocusRequest();
        Composer.SelectionStart = Composer.Text.Length;
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
        _cardKeys.Focused();
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
        if (IsCtrlKey(e.Key))
            return;
        var ctrl = CtrlDown;
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
                Escape(session, fromComposer: true);
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

    private static bool IsCtrlKey(VirtualKey key) => key is VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl;

    private void TrackCtrl(VirtualKey key, bool down)
    {
        if (IsCtrlKey(key))
            _ctrlHeld = down;
    }

    /// <summary>
    /// A4V2-1: Ctrl as the page's key events saw it (the same stream as the Enter being read, so in order), or the thread's
    /// key state; Uno's KeyRoutedEventArgs has no public modifiers. Focus moving within or out of the page forgets it, so a
    /// Ctrl released where the page never hears it does not stay held.
    /// </summary>
    private bool CtrlDown => _ctrlHeld || IsDown(VirtualKey.Control);

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
        else if (IsWindowAltLetter(sender.Text, args.NewText))
        {
            args.Cancel = true;
        }
    }

    /// <summary>
    /// A5: Alt+P (a window key on Alt and a letter) switches the generator without leaving the composer, but Uno still types
    /// the letter into it. One letter that a window binding on Alt names, inserted while Alt alone is held, is that key's,
    /// not text. AltGr (Ctrl with Alt) types its own characters and is left alone.
    /// </summary>
    private bool IsWindowAltLetter(string before, string after)
    {
        if (_composition is null || after.Length != before.Length + 1 || !IsDown(VirtualKey.Menu) || IsDown(VirtualKey.Control))
            return false;
        var at = 0;
        while (at < before.Length && before[at] == after[at])
            at++;
        var typed = after[at];
        if (!char.IsAsciiLetter(typed) || before[at..] != after[(at + 1)..])
            return false;
        return _composition.Keys.Find(new Corvid.App.Core.Keys.KeyGesture(Corvid.App.Core.Keys.KeyModifiers.Alt,
            char.ToUpperInvariant(typed).ToString())) is not null;
    }

    private static int LineBreaks(string text) => text.Count(c => c is '\r' or '\n');

    /// <summary>
    /// Esc interrupts a running turn; on an idle, empty composer a second Esc opens the rewind timeline. While an approval
    /// card waits, Esc in the composer interrupts too (the interrupt closes the card as cancelled), but one from elsewhere
    /// does not: Esc on the card goes back to the composer, and in the card's reason field it leaves the field.
    /// </summary>
    private void Escape(SessionViewModel session, bool fromComposer = false)
    {
        if (_composition?.Shell.ShowScrim == true)
        {
            _composition.Shell.CloseOverlaysCommand.Execute(null);
            return;
        }

        if (session.IsRunning && (fromComposer || !session.HasApproval))
        {
            session.InterruptCommand.Execute(null);
            _lastEscape = default;
            return;
        }

        var now = DateTimeOffset.UtcNow;
        // A read-only session is not rewound from here, as its header's Rewind says.
        if (session.Draft.Length == 0 && !session.IsReadOnly && now - _lastEscape < DoubleEscape)
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

    /// <summary>
    /// A4V2-1: keys on a focused approval card that are not its answers, read before its buttons see them. Typing (a letter)
    /// goes back to the composer, since a person who types after Enter pointed at the card means the composer; Space never
    /// presses an answer (a space in the words being typed approved the call); Ctrl+Enter is a steer, never an answer, so it
    /// goes back to the composer too. Digits, the arrows and Enter answer as before; the card's reason field is left alone.
    /// A4 verification: a repeated key-down never answers, and for a moment after the page moved focus to the card (the
    /// Enter that sent) an Enter answers nothing and a digit goes back to the composer as typing (<see cref="CardKeyGate"/>);
    /// an arrow or Tab arms the card at once.
    /// </summary>
    private void OnCardPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Handled || e.OriginalSource is TextBox || !InApprovalCard(e.OriginalSource as DependencyObject))
            return;
        var ctrl = CtrlDown;
        if (e.Key == VirtualKey.Space || e.Key == VirtualKey.Enter && ctrl)
        {
            e.Handled = true;
            if (e.Key == VirtualKey.Enter)
                FocusComposerAtEnd();
            return;
        }
        if (e.Key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down or VirtualKey.Tab)
            _cardKeys.Arm();
        var digit = e.Key is (>= VirtualKey.Number1 and <= VirtualKey.Number5) or (>= VirtualKey.NumberPad1 and <= VirtualKey.NumberPad5);
        if ((e.Key == VirtualKey.Enter || digit) && !IsDown(VirtualKey.Menu))
        {
            var repeated = e.KeyStatus.WasKeyDown || e.KeyStatus.RepeatCount > 1;
            switch (_cardKeys.Decide(digit, repeated))
            {
                case CardKey.Ignore:
                    e.Handled = true;
                    return;
                case CardKey.ToComposer:
                    // Handled so the card's own digit handler never answers; the digit's text reaches the composer, as a letter's does.
                    e.Handled = true;
                    FocusComposerAtEnd();
                    return;
            }
        }
        if (e.Key is >= VirtualKey.A and <= VirtualKey.Z && !ctrl && !IsDown(VirtualKey.Menu))
            FocusComposerAtEnd();
    }

    private void FocusComposerAtEnd()
    {
        Composer.Focus(FocusState.Keyboard);
        Composer.SelectionStart = Composer.Text.Length;
    }

    private static bool InApprovalCard(DependencyObject? element)
    {
        for (var node = element; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is FrameworkElement { DataContext: ApprovalCardBlock })
                return true;
        }

        return false;
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
        // A5 (A4 leftover): not before the history is in. Positioned on the empty view of a first opening (the engine still
        // replaying the log), the transcript then filled in around an offset taken from no blocks, mid-way down.
        if (_session is not { IsOpen: true } session || _positioned)
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
        // estimate can move it, so the position is taken again after each layout until it holds (a few passes at most),
        // unless the person scrolled meanwhile.
        Settle(session, index, passes: 4);
    }

    private void Settle(SessionViewModel session, int index, int passes)
    {
        var offset = TranscriptScroll.VerticalOffset;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (!ReferenceEquals(_session, session) || index >= session.Blocks.Count || Math.Abs(TranscriptScroll.VerticalOffset - offset) > 1)
                return;
            ScrollToBlock(index);
            if (passes > 1 && Math.Abs(TranscriptScroll.VerticalOffset - offset) > 1)
                Settle(session, index, passes - 1);
        });
    }

    /// <summary>The header's Aside: the same surface as Ctrl+;, which says what asides come with.</summary>
    private void OnAside(object sender, RoutedEventArgs e) => _composition?.Palette.Execute(Corvid.App.Core.Keys.KeyCommands.Aside);

    /// <summary>
    /// The index of the block find-in-session asked for, or of the turn a search across sessions opened this session at (G-5),
    /// taken once; -1 when none was asked or it is gone.
    /// </summary>
    private int RevealIndex(SessionViewModel session) => ViewModel.TakeRevealIndex(session);

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

    /// <summary>The title's room the models pill leaves at least; with less, the pill gives way (the status bar names both).</summary>
    private const double ModelsPillTitleRoom = 220;

    private void FitTitle()
    {
        // Not laid out (the page away): nothing to fit to, and its next SizeChanged fits it.
        if (HeaderGrid.ActualWidth <= 0)
            return;
        // The room left of the actions, less the pill at its natural width.
        var room = HeaderGrid.ActualWidth - HeaderActions.ActualWidth - HeaderGrid.ColumnSpacing;
        var pill = ForkPill.Visibility == Visibility.Visible ? ForkPill.ActualWidth + TitleRow.Spacing : 0;
        // A5: a child's parent link and a parent's children button keep their room too; the models pill gives way first.
        foreach (var kept in new FrameworkElement[] { ParentLink, ChildrenButton })
            if (kept.Visibility == Visibility.Visible)
                pill += kept.ActualWidth + TitleRow.Spacing;
        // A5: the models pill gives way to the title: it shows only while the title keeps room for a few words beside it.
        if (ModelsPill.Visibility == Visibility.Visible)
        {
            // Its natural width from its text, not the pill's own measure: once given way (MaxWidth 0) the pill measures 0 wide,
            // which would read as room and bring it back, a layout cycle whenever the title's room is near the line.
            ModelsPillText.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            var wanted = ModelsPillText.DesiredSize.Width + ModelsPill.Padding.Left + ModelsPill.Padding.Right
                + ModelsPill.BorderThickness.Left + ModelsPill.BorderThickness.Right + TitleRow.Spacing;
            var fits = room - pill - wanted >= ModelsPillTitleRoom;
            var limit = fits ? double.PositiveInfinity : 0;
            if (ModelsPill.MaxWidth != limit)
            {
                ModelsPill.MaxWidth = limit;
                AutomationProperties.SetAccessibilityView(ModelsPill, fits ? Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Content : Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
            }
            if (fits)
                pill += wanted;
        }
        var width = Math.Max(40, room - pill);
        if (Math.Abs(TitleText.MaxWidth - width) > 0.5)
            TitleText.MaxWidth = width;
    }

    private void OnBranchFromHere(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is UserMessageBlock block)
            _session?.ForkCommand.Execute(block);
    }

    // ---- A4: rename, pin and recap from the header's menu ----

    private void OnSessionMenuOpening(object? sender, object e)
    {
        RenameBox.Text = _session?.Title ?? "";
        RenameBox.SelectAll();
        PinButton.IsEnabled = _session?.CanPin == true;
        // G-12: a read-only view writes nothing to the session's log, a recap or a title included.
        RecapButton.IsEnabled = _session is { CanRecap: true, IsReadOnly: false };
        RenameButton.IsEnabled = _session is { IsReadOnly: false };
    }

    private void OnRename(object sender, RoutedEventArgs e)
    {
        if (_session is { } session && session.RenameCommand.CanExecute(RenameBox.Text))
        {
            session.RenameCommand.Execute(RenameBox.Text);
            SessionMenu.Hide();
        }
    }

    private void OnRenameKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
            return;
        e.Handled = true;
        OnRename(sender, e);
    }

    private void OnTogglePin(object sender, RoutedEventArgs e)
    {
        if (_session is { } session && session.TogglePinCommand.CanExecute(null))
        {
            session.TogglePinCommand.Execute(null);
            SessionMenu.Hide();
        }
    }

    private void OnRecap(object sender, RoutedEventArgs e)
    {
        if (_session is { } session && session.RecapCommand.CanExecute(null))
        {
            session.RecapCommand.Execute(null);
            SessionMenu.Hide();
        }
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
