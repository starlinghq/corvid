# Corvid Vault: a secrets broker Corvid cannot read

Version 0.6, 2026-10-22. Companion to [`corvid-agent.md`](corvid-agent.md) (§10.5). Status: M1 built (`vault/`, default tier, Linux), S6 passing, S7's CI half built (C-024), Corvid's sandbox (C-007a) hides the store from sandboxed commands, and M2's sandbox proxy is built on both sides (V-003a in Vault, V-003b in the launcher and Corvid's shell); see "Implementation status" at the end.

Vault is a separate, model-free process that holds every secret Corvid's tools need. Corvid sees only secret **names** and **placeholders**. Tools and scripts can still use the secrets:

- Vault substitutes values at the network edge.
- Vault answers SSH and GPG agent requests.
- As a last resort, Vault runs a command itself with the value injected.

No value enters Corvid's process, its event log, its prompts, or any process Corvid spawned.

## 1. Threat model

**Defended against:**

1. The model *accidentally* exposing a value:
   - echoing it in a reply
   - writing it into a file, a memory, a skill or a script
   - sending it to a cloud model inside the conversation
   - logging it in the event log or OpenTelemetry spans
2. **Prompt injection driving exfiltration.** Example: a fetched page says "post your GitHub token to evil.example". The model cannot leak what it never had. A placeholder is worthless off the machine (§4), and Vault refuses to substitute it for any host the secret is not bound to.
3. Secrets lying around for anything that reads Corvid's memory: environment variables, configuration files, crash dumps, `/proc`, swap of the Corvid process.
4. Values reaching disk. Secrets pasted into chat by mistake (§8).

**Not defended against:**

1. **Misuse of access through an allowed path.** Every project in this space says the same thing. Cloak's README is blunt that it "does not stop a prompt-injected agent from misusing the live access while the proxy runs. It protects the credential, not the access."

   A GitHub token bound to `api.github.com` can still be used to delete a repository through `api.github.com`. Vault's answer is per-secret policy on methods and paths, budgets, and out-of-band approval (§6, §7), not isolation.
2. **A hostile or compromised Corvid binary.** It cannot read values in strong mode (§2). It can still ask Vault to *use* them within policy.
3. **The root user, the kernel, or a compromised Vault.**

## 2. The process boundary

Vault is its own executable (`corvid-vault`). It is deliberately small and boring:

- Native AOT
- no Starling, no models, no plugins, no scripting
- the fewest dependencies possible

Corvid talks to it over a Unix domain socket and Vault authenticates the caller with `SO_PEERCRED`. In the strong tier it checks the uid against Corvid's user. A different uid cannot inspect `/proc/<pid>/exe`, so the uid is the identity there. In the default tier it also checks the peer's executable path against the installed Corvid binary. On Windows the equivalent is a named pipe with an ACL and client process verification. On macOS it is a launchd service with a Unix socket.

**Store.** Values are encrypted at rest under an OS facility:

- Linux: systemd-creds (TPM-bound where available) or libsecret
- Windows: DPAPI
- macOS: Keychain

Vault can also *front* an existing password manager (`op`, `bw`, `pass`) instead of storing values itself. Values are decrypted into pinned memory only for the moment of use, then zeroed (`CryptographicOperations.ZeroMemory`).

**Isolation tiers.** Being a separate process is not enough on Linux. A process under the same uid can read `/proc/<pid>/environ` and attach a debugger. `PR_SET_DUMPABLE=0` protects Vault itself, but it resets on `execve`, so a child Vault starts with injected env is readable by that uid again. Hence two tiers:

| Tier | Setup | What Corvid can reach |
| --- | --- | --- |
| **Strong** (recommended) | Vault runs as a dedicated system user `corvid-vault`. Exec-mode children run as a second user, `corvid-exec`, and the store directory is mode 0700 for `corvid-vault`. A one-time `sudo corvid-vault install` sets this up. | Nothing. Different uids, so no `/proc` access, no ptrace, and no reading of the store or of exec children. |
| **Default** (no root) | Same uid. Vault sets `PR_SET_DUMPABLE=0` and disables core dumps. At startup, before any model loads, Corvid applies a Landlock ruleset to itself that denies the store directory and denies opening `/proc/<vault-pid>/*`. Exec mode is **disabled** in this tier. | Values are protected from the model and from accidents. A deliberately hostile same-uid process could still read them, which is outside the threat model (§1). |

`corvid doctor` reports the tier, and `/status` shows it.

## 3. Using a secret: three paths in order of preference

### 3.1 Egress proxy (default; covers most cases)

Most secrets are bearer tokens or API keys sent in an HTTP header, so most secret use is an HTTP request. Vault is the egress point for those requests:

- **In-process C# tools.** Corvid's `HttpClient` gets a `VaultHandler`. A request whose headers contain a placeholder goes to Vault over the socket as a structured *forward request*: method, URL, headers, body stream. Vault then:
  1. checks policy (§6)
  2. opens TLS to the real host itself and **verifies the upstream certificate** for a host in the secret's allowlist
  3. substitutes the value
  4. streams the response back, scrubbed (§5)
- **Scripts and CLIs in the sandbox** (`curl`, `gh`, Python `requests`):
  - The sandbox gets `HTTPS_PROXY` pointing at Vault's proxy socket, plus a Vault-minted CA certificate installed in the **sandbox's** trust store only.
  - The sandbox has no other network route: in strong mode, egress is only to Vault.
  - The script carries placeholders, for example `Authorization: Bearer $GITHUB_TOKEN`, where `GITHUB_TOKEN` holds the placeholder.
  - Vault intercepts and substitutes exactly as above.
