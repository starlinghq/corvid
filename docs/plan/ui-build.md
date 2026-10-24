# Building the windowed UI: the shared spec

The rules every builder of the app follows, person or agent, so that work done in parallel fits together. The design is
[`ui.md`](ui.md); the approved mockups are in [`ui-mockups/`](ui-mockups/README.md); where the build stands and what
comes next is in [`ui-handoff.md`](ui-handoff.md); the app's own conventions (areas, composition, theme, type, icons,
gotchas) are in [`src/Corvid.App/README.md`](../../src/Corvid.App/README.md).

## 1. Goal

Build the windowed Corvid on Uno Platform as `ui.md` designs it (read §0, §3, §4, §5, §6, §7 and §8 before coding). The
built screens must look like the mockups and behave as the design's flows (§5) say. Every step is checked by running the
app and looking at it.

## 2. References

| What | Where |
| --- | --- |
| Design | `docs/plan/ui.md`: features §4, flows §5, keys §6, visual language §7, architecture §8 |
| Mockup sources (exact copy, sizes, colours) | `docs/plan/ui-mockups/source/<Artboard>.dc.html` and `source/corvid.css` |
| Mockup renders | `docs/plan/ui-mockups/png/<Artboard>.png` (1440x900), `<Artboard>-narrow.png` (820x900), `Phone.png` (390x844) |
| Engine code the UI maps from | `src/Corvid.Abstractions/Events/SessionEvent.cs`, `src/Corvid.Acp/TurnReporter.cs`, `ToolPresentation.cs`, `PermissionPrompter.cs`, `src/Corvid.Approvals/ApprovalPipeline.cs`, `src/Corvid.Host/Commands/*.cs`, `src/Corvid.Host/Composition/CorvidRuntime.cs`, `src/Corvid.Host/Telemetry/TurnFeed.cs`, `src/Corvid.Host/Diagnostics/Doctor.cs` |
| The terminal design's shared update contract and kernel work (K-1 to K-10) | `docs/plan/tui.md` §3.2 and §9 |

## 3. Environment

- The .NET SDK from `global.json` (10.0.401 or a later 10.0.4xx) and network access to nuget.org. Uno.Sdk 6.7.30 comes
  from NuGet through `global.json`'s `msbuild-sdks`; no Uno template or workload is needed for the desktop head.
- The desktop head (`net10.0-desktop`) runs on Linux (X11), Windows and macOS. Demo mode needs no models and no Vault,
  so it runs anywhere. The WebAssembly head needs the `wasm-tools` workload (UNOWA0001 without it), which CI does not
  have, so it is opt-in (`-p:CorvidAppWasm=true`) and never part of the default build.
- Screenshot Release builds: a Debug build talks to Uno's dev server.

## 4. Repository rules

- `Directory.Build.props`: TreatWarningsAsErrors, Nullable and ImplicitUsings on, central package management. Every
  project builds with no warnings in Release. Never disable NuGet audit; pin a patched version centrally instead.
- `tools/Corvid.Architecture` reads `obj/project.assets.json` of every project under `src/`, `vault/` and `sandbox/`. A
  new project or package needs an explicit, reviewed rule there and a test in `tests/Corvid.Architecture.Tests`. The
  app's rules: only the desktop head and `Corvid.App.Engine` reference `Corvid.Host`; the WebAssembly target never does;
  Uno packages appear in `Corvid.App` only; `Corvid.App.Core` takes no package but Markdig.
- Packages: Uno.Sdk's implicit packages in `Corvid.App`, Markdig in `Corvid.App.Core`, the pinned security patches.
  Nothing else: no CommunityToolkit, MVUX, Uno.Extensions or Uno Toolkit.
- Tests: xunit.v3 projects (`OutputType Exe`, `xunit.v3`, `<Using Include="Xunit" />`), run with `dotnet test --project
  <csproj> -c Release`. Pass `TestContext.Current.CancellationToken` to every call that takes a token (analyzer
  xUnit1051 is an error here). Never skip or weaken a test to get green.
- Style: match the repo: file-scoped namespaces, sealed classes and records by default, XML doc comments that say why.
- CI (`.github/workflows/ci.yml`) builds `Corvid.slnx` in Release, runs the checker, then the tests, on a self-hosted
  Arch runner with no display and no workloads. Nothing may need a display in CI.
