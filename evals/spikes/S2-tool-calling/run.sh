#!/usr/bin/env bash
# Spike S2 (C-034): the tool-calling golden set with Qwen3.5-0.8B and Kev-0.8B on the RTX 3090. Usage:
#   run.sh build
#   run.sh run --out <dir> [corvid-evals tool-calling options, e.g. --configs qwen-all,shortlist,shortlist-kev --limit 20]
# Environment (defaults are the workstation's): DOTNET, OUT (build directory), GPU_UUID (the RTX 3090; the 4060 Ti is
# nearly full), CUDNN_LIB (cuDNN 9 from a pip wheel; the CUDA 13 runtime comes from /opt/cuda via ldconfig), GPU_LOCK
# (the shared GPU lock: one model job at a time), BUILD_LOCK. The Corvid commit goes into the report header.
set -euo pipefail
here="$(dirname "$(readlink -f "$0")")"
repo="$(cd "$here/../../.." && pwd)"
dotnet="${DOTNET:-$HOME/.dotnet/dotnet}"
out="${OUT:-${TMPDIR:-/tmp}/corvid-s2-tool-calling}"
gpu="${GPU_UUID:-GPU-9e21a72e-3a6f-e082-3d74-4b8d361de70c}"
cudnn="${CUDNN_LIB:-$HOME/.cache/starling-bench-venvs/genai-cuda/lib/python3.11/site-packages/nvidia/cudnn/lib}"
gpu_lock="${GPU_LOCK:-/tmp/corvid-spikes-gpu.lock}"
build_lock="${BUILD_LOCK:-/tmp/corvid-spikes-build.lock}"
mkdir -p "$out"

case "${1:-}" in
  build)
    MSBUILDDISABLENODEREUSE=1 flock -o "$build_lock" "$dotnet" build "$here/Corvid.Spikes.ToolCalling.csproj" -c Release \
      --disable-build-servers -p:UseCuda=true -o "$out/build-cuda"
    exit 0 ;;
  run) shift ;;
  *) echo "usage: run.sh build | run.sh run --out <dir> [options]" >&2; exit 2 ;;
esac

export MALLOC_MMAP_THRESHOLD_=65536 DOTNET_gcServer=0
export CUDA_DEVICE_ORDER=PCI_BUS_ID CUDA_VISIBLE_DEVICES="$gpu" LD_LIBRARY_PATH="$cudnn${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
export CORVID_COMMIT="${CORVID_COMMIT:-$(git -C "$repo" rev-parse --short HEAD 2>/dev/null || echo unknown)}"
cd "$repo"
exec flock -o "$gpu_lock" "$dotnet" "$out/build-cuda/Corvid.Spikes.ToolCalling.dll" --set "$repo/evals/tool-calling" "$@"
