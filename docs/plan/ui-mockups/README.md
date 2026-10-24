# Windowed UI mockups

Ten artboards of the design in [`../ui.md`](../ui.md), drawn before the app existed and approved as the target the app
is built to. They were made on a design canvas (claude.ai artifact `UkU7aRT487Ef6hKG6i8EDA`, private to its owner); the
sources here are that canvas's files, so the canvas is not needed to read or render them.

| Artboard | Shows |
| --- | --- |
| `Main` | a session in the Work layout: rail, transcript with decision chips, escalation and taint rows, tool cards and a running shell tail, the turn strip with a queued message, the composer, the dock's Changes tab, the status bar |
| `Approval` | the approval card with the parsed commands, reasons, five answers and the rule preview; the dock's Decisions tab |
| `Review` | changes since the last review point, a side-by-side diff with two drafted line comments |
| `Rewind` | the checkpoint ledger, a restore point with its three-way merge warning and the three restore modes |
| `Board` | sessions across two workspaces and the attention tray (dark) |
| `Models` | the seven slots with the escalation ladder, the catalogue with a download in progress, privacy, decider, placement |
| `Memory` | the review queue beside the nearest existing facts, core block budgets, a lesson candidate, the Hermes import |
| `Audit` | the event log with a detection match and the egress ledger's request as sent (dark) |
| `FirstRun` | the doctor's checks with fixes, model choices with sizes, workspace, approval mode and cloud privacy |
| `Phone` | the 390 px layout of an attached client answering an approval |

`png/` holds the renders the app is compared with: `<Artboard>.png` at 1440x900 (`Phone.png` at 390x844) and
`<Artboard>-narrow.png` at 820x900. To render them again (fonts come from Google Fonts, so this needs the network):

```sh
cd docs/plan/ui-mockups
npm install            # Playwright
python3 to-html.py     # source/*.dc.html into html/*.html, the theme each artboard declares filled in
node render.mjs        # html/*.html into png/
```

`source/corvid.css` holds the colour tokens the app's theme copies exactly (`.app` light, `.app.dark` dark).
