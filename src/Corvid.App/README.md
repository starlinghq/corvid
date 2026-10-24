# Corvid.App

The windowed Corvid (design: [`docs/plan/ui.md`](../../docs/plan/ui.md)), an [Uno Platform](https://platform.uno) single
project. It builds the Skia desktop head (`net10.0-desktop`: X11 and the framebuffer on Linux, Win32, macOS) by default
and the WebAssembly head only on request. Its view models live in [`Corvid.App.Core`](../Corvid.App.Core), which has no
Uno types and is tested headless in [`tests/Corvid.App.Core.Tests`](../../tests/Corvid.App.Core.Tests).

This is the A0 scaffold: the shell as the Main mockup draws it (rail, content frame, dock column, status bar), the theme,
the fonts, the icons, navigation to every area, and a placeholder page per area. The areas fill in from here.

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

It restores and passes the boundary checker here, but has not been built or run, for want of the workload.

## Command line

| Option | Effect |
| --- | --- |
| `--demo` | Demo services and the scripted scenario, with the clock frozen at 2026-10-24 14:05 local so every relative time and screenshot is stable. The default (and the only mode) until `Corvid.App.Engine` exists. |
| `--theme light\|dark\|system` | The theme to start in; `system` by default. Settings switches it at run time. |
| `--page <area>` | The area to open: `sessions`, `board`, `review`, `rewind`, `memory`, `skills`, `jobs`, `models`, `safety`, `mcp`, `gateway`, `secrets`, `audit`, `settings`, `first-run` (any case; `=` works too: `--page=models`). |
| `--session <id>` | The session to select and open, such as `cli-9f2e`; with `--page`, it is selected and the page shows. |
| `--help` | The usage. A wrong argument prints the usage and exits with 2. |

`CORVID_APP_RENDERER=software` draws with Skia on the CPU instead of OpenGL (see the harness check below).

Keys so far: `Ctrl+B` the dock, `Ctrl+Shift+B` the rail, `Ctrl+,` Settings, `Esc` closes an overlay. The rest of the
keyboard map (`docs/plan/ui.md` §6) comes with the screens it drives.

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
                             PhoneTabItem, DockTab, LinkButton, PitchedStackPanel, WrapRowPanel
  Shell/                     ShellPage (the window's content), RailView, StatusBarView, AreaPages, IDockProvider,
                             EqualColumnsPanel
  Pages/                     one <Area>Page.xaml per area; SessionsDock and BoardDock are those areas' dock panels
  Infrastructure/            UiDispatcher (the core's IDispatcher), Ui (x:Bind helpers), AppLogging
src/Corvid.App.Core/
  Mvvm/                      ObservableObject, RelayCommand(<T>), AsyncRelayCommand, Choice, IDispatcher, InlineDispatcher
  Shell/                     ShellViewModel, StatusBarViewModel, NavItem, SessionListItem, PhoneTab, ShellLayout, DemoShell
  Composition/               AppComposition, one partial file per area
  <Area>/                    each area's view model (and, as the area is built, its service, DTOs and demo service)
  Area.cs, AppOptions.cs, IconKind.cs, AppTheme.cs (with Tone), FrozenTimeProvider.cs, AreaViewModel.cs
```

## Areas

Each rail destination is an area: Sessions, Board, Review, Rewind, Memory, Skills, Jobs, Models, Safety, Mcp, Gateway,
Secrets, Audit, Settings, FirstRun (`Area` in the core). An area owns, and only its builder edits:

- `src/Corvid.App.Core/<Area>/`: `I<Area>Service`, its DTO records, `Demo<Area>Service`, `<Area>ViewModel` (deriving from
  `AreaViewModel`, which gives the page its title).
- `src/Corvid.App.Core/Composition/AppComposition.<Area>.cs`: the property named after the area that creates its service
  and view model on first use. Demo mode passes the demo service and `Time` (the frozen clock); the engine's service comes
  with `Corvid.App.Engine`.
- `src/Corvid.App/Pages/<Area>Page.xaml(.cs)`: the page. It takes its view model from the composition the shell navigates
  with (`OnNavigatedTo`: `((AppComposition)e.Parameter).<Area>`, then `Bindings.Update()`) and binds with `x:Bind`.

A page that has dock panels implements `IDockProvider`; the shell shows its `Dock` as the 380 px column at wide widths
and over the content from the status bar's toggle below 1180 px. Create the dock with the page: the frame raises
`Navigated` (where the shell takes the dock) before `OnNavigatedTo` gives the page its view model.

The shell itself (rail, status bar, layout, theme) is `ShellViewModel` and `StatusBarViewModel`; the demo fills them with
the Main mockup's values (`DemoShell`), and live data sets the same members: `SetBadge`, `SetSessions`, `AttentionCount`,
`Workspace`, the status bar's strings, `Mode` (a person's choice raises `ModeRequested`; the engine's answer sets `Mode`).

## Theme, type and icons

- Brushes are named after the CSS tokens and swap with the theme: `{ThemeResource CorvidText}` and so on. Use them in
  templates' visual states rather than computing colours in code, so a theme switch repaints. Uno does not repaint a
  `Run`'s theme brush when the theme changes, so give differently coloured words their own `TextBlock`.
- Inter is the default text font (`ms-appx:///Assets/Fonts/Inter`, a manifest choosing 400, 500 or 600 by `FontWeight`);
  `CorvidMono` is JetBrains Mono. CSS centres a line in its line height; XAML puts the extra space above. For one line in
  a CSS line box, centre the text at its natural height (Inter: 1.21 em) or pad it by half the leading; rows of a
  fractional height stack in a `PitchedStackPanel` so they land on the mockup's pixels.
- `Icon Kind="..."` draws one of the mockups' icons in its `Foreground`. Kinds: Logo, Bell, Chat, Board, Sliders, Layers,
  Sparkle, Clock, Chip, Shield, Plug, Send, Lock, Lines, Terminal, Paperclip, Question, Warning, PopOut, Cloud, File,
  ChevronDown, Search, Globe, Menu, More, Check, and PanelRight (the dock toggle, which the mockups lack). To add one,
  copy the 16 px path from the `.dc.html`, write circles and rectangles as arcs, and add it to `IconPaths`.

## Fonts

| Files | Family | Source | Licence |
| --- | --- | --- | --- |
| `Inter-Regular.ttf`, `Inter-Medium.ttf`, `Inter-SemiBold.ttf` | Inter 4.1, the static TTFs of `extras/ttf` | [rsms/inter v4.1](https://github.com/rsms/inter/releases/tag/v4.1), `Inter-4.1.zip`, sha256 `9883fdd4a49d4fb66bd8177ba6625ef9a64aa45899767dde3d36aa425756b11e` | `Inter-OFL.txt` |
| `JetBrainsMono-Regular.ttf`, `JetBrainsMono-Medium.ttf` | JetBrains Mono 2.304, `fonts/ttf` | [JetBrains/JetBrainsMono v2.304](https://github.com/JetBrains/JetBrainsMono/releases/tag/v2.304), `JetBrainsMono-2.304.zip`, sha256 `6f6376c6ed2960ea8a963cd7387ec9d76e3f629125bc33d1fdcd7eb7012f7bbf` | `JetBrainsMono-OFL.txt` |

## Boundaries

`tools/Corvid.Architecture` holds the app's rules, with tests in `tests/Corvid.Architecture.Tests`: Uno's packages appear
in `Corvid.App` only, from an explicit list of what Uno.Sdk's implicit packages resolve (another list for the WebAssembly
target); `Corvid.App.Core` takes no package but Markdig and no project but `Corvid.Abstractions`; only the head's desktop
target and `Corvid.App.Engine` reach `Corvid.Host`, and the WebAssembly target never does. No CommunityToolkit, MVUX,
Uno.Extensions or Uno Toolkit: Hot Design (which brings them) and the in-app MCP endpoint are turned off in the project.
The WebAssembly target pins `System.Security.Cryptography.Pkcs` and `.Xml` to the patched release over the 10.0.12 that
Uno.Sdk's `Microsoft.Windows.Compatibility` brings (Directory.Packages.props); the desktop graph has neither.
