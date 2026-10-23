#!/usr/bin/env bash
# Spike S4 (C-038): Kev's tool and done accuracy with the 600-token TurnState (shortlist-kev) against a transcript-sized
# one (shortlist-kev-transcript), on S2's harness and CUDA wrapper, which this only delegates to. Usage:
#   run.sh build
#   run.sh history --out <dir> [options]   the gating run: the history cases in both arms
#   run.sh all --out <dir> [options]       the no-harm control: every case in both arms
# Options and environment are S2's (evals/spikes/S2-tool-calling/run.sh); the committed results are report.json,
# report.md and the console output of each run (results/ for history, results-all/ for all).
set -euo pipefail
here="$(dirname "$(readlink -f "$0")")"
s2="$here/../S2-tool-calling/run.sh"
arms="shortlist-kev,shortlist-kev-transcript"

case "${1:-}" in
  build) exec "$s2" build ;;
  history) shift; exec "$s2" run --configs "$arms" --tags history "$@" ;;
  all) shift; exec "$s2" run --configs "$arms" "$@" ;;
  *) echo "usage: run.sh build | run.sh history --out <dir> [options] | run.sh all --out <dir> [options]" >&2; exit 2 ;;
esac
