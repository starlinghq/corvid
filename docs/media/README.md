# The README's media

| File | What it is |
| --- | --- |
| `corvid-app-demo.gif` | the windowed app's demo, about 39 s, 1200 px wide |
| `app-approval.png` | `--demo --scene approval`: the approval card and the Decisions panel |
| `app-rewind.png` | the Rewind page, the last frame of the recording |
| `app-board.png` | `--demo --page board` |

All of it is `corvid-app --demo --theme dark` at 1440x900: the scripted scenario, demo services and the clock frozen at
2026-10-24 14:05, so no models, no Corvid home and nothing personal appear. The app ran on a headless X server (Xvnc,
standing in for Xvfb as [`docs/plan/ui-handoff.md`](../plan/ui-handoff.md) describes) with `CORVID_APP_RENDERER=software`
and `HOME` and the XDG directories pointed at an empty temporary directory. The window was sized to 1440x900, then one
pixel wider and back (Uno resizes itself to 1024x640 after start-up).

## Re-recording the GIF

The keystrokes, in order, sent with `xdotool` (or [`tools/ui-harness.sh`](../../tools/ui-harness.sh)'s `click`, `type`
and `key`) while `ffmpeg -f x11grab -draw_mouse 0 -framerate 20 -video_size 1440x900 -i :N` records:

1. Wait 2 s on the opening scene (turn 12 running its shell call).
2. Click "Jump to bottom" (660,661), then the composer (600,782). Type `keep the fix inside the test, not the launcher`
   and press `Ctrl+Enter` (a steer). Wait 6.5 s: turn 12 finishes, and the queued message starts turn 13, which asks
   approval a-32.
3. `Enter` focuses the card. Wait 3 s, then `Enter` answers "Allow once". Wait 3.5 s for the patch approval, a-33.
4. `Enter`, then `Down` and `4` ("Deny"). Wait 4.5 s for the turn to finish.
5. `Ctrl+K`, type `board`, `Enter`. Wait 3 s. `Ctrl+K`, type `rewind`, `Enter`. Wait 3.5 s.

Then a two-pass palette keeps the GIF near 4 MB:

```sh
ffmpeg -i take.mp4 -vf "fps=10,scale=1200:-1:flags=lanczos,split[a][b];[a]palettegen=max_colors=128:stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=5:diff_mode=rectangle" corvid-app-demo.gif
```

Re-record when the Sessions page, the approval card, the Board or Rewind change in a way the GIF would misstate.
