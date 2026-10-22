# Evals

Golden trajectories and the replay harness (C-009), decision calibration and export (C-021), and the S2 tool-calling
golden set (C-034). The design is in
[`docs/plan/corvid-agent.md`](../docs/plan/corvid-agent.md) §3.2 and §16; the format and
harness are described in [`docs/architecture.md`](../docs/architecture.md).

| Path | Contents |
| --- | --- |
| `golden/` | one trajectory per file: events in the log's persisted form and the projections they must replay to |
| `Corvid.Evals/` | `GoldenTrajectoryFile` (format), `ReplaySession` and `ReplayRunner` (replay), `SnapshotJson` and `JsonDiff` (comparison) |
| `Corvid.Evals/Calibration/` | `DecisionSet` (decisions joined with outcomes), `FloorFitter`, `CalibrationReport`, `FloorReplay`, `KevTrainingExport`, `ShareGptExport`, `CalibrationCli` |
| `Corvid.Evals.Calibrate/` | `corvid-evals`, the command-line tool; `corvid evals <command>` (C-013) runs it with `--log` defaulting to the user's event log |
| `tool-calling/` | the S2 tool-calling golden set (C-034): `case.schema.json` (the case format), `catalogue.json` (15 built-ins snapshotted from the generated descriptors, 13 MCP-like extras) and `cases.jsonl` (208 cases), version `2026-10-21.1` |
| `Corvid.Evals/ToolCalling/` | `ToolCallingSet` (strict loader), `CatalogueTool`, `AbstainingDecider` and the timed wrappers, `ToolCallingRunner` (configurations over `TurnRunner`), `ToolCallingScorer`, `ToolCallingReport`, `ToolCallingCli` |
| `spikes/S2-tool-calling/` | the local CUDA wrapper for the measured S2 run (not in `Corvid.slnx`) and its results |

`dotnet test --solution Corvid.slnx` replays every file in `golden/` through
`tests/Corvid.Evals.Tests`.

## Adding a trajectory

1. Write `golden/<id>.json` with `"expected": null` for each check.
2. Build, then record: `CORVID_RECORD_GOLDENS=1 tests/Corvid.Evals.Tests/bin/Release/net10.0/Corvid.Evals.Tests -method "*Golden_trajectories*"`.
   This rewrites every fixture from the current projection.
3. Review the diff. Recording accepts whatever the projection does today, so check each
   expected projection by hand before committing.

The `turn-*` trajectories are recorded from the turn loop itself (C-011) by `tests/Corvid.Kernel.Tests/GoldenTurnTests`,
which runs each scenario on fakes and, without the variable, checks the loop still produces the committed file (decision
and approval ids aside). Record them with
`CORVID_RECORD_GOLDENS=1 tests/Corvid.Kernel.Tests/bin/Release/net10.0/Corvid.Kernel.Tests -class "*GoldenTurnTests"`.

## Calibrating floors

`corvid evals <command>` is the normal entry point. It finds `corvid-evals` through `$CORVID_EVALS`, next to `corvid` or
on `PATH`, and passes `--log` as the user's event log when none is given:

```sh
corvid evals calibrate --version 2026-11-01.1 --out floors.json --report-json report.json
corvid evals replay --candidate floors.json
```

Without an installed `corvid`, run the tool from a checkout with the log named:

```sh
dotnet build Corvid.slnx -c Release
evals/Corvid.Evals.Calibrate/bin/Release/net10.0/corvid-evals calibrate --log ~/path/to/events.db \
    --version 2026-11-01.1 --out floors.json --report-json report.json
evals/Corvid.Evals.Calibrate/bin/Release/net10.0/corvid-evals replay --log ~/path/to/events.db --candidate floors.json
```

Only calibrated decisions (recorded with `calibrated: true` by a non-degraded decider) fit floors; degraded-tier and
unproven decisions are reported separately. `report`,
`export-decisions` (Kev training records) and `export-trajectories` (ShareGPT) take the same `--log` options; run
`corvid evals --help` (or `corvid-evals --help`) for the rest. Replaying recorded states against a new decider model is not built.

## Running the tool-calling set

```sh
dotnet build Corvid.slnx -c Release
corvid model download qwen3.5-0.8b    # and embeddinggemma-2, kev-0.8b; gliner2-base-v1 is optional
MALLOC_MMAP_THRESHOLD_=65536 evals/Corvid.Evals.Calibrate/bin/Release/net10.0/corvid-evals tool-calling \
    --out /tmp/s2 [--configs qwen-all,shortlist,shortlist-kev,shortlist-kev-no-desc,degraded] [--tags near_miss] [--limit 20] [--no-extractor]
evals/Corvid.Evals.Calibrate/bin/Release/net10.0/corvid-evals calibrate --log /tmp/s2/events-shortlist-kev.db \
    --version 2026-11-01.1 --out floors.json   # Kev's tool decisions carry accepted/corrected outcomes
```

This is the shipped path: every model on the CPU, located offline as `corvid` locates them, never downloaded (a missing
one exits 1 naming `corvid model download`). It is slow on the CPU (Kev is 2.6 s or more per checkpoint, S1); the
measured run used the GPU wrapper, `evals/spikes/S2-tool-calling/run.sh build` then `run.sh run --out <dir>`, which binds
Qwen and Kev on the 3090 and otherwise runs the same `ToolCallingCli` (its `-p:UseCuda=true` restore shares
`Corvid.Models.Starling`'s `obj/`, so build the solution again afterwards). Results and the method are in
[`docs/spikes/S2-tool-calling.md`](../docs/spikes/S2-tool-calling.md).

CI runs no model: `tests/Corvid.Evals.Tests` validates the committed cases and their coverage and scores a fake model
through the real turn loop, and `tests/Corvid.Tools.Builtin.Tests/CatalogueSnapshotTests` fails when a built-in tool's
description, schema or examples drift from `catalogue.json`. To change a built-in, re-record the snapshot
(`CORVID_RECORD_CATALOGUE=1`, see the test) and re-check the cases that use it. To change a case, bump the set version in
`catalogue.json`, commit the cases before running any model on them, and never edit a label after seeing a result.
