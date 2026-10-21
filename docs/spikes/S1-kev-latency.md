# Spike S1: Kev checkpoint latency

C-003, spike S1 of [the plan](../plan/corvid-agent.md#phase-0-prove-the-fabric-about-2-weeks), run 2026-10-21.

**Question.** How long do Kev-0.8B and Kev-4B take on the RTX 3090 and on the workstation CPU for turn-states of 300,
600 and 1,500 tokens with 4–8 questions and rows up to 1,000 tokens, and what does a one-question `pre-execute`
checkpoint cost?

**Pass condition.** An ingress checkpoint at p50 ≤ 300 ms on GPU with Kev-0.8B. If it only fits on CPU at ≥ 1 s, reduce
checkpoints to ingress + egress and drop `step` questions to two.

## Result

**Mixed, provisional pass for a 4-question ingress, fail for 8 questions.** Kev-0.8B on the 3090, 600-token state:

| Ingress, 600 tokens | Run 1 p50 | Run 2 p50 | Best call (both runs) |
| --- | --- | --- | --- |
| 4 questions | **284 ms** (load 21) | **258 ms** (load 47–57) | 237 ms |
| 8 questions | **412 ms** (load 18–21) | **1,088 ms** (load 38–81) | 352 ms |
| `pre-execute`, one `risk` question (260-token row) | 253 ms | 706 ms (load 62–76) | 208 ms |

- A 4-question ingress (`intent`, `difficulty`, `clarify`, `needs_recall`, which is what `TurnRunner` asks with no tool or
  skill shortlist) meets the 300 ms line in both runs, by 16 and 42 ms. An 8-question ingress does not, even at its best
  call (352 ms). `TurnRunner` asks 3 to 6 questions today; with a tool and a skill shortlist (6 questions) the cost sits
  between the two cells, so a typical ingress with tools is over the line.
- The workstation was heavily loaded by other jobs throughout (1-minute load average 9 to 83 on 12 hardware threads; see
  [Contamination](#contamination)). Every number here is an upper bound on what an idle machine would give, and the
  spread between the two runs is load, not the model. The verdict is **provisional** until the four decisive cells
  (0.8B CUDA, 600 tokens × 4 and 8 questions, 300 × 4, `pre-execute`) are rerun on an idle machine.
- On the CPU, Kev-0.8B is far above 1 s for every checkpoint (see [CPU](#cpu)), so the plan's CPU branch applies to
  CPU-only installs.
- Kev-4B on the 3090 takes 0.7–1.3 s for a 600-token checkpoint: usable as the second tier for gates below their floor
  (§5.5, rare by construction), not as the default decider.

### Decision it triggers

1. **Place Kev-0.8B on the 3090** when one is present, and keep ingress at 600 tokens or less with at most four or five
   questions per request. That needs a CUDA path in `KevDecider.Create`, which C-004b deferred (it throws for any
   non-CPU provider; this spike bypassed it, see [Method](#method)).
2. **On CPU-only hosts, reduce checkpoints** to ingress + egress and drop `step` questions to two, as the plan's
   fallback says: no CPU cell came in under 1 s.
3. **Kev-4B stays the gate escalation tier** on the GPU; on CPU it is too slow for interactive use.
4. **The CUDA profile is not GPU-bound.** Mean GPU utilisation while the model was loaded was 31% (0.8B) and 37% (4B),
   and latency moved with the CPU load. Starling's Kev README explains why: the CUDA profiles run the builder's CPU
   export on the CUDA provider, ONNX Runtime keeps the `GroupQueryAttention` nodes on the CPU provider, and the state
   cache passes through host memory for every row. The builder's own CUDA graph (`-e cuda -p fp32`) is faster but did
   not load unpatched with ONNX Runtime GenAI 0.17.1. Getting attention onto the GPU is the lever for the 8-question
   case, and it is Starling work.

New backlog items are proposed in the plan (C-022 to C-024); none is implemented here.

## Setup

| | |
| --- | --- |
| Machine | Intel Core i7-5820K (6 cores, 12 threads, 3.3 GHz), 94 GB RAM, Linux 7.2.5 (Omarchy) |
| GPU | NVIDIA GeForce RTX 3090, 24 GB, UUID `GPU-9e21a72e-3a6f-e082-3d74-4b8d361de70c` (the only GPU used; the 4060 Ti was not touched), driver 610.57.04, CUDA 13.3 runtime from `/opt/cuda`, cuDNN 9 from the `nvidia-cudnn` pip wheel on `LD_LIBRARY_PATH` |
| .NET | SDK 10.0.401, runtime 10.0.12, Release build |
| Packages | `Starlings.Models.Kev` 0.3.0 and `Starlings.Models.Qwen35` 0.3.0 from nuget.org; `Microsoft.ML.OnnxRuntime` 1.30.0 (CPU build) or `Microsoft.ML.OnnxRuntime.Gpu.Linux` 1.30.0 (CUDA build) |
| Profiles | `kev-0.8b/onnx-fp32-cpu`, `kev-0.8b/onnx-fp32-cuda`, `kev-4b/onnx-fp32-cpu`, `kev-4b/onnx-fp32-cuda`; the cached bundles under `~/.cache/starlings/models/jaredpalmer/` opened offline with size verification (as `StarlingModelStore.OpenKev` does); calibrated temperature; CUDA calls serialized (the profile default) |
| Threads | ONNX Runtime defaults (Starling sets no intra-op count on an unconstrained host, so ONNX Runtime uses one thread per physical core, 6) |
| Environment | `MALLOC_MMAP_THRESHOLD_=65536` (Starling's Kev README), `CUDA_DEVICE_ORDER=PCI_BUS_ID`, `CUDA_VISIBLE_DEVICES=<3090 UUID>` for CUDA and empty for CPU; every model process under `flock` on the shared GPU lock, one at a time |
| Corvid | `main` at `5a9e534` |

Commands (from the repository root; `run.sh` sets the environment above and takes the lock):

```sh
evals/spikes/S1-kev-latency/run.sh build                                   # CPU and CUDA variants, separate output dirs
evals/spikes/S1-kev-latency/run.sh 0.8b cuda --calls 100 --warmup 10      # run 1
evals/spikes/S1-kev-latency/run.sh 4b cuda --calls 50 --warmup 5
evals/spikes/S1-kev-latency/run.sh 0.8b cpu --calls 50 --warmup 5 --cells ingress-600-q4   # run 2: one process per cell
python3 -I evals/spikes/S1-kev-latency/table.py <results>.jsonl           # the tables below
```

### Method

The harness (`evals/spikes/S1-kev-latency/`) measures Corvid's own path, not a raw Kev call:

- **State.** `TurnStateBuilder` with `Qwen35TokenCounter` over the Qwen3.5-0.8B `tokenizer.json`, the counter
  `TurnRunner` uses with a Starling generator. The input is oversized (a request with a pasted log, 24 recent
  one-liners, a goal, four entity labels and a last tool result) and the builder cuts it to the budget: 300
  (`TurnStateBudget(Total: 300)`), 600 (the default) and 1,500 (`Total` and `HardStop` 1,500, field budgets raised).
  Actual sizes by Corvid's counter: **297, 560 and 1,496 tokens**; Kev's own state, with its markers, is 307, 601 and
  1,559 tokens. No state was over the hard stop.
- **Questions.** The §5.3 catalogue through `QuestionCatalogue.ToQuestion`, in `TurnRunner`'s ingress order: 4 =
  `intent`, `difficulty`, `clarify`, `needs_recall`; 8 = those plus `tool` (8 tools + `none`, `name: description`),
  `skill` (4 skills + `none`), `injection`, `delegate`. Kev rows: 17–83 tokens for 4 questions, 17–113 for 8.
  `pre-execute` is one `risk` question whose instructions are a `RiskDocument`-shaped JSON (question plus the analysed
  action for `rm -rf build/ dist/ && git push --force origin main`), as `ApprovalPipeline` sends it; `ActionDocuments`
  is internal to `Corvid.Approvals`, so the spike writes the same snake_case shape itself. Its row is 260 tokens.
- **Row sweep.** The same `risk` question with the command padded with more targets until Kev's row is as close as
  fits to 500, 750 and 1,000 tokens (456, 750 and 962), on the 600-token state. The 250 target could not go below the
  base row (260), so `rows-600-risk-250` repeats `pre-execute`.
- **Call.** `DecisionFabric.DecideAsync` with `DecisionPolicy.Default`, a `SqliteEventLog` on disk (the append of one
  `Decision` per question is in the timing) and `KevDecider` as the calibrated tier. Every call is checked: each
  question answered by Kev itself, no fallback, nothing declined; otherwise the run stops.
- **KevDecider on CUDA.** `KevDecider.Create` refuses a non-CPU provider, so the spike compiles
  `src/Corvid.Models.Starling/KevDecider.cs` in (linked source) and builds the decider through its internal
  constructor over a `KevDecisionModel` created with the CUDA provider. `Create` does exactly this on the CPU, minus the
  guard, so `Decide` is the adapter's own code in both cases. The same construction is used on the CPU for symmetry.
- **Timing.** Per cell: warm-up calls excluded (10 for 0.8B CUDA, 5 otherwise; each cell is a new shape), then 50–100
  timed calls (Kev-4B CPU: 20, stated below). Three variants of the input rotate. Reported: nearest-rank p50, p90, p99,
  mean and min of the whole fabric call, plus the Kev-only p50 (the decider call). The fabric's own overhead (policy and
  SQLite append) is 5–15 ms; Kev's tokenization is 1–3 ms; the rest is the graph runs. Load time is per process.

### Spike placement

The project is not in `Corvid.slnx`, so `dotnet build Corvid.slnx` and CI never restore it, and the CUDA natives never
meet Corvid's CPU-only packages. It sits under `evals/`, which the dependency-boundary check does not scan (it reads
`src/`, `vault/` and `sandbox/` only), so no exclusion was needed. Its two extra package versions (`Microsoft.ML.OnnxRuntime`,
`Microsoft.ML.OnnxRuntime.Gpu.Linux`, and `Starlings.Models.Qwen35` for the linked counter) are declared in the spike's
own project file, not in `Directory.Packages.props`. `-p:UseCuda=true` swaps the native package as Starling's Kev
fidelity tool does.

## Load times

| Model | CUDA | CPU |
| --- | --- | --- |
| Kev-0.8B | 1.3–6.1 s (2.8 s first load) | LOAD_08_CPU |
| Kev-4B | 15–22 s | LOAD_4_CPU |

The device memory on the 3090 rose from 298 MiB idle to 4,157–4,711 MiB with Kev-0.8B loaded and to 17,991–19,015 MiB
with Kev-4B, and fell back to 298 MiB between processes (`nvidia-smi --id=<UUID>` every 2 s; excerpts in
`evals/spikes/S1-kev-latency/results/`):

```text
timestamp, uuid, name, memory.used [MiB], utilization.gpu [%]
2026/10/21 08:17:30.889, GPU-9e21a72e-3a6f-e082-3d74-4b8d361de70c, NVIDIA GeForce RTX 3090, 298 MiB, 0 %
2026/10/21 08:17:32.893, GPU-9e21a72e-3a6f-e082-3d74-4b8d361de70c, NVIDIA GeForce RTX 3090, 4157 MiB, 67 %
...
(Kev-4B run 1) 378 samples at 19015 MiB, 93 at 17991 MiB
```

## Results

All times in milliseconds for one whole checkpoint (`DecisionFabric.DecideAsync`). "Load" is the 1-minute load
average before and after the cell.

### GPU (RTX 3090), run 1: all cells in one process per model

GPU_RUN1

### GPU (RTX 3090), run 2: one process per cell

Run 2 waited for the load average to fall below 6 before each cell; it never did (the first cell waited 20 minutes
and started at load 61), so the wait was cut to a minute and the cells ran under whatever load there was.

GPU_RUN2

### CPU

CPU_TABLE

### Reading the numbers

- **State size dominates.** With 4 questions on 0.8B CUDA, 300 → 600 → 1,500 tokens costs about 226 → 284 → 718 ms
  (run 1 p50). A 1,500-token state is over 300 ms on every device and model.
- **A one-question checkpoint costs almost as much as four.** `pre-execute` (one 260-token row) is 253 ms against
  284 ms for 4 short rows on the same state: the state pass is most of the cost, and Kev does not reuse a state across
  requests (Starling: unsupported). A `pre-execute` checkpoint per side-effecting tool call therefore costs about a
  quarter of a second on the GPU even when the ingress state is unchanged.
- **Rows.** On 0.8B CUDA (run 1) the row sweep adds about 0.45 ms per row token above the base row: 253 ms at 260
  tokens, 352 at 456, 468 at 750, 570 at 962. Rows up to 1,000 tokens fit KevDecider's row budget; none was declined.
- **8 questions.** The `tool` and `skill` rows (shortlists with descriptions, 100–113 tokens) are the long ones; going
  from 4 to 8 questions added 100–130 ms in run 1.
- **Kev-4B on GPU** is 2.4–2.9× Kev-0.8B for the same cell in run 1 (691 against 284 ms at 600 × 4).

## Contamination

Other agents' builds and test suites ran on the workstation for the whole spike (all at the same nice level as the
spike), with 1-minute load averages of 9 to 83 on 12 hardware threads. At least one other model job
(`S3.Gliner2Targets`, GLiNER2 on the CPU) ran while Kev-4B was on the GPU, so the one-model-at-a-time rule was not
holding on this machine. Because the CUDA profile runs attention and the state-cache copies on the CPU (above), the GPU
cells are load-sensitive too: the same 0.8B 600 × 8 cell has p50 412 ms at load 18–21 and 1,088 ms at load 38–81. The
`min` column is the closest thing to an idle-machine number this spike has.

**Follow-up:** rerun the decisive cells on an idle machine before acting on a 16–42 ms margin. The harness does it in
four commands:

```sh
for c in ingress-600-q4 ingress-600-q8 ingress-300-q4 pre-execute-600-risk; do
  evals/spikes/S1-kev-latency/run.sh 0.8b cuda --cells "$c" --calls 100 --warmup 10
done
```

## Files

- `evals/spikes/S1-kev-latency/`: the harness (`Program.cs`, project, `run.sh`, `table.py`), not in `Corvid.slnx`.
- `evals/spikes/S1-kev-latency/results/`: one JSON line per process (`load`) and per cell (`cell`, with every
  percentile of the fabric, Kev-only, encode and run times, the state and row sizes, and the load average), the console
  logs and the `nvidia-smi` logs.
