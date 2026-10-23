# Corvid

A local-first .NET 10 agent in the spirit of Hermes Agent, built on Starling's in-process models. Kev, GLiNER2 and the embedding models make the agent's many small decisions (System One); a swappable generator, local or cloud, does the writing (System Two).

Vault M1 and the Vault side of its sandbox proxy for scripts (V-003a), the agent's initial project layout and dependency checks, the session event log (C-002), the tool generator and registry (C-005), the golden-trajectory replay harness (C-009), calibration tooling with decision export (C-021), the session actor and turn loop (C-011), the built-in tools with the durable scheduler (C-012), the host and `corvid` CLI with OpenTelemetry spans (C-013), the Kev decider with its Kev-4B second tier (C-004b), checkpoints with `/undo` and `/rollback` (C-014), compaction with map-reduce summaries and goal re-injection (C-016), skills with the git-versioned store, the learning loop and replay tests (C-017) and its sharper learning signals: a task-success precondition, the recovery-pattern trigger and a head-and-tail drafting window (C-026), and cloud and local-server model slots behind the privacy egress gate, keyed only through Vault (C-015), and the MCP client and `corvid mcp serve` (C-018), the ACP endpoint for editors and the OpenAI-compatible HTTP endpoint (C-019), and the messaging gateway with pairing, the delivery ledger and Telegram, Discord and Slack adapters (C-020), MCP tool-definition pinning with poisoned-description detection (C-022), trace export in Beacon's JSONL schema with Hermes memory import (C-027), per-project lessons, a reviewed memory tier between facts and skills (C-025), detection rules over the session event log, inline at approval time and post-hoc with `corvid scan` (C-023), project memory over `corvid mcp serve` (C-028), MCP pinning follow-ups, with CLI review, holds for an `injection` verdict (no longer taken from the generic question since C-037) and `forget` (C-033), and `/compact`, `/cron` with pausing and one Hermes home with its sticky profile for both Hermes imports (C-035), and credential-value detection shared by the egress gate, trace export and lessons, so a token or password in a cloud request is always redacted (C-032), and Kev on CUDA with placement configuration and a CUDA build variant (C-029), and fewer checkpoints when Kev runs on the CPU, at most two step questions and no per-call `risk` question where the outcome is ask anyway (C-030), and the S2 tool-calling golden set with its `corvid-evals tool-calling` harness (C-034), and description withholding decided before the tool pick, never on the generic `injection` verdict (C-037), and a credential scrub on writes, so a token a tool returns reaches neither the event log, memory nor any model, and memory and skills refuse one (C-040), are built. Without Kev downloaded, the decisions run on the degraded tier and `corvid chat` says so. The design is in [`docs/plan/corvid-agent.md`](docs/plan/corvid-agent.md); secrets live in a separate process, Corvid Vault ([`docs/plan/vault.md`](docs/plan/vault.md)).

## Run

```sh
dotnet run --project src/Corvid.Cli -- doctor              # what is missing, and how to fix it
dotnet run --project src/Corvid.Cli -- model download qwen3.5-0.8b gliner2-base-v1 embeddinggemma-2 kev-0.8b
                                                           # the default models (about 10 GB); nothing downloads implicitly
dotnet run --project src/Corvid.Cli -- model download kev-4b # optional second tier for gates (about 17 GB, 18 GB of RAM)
dotnet run --project src/Corvid.Cli -- chat
dotnet publish src/Corvid.Cli -c Release -r linux-x64      # a single-file `corvid`
dotnet publish src/Corvid.Cli -c Release -r linux-x64 -p:UseCuda=true -o out-cli-cuda
                                                           # the CUDA variant, for Kev on an NVIDIA GPU (about 300 MB more)
```

