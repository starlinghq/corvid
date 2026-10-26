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
# The engine (the default) over this machine's Corvid home; set HOME, CORVID_CONFIG and the XDG directories to try a
# temporary one:
dotnet src/Corvid.App/bin/Release/net10.0-desktop/corvid-app.dll
# The demo, as the mockups draw it:
dotnet src/Corvid.App/bin/Release/net10.0-desktop/corvid-app.dll --demo
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
implement in the browser head; A6: the approval card's key repeat check reads no `KeyStatus` there) and runs in a browser
(A6, headless Chromium): attached to the Corvid that serves it (below), or the demo with `?demo`. It does not call
`AppLogging`, whose console logger provider throws in a browser and left the tab on its loading bar.

## Attached mode (G-1)

The window can show the sessions of a Corvid that runs elsewhere: `corvid serve --attach` serves the session seam
(`ISessionService`, the engine's own `EngineSessionService`) over a WebSocket, and the app attaches with `--attach`. The
Sessions page, the rail, approvals with the full card, questions and the queue work as in-process; the other areas say
that they run on the engine's machine.

```sh
corvid serve --attach --no-openai                                  # unix:$XDG_RUNTIME_DIR/corvid/attach.sock
corvid-app --attach unix:$XDG_RUNTIME_DIR/corvid/attach.sock       # the owner, on the same machine
corvid serve --attach --listen ws://127.0.0.1:8737 --web <wasm publish>/wwwroot   # plus a loopback WebSocket and the browser app
corvid serve pair                                                  # a one-time code (5 min) for another device
corvid-app --attach ws://127.0.0.1:8737 --pair XXXXX-XXXXX         # pairs, keeps the device token, attaches
# a browser: http://127.0.0.1:8737/#pair=XXXXX-XXXXX               # the code in the fragment, the token in localStorage
corvid serve devices; corvid serve revoke <name>
```

- **Tokens.** Every connection needs one. The owner token is the Vault secret `Corvid:Serve:Attach:TokenSecret` names
  (checked through Vault's scrubber, as `corvid serve`'s bearer token is; Corvid never holds it, so hand it to the app
  with `CORVID_ATTACH_TOKEN`), or, without one, a token generated on first use in
  `$XDG_STATE_HOME/corvid/attach/owner.token` (0600, in a 0700 directory; a file others can read is refused). A paired
  device's token is kept on the server as a SHA-256 digest in `devices.json` (0600) and on the client in
  `client-tokens.json` (0600) or the browser's localStorage. Pairing codes live in the server's memory only, redeem
  once and expire after 5 minutes. Past 10 wrong codes or tokens a minute from one peer, that peer is refused until the
  minute ends, and past 100 a minute from the network as a whole every network peer is; the Unix socket is counted on its
  own, so guesses from the network never lock the owner out of their own machine. `corvid serve revoke <name>` refuses
  the device's next connection and closes the ones it has open (at its next request, or within 5 seconds).
- **Where it listens.** A Unix socket (0600) by default; `--listen` adds a WebSocket, loopback only unless the owner token
  is a Vault secret. On loopback only loopback host names are served (no DNS rebinding), and a browser is accepted only
  from the pages the server serves itself (`Origin`). There is no TLS: past loopback the owner token, device tokens and
  pairing codes cross the network in cleartext, and they answer approvals, so `corvid serve` says so at start. Listen
  beyond loopback only on a network you trust, or reach a loopback listener through an SSH tunnel or a TLS proxy. The
  browser head sends a `#pair=` code only to the server that served the page, never to another `?attach=` address.
- **Who sees what.** There is no guest: the owner and devices the owner paired get the seam, approvals included (F22);
  anything else is closed before it sees a session. A device cannot make pairing codes.
- **Disconnects.** A client that goes leaves the engine as it was: its approvals wait for another client or their deadline
  (then denied), and once no client is left a question a turn asked inside the turn is put aside (the turn ends with it
  open, and the next message answers it). The window shows a notice in each open session and its requests fail; open it
  again to attach (no automatic reconnect yet).
- **Images and workspaces (A6).** A message's images cross the wire base64 in `session/prompt` (at most 8, 40 MB
  together), and thumbnails come back by digest (`_corvid/read_image`); the engine checks, strips and stores them as for
  its own window. The server reads requests of up to 256 KB until a connection authenticates with the owner's or a paired
  device's token, and up to 64 MB after (a pairing connection never gets more). A file dropped on an attached window is
  read on that device, where the person dropped it, never on the engine's machine. `session/new` takes `workspace` (a
  path on the engine's machine, which the engine checks), `worktree`, `worktreeName` and `askIn`; the worktree's approval
  is the asking session's card, answered from the client. The capabilities say `canSendImages` and `canChooseWorkspace`.

