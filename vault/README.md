# corvid-vault

Holds the secrets Corvid's tools need, in a separate process that runs no model. Corvid sees names and placeholders; Vault substitutes real values on the way out to hosts each secret is bound to, and scrubs them from everything coming back. The design is in [`docs/plan/vault.md`](../docs/plan/vault.md).

Status: **M1** plus **M2's Vault side** (V-003a, the sandbox proxy), Linux only, default tier (same uid, non-dumpable). The launcher side that points sandboxed commands at the proxy is V-003b. See "What Vault does not do yet" below.

> **Default-tier limit.** Corvid's process never holds a value (test S6). But anything running as your user can run `systemd-creds --user decrypt` on `master.cred` and read the store. A shell tool driven by a model is safe from this only once Corvid's sandbox denies Vault's directories (C-007), or once Vault runs as its own user (strong tier, V-008).

## Use

```sh
dotnet build Corvid.slnx
alias corvid-vault=vault/Corvid.Vault/bin/Debug/net10.0/corvid-vault   # or publish natively, below

corvid-vault init                                   # master key (systemd-creds --user) and a starter vault.json
corvid-vault set github.token --description "GitHub API, repo scope"   # value read without echo
corvid-vault list                                   # names, bound hosts, descriptions; never values
corvid-vault serve                                  # listen on $XDG_RUNTIME_DIR/corvid-vault/vault.sock
corvid-vault audit --tail 20
```

Files:

| Path | What |
| --- | --- |
| `$XDG_DATA_HOME/corvid-vault/master.cred` | 256-bit master key, encrypted by `systemd-creds --user` (host- and user-bound; TPM where available) |
| `$XDG_DATA_HOME/corvid-vault/store.bin` | AES-256-GCM store; names and values both encrypted |
| `$XDG_DATA_HOME/corvid-vault/audit.jsonl` | every use, refusal and scrub hit; names, never values |
| `$XDG_CONFIG_HOME/corvid-vault/vault.json` | policy (below) |
| `$XDG_RUNTIME_DIR/corvid-vault/vault.sock` | the socket, 0600 in a 0700 directory |
| `$XDG_RUNTIME_DIR/corvid-vault/sandbox/<id>.sock` | one HTTPS proxy socket per open sandbox (V-003a), 0600 in a 0700 directory, removed on close or expiry |

Native build (needs clang): `dotnet publish vault/Corvid.Vault -c Release -r linux-x64 -p:PublishAot=true` gives one 8.6 MB binary with no warnings.

## Policy (`vault.json`)

```jsonc
{
  "allowPlainHttpLoopback": false,     // tests and local services only
  "allowedUids": [],                   // empty: Vault's own uid
  "placeholderLifetimeMinutes": 720,
  "maxResponseBytes": 16777216,
  "scrubRequestsPerMinute": 600,
  "sandboxLifetimeMinutes": 720,       // cap on a sandbox proxy's lifetime
  "maxSandboxes": 64,                  // sandbox proxies open at once
  "maxSandboxConnections": 16,         // live connections to one sandbox proxy
  "maxSandboxConnectionsTotal": 64,    // live connections to all sandbox proxies
  "secrets": {
    "github.token": {
      "hosts": ["api.github.com", "uploads.github.com"],   // or "*.example.com"
      "methods": { "GET": "auto", "POST": "ask", "DELETE": "confirm" },  // unlisted methods are denied
      "paths": [],                                          // optional allow list, * globs
      "denyPaths": ["/user/keys*", "/repos/*/*/hooks*"],
      "toolsets": ["github"],                               // empty: any toolset
      "requestsPerHour": 300,
      "tainted": "ask",                                     // floor for turns holding untrusted content
      "allowInUrl": false                                   // Telegram-style tokens in the URL path
    }
  }
}
```

Without `methods`, GET and HEAD are `auto` and everything else is `ask`. `ask` is satisfied by Corvid's own approval (the request carries `Approved`); `confirm` needs out-of-band confirmation, which M1 does not have, so it refuses. A secret with no `hosts` is stored but unusable. Vault reloads the file when it changes.

## From Corvid

```csharp
var vault = new VaultClient(sessionId);
string token = await vault.MintPlaceholderAsync(new SecretRef("github.token"));   // vsec_github.token_…

using var http = new HttpClient(new VaultHandler(vault));
using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
request.Headers.Authorization = new("Bearer", token);
request.Options.Set(VaultHandler.Toolset, "github");
using var response = await http.SendAsync(request);   // sent by Vault; body and headers scrubbed

string safe = await vault.ScrubAsync(toolOutput);      // every tool result, before the event log
```

Requests without a placeholder go straight to the inner handler; requests with one never do.

For a sandboxed command (V-003a; the launcher wiring is V-003b):

