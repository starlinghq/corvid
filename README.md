# Corvid

A local-first .NET 10 agent in the spirit of Hermes Agent, built on Starling's in-process models. Kev, GLiNER2 and the embedding models make the agent's many small decisions (System One); a swappable generator, local or cloud, does the writing (System Two).

Vault M1, the agent's initial project layout and dependency checks, the session event log (C-002), the tool generator and registry (C-005), the golden-trajectory replay harness (C-009), calibration tooling with decision export (C-021), the session actor and turn loop (C-011), the built-in tools with the durable scheduler (C-012), the host and `corvid` CLI with OpenTelemetry spans (C-013), the Kev decider with its Kev-4B second tier (C-004b), checkpoints with `/undo` and `/rollback` (C-014), compaction with map-reduce summaries and goal re-injection (C-016), skills with the git-versioned store, the learning loop and replay tests (C-017), and cloud and local-server model slots behind the privacy egress gate, keyed only through Vault (C-015) are built. Without Kev downloaded, the decisions run on the degraded tier and `corvid chat` says so. The design is in [`docs/plan/corvid-agent.md`](docs/plan/corvid-agent.md); secrets live in a separate process, Corvid Vault ([`docs/plan/vault.md`](docs/plan/vault.md)).

## Run

```sh
dotnet run --project src/Corvid.Cli -- doctor              # what is missing, and how to fix it
dotnet run --project src/Corvid.Cli -- model download qwen3.5-0.8b gliner2-base-v1 embeddinggemma-2 kev-0.8b
                                                           # the default models (about 10 GB); nothing downloads implicitly
dotnet run --project src/Corvid.Cli -- model download kev-4b # optional second tier for gates (about 17 GB, 18 GB of RAM)
dotnet run --project src/Corvid.Cli -- chat
dotnet publish src/Corvid.Cli -c Release -r linux-x64      # a single-file `corvid`
```

