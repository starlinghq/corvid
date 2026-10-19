# Evals

Golden trajectories and the replay harness (C-009), and decision calibration and export (C-021). The design is in
[`docs/plan/corvid-agent.md`](../docs/plan/corvid-agent.md) §3.2 and §16; the format and
harness are described in [`docs/architecture.md`](../docs/architecture.md).

| Path | Contents |
| --- | --- |
| `golden/` | one trajectory per file: events in the log's persisted form and the projections they must replay to |
| `Corvid.Evals/` | `GoldenTrajectoryFile` (format), `ReplaySession` and `ReplayRunner` (replay), `SnapshotJson` and `JsonDiff` (comparison) |
| `Corvid.Evals/Calibration/` | `DecisionSet` (decisions joined with outcomes), `FloorFitter`, `CalibrationReport`, `FloorReplay`, `KevTrainingExport`, `ShareGptExport`, `CalibrationCli` |
| `Corvid.Evals.Calibrate/` | `corvid-evals`, the command-line entry point (later `corvid evals calibrate`, C-013) |

`dotnet test --solution Corvid.slnx` replays every file in `golden/` through
`tests/Corvid.Evals.Tests`.

## Adding a trajectory

1. Write `golden/<id>.json` with `"expected": null` for each check.
2. Build, then record: `CORVID_RECORD_GOLDENS=1 tests/Corvid.Evals.Tests/bin/Release/net10.0/Corvid.Evals.Tests -method "*Golden_trajectories*"`.
   This rewrites every fixture from the current projection.
3. Review the diff. Recording accepts whatever the projection does today, so check each
   expected projection by hand before committing.

## Calibrating floors

```sh
dotnet build Corvid.slnx -c Release
evals/Corvid.Evals.Calibrate/bin/Release/net10.0/corvid-evals calibrate --log ~/path/to/events.db \
    --version 2026-11-01.1 --out floors.json --report-json report.json
evals/Corvid.Evals.Calibrate/bin/Release/net10.0/corvid-evals replay --log ~/path/to/events.db --candidate floors.json
```

Only calibrated decisions fit floors; degraded-tier decisions are reported separately. `report`,
`export-decisions` (Kev training records) and `export-trajectories` (ShareGPT) take the same `--log` options; run
`corvid-evals --help` for the rest. Replaying recorded states against a new decider model is not built.
