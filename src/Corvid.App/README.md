# Corvid.App

The windowed Corvid (design: [`docs/plan/ui.md`](../../docs/plan/ui.md)), an [Uno Platform](https://platform.uno) single
project. It builds the Skia desktop head (`net10.0-desktop`: X11 and the framebuffer on Linux, Win32, macOS) by default
and the WebAssembly head only on request. Its view models live in [`Corvid.App.Core`](../Corvid.App.Core), which has no
Uno types and is tested headless in [`tests/Corvid.App.Core.Tests`](../../tests/Corvid.App.Core.Tests).

So far: the A0 scaffold (the shell as the Main mockup draws it, the theme, the fonts, the icons, navigation to every
area, a placeholder page per area), the session core in `Corvid.App.Core/Sessions`, and the Sessions page on it (A0.3:
the transcript, the approval card, the turn strip, the composer and the dock's panels). Where the build stands and what
comes next: [`docs/plan/ui-handoff.md`](../../docs/plan/ui-handoff.md); the rules every builder follows:
[`docs/plan/ui-build.md`](../../docs/plan/ui-build.md).

## Build and run

```sh
dotnet build src/Corvid.App/Corvid.App.csproj -c Release
dotnet src/Corvid.App/bin/Release/net10.0-desktop/Corvid.App.dll --demo
# or, building as it goes:
dotnet run --project src/Corvid.App -c Release -- --demo --page models --theme dark
```

The whole solution builds the desktop head too (`dotnet build Corvid.slnx -c Release`, as CI does); it needs no display
and no workload. Uno.Sdk's version is pinned in the repository's `global.json` (`msbuild-sdks`).

The WebAssembly head is opt-in, because it needs the `wasm-tools` workload, which CI does not have (without it the build
stops with UNOWA0001):

```sh
dotnet workload install wasm-tools
dotnet build src/Corvid.App/Corvid.App.csproj -c Release -f net10.0-browserwasm -p:CorvidAppWasm=true
```

It builds with the workload (A3: `SelectableButtonAutomationPeer` skips the selection property event, which Uno 6.7 does not
implement in the browser head) but has not been run in a browser yet; it runs on the demo services only, since the engine
is desktop-only until attached mode (G-1).

## Command line

| Option | Effect |
| --- | --- |
| `--demo` | Demo services and the scripted scenario, with the clock frozen at 2026-10-24 14:05 local so every relative time and screenshot is stable. The default. |
| `--engine` | Corvid's engine in-process (`Corvid.App.Engine`, desktop head only), over the same `corvid.json`, models and event log as the CLI; `CORVID_CONFIG` and `XDG_CONFIG_HOME`, `XDG_DATA_HOME`, `XDG_STATE_HOME`, `XDG_RUNTIME_DIR` move them (a test home sets them all, and `Corvid:Models:Cache`, to temporary directories). The host starts and the model cache is checked before the window opens; the models load in the background. With models missing it opens First run. Every area runs on the engine; a member the engine cannot honour yet refuses with the work item that brings it ("comes with G-3: …", shown as a red notice or a disabled action). Not with `--demo`. |
| `--theme light\|dark\|system` | The theme to start in; `system` by default. Settings switches it at run time. |
| `--page <area>` | The area to open: `sessions`, `board`, `review`, `rewind`, `memory`, `skills`, `jobs`, `models`, `safety`, `mcp`, `gateway`, `secrets`, `audit`, `settings`, `first-run` (any case; `=` works too: `--page=models`). |
| `--session <id>` | The session to select and open, such as `cli-9f2e`; with `--page`, it is selected and the page shows. |
| `--scene main\|approval` | The demo scenario's moment: `main` (turn 12 running, as Main.png; the default) or `approval` (turn 13 waiting on approval a-32, as Approval.png). |
| `--help` | The usage. A wrong argument prints the usage and exits with 2. |

`CORVID_APP_RENDERER=software` draws with Skia on the CPU instead of OpenGL (see the harness check below).

The window's own keys come from the key map (`Corvid.App.Core/Keys/KeyMap.cs`, `ui.md` §6) and can be remapped at once
in Settings › Keybindings: `Ctrl+K` and `Ctrl+P` the command palette, `Ctrl+Tab` the session switcher (again for the
next, `Ctrl+Shift+Tab` back), `Ctrl+R` prompt history, `Ctrl+F` find in the session, `Ctrl+Shift+F` search across
sessions, `Alt+P` switch the generator, `F1` and `?` (outside a text field) the shortcuts, `Ctrl+N`, `Ctrl+Shift+N` and
`Ctrl+;` the surfaces of what comes later, `Ctrl+B` the dock, `Ctrl+Shift+B` the rail, `Ctrl+,` Settings,
`Ctrl+Shift+C` Review, `Esc` closes the palette or an overlay. A page's own key on the same gesture (Audit's, Memory's
and Skills' `Ctrl+F`, Models' `Alt+P`) wins while that area shows; a new page key goes into `KeyMap`'s defaults and
`KeyMap.ContextsOf` so the help and the conflict check know it. On the Sessions
page: `Enter` sends (queued while a turn runs), `Shift+Enter` breaks the line, `Ctrl+Enter` steers, `Esc` interrupts,
`Esc Esc` on an idle empty composer opens Rewind, `Up` takes the last queued message back, `Shift+Tab` cycles the
approval mode, `Ctrl+S` puts the draft aside; on an open approval card the first `Enter` focuses it, then `1` to `5`
answer and the arrows move; `Ctrl+1` to `Ctrl+7` pick dock tabs; `Ctrl+O`, `Ctrl+T` and `Ctrl+Shift+D` expand the latest
output, the thoughts and every checkpoint's chips. On Models: `Alt+P` opens the generator's Switch… picker, `Alt+Left`
and `Alt+Right` move a focused escalation-ladder step (or drag it by its grip), and `Esc` closes a picker or the
placement editor. `Ctrl+Shift+C` opens Review from anywhere; on Review `Up` and `Down` move through the file list. On
Audit: `Up` and `Down` move between event rows, `Enter`
and `Space` open the focused row, `Ctrl+F` puts the caret in the "path or tool" filter, and `Esc` first shows every
event again after a detection's link narrowed the log, then closes the open event. On Skills: `Ctrl+1` to `Ctrl+4` pick
Review, Library, History and Import, `Ctrl+F` puts the caret in the filter (`Esc` there clears it), `Up` and `Down` in
the library or the history open the next or previous row, `Esc` closes the open skill or commit; in a draft's editor
`Ctrl+Enter` approves as edited and `Esc` undoes the edit, in a reason `Enter` rejects. On Jobs and MCP, `Up` and `Down`
move between jobs or servers from a focused row and `Enter` opens it; on Jobs, MCP and Gateway, `Esc` closes an open
question (remove, trust, forget, pair as owner, revoke), then the form or allowlist editor. On Safety: `Delete` on a rule's
Remove… asks to remove it, the question's Remove takes focus, and `Esc` stops asking. On Secrets: `Enter` in Set a
secret…'s name field starts the set, and `Esc` closes the picker. The rest of the keyboard map
(`docs/plan/ui.md` §6) comes with the screens it drives.

## The harness check

Every change to a screen is checked by running the app and looking at it next to its mockup (the approved artboards:
Main, Approval, Review, Rewind, Board, Models, Memory, Audit, FirstRun and Phone, rendered at 1440x900, 820 wide and
390x844). On a machine without a desktop, under Xvfb:

```sh
Xvfb :99 -screen 0 2560x1600x24 -nolisten tcp &
export DISPLAY=:99 CORVID_APP_RENDERER=software
dotnet src/Corvid.App/bin/Release/net10.0-desktop/Corvid.App.dll --demo --page models &
w=$(xdotool search --sync --onlyvisible --name '^Corvid$' | head -1)
xdotool windowmove "$w" 0 0 windowsize "$w" 1440 900; sleep 2
import -window root -crop 1440x900+0+0 +repage models-1440.png
xdotool mousemove 130 78 click 1      # the rail's first row; rows are 31.575 px apart from y 62
```

Then compare the screenshot with the mockup: layout, spacing, copy, colours, states, alignment, clipping. Check the dark
theme and the widths 1024x900, 820x900 and 390x844, click what the screen owns, and read the app's standard error, where
Uno's warnings and errors go in every build. Things to know:

- Screenshot a Release build. A Debug build talks to Uno's dev server and logs that none answered.
- Set `CORVID_APP_RENDERER=software` under Xvfb. With OpenGL (Mesa's llvmpipe), Uno sometimes keeps the old height after
  the window shrinks and draws the frame shifted down; the software renderer does not. Rendering is otherwise the same,
  up to antialiasing.
- Xvfb has no window manager, so there is no title bar, and the log shows harmless D-Bus, IME and `_NET_WM_STATE`
  errors. The window's title is "Corvid".
- Text is Inter, so text widths match the browser's to a pixel or two per item; light text on dark looks a little
  heavier than Chrome draws it.

## Layout

```
src/Corvid.App/
  App.xaml(.cs)              the window ("Corvid"), the theme dictionaries merged, Inter as the default font
  Platforms/Desktop/         Program.cs: the command line, logging, the Skia desktop host
  Platforms/WebAssembly/     Program.cs and LinkerConfig.xml (only with -p:CorvidAppWasm=true)
  Themes/Colors.xaml         the corvid.css tokens as Light and Dark brushes (CorvidBg ... CorvidDelWord, CorvidOnAccent)
  Themes/Typography.xaml     CorvidSans, CorvidMono and the text styles
  Themes/Controls.xaml       templates for the controls below, from the mockups' CSS
  Assets/Fonts/              Inter and JetBrains Mono, their Uno font manifests and OFL licences
  Controls/                  Icon (+ IconPaths), RailNavItem, RailSessionItem, IconButton, SegmentButton, TagChip,
                             PhoneTabItem, DockTab, LinkButton, PitchedStackPanel, WrapRowPanel, CorvidButton (.btn),
                             ToneRow (.row), ColumnsPanel, Meter, RichText (runs from the markdown model), MarkdownView,
                             DiffRowsView, HintLine (key caps)
  Shell/                     ShellPage (the window's content), RailView, StatusBarView (+ FitRowPanel), AreaPages, IDockProvider,
                             EqualColumnsPanel, CommandPalette (Ctrl+K, the switcher, history, find, help), KeyGestures
  Pages/                     one <Area>Page.xaml per area; SessionsDock and BoardDock are those areas' dock panels;
                             ModelsStyles.xaml and SpanText are the Models and First run pages' shared parts;
                             Jobs, MCP and Gateway also merge ModelsStyles.xaml, and share OpsStyles.xaml (row button,
                             fields, pairing code) and OpsLayout (head, columns, tables, focus after a change)
  Infrastructure/            UiDispatcher (the core's IDispatcher), Ui (x:Bind helpers), AppLogging
src/Corvid.App.Engine/       the in-process engine (desktop head only): AppEngine (the host and the runtime starting in the
                             background), EngineAppServices (one partial file per area), and one folder per area's service:
                             Sessions/ (EngineSessionService with its approvals and commands, CardPrompter, ApprovalCards,
                             SessionUpdateTranslator from the host's K-10 stream to the app's updates), Review/, Rewind/,
                             Audit/ and History/ (the ledger and the signal they share), Models/, FirstRun/, Settings/,
                             Memory/, Skills/, Safety/, Jobs/, Mcp/, Gateway/, Secrets/ and Ops/ (their shared change
                             signal), Board/ (the tray and status over the MCP, Gateway, Memory and Jobs services) and
                             PromptHistory/ (the event log's user messages)
src/Corvid.App.Core/
  Mvvm/                      ObservableObject, RelayCommand(<T>), AsyncRelayCommand, Choice, IDispatcher, InlineDispatcher
  Shell/                     ShellViewModel, StatusBarViewModel, NavItem, SessionListItem, PhoneTab, ShellLayout, DemoShell
  Composition/               AppComposition, one partial file per area (and AppComposition.Palette); IAppServices with
                             DemoAppServices and Unavailable (the refusing services engine mode uses until an area is built)
  Keys/                      KeyMap (every key with its context, remapping, conflicts), KeyGesture
  Palette/                   PaletteViewModel (commands, pages, settings, sessions, prompt history, find), FuzzyMatch
  <Area>/                    each area's view model (and, as the area is built, its service, DTOs and demo service)
  Area.cs, AppOptions.cs, IconKind.cs, AppTheme.cs (with Tone), FrozenTimeProvider.cs, AreaViewModel.cs
```

## Areas

Each rail destination is an area: Sessions, Board, Review, Rewind, Memory, Skills, Jobs, Models, Safety, Mcp, Gateway,
Secrets, Audit, Settings, FirstRun (`Area` in the core). An area owns, and only its builder edits:

- `src/Corvid.App.Core/<Area>/`: `I<Area>Service`, its DTO records, `Demo<Area>Service`, `<Area>ViewModel` (deriving
  from `AreaViewModel`, which gives the page its title).
- `src/Corvid.App.Core/Composition/AppComposition.<Area>.cs`: the property named after the area that creates its service
  (through `Services`, an `IAppServices`: `DemoAppServices` in demo mode, `Corvid.App.Engine`'s `EngineAppServices` in
  engine mode) and view model on first use.
- `src/Corvid.App.Engine/<Area>/` and `src/Corvid.App.Engine/EngineAppServices.<Area>.cs`: the area's engine service and
  the factory member that makes it (until it is built, one of `Unavailable`'s refusing services).
- `src/Corvid.App/Pages/<Area>Page.xaml(.cs)`: the page. It takes its view model from the composition the shell
  navigates with (`OnNavigatedTo`: `((AppComposition)e.Parameter).<Area>`, then `Bindings.Update()`) and binds with
  `x:Bind`.

A page that has dock panels implements `IDockProvider`; the shell shows its `Dock` as the 380 px column at wide widths
and over the content from the status bar's toggle below 1180 px. Create the dock with the page: the frame raises
`Navigated` (where the shell takes the dock) before `OnNavigatedTo` gives the page its view model.

The shell itself (rail, status bar, layout, theme) is `ShellViewModel` and `StatusBarViewModel`; the demo fills them
with the Main mockup's values (`DemoShell`), and live data sets the same members: `SetBadge`, `SetSessions`,
`AttentionCount`, `Workspace`, the status bar's strings, `Mode` (a person's choice raises `ModeRequested`; the engine's
answer sets `Mode`).

## Theme, type and icons

- Brushes are named after the CSS tokens and swap with the theme: `{ThemeResource CorvidText}` and so on. Use them in
  templates' visual states rather than computing colours in code, so a theme switch repaints. Uno does not repaint a
  `Run`'s theme brush when the theme changes, so give differently coloured words their own `TextBlock`.
- Inter is the default text font (`ms-appx:///Assets/Fonts/Inter`, a manifest choosing 400, 500 or 600 by `FontWeight`);
  `CorvidMono` is JetBrains Mono. CSS centres a line in its line height; XAML puts the extra space above. For one line
  in a CSS line box, centre the text at its natural height (Inter: 1.21 em) or pad it by half the leading; rows of a
  fractional height stack in a `PitchedStackPanel` so they land on the mockup's pixels.
- `Icon Kind="..."` draws one of the mockups' icons in its `Foreground`. Kinds: Logo, Bell, Chat, Board, Sliders,
  Layers, Sparkle, Clock, Chip, Shield, Plug, Send, Lock, Lines, Terminal, Paperclip, Question, Warning, PopOut, Cloud,
  File, ChevronDown, Search, Globe, Menu, More, Check, and PanelRight (the dock toggle, which the mockups lack). To add
  one, copy the 16 px path from the `.dc.html`, write circles and rectangles as arcs, and add it to `IconPaths`.
- Views built in code (`MarkdownView`, `DiffRowsView`, `HintLine`, `RichText`) look brushes up with `ThemeBrushes.Get`
  for the element's theme and rebuild on `ActualThemeChanged`.

More Uno gotchas found building the Sessions page:

- An explicit `Style` replaces the implicit one rather than adding to it. Base keyed styles on a keyed base
  (`CorvidSmallButton` on `CorvidButtonBase`), or the control loses its template.
- A trailing space, even a no-break one, is trimmed from a measured `TextBlock`; put gaps in the panel's `Spacing`.
- A horizontal `StackPanel` reports its content's width, not its column's: measure the column instead (the session
  title gives way to the fork pill this way).
- `TextBox` with `AcceptsReturn` still types the line break of an Enter whose key event was handled; cancel that change
  in `BeforeTextChanging`.
- The page's own `x:Bind` paths do not follow a static source's `PropertyChanged`; templates created later do read it.
- `x:Bind` function arguments are paths or literals, not other function calls.

And from the A1 screens:

- With `UseSystemFocusVisuals=True`, a row whose `Button` template was visited by keyboard can stay drawn faded after
  focus moves on. Rewind's ledger rows and the side-by-side diff's lines draw their own 2 px accent ring instead.
- A text run made only of spaces measures as zero width, so indentation before a changed word disappears.
  `SideBySideDiffView` pads such runs; the shared `DiffRowsView` still has the bug where leading whitespace is its own
  segment.
- Under `--theme light` on a system in dark, a control that took keyboard focus (Tab, or `Focus(FocusState.Keyboard)`)
  redrew in dark-theme brushes and stayed so: Uno resolves a focused control's visual-state brushes against the
  application's theme, not the root page's. `App` now sets `Application.RequestedTheme` from `--theme` before start-up,
  which fixes it for a theme chosen on the command line. A theme switched at run time (Settings) can still mismatch,
  since Uno takes the application's theme only before start-up.
- `ObservableCollection.Move` in an `ItemsControl` re-templated the moved item outside the page's theme; remove and
  re-insert instead (the Board's sync does).
- An `x:Bind` from a `DataTemplate`'s `ColumnDefinition.Width` (or a `ContentControl` header template) to a static
  source's `GridLength` did not follow later `PropertyChanged`; set such widths in code (Memory's `MemoryTableRow`).
- The window's first focus lands on the first `Button`, with a keyboard ring. A page that should not start there takes
  focus itself on `Loaded` (`IsTabStop` and a programmatic `Focus`).
- Closing an inline editor sends focus to the next item's first button, which then looks pressed: focus the page before
  the command runs.

And from A2:

- Uno fires the first matching `KeyboardAccelerator` in registration order, whatever has focus, and stops there even
  when the handler leaves `args.Handled` false. So the shell leaves unbound a window gesture the showing page also binds
  (`KeyMap.IsPagesKey`), and a page key that shares a window key's gesture must be in `KeyMap`'s defaults.
- A `Tapped` that follows the `Click` that opened an overlay lands on the overlay's scrim: use `PointerPressed` on
  scrims, as `CommandPalette` does. Tab gestures never reach accelerators; the shell handles them in `PreviewKeyDown`.
- Uno refuses focus to a control that has not been laid out since it became visible; `OpsLayout.FocusWhenShown` waits
  for its layout.
- A template's `x:Bind` to a static source (`SessionsLayout.Current.IsPhone`) is read when the template is made and not
  again: the window starts at one width and is resized, so the Sessions page makes its transcript again when the width
  class changes. That takes three steps, since the repeater otherwise hands back its old elements bound as before:
  clear the source and lay out, replace the templates' `RecyclePool`s, and set each element it kept anyway (a focused
  one) to its data context again. A nested item template is not rebound by that, so a list that differs by width is two
  lists, one shown per width (the card's reasons). A row that must follow the width (First run's checks) sets itself on
  its own `SizeChanged`.
- An `ItemsRepeater` let Tab reach only its first focusable control; the transcript sets `TabFocusNavigation="Local"`.
- The status bar's row (`FitRowPanel`) gives way whole and by priority: each tag is its own item, "tainted" never gives
  way, and the right-hand item is in the same contest. An item that gave way leaves the tab order until it shows again.
  The right-hand item is the area's own (the Board's activity, Audit's log size, the first run's step) or else the open
  session's (`StatusBarViewModel.ShownTrailing`), so every page says the same while a turn runs; an area that writes one
  gives it back when it goes.