## Command line

| Option | Effect |
| --- | --- |
| `--engine` | The default (the option only says so): Corvid's engine in-process (it hosts the gateway and the scheduler only when asked: `Corvid:App:HostGateway`, `Corvid:App:HostScheduler`, or Start hosting on the Gateway and Jobs pages; U-8) (`Corvid.App.Engine`, desktop head only), over the same `corvid.json`, models and event log as the CLI; `CORVID_CONFIG` and `XDG_CONFIG_HOME`, `XDG_DATA_HOME`, `XDG_STATE_HOME`, `XDG_RUNTIME_DIR` move them (a test home sets them all, and `Corvid:Models:Cache`, to temporary directories). The host starts and the model cache is checked before the window opens; the models load in the background. With models missing it opens First run. Every area runs on the engine; a member the engine cannot honour yet refuses with the work item that brings it ("comes with U-6: …", shown as a red notice or a disabled action). Not with `--demo`. |
| `--demo` | Demo services and the scripted scenario instead of the engine, with the clock frozen at 2026-10-24 14:05 local so every relative time and screenshot is stable. Every mockup comparison and screenshot of a mocked screen passes it; the browser head opens it with `?demo`. |
| `--attach <address>` | G-1: a Corvid elsewhere (`corvid serve --attach`) instead of the engine here: `unix:/path/attach.sock` or `ws://host:port`. The token is the one pairing kept for that address or, on this machine, the owner token (`$XDG_STATE_HOME/corvid/attach/owner.token`, or `$CORVID_ATTACH_TOKEN`). A refusal opens the window with every area saying why. Not with `--demo`, `--engine` or `--scene`. See Attached mode. |
| `--pair <code>` | With `--attach`: pairs this window as a device with a code from `corvid serve pair` first, keeping its token (0600) for the next `--attach` to the address. |
| `--theme light\|dark\|system` | The theme to start in; `system` by default. Settings switches it at run time. |
| `--page <area>` | The area to open: `sessions`, `board`, `review`, `rewind`, `memory`, `skills`, `jobs`, `models`, `safety`, `mcp`, `gateway`, `secrets`, `audit`, `settings`, `first-run` (any case; `=` works too: `--page=models`). |
| `--session <id>` | The session to select and open, such as `cli-9f2e`; with `--page`, it is selected and the page shows. |
| `--scene main\|approval` | The demo scenario's moment: `main` (turn 12 running, as Main.png; the default) or `approval` (turn 13 waiting on approval a-32, as Approval.png). It implies `--demo` (a scene never opens the engine on this machine's home) and is refused with `--engine`. |
| `--help` | The usage. A wrong argument prints the usage and exits with 2. |

`CORVID_APP_RENDERER=software` draws with Skia on the CPU instead of OpenGL (see the harness check below).

