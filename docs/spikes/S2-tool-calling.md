# Spike S2: does the fabric make Qwen3.5-0.8B a usable tool caller?

C-003 and C-034, spike S2 of [the plan](../plan/corvid-agent.md#phase-0-prove-the-fabric-about-2-weeks), run 2026-10-22
at Corvid `f660341` (the branch's scorer fix; the harness commit `67f090a`'s run made the same calls, see Load).

**Question.** On a 200-case golden set of personal-assistant tasks, compare (a) Qwen alone with all tools, (b) the
embedding shortlist, (c) the shortlist plus Kev's `tool` pick, one schema and repair, on correct tool, argument match,
prose instead of a call, escalation rate and latency.

**Pass condition.** (c) beats (a) by ≥ 25 points on tool choice and argument exact match; escalation rate below 30%.

## Result

**Fail, in the opposite direction: (c) is 17.8 points below (a) on tool choice and 18.8 below on argument match.**
Escalation is 0% everywhere. Correct means the expected tools ran and nothing else did; a turn that ran the expected
tool and then another one is over-called, a miss (see Scoring). Two separate things are behind the result, and neither
is the shortlist or Kev's pick itself:

- **Qwen alone picks the right tool first but often does not stop.** (a), with all 28 tools in the prompt, ran the
  right tool first on 179 of 208 cases (86.1%), but on 31 of them went on to a tool nobody asked for: `shell` after a
  read, write or search 13 times, `web_fetch` after a web search, `memory_reject` after `memory_add`, a calendar event
  after a message, `patch_file` after a read. Strictly it is right on 148 (71.2%), arguments on 144 (69.2%), behind
  (b)'s 155 (74.5%), which over-calls 14 times with five tools in view. 0 invalid names and 5 prose answers in 191 cases
  that need a tool. (a) also costs latency and context: a 3,334-token first prompt and 8.1 s per turn at the median
  (p90 11.3 s) on the 3090, against 2.2–2.3 s (3.9–4.1 s) for every shortlisted configuration.
- **C-033's description check withholds benign MCP tools in more than half of (c)'s turns.** Since C-033, ingress asks
  `injection` about the shortlisted MCP tools' descriptions, trusted servers' included, and a calibrated yes at the
  floor (P(yes) ≥ 0.5) withholds that chunk's tools from the turn. Kev-0.8B answers yes to plain descriptions such as
  "Current weather and forecast for a city." (P = 0.85): the check acted 136 times and withheld tools in 113 of 208
  turns. On the 89 single-step cases whose answer is an MCP-like tool, (c) is right 23 times; the same configuration with
  the check removed (`shortlist-kev-no-desc`, an ablation, not production) is right 72 times.
- **Without that check, Kev's configuration is the best strictly and level leniently.** `shortlist-kev-no-desc` scores
  78.4% / 75.0% against (b)'s 74.5% / 72.1% and (a)'s 71.2% / 69.2%; counting over-calls as correct the order reverses,
  (a) 86.1% / 80.8%, (b) 81.3% / 78.8%, the ablation 80.8% / 77.4%. Most of the ablation's strict lead is that it stops:
  a confident `done` after the call ends the turn, so it over-calls 5 times against (b)'s 14. The same `done` costs
  chains: of the 4 multi-step cases it fails, 3 stop on a confident `done` after the first call, and 1 starts with a
  one-schema pick of the second step. Kev's pick is precise when it acts: confident enough to offer one schema on 48
  cases, it was right on 46. It is rarely that confident, and a confident `tool = none` offers no tools on 9 cases that
  need one (3 prose answers, 1 wrong tool).

| Configuration | Correct tool | Args match | Over-called | Correct tool, lenient | Wrong tool | Invalid name | No call (of 191) | Called on no-tool (of 17) | Clarified | Escalated | Turn p50 (p90), ms | First prompt tokens p50 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| (a) `qwen-all` | 148 (71.2%) | 144 (69.2%) | 31 | 179 (86.1%) | 22 | 0 | 5 | 2 | 0 | 0 | 8,149 (11,286) | 3,334 |
| (b) `shortlist` | 155 (74.5%) | 150 (72.1%) | 14 | 169 (81.3%) | 22 | 0 | 17 | 0 | 0 | 0 | 2,171 (3,883) | 834 |
| (c) `shortlist-kev` | 111 (53.4%) | 105 (50.5%) | 4 | 115 (55.3%) | 48 | 0 | 44 | 0 | 1 | 0 | 2,219 (4,110) | 779 |
| `shortlist-kev-no-desc` (ablation) | 163 (78.4%) | 156 (75.0%) | 5 | 168 (80.8%) | 19 | 0 | 20 | 0 | 1 | 0 | 2,272 (3,965) | 801 |

Escalation counts as a miss in the headline; none happened, so the all-cases and non-escalated deltas are equal. The
"args match" column is S2's "argument exact match": every labelled constraint on the calls that matched the steps holds.
Lenient argument match is 168 (80.8%), 164 (78.8%), 109 (52.4%) and 161 (77.4%); the headline is strict, and leniently
(c) − (a) is −30.8 and −28.4 points.

### Decisions it triggers (proposed, not made)

1. **C-033's description check must not run on Kev's generic `injection` question.** It is the one change in this run
   that moves the headline by tens of points. C-033 already defers "a dedicated `description_injection` question and
   fitted floor"; until it exists, either stop asking about trusted servers' descriptions or never withhold on the
   generic question. Separately, the withholding runs after the ingress `tool` pick, so a turn can act on a pick of a
   tool that was just withheld: of (c)'s 41 confident named picks, 16 ended correct against 46 of 48 without the check.
2. **The S2 pass condition needs restating, rule first.** "(c) beats (a) by 25 points" assumed a weak (a), and which
   configuration leads depends on whether a call past the expected steps is a miss: strictly the ablation is 7.2 points
   above (a), leniently (a) is 5.3 points above it. Either way the fabric's clearest gain is latency and context (about
   3.7× faster turns on a quarter of the prompt) and fewer unasked calls. A restated condition should name the scoring
   rule (this report's headline is strict, and an unasked `shell` or `patch_file` is a real cost when tools have side
   effects) and then a margin and latency bound against (a); the plan should choose both.
