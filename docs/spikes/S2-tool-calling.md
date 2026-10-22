# Spike S2: does the fabric make Qwen3.5-0.8B a usable tool caller?

C-003 and C-034, spike S2 of [the plan](../plan/corvid-agent.md#phase-0-prove-the-fabric-about-2-weeks), run 2026-10-22
at Corvid `67f090a` (the branch's harness commit, on `main` at `9fa8081`).

**Question.** On a 200-case golden set of personal-assistant tasks, compare (a) Qwen alone with all tools, (b) the
embedding shortlist, (c) the shortlist plus Kev's `tool` pick, one schema and repair, on correct tool, argument match,
prose instead of a call, escalation rate and latency.

**Pass condition.** (c) beats (a) by ≥ 25 points on tool choice and argument exact match; escalation rate below 30%.

## Result

**Fail, in the opposite direction: (c) is 30.8 points below (a) on tool choice and 28.4 below on argument match.**
Escalation is 0% everywhere. Two separate things are behind it, and neither is the shortlist or Kev's pick itself:

- **Qwen alone is already a good tool caller on this set.** (a), with all 28 tools in the prompt, gets the right tool on
  179 of 208 cases (86.1%) and the arguments on 168 (80.8%), with 0 invalid names and 5 prose answers in 191 cases that
  need a tool. There are not 25 points left above it for the fabric to win. What (a) costs is latency and context: a
  3,334-token first prompt and 8.4 s per turn at the median (p90 12.2 s) on the 3090, against 2.3 s (4.0 s) for every
  shortlisted configuration.
- **C-033's description check withholds benign MCP tools in more than half of (c)'s turns.** Since C-033, ingress asks
  `injection` about the shortlisted MCP tools' descriptions, trusted servers' included, and a calibrated yes at the
  floor (P(yes) ≥ 0.5) withholds that chunk's tools from the turn. Kev-0.8B answers yes to plain descriptions such as
  "Current weather and forecast for a city." (P = 0.85): the check acted 136 times and withheld tools in 113 of 208
  turns. On the 89 single-step cases whose answer is an MCP-like tool, (c) is right 23 times; the same configuration with
  the check removed (`shortlist-kev-no-desc`, an ablation, not production) is right 72 times.
- **Without that check, Kev adds nothing net over the shortlist.** `shortlist-kev-no-desc` scores 80.8% / 77.4% against
  (b)'s 81.3% / 78.8% (and (a)'s 86.1% / 80.8%). Kev's pick is precise when it acts: confident enough to offer one
  schema on 48 cases, it was right on 46. It is rarely that confident, and its other confident answers cost about as
  much as the picks gain: of the 4 multi-step chains it fails, 3 stop on a confident `done` after the first call and 1
  starts with a one-schema pick of the second step; a confident `tool = none` offers no tools on 9 cases that need one
  (3 prose answers, 1 wrong tool).

| Configuration | Correct tool | Args match | Wrong tool | Invalid name | No call (of 191) | Called on no-tool (of 17) | Clarified | Escalated | Turn p50 (p90), ms | First prompt tokens p50 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| (a) `qwen-all` | 179 (86.1%) | 168 (80.8%) | 22 | 0 | 5 | 2 | 0 | 0 | 8,446 (12,171) | 3,334 |
| (b) `shortlist` | 169 (81.3%) | 164 (78.8%) | 22 | 0 | 17 | 0 | 0 | 0 | 2,295 (4,000) | 834 |
| (c) `shortlist-kev` | 115 (55.3%) | 109 (52.4%) | 48 | 0 | 44 | 0 | 1 | 0 | 2,320 (4,228) | 779 |
| `shortlist-kev-no-desc` (ablation) | 168 (80.8%) | 161 (77.4%) | 19 | 0 | 20 | 0 | 1 | 0 | 2,401 (4,089) | 801 |

Escalation counts as a miss in the headline; none happened, so the all-cases and non-escalated deltas are equal. The
"args match" column is S2's "argument exact match": every labelled constraint on the decisive call holds.

### Decisions it triggers (proposed, not made)

1. **C-033's description check must not run on Kev's generic `injection` question.** It is the one change in this run
   that moves the headline by tens of points. C-033 already defers "a dedicated `description_injection` question and
   fitted floor"; until it exists, either stop asking about trusted servers' descriptions or never withhold on the
   generic question. Separately, the withholding runs after the ingress `tool` pick, so a turn can act on a pick of a
   tool that was just withheld: of (c)'s 41 confident named picks, 16 ended correct against 46 of 48 without the check.
2. **The S2 pass condition needs restating.** "(c) beats (a) by 25 points" assumed a weak (a). On a 28-tool catalogue
   with a 0.8B model that already picks the right tool 86% of the time, the fabric's measurable value is latency and
   context (3.6× faster turns, a quarter of the prompt) at a cost of about 5 points of tool choice ((b) or the ablation
   against (a)). A condition such as "(c) within 3 points of (a) at a third of (a)'s latency, escalation under 30%"
   would test that; the plan should choose.
3. **Kev's confident `done` and `tool = none` cost multi-step and `shell` cases.** These are floor questions for C-021
   once more labelled decisions exist; the run appends outcomes for 450 calibrated `tool` decisions to
   `events-shortlist-kev.db` (243 accepted, 207 corrected), which `corvid-evals calibrate` reads. Those are (c)'s
   decisions, so they include picks of tools later withheld.
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

**Scoring.** The decisive call of a single-step case is the first call that ran; a call rejected for invalid arguments
before it is a repair; a name that was not offered is an invalid name. A multi-step case is correct when the calls that
ran match its steps in order and nothing else ran. Per case and configuration the report records the outcome, the calls,
argument misses, Kev's pick and confidence, the questions that acted, and the turn's generator, decider and embedding
milliseconds.

**Load.** One process, 832 turns interleaved per case after an untimed warm-up, 20:10–21:10 UTC. The 1-minute load
average was 46–53 at the start (other agents' builds) and 6–8 from about ten minutes in to the end. A first full run
under load 20–97 was killed (exit 137, no OOM record found) after 104 cases; its partial numbers point the same way
((a) 86.5%, (b) 76.9%, (c) 42.3%) and are not used.

## Results

### By tag

Correct tool / argument match (a case has several tags).

| Tag | n | (a) | (b) | (c) | no-desc |
| --- | --- | --- | --- | --- | --- |
| direct | 110 | 94 / 84 | 87 / 83 | 49 / 45 | 88 / 82 |
| near_miss | 71 | 60 / 59 | 56 / 55 | 46 / 44 | 57 / 56 |
| no_tool | 17 | 15 / 15 | 17 / 17 | 17 / 17 | 17 / 17 |
| required | 57 | 48 / 45 | 42 / 42 | 26 / 26 | 39 / 39 |
| tempting | 48 | 39 / 36 | 33 / 33 | 20 / 20 | 33 / 33 |
| multi_step | 10 | 10 / 10 | 9 / 9 | 3 / 3 | 6 / 6 |
| s5 | 100 | 88 / 84 | 77 / 77 | 41 / 40 | 81 / 79 |

S5's 100 prompts score 88 in (a); S5 measured them against its own 12-tool catalogue with a single generation, and here
the catalogue, the labels (S5's tools mapped onto built-ins, `any_of`) and the loop (repair, nudges, results fed back)
all differ, so this is not a replication.

### Prose instead of a call

5 of 191 tool cases in (a), 17 in (b), 20 in the ablation and 44 in (c). With five tools offered Qwen answers in prose
more often than with all 28 (the shortlist sometimes leaves the right tool out: `memory_search` cases are no-calls in
every shortlisted configuration). In (c), 29 of the 44 are turns in which the description check withheld tools, 13 are turns in which Kev's
`tool = none` acted (10 of them both), and 12 neither. The `required` cases (prose wrong by construction) are where S5's Required-mode grammar removed
no-calls; their 26–48 of 57 here are the benchmark for U-2 when it lands.

### Latency (ms, nearest-rank p50 (p90) per turn)

| Configuration | Turn | Generator | Generator calls (mean) | Decider | Embed |
| --- | --- | --- | --- | --- | --- |
| (a) | 8,446 (12,171) | 8,259 (11,957) | 2.40 | – | – |
| (b) | 2,295 (4,000) | 2,105 (3,778) | 2.16 | – | 47 (77) |
| (c) | 2,320 (4,228) | 1,887 (3,756) | 1.93 | 252 (341) | 46 (75) |
| no-desc | 2,401 (4,089) | 1,998 (3,618) | 2.05 | 234 (326) | 44 (73) |

Kev on the 3090 costs about 250 ms per turn in all (ingress plus step checkpoints), in line with S1; the generator's
prompt size is what separates (a) from the rest. The decider total for (a) and (b) is the abstaining tier (under 1 ms).

### Kev's picks

Kev's top `tool` answer at ingress matched the label on 108 of 208 cases. It acted there (confidence at or above the
0.45 floor) on 51 cases and its pick matched the label on 46 of them. Counting every turn in which a confident pick of a
named tool acted (later checkpoints included, `none` excluded), the ablation has 48 such turns, 46 of them correct. Below the floor the loop offers the whole shortlist, and Qwen chose right
on 122 of the 157 cases where Kev did not act (no-desc).

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
