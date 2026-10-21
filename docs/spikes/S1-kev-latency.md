# Spike S1: Kev checkpoint latency

C-003, spike S1 of [the plan](../plan/corvid-agent.md#phase-0-prove-the-fabric-about-2-weeks), run 2026-10-21.

**Question.** How long do Kev-0.8B and Kev-4B take on the RTX 3090 and on the workstation CPU for turn-states of 300,
600 and 1,500 tokens with 4–8 questions and rows up to 1,000 tokens, and what does a one-question `pre-execute`
checkpoint cost?

**Pass condition.** An ingress checkpoint at p50 ≤ 300 ms on GPU with Kev-0.8B. If it only fits on CPU at ≥ 1 s, reduce
checkpoints to ingress + egress and drop `step` questions to two.

## Result

**Pass for a 4-question ingress; `TurnRunner`'s largest ingress (6 questions) is on the line at 600 tokens and passes
at 300; 8 questions fail.** Kev-0.8B on the 3090, whole fabric call, on the two quietest runs (run 3, load 4–12, and
run 4, the review rerun at load 3–6):

| Kev-0.8B, RTX 3090 | Run 4 p50 (p90) | Run 3 p50 (p90) | Runs 1 and 2 p50 (loaded machine) |
| --- | --- | --- | --- |
| Ingress, 600 tokens, 4 questions | **245, 220 ms** (262, 231) | **260 ms** (434) | 284, 258 ms |
| Ingress, 600 tokens, 6 questions (`TurnRunner`'s maximum) | **302, 301 ms** (313, 314) | not run | not run |
| Ingress, 600 tokens, 8 questions (stress bound) | **340 ms** (384) | **361 ms** (437) | 412, 1,088 ms |
| Ingress, 300 tokens, 4 questions | not run | **159 ms** (166) | 226, 438 ms |
| Ingress, 300 tokens, 6 questions | **212 ms** (228) | not run | not run |
| `pre-execute`, one `risk` question (260-token row) | not run | **249 ms** (510) | 253, 706 ms |

- `TurnRunner` asks 3 to 6 questions at ingress: `intent`, `difficulty`, `clarify`, then `needs_recall` when there is a
  retriever and the turn is not a guest's, `tool` when there is a tool shortlist and `skill` when there is a skill
  shortlist. The 8-question cell adds `injection` and `delegate`, which `TurnRunner` does not ask at ingress, so it is
  a stress bound, not a real checkpoint; the 6-question cell (added in the review rerun) is the realistic worst case
  for the `TurnRunner` measured here (`main` at `5a9e534`). Since MCP description pinning (backlog C-022, #38),
  `TurnRunner` on `main` also adds one `injection/<server>` question per MCP server whose tool descriptions are new to
  the session, with up to 1,200 characters of descriptions (a long row). The first turn that shortlists a server's
  tools can therefore ask 7 or more questions with a row of a few hundred tokens; this spike did not measure that case.
  An estimate from the row sweep (about 0.45 ms per row token): 6 questions at 301 ms plus a 300-token row is roughly
  430 ms at 600 tokens, over the line; such a turn is rare (once per new description set per session).
- A 4-question ingress (no tool or skill shortlist) meets the 300 ms line at 600 tokens in every run: p50 220–284 ms,
  55–80 ms under the line on the quiet machine. At 300 tokens it passed on the quieter runs (159 and 226 ms) but not
  under load 71–83 in run 2 (438 ms).
- A 6-question ingress (both shortlists: the two longest rows, 100–113 tokens) came in at **301.6 and 300.9 ms** in two
  separate processes at 600 tokens: on the line, formally a miss by 1–2 ms, and any machine load pushes it over. At
  300 tokens it is 212 ms. So the pass holds for every ingress `TurnRunner` asks only if the state stays nearer 300
  than 600 tokens when both shortlists are present, or the shortlist rows get shorter. Moving the shortlists to a
  request of their own does not help on the critical path: a second request pays the state pass again (a one-question
  request is about 250 ms), so 4 + 2 questions in two requests is about 500 ms.
- An 8-question ingress fails: 340–361 ms on the quiet machine, 412 ms and 1,088 ms on the loaded one.
- Tail latency depends on load. On the quiet rerun the 600-token p90 is within 15–45 ms of the p50 (231–384 ms); in
  run 3, with load rising to 11 during the cells, p90 was 434–510 ms. Under load one ingress in ten takes 430 ms or
  more.
- On the CPU, Kev-0.8B is over 1 s for every checkpoint (the quickest p50 is 2.6 s, a 300-token 4-question ingress),
  also on the quieter machine (`pre-execute` p50 3.0 s at load 5–6), so the plan's CPU branch applies to CPU-only installs. Kev-4B on the CPU takes about 15 s per checkpoint.
- Kev-4B on the 3090 takes 0.7–1.3 s for a 600-token checkpoint: usable as the second tier for gates below their floor
  (§5.5, rare by construction), not as the default decider.
- The workstation was shared with other jobs for most of the spike (load average up to 99 on 12 hardware threads; see
  [Contamination](#contamination)). Run 3 repeated the four decisive cells once the load had dropped (the driver
  launched each process at load 4.0–4.7; the cells' own records read 4.0–8.6 at their start and up to 11.8 at their
  end); its p50s are 4–67 ms below run 1's. Run 4, the review rerun at load 3.3–6.3, added the 6-question cell and
  repeated the 600-token cells; the verdict rests on runs 3 and 4.

### Decision it triggers

1. **Place Kev-0.8B on the 3090** when one is present, keep the ingress state at 600 tokens or less, and keep a
   checkpoint at four or five questions. That needs a CUDA path in `KevDecider.Create`, which C-004b deferred (it
   throws for any non-CPU provider; this spike bypassed it, see [Method](#method)). Six questions at 600 tokens are at
   301 ms, so with both shortlists present keep that state nearer 300 tokens (212 ms measured) or trim the shortlist
   descriptions. Splitting `tool` and `skill` into a second request costs another state pass (about 250 ms) and only
   helps if that request can run off the critical path. Proposed as C-029.
2. **On CPU-only hosts, reduce checkpoints** to ingress + egress and drop `step` questions to two, as the plan's
   fallback says: no CPU cell came in under 1 s. Proposed as C-030, not implemented.
3. **Kev-4B stays the gate escalation tier**, on the GPU only; on the CPU (about 15 s) it is not interactive.
4. **A `pre-execute` checkpoint costs about 250 ms** on the GPU, almost as much as a 4-question ingress, because the
   state pass dominates and Kev does not reuse a state across requests. A per-tool-call `pre-execute` adds a quarter
   of a second to every side-effecting tool call.
5. **The CUDA profile is not GPU-bound.** Mean GPU utilisation while the model was loaded was 31% (0.8B) and 37% (4B)
   in run 1, and 51% for 0.8B on the quiet rerun (run 4, including load and teardown samples), and latency moved with
   the CPU load. Starling's Kev README explains why: the CUDA profiles run the builder's CPU
   export on the CUDA provider, ONNX Runtime keeps the `GroupQueryAttention` nodes on the CPU provider, and the state
   cache passes through host memory for every row. The builder's own CUDA graph (`-e cuda -p fp32`) is faster but did
   not load unpatched with ONNX Runtime GenAI 0.17.1. A GPU-resident graph and state reuse across checkpoints are the
   levers for 8-question checkpoints and `pre-execute`; that is Starling work, proposed as C-031.

## Setup

| | |
| --- | --- |
| Machine | Intel Core i7-5820K (6 cores, 12 threads, 3.3 GHz), 94 GB RAM, Linux 7.2.5 (Omarchy) |
| GPU | NVIDIA GeForce RTX 3090, 24 GB, UUID `GPU-9e21a72e-3a6f-e082-3d74-4b8d361de70c` (the only GPU used; the 4060 Ti was not touched), driver 610.57.04, CUDA 13.3 runtime from `/opt/cuda`, cuDNN 9 from the `nvidia-cudnn` pip wheel on `LD_LIBRARY_PATH` |
| .NET | SDK 10.0.401, runtime 10.0.12, Release build |
| Packages | `Starlings.Models.Kev` 0.3.0 and `Starlings.Models.Qwen35` 0.3.0 from nuget.org; `Microsoft.ML.OnnxRuntime` 1.30.0 (CPU build) or `Microsoft.ML.OnnxRuntime.Gpu.Linux` 1.30.0 (CUDA build) |
| Profiles | `kev-0.8b/onnx-fp32-cpu`, `kev-0.8b/onnx-fp32-cuda`, `kev-4b/onnx-fp32-cpu`, `kev-4b/onnx-fp32-cuda`; the cached bundles under `~/.cache/starlings/models/jaredpalmer/` opened offline with size verification (as `StarlingModelStore.OpenKev` does); calibrated temperature; CUDA calls serialized (the profile default) |
| Threads | ONNX Runtime defaults (Starling sets no intra-op count on an unconstrained host, so ONNX Runtime uses one thread per physical core, 6) |
| Environment | `MALLOC_MMAP_THRESHOLD_=65536` (Starling's Kev README), `CUDA_DEVICE_ORDER=PCI_BUS_ID`, `CUDA_VISIBLE_DEVICES=<3090 UUID>` for CUDA and empty for CPU; every model process under `flock` on the lock `GPU_LOCK` names, one at a time (see [Contamination](#contamination) on which lock that was) |
| Corvid | `main` at `5a9e534` |

Commands (from the repository root; `run.sh` sets the environment above and takes the lock):

```sh
evals/spikes/S1-kev-latency/run.sh build                                   # CPU and CUDA variants, separate output dirs
evals/spikes/S1-kev-latency/run.sh 0.8b cuda --calls 100 --warmup 10      # run 1
evals/spikes/S1-kev-latency/run.sh 4b cuda --calls 50 --warmup 5
evals/spikes/S1-kev-latency/run.sh 0.8b cpu --calls 50 --warmup 5
evals/spikes/S1-kev-latency/run.sh 0.8b cuda --cells ingress-600-q4 --calls 100 --warmup 10   # runs 2 and 3: one process per cell
GPU_LOCK=<shared lock> evals/spikes/S1-kev-latency/run.sh 0.8b cuda --cells ingress-600-q6 --calls 100 --warmup 10   # run 4
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
  `intent`, `difficulty`, `clarify`, `needs_recall`; 6 = those plus `tool` (8 tools + `none`, `name: description`) and
  `skill` (4 skills + `none`), `TurnRunner`'s maximum; 8 = those plus `injection` and `delegate`, which `TurnRunner`
  does not ask at ingress (a stress bound). Kev rows: 17–83 tokens for 4 questions, 17–113 for 6 and 8. The 6-question
  cell was added in the review rerun (run 4).
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
- **Timing.** Per cell: warm-up calls excluded (10 for 0.8B CUDA, 1–5 otherwise; each cell is a new shape), then 50–100
  timed calls on the GPU. On the CPU fewer: 50 for the first five 0.8B cells, then 20 (a CPU call took 3–30 s on the
  loaded machine), and 10 for Kev-4B (about 15 s a call). Some CPU cells were not run (0.8B 1,500 × 8, the 500 and
  750 row sweeps; Kev-4B everything but 600 × 4 and `pre-execute`): every CPU cell that ran is already over 1 s by
  several times, so they would not change the decision. Three variants of the input rotate. Reported: nearest-rank p50, p90, p99,
  mean and min of the whole fabric call, plus the Kev-only p50 (the decider call). The fabric's own overhead (policy and
  SQLite append) is 5–15 ms; Kev's tokenization is 1–3 ms; the rest is the graph runs. Load time is per process.
  Runs 1–3 recorded only the summary statistics, so their percentiles cannot be recomputed from the JSONL; from run 4
  on, each cell record also carries every timed call (`fabric_samples_ms`), and recomputing nearest-rank p50, p90 and
  p99 from those samples gives the recorded values exactly.

### Spike placement

The project is not in `Corvid.slnx`, so `dotnet build Corvid.slnx` and CI never restore it, and the CUDA natives never
meet Corvid's CPU-only packages. It sits under `evals/`, which the dependency-boundary check does not scan (it reads
`src/`, `vault/` and `sandbox/` only), so no exclusion was needed. Its three extra package versions (`Microsoft.ML.OnnxRuntime`,
`Microsoft.ML.OnnxRuntime.Gpu.Linux`, and `Starlings.Models.Qwen35` for the linked counter) are declared in the spike's
own project file, not in `Directory.Packages.props`. `-p:UseCuda=true` swaps the native package as Starling's Kev
fidelity tool does.

## Load times

| Model | CUDA | CPU |
| --- | --- | --- |
| Kev-0.8B | 1.3–6.1 s (2.8 s first load) | 2.5–5.4 s |
| Kev-4B | 15–22 s | 25.7 s |

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

### GPU (RTX 3090), run 4: review rerun, load 3–6

Run by the PR review on a quiet machine, one process per cell under the shared GPU lock, `--calls 100 --warmup 10`,
with every timed call recorded. The 600-token 4- and 6-question cells were run twice, in separate processes.
`nvidia-smi` sampled the 3090 throughout (`nvidia-smi-0.8b-run4.csv`, 99 samples, only that UUID).

| Model | Device | Cell | State (Corvid / Kev tokens) | Rows (Kev tokens) | n | p50 | p90 | p99 | mean | min | Kev only p50 | Load before → after |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| kev-0.8b | cuda | ingress-600-q4 | 560 / 601 | 17–83 | 100 | 245 | 262 | 278 | 241 | 213 | 244 | 3.27 → 5.98 |
| kev-0.8b | cuda | ingress-600-q6 | 560 / 601 | 17–113 | 100 | 302 | 313 | 335 | 303 | 292 | 301 | 5.98 → 6.23 |
| kev-0.8b | cuda | ingress-600-q8 | 560 / 601 | 17–113 | 100 | 340 | 384 | 397 | 346 | 330 | 339 | 6.23 → 6.27 |
| kev-0.8b | cuda | ingress-300-q6 | 297 / 307 | 17–113 | 100 | 212 | 228 | 236 | 216 | 208 | 212 | 6.27 → 6.01 |
| kev-0.8b | cuda | ingress-600-q6 | 560 / 601 | 17–113 | 100 | 301 | 314 | 321 | 303 | 293 | 300 | 4.45 → 5.36 |
| kev-0.8b | cuda | ingress-600-q4 | 560 / 601 | 17–83 | 100 | 220 | 231 | 240 | 221 | 214 | 219 | 5.01 → 5.43 |

The minimum of the 6-question cell (292 ms) is only 8 ms under the line: its distribution sits on 300 ms.

### GPU (RTX 3090), run 3: the decisive cells, load 4–12

Run after the other jobs had mostly finished. The driver launched each process once the load average was below 5
(4.0–4.7 at launch); the cells' own records, taken after the model had loaded, read 4.0–8.6 at the start and up to
11.8 at the end. `nvidia-smi-0.8b-run3.csv` covers only the last cell (`pre-execute`), and its first sample (60%
utilisation at 282 MiB, before Kev had loaded) shows another process briefly on the 3090; for the three ingress cells
the device evidence is the process header (`kev-0.8b/onnx-fp32-cuda`, `CUDA_VISIBLE_DEVICES` = the 3090's UUID).

| Model | Device | Cell | State (Corvid / Kev tokens) | Rows (Kev tokens) | n | p50 | p90 | p99 | mean | min | Kev only p50 | Load before → after |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| kev-0.8b | cuda | ingress-600-q4 | 560 / 601 | 17–83 | 100 | 260 | 434 | 614 | 294 | 222 | 253 | 8.56 → 10.82 |
| kev-0.8b | cuda | ingress-600-q8 | 560 / 601 | 17–113 | 100 | 361 | 437 | 753 | 394 | 335 | 345 | 4.63 → 5.09 |
| kev-0.8b | cuda | ingress-300-q4 | 297 / 307 | 17–83 | 100 | 159 | 166 | 171 | 158 | 146 | 156 | 4.04 → 4.60 |
| kev-0.8b | cuda | pre-execute-600-risk | 560 / 601 | 260–260 | 100 | 249 | 510 | 628 | 324 | 203 | 243 | 4.60 → 11.77 |

### GPU (RTX 3090), run 1: all cells in one process per model, load 9–48

| Model | Device | Cell | State (Corvid / Kev tokens) | Rows (Kev tokens) | n | p50 | p90 | p99 | mean | min | Kev only p50 | Load before → after |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| kev-0.8b | cuda | ingress-300-q4 | 297 / 307 | 17–83 | 100 | 226 | 406 | 591 | 265 | 153 | 213 | 10.80 → 19.90 |
| kev-0.8b | cuda | ingress-300-q8 | 297 / 307 | 17–113 | 100 | 326 | 721 | 1033 | 426 | 250 | 306 | 19.90 → 20.87 |
| kev-0.8b | cuda | ingress-600-q4 | 560 / 601 | 17–83 | 100 | 284 | 558 | 729 | 351 | 243 | 275 | 20.87 → 20.78 |
| kev-0.8b | cuda | ingress-600-q8 | 560 / 601 | 17–113 | 100 | 412 | 814 | 1208 | 512 | 352 | 402 | 20.78 → 17.92 |
| kev-0.8b | cuda | ingress-1500-q4 | 1496 / 1559 | 17–83 | 100 | 718 | 1250 | 1618 | 834 | 577 | 700 | 17.92 → 19.56 |
| kev-0.8b | cuda | ingress-1500-q8 | 1496 / 1559 | 17–113 | 100 | 1011 | 1753 | 2399 | 1187 | 751 | 994 | 19.56 → 24.23 |
| kev-0.8b | cuda | pre-execute-600-risk | 560 / 601 | 260–260 | 100 | 253 | 506 | 633 | 323 | 208 | 244 | 24.23 → 20.43 |
| kev-0.8b | cuda | rows-600-risk-250 | 560 / 601 | 260–260 | 100 | 256 | 417 | 484 | 295 | 204 | 244 | 20.43 → 17.29 |
| kev-0.8b | cuda | rows-600-risk-500 | 560 / 601 | 456–456 | 100 | 352 | 584 | 740 | 396 | 257 | 314 | 17.29 → 17.31 |
| kev-0.8b | cuda | rows-600-risk-750 | 560 / 601 | 750–750 | 100 | 468 | 762 | 863 | 544 | 352 | 452 | 17.31 → 15.38 |
| kev-0.8b | cuda | rows-600-risk-1000 | 560 / 601 | 962–962 | 100 | 570 | 835 | 1064 | 620 | 424 | 564 | 15.38 → 13.43 |
| kev-4b | cuda | ingress-300-q4 | 297 / 307 | 17–83 | 50 | 481 | 724 | 1305 | 583 | 465 | 474 | 9.45 → 18.06 |
| kev-4b | cuda | ingress-300-q8 | 297 / 307 | 17–113 | 50 | 787 | 799 | 904 | 775 | 725 | 780 | 18.06 → 12.14 |
| kev-4b | cuda | ingress-600-q4 | 560 / 601 | 17–83 | 50 | 691 | 754 | 1002 | 717 | 658 | 682 | 12.14 → 9.05 |
| kev-4b | cuda | ingress-600-q8 | 560 / 601 | 17–113 | 50 | 1031 | 1710 | 2240 | 1197 | 989 | 1026 | 9.05 → 15.50 |
| kev-4b | cuda | ingress-1500-q4 | 1496 / 1559 | 17–83 | 50 | 2078 | 3594 | 4572 | 2420 | 1757 | 2074 | 15.50 → 21.80 |
| kev-4b | cuda | ingress-1500-q8 | 1496 / 1559 | 17–113 | 50 | 2681 | 4457 | 5639 | 3020 | 2193 | 2678 | 21.80 → 16.03 |
| kev-4b | cuda | pre-execute-600-risk | 560 / 601 | 260–260 | 50 | 753 | 779 | 871 | 734 | 653 | 747 | 16.03 → 11.71 |
| kev-4b | cuda | rows-600-risk-250 | 560 / 601 | 260–260 | 50 | 763 | 796 | 1001 | 774 | 660 | 760 | 11.71 → 10.63 |
| kev-4b | cuda | rows-600-risk-500 | 560 / 601 | 456–456 | 50 | 973 | 1034 | 1406 | 998 | 967 | 968 | 10.63 → 8.48 |
| kev-4b | cuda | rows-600-risk-750 | 560 / 601 | 750–750 | 50 | 1332 | 1562 | 2943 | 1364 | 1111 | 1327 | 8.48 → 11.44 |
| kev-4b | cuda | rows-600-risk-1000 | 560 / 601 | 962–962 | 50 | 3698 | 6813 | 9503 | 4335 | 1982 | 3693 | 11.44 → 48.11 |

### GPU (RTX 3090), run 2: one process per cell, load 20–84

Run 2 waited for the load average to fall below 6 before each cell; it never did (the first cell waited 20 minutes
and started at load 61), so the wait was cut to a minute and the cells ran under whatever load there was.

| Model | Device | Cell | State (Corvid / Kev tokens) | Rows (Kev tokens) | n | p50 | p90 | p99 | mean | min | Kev only p50 | Load before → after |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| kev-0.8b | cuda | ingress-600-q4 | 560 / 601 | 17–83 | 50 | 258 | 264 | 346 | 259 | 237 | 254 | 56.61 → 46.39 |
| kev-0.8b | cuda | ingress-600-q8 | 560 / 601 | 17–113 | 50 | 1088 | 1854 | 2924 | 1202 | 572 | 971 | 38.39 → 80.83 |
| kev-0.8b | cuda | ingress-300-q4 | 297 / 307 | 17–83 | 50 | 438 | 810 | 1141 | 502 | 241 | 427 | 71.16 → 82.54 |
| kev-0.8b | cuda | pre-execute-600-risk | 560 / 601 | 260–260 | 50 | 706 | 1059 | 1605 | 759 | 325 | 696 | 76.45 → 62.27 |
| kev-0.8b | cuda | ingress-300-q8 | 297 / 307 | 17–113 | 50 | 280 | 389 | 606 | 308 | 257 | 275 | 41.45 → 35.84 |
| kev-0.8b | cuda | ingress-1500-q4 | 1496 / 1559 | 17–83 | 50 | 789 | 1172 | 1455 | 861 | 591 | 775 | 23.43 → 20.35 |
| kev-0.8b | cuda | ingress-1500-q8 | 1496 / 1559 | 17–113 | 50 | 2032 | 4034 | 5750 | 2407 | 1062 | 2024 | 41.17 → 62.47 |
| kev-0.8b | cuda | rows-600-risk-500 | 560 / 601 | 456–456 | 50 | 316 | 371 | 452 | 320 | 266 | 306 | 29.84 → 23.37 |
| kev-0.8b | cuda | rows-600-risk-750 | 560 / 601 | 750–750 | 50 | 692 | 1287 | 2522 | 858 | 501 | 664 | 79.47 → 83.57 |
| kev-0.8b | cuda | rows-600-risk-1000 | 560 / 601 | 962–962 | 50 | 1418 | 2284 | 3362 | 1543 | 853 | 1412 | 79.25 → 74.57 |
| kev-4b | cuda | ingress-600-q4 | 560 / 601 | 17–83 | 50 | 1254 | 2372 | 4921 | 1472 | 742 | 1249 | 60.55 → 52.10 |
| kev-4b | cuda | ingress-600-q8 | 560 / 601 | 17–113 | 50 | 1207 | 1720 | 2064 | 1320 | 1024 | 1160 | 25.26 → 27.07 |
| kev-4b | cuda | pre-execute-600-risk | 560 / 601 | 260–260 | 50 | 968 | 1053 | 1279 | 989 | 879 | 961 | 44.19 → 79.68 |

### CPU

Kev-0.8B: the first two rows are run 3 (load 5 at the start; the first cell's load rose to 54 during it), then run 1
(two cells before it was stopped to free the lock), run 2 and a 20-call follow-up. Kev-4B: 10 calls per cell.

| Model | Device | Cell | State (Corvid / Kev tokens) | Rows (Kev tokens) | n | p50 | p90 | p99 | mean | min | Kev only p50 | Load before → after |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| kev-0.8b | cpu | ingress-600-q4 | 560 / 601 | 17–83 | 20 | 7206 | 17771 | 30705 | 10088 | 4654 | 7200 | 5.47 → 53.72 |
| kev-0.8b | cpu | pre-execute-600-risk | 560 / 601 | 260–260 | 20 | 2968 | 3207 | 4500 | 3079 | 2871 | 2963 | 5.35 → 5.95 |
| kev-0.8b | cpu | ingress-300-q4 | 297 / 307 | 17–83 | 50 | 2650 | 8469 | 12922 | 3559 | 1701 | 2601 | 31.30 → 33.88 |
| kev-0.8b | cpu | ingress-300-q8 | 297 / 307 | 17–113 | 50 | 6485 | 17739 | 39590 | 9044 | 3986 | 6377 | 33.88 → 29.25 |
| kev-0.8b | cpu | ingress-600-q4 | 560 / 601 | 17–83 | 50 | 3430 | 9681 | 13980 | 4656 | 2698 | 3422 | 15.29 → 29.66 |
| kev-0.8b | cpu | ingress-600-q8 | 560 / 601 | 17–113 | 50 | 4892 | 10664 | 27137 | 6933 | 3784 | 4854 | 19.11 → 9.92 |
| kev-0.8b | cpu | pre-execute-600-risk | 560 / 601 | 260–260 | 50 | 4393 | 19555 | 27260 | 7959 | 2902 | 4388 | 24.59 → 48.59 |
| kev-0.8b | cpu | ingress-300-q4 | 297 / 307 | 17–83 | 20 | 2572 | 4602 | 15088 | 3636 | 1936 | 2523 | 98.83 → 43.35 |
| kev-0.8b | cpu | ingress-1500-q4 | 1496 / 1559 | 17–83 | 20 | 12099 | 17810 | 25683 | 12350 | 6290 | 12094 | 22.37 → 29.88 |
| kev-0.8b | cpu | rows-600-risk-1000 | 560 / 601 | 962–962 | 20 | 7136 | 10693 | 21684 | 8084 | 5545 | 7130 | 39.23 → 13.79 |
| kev-4b | cpu | ingress-600-q4 | 560 / 601 | 17–83 | 10 | 14769 | 15479 | 17430 | 15102 | 14434 | 14764 | 21.29 → 7.69 |
| kev-4b | cpu | pre-execute-600-risk | 560 / 601 | 260–260 | 10 | 15652 | 31200 | 65282 | 23899 | 15253 | 15647 | 43.21 → 11.55 |

### Reading the numbers

- **State size dominates.** With 4 questions on 0.8B CUDA, 300 → 600 → 1,500 tokens costs 159 → 260 ms (run 3 p50)
  and about 718 ms at 1,500 (run 1). A 1,500-token state is over 300 ms on every device and model.
- **A one-question checkpoint costs almost as much as four.** `pre-execute` (one 260-token row) is 249 ms against
  260 ms for 4 short rows on the same state (run 3): the state pass is most of the cost, and Kev does not reuse a state across
  requests (Starling: unsupported). A `pre-execute` checkpoint per side-effecting tool call therefore costs about a
  quarter of a second on the GPU even when the ingress state is unchanged.
- **Rows.** On 0.8B CUDA (run 1) the row sweep adds about 0.45 ms per row token above the base row: 253 ms at 260
  tokens, 352 at 456, 468 at 750, 570 at 962. Rows up to 1,000 tokens fit KevDecider's row budget; none was declined.
- **6 and 8 questions.** The `tool` and `skill` rows (shortlists with descriptions, 100–113 tokens) are the long ones:
  adding them (4 → 6 questions) costs 57–81 ms at 600 tokens (run 4), and the two short `injection` and `delegate` rows
  (6 → 8) another 38 ms. Going from 4 to 8 added about 100 ms in every run (run 4: 220–245 → 340 ms; run 3: 260 → 361
  ms; run 1: 284 → 412 ms).
- **Kev-4B on GPU** is 2.4–2.9× Kev-0.8B for the same cell in run 1 (691 against 284 ms at 600 × 4).
- **CPU.** The quickest Kev-0.8B CPU checkpoint is a 300-token, 4-question ingress at 2.6 s (runs 1 and 2, loaded);
  on the quieter machine (run 3, load 5–6) `pre-execute` was 3.0 s.
  Starling's admission measured about 0.37 s per question on short states; a 300–600-token state pass is what makes
  every CPU checkpoint here several seconds.

## Contamination

Other agents' builds and test suites ran on the workstation for most of the spike (at the same nice level as the
spike), with 1-minute load averages of 4 to 99 on 12 hardware threads. Other model jobs ran beside this one: the S2
spike held the shared GPU lock for long stretches, and `S3.Gliner2Targets` (GLiNER2) ran while Kev-4B was on the GPU,
apparently without the lock, so the one-model-at-a-time rule was not holding on this machine. Because the CUDA profile
runs attention and the state-cache copies on the CPU (above), the GPU cells are load-sensitive too: the same 0.8B
600 × 8 cell has p50 361 ms at load 5, 412 ms at load 18–21 and 1,088 ms at load 38–81.

Runs 3 (load 4–12) and 4 (load 3–6) are the cleanest measurements this spike has, and the verdict rests on them; runs
1 and 2 show how far latency moves on a busy workstation, which is also the condition Corvid will run in next to a
generator. The 4-question margin is 40–80 ms at p50 (run 3: 40; run 4: 55 and 80); the 6-question cell has none. On
the quiet machine p90 stays under the line for 4 questions (231–262 ms); with load rising during run 3 it was 434 ms.

`run.sh` takes the lock named by `GPU_LOCK` and defaults to a private `/tmp/corvid-s1-gpu.lock`. The logs do not say
which lock runs 1–3 used; if it was the default, the spike did not hold the shared GPU lock, which would also explain
another model job on the 3090 at the same time. Run 4 passed the shared lock explicitly.

## Files

- `evals/spikes/S1-kev-latency/`: the harness (`Program.cs`, project, `run.sh`, `table.py`), not in `Corvid.slnx`.
- `evals/spikes/S1-kev-latency/results/`: one JSON line per process (`load`) and per cell (`cell`, with every
  percentile of the fabric, Kev-only, encode and run times, the state and row sizes, and the load average), the console
  logs and the `nvidia-smi` logs.