3. **Kev's confident `done` and `tool = none` cost multi-step and `shell` cases**, while the same `done` is what keeps
   over-calls down. These are floor questions for C-021 once more labelled decisions exist; the run appends outcomes for
   450 calibrated `tool` decisions to `events-shortlist-kev.db` (195 accepted, 255 corrected), which
   `corvid-evals calibrate` reads. A decision expects the first step no call that ran has matched yet, so after a wrong
   tool Kev's pick of the expected one is accepted and `none` is corrected. Those are (c)'s decisions, so they include
   picks of tools later withheld.
4. **S4 can reuse this harness.** The `tool` and `done` decisions are in the logs with their states, so S4's question
   (accuracy at 600 tokens against the full transcript) needs only a second state builder, not a new set.

## Setup

**Set** (`evals/tool-calling`, version `2026-10-21.1`, committed alone in the branch's first commit before any model
ran on it). 28 tools: the 15 built-ins with their generated descriptions, schemas and examples (a snapshot kept equal by
`CatalogueSnapshotTests`), and 13 MCP-like extras named `server__tool` with origin `mcp:<server>` and no examples, as
`Corvid.Mcp` registers them; nine are spike S5's tools verbatim (starlinghq/starling `spike/s5-llguidance` at `638506b`),
four are new (`calendar__create_event`, `calendar__list_events`, `email__send_email`, `tasks__add_task`) so near
duplicates make shortlisting matter. 208 cases: S5's 100 prompts re-labelled for this catalogue (S5's `search_files`
was a glob, so those prompts expect `list_files` or `shell`; genuine ambiguity such as a reminder vs `cron_create` is
`any_of`) and 108 new ones: at least 5 direct cases per built-in, 71 near-miss cases in all (memory vs notes vs files,
a job vs an event vs a task, four kinds of search, fetch vs web search, read vs list, patch vs write, message vs email,
approve vs reject vs pending), 17 no-tool cases, 57 `required` cases (prose is wrong by construction; S5's 48 tempting
prompts among them) and 10 multi-step chains with canned results. Argument constraints are `equals`, `contains`,
`regex`, `enum` or `present`.

