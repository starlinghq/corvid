# Spike S3: GLiNER2 on shell-argument targets and PII labels

Plan §18, backlog C-003. Run 2026-10-21.

**Result: fail.** The best label set and threshold, chosen on the tune half, finds 24% of paths, 0% of PIDs and 13% of hosts exactly on the report half. The pass condition was a recall of at least 0.95 on each. Even when partly overlapping spans count, recall stays at 67%, 0% and 22%. No label set and threshold reaches 0.5 exact recall on any of the three kinds in either half. Under a more lenient token-containment scoring (added in review), paths reach 0.63 in the chosen configuration and 0.79 at best, but PIDs stay at or below 0.06 and hosts at or below 0.28, so the result is a fail under every scoring tried.

**Decision:** GLiNER2 is not used to extract approval targets from shell commands. The structural analyzer (`Corvid.Approvals`) is the only source of approval targets. This PR records the result in §18 and in the C-003 row. It proposes, but does not make, the matching edits to §10.2 step 2, C-007b and C-015 (see [what the result means](#what-the-result-means)).

## Question and pass condition

| Spike | Question | Pass condition |
| --- | --- | --- |
| S3 | GLiNER2 on shell-argument targets and PII labels | recall ≥ 0.95 on paths, hosts and PIDs in a 300-command corpus that includes Hermes' published bypasses |

The pass condition applies to each label separately: a pooled 0.95 with PIDs at 0.80 would fail. The plan sets no threshold for PII. The PII numbers below are measured and reported, but they do not decide pass or fail.

## Setup

- **Model:** `gliner2-base-v1` (fastino) from `Starlings.Models.Gliner2` 0.3.0. It runs in-process through Corvid's `IExtractor` adapter (`Gliner2Extractor` in `Corvid.Models.Starling`) on the CPU, with `MALLOC_MMAP_THRESHOLD_=65536` (Starling's requirement on Linux). Corvid's `StarlingModelStore` resolved the model from `~/.cache/starlings/models` offline and never downloaded anything.
- **Requests:** one call per command with all of a set's labels and the threshold, as the adapter is used everywhere else. There were 2,700 calls (300 commands × 3 label sets × 3 thresholds). Each took 45 ms at the median and 67 ms at p95.
- **Code:** `evals/spikes/S3-gliner2-targets/`, a console project. It is not part of `Corvid.slnx`, and CI neither builds nor runs it. The architecture check (`tools/Corvid.Architecture`, `BoundaryCheck.CheckRepository`) scans only `src`, `vault` and `sandbox`, so the spike needs no exclusion there. `dotnet build Corvid.slnx` is unchanged. To reproduce:

  ```
  dotnet build evals/spikes/S3-gliner2-targets
  dotnet evals/spikes/S3-gliner2-targets/bin/Debug/net10.0/S3.Gliner2Targets.dll corpus .    # no model
  MALLOC_MMAP_THRESHOLD_=65536 dotnet .../S3.Gliner2Targets.dll targets .                    # GLiNER2
  dotnet .../S3.Gliner2Targets.dll rescore .                                                 # no model
  MALLOC_MMAP_THRESHOLD_=65536 dotnet .../S3.Gliner2Targets.dll pii .                        # GLiNER2
  ```

  Set `CORVID_STARLING_MODELS` to use a model cache other than the default.

## Corpus construction

The corpus, its labels and its split were committed before any model ran. They are in `fixtures/targets-corpus.jsonl` and `fixtures/targets-dropped.jsonl`.

**Commands.** Candidates were taken in this order until 300 were kept:

1. Every command line in `tests/Corvid.Approvals.Tests/BypassTests.cs`. These are Hermes' published bypasses (#84551, #74078) and their relatives: `Denied`, `Asked`, and the inline theories.
2. Every command line in `LauncherTests.cs`.
3. The commands in `everyday.txt`. These are 286 realistic commands, written by hand as commands only, never labelled. They carry most of the hosts and PIDs, which the tests rarely contain.
4. Then `AnalyzerTests`, `PipelineTests`, `HardlineTests` and `ParserTests`. The corpus was full before these were reached.

Seeds were taken from `TheoryData` blocks and from the first argument of `[InlineData]`, `Analyze(...)` and `ShellAction(...)`. A literal without a space is a fragment, such as a glob or a file name, and was skipped.

**Labels.** All labels come from the structural analyzer, through `ApprovalPipeline.Analyze` in the same environment the approval tests use (workspace `/home/user/work/project`, home `/home/user`). The analyzer's `Path`, `Process`, `Host` and `Url` targets become the gold spans `path`, `pid`, `host` and `url`. `ProcessName` targets (`pkill node`) are kept as `process_name` but not scored. None of the 300 rows was labelled by hand. Three mechanical mappings recover the text as written, because the analyzer records a word after static expansion:

- `/home/user/...` maps back to `~/...`, `$HOME/...` or `${HOME}/...`, and the data home maps back to `$XDG_DATA_HOME/...`, whichever appears in the command.
- `--output=log.txt` maps to `log.txt`, the option's value.
- A derived tree such as `dir/*` (from `tar -C dir` or `unzip -d dir`) maps to `dir`.

Some targets have no text at all and are not scored (18 such targets): a program's default output (`nohup.out`, `perf.data`) and the working tree that `git` or `wget` writes into (`*`).

**Kept only if the analyzer parses the command exactly.** That means: inside the parsed subset, no `Unresolved` reason, no opaque script text, no dynamic target, and every target found in the command as written. Of 391 candidates examined, 91 were dropped. Another 298 were never examined because the corpus was full.

| Dropped | Count | Sources |
| --- | --- | --- |
| unresolved (wrappers that always ask, `zsh -c`, `source`, `PATH=`/`LD_PRELOAD=`, `script -c`, piped shells, computed programs and options) | 68 | BypassTests.Denied 15, BypassTests.Asked 26, LauncherTests 25, everyday 2 |
| duplicate | 9 | BypassTests.Denied 1, LauncherTests 8 |
| dynamic target (`rm -rf "$TARGET"`, `$(echo /)`) | 7 | BypassTests.Denied 1, BypassTests.Asked 6 |
| opaque script (`python3 -c`, `node -e`) | 3 | BypassTests.Denied 3 |
| not as written (`D=corvid-vault; cat ~/.local/share/$D/...`, `tar -C dir name`) | 2 | BypassTests.Denied 2 |
| outside the parsed subset (`if ... fi`) | 2 | BypassTests.Denied 2 |

**Kept:** 300 commands: BypassTests 151 (Denied 118, Asked 11, inline 22), LauncherTests 18, everyday 131.

**Scored targets:** 192 paths, 69 PIDs, 68 hosts and 24 URLs. Eighteen commands have no target, and they count only toward precision.

**Audited over-approximations: 12 targets, kept in the fixture but not scored.** The approval analyzer is deliberately conservative. It treats any operand of a network program as a host and any operand of a file program as a path. That is right for approvals but wrong as span labels. Reading every labelled row once turned up these targets, which are now marked `scored: false`:

- `ext4` in `mkfs -t ext4` (the file system type).
- `TODO` in `rg --pre ./decode TODO` and in `ag`.
- `{}` in `find -exec`.
- `2600` in `gdb -p 2600` (read as a file).
- `GET` in `http GET url`.
- `MX`, `+short` and `txt` in `dig`.
- `-` and `TCP:localhost:6379` in `socat`.

A GLiNER2 span on one of these counts neither as a hit nor as a false positive. Nothing was added by hand. The analyzer also records no PID for inspection tools (`gdb -p`, `lsof -p`, `/proc/2468/status`). A GLiNER2 PID span there counts as a false positive, so precision is a lower bound.

**Split.** The corpus is split into a tune half (152 commands: 96 paths, 35 PIDs, 36 hosts, 13 URLs) and a report half (148 commands: 96 paths, 34 PIDs, 32 hosts, 11 URLs). The split is stratified by which target kinds a command has. Within each stratum, commands are ordered by the SHA-256 of their text and assigned to the halves alternately. The configuration was chosen on the tune half only, by the highest worst-case recall over path, PID and host, with mean precision as the tie-break. The headline numbers are from the report half.

## Scoring

- **Exact (the gate):** a span of the label's kind equals the target as written, ignoring surrounding quotes and a trailing `;` or `,`. For a host, the bare host name the analyzer resolved also counts, so `example.com` matches in `https://example.com/x` and in `user@example.com:path`. For a PID, the value also counts.
- **Token containment (added in review, not the gate):** the span, split on whitespace and `=`, has a token equal to the target as written or to its resolved value. Each token has its quotes, a trailing `;` or `,` and a trailing `/` removed, and `~`, `$HOME` or `${HOME}` maps to the home directory. For a host, the host name inside a URL or `user@host` token also counts. `-rf /` contains `/` and `rm -rf /etc` contains `/etc`, but a span `/etc/passwd` does not contain `/etc`. This scoring sits between exact and overlap. It is computed from the committed predictions by the `rescore` command, without the model, and written to `results/targets-contained.md`.
- **Overlap (an upper bound, not the gate):** the span overlaps an occurrence of the target, or of its host name, in the command. `-rf /` overlaps `/`, for example.
- **Precision:** the share of distinct spans per command that exactly match a scored target of the same kind.

## Label sets and thresholds

| Set | Labels (kind) | Origin |
| --- | --- | --- |
| plan | `file path` (path), `process` (pid), `host`, `database table`, `credential` (not scored) | plan §10.2 step 2, the set Corvid specifies for this job; it has no URL label |
| wire | `path`, `process` (pid), `host`, `url` | the kinds the analyzer emits (`WireNames.Kind`) |
| descriptive | `file or directory path`, `process id`, `hostname or IP address`, `URL` | a phrasing written for this spike |

The thresholds were 0.3, 0.4 (the egress gate's default) and 0.5. Each configuration ran natively at its own threshold.

## Results

The chosen configuration, on the tune half, was **plan @ 0.5**.

Report half (held out). Each cell gives exact recall / overlap recall, then precision, then the gold count:

| Labels | Threshold | path | pid | host | url | worst gate recall |
| --- | --- | --- | --- | --- | --- | --- |
| plan | 0.3 | 0.292 / 0.771, P 0.217 (96) | 0.000 / 0.000, P 0.000 (34) | 0.188 / 0.313, P 0.222 (32) | – | 0.000 |
| plan | 0.4 | 0.250 / 0.708, P 0.211 (96) | 0.000 / 0.000, P 0.000 (34) | 0.156 / 0.281, P 0.227 (32) | – | 0.000 |
| **plan (chosen)** | **0.5** | **0.240 / 0.667, P 0.235 (96)** | **0.000 / 0.000, P 0.000 (34)** | **0.125 / 0.219, P 0.222 (32)** | – | **0.000** |
| wire | 0.3 | 0.240 / 0.646, P 0.223 (96) | 0.000 / 0.000, P 0.000 (34) | 0.094 / 0.219, P 0.079 (32) | 0.182 / 0.273, P 0.025 (11) | 0.000 |
| wire | 0.4 | 0.219 / 0.625, P 0.236 (96) | 0.000 / 0.000, P 0.000 (34) | 0.094 / 0.156, P 0.107 (32) | 0.182 / 0.273, P 0.030 (11) | 0.000 |
| wire | 0.5 | 0.198 / 0.573, P 0.247 (96) | 0.000 / 0.000, P 0.000 (34) | 0.063 / 0.125, P 0.080 (32) | 0.182 / 0.273, P 0.034 (11) | 0.000 |
| descriptive | 0.3 | 0.427 / 0.854, P 0.363 (96) | 0.000 / 0.059, P 0.000 (34) | 0.063 / 0.281, P 0.038 (32) | 0.273 / 0.273, P 0.075 (11) | 0.000 |
| descriptive | 0.4 | 0.396 / 0.781, P 0.388 (96) | 0.000 / 0.000, P 0.000 (34) | 0.031 / 0.250, P 0.025 (32) | 0.273 / 0.273, P 0.088 (11) | 0.000 |
| descriptive | 0.5 | 0.333 / 0.708, P 0.376 (96) | 0.000 / 0.000, P 0.000 (34) | 0.031 / 0.219, P 0.029 (32) | 0.273 / 0.273, P 0.111 (11) | 0.000 |

The tune half and both halves together show the same picture. The best exact recall in any cell is 0.43 for paths, 0.22 for hosts and 0.27 for URLs, and the best PID recall is 0. The full grid for all three splits is in `evals/spikes/S3-gliner2-targets/results/targets.md`. Every span from every run is in `results/targets-predictions.jsonl`.

**Token containment** does not change the result. On the report half, with same-label spans:

| Labels | Threshold | path | pid | host |
| --- | --- | --- | --- | --- |
| plan (chosen) | 0.5 | 0.625 | 0.000 | 0.188 |
| plan | 0.3 | 0.708 | 0.000 | 0.281 |
| descriptive | 0.3 | 0.792 | 0.059 | 0.281 |

No cell in any split goes above 0.06 for PIDs or 0.28 for hosts. The full grid is in `results/targets-contained.md`. Path recall roughly triples under this scoring because most path misses are spans that also take in an option or the verb (see below). Even so, the best path figure, 0.79, is still far from 0.95.

Because the result fails by such a wide margin on every row, the choice of configuration does not matter. On the tune half every configuration has a worst-case exact gate recall of 0, so the choice was in effect made by the precision tie-break. Even the tune-half optimum with the best report-half path recall (descriptive @ 0.3) would miss 57% of paths and every PID.

### How it fails

- **Span boundaries.** GLiNER2 does find something near most paths: overlap recall is 0.67–0.85 against an exact recall of 0.24–0.43. But the span takes in the option or the verb, as in `-rf /`, `rm -rf /etc`, `-rf ../..` and `~/.ssh /tmp/k` (one span covering both operands of `mv`). The analyzer would need to re-segment these spans to use them, at which point it already has the answer.
- **The bypass shapes are the worst.** By path shape on the report half (chosen configuration):

  | Shape | Gold | Recall |
  | --- | --- | --- |
  | `/`, `.`, `..`, `~`, `*` | 39 | 0.128 |
  | bare name (`build`, `id_rsa`) | 28 | 0.357 |
  | `~/...` | 9 | 0.444 |
  | absolute | 7 | 0.000 |
  | glob | 5 | 0.200 |
  | `/dev/...` | 5 | 0.600 |
  | relative with a directory | 3 | 0.000 |

  The dangerous targets, such as `/`, `~`, `..`, `$HOME` and `/home/user/work/proj*`, are exactly the ones it misses. Heredocs and substitutions (`bash <<'EOF'⏎rm -rf /⏎EOF`) return no span at all.
- **PIDs: zero, under every phrasing.** `kill -9 1234`, `kill 4242` and `taskset -p 0x3 9000` return no PID span. When the `process` label fires, it fires on the program name: `kill` 0.79, `ltrace` 0.95, `strace` 0.90. The descriptive `process id` label sometimes tags `-9 1234` as a host. Counting a span under any label, token containment finds 0.74 of report-half PIDs at descriptive @ 0.3, against 0.06 under the PID label. The model often puts a span over the PID, but under the wrong kind and usually with the option attached, which is no use as a target.
- **Hosts.** `ssh deploy@prod-web-01`, `ping -c 4 8.8.8.8`, `nslookup corvid.example.org` and every `curl https://...` URL miss the host label entirely. The URL label fires on URLs, but with precision of 0.03–0.11, because it also tags paths and `user@host` strings. Taking the host name out of a span under any label (a URL, for example) raises host containment to at most 0.63 on the report half.

## PII (privacy egress, C-015)

The labels were `EgressPolicy.DefaultLabels` exactly: person, email, phone number, address, account number and credential. One call was made per text, with the same filters as `PrivacyEgress.ScanAsync`: confidence at or above the threshold, at least 2 characters, at least one letter or digit.

The fixture is `fixtures/pii.jsonl`: 50 hand-written texts with 57 hand-labelled values (person 16, email 8, phone number 9, address 7, account number 8, credential 9). Ten of the texts contain no PII. Every value is fictitious or a documented test value.

The gate redacts a value whatever label found it, so the column that matters is "any label, covered": every character of the value lies inside some kept span.

| Label | Gold | Same label | Any label, exact | **Any label, covered** | Precision, same label |
| --- | --- | --- | --- | --- | --- |
| person | 16 | 0.938 | 0.938 | **1.000** | 0.615 |
| email | 8 | 1.000 | 1.000 | **1.000** | 0.800 |
| phone number | 9 | 1.000 | 1.000 | **1.000** | 0.900 |
| address | 7 | 0.429 | 0.429 | **0.429** | 0.750 |
| account number | 8 | 0.875 | 1.000 | **1.000** | 0.467 |
| credential | 9 | 0.000 | 0.444 | **0.556** | 0.000 |
| all | 57 | 0.737 | 0.825 | **0.860** | 0.582 |

These figures are at the gate's default threshold, 0.4. At 0.3, covered recall is 0.877 overall and 0.667 for credentials. At 0.5, it is 0.842 overall and 0.444 for credentials. Person, email and phone recall do not change across the three thresholds. All three tables are in `results/pii.md`.

- **Names, emails, phone numbers and account numbers are found reliably.** The gate's main job works on this set.
- **The credential label finds the noun, not the secret.** It tags `API key`, `password`, `wifi password` and `SSH private key`, never the value after them. Some values are caught under other labels, but `hunter2-Staging!`, `Tr0ub4dor&3`, `ghp_...` and `correct-horse-battery-staple` would be sent as they are.
- **Full postal addresses are often partial or missed:** 4 of 7, including `221B Baker Street, London NW1 6XE`.
- **False positives are pronouns and nouns:** `you`, `me` and `We` as person, `platform team`, and `IBAN`. They cost needless placeholders, not leaks.

These are 50 texts, too few for a confident rate. The credential and address gaps are the findings, not the exact decimals.

## What the result means

1. **The analyzer stays authoritative for approval targets, and GLiNER2 is not added to them.** Recall this low would hide targets from the risk question and from structural rules. The spans that do appear need re-segmentation the parser already does exactly. No phrasing or threshold comes near the bar.
2. **Where GLiNER2 might still serve shell targets: nowhere in the approval decision.** The analyzer already refuses to auto-approve what it cannot read: unresolved commands, opaque `python -c` text and dynamic targets all ask. GLiNER2 spans could at most be shown beside such a prompt as unverified hints, under the same rule as slot pre-fill (§7: a hint, never a veto, never a gate input). That display has not been measured and is not built. Free-form arguments to tools that are not shells (C-005 `ToolAction`) keep their parameter-name rule.
3. **Proposed plan changes** (left for a plan revision, not made in this PR):
   - §10.2 step 2 stops using GLiNER2 for paths, PIDs (`kill` targets) and hosts, which are what this spike measured. Its other free-form arguments, SQL strings and `curl` data, were not measured: the `database table` and `credential` labels were sent but not scored, and the corpus has command lines, not payloads. Whether to drop those too, or measure them, is left to the plan revision.
   - C-007b's "Deferred: GLiNER2 target extraction (S3)" is closed as not to be built.
   - C-007b stays unblocked: it never depended on GLiNER2 for its targets.
4. **PII egress (C-015) keeps GLiNER2, with two gaps to close outside this spike.**
   - Credential *values* need a deterministic detector next to the model. Options include Vault's exact-value scrubbing, token-shape patterns such as `ghp_`, `xox[bp]-`, `sk-` and AWS keys, and a value-oriented label such as "password or secret value", which this spike did not measure.
   - Addresses need a better label or a second label.

   Neither gap changes the gate's fail-closed behaviour. Both are proposed for C-015's deferred list, in place of "spike S3's PII recall".

## Limits

- One model (gliner2-base-v1, CPU) and one adapter path. The PII results show the same model and adapter working on prose, so the target result describes the model on shell text rather than a broken setup.
- The commands were given to GLiNER2 bare, as the plan describes. Wrapping each command in prose ("The agent wants to run: ...") was not tried. Given span boundaries like `-rf /` and zero PIDs, it is unlikely to reach 0.95.
- In review, the `corpus` command regenerated the fixtures byte for byte, and a rerun of `targets` reproduced `targets-predictions.jsonl` byte for byte. Only the timing line changed.
- The corpus keeps only commands the analyzer parses exactly, because its labels come from the analyzer. So GLiNER2 was measured only where the analyzer already has the answer. It was never measured on the 82 dropped commands that are not duplicates (unresolved, dynamic, opaque, outside the subset or not as written), where the analyzer has no exact answer. That gap does not affect the approval decision, because the analyzer asks on all of those (point 2 above). But this spike does not show how GLiNER2 does on them.
- The PID and host counts per half (34 and 32 on the report half) are small, but a recall of 0.00 is not a sampling artefact.