- The bell counts every attention item across sources (`ShellViewModel.SetAttention`: the sessions' approvals and
  questions, and the tray's other items, such as a memory review or a pairing request): 5 in the demo, where `Main.png`
  and `Phone.png` draw 3 (the mockups disagree among themselves). The sum is the right count for a tray that lists all
  of them, so it stays.

## The phone layout and accessibility

Under 720 px (Phone.png): the top bar (the menu, the session's or area's title, one tag of the status bar's, the bell)
and the bottom bar (Chat to Sessions, Changes to Review, Attention to the Board and its tray, More to the rail). The tag
is "tainted" whenever the bar has it (taint always shows), otherwise the first. The top bar's title is the page's
level-1 heading where the page drops its own title on the phone, and not a heading where it keeps it (Review, Rewind,
Models, Audit, the first run). The first run's setup has neither the bottom bar nor the menu, bell and workspace line
(`ShellViewModel.SetupShowing`). On the session the header, the key hint and the composer's bar go; the turn strip
shows while the turn works or has a queue, not while it waits on an answer (the card says so); the composer is
one line with a 44 px send button; the approval card leaves the command to the tool card above it, shows compact parsed
rows (the command, its effects, each target), the short reasons, and full-width answers with the denials side by side
(`PhoneAnswersPanel`). Other areas lay out in one column; their wide tables scroll sideways inside their panels.

Accessibility (`ui.md` §4.15), as built: icon-only buttons are named and every page has a level-1 heading (both checked
over every XAML file by `AccessibilityMarkupTests`, with notice and error rows as live regions); the rail's areas and
sessions, the dock's tabs and the phone's tabs report which is current through `SelectableButtonAutomationPeer`; diffs
draw `+` and `−` in the gutter as well as their colours; the Sessions page announces the turn's step politely (without
its ticking time, `TurnStripStatus`) and an approval, a question or a finished turn assertively; the command palette
marks its selected row with a bar as well as its colour and says it politely as the arrows move it ("Go to Board, 2 of
42"). Uno's X11 head has no
AT-SPI bridge, so on Linux no screen reader sees the app; the peers and live regions are for the Windows and macOS
heads and are unverified with a real screen reader.

## Fonts

| Files | Family | Source | Licence |
| --- | --- | --- | --- |
| `Inter-Regular.ttf`, `Inter-Medium.ttf`, `Inter-SemiBold.ttf` | Inter 4.1, the static TTFs of `extras/ttf` | [rsms/inter v4.1](https://github.com/rsms/inter/releases/tag/v4.1), `Inter-4.1.zip`, sha256 `9883fdd4a49d4fb66bd8177ba6625ef9a64aa45899767dde3d36aa425756b11e` | `Inter-OFL.txt` |
| `JetBrainsMono-Regular.ttf`, `JetBrainsMono-Medium.ttf` | JetBrains Mono 2.304, `fonts/ttf` | [JetBrains/JetBrainsMono v2.304](https://github.com/JetBrains/JetBrainsMono/releases/tag/v2.304), `JetBrainsMono-2.304.zip`, sha256 `6f6376c6ed2960ea8a963cd7387ec9d76e3f629125bc33d1fdcd7eb7012f7bbf` | `JetBrainsMono-OFL.txt` |

## Boundaries

`tools/Corvid.Architecture` holds the app's rules, with tests in `tests/Corvid.Architecture.Tests`: Uno's packages
appear in `Corvid.App` only, from an explicit list of what Uno.Sdk's implicit packages resolve (another list for the
WebAssembly target); `Corvid.App.Core` takes no package but Markdig and no project but `Corvid.Abstractions`; only the
head's desktop target and `Corvid.App.Engine` reach `Corvid.Host`, and the WebAssembly target never does. No
CommunityToolkit, MVUX, Uno.Extensions or Uno Toolkit: Hot Design (which brings them) and the in-app MCP endpoint are
turned off in the project. The WebAssembly target pins `System.Security.Cryptography.Pkcs` and `.Xml` to the patched
release over the 10.0.12 that Uno.Sdk's `Microsoft.Windows.Compatibility` brings (Directory.Packages.props); the desktop
graph has neither.