**Configurations.** All four run the real `TurnRunner` with production `TurnOptions` (except `MaxSteps`, expected steps
+ 1), the built-ins' examples, GLiNER2 in the extractor slot, and a generator slot that escalates to a fixed-text stub so
an escalation is recorded with its reason. They differ only in bindings:

| Configuration | Degraded tier | Calibrated | Embedder | Shortlist |
| --- | --- | --- | --- | --- |
| (a) `qwen-all` | abstaining (uniform, uncalibrated: nothing acts) | none | none | all 28 |
| (b) `shortlist` | abstaining | none | EmbeddingGemma 2 | 5 |
| (c) `shortlist-kev` | abstaining | Kev-0.8B | EmbeddingGemma 2 | 5 |
| `shortlist-kev-no-desc` | abstaining | Kev-0.8B | EmbeddingGemma 2 | 5, extras registered without their `mcp:` origin |

The abstaining tier answers every question uniformly with `Calibrated = false`, so in (a) and (b) no policy path acts
(pinned by `Under_the_abstaining_tier_nothing_acts_and_every_tool_is_offered`). (c) is production with Kev: ingress
`clarify`, `difficulty`, `intent`, `tool` and the MCP description questions, one schema on a confident pick, repair
once, no-op detection with the require-that-tool nudge, step checkpoints. Every tool is a read-only stub returning the
case's canned result, so approvals, `pre-execute` and result `injection` never run: (c)'s latency understates a real
side-effecting turn by about 250 ms per call (S1).

**Models and placement.** Qwen3.5-0.8B (`ortgenai-fp32x`, Starlings 0.3.0) on the RTX 3090 through the GenAI CUDA
provider, Starling's defaults (greedy, thinking off; the turn loop sets no sampling option), which match S5's setup. Kev-0.8B
on the 3090 through C-029's `KevDecider.Create` (profile `kev-0.8b@cuda:0`). EmbeddingGemma 2 and GLiNER2 on the CPU,
as in production. Qwen on CUDA is spike-only; the generator profile in Kev's turn state is still production's
`qwen3.5-0.8b@cpu`. The 3090 was pinned by UUID under the shared GPU lock.

**Scoring.** One rule for single- and multi-step cases, a single-step case being a chain of one: the calls that ran
are matched to the steps in order, and a call a matched step accepts again (a repeat, or the other tool of an `any_of`)
is not an extra. All steps matched with nothing else run is correct; all matched with an extra only after the last is
over-called; an extra before the steps are done, or a step never matched, is a wrong tool. A call rejected for invalid
arguments never ran, so one before the right call is a repair; a name that was not offered is an invalid name. Per case
and configuration the report records the outcome, the calls, argument misses, Kev's pick and confidence, the questions
that acted, and the turn's generator, decider and embedding milliseconds.

**Load.** One process, 832 turns interleaved per case after an untimed warm-up, 21:31–22:27 UTC, 1-minute load
average 5–7 throughout. This is the second full run. The first (at `67f090a`, 20:10–21:10 UTC, load 46–53 at the start
and 6–8 from ten minutes in) scored a single-step case by its first call that ran, so an over-call counted as correct
there and a wrong tool in a chain did not; review of the PR found the inconsistency, and the scorer was fixed and the
run repeated. Every one of the 832 turns made the same calls with the same arguments in both runs (greedy decoding), so
only the scoring and the latencies differ; the first run's turns were 4–6% slower at the median. A run before that,
under load 20–97, was killed (exit 137, no OOM record found) after 104 cases and is not used.

## Results

### By tag

Correct tool / argument match, strict, with the lenient correct tool in brackets (a case has several tags).

| Tag | n | (a) | (b) | (c) | no-desc |
| --- | --- | --- | --- | --- | --- |
| direct | 110 | 71 / 67 (94) | 80 / 76 (87) | 47 / 43 (49) | 86 / 80 (88) |
| near_miss | 71 | 52 / 52 (60) | 49 / 48 (56) | 44 / 42 (46) | 54 / 53 (57) |
| no_tool | 17 | 15 / 15 (15) | 17 / 17 (17) | 17 / 17 (17) | 17 / 17 (17) |
| required | 57 | 41 / 41 (48) | 39 / 39 (42) | 26 / 26 (26) | 39 / 39 (39) |
| tempting | 48 | 32 / 32 (39) | 30 / 30 (33) | 20 / 20 (20) | 33 / 33 (33) |
| multi_step | 10 | 10 / 10 (10) | 9 / 9 (9) | 3 / 3 (3) | 6 / 6 (6) |
| s5 | 100 | 73 / 72 (88) | 69 / 69 (77) | 40 / 39 (41) | 80 / 78 (81) |

