# Corvid

A local-first .NET 10 agent in the spirit of Hermes Agent, built on Starling's in-process models. Kev, GLiNER2 and the embedding models make the agent's many small decisions (System One); a swappable generator, local or cloud, does the writing (System Two).

Vault M1, the agent's initial project layout and dependency checks, the session event log (C-002), and the tool generator and registry (C-005) are built. The agent loop is not implemented yet. The design is in [`docs/plan/corvid-agent.md`](docs/plan/corvid-agent.md); secrets live in a separate process, Corvid Vault ([`docs/plan/vault.md`](docs/plan/vault.md)).

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
| `src/Corvid.Abstractions` | dependency-free agent contracts: session events and `IEventLog` |
| `src/Corvid.Kernel` | agent kernel: append-only SQLite event log and session projections (C-002) |
| `src/Corvid.Models` | provider-independent model project; slots and profiles follow in C-006 |
| `vault/Corvid.Vault` | `corvid-vault`, the secrets process; see [`vault/README.md`](vault/README.md) |
| `src/Corvid.Tools` | tool attributes, risk metadata and the tool registry (C-005) |
| `src/Corvid.Tools.Generators` | Roslyn source generator for `[Tool]` methods; build tooling, consumed as an analyzer |
| `src/Corvid.Vault.Contracts` | wire contract between Corvid and Vault |
| `src/Corvid.Vault.Client` | Corvid's side: `VaultClient`, `VaultHandler`, `SecretRef` |
| `tools/Corvid.Architecture` | resolved dependency graph checker; see [`docs/architecture.md`](docs/architecture.md) |
| `tests/` | boundary regression tests, event log tests, tool generator and registry tests, and Vault tests, including S6 |

