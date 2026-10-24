#!/usr/bin/env bash
# Drives a desktop app on a private Xvfb display: start the display, launch the app, resize, screenshot, click, type, stop.
# Every subcommand takes the display number first, so parallel agents never share a screen.
#
#   ui-harness.sh start  <n>                          start Xvfb :n (2560x1600x24) if it is not running
#   ui-harness.sh launch <n> <WxH> -- <command...>    run the command on :n, wait for its window, size it to WxH at 0,0
#   ui-harness.sh resize <n> <WxH>                    resize the launched window
#   ui-harness.sh shot   <n> <out.png>                capture the launched window's area (falls back to the whole screen)
#   ui-harness.sh click  <n> <x> <y> [button]         click at window-relative x,y (button 1 by default, 3 = right)
#   ui-harness.sh move   <n> <x> <y>                  move the pointer to window-relative x,y (hover)
#   ui-harness.sh scroll <n> <x> <y> <up|down> [count] wheel scroll at window-relative x,y
#   ui-harness.sh type   <n> <text>                   type text into the focused control
#   ui-harness.sh key    <n> <keys...>                send keys, xdotool names: Return, Escape, ctrl+Return, shift+Tab, ctrl+k
#   ui-harness.sh alive  <n>                          exit 0 when the launched app is still running
#   ui-harness.sh log    <n>                          print the last 60 lines of the app's stdout and stderr
#   ui-harness.sh stop   <n> [--xvfb]                 stop the app (and the display with --xvfb)
#
# For the Corvid app, launch with CORVID_APP_RENDERER=software: under Xvfb, Uno's OpenGL path can keep a stale frame
# after a shrinking resize. Needs Xvfb, xdotool, xdpyinfo, xwininfo and ImageMagick (apt: xvfb xdotool x11-utils imagemagick).
#
# State lives in $UI_HARNESS_STATE (default $XDG_RUNTIME_DIR/ui-harness-$UID, private to the user): xvfb-<n>.pid,
# app-<n>.pid, win-<n>.id, app-<n>.log. Processes are stopped by the PID recorded at launch, never by pattern, so a stop
# can never kill the calling shell. Numbers are checked before they reach arithmetic or paths.
set -euo pipefail

STATE="${UI_HARNESS_STATE:-${XDG_RUNTIME_DIR:-/tmp}/ui-harness-$UID}"
mkdir -p -m 700 "$STATE"; chmod 700 "$STATE"
[[ -O "$STATE" ]] || { echo "state directory $STATE is not owned by you" >&2; exit 2; }
cmd="${1:-}"; shift || true
n="${1:-}"; shift || true
[[ -n "$cmd" && -n "$n" ]] || { sed -n '2,23p' "$0"; exit 2; }
[[ "$n" =~ ^[0-9]+$ ]] || { echo "the display must be a number: $n" >&2; exit 2; }
int() { [[ "$1" =~ ^-?[0-9]+$ ]] || { echo "not a whole number: $1" >&2; exit 2; }; }
count() { [[ "$1" =~ ^[1-9][0-9]*$ ]] || { echo "not a positive count: $1" >&2; exit 2; }; }
wxh() { [[ "$1" =~ ^[0-9]+x[0-9]+$ ]] || { echo "not a size like 1440x900: $1" >&2; exit 2; }; }
export DISPLAY=":$n"

alive_pid() { [[ -f "$1" ]] && kill -0 "$(cat "$1")" 2>/dev/null; }
win() { cat "$STATE/win-$n.id" 2>/dev/null || true; }
geometry() { # prints X Y W H of the launched window, or the whole screen
  local w; w=$(win)
  if [[ -n "$w" ]] && xwininfo -id "$w" >/dev/null 2>&1; then
    xwininfo -id "$w" | awk '/Absolute upper-left X/{x=$4}/Absolute upper-left Y/{y=$4}/Width:/{w=$2}/Height:/{h=$2}END{print x,y,w,h}'
  else
    xwininfo -root | awk '/Width:/{w=$2}/Height:/{h=$2}END{print 0,0,w,h}'
  fi
}