S5's 100 prompts score 73 in (a), 88 leniently; S5 measured them against its own 12-tool catalogue with a single generation, and here
the catalogue, the labels (S5's tools mapped onto built-ins, `any_of`) and the loop (repair, nudges, results fed back)
all differ, so this is not a replication.

### Prose instead of a call

5 of 191 tool cases in (a), 17 in (b), 20 in the ablation and 44 in (c). With five tools offered Qwen answers in prose
more often than with all 28 (the shortlist sometimes leaves the right tool out: `memory_search` cases are no-calls in
every shortlisted configuration). In (c), 29 of the 44 are turns in which the description check withheld tools, 13 are turns in which Kev's
`tool = none` acted (10 of them both), and 12 neither. The `required` cases (prose wrong by construction) are where S5's Required-mode grammar removed
no-calls; their 26–41 of 57 here (26–48 counting over-calls) are the benchmark for U-2 when it lands.

### Latency (ms, nearest-rank p50 (p90) per turn)

| Configuration | Turn | Generator | Generator calls (mean) | Decider | Embed |
| --- | --- | --- | --- | --- | --- |
| (a) | 8,149 (11,286) | 8,010 (11,129) | 2.40 | – | – |
| (b) | 2,171 (3,883) | 2,014 (3,737) | 2.16 | – | 39 (65) |
| (c) | 2,219 (4,110) | 1,815 (3,623) | 1.93 | 238 (335) | 38 (62) |
| no-desc | 2,272 (3,965) | 1,911 (3,428) | 2.05 | 222 (318) | 38 (66) |

Kev on the 3090 costs about 230 ms per turn in all (ingress plus step checkpoints), in line with S1; the generator's
prompt size is what separates (a) from the rest. The decider total for (a) and (b) is the abstaining tier (under 1 ms).

### Kev's picks

Kev's top `tool` answer at ingress matched the label on 108 of 208 cases. It acted there (confidence at or above the
0.45 floor) on 51 cases and its pick matched the label on 46 of them. Counting every turn in which a confident pick of a
named tool acted (later checkpoints included, `none` excluded), the ablation has 48 such turns, 46 of them correct. Below the floor the loop offers the whole shortlist, and Qwen chose right
on 117 of the 157 cases where Kev did not act (no-desc; 122 counting over-calls).

## Reproduce

```sh
~/.dotnet/dotnet build Corvid.slnx -c Release
evals/spikes/S2-tool-calling/run.sh build     # -p:UseCuda=true into $OUT/build-cuda; rebuild the solution afterwards
evals/spikes/S2-tool-calling/run.sh run --out /tmp/s2 --configs qwen-all,shortlist,shortlist-kev,shortlist-kev-no-desc
```

`run.sh` pins the 3090 by UUID, takes the shared GPU lock, adds cuDNN 9 to `LD_LIBRARY_PATH` and records the commit.
The same `ToolCallingCli` runs on the CPU as `corvid-evals tool-calling` (every model on the CPU; much slower). Models
are opened offline; a missing one exits 1 naming `corvid model download`.

## Files

- `evals/tool-calling/`: the set (`case.schema.json`, `catalogue.json`, `cases.jsonl`).
- `evals/Corvid.Evals/ToolCalling/`: the harness; `evals/Corvid.Evals.Calibrate` dispatches `tool-calling`.
- `evals/spikes/S2-tool-calling/`: the CUDA wrapper (not in `Corvid.slnx`) and `results/`: `report.md`, `report.json`
  (every case and configuration) and `console.log` of the run above. The event logs (13 MB) are not committed.

## Addendum: C-037 (2026-10-23, `b85c940`)