```csharp
SandboxOpenResponse sandbox = await vault.OpenSandboxAsync(toolset: "shell", tainted: false, network: false);
// sandbox.ProxySocketPath: an HTTP CONNECT proxy on a Unix socket (DNS names on port 443 only)
// sandbox.CaCertificatePem: the only CA the sandboxed client should trust; its key never leaves Vault
// ... run the command; its requests carry placeholders minted for the same session ...
await vault.CloseSandboxAsync(sandbox.SandboxId);
```

Vault terminates TLS with a leaf minted for the CONNECT host, substitutes placeholders exactly as for Corvid's own requests (bound hosts only, headers only, never pre-approved, so `ask` rules refuse) and scrubs every response. Without `network`, only hosts some secret is bound to are reachable, and only with a placeholder; with it, other HTTPS requests pass through, still scrubbed. Refusals carry `X-Corvid-Vault-Error`. The protocol contract is in `docs/plan/vault.md`, "Implementation status".

## Guarantees and their tests

| Property | Test |
| --- | --- |
| The value never enters the client process (S6) | `S6_the_value_never_enters_the_client_process`: a separate process uses the secret; its memory, read through `/proc`, holds the value in none of 8 encodings, while a canary it holds is found |
| Values go only to bound hosts, over HTTPS | Host binding: `A_host_the_secret_is_not_bound_to_is_refused_before_anything_is_sent`. HTTPS-only: evaluator unit test only. The end-to-end tests use plain HTTP to loopback, so TLS on the wire and upstream certificate checks (`SocketsHttpHandler` defaults) are not exercised by the suite. |
| Headers only, once per request | `Placeholders_in_the_body_or_url_or_repeated_are_refused` |
| Placeholders are bound to session, uid and lifetime | `A_placeholder_from_another_session_is_refused`, `PlaceholderTests` |
| Echoes are scrubbed, including compressed and redirected ones | `The_upstream_gets_the_value_and_Corvid_gets_it_scrubbed`, `Compressed_echoes_…`, `Redirects_are_returned_not_followed_…` |
| Store is encrypted, 0600, and needs the key | `StoreTests` (with the test-only `file` key provider). The `systemd` key provider was checked by a manual smoke run on 2026-10-16 (init, set, list, AOT binary), not by the suite. |
| Other uids are rejected | `Other_uids_are_rejected_and_audited` |
| Sandbox proxy: values go only to bound hosts, over verified HTTPS, and come back scrubbed | `The_placeholder_is_substituted_for_the_bound_host_and_scrubbed_back`, `The_placeholder_sent_to_another_host_is_refused_and_audited`, `With_network_a_request_carrying_no_placeholder_passes_through_and_is_still_scrubbed`. Unlike the M1 end-to-end tests, these run real TLS on both legs, with Vault's production upstream handler trusting only the test CA. |
| Sandbox proxy: no network means no unbound traffic | `Without_network_an_unbound_host_is_refused_at_CONNECT`, `Without_network_a_request_carrying_no_placeholder_is_refused_even_to_a_bound_host` |
| Sandbox CA: key never leaves Vault; leaves scoped to one SNI host and one sandbox | `The_CA_private_key_never_leaves_Vault`, `Leaves_are_scoped_to_the_SNI_host_and_to_their_sandbox`, `An_SNI_that_differs_from_the_CONNECT_host_fails_the_handshake`, `SandboxCertificateTests` |
| Sandbox proxy: CONNECT to DNS names on 443 only, strict HTTP/1.1, no raw tunnels | `Anything_but_CONNECT_to_a_DNS_name_on_443_is_refused`, `Plain_bytes_after_CONNECT_fail_the_handshake_…`, `Inside_the_tunnel_…_are_refused`, `Http1Tests` |
| Sandbox proxy: close and expiry end everything; never pre-approved | `Closing_removes_the_socket_and_ends_live_connections`, `Expiry_closes_the_sandbox_like_close_does`, `Ask_and_tainted_rules_still_refuse_…` |

## What Vault does not do yet

Deferred, in `vault.md` §13 order: the launcher side of the sandbox proxy (V-003b, next: socket into the sandbox, a loopback bridge for `HTTPS_PROXY`, the CA in the sandbox's trust variables), ssh-agent (V-004), paste interception in Corvid (V-005), out-of-band confirm (V-006, so `confirm` refuses), OAuth (V-007), the strong-tier installer with separate uids (V-008), exec mode and SigV4 (V-009). Also not yet: websocket first-frame substitution, streamed bodies (Vault buffers up to `maxResponseBytes`, both for Corvid's requests and through the sandbox proxy), per-request `ask` approval for sandboxed requests, sandbox proxying beyond CONNECT to DNS names on 443, Windows and macOS.
