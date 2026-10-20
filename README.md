# Corvid

A local-first .NET 10 agent in the spirit of Hermes Agent, built on Starling's in-process models. Kev, GLiNER2 and the embedding models make the agent's many small decisions (System One); a swappable generator, local or cloud, does the writing (System Two).

Vault M1, the agent's initial project layout and dependency checks, the session event log (C-002), the tool generator and registry (C-005), the golden-trajectory replay harness (C-009), calibration tooling with decision export (C-021), the session actor and turn loop on the degraded decider (C-011), the built-in tools with the durable scheduler (C-012), and the host and `corvid` CLI with OpenTelemetry spans (C-013) are built. The design is in [`docs/plan/corvid-agent.md`](docs/plan/corvid-agent.md); secrets live in a separate process, Corvid Vault ([`docs/plan/vault.md`](docs/plan/vault.md)).

## Run

```sh
dotnet run --project src/Corvid.Cli -- doctor              # what is missing, and how to fix it
dotnet run --project src/Corvid.Cli -- model download --all  # the Starling models (about 5 GB); nothing downloads implicitly
dotnet run --project src/Corvid.Cli -- chat
dotnet publish src/Corvid.Cli -c Release -r linux-x64      # a single-file `corvid`
```

Configuration is `$XDG_CONFIG_HOME/corvid/corvid.json` (section `Corvid`), `CORVID__` environment variables, or `--set key=value`;
it is validated at start. See [`docs/architecture.md`](docs/architecture.md#host-and-cli-c-013).

## Build and test

Requires the .NET 10 SDK (10.0.401 or a later feature band). The Vault tests are Linux-only.

```sh
dotnet build Corvid.slnx
dotnet run --project tools/Corvid.Architecture --no-build -- .
dotnet test --solution Corvid.slnx
```

| Directory | Contents |
| --- | --- |
| `docs/plan/` | `corvid-agent.md` (the agent) and `vault.md` (secrets) |
| `src/Corvid.Abstractions` | dependency-free agent contracts: session events, `IEventLog`, the `Envelope` surfaces post, and the `IDecider`, `IExtractor` and `ITokenCounter` model contracts |
| `src/Corvid.Kernel` | agent kernel: append-only SQLite event log and session projections (C-002); session actors, the turn loop, prompt rendering with result clearing, and the device scheduler (C-011); durable jobs, the schedule grammar and the job scheduler with the `wake` gate (C-012) |
| `src/Corvid.Models` | provider-independent model slots, profiles and switching (C-006) |
| `src/Corvid.Fabric` | decision fabric without Kev: question catalogue, versioned floors, TurnState, policy and the degraded decider (C-004a); the embedding tool shortlist (C-011) |
| `src/Corvid.Memory` | memory store on SQLite: core blocks, episodic FTS5, vector index, bi-temporal fact graph, hybrid retrieval and the `memory_op`-gated write path (C-008) |
| `src/Corvid.Host` | the composition root (C-013): typed configuration validated at start, slots composed from configuration (Starling by default), the runtime, the slash-command registry shared by surfaces, `corvid doctor`'s checks, and OpenTelemetry export |
| `src/Corvid.Cli` | `corvid` (C-013): `chat` (Spectre.Console, scrollback-first, streaming, live tool panel, inline approvals), `model`, `doctor`, `evals` |
| `src/Corvid.Approvals` | structural approval pipeline: bash-subset parser, wrapper unwrapping, target extraction, the hardline deny list, `risk` through the fabric, approval modes, structural rules and approval events (C-007b) |
| `vault/Corvid.Vault` | `corvid-vault`, the secrets process; see [`vault/README.md`](vault/README.md) |
| `src/Corvid.Tools` | tool attributes, risk metadata and the tool registry (C-005) |
| `src/Corvid.Tools.Builtin` | built-in tools (C-012): files confined to the workspace, the sandboxed and approved shell, web fetch, memory and cron tools, and the scheduler's sandboxed script runner |
| `src/Corvid.Tools.Generators` | Roslyn source generator for `[Tool]` methods; build tooling, consumed as an analyzer |
| `src/Corvid.Vault.Contracts` | wire contract between Corvid and Vault |
| `src/Corvid.Vault.Client` | Corvid's side: `VaultClient`, `VaultHandler`, `SecretRef` |
| `evals/` | golden trajectories (`evals/golden`) and the replay harness, `Corvid.Evals` (C-009); calibration, floor replay and decision export, with the `corvid-evals` console in `Corvid.Evals.Calibrate` (C-021); see [`evals/README.md`](evals/README.md) |
| `tools/Corvid.Architecture` | resolved dependency graph checker; see [`docs/architecture.md`](docs/architecture.md) |
| `tests/` | boundary regression tests, host configuration, composition, telemetry and command tests, scripted CLI sessions, event log, turn loop, session actor and device scheduler tests, replay and calibration tests, decision fabric, memory and approval tests (including the shell bypass suite), tool generator and registry tests, built-in tool and scheduler tests (including the shell's confinement tests), and Vault tests, including S6 |

