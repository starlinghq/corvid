# Evals

Golden trajectories and the replay harness (C-009). The design is in
[`docs/plan/corvid-agent.md`](../docs/plan/corvid-agent.md) §3.2 and §16; the format and
harness are described in [`docs/architecture.md`](../docs/architecture.md).

| Path | Contents |
| --- | --- |
| `golden/` | one trajectory per file: events in the log's persisted form and the projections they must replay to |
| `Corvid.Evals/` | `GoldenTrajectoryFile` (format), `ReplaySession` and `ReplayRunner` (replay), `SnapshotJson` and `JsonDiff` (comparison) |

`dotnet test --solution Corvid.slnx` replays every file in `golden/` through
`tests/Corvid.Evals.Tests`.

## Adding a trajectory

1. Write `golden/<id>.json` with `"expected": null` for each check.
2. Build, then record: `CORVID_RECORD_GOLDENS=1 tests/Corvid.Evals.Tests/bin/Release/net10.0/Corvid.Evals.Tests -method "*Golden_trajectories*"`.
   This rewrites every fixture from the current projection.
3. Review the diff. Recording accepts whatever the projection does today, so check each
   expected projection by hand before committing.

Decision replay against new thresholds or models is not built; it waits for
`IDecider` (C-004).