On the engine, a session opened from the log (`--session`, the rail after a restart) is replayed (K-10) and folds to
what the live turn showed for what the log records: messages, calls with their results and diffs, plans, checkpoints,
each asked call's approval answer (approved once, for the session or always; denied, with the reason the person
gave), and since A4 each turn's generator, the checkpoints' decisions, where outside content tainted the session and
where the person's next message cleared it, compaction markers, steers, clarifying questions, each turn's steps and why a
call that asked nobody ran (G-2). A turn the log holds no answer for replays as interrupted. It does not show what the log
does not keep: a past turn's tokens, a running call's output tail, a call's elapsed
time, the rule text a session or always answer wrote, a call refused without a prompt (the hardline list, a remembered
denial, no approver) as "denied" rather than "failed", and the reason for denials logged before `approval_resolved`
carried it (`note`). The composer's mode lines (A4, docs/plan/tui.md §4.1) are read by the host's `ComposerLine`, as
`corvid chat` reads them: `# fact` is staged at once through memory's gated write path (a "Staged for review" notice
with a review link; it starts no turn and the model never sees it), `!command` is a turn of its own that runs the command
as the person's `shell` call through the approval card, the sandbox and the checkpoints with no generator call (its output
is in the conversation for the next message), and `!!command` does the same with the message, call and output kept out
of every later prompt, compaction, the decider's recent events and the episodic index (the log keeps them). Only a
one-line message is a mode line: text of more than one line (pasted notes under a Markdown heading) and a Markdown image
are sent; a mark with nothing after it is refused with what to type, and the draft stays. A mark is a mode only before
a space (`#`) or a space or a command word (`!`, `!!`: letters, digits and `_ . ~ / + -` up to a space), so `#123 is
failing again`, `#include <stdio.h> not found` and `!important: keep the API stable` are sent as messages; `\#` or `\!`
at the start sends any such line as a message, without the backslash.

Images (A6, K-8, flow F19): a pasted bitmap or copied image file, an image file dropped on the composer, or a path typed
in the composer's attach box becomes a chip above the composer (up to 8 per message, 20 MiB and 40 megapixels each; PNG,
JPEG, GIF and WebP, read from the bytes, never from the name); a dropped file that is not an image becomes an `@`
reference in the draft, and a dropped folder is refused with why. The engine checks the chips before the message is
queued (a refusal gives the message and chips back), and the turn strips every image's metadata (EXIF, text chunks,
location) and stores the stripped bytes in the media store (`<data>/media/sha256/`, content-addressed); the log's
`user_message` carries only references (digest, type, size, dimensions, name) in an optional `images` field, so a binary
older than A6 reads such a message as its text alone. The transcript draws them as bounded thumbnails (360 by 240 at
most) with their captions, live and replayed. A generator that reads images (a remote model with `"Vision": true`) is
sent them; any other is sent the vision slot's description of each, labelled as a model's reading of the image, or the
message is refused when no vision model is set. A gated (cloud) model is sent no image unless its egress policy allows
images (`Corvid:Privacy:Images`, or the model's own `Egress:Images`); otherwise the privacy gate keeps the turn local or
refuses it with why. `corvid chat` takes `/image <path>` (stage, list, `clear`) and `@shot.png` in a message; ACP takes
image content blocks (base64 `data`; a `uri` alone is refused) and advertises `promptCapabilities.image`.

Titles, recaps, pins and drafts (A4, G-6 and G-7): a session's title is its rename, else the one the local generator
wrote after its first exchange (never a cloud generator), else its first message; the header's ••• menu renames it, pins
it to the rail and asks for a recap, and the CLI's `/rename`, `/pin`, `/recap` and `/sessions` do the same over the same
log and `ui.db`. Drafts (with the one put aside by `Ctrl+S`) are written to `ui.db` once the composer has been still for
0.75 s and come back after a restart; the CLI shows the app's draft when it resumes the session. Opening a session the
person last saw over an hour ago (`Corvid:Sessions:RecapAfterMinutes`) recaps what happened meanwhile ("While you were
away"). Settings › Appearance, Notifications and Layout are kept in `ui.db`; the window applies the layout ones (rail or
dock hidden, Focus, Board) when it starts. A rollback from Rewind
to before a call keeps that call's turn and its message, as the kernel does (it goes back to just before the call): the
call and what came after it in the turn leave, and so do the later turns, and the open transcript then reads as a replay
of the log would.

The window's own keys come from the key map (`Corvid.App.Core/Keys/KeyMap.cs`, `ui.md` §6) and can be remapped at once
in Settings › Keybindings: `Ctrl+K` and `Ctrl+P` the command palette, `Ctrl+Tab` the session switcher (again for the
next, `Ctrl+Shift+Tab` back), `Ctrl+R` prompt history, `Ctrl+F` find in the session, `Ctrl+Shift+F` search across
sessions, `Alt+P` switch the generator, `F1` and `?` (outside a text field) the shortcuts, `Ctrl+N`, `Ctrl+Shift+N` and
`Ctrl+;` the surfaces of what comes later, `Ctrl+B` the dock, `Ctrl+Shift+B` the rail, `Ctrl+,` Settings,
`Ctrl+Shift+C` Review, `Esc` closes the palette or an overlay. A page's own key on the same gesture (Audit's, Memory's
and Skills' `Ctrl+F`, Models' `Alt+P`) wins while that area shows; a new page key goes into `KeyMap`'s defaults and
`KeyMap.ContextsOf` so the help and the conflict check know it. On the Sessions
page: `Enter` sends (queued while a turn runs), `Shift+Enter` breaks the line, `Ctrl+Enter` steers, `Esc` interrupts
(in the composer also while an approval card waits, closing it as cancelled), `Esc Esc` on an idle empty composer opens Rewind, `Up` takes the last queued message back, `Shift+Tab` cycles the
approval mode, `Ctrl+S` puts the draft aside; on an open approval card the first `Enter` focuses it, then `1` to `5`
answer and the arrows move (`Space` answers nothing, typing a letter goes back to the composer with it, and `Ctrl+Enter`
on the card goes back to the composer: it never answers a card; a held `Enter`'s repeats answer nothing, and for ¾ s after
`Enter` moves focus to the card, or after a key it swallowed, `Enter` answers nothing and a digit goes back to the
composer as typing, while an arrow or `Tab` arms the card at once); `Ctrl+1` to `Ctrl+7` pick dock tabs; `Ctrl+O`, `Ctrl+T` and `Ctrl+Shift+D` expand the latest
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

