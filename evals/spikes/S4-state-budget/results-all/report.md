# S2 tool-calling report

Set `2026-10-23.1` (30 tools, catalogue `6692c33be360`, cases `f064c27b3b7c`), floors `2026-10-21.1`, Corvid `446b62a`, 2026-10-23 05:22–06:21 UTC.

- embeddinggemma-2: embeddinggemma-2@cpu, cpu, embeddinggemma-2/onnx-fp32-cpu, google/embeddinggemma-2@914f7f89, Starlings 0.3.0
- generator profile: qwen3.5-0.8b@cpu
- gliner2-base-v1: gliner2-base-v1@cpu, cpu, gliner2-base-v1/onnx-fp32-cpu, fastino/gliner2-base-v1@f9634218, Starlings 0.3.0
- kev-0.8b: kev-0.8b@cuda:0 (KevDecider.Create, C-029); calibrated temperature, jaredpalmer/kev-0.8b@9a45d25e, Starlings 0.3.0
- qwen3.5-0.8b: cuda:0 (GenAI CUDA provider, spike-only); Qwen35ChatClientOptions defaults: greedy, thinking off, Qwen/Qwen3.5-0.8B@2fc06364, Starlings 0.3.0
- cases: 259 scored of 259 selected of 259
- configurations: shortlist-kev, shortlist-kev-transcript
- escalation: generator slot escalates to a stub that answers with fixed text
- extractor: gliner2-base-v1@cpu
- history: a history case's entries are seeded into its fresh session before the turn; only the turn's own events are scored
- kev outcomes appended: shortlist-kev: tool: 254 accepted, 294 corrected (first tool decision of a turn: 130 accepted, 129 corrected; later: 124 accepted, 165 corrected); done: 226 accepted, 63 corrected; shortlist-kev-transcript: tool: 250 accepted, 301 corrected (first tool decision of a turn: 128 accepted, 131 corrected; later: 122 accepted, 170 corrected); done: 231 accepted, 61 corrected
- shortlist: qwen-all: 30 (every tool, no embedder); others: 5, built-in examples
- state budget: production (600 tokens, 8 recent events, 300 at a tool-and-skill ingress); shortlist-kev-transcript: 2000 tokens (recent 1800, hard stop 4000), 64 recent events, no dual-shortlist budget
- turn options: production defaults (MaxOutputTokens 1024, approval mode smart), MaxSteps = expected steps + 1
- warm-up: one untimed case per configuration: What's the weather like in Paris today?
- CUDA_VISIBLE_DEVICES: GPU-9e21a72e-3a6f-e082-3d74-4b8d361de70c
- load average after: 17.30 20.30 34.85
- load average before: 99.35 58.92 33.88
- os: Omarchy
- processors: 12
- runtime: .NET 10.0.12

## S4: 600-token state against a transcript-sized state

Plan §18, read by C-038: on the `history` cases, `shortlist-kev` (TurnState 600 tokens, 8 recent events) is at most 3 points below `shortlist-kev-transcript` (2,000 tokens, every event one-liner) on Kev's calibrated `tool` decisions AND on its `done` decisions. Kev's answer is the argmax for `tool` and P(yes) ≥ 0.5 for `done`; acted means at or above the floor. Only ingress `tool` is paired (one per case in each arm); later `tool` and `done` decisions follow each arm's own trajectory.

| Measure (history cases) | 600 | Transcript | 600 − transcript |
| --- | --- | --- | --- |
| `tool`, all decisions | 39 (46.4%) | 35 (40.2%) | +6.2 points |
| `done` | 28 (65.1%) | 33 (71.7%) | −6.6 points |
| `tool` at ingress (paired) | 23 (56.1%) [41–70%] | 21 (51.2%) [36–66%] | +4.9 points |

Ingress `tool` on 41 paired cases: both right 18, only 600 right 5, only transcript right 3, both wrong 15 (Wilson 95% intervals in brackets). **Fail.**