Configuration is `$XDG_CONFIG_HOME/corvid/corvid.json` (section `Corvid`), `CORVID__` environment variables, or `--set key=value`;
it is validated at start. Kev-4B is used only when configured: `--set Models:Slots:decider:Escalation:0=kev-4b`, or the same
key in the file. See [`docs/architecture.md`](docs/architecture.md#host-and-cli-c-013).

Kev answers a checkpoint in about a quarter of a second on an RTX 3090 and in seconds on a CPU ([S1](docs/spikes/S1-kev-latency.md)).
To run it on the GPU, use the CUDA variant (it needs the NVIDIA driver and the CUDA 13 runtime; `corvid doctor` checks) and place it:
`--set Models:Placement:kev-0.8b:Device=cuda --set Models:Placement:kev-0.8b:Fallback=Cpu`, or in the file
`"Models": { "Placement": { "kev-0.8b": { "Device": "cuda", "Fallback": "Cpu" } } }`. `Fallback: Cpu` runs it on the CPU, with a
notice, when the GPU cannot be used; the default `Fail` leaves the decisions on the degraded tier instead. Build the two variants
into separate output directories ([details](docs/architecture.md#kev-on-cuda-c-029)).

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

MCP servers go under `Corvid:Mcp:Servers`. Their tools join the built-in ones as `server__tool`. A stdio server runs under
`corvid-sandbox` with a private workspace and no network unless you allow it. An HTTP server's token is a Vault secret, never
configuration. Servers are untrusted by default: every tool asks before it runs and its description and every result taint the
session, and `"Trust": "Trusted"` honours a server's read-only hints. `corvid mcp list` starts each server and shows its tools, `/mcp` shows
them in chat, and `corvid mcp serve` offers Corvid's memory search and skills, read-only, to another agent over stdio
([details](docs/architecture.md#mcp-client-and-server-c-018)). With `"Corvid:Mcp:Serve:ProjectMemory": true` it also offers
`get_memory` and `get_memory_context` and serves the approved lessons of a project it knows, never local-only ones, logging each call as
one standard-error line without any memory text ([details](docs/architecture.md#project-memory-over-mcp-c-028)). A server's tool definitions are pinned on its first listing;
a tool that is new or changed later, or whose description or schema reads like injected instructions, is held until you review it with
`/mcp diff` and take it with `/mcp accept` (or `/mcp allow` for a flagged one, which then always asks)
([details](docs/architecture.md#mcp-tool-pinning-and-poisoned-descriptions-c-022)).

```json
{
  "Corvid": {
    "Mcp": {
      "Servers": {
        "notes": { "Command": "notes-mcp", "Arguments": ["--stdio"], "ReadOnlyPaths": ["~/notes"] },
        "tracker": { "Url": "https://mcp.tracker.example/mcp", "Secret": "tracker.token", "Trust": "Trusted" }
      }
    }
  }
}
```

`corvid acp` runs Corvid as an [Agent Client Protocol](https://agentclientprotocol.com) agent on stdin and stdout, so Zed,
JetBrains and VS Code can start it (in Zed, an `agent_servers` entry with `"command": "corvid", "args": ["acp"]`). Start it in
the workspace: a session for another directory is refused. Approvals appear as the editor's permission prompts, with the diff.

`corvid serve` is an OpenAI-compatible endpoint (`/v1/chat/completions`, streamed or not, and `/v1/models`, model `corvid`) for
Open WebUI and other front ends, on `http://127.0.0.1:8411` by default and loopback only. To listen elsewhere, name a Vault
secret as the bearer token (`corvid-vault set serve.token` with a random value of at least 24 bytes); clients send it as
`Authorization: Bearer`. Nobody can approve a tool call over HTTP, so a call that needs approval fails the request with
`approval_required` (or, with `Approvals` set to `Deny`, the model answers without it). See
[`docs/architecture.md`](docs/architecture.md#acp-and-openai-compatible-http-c-019).

```json
{ "Corvid": { "Serve": { "Url": "http://0.0.0.0:8411", "TokenSecret": "serve.token", "Approvals": "Fail", "Tools": true } } }
```

The messaging gateway (`corvid gateway`) answers on Telegram, Discord and Slack. Each bot token is a Vault secret named in the
configuration, never a value. Someone the configuration does not list gets a pairing code and nothing else until you approve
it on this machine: `corvid gateway pair` lists codes, `corvid gateway pair approve ABCD-EFGH --owner` pairs you, and `--guest`
pairs someone else, whose messages are treated as outside content. Only the owner can run commands and answer approvals. Every answer goes
through a delivery ledger in the event log, and anything not yet sent is sent again after a restart. A job created in a chat
reports there. `corvid gateway status` shows the adapters, pairings and waiting deliveries. See
[`docs/architecture.md`](docs/architecture.md#gateway-c-020).

```json
{
  "Corvid": {
    "Gateway": {
      "Adapters": {
        "telegram": { "Enabled": true, "Secret": "telegram.bot", "Owners": ["123456789"] },
        "discord": { "Enabled": true, "Secret": "discord.bot", "Channels": ["1180000000000000000"] },
        "slack": { "Enabled": true, "Secret": "slack.bot", "AppSecret": "slack.app", "HomeChat": "C0123456789",
                   "Chats": { "C0123456789": { "Persona": "Answer briefly.", "Slots": { "generator": "claude" } } } }
      }
    }
  }
}
```

In `vault.json`, bind each token to its API host (`api.telegram.org`, `discord.com`, `slack.com`) and toolset `gateway`. Allow
`POST` as `auto`, because sends carry no approval. Telegram's token goes in the URL path, so it needs `"allowInUrl": true`. Answers from a
tainted session, such as a chat with a guest, are refused under the default `"tainted": "ask"`. Set `"tainted": "auto"` on a
token only if guests should get answers.

`corvid traces export --session <id>` (or `--all`) writes sessions from the event log as JSON lines in the telemetry schema of
[Beacon](https://github.com/Asymptote-Labs/agent-beacon) (MIT), so Beacon's detections and SIEM forwarding read Corvid like any
agent they collect. Secrets are redacted and long strings cut at 4 KiB; `--retention metadata` keeps names, paths, commands and
decisions but no content; memory tool content is withheld unless `--include-local-memory` is given; `--out <file>` writes an
owner-only file and never overwrites one. `/memory import hermes [<hermes home>] [--dry-run]` stages each entry of Hermes'
`MEMORY.md` and `USER.md` as a local-only memory for review in `/memory pending`; nothing is written until you approve it. See
[`docs/architecture.md`](docs/architecture.md#trace-export-and-hermes-memory-import-c-027).

Detection rules (C-023) watch the session event log. A built-in pack of twelve rules runs before every approval, and
before a network read that would otherwise run unasked (a `web_fetch` after a `read_file .env`): a match of an enforce
rule (a credential read followed by network egress, the environment or a credential file sent out, base64 in
a request URL, a command the user just denied proposed again) asks, once only, whatever the mode or remembered approvals
say; the others only record a `detection_matched` event. `corvid scan` runs the same rules over the whole log afterwards,
`--session <id>` over one session, `--json` as one document, and `--fail-on high` exits 4 when a finding is at least that
severe. `Corvid:Detections:Enforce=false` records every match without asking (scheduled jobs then never stop on a rule),
and `Corvid:Detections:Enabled=false` turns the inline check off. See
[`docs/architecture.md`](docs/architecture.md#detection-rules-c-023).

```sh
corvid scan --fail-on high
corvid chat --set Detections:Enforce=false
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
| `src/Corvid.Memory` | memory store on SQLite: core blocks, episodic FTS5, vector index, bi-temporal fact graph, hybrid retrieval and the `memory_op`-gated write path (C-008); per-project lessons with their quality bar and gated writer (C-025) |
| `src/Corvid.Host` | the composition root (C-013): typed configuration validated at start, slots composed from configuration (Starling by default), the runtime with C-012's built-in tools and scheduler wired in, the slash-command registry shared by surfaces (with `/compact` and `/cron` from C-035), the Hermes home resolution both Hermes imports share (C-035), `corvid doctor`'s checks, and OpenTelemetry export |
| `src/Corvid.Cli` | `corvid` (C-013): `chat` (Spectre.Console, scrollback-first, streaming, live tool panel, inline approvals), `model`, `doctor`, `evals`; `mcp list` and `mcp serve` (C-018); `acp` and `serve` (C-019); `gateway` (C-020); `traces export` (C-027); `scan` (C-023) |
| `src/Corvid.Mcp` | MCP (C-018), the only project on the ModelContextProtocol SDK: configured servers as Corvid tools (stdio under `corvid-sandbox`, streamable HTTP with tokens only through Vault, per-server trust, timeouts, lazy restarts, the tasks extension) and Corvid as a read-only MCP server for memory search and skills; tool-definition pins with held tools and the poisoned-description scan (C-022); `injection` holds (C-033; none from the generic question since C-037, persisted ones kept until allowed), `--hash` review and forgetting pins (C-033) |
| `src/Corvid.Acp` | the Agent Client Protocol surface (C-019): JSON-RPC over stdio, sessions with load, resume, fork and list, streamed tool calls with diffs, approvals as permission requests, cancellation |
| `src/Corvid.Http` | the OpenAI-compatible endpoint (C-019): `/v1/chat/completions` and `/v1/models` on ASP.NET Core minimal APIs, conversations mapped onto sessions, loopback or a Vault-checked bearer token |
| `src/Corvid.Rules` | detection rules (C-023): the JSON rule format, its loader and field-path validator, the predicate evaluator and correlation matcher, and the embedded built-in pack |
| `src/Corvid.Secrets` | the credential-value detector (C-032): provider token shapes, PEM blocks, Authorization values, labelled assignments, URL passwords and prose values, NonBacktracking with timeouts; shared by the egress gate, trace export and the lesson validator; and the credential scrub (C-040) that replaces each value with a non-reversible marker in text and JSON, used by the event log and the skill scanner |
| `src/Corvid.Gateway` | the gateway (C-020): one host for every enabled adapter (each created lazily in its own scope, connected under a timeout and retried with a backoff), pairing codes and allowlists, per-chat sessions with persona and slot overrides, approvals in the chat, `deliver` routing, and the delivery ledger in the event log with at-least-once redelivery |
| `src/Corvid.Gateway.Telegram`, `.Discord`, `.Slack` | platform adapters over plain HTTP (and Slack's Socket Mode WebSocket), with bot tokens only through Vault (C-020) |
| `src/Corvid.Skills` | skills (C-017): the agentskills.io `SKILL.md` loader with Hermes metadata, the skills directory as a git repository with `user`/`agent`/`hub:<source>` authorship, imports (including `~/.hermes/skills`), the learning loop (draft, replay test, `skill_write_safe`), evals, the curator and the skill tools |
| `src/Corvid.Approvals` | structural approval pipeline: bash-subset parser, wrapper unwrapping, target extraction, the hardline deny list, `risk` through the fabric, approval modes, structural rules and approval events (C-007b) |
| `vault/Corvid.Vault` | `corvid-vault`, the secrets process; see [`vault/README.md`](vault/README.md) |
| `src/Corvid.Tools` | tool attributes, risk metadata and the tool registry (C-005) |
| `src/Corvid.Tools.Builtin` | built-in tools (C-012): files confined to the workspace, the sandboxed and approved shell, web fetch, memory and cron tools, and the scheduler's sandboxed script runner |
| `src/Corvid.Tools.Generators` | Roslyn source generator for `[Tool]` methods; build tooling, consumed as an analyzer |
| `src/Corvid.Vault.Contracts` | wire contract between Corvid and Vault |
| `src/Corvid.Vault.Client` | Corvid's side: `VaultClient`, `VaultHandler`, `VaultSecretHandler` (placeholder credentials), `SecretRef` |
| `evals/` | golden trajectories (`evals/golden`) and the replay harness, `Corvid.Evals` (C-009); calibration, floor replay and decision export, with the `corvid-evals` console in `Corvid.Evals.Calibrate` (C-021); the tool-calling golden set and `corvid-evals tool-calling` (C-034); see [`evals/README.md`](evals/README.md) |
| `tools/Corvid.Architecture` | resolved dependency graph checker; see [`docs/architecture.md`](docs/architecture.md) |
| `tests/` | boundary regression tests, host configuration, composition, telemetry and command tests, scripted CLI sessions, event log (including forks at a log position), turn loop, session actor and device scheduler tests, replay and calibration tests, decision fabric, memory and approval tests (including the shell bypass suite), tool generator and registry tests, remote model adapter tests against a stand-in Vault (C-015), built-in tool and scheduler tests (including the shell's confinement tests), checkpoint tests (the shadow store against real git, undo and rollback, the user's edits kept, a workspace that is itself a git repository, git missing), skill loader, repository, import, learning-loop and turn tests, MCP client, task, HTTP and server-mode tests against an in-process server and the real `corvid mcp serve` (C-018), recorded ACP exchanges and protocol scenario tests, HTTP endpoint tests on a loopback Kestrel with the token check against a stand-in Vault, gateway tests (pairing, taint, the ledger across a restart, a hanging adapter, and each adapter against a fake platform behind a stand-in Vault) (C-020), detection rule tests (the loader, predicates, correlation, every shipped rule's fixtures, and the pipeline order) (C-023), and Vault tests, including S6 |