- **Substitution rules** follow the projects that have hardened this pattern (varlock, Gondolin, Novita):
  - headers only by default; body and query substitution must be enabled per secret
  - a placeholder may appear at most once per request
  - substitution happens only when the verified destination matches the secret's binding
  - otherwise the request is refused, not forwarded with the placeholder, and the refusal is audited
- **Cloud model calls are the same case** (§9).

### 3.2 Agent protocols: keys that never leave

- **SSH.** Vault implements the ssh-agent protocol on a socket handed to the sandbox as `SSH_AUTH_SOCK`. `git push` and `ssh host` work, and the private key never leaves Vault. Policy binds each key to hosts. Signing requests for an unbound host are refused, and high-value keys ask out of band.
- **GPG.** Vault does the same for signing (`gpg-agent` protocol).
- Later: a PKCS#11 or TPM-backed key store, so some keys are never extractable even by Vault.

### 3.3 Exec with injection (fallback; strong tier only)

Some tools need the raw value: a database CLI with a connection string, a cloud CLI that signs requests (AWS SigV4), a binary that reads a key file. Two options, best first:

- **A signing adapter.** Vault signs on the caller's behalf. Vault's proxy can do SigV4 itself, so `aws` gets placeholder credentials and the proxy re-signs the request. Adapters are added per protocol.
- **Exec.** Corvid asks Vault to run a command. Vault spawns it as `corvid-exec` through `corvid-sandbox`. That is a small launcher executable shared by Corvid and Vault, so the bubblewrap, Landlock and seccomp policy is defined in one place and Vault does not depend on Corvid's libraries. The child gets the value in env or in a memory-backed file (`memfd`) that is deleted on exit.
  - stdout and stderr are relayed back to Corvid **through the scrubber**.
  - The child's network is still limited to the secret's bound hosts.
  - The model writes the command, but Vault decides whether it may run with the secret: policy names the allowed executables and argument patterns per secret, for example `psql` only, against the bound host.

Exec is the weakest path, because the value lives in a process the model's code influences. A script can still write the value somewhere inside its sandbox, or encode it into an allowed request. Exec secrets are therefore tagged in policy, and Kev's `risk` question sees "exec with secret" as a risk factor.

