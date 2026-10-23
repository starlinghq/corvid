# Spike S4: does Kev hold `tool` and `done` accuracy with the 600-token TurnState?

C-003 and C-038, spike S4 of [the plan](../plan/corvid-agent.md#phase-0-prove-the-fabric-about-2-weeks), run 2026-10-23
at Corvid `446b62a` (the harness commit; the cases were committed there before any model ran on them).

**Question.** Kev answers from a TurnState of at most 600 tokens (§5.1: the user message, the surface, a few one-line
`recent` events, the goal, entities, the step and the last tool result; never the transcript). Does its answer quality on
`tool` and `done` hold against a state that carries the whole session?

**Pass condition.** `tool` and `done` accuracy within 3 points, read as: on the `history` cases, the 600 arm is no more
than 3 points below the transcript-sized arm on Kev's calibrated `tool` decisions AND on its `done` decisions.

## Result

**Fail on `done` by the plan's rule, pass on `tool`; at n = 41 the `done` gap is not distinguishable from noise.** On the
41 history cases the 600-token state is +6.2 points on `tool` (46.4% of 84 decisions against 40.2% of 87) and −6.6 points
on `done` (65.1% of 43 against 71.7% of 46). The rule fails on `done`. Neither gap is significant: on the one decision that
is paired per case, ingress `tool`, the arms disagree on 8 cases, 5 for the 600 state and 3 for the transcript
(56.1% [41–70%] against 51.2% [36–66%], Wilson 95%), and on each case's first `done` decision (38 pairs) they disagree on
5, 1 for 600 and 4 for the transcript (exact McNemar p ≈ 0.38). Every decision was Kev's (0 fallbacks), no turn compacted.

| History cases (41) | 600 (`shortlist-kev`) | Transcript (`shortlist-kev-transcript`) | 600 − transcript |
| --- | --- | --- | --- |
| `tool`, all calibrated decisions | 39 / 84 (46.4%) | 35 / 87 (40.2%) | +6.2 points |
| `done` | 28 / 43 (65.1%) | 33 / 46 (71.7%) | **−6.6 points** |
| `tool` at ingress (paired) | 23 / 41 (56.1%) | 21 / 41 (51.2%) | +4.9 points |
| `tool` acted (right) | 16 (9) | 19 (9) | |
| `done` acted (right) | 16 (13) | 13 (11) | |
| State size, chars / 4, p50 (max) | 125 (146) | 311 (456) | |
| `recent` items, p50 (max) | 8 (8) | 29 (47) | |
| Decider ms per call, ingress p50 (p90) | 222 (503) | 281 (567) | +59 |
| Decider ms per call, step p50 (p90) | 231 (465) | 362 (708) | +131 |
| Case outcome correct (context only) | 28 | 29 | |

Three findings matter more than the sign of the totals:

- **The 600-token budget is not what limits the state; `RecentEvents` = 8 is.** The 600 arm's states were 125 tokens at
  the median and 146 at most (chars / 4); the transcript arm, carrying every event one-liner of a 15–40 entry history,
  311 and 456. Both fit in 600. What the 600 arm loses is everything older than the last eight one-liners, which
  production drops before any budget applies (`TurnRunner.Recent` takes the last `RecentEvents`; the `recent` field's
  160-token budget never cut a line here).
- **What the narrow state loses is "this was already done".** The `done` gap is the already-handled cases (category 2): on
  `history`, `no_tool` the 600 arm's `done` is 4 of 10 right against 8 of 10. Asked "Is the note saved?" 20 events after
  `notes__create_note` ran, Kev with the full transcript sees `called notes__create_note` and answers done; with eight
  recent lines it does not (`hist-note-saved-done`, `hist-task-done`, `hist-allergy-done`, `hist-ctl-thanks`). Of the
  five cases where only one arm's first `done` is right, the transcript's four are these no-tool turns with nothing left
  to do; the 600 arm's one is a standing instruction (`hist-lists-in-notes`).
- **More history also misleads `tool`.** On the near already-handled cases the transcript arm re-picks the tool that just
  ran (`email__send_email` for "Did the email to Dana go out?", `notes__create_note`, `calendar__create_event`; none acted),
  and its no-tool ingress accuracy is 8 of 11 against 10 of 11. That is most of the 600 arm's `tool` lead. On the
  `history_far` cases, where the cue is outside the narrow window, ingress `tool` is 11 of 24 against 12 of 24: Kev rarely
  uses a far cue for the tool even when it can see it. Its most common wrong ingress answer in both arms is a below-floor `none`
  (as in S2), and the shortlist held the answer on 27 of 30 tool cases.

### Recommendations (proposed, not decided)

1. **Do not move to a transcript-sized state.** It costs 60–130 ms per Kev call on the 3090 at these sizes (more under
   load), buys nothing on `tool`, and the `done` gain comes from a few earlier calls, not from the messages.
2. **Widen `recent` inside the 600 tokens instead** (a D-n decision for the maintainer, with a re-run): raise
   `RecentEvents` from 8 to about 24–32 (the 600 arm used a quarter of its budget) and, if needed, the `recent` field's
   160 tokens, or keep the earlier turns' `called X` / `X returned` lines when older messages are dropped. Then repeat
   this run with that arm as a third configuration; the decisive slice is `history`, `no_tool`.
3. **Grow the set before trusting a 3-point rule.** One `done` decision is about 2.2 points here and one ingress `tool`
   2.4 points; a 3-point condition needs several hundred paired decisions. The paired decision probe (fixed trajectories,
   both questions asked on the expected steps under both states; deferred) would make `done` paired as well.
4. **Kev's `done` misfires on chains in both arms** (`history`, `multi_step`: 0 of 6 and 0 of 7 `done` right; a
   confident yes after the first of two steps ends the turn), as S2 saw. That is a floor or training question for C-021,
   independent of the state size.