| Slice | Arm | n | `tool` ingress | `tool` all | `done` | `tool` acted (right) | `done` acted (right) | Fallbacks | Shortlist hits | Ingress on hits | Compacted | Decider ingress ms p50 (p90) | Decider step ms p50 (p90) | State chars/4 p50 (max) | `recent` items p50 (max) |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| history | 600 | 41 | 23 (56.1%) | 39 (46.4%) | 28 (65.1%) | 16 (9) | 16 (13) | 0 | 27/30 | 13 (48.1%) | 0 | 193 (288) | 225 (359) | 125 (146) | 8 (8) |
| history | transcript | 41 | 21 (51.2%) | 35 (40.2%) | 33 (71.7%) | 19 (9) | 13 (11) | 0 | 27/30 | 13 (48.1%) | 0 | 273 (331) | 282 (486) | 311 (456) | 29 (47) |
| history_far | 600 | 24 | 11 (45.8%) | 20 (42.6%) | 13 (56.5%) | 6 (3) | 7 (6) | 0 | 16/18 | 6 (37.5%) | 0 | 179 (262) | 208 (320) | 124 (142) | 8 (8) |
| history_far | transcript | 24 | 12 (50.0%) | 20 (40.8%) | 17 (68.0%) | 5 (3) | 5 (4) | 0 | 16/18 | 7 (43.8%) | 0 | 273 (326) | 256 (483) | 336 (456) | 32 (47) |
| history, cue near | 600 | 8 | 6 (75.0%) | 9 (50.0%) | 10 (100.0%) | 1 (0) | 3 (3) | 0 | 5/5 | 3 (60.0%) | 0 | 248 (345) | 232 (288) | 130 (146) | 8 (8) |
| history, cue near | transcript | 8 | 3 (37.5%) | 5 (27.8%) | 10 (100.0%) | 2 (1) | 2 (2) | 0 | 5/5 | 2 (40.0%) | 0 | 279 (333) | 282 (383) | 241 (286) | 21 (25) |
| history, no cue (controls) | 600 | 9 | 6 (66.7%) | 10 (52.6%) | 5 (50.0%) | 9 (6) | 6 (4) | 0 | 6/7 | 4 (66.7%) | 0 | 171 (457) | 209 (404) | 125 (143) | 8 (8) |
| history, no cue (controls) | transcript | 9 | 6 (66.7%) | 10 (50.0%) | 6 (54.5%) | 12 (5) | 6 (5) | 0 | 6/7 | 4 (66.7%) | 0 | 254 (354) | 297 (599) | 311 (433) | 30 (46) |
| history, multi_step | 600 | 5 | 3 (60.0%) | 3 (27.3%) | 0 (0.0%) | 4 (3) | 3 (0) | 0 | 4/5 | 3 (75.0%) | 0 | 175 (457) | 191 (404) | 126 (143) | 8 (8) |
| history, multi_step | transcript | 5 | 3 (60.0%) | 3 (25.0%) | 0 (0.0%) | 8 (3) | 2 (0) | 0 | 4/5 | 3 (75.0%) | 0 | 283 (354) | 333 (599) | 351 (406) | 34 (40) |
| history, no_tool | 600 | 11 | 10 (90.9%) | 18 (85.7%) | 4 (40.0%) | 1 (1) | 2 (2) | 0 | 0/0 | – | 0 | 207 (288) | 124 (280) | 123 (146) | 8 (8) |
| history, no_tool | transcript | 11 | 8 (72.7%) | 15 (71.4%) | 8 (80.0%) | 1 (1) | 1 (1) | 0 | 0/0 | – | 0 | 254 (295) | 209 (343) | 260 (456) | 24 (47) |
| all cases | 600 | 259 | 130 (50.2%) | 254 (46.4%) | 226 (78.2%) | 134 (78) | 124 (109) | 0 | 225/231 | 103 (45.8%) | 0 | 210 (468) | 182 (386) | 75 (146) | 2 (8) |
| all cases | transcript | 259 | 128 (49.4%) | 250 (45.4%) | 231 (79.1%) | 137 (78) | 121 (107) | 0 | 225/231 | 103 (45.8%) | 0 | 254 (497) | 199 (408) | 75 (456) | 2 (47) |