**Out of scope for v1:** scripts that write a secret into a long-lived config file for another program (`~/.netrc`, a kubeconfig, a database client's password file). Exec's `memfd` covers one-shot use. Persistent cases need a protocol adapter (as §3.2 does for SSH) or the user's own setup outside Corvid.

## 4. Names, placeholders and the catalogue

- The model sees a **catalogue**: names, descriptions, bound hosts and allowed uses. For example: `github.token: GitHub API, api.github.com, read and write repositories`. Never values, lengths or prefixes.
- Tools declare secret parameters with a `SecretRef` type. The source generator (`corvid-agent.md` §7) renders them in the schema as an enum of catalogue names, so the generator picks a name, never a value.
- **Placeholders** look like `ksec_github.token_9fQ2…`, with a per-session random suffix that Vault mints and remembers. A placeholder:
  - expires with the session
  - is valid only from the requesting peer
  - is meaningless off the machine

  If one leaks into a reply, a cloud prompt or a public gist, nothing is lost.
- Kev and GLiNER2 work on names and placeholders like any other text. The decision fabric needs no change.

## 5. The scrub service

Vault is the only component that knows the values, so Vault is the only one that can find them:

- **What is scrubbed.** Every byte Vault relays: proxied responses, exec output, agent-protocol errors. APIs echo tokens, and CLIs print them in debug output.
- **`scrub(text)` for the kernel.** Corvid's kernel routes **every** tool result and script output through it before the event log and before any model, including tools that never used a secret. A file the model reads may contain a value that Vault also holds (a stray `.env`).
- **Matching.** Aho–Corasick over each value and its common encodings:
  - raw
  - base64 and base64url
  - percent-encoded
  - hex
  - JSON-escaped
  - prefix runs of 12 or more characters

  Matches become `[secret:github.token]`.
- **The oracle risk.** A scrubber reveals whether a string matches, so code that can call it in a loop could guess a value. Mitigations:
  - The model cannot call `scrub` directly; only the kernel can.
  - There is a minimum match length.
  - Calls are rate-limited and the volume is audited.
  - Values below an entropy floor are refused at `corvid-vault set` (a password like `hunter2` belongs in the password manager the user already has, not in an agent's toolbelt).

  For high-entropy tokens, guessing through the scrubber is not practical.

## 6. Per-secret policy: access, not only the credential

Policy lives in Vault's own configuration (`vault.toml`), owned by Vault's user. Corvid cannot write it.

```toml
[secret."github.token"]
hosts       = ["api.github.com", "uploads.github.com"]
methods     = { GET = "auto", POST = "ask", PATCH = "ask", PUT = "ask", DELETE = "confirm" }
deny_paths  = ["/user/keys*", "/repos/*/*/hooks*", "/authorizations*"]
toolsets    = ["git", "github"]
budget      = { requests_per_hour = 300 }
approval    = "vault"          # ask/confirm go out of band (section 7)

[secret."anthropic.key"]
hosts       = ["api.anthropic.com"]
methods     = { POST = "auto" }
paths       = ["/v1/messages", "/v1/messages/count_tokens"]
role        = "model-provider"  # only Corvid's model slot may use it (section 9)
```

- `auto` uses the secret silently. `ask` uses Corvid's approval prompt. `confirm` needs out-of-band confirmation that Corvid cannot fake (§7).
- **Budgets.** A runaway loop or an injected instruction hits a rate ceiling, not your bill.
- **Taint.** When Corvid's turn is tainted (`corvid-agent.md` §10.3), it says so in the forward request. Vault's policy can raise `auto` to `ask` for tainted turns, and the default does. Vault treats the flag only as a way to be *stricter*. It never relaxes anything because Corvid said so.

## 7. Approvals Corvid cannot forge

Corvid's own approval flow (`corvid-agent.md` §10.2) still runs first: parse, risk score, ask. Vault then enforces its own policy independently and trusts nothing in the request beyond the peer credentials.

For `confirm` uses, Vault asks the user through a channel it owns:

- a desktop notification through D-Bus with Approve and Deny buttons
- a Vault-owned `pinentry`-style dialog
- a push to the user's phone through a paired ntfy topic that Vault holds the key for

The prompt shows Vault's own rendering of the request: the secret, the method, host and path, a body summary, and which Corvid session asked. A model-written description cannot change what you see.

## 8. Getting secrets in

- **Only through `corvid-vault set NAME`**: an interactive TTY prompt, from stdin, or `import` from `.env` files, `~/.aws/credentials`, `gh auth token` and existing password managers. Corvid has no tool that writes secrets.
- **Asking for a secret.** When a tool needs a secret that isn't set, Corvid tells the user which name to set and why. It never asks for the value in chat.
- **Pasted credentials.** At ingress, GLiNER2's credential labels plus well-known token formats (`ghp_`, `sk-`, `xoxb-`, AWS key ids, JWTs, PEM blocks) catch a pasted secret *before* it reaches the event log or a model. Corvid then:
  1. stores the message with the span replaced
  2. offers "move this to Vault as `<suggested name>`?", handing the raw span to Vault over the socket and dropping it from memory
  3. on the gateway, also suggests deleting the chat message, because the platform still has it
- **OAuth.** Vault runs authorization-code and device flows itself (Google, GitHub, Slack, Microsoft). It owns refresh tokens and refreshes them; Corvid only ever holds a placeholder for the access token.
- **Files on disk.** Corvid's sandbox (`corvid-agent.md` §10.1) denies the usual secret locations: `~/.ssh`, `~/.aws`, `~/.config/gh`, `.env*` and `*.pem`. `corvid doctor` lists secrets found on disk that could be imported into Vault.

## 9. Corvid's own keys go through Vault too

Cloud model slots (`corvid-agent.md` §4) authenticate through the proxy like any other tool. `Corvid.Models.Anthropic` and `Corvid.Models.OpenAICompat` get an `HttpClient` with `VaultHandler`, and the provider key is a secret with `role = "model-provider"`. So Corvid's process holds no API keys at all:

- A leaked config file, crash dump or environment dump from Corvid contains nothing.
- Hermes' credential pools and key rotation become Vault policy (several values per name, round-robin, cool-down on 429) without Corvid knowing.

Gateway bot tokens (Telegram, Discord, Slack) also go through the proxy path, never exec, so they work in both tiers:
- Telegram puts the token in the URL path, so that secret enables path substitution for `api.telegram.org`.
- Slack's Socket Mode uses the token in a header to open the connection.
- Discord sends its token inside the first websocket frame (`IDENTIFY`). Vault's proxy handles the websocket upgrade and substitutes only in that first client frame, for secrets marked `ws_first_frame`.

In every case the value stays inside Vault.

## 10. Audit

Vault keeps an append-only audit log of every use, refusal, approval and scrub hit, written to its own store and readable only through `corvid-vault audit`. Each entry records:

- the secret
- the peer, and the Corvid session and turn ids from the request
- the destination
- the decision and who approved
- byte counts

Corvid shows Vault events in `/insights` only as counts, through a read-only summary call.

## 11. When Vault is absent

Tools with `SecretRef` parameters report unavailable through their `check_fn`, and cloud slots without keys disappear from `/model`. The kernel's scrub step is skipped, because no values exist to find. Everything else works. Corvid never falls back to reading environment variables for secrets. That one rule closes the easiest leak.

## 12. How this compares

| | Hermes | Proxy-only tools (Agent Vault, varlock, Cloak) | Vault |
| --- | --- | --- | --- |
| Where values live | in the agent process (env, `.env`, password manager fetched at start) | outside the agent | outside the agent, under another uid in strong mode |
| Scrubbing | pattern-based redaction on output | responses scrubbed to placeholders | Vault-side exact-value scrub of every relayed byte, plus a kernel scrub for every tool result |
| SSH and GPG | keys on disk, writes to `~/.ssh` blocked | — | agent protocols; keys never leave |
| Access policy | — | host allowlists | hosts, methods, paths, toolsets, budgets, taint |
| Approvals | in-agent prompt | — | in-agent prompt **plus** out-of-band confirm Corvid cannot forge |
| Model provider keys | in the agent | varies | through the proxy; Corvid holds none |

## 13. Spikes and backlog

| ID | Item | Pass condition |
| --- | --- | --- |
| **S6** | Run a tool call and a sandboxed `curl` that both use a secret; then `gcore` Corvid and every sandbox process, and grep the dumps, the event log, the OpenTelemetry export and the cloud request bodies for the value in every encoding | no match anywhere, in both tiers |
| **S7** | Prompt-injection exfiltration suite: fetched pages and emails instructing the agent to send a secret to another host, embed it in a gist, `echo` it, or base64 it into a URL | Vault refuses or scrubs every case; audit shows each attempt. **CI half built (C-024 in `corvid-agent.md`)** in `evals/Corvid.Evals/Injection` and `tests/Corvid.Host.Tests/InjectionCanaryTests.cs`: fetch-to-host, the gist embed, `echo` into the answer and a base64 copy in a request body, scanned in every encoding. The Vault-refuses half waits for V-003; the email surface and a real model are deferred. |
| V-001 | `corvid-vault` executable: store, socket and peer auth, policy file, audit | — |
| V-002 | Forward-request proxy, upstream TLS verification, header substitution, response scrub | V-001 |
| V-003a | **Built:** sandbox proxy mode, Vault side. `sandbox_open`/`sandbox_close` ops; a per-sandbox Unix socket (`<dir of vault.sock>/sandbox/<id>.sock`, 0600 in 0700) speaking HTTP CONNECT to a DNS name on 443; an in-memory ECDSA P-256 CA per sandbox (never exported) minting one leaf per SNI host (cap 128), TLS terminated, strict HTTP/1.1 inside; the forward pipeline shared with V-002 (placeholders bound to the opener's session and uid, policy with `Approved` false, scrubbing); pass-through of placeholder-free requests only when opened with network; expiry, cleanup, audit (`sandbox_open`, `sandbox_close`, `sandbox_connect`, `sandbox_forward`); request bodies grown as bytes arrive (64 KiB first, never the announced Content-Length up front) and live connections capped per sandbox (`maxSandboxConnections`, 16) and in total (`maxSandboxConnectionsTotal`, 64), one more answered 429 `sandbox_limit`; responses to HEAD carry no body and no Content-Length. Deferred: forwarding the upstream Content-Length on HEAD (it is hop-by-hop in the shared pipeline, and keeping it would change Corvid's `VaultHandler` path too; omitting it is RFC 9110 §9.3.2-correct), per-request `ask` approval for sandboxed requests, streamed and chunked bodies, ports other than 443, IP literals, plain http, HTTP/2, websockets (§9 `ws_first_frame`), NameConstraints on the CA, a Vault-side ceiling on pass-through and per-sandbox budgets, an outbound exfiltration check, private upstream CAs, Windows and macOS; see "Implementation status" | V-002, C-007 |
| V-003b | **Built:** sandbox proxy mode, launcher side. `corvid-sandbox --proxy-socket --proxy-ca`: the network namespace stays private, the per-sandbox socket and CA bind-mounted under `/run/corvid-sandbox` (nothing else of Vault's), a relay that stage 2 re-executes into after Landlock and seccomp (the command's parent, 127.0.0.1:3128 bridged to the socket), `HTTPS_PROXY` and the trust variables naming the sandbox CA alone; Corvid's shell opens and closes a sandbox per command under `Corvid:Tools:ShellNetwork` (off by default; in a tainted turn only for a command a person approved as network egress; fail closed without Vault), other sandboxes' sockets denied, a span, a doctor check; end-to-end curl and Python `urllib` tests with real bubblewrap and Vault, and a no-direct-egress test. Deferred: a placeholder path into shell commands, per-toolset grants and a bound-hosts mode, the route for MCP stdio servers, skill scripts and tainted scheduled scripts, S6 extended to sandbox processes, per-request `ask`, Landlock TCP rules, clients beyond curl and `urllib` (`gh`, git, Node, pip, Java, Go, .NET), tying the sandbox's lifetime to the launcher, macOS and Windows; see "Implementation status" | V-003a, C-007a |
| V-004 | ssh-agent protocol | V-001 |
| V-005 | Ingress credential interception (GLiNER2 labels + token formats) | C-004 |
| V-006 | Out-of-band confirm (D-Bus, ntfy) | V-001 |
| V-007 | OAuth flows and refresh | V-002 |
| V-008 | Strong-tier installer (users, permissions, systemd units) | V-001 |
| V-009 | Exec mode and SigV4 re-signing | V-008 |

## 14. Open decisions

| ID | Decision | Default proposal |
| --- | --- | --- |
| D-10 | Is the strong tier (separate uids, one `sudo` at install) required in v1, or optional? | optional but recommended; exec mode only in strong |
| D-11 | Windows and macOS isolation: a separate service account (Windows) and a launchd daemon user (macOS), or same-user with OS keystores only | separate account on both, after Linux |
| D-12 | Vault's own store, or front an existing password manager (1Password, Bitwarden, pass) as the primary backend | own store with password-manager import; fronting one as a backend option |

## 15. Sources

These are vendor docs and blog posts found by search on 2026-10-16. Their claims are self-reported and were not verified independently.

| Source | Used for |
| --- | --- |
| [Fly.io, "Tokenized Tokens"](https://fly.io/blog/tokenized-tokens/) and [superfly/tokenizer](https://github.com/superfly/tokenizer) | the original sealed-token injection proxy |
| [Infisical Agent Vault](https://trilogyai.substack.com/p/agent-vault-protects-secrets) | HTTP credential proxy for agents |
| [hoophq/cloak](https://github.com/hoophq/cloak) | "protects the credential, not the access" |
| [varlock July 2026 recap](https://varlock.dev/blog/july-2026-recap/) | header-only substitution, once per request, upstream TLS binding, response scrubbing, proxy-only sandbox egress |
| [Gondolin Agent Sandbox](https://docsearch.algolia.com/mcp/docs/repo/earendil-works/gondolin) | per-secret host policy for substitution |
| [Novita sandbox secrets](https://docs.novita.ai/guides/sandbox-secret) | opaque placeholders with host allowlists |
| [Blaxel, egress proxy for agent sandboxes](https://blaxel.ai/blog/egress-proxy-api-credentials-ai-agent-sandboxes) | why environment variables are the wrong place |
| [agentpatterns.ai, sandbox credential masking](https://agentpatterns.ai/security/sandbox-credential-masking/) | the pattern in general |

## Implementation status

**M1 (2026-10-16)** lives in `vault/Corvid.Vault` (the `corvid-vault` executable), `src/Corvid.Vault.Contracts` and `src/Corvid.Vault.Client`. It covers:

- V-001: encrypted store, socket with `SO_PEERCRED`, policy, audit, CLI
- V-002: forward proxy with upstream TLS verification, header substitution and response scrubbing
- the scrub service and placeholders
- the default tier's `PR_SET_DUMPABLE=0`

It has 59 tests. S6 passes: a separate client process's memory (about 119 MB scanned through `/proc`) holds the value in none of 8 encodings, while a canary it holds is found. Native AOT publish is clean (8.6 MB).

**What S6 does and does not prove.** S6 proves the value never enters Corvid's process. It does not prove that something running as the user cannot get the value.

In the default tier, `master.cred` decrypts for anyone running as the user (`systemd-creds --user decrypt`), and `store.bin` sits next to it. So a shell tool the model drives could decrypt the store. Two changes close that:
- **Built (C-007a in `corvid-agent.md`):** Corvid's sandbox. A command run through `corvid-sandbox` cannot read or list `$XDG_DATA_HOME/corvid-vault/` or `$XDG_CONFIG_HOME/corvid-vault/` (nor the `~/.local/share` and `~/.config` defaults), and Vault's socket is out of reach: `$XDG_RUNTIME_DIR` and `/run` are hidden, and the PID namespace hides Vault's `/proc` entries. Confinement tests prove it against a temporary HOME and XDG layout. This covers only commands that go through the sandbox. The built-in shell tool (C-012) runs commands only through it and passes Vault's configured data directory, policy file and socket (`VaultLocations`, for a store kept elsewhere with `--data-dir`, `--config` or `--socket`) as extra denied paths, which the hardline list also refuses; the code tool comes later. The host must supply those locations from configuration (C-013).
- **Not built:** the strong tier puts Vault under its own uid (V-008). Until then, a same-uid process that is not sandboxed can still decrypt the store, which is the residual default-tier risk.

M1 on its own protects against accidental exposure through Corvid's own code paths: replies, logs, prompts and HTTP responses. With C-007a, sandboxed tools can no longer read Vault's files either.

**S7, CI half (C-024 in `corvid-agent.md`).** A fresh secret-shaped canary and a sentinel are minted per run (`InjectionTokens`), and `CanaryScan` looks for the canary literally, URL- and JSON-escaped, in base64 and base64url at each byte alignment, and in hex (`evals/Corvid.Evals/Injection`, unit tests in `tests/Corvid.Evals.Tests/InjectionScannerTests.cs`). `tests/Corvid.Host.Tests/InjectionCanaryTests.cs` runs the real `web_fetch` tool against a recording sink (`WebFetchTransport`) with a scripted generator: the fetched page asks for the canary to be sent to another host or embedded in a gist, the tainted turn's approval is denied, and no request carries the canary in any encoding; with the approval granted, a base64 copy in the request is flagged; an answer that echoes the canary is flagged from the transcript. This proves Corvid's taint and approval gate and the scanner, not Vault: the Vault-refuses-or-scrubs half and its audit wait for V-003, and the injected-email surface and a run against a real model are deferred.

`corvid-sandbox` is also the launcher §3.3's exec path will use (`sandbox/Corvid.Sandbox.Launcher`; no `Corvid.*` dependencies, started by path). Running it as `corvid-exec` and injecting values is not built.

Deviations from the text above:

- **The policy file is `vault.json`, not TOML.** JSON keeps Vault free of dependencies and AOT-safe through source generation. Same keys, in camelCase. §6's `approval = "keeper"` key is not implemented: `confirm` refuses until V-006.
- **Response bodies are buffered**, up to `maxResponseBytes` (16 MB by default), so scrubbing sees whole bodies. Streaming comes later.
- **Redirects are never followed.** The 3xx goes back to Corvid with its `Location` scrubbed, so a substituted header cannot follow a redirect to another host.
- **Responses are decompressed before scrubbing**, so a gzip-compressed echo of a token cannot slip past.
- **The scrubber matches the longest pattern first**, using a first-byte index rather than Aho–Corasick. That is enough for tens of secrets.
- **The scrubber does not see** a value embedded inside a larger encoded blob (for example base64 of `user:token`). The egress path keeps values out of reach in the first place.
- **Not built yet:** websocket first-frame substitution (§9) and `allowInUrl` beyond plain substitution. `allowInUrl` exists as a policy flag and is tested only through refusal.

**Model provider keys (C-015 in `corvid-agent.md`).** Corvid's cloud model slots use the forward proxy as §9 describes:
`VaultSecretHandler` (in `Corvid.Vault.Client`) puts a placeholder, minted in one Vault session per Corvid process and renewed
before it expires, into the provider's key header, and `VaultHandler` forwards the request; a cloud model has no other route.
Requests carry `Tainted` and the toolset `model-provider`, so a provider key can be limited to model calls with
`toolsets = ["model-provider"]`. A secret's `tainted` rule defaults to `ask`, so by default Vault refuses a provider key
for a tainted turn (`approval_required`, which Corvid reports and does not fall back from); a key meant to serve tainted
sessions sets `"tainted": "auto"`. §6's `role = "model-provider"` key is not implemented. Without Vault, cloud models are
unavailable (§11). A key for a local server needs an https endpoint, because Vault refuses plain HTTP.

**M2, part a (V-003a, 2026-10-22): the sandbox proxy, Vault side.** §3.1's "scripts and CLIs in the sandbox" path, in
`vault/Corvid.Vault` (`SandboxProxy.cs`, `SandboxCertificates.cs`, `Http1.cs`) with the two ops in
`src/Corvid.Vault.Contracts` and `VaultClient.OpenSandboxAsync`/`CloseSandboxAsync`. No new project or package; the
crypto is .NET's own (`CertificateRequest`, `SslStream`).

- **Per-sandbox CA.** Each `sandbox_open` creates an ECDSA P-256 root (`CN=Corvid Vault sandbox CA <id>`, basic
  constraints CA with path length 0 and key usage keyCertSign|cRLSign, both critical, a subject key identifier, a 16-byte
  random serial, valid from an hour before opening to an hour after expiry). It is signed with
  `CertificateRequest.Create` and an `X509SignatureGenerator`, not `CreateSelfSigned`, so no certificate object ever
  carries the key: the `ECDsa` in Vault's memory is the only copy, never exported, written or serialised, and disposed on
  close. Only the public certificate's PEM leaves Vault.
- **Leaves.** Minted at CONNECT for exactly the CONNECT host, which the TLS SNI must equal: an own P-256 key, SAN
  dNSName = host and nothing else, EKU serverAuth, basic constraints CA false, an authority key identifier from the CA,
  the CA's validity. Cached per sandbox as an offline `SslStreamCertificateContext` (no AIA or OCSP fetch), at most 128
  hosts per sandbox (`sandbox_limit` beyond), disposed on close.
- **Identity.** A sandbox records its opener's session id and uid, toolset, taint and network flag. Every connection on
  its socket passes the same `SO_PEERCRED` check as `vault.sock` (now a shared helper), and then acts as the opener:
  placeholders resolve only if minted for that session and uid. `Approved` is always false and `Tainted` comes from
  open, so Vault only ever becomes stricter from Corvid's flags. `Network` is the one grant, and it governs only traffic
  that carries no placeholder.
- **Proxy protocol.** One `CONNECT host:443 HTTP/1.1` per connection. Refused, with `X-Corvid-Vault-Error`, audited as
  `sandbox_connect`, and closed: any other method including absolute-form plain http (405 `not_proxyable`), another
  port or an IP literal (403 `not_proxyable`), an invalid host (400 `bad_request`), bytes sent before the 200 (400),
  an unbound host when the sandbox has no network (403 `network_not_granted`, before any TLS), and a 129th host (429
  `sandbox_limit`). Otherwise `200 Connection established`, then TLS with the host's leaf, ALPN `http/1.1` only; a
  missing or different SNI, or anything that is not a ClientHello, fails the handshake (`tls_handshake_failed`), so
  there are no raw tunnels. TLS is terminated for every accepted CONNECT, bound or not, which is how a placeholder sent
  to an unbound host is seen, refused as `host_not_bound` and audited, and how pass-through is scrubbed too.
- **Inside the tunnel** (`Http1Connection`): strict HTTP/1.1, sequential keep-alive requests until the client closes,
  sends `Connection: close`, or the sandbox ends. A header block of at most 64 KB read within 30 s (431 beyond), CRLF only
  (a bare CR or LF is 400), no obs-fold, header names must be tokens; origin-form targets only; Host must name the
  CONNECT host (with or without `:443`); a single Content-Length up to `maxResponseBytes` (413 beyond); Transfer-Encoding
  is 411 alone and 400 with Content-Length, a duplicate Content-Length is 400, Upgrade is 400 `not_proxyable`;
  `Expect: 100-continue` gets an interim 100. A malformed request ends the connection after the refusal.
- **One forward pipeline.** `VaultCore.ForwardAsync` takes an optional sandbox. The proxy builds the request with
  `Url = https://<CONNECT host><target>` (never from the inner Host header), the parsed headers, the sandbox's session,
  toolset and taint, and a peer of the connection's pid with the opener's uid. Everything V-002 does then applies
  unchanged: headers-only and once-per-request placeholders, `Resolve`, `PolicyEvaluator`, budgets, substitution with
  the scheme/host/port check, hop-by-hop removal, the response cap, decompression and scrubbing of headers, reason and
  body. Sandbox mode differs in two ways: a request with no placeholder passes through (HTTPS, upstream-verified,
  scrubbed) only when the sandbox has network and is otherwise `network_not_granted`, and the audit op is
  `sandbox_forward` with the sandbox id. Responses go back as HTTP/1.1 with a fresh Content-Length (none, and no body,
  for HEAD: the upstream length is hop-by-hop in the pipeline, so `curl -I` sees no length rather than 0); refusals are 403 for
  policy codes, 502 for `upstream_error` and `response_too_large`, 400 for `bad_request`, with the code in
  `X-Corvid-Vault-Error` and a one-line text body.
- **Lifetime.** `ExpiresAt` is the requested lifetime capped at `sandboxLifetimeMinutes` (default 720); at most
  `maxSandboxes` (default 64) are open. `sandbox_close` from the same session and uid (otherwise `unknown_sandbox`)
  cancels the sandbox, closes its listener and every live connection, deletes the socket file and disposes the CA and
  leaves. Expiry is lazy: every Vault call sweeps expired sandboxes, and accept and each request re-check, so an idle
  expired sandbox's socket file can linger until the next Vault call or Vault's exit but accepts nothing. Disposing Vault
  closes them all. The registry is created on first use, so `set`, `list` and the other CLI commands bind nothing.
- **Memory bounds** (security review of PR #58). A request body is read into a buffer that starts at 64 KiB and doubles
  as bytes arrive, so a Content-Length is a claim, not an allocation: the review's 40 tunnels each announcing 16 MB and
  sending one byte now hold 40 × 64 KiB = 2.5 MiB of body buffer (it measured 615 MB of heap before). Live connections are counted at accept, before a byte is read: at most
  `maxSandboxConnections` (16) per sandbox and `maxSandboxConnectionsTotal` (64) across all; one more gets 429
  `sandbox_limit` (audited as `sandbox_connect`) and is closed, and an idle keep-alive tunnel holds its slot until the
  30 s head timeout. Each live connection holds at most a request body and a buffered response of `maxResponseBytes`,
  so the proxy's worst case is about 2 × 64 × 16 MB = 2 GiB of bytes actually sent or received, not announced.

**Contract for V-003b** (the launcher side builds against exactly this):

1. Corvid sends `SandboxOpenRequest { session_id, toolset, tainted, network, lifetime_minutes? }` (`op: "sandbox_open"`)
   on `vault.sock` and gets `SandboxOpenResponse { sandbox_id, proxy_socket_path, ca_certificate_pem, expires_at }`, or
   an error (`sandbox_limit`, `bad_request` when the socket path would exceed 107 bytes or cannot be bound).
2. A sandbox client connects to `proxy_socket_path` (a Unix socket; V-003b bind-mounts it into the namespace and bridges
   loopback TCP to it, because `HTTPS_PROXY` must be TCP) and speaks HTTP/1.1. The first request must be
   `CONNECT <dns-host>:443 HTTP/1.1`.
3. After the `200`, the client does TLS with SNI equal to that host and verifies against `ca_certificate_pem` only.
   Inside the tunnel it sends HTTP/1.1 requests whose headers carry placeholders minted for the same session.
4. Refusals come back as 4xx or 5xx with header `X-Corvid-Vault-Error: <VaultErrorCodes value>`.
5. Corvid sends `SandboxCloseRequest { session_id, sandbox_id }` (`op: "sandbox_close"`) when the command ends; the
   socket also dies at `expires_at`.

Deviations and limits, which V-003b's end-to-end tests should expect rather than read as bugs:

- **CONNECT to DNS names on 443 only.** No other ports, no IP literals, no plain-http proxying, no non-TLS tunnels, no
  HTTP/2 to the client, no websocket upgrade (so §9's `ws_first_frame` is still not built).
- **Bodies are buffered**, requests and responses, up to `maxResponseBytes`: a large `git clone` or download through
  pass-through fails with `response_too_large`, and chunked uploads get 411.
- **No per-request `ask`.** `Approved` is always false in sandbox mode, so under the default policy a script's
  POST/PATCH/PUT with a secret refuses with `approval_required`, and a tainted sandbox's GET does too. Routing these
  through Corvid's approval channel is a follow-up.
- **Pass-through rests on Corvid's `network` flag alone.** There is no Vault-side ceiling (policy switch or host lists for
  unbound traffic) and no per-sandbox budget yet; pass-through trusts the system roots only, so upstreams with private
  CAs are unreachable.
- **No NameConstraints on the sandbox CA** (.NET has no builder, and it would conflict with pass-through hosts); the
  CA's key never leaving Vault and its lifetime are the containment.
- **Lifetime is not tied to the opener's connection** (`VaultClient` opens one connection per call): explicit close plus
  expiry.
- **No outbound exfiltration check** yet (refusing a sandboxed request whose outgoing bytes match a stored value read from
  a stray file); it belongs with S7's Vault half.
- **Default tier exposure.** Any same-uid process that can reach the sandbox socket and holds a placeholder for that
  session can use it, as with `vault.sock` today; C-007a's runtime-directory hiding and V-008 close it. In the strong
  tier the sandbox's uid may differ from Corvid's, which needs an ownership story for per-sandbox sockets (V-008).
- Linux only: on Windows, `SslStream` with ephemeral `CopyWithPrivateKey` keys needs a PFX round trip.

Tests (`tests/Corvid.Vault.Tests/SandboxProxyTests.cs`, with a loopback `TlsEchoServer` that has its own CA; Vault's
production upstream handler, from `VaultCore.CreateUpstreamHandler()`, changed only to dial it and trust its CA): the
placeholder substituted for the bound host and scrubbed back; the same placeholder to another host refused
(`host_not_bound`, audited, nothing sent); a placeholder from another session refused; no network refused at CONNECT
and inside the tunnel, pass-through with network and still scrubbed; leaves scoped to their SNI host, a mismatched SNI
failing the handshake, and one sandbox's CA not validating another's leaf; the CA key absent from the open response's
bytes, from disk and from the presented chain; the protocol refusals above (the host cap through the unit tests); close and expiry removing the socket and
ending live connections; `ask`, taint and toolset rules still applying; the per-sandbox connection cap refusing a third
idle connection with 429 and a closed one freeing its slot, the total cap across two sandboxes released by closing one,
a 16 MB claim cut short never forwarded and releasing its slot, and HEAD (forwarded and refused) on one keep-alive tunnel
without Content-Length or body. Unit tests cover the CA and leaf extensions, the leaf cache and cap, the HTTP/1.1
parser's edge cases, a body read in growing steps (no read asks for the announced length), HEAD responses and the
CONNECT target parser. The Vault suite has 154 tests (59 before). The Native AOT publish stays warning-free (8.9 MB).

**M2, part b (V-003b, 2026-10-22): the sandbox proxy, launcher side.** With it, §3.1's path for scripts and CLIs is
built end to end: a sandboxed command reaches HTTPS only through its own sandbox proxy, and never has a network of its
own. The launcher (`corvid-sandbox`) takes `--proxy-socket` and `--proxy-ca`, keeps the network namespace private,
bind-mounts the socket and the CA (and nothing else of Vault's runtime directory) under `/run/corvid-sandbox`, and runs
the command under a relay: stage 2, after Landlock and seccomp, binds 127.0.0.1:3128 and re-executes itself as the
command's parent, bridging each loopback connection to the socket byte for byte. `HTTPS_PROXY` and friends point at the
relay; `SSL_CERT_FILE`, `CURL_CA_BUNDLE`, `REQUESTS_CA_BUNDLE`, `NODE_EXTRA_CA_CERTS`, `GIT_SSL_CAINFO` and `PIP_CERT` name
the sandbox CA alone. Corvid's shell opens a sandbox per command when `Corvid:Tools:ShellNetwork` is on (toolset
`shell`, network true, the turn's taint) and closes it when the command ends, expiry as the backstop; a tainted turn's
command gets the route only when a person approved it as network egress, and a scheduled script from a tainted job
never does. When the route is due and Vault cannot open it, the command does not run. Details in
[architecture](../architecture.md#sandbox-egress-through-vault-v-003b).

As the contract above expects:

- `SO_PEERCRED` through bubblewrap's user and PID namespaces maps to the real uid and the global pid, so the relay's
  connections pass Vault's peer check as the opener; the end-to-end tests prove it rather than assume it.
- The tests bind the secret to `bound.test` and reach a loopback `TlsEchoServer` through Vault's production upstream
  handler: curl and Python's `urllib` with a placeholder are substituted and scrubbed; pass-through needs network and is
  scrubbed; without network an unbound host is 403 `network_not_granted`; there is no direct egress at all.
- A secret meant for scripts lists `shell` in its `toolsets`, or none.

Deviations and limits:

- No way yet for the model to put a placeholder into a shell command: the shell has no `SecretRef` parameter, and §3.1's
  `GITHUB_TOKEN=<placeholder>` environment form is refused by the sandbox's secret-name rule and needs its own launcher
  option. Substitution works only for placeholders the caller already holds; in practice v1 is pass-through, scrubbed.
- One global switch and the constant toolset `shell`: per-toolset grants and a bound-hosts-only mode (`network` false)
  wait for toolset objects.
- MCP stdio servers (`Prepare` refuses a proxy), skill scripts and tainted scheduled scripts get no route.
- S6 is not yet extended to the relay and the command (the value never enters the sandbox by design, unproven by a
  memory scan); per-request `ask` is still absent (`approval_required` for a POST with a secret).
- Clients beyond curl and Python's `urllib` are untested (gh, git over https, Node with `NODE_USE_ENV_PROXY`, pip,
  cargo, Java trust stores, Go, .NET, an `AWS_CA_BUNDLE` equivalent); those that ignore the variables fail closed.
- The sandbox's lifetime is not tied to the launcher process: a crashed Corvid leaves the socket until expiry.
- `/sys/class/net` inside the sandbox shows the host's interfaces (the host's sysfs is bound read-only); `/proc/net/dev`
  shows loopback only, and that is what the tests check.

Next: S6 for sandboxed processes, per-request `ask` through Corvid's approval channel, and a placeholder path into
shell commands (§13).

## Changelog

| Version | Date | Change |
| --- | --- | --- |
| 0.1 | 2026-10-16 | Design. |
| 0.2 | 2026-10-16 | M1 built; deviations recorded under "Implementation status". |
| 0.3 | 2026-10-18 | Implementation status: Corvid's sandbox (C-007a) hides Vault's directories and socket from sandboxed commands; the strong tier (V-008) remains. |
| 0.4 | 2026-10-22 | §13 and Implementation status: S7's CI half is built (C-024); the Vault half waits for V-003. |
| 0.5 | 2026-10-22 | §13: V-003 split into V-003a (built: the Vault side of the sandbox proxy) and V-003b (pending: the launcher side). Implementation status: M2 part a, the V-003b protocol contract and its deviations. |
| 0.6 | 2026-10-22 | §13 and Implementation status: V-003b built (the launcher side and Corvid's shell route), M2 part b and its deviations. |