- Commit messages follow `git log` (`App: <what> (<step>)`) and carry no Claude attribution: no `Co-Authored-By`,
  `Claude-Session` or "Generated with" lines, and no claude.ai links, in commits or pull request bodies.
- Memory: this machine shares its RAM and CPU with CI runners and a model server. Before every `dotnet build` or `dotnet
  test`, run `agent-budget level`; if it prints `critical`, wait (`agent-budget alert --from critical --timeout 300`);
  if `tight`, pass `-m:2`. Spawn no more parallel builders than `agent-budget -q` allows.

## 5. Architecture

| Project | Role |
| --- | --- |
| `src/Corvid.App.Core` | net10.0, no Uno types: the update contract, the fold, view models, per-area services and DTOs, the demo services, the markdown and diff models. Tested headless. |
| `src/Corvid.App` | the Uno.Sdk single project: `net10.0-desktop` always, `net10.0-browserwasm` on request. Pages, controls, theme, fonts, icons, keyboard. |
| `src/Corvid.App.Engine` | to come: the in-process services over `CorvidRuntime`, referencing `Corvid.Host` as `Corvid.Acp` does. |
| `tests/Corvid.App.Core.Tests` | the core's tests. |

- MVVM on hand-written `ObservableObject`, `RelayCommand` and `AsyncRelayCommand`; pages bind with `x:Bind`; the core
  posts to the UI thread through `IDispatcher`. View models change only on the UI thread.
- Areas: each rail destination owns `src/Corvid.App.Core/<Area>/`, `AppComposition.<Area>.cs` and
  `src/Corvid.App/Pages/<Area>Page.xaml`. Builders working on different areas never edit the same file; a change to a
  shared file is additive and called out.
- Command line: `--demo`, `--theme light|dark|system`, `--page <area>`, `--session <id>`, `--scene main|approval`. Demo
  mode freezes the clock at 2026-10-24 14:05 local so screenshots are stable.
- Theme brushes are named after `corvid.css`'s tokens (`CorvidBg` to `CorvidDelWord`, plus `CorvidOnAccent`) with the
  exact values; Inter for prose and JetBrains Mono for code, embedded with their OFL licences; the mockups' stroke icons
  through the one `Icon` control; no emoji.
- Layout: rail 260 px, content, dock 380 px, status bar 28 px. The dock goes to an overlay under 1180 px, the rail under
  860 px, and under 720 px the phone layout with its bottom bar takes over.
- Accessibility: every icon-only button has `AutomationProperties.Name`; headings set
  `AutomationProperties.HeadingLevel`; tab order follows reading order; colour never carries meaning alone; streaming
  never moves focus or the scroll position.
- Keyboard: `ui.md` §6.

## 6. Looking at the running app

Every change to a screen is checked on the running app, one of two ways:

- **On a desktop, with computer use.** Run `dotnet src/Corvid.App/bin/Release/net10.0-desktop/Corvid.App.dll --demo
  --page <area>`, size the window to 1440x900, and take screenshots and click through computer use. One builder at a
  time per screen: parallel builders would fight over one display.
- **Headless on Linux.** `tools/ui-harness.sh` runs the app on a private Xvfb display per builder: `launch <n> 1440x900
  -- dotnet <dll> --demo`, then `shot`, `click`, `type`, `key`, `resize` and `stop <n> --xvfb`. Set
  `CORVID_APP_RENDERER=software`. Builders in parallel use different display numbers. Never stop processes with `pkill
  -f` or `pgrep -f` on a pattern that is also in your own command line: it kills your own shell.

The protocol, either way:

1. Build Release and open the screen at 1440x900 with `--demo`. Screenshot it and compare it with the matching render in
   `ui-mockups/png/`: layout, spacing, copy, colours, states, alignment, clipping.
2. Drive the interactions the screen owns, screenshot after each, and confirm the change you expected. Read the app's
   standard error for exceptions.
3. Check the dark theme and the widths 1024x900, 820x900 and 390x844.
4. Fix what differs and repeat. Report what still differs; never claim a check you did not run.
5. Build the whole solution, run the checker and the tests, commit.