## Outcomes

Correct tool and args match are strict: a case whose expected steps all ran and then another tool ran is over-called, a miss. The two lenient columns count it as correct.

| Configuration | n | Correct tool | Args match | Over-called | Correct tool, lenient | Args match, lenient | Wrong tool | Invalid name | Malformed | No call | Called on no-tool | Clarified | Escalated | Failed | Correct, non-escalated | Args valid (first offered call) | First response correct | Repaired | Nudged | MCP descriptions flagged |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| shortlist-kev | 259 | 201 (77.6%) | 188 (72.6%) | 4 | 205 (79.2%) | 192 (74.1%) | 26 | 0 | 0 | 24 of 231 | 2 | 1 | 1 (repair 1) | 0 | 201 (77.9%) | 207/207 | 187 | 1 | 12 | 0 |
| shortlist-kev-transcript | 259 | 202 (78.0%) | 189 (73.0%) | 4 | 206 (79.5%) | 193 (74.5%) | 26 | 0 | 0 | 23 of 231 | 2 | 1 | 1 (repair 1) | 0 | 202 (78.3%) | 208/208 | 188 | 1 | 12 | 0 |

## By tag

Correct tool / argument match, per tag (a case has several tags).

| Tag | n | shortlist-kev | shortlist-kev-transcript |
| --- | --- | --- | --- |
| direct | 141 | 111 / 101 | 112 / 102 |
| near_miss | 75 | 58 / 56 | 58 / 56 |
| no_tool | 28 | 26 / 26 | 26 / 26 |
| required | 59 | 40 / 40 | 40 / 40 |
| tempting | 48 | 32 / 32 | 32 / 32 |
| multi_step | 15 | 6 / 5 | 6 / 5 |
| s5 | 100 | 79 / 77 | 79 / 77 |
| history | 41 | 28 / 25 | 29 / 26 |
| history_far | 24 | 15 / 12 | 16 / 13 |

## Latency

Milliseconds per case, nearest-rank p50 (p90). Generator and decider are the turn's totals; prompt tokens are the first generator call's input.

| Configuration | Turn | Generator | Generator calls (mean) | Decider | Embed | Prompt tokens |
| --- | --- | --- | --- | --- | --- | --- |
| shortlist-kev | 4854 (10033) | 3826 (8134) | 2.00 | 415 (861) | 133 (364) | 817 (1155) |
| shortlist-kev-transcript | 4874 (10422) | 3682 (8611) | 2.01 | 490 (970) | 138 (398) | 818 (1155) |

## Disagreements

Every case some configuration did not get right, with what each called (`!` rejected, `?` invalid name) and the argument misses.