Workspaces (A6, G-4): on the engine a session keeps the workspace it was started in (a `WorkspaceBound` event in its
log), and its approvals, sandboxed shell, file tools, checkpoints, Review and Rewind follow it; a session that recorded
none works in the configured one (`Corvid:Workspace`), as before. With sessions in several workspaces the rail groups
them by workspace (the folder's name, its path where two share a name, and a worktree's branch). Ctrl+N and New session
start in the workspace of the session in view; the Board's Open a workspace… starts one in a typed folder (`~/` is the
home directory), in one the window already works in, or in a new git worktree of the session in view's repository (a
branch `corvid/<name>` under `$XDG_STATE_HOME/corvid-worktrees`, `Corvid:Sessions:WorktreesDirectory`), made only after
the person approves it on that session's card. `/workspace` does the same from the composer and the CLI (`list`,
`open <path>`, `new [<name>]`, `remove <name>`), and `corvid chat --workspace <dir>` starts a chat session elsewhere. The
demo keeps one workspace and the rail's Pinned and Recent.

## The harness check

Every change to a screen is checked by running the app and looking at it next to its mockup (the approved artboards:
Main, Approval, Review, Rewind, Board, Models, Memory, Audit, FirstRun and Phone, rendered at 1440x900, 820 wide and
390x844). On a machine without a desktop, under Xvfb:

```sh
Xvfb :99 -screen 0 2560x1600x24 -nolisten tcp &
export DISPLAY=:99 CORVID_APP_RENDERER=software
dotnet src/Corvid.App/bin/Release/net10.0-desktop/corvid-app.dll --demo --page models &
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
head's desktop target and `Corvid.App.Engine` reach `Corvid.Host`, and the WebAssembly target never does. The CLI
references `Corvid.App.Engine` (A6, G-1) so `corvid serve --attach` serves the engine's own session service: the remote
seam is `ISessionService` itself, with no second copy of the approval, queue and question logic. No
CommunityToolkit, MVUX, Uno.Extensions or Uno Toolkit: Hot Design (which brings them) and the in-app MCP endpoint are
turned off in the project. The WebAssembly target pins `System.Security.Cryptography.Pkcs` and `.Xml` to the patched
release over the 10.0.12 that Uno.Sdk's `Microsoft.Windows.Compatibility` brings (Directory.Packages.props); the desktop
graph has neither.
