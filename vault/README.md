# corvid-vault

Holds the secrets Corvid's tools need, in a separate process that runs no model. Corvid sees names and placeholders; Vault substitutes real values on the way out to hosts each secret is bound to, and scrubs them from everything coming back. The design is in [`docs/plan/vault.md`](../docs/plan/vault.md).

Status: **M1**, Linux only, default tier (same uid, non-dumpable). See "What M1 does not do yet" below.

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

Native build (needs clang): `dotnet publish vault/Corvid.Vault -c Release -r linux-x64 -p:PublishAot=true` gives one 8.6 MB binary with no warnings.

## Policy (`vault.json`)

```jsonc
{
  "allowPlainHttpLoopback": false,     // tests and local services only
  "allowedUids": [],                   // empty: Vault's own uid
  "placeholderLifetimeMinutes": 720,
  "maxResponseBytes": 16777216,
  "scrubRequestsPerMinute": 600,
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

## Guarantees and their tests

| Property | Test |
| --- | --- |
| The value never enters the client process (S6) | `S6_the_value_never_enters_the_client_process`: a separate process uses the secret; its memory, read through `/proc`, holds the value in none of 8 encodings, while a canary it holds is found |
| Values go only to bound hosts, over HTTPS | `A_host_the_secret_is_not_bound_to_is_refused_before_anything_is_sent`, `PolicyTests` |
| Headers only, once per request | `Placeholders_in_the_body_or_url_or_repeated_are_refused` |
| Placeholders are bound to session, uid and lifetime | `A_placeholder_from_another_session_is_refused`, `PlaceholderTests` |
| Echoes are scrubbed, including compressed and redirected ones | `The_upstream_gets_the_value_and_Corvid_gets_it_scrubbed`, `Compressed_echoes_…`, `Redirects_are_returned_not_followed_…` |
| Store is encrypted, 0600, and needs the key | `StoreTests` |
| Other uids are rejected | `Other_uids_are_rejected_and_audited` |

## What M1 does not do yet

Deferred, in `vault.md` §13 order: sandbox proxy for scripts (V-003, next), ssh-agent (V-004), paste interception in Corvid (V-005), out-of-band confirm (V-006, so `confirm` refuses), OAuth (V-007), the strong-tier installer with separate uids (V-008), exec mode and SigV4 (V-009). Also not yet: websocket first-frame substitution, streamed response bodies (M1 buffers up to `maxResponseBytes`), Windows and macOS.