case "$cmd" in
  start)
    if ! alive_pid "$STATE/xvfb-$n.pid"; then
      Xvfb ":$n" -screen 0 2560x1600x24 -nolisten tcp -dpi 96 >"$STATE/xvfb-$n.log" 2>&1 &
      echo $! >"$STATE/xvfb-$n.pid"
      for _ in $(seq 1 50); do xdpyinfo >/dev/null 2>&1 && break; sleep 0.1; done
    fi
    echo "display :$n ready"
    ;;
  launch)
    size="$1"; shift; wxh "$size"
    [[ "${1:-}" == "--" ]] && shift
    alive_pid "$STATE/xvfb-$n.pid" || "$0" start "$n" >/dev/null
    if alive_pid "$STATE/app-$n.pid"; then echo "an app is already running on :$n; stop it first" >&2; exit 1; fi
    ( exec "$@" ) >"$STATE/app-$n.log" 2>&1 &
    pid=$!
    echo "$pid" >"$STATE/app-$n.pid"
    rm -f "$STATE/win-$n.id"
    w=""
    for _ in $(seq 1 120); do
      kill -0 "$pid" 2>/dev/null || { echo "the app exited during start-up; its log:" >&2; tail -40 "$STATE/app-$n.log" >&2; exit 1; }
      w=$(xdotool search --onlyvisible --pid "$pid" 2>/dev/null | tail -1 || true)
      # A launcher (dotnet run) owns no window itself: fall back to the window titled Corvid, never any window.
      [[ -z "$w" ]] && w=$(xdotool search --onlyvisible --name '^Corvid$' 2>/dev/null | tail -1 || true)
      [[ -n "$w" ]] && break
      sleep 0.5
    done
    [[ -n "$w" ]] || { echo "no window appeared within 60 s; app log:" >&2; tail -40 "$STATE/app-$n.log" >&2; exit 1; }
    echo "$w" >"$STATE/win-$n.id"
    xdotool windowmove "$w" 0 0 windowsize "$w" "${size%x*}" "${size#*x}" windowactivate --sync "$w" 2>/dev/null || true
    sleep 2
    echo "pid $pid window $w size $size on :$n"
    ;;
  resize)
    wxh "$1"; w=$(win); xdotool windowsize "$w" "${1%x*}" "${1#*x}"; sleep 1.5; echo "resized to $1"
    ;;
  shot)
    out="$1"; read -r x y w h < <(geometry)
    import -window root -crop "${w}x${h}+${x}+${y}" +repage "$out"
    echo "$out (${w}x${h})"
    ;;
  click)
    int "$1"; int "$2"; count "${3:-1}"
    read -r x y _ _ < <(geometry)
    xdotool mousemove $((x + $1)) $((y + $2)) click "${3:-1}"; sleep 0.4
    ;;
  move)
    int "$1"; int "$2"
    read -r x y _ _ < <(geometry)
    xdotool mousemove $((x + $1)) $((y + $2)); sleep 0.4
    ;;
  scroll)
    int "$1"; int "$2"; count "${4:-3}"
    read -r x y _ _ < <(geometry)
    b=5; [[ "${3:-down}" == "up" ]] && b=4
    xdotool mousemove $((x + $1)) $((y + $2)) click --repeat "${4:-3}" --delay 60 "$b"; sleep 0.4
    ;;
  type)
    xdotool type --delay 15 -- "$1"; sleep 0.3
    ;;
  key)
    xdotool key --delay 60 "$@"; sleep 0.4
    ;;
  alive)
    alive_pid "$STATE/app-$n.pid"
    ;;
  log)
    tail -60 "$STATE/app-$n.log" 2>/dev/null || true
    ;;
  stop)
    if alive_pid "$STATE/app-$n.pid"; then
      pid=$(cat "$STATE/app-$n.pid"); kill "$pid" 2>/dev/null || true
      for _ in $(seq 1 30); do kill -0 "$pid" 2>/dev/null || break; sleep 0.2; done
      kill -9 "$pid" 2>/dev/null || true
    fi
    rm -f "$STATE/app-$n.pid" "$STATE/win-$n.id"
    if [[ "${1:-}" == "--xvfb" ]] && alive_pid "$STATE/xvfb-$n.pid"; then kill "$(cat "$STATE/xvfb-$n.pid")" 2>/dev/null || true; rm -f "$STATE/xvfb-$n.pid"; fi
    echo "stopped on :$n"
    ;;
  *) sed -n '2,23p' "$0"; exit 2 ;;
esac
