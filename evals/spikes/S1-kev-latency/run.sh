#!/usr/bin/env bash
# Spike S1: build both variants, then run one (model, device) per process. Usage:
#   run.sh build
#   run.sh <0.8b|4b> <cpu|cuda> [extra args, e.g. --calls 20 --cells ingress-600]
# Environment (defaults are the workstation's): DOTNET, OUT (build and result directory), GPU_UUID (the RTX 3090),
# CUDNN_LIB (cuDNN 9 from a pip wheel; the CUDA 13 runtime comes from /opt/cuda via ldconfig), GPU_LOCK, BUILD_LOCK.
set -euo pipefail
here="$(dirname "$(readlink -f "$0")")"
dotnet="${DOTNET:-$HOME/.dotnet/dotnet}"
out="${OUT:-${TMPDIR:-/tmp}/corvid-s1}"
gpu="${GPU_UUID:-GPU-9e21a72e-3a6f-e082-3d74-4b8d361de70c}"
cudnn="${CUDNN_LIB:-$HOME/.cache/starling-bench-venvs/genai-cuda/lib/python3.11/site-packages/nvidia/cudnn/lib}"
gpu_lock="${GPU_LOCK:-/tmp/corvid-s1-gpu.lock}"
build_lock="${BUILD_LOCK:-/tmp/corvid-s1-build.lock}"
mkdir -p "$out"

if [[ "${1:-}" == build ]]; then
  for variant in cpu cuda; do
    cuda=false; [[ $variant == cuda ]] && cuda=true
    MSBUILDDISABLENODEREUSE=1 flock -o "$build_lock" "$dotnet" build "$here/Corvid.Spikes.KevLatency.csproj" -c Release \
      --disable-build-servers -p:UseCuda=$cuda -o "$out/build-$variant"
  done
  exit 0
fi

model="$1"; device="$2"; shift 2
export MALLOC_MMAP_THRESHOLD_=65536 DOTNET_gcServer=0
if [[ $device == cuda ]]; then
  export CUDA_DEVICE_ORDER=PCI_BUS_ID CUDA_VISIBLE_DEVICES="$gpu" LD_LIBRARY_PATH="$cudnn${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
else
  export CUDA_VISIBLE_DEVICES=""
fi
exec flock -o "$gpu_lock" "$dotnet" "$out/build-$device/Corvid.Spikes.KevLatency.dll" \
  --model "$model" --device "$device" --out "$out/s1-$model-$device.jsonl" "$@"