Configuration is `$XDG_CONFIG_HOME/corvid/corvid.json` (section `Corvid`), `CORVID__` environment variables, or `--set key=value`;
it is validated at start. Kev-4B is used only when configured: `--set Models:Slots:decider:Escalation:0=kev-4b`, or the same
key in the file. See [`docs/architecture.md`](docs/architecture.md#host-and-cli-c-013).

Tool calls that change files are checkpointed first, so `/undo` (the last turn) and `/rollback N` can put the files back and take
those turns out of the conversation; your own edits are kept or the restore is refused. Checkpoints need `git` on `PATH`
([details](docs/architecture.md#checkpoints-c-014)).

Remote models go under `Corvid:Models:Remote` and are named in slots like any other. A cloud model's key is a Vault secret
(`corvid-vault set anthropic.key`, bound to the provider's host); Corvid never reads keys from the environment, and without
Vault cloud models are unavailable. Requests to cloud models pass the privacy egress gate, which redacts PII by default.
See [`docs/architecture.md`](docs/architecture.md#cloud-slots-and-privacy-egress-c-015).

```json
{
  "Corvid": {
    "Models": {
      "Remote": {
        "claude": { "Provider": "anthropic", "Model": "claude-opus-5-5", "Secret": "anthropic.key", "ContextWindow": 1000000, "AcceptedContext": 200000 },
        "qwen-27b": { "Provider": "openai-compatible", "Endpoint": "http://127.0.0.1:8080/v1", "Model": "qwen3.5-27b", "ContextWindow": 32768 }
      },
      "Slots": { "generator": { "Model": "qwen3.5-0.8b", "Escalation": ["qwen-27b", "claude"] } }
    },
    "Privacy": { "Cloud": "Redact" }
  }
}
```

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
| `src/Corvid.Kernel` | agent kernel: append-only SQLite event log and session projections (C-002); session actors, the turn loop, prompt rendering with result clearing, and the device scheduler (C-011); compaction into the structured record, map-reduce summaries, goal re-injection and expansion on a larger window (C-016); durable jobs, the schedule grammar and the job scheduler with the `wake` gate (C-012); checkpoints in a shadow git store per workspace, the write ledger, and undo and rollback as rewinds to a log position (C-014) |
| `src/Corvid.Models` | provider-independent model slots, profiles and switching (C-006); the privacy egress gate, placeholder redaction and the Vault-only HTTP pipeline for remote models (C-015) |
| `src/Corvid.Models.Anthropic` | Anthropic's Messages API through the official SDK, with prompt-cache breakpoints in Hermes' layout (C-015) |
| `src/Corvid.Models.OpenAICompat` | OpenAI-compatible endpoints through the OpenAI SDK: local servers, OpenAI, OpenRouter and Gemini's compatible endpoint (C-015) |
| `src/Corvid.Fabric` | decision fabric: question catalogue, versioned floors, TurnState, policy and the degraded decider (C-004a); the embedding tool shortlist (C-011); the two-tier escalation of gates to Kev-4B (C-004b) |
| `src/Corvid.Models.Starling` | in-process Starling adapters (0.3.0, CPU): Qwen3.5-0.8B generator, GLiNER2 extractor, EmbeddingGemma 2 and e5 embedders (C-010), the Kev-0.8B and Kev-4B decider (C-004b), and offline-first acquisition |
| `src/Corvid.Memory` | memory store on SQLite: core blocks, episodic FTS5, vector index, bi-temporal fact graph, hybrid retrieval and the `memory_op`-gated write path (C-008) |
| `src/Corvid.Host` | the composition root (C-013): typed configuration validated at start, slots composed from configuration (Starling by default), the runtime with C-012's built-in tools and scheduler wired in, the slash-command registry shared by surfaces, `corvid doctor`'s checks, and OpenTelemetry export |
| `src/Corvid.Cli` | `corvid` (C-013): `chat` (Spectre.Console, scrollback-first, streaming, live tool panel, inline approvals), `model`, `doctor`, `evals` |
| `src/Corvid.Skills` | skills (C-017): the agentskills.io `SKILL.md` loader with Hermes metadata, the skills directory as a git repository with `user`/`agent`/`hub:<source>` authorship, imports (including `~/.hermes/skills`), the learning loop (draft, replay test, `skill_write_safe`), evals, the curator and the skill tools |
| `src/Corvid.Approvals` | structural approval pipeline: bash-subset parser, wrapper unwrapping, target extraction, the hardline deny list, `risk` through the fabric, approval modes, structural rules and approval events (C-007b) |
| `vault/Corvid.Vault` | `corvid-vault`, the secrets process; see [`vault/README.md`](vault/README.md) |
| `src/Corvid.Tools` | tool attributes, risk metadata and the tool registry (C-005) |
| `src/Corvid.Tools.Builtin` | built-in tools (C-012): files confined to the workspace, the sandboxed and approved shell, web fetch, memory and cron tools, and the scheduler's sandboxed script runner |
| `src/Corvid.Tools.Generators` | Roslyn source generator for `[Tool]` methods; build tooling, consumed as an analyzer |
| `src/Corvid.Vault.Contracts` | wire contract between Corvid and Vault |
| `src/Corvid.Vault.Client` | Corvid's side: `VaultClient`, `VaultHandler`, `VaultSecretHandler` (placeholder credentials), `SecretRef` |
| `evals/` | golden trajectories (`evals/golden`) and the replay harness, `Corvid.Evals` (C-009); calibration, floor replay and decision export, with the `corvid-evals` console in `Corvid.Evals.Calibrate` (C-021); see [`evals/README.md`](evals/README.md) |
| `tools/Corvid.Architecture` | resolved dependency graph checker; see [`docs/architecture.md`](docs/architecture.md) |
| `tests/` | boundary regression tests, host configuration, composition, telemetry and command tests, scripted CLI sessions, event log, turn loop, session actor and device scheduler tests, replay and calibration tests, decision fabric, memory and approval tests (including the shell bypass suite), tool generator and registry tests, remote model adapter tests against a stand-in Vault (C-015), built-in tool and scheduler tests (including the shell's confinement tests), checkpoint tests (the shadow store against real git, undo and rollback, the user's edits kept, a workspace that is itself a git repository, git missing), skill loader, repository, import, learning-loop and turn tests, and Vault tests, including S6 |