## Setup

**Set** (`evals/tool-calling`, version `2026-10-23.1`, the catalogue's 30 tools unchanged). S2's 208 single-turn cases
plus 41 `history` cases, appended inline: each carries `history`, 15–40 entries of `{"user": …}`, `{"assistant": …}` or
`{"tool": …, "args": …, "result": …}`, and, unless the current message alone decides, `cue`, the index of the entry the
answer depends on. 24 are `history_far` (the cue earlier than the last eight entries, so outside the 600 arm's window even
before tool entries render as two lines); 8 have a near cue; 9 are controls with no cue. By kind: 9 referents many turns
back ("And the same for Porto.", "Text that list to Sam."), 8 already handled (expect no call; `done` should be yes), 7
chains whose first step ran in an earlier turn (expect only the rest, so a re-read is wrong), 8 standing instructions early
in the session ("Always email Dana", "Any to-do I mention, add it to my task list"), and the 9 controls. Shapes: 25
single-step, 5 `multi_step`, 11 `no_tool`. Distractor entries are everyday exchanges that never call a tool the case is
about. Every message is at most 80 characters, `TurnRunner`'s one-liner cut, so the wide arm loses no message text, and
every history renders at most 47 one-liners, so the wide arm's 64 keep all of it plus the turn's own.

**Arms.** Both are S2's (c): the real `TurnRunner`, production `TurnOptions` but `MaxSteps` (expected steps + 1),
Qwen3.5-0.8B and Kev-0.8B on the 3090, EmbeddingGemma 2 and GLiNER2 on the CPU, the 5-tool shortlist. They differ only in
the TurnState:

| Arm | `StateBudget` | `RecentEvents` | Dual-shortlist ingress budget |
| --- | --- | --- | --- |
| `shortlist-kev` (600) | production: Total 600, HardStop 1,500, `recent` 160 | 8 | 300 (never applies: the harness binds no skills) |
| `shortlist-kev-transcript` | Total 2,000, HardStop 4,000, `recent` 1,800, other fields production | 64 | off |

"Transcript-sized" means every event one-liner in `recent` (`user: …`, `assistant: …`, `called X`, `X returned`), not
raw messages or tool-result text: §5.1 forbids those and both arms use the same rendering, so a cue is in a message or in
which tool was called. Raising the budget alone would change nothing (the input is already eight short lines); the arm
raises `RecentEvents` too. `KevDecider` takes states up to 8,192 tokens and keeps its 1,000-token row budget apart from
the state, so the wide state is answered by Kev; the report counts fallbacks to prove it (0).

**Seeding.** Before the turn, the case's history is appended to its fresh session as the loop would have logged it:
`UserMessage`, `AssistantMessage`, and per tool entry a `ToolCallIssued` (call id `h-<n>`, no round) and its
`ToolResult`. Qwen sees the whole seeded conversation in both arms, so case outcomes are context, not the metric.
Scoring reads only the events after the seed (`CaseRun.SeedHead`), so a seeded call is never one the turn made.
`clear_results` is asked at step checkpoints about seeded results older than the last two in both arms.

**Metric.** `KevDecisionScorer` labels every `tool` and `done` `Decision` of the turn: `tool` expects the first step not
yet matched by a call that ran, or none once every step has or on a no-tool case; Kev's answer is the argmax. `done`
expects yes when every step has run or on a no-tool case, no otherwise; Kev's answer is P(yes) ≥ 0.5, acted at the 0.8
floor. Only calibrated answers count. The ingress `tool` decision is one per case and paired across arms; later `tool`
decisions (the no-op check and step checkpoints) and every `done` follow each arm's own trajectory, because Kev's answers
steer the turn, so they are over different decision sets.

**Load.** One process, 82 turns interleaved per case after an untimed warm-up, 05:11–05:22 UTC. The workstation was shared:
1-minute load average 7.5 at the start and 85.6 at the end, so the latency figures are high and noisy (S1 measured
220–260 ms per ingress call on a quiet machine).

## Results

### By slice

Kev's calibrated decisions right / scored (ingress `tool`, all `tool`, `done`), 600 against transcript:

| Slice | n | Ingress `tool` | All `tool` | `done` |
| --- | --- | --- | --- | --- |
| history | 41 | 23 / 21 of 41 | 39 of 84 / 35 of 87 | 28 of 43 / 33 of 46 |
| history_far | 24 | 11 / 12 of 24 | 20 of 47 / 20 of 49 | 13 of 23 / 17 of 25 |
| cue near | 8 | 6 / 3 of 8 | 9 of 18 / 5 of 18 | 10 of 10 / 10 of 10 |
| controls (no cue) | 9 | 6 / 6 of 9 | 10 of 19 / 10 of 20 | 5 of 10 / 6 of 11 |
| multi_step | 5 | 3 / 3 of 5 | 3 of 11 / 3 of 12 | 0 of 6 / 0 of 7 |
| no_tool | 11 | 10 / 8 of 11 | 18 of 21 / 15 of 21 | 4 of 10 / 8 of 10 |

The controls, where the current message decides, are level, so the extra lines did not add noise there. The far slice
is where the transcript's `done` lead sits (13 of 23 against 17 of 25); the near slice is where its `tool` loses.

### Latency

Per Kev call, nearest-rank p50 (p90): ingress 222 (503) ms with the 600 state, 281 (567) with the transcript; step
checkpoints 231 (465) against 362 (708). Per turn: 4.7 s against 5.3 s at the median. The step figure includes the
`clear_results` questions about seeded results, which both arms ask. Under the run's load these are upper bounds; the
difference in prefill (a 2.5× larger state) is the comparable part.

### Kev's answers

At ingress Kev's top `tool` answer is `none` on 14 of the 30 tool cases with the 600 state and 15 with the transcript,
all but one below the 0.45 floor, so the loop offers the whole shortlist and Qwen decides; it acted on 16 `tool` decisions in all (9 right) with
the 600 state and 19 (9 right) with the transcript. A wrong `done` acted 3 times in the 600 arm and 2 in the transcript
arm, each ending a chain after its first step.

## Reproduce

```sh
~/.dotnet/dotnet build Corvid.slnx -c Release
evals/spikes/S4-state-budget/run.sh build                  # S2's CUDA build; rebuild the solution afterwards
evals/spikes/S4-state-budget/run.sh history --out /tmp/s4  # the gating run: the history cases, both arms
evals/spikes/S4-state-budget/run.sh all --out /tmp/s4-all  # the no-harm control: every case, both arms
```

`run.sh` only adds `--configs shortlist-kev,shortlist-kev-transcript` (and `--tags history`) to S2's wrapper, which pins
the 3090 by UUID, takes the shared GPU lock and records the commit. Both arms' event logs carry accepted/corrected outcomes
on Kev's `tool` and `done` decisions, so either is `corvid-evals calibrate --log` input.

## Files

- `evals/tool-calling/`: the set, version `2026-10-23.1` (`history` and `cue` in `case.schema.json`).
- `evals/Corvid.Evals/ToolCalling/`: the harness (`ToolCallingRunner.Seed`, `KevDecisionScorer`, `S4Comparison` in
  `ToolCallingReport`).
- `evals/spikes/S4-state-budget/`: `run.sh` and `results/` (`report.md`, `report.json` with every decision, `console.log`)
  of the gating run, and `results-all/` of the control run. The event logs are not committed.
- The set's new version changes `cases_sha256`; S2's committed reports name `2026-10-21.1` and `2026-10-22.1`. An S2
  re-run on this version includes the history cases unless it filters by tag.