| Case | Expected | shortlist-kev | shortlist-kev-transcript |
| --- | --- | --- | --- |
| s5-004 | any_of(reminders__create_reminder, cron_create) | no_call | no_call |
| s5-022 | any_of(notes__create_note, memory_add) | no_call | no_call |
| s5-023 | notes__create_note | correct, args miss: notes__create_note; notes__create_note.title: equals "Book ideas" (got "Book ideas: Mars Detective Story") | correct, args miss: notes__create_note; notes__create_note.title: equals "Book ideas" (got "Book ideas: Mars Detective Story") |
| s5-025 | any_of(notes__create_note, memory_add) | no_call | no_call |
| s5-030 | any_of(notes__search_notes, memory_search) | no_call | no_call |
| s5-033 | notes__search_notes | wrong_tool: memory_add | wrong_tool: memory_add |
| s5-034 | notes__search_notes | wrong_tool: memory_search | wrong_tool: memory_search |
| s5-035 | any_of(notes__search_notes, memory_search) | no_call | no_call |
| s5-049 | write_file | wrong_tool: patch_file | wrong_tool: patch_file |
| s5-053 | any_of(list_files, shell) | wrong_tool: search_files | wrong_tool: search_files |
| s5-054 | any_of(list_files, shell) | wrong_tool: search_files | wrong_tool: search_files |
| s5-057 | any_of(list_files, shell) | over_called: list_files, search_files | over_called: list_files, search_files |
| s5-058 | any_of(list_files, shell) | wrong_tool: search_files | wrong_tool: search_files |
| s5-060 | web__web_search | no_call | no_call |
| s5-061 | web__web_search | correct, args miss: web__web_search; web__web_search.max_results: equals 3 (got 10) | correct, args miss: web__web_search; web__web_search.max_results: equals 3 (got 10) |
| s5-062 | web__web_search | wrong_tool: ?web_search, web_fetch | wrong_tool: ?web_search, web_fetch |
| s5-065 | web__web_search | wrong_tool: web_fetch, web_fetch | wrong_tool: web_fetch, web_fetch |
| s5-067 | web__web_search | wrong_tool: web_fetch | wrong_tool: web_fetch |
| s5-068 | web__web_search | wrong_tool: ?web_search, web_fetch | wrong_tool: ?web_search, web_fetch |
| s5-070 | any_of(messages__send_message, email__send_email) | no_call | no_call |
| s5-083 | weather__get_weather | no_call | no_call |
| s5-095 | clock__set_timer | wrong_tool: reminders__create_reminder | wrong_tool: reminders__create_reminder |
| s5-096 | notes__create_note | wrong_tool: web_fetch | wrong_tool: web_fetch |
| d-003 | list_files | correct, args miss: list_files, list_files; list_files.path: regex ^(\./)?tests/?$ (got ".") | correct, args miss: list_files, list_files; list_files.path: regex ^(\./)?tests/?$ (got ".") |
| d-004 | list_files | clarified | clarified |
| d-009 | patch_file | correct, args miss: patch_file; patch_file.new_text: contains "5050" (got "{"); patch_file.old_text: contains "5000" (got "{") | correct, args miss: patch_file; patch_file.new_text: contains "5050" (got "{"); patch_file.old_text: contains "5000" (got "{") |
| d-011 | patch_file | correct, args miss: patch_file; patch_file.old_text: contains "colour" (got "color"); patch_file.replace_all: equals true (got nothing) | correct, args miss: patch_file; patch_file.old_text: contains "colour" (got "color"); patch_file.replace_all: equals true (got nothing) |
| d-012 | shell | no_call | no_call |
| d-013 | shell | no_call | no_call |
| d-014 | shell | wrong_tool: list_files, shell; shell.command: regex \bdu\b\|\bdf\b (got "ls -la") | wrong_tool: list_files, shell; shell.command: regex \bdu\b\|\bdf\b (got "ls -la") |
| d-015 | shell | wrong_tool: list_files, shell | wrong_tool: list_files, shell |
| d-016 | shell | no_call | no_call |
| d-018 | web_fetch | no_call | no_call |
| d-020 | memory_search | no_call | no_call |
| d-021 | memory_search | no_call | no_call |
| d-022 | memory_search | no_call | no_call |
| d-026 | memory_pending | over_called: memory_pending, memory_search | over_called: memory_pending, memory_search |
| d-027 | memory_pending | wrong_tool: memory_search, memory_search | wrong_tool: memory_search, memory_search |
| d-036 | cron_create | correct, args miss: cron_create; cron_create.schedule: regex (?i)mon.*(5\s*pm\|17)\|17 \* \* 1 (got "every 2 hours") | correct, args miss: cron_create; cron_create.schedule: regex (?i)mon.*(5\s*pm\|17)\|17 \* \* 1 (got "every 2 hours") |
| d-037 | cron_create | no_call | no_call |
| d-050 | cron_pause | correct, args miss: cron_pause; cron_pause.job_id: equals "inbox-check" (got "job_inbox_check") | correct, args miss: cron_pause; cron_pause.job_id: equals "inbox-check" (got "job_inbox_check") |
| nm-003 | memory_add | no_call | no_call |
| nm-007 | cron_create | correct, args miss: cron_create; cron_create.schedule: regex (?i)weekday.*9\|0 9 \* \* 1-5 (got "every 2 hours") | correct, args miss: cron_create; cron_create.schedule: regex (?i)weekday.*9\|0 9 \* \* 1-5 (got "every 2 hours") |
| nm-011 | cron_create | wrong_tool: cron_list, cron_create | wrong_tool: cron_list, cron_create |
| nm-015 | memory_search | no_call | no_call |
| nm-017 | any_of(search_files, shell) | over_called: search_files, web_fetch | over_called: search_files, web_fetch |
| nm-018 | memory_search | no_call | no_call |
| nm-022 | list_files | correct, args miss: list_files, list_files; list_files.path: regex ^(\./)?docs/?$ (got ".") | correct, args miss: list_files, list_files; list_files.path: regex ^(\./)?docs/?$ (got ".") |
| nm-023 | read_file | over_called: read_file, shell | over_called: read_file, shell |
| nm-029 | messages__send_message | no_call | no_call |
| ms-001 | read_file → notes__create_note | wrong_tool: notes__create_note | wrong_tool: notes__create_note |
| ms-002 | notes__search_notes → messages__send_message | correct, args miss: notes__search_notes, messages__send_message; messages__send_message.text: contains "hunter2" (got "The wifi password is: [REDACTED:labelled-value]") | correct, args miss: notes__search_notes, messages__send_message; messages__send_message.text: contains "hunter2" (got "The wifi password is: [REDACTED:labelled-value]") |
| ms-003 | weather__get_weather → any_of(reminders__create_reminder, cron_create) | wrong_tool: reminders__create_reminder | wrong_tool: reminders__create_reminder |
| ms-006 | search_files → read_file | wrong_tool: search_files | wrong_tool: search_files |
| ms-007 | web_fetch → write_file | wrong_tool: web_fetch | wrong_tool: web_fetch |
| hist-same-weather-porto | weather__get_weather | no_call | no_call |
| hist-task-same-way | tasks__add_task | escalated (repair): ?tasks__add_task, ?tasks__add_task | escalated (repair): ?tasks__add_task, ?tasks__add_task |
| hist-agenda-to-priya | email__send_email | no_call | correct: email__send_email |
| hist-packing-to-sam | messages__send_message | correct, args miss: messages__send_message; messages__send_message.text: contains "charger" (got "I have a note titled Packing.") | correct, args miss: messages__send_message; messages__send_message.text: contains "charger" (got "I have a note titled Packing.") |
| hist-same-berlin-text-sam | weather__get_weather → messages__send_message | no_call | no_call |
| hist-lunch-event-done | none | called_on_no_tool: calendar__list_events | called_on_no_tool: calendar__list_events |
| hist-sam-text-done | none | called_on_no_tool: messages__send_message | called_on_no_tool: messages__send_message |
| hist-locker-to-ana | messages__send_message | correct, args miss: messages__send_message; messages__send_message.text: contains "212" (got "A tomato and mozzarella sandwich.") | correct, args miss: messages__send_message; messages__send_message.text: contains "212" (got "A tomato and mozzarella sandwich.") |
| hist-todo-note-and-text | notes__create_note → messages__send_message | wrong_tool: notes__create_note | wrong_tool: notes__create_note |
| hist-port-patch | patch_file | wrong_tool: web_fetch, web_fetch | wrong_tool: web_fetch, web_fetch |
| hist-dana-by-email | email__send_email | wrong_tool: messages__send_message | wrong_tool: messages__send_message |
| hist-drafts-folder | list_files | correct, args miss: list_files; list_files.path: regex ^(\./)?drafts/?$ (got ".") | correct, args miss: list_files, list_files; list_files.path: regex ^(\./)?drafts/?$ (got ".") |
| hist-todos-to-tasks | tasks__add_task | no_call | no_call |
| hist-weather-also-note | weather__get_weather → notes__create_note | wrong_tool: weather__get_weather | wrong_tool: weather__get_weather |
| hist-ctl-read-and-email | read_file → email__send_email | wrong_tool: write_file, email__send_email | wrong_tool: notes__search_notes, notes__search_notes, notes__search_notes |
| hist-ctl-oslo-note | weather__get_weather → notes__create_note | wrong_tool: weather__get_weather | wrong_tool: weather__get_weather |