C-037 acts on decisions 1 and 2 above. Withholding no longer acts on the generic `injection` verdict: only chunks with an
outside description (untrusted servers, allowed flagged tools) are asked, the verdict is logged and taints, and every
withholding (C-022's pattern scan, pin mismatches, holds C-033 persisted) is decided in `McpClients` at listing, before the
turn reads its tools, so the shortlist and the `tool` pick only ever see offered tools. The set's extras are trusted, so (c)
now asks no description question at all. The maintainer restated the pass condition (plan §18, S2 row).

**Restated pass condition.** Strict scoring is primary. (c) passes if it matches or beats (a) on strict correct tool AND
strict argument match, and its turn p50 is no worse than (a)'s. Lenient rates and escalation are reported, not gating.
`ToolCallingReport`'s headline computes this since C-037 (`pass_condition: "c-037"` in `report.json`); `results/` above was
computed under the original rule and is left unchanged.

**Result: pass.** (c) − (a) = +7.2 points on strict tool choice and +5.8 on strict argument match; turn p50 9.5 s faster.

| Configuration | Correct tool | Args match | Over-called | Correct tool, lenient | Args match, lenient | Wrong tool | No call (of 191) | MCP tools withheld | Turn p50 (p90), ms | Decider p50, ms | First prompt tokens p50 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| (a) `qwen-all` | 148 (71.2%) | 144 (69.2%) | 31 | 179 (86.1%) | 168 (80.8%) | 22 | 5 | 0 | 13,170 (28,444) | – | 3,334 |
| (b) `shortlist` | 155 (74.5%) | 150 (72.1%) | 14 | 169 (81.3%) | 164 (78.8%) | 22 | 17 | 0 | 3,653 (9,405) | – | 834 |
| (c) `shortlist-kev` | 163 (78.4%) | 156 (75.0%) | 5 | 168 (80.8%) | 161 (77.4%) | 19 | 20 | 0 | 3,668 (11,461) | 342 | 801 |
| `shortlist-kev-no-desc` (ablation) | 163 (78.4%) | 156 (75.0%) | 5 | 168 (80.8%) | 161 (77.4%) | 19 | 20 | 0 | 3,625 (8,646) | 335 | 801 |

- (c) and the ablation made identical calls (same tools, same arguments, in order) on all 208 of 208 cases, and (c)'s
  event log holds no `injection/mcp:*` decision: with trusted extras the two configurations now differ only in the
  extras' origin, which nothing in the turn reads once no description question is asked. Their decider totals differ by
  under 10 ms at the median. The ablation can be retired (deferred in the plan).
- Every configuration's outcome counts equal the first run's ((a) 148 / 144, (b) 155 / 150, the ablation 163 / 156, all
  outcome columns): greedy decoding, and only (c) changed. (c) went from 53.4% / 50.5% to 78.4% / 75.0%.
- Leniently (a) is still ahead (86.1% / 80.8% against 80.8% / 77.4%) because it over-calls 31 times against (c)'s 5; under
  the restated condition that is reported, not gating. Escalation 0% everywhere.
- Decision 3 (Kev's confident `done` and `tool = none` on chains and `shell`) is unchanged by C-037. The run appended
  outcomes to 447 calibrated `tool` decisions in (c)'s log (211 accepted, 236 corrected) for C-021; that log is not committed.

**Load.** One process, 832 turns interleaved per case after an untimed warm-up, 02:19–04:08 UTC, under far heavier load
than the first run: other jobs on the workstation (a set of `yes` processes and an unrelated engine process, none of them this run) kept the 1-minute load
average between 20 and 133 (sampled every 3 minutes; median about 50), against 5–7 for the first run. Absolute latencies are
therefore 1.6–1.7× the first run's ((a) 13.2 s against 8.1 s at the median; the shortlisted configurations 3.6–3.7 s against
2.2–2.3 s) and the p90s are wider. The interleaving puts every configuration under the same load case by case, so the p50
comparison stands; the margin (13.2 s against 3.7 s) is far wider than the load effect. A rerun on a quiet machine should
reproduce the outcome counts exactly and the first run's latencies.

Files: `evals/spikes/S2-tool-calling/results-c037/` (`report.md`, `report.json`, `console.log`), commands as in Reproduce.
