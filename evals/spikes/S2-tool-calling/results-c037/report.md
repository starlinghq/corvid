# S2 tool-calling report

Set `2026-10-21.1` (28 tools, catalogue `945c3558e38f`, cases `5b8a1d1f4a29`), floors `2026-10-21.1`, Corvid `b85c940`, 2026-10-23 02:19–04:08 UTC.

- embeddinggemma-2: embeddinggemma-2@cpu, cpu, embeddinggemma-2/onnx-fp32-cpu, google/embeddinggemma-2@914f7f89, Starlings 0.3.0
- generator profile: qwen3.5-0.8b@cpu
- gliner2-base-v1: gliner2-base-v1@cpu, cpu, gliner2-base-v1/onnx-fp32-cpu, fastino/gliner2-base-v1@f9634218, Starlings 0.3.0
- kev-0.8b: kev-0.8b@cuda:0 (KevDecider.Create, C-029); calibrated temperature, jaredpalmer/kev-0.8b@9a45d25e, Starlings 0.3.0
- qwen3.5-0.8b: cuda:0 (GenAI CUDA provider, spike-only); Qwen35ChatClientOptions defaults: greedy, thinking off, Qwen/Qwen3.5-0.8B@2fc06364, Starlings 0.3.0
- cases: 208 scored of 208 selected of 208
- configurations: qwen-all, shortlist, shortlist-kev, shortlist-kev-no-desc
- escalation: generator slot escalates to a stub that answers with fixed text
- extractor: gliner2-base-v1@cpu
- kev outcomes appended: 211 accepted, 236 corrected (first tool decision of a turn: 108 accepted, 100 corrected; later: 103 accepted, 136 corrected)
- shortlist: qwen-all: 28 (every tool, no embedder); others: 5, built-in examples
- turn options: production defaults (MaxOutputTokens 1024, approval mode smart), MaxSteps = expected steps + 1
- warm-up: one untimed case per configuration: What's the weather like in Paris today?
- CUDA_VISIBLE_DEVICES: GPU-9e21a72e-3a6f-e082-3d74-4b8d361de70c
- load average after: 12.71 22.49 38.33
- load average before: 21.71 22.17 15.32
- os: Omarchy
- processors: 12
- runtime: .NET 10.0.12

## Headline

Plan §18, restated by the maintainer (C-037): strict scoring is primary; (c) passes if it matches or beats (a) on strict tool AND strict argument accuracy and its turn p50 is no worse than (a)'s. Lenient rates and escalation are reported, not gating. An escalation counts as a miss.

| Measure | (c) − (a), all cases | (c) − (a), non-escalated cases |
| --- | --- | --- |
| Tool choice | +7.2 points | +7.2 points |
| Argument match | +5.8 points | +5.8 points |

Turn p50, (c) − (a): -9502 ms. Escalation rate of (c): 0.0% (not gating). **Pass.**

## Outcomes

Correct tool and args match are strict: a case whose expected steps all ran and then another tool ran is over-called, a miss. The two lenient columns count it as correct.

| Configuration | n | Correct tool | Args match | Over-called | Correct tool, lenient | Args match, lenient | Wrong tool | Invalid name | Malformed | No call | Called on no-tool | Clarified | Escalated | Failed | Correct, non-escalated | Args valid (first offered call) | First response correct | Repaired | Nudged | MCP tools withheld |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| qwen-all | 208 | 148 (71.2%) | 144 (69.2%) | 31 | 179 (86.1%) | 168 (80.8%) | 22 | 0 | 0 | 5 of 191 | 2 | 0 | 0 | 0 | 148 (71.2%) | 188/188 | 144 | 0 | 0 | 0 |
| shortlist | 208 | 155 (74.5%) | 150 (72.1%) | 14 | 169 (81.3%) | 164 (78.8%) | 22 | 0 | 0 | 17 of 191 | 0 | 0 | 0 | 0 | 155 (74.5%) | 174/174 | 148 | 2 | 0 | 0 |
| shortlist-kev | 208 | 163 (78.4%) | 156 (75.0%) | 5 | 168 (80.8%) | 161 (77.4%) | 19 | 0 | 0 | 20 of 191 | 0 | 1 | 0 | 0 | 163 (78.4%) | 170/170 | 155 | 1 | 11 | 0 |
| shortlist-kev-no-desc | 208 | 163 (78.4%) | 156 (75.0%) | 5 | 168 (80.8%) | 161 (77.4%) | 19 | 0 | 0 | 20 of 191 | 0 | 1 | 0 | 0 | 163 (78.4%) | 170/170 | 155 | 1 | 11 | 0 |

## By tag

Correct tool / argument match, per tag (a case has several tags).

| Tag | n | qwen-all | shortlist | shortlist-kev | shortlist-kev-no-desc |
| --- | --- | --- | --- | --- | --- |
| direct | 110 | 71 / 67 | 80 / 76 | 86 / 80 | 86 / 80 |
| near_miss | 71 | 52 / 52 | 49 / 48 | 54 / 53 | 54 / 53 |
| no_tool | 17 | 15 / 15 | 17 / 17 | 17 / 17 | 17 / 17 |
| required | 57 | 41 / 41 | 39 / 39 | 39 / 39 | 39 / 39 |
| tempting | 48 | 32 / 32 | 30 / 30 | 33 / 33 | 33 / 33 |
| multi_step | 10 | 10 / 10 | 9 / 9 | 6 / 6 | 6 / 6 |
| s5 | 100 | 73 / 72 | 69 / 69 | 80 / 78 | 80 / 78 |

## Latency

Milliseconds per case, nearest-rank p50 (p90). Generator and decider are the turn's totals; prompt tokens are the first generator call's input.

| Configuration | Turn | Generator | Generator calls (mean) | Decider | Embed | Prompt tokens |
| --- | --- | --- | --- | --- | --- | --- |
| qwen-all | 13170 (28444) | 12819 (27644) | 2.40 | 0 (0) | – | 3334 (3342) |
| shortlist | 3653 (9405) | 3315 (8519) | 2.16 | 0 (0) | 100 (318) | 834 (1042) |
| shortlist-kev | 3668 (11461) | 2905 (9528) | 2.05 | 342 (814) | 98 (311) | 801 (1027) |
| shortlist-kev-no-desc | 3625 (8646) | 2864 (7067) | 2.05 | 335 (810) | 95 (298) | 801 (1027) |

## Disagreements

Every case some configuration did not get right, with what each called (`!` rejected, `?` invalid name) and the argument misses.

| Case | Expected | qwen-all | shortlist | shortlist-kev | shortlist-kev-no-desc |
| --- | --- | --- | --- | --- | --- |
| s5-001 | any_of(reminders__create_reminder, cron_create) | correct: reminders__create_reminder | over_called: reminders__create_reminder, reminders__list_reminders | correct: reminders__create_reminder | correct: reminders__create_reminder |
| s5-004 | any_of(reminders__create_reminder, cron_create) | wrong_tool: messages__send_message | no_call | no_call | no_call |
| s5-009 | any_of(reminders__list_reminders, cron_list) | correct: reminders__list_reminders, reminders__list_reminders | over_called: reminders__list_reminders, memory_search | correct: reminders__list_reminders | correct: reminders__list_reminders |
| s5-019 | any_of(reminders__delete_reminder, cron_remove) | wrong_tool: reminders__list_reminders, reminders__delete_reminder | correct: reminders__delete_reminder | correct: reminders__delete_reminder | correct: reminders__delete_reminder |
| s5-022 | any_of(notes__create_note, memory_add) | wrong_tool: shell, shell | no_call | no_call | no_call |
| s5-023 | notes__create_note | correct: notes__create_note | correct: notes__create_note | correct, args miss: notes__create_note; notes__create_note.title: equals "Book ideas" (got "Book ideas: Mars Detective Story") | correct, args miss: notes__create_note; notes__create_note.title: equals "Book ideas" (got "Book ideas: Mars Detective Story") |
| s5-024 | notes__create_note | over_called: notes__create_note, memory_add | correct: notes__create_note | correct: notes__create_note | correct: notes__create_note |
| s5-025 | any_of(notes__create_note, memory_add) | wrong_tool: patch_file | no_call | no_call | no_call |
| s5-030 | any_of(notes__search_notes, memory_search) | correct: memory_search, memory_search | no_call | no_call | no_call |
| s5-031 | notes__search_notes | correct: notes__search_notes, notes__search_notes | over_called: notes__search_notes, read_file | correct: notes__search_notes | correct: notes__search_notes |
| s5-033 | notes__search_notes | over_called: notes__search_notes, memory_search | wrong_tool: memory_add | wrong_tool: memory_add | wrong_tool: memory_add |
| s5-034 | notes__search_notes | correct: notes__search_notes | wrong_tool: memory_search | wrong_tool: memory_search | wrong_tool: memory_search |
| s5-035 | any_of(notes__search_notes, memory_search) | correct: memory_search, memory_search | no_call | no_call | no_call |
| s5-036 | read_file | over_called: read_file, shell | correct: read_file | correct: read_file | correct: read_file |
| s5-038 | read_file | over_called: read_file, shell | correct: read_file | correct: read_file | correct: read_file |
| s5-039 | any_of(read_file, shell) | over_called: read_file, patch_file | over_called: read_file, write_file | correct: read_file | correct: read_file |
| s5-040 | read_file | over_called, args miss: read_file, shell; read_file.path: regex ^(\./)?package\.json$ (got ".") | correct: read_file, read_file | correct: read_file | correct: read_file |
| s5-041 | read_file | over_called: read_file, shell | correct: read_file | correct: read_file | correct: read_file |
| s5-042 | read_file | over_called, args miss: read_file, shell; read_file.path: regex ^(\./)?LICENSE$ (got "/workspace/LICENSE") | correct: read_file | correct: read_file | correct: read_file |
| s5-046 | write_file | over_called: write_file, shell | correct: write_file, write_file | correct: write_file | correct: write_file |
| s5-047 | write_file | wrong_tool: patch_file, patch_file | correct: write_file | correct: write_file | correct: write_file |
| s5-048 | write_file | wrong_tool: patch_file, patch_file | correct: write_file, write_file | correct: write_file | correct: write_file |
| s5-049 | write_file | wrong_tool: patch_file | wrong_tool: patch_file | wrong_tool: patch_file | wrong_tool: patch_file |
| s5-050 | write_file | over_called: write_file, read_file | correct: write_file | correct: write_file | correct: write_file |
| s5-051 | any_of(list_files, shell) | correct: shell, shell | over_called: list_files, search_files | correct: list_files | correct: list_files |
| s5-053 | any_of(list_files, shell) | correct: shell, shell | wrong_tool: search_files | wrong_tool: search_files | wrong_tool: search_files |
| s5-054 | any_of(list_files, shell) | correct: shell, shell | wrong_tool: search_files, list_files | wrong_tool: search_files | wrong_tool: search_files |
| s5-057 | any_of(list_files, shell) | correct: shell, shell | over_called: list_files, search_files | over_called: list_files, search_files | over_called: list_files, search_files |
| s5-059 | web__web_search | correct: web__web_search, web__web_search | over_called: web__web_search, memory_search | correct: web__web_search | correct: web__web_search |
| s5-060 | web__web_search | correct: web__web_search | no_call | no_call | no_call |
| s5-061 | web__web_search | correct, args miss: web__web_search, web__web_search; web__web_search.max_results: equals 3 (got 10) | wrong_tool: web_fetch, web_fetch | correct, args miss: web__web_search; web__web_search.max_results: equals 3 (got 10) | correct, args miss: web__web_search; web__web_search.max_results: equals 3 (got 10) |
| s5-062 | web__web_search | correct: web__web_search, web__web_search | wrong_tool: ?web_search, web_fetch | wrong_tool: ?web_search, web_fetch | wrong_tool: ?web_search, web_fetch |
| s5-063 | web__web_search | over_called: web__web_search, web_fetch | wrong_tool: web_fetch, web_fetch | correct: web__web_search | correct: web__web_search |
| s5-064 | web__web_search | correct: web__web_search, web__web_search | wrong_tool: web_fetch | correct: web__web_search | correct: web__web_search |
| s5-065 | web__web_search | over_called, args miss: web__web_search, web_fetch; web__web_search.max_results: equals 5 (got 10) | wrong_tool: web_fetch, web_fetch | wrong_tool: web_fetch, web_fetch | wrong_tool: web_fetch, web_fetch |
| s5-066 | web__web_search | over_called: web__web_search, web_fetch | wrong_tool: web_fetch, web_fetch | correct: web__web_search | correct: web__web_search |
| s5-067 | web__web_search | wrong_tool: web_fetch, web_fetch | wrong_tool: web_fetch | wrong_tool: web_fetch | wrong_tool: web_fetch |
| s5-068 | web__web_search | wrong_tool: web_fetch, web_fetch | wrong_tool: ?web_search, web_fetch | wrong_tool: ?web_search, web_fetch | wrong_tool: ?web_search, web_fetch |
| s5-070 | any_of(messages__send_message, email__send_email) | no_call | no_call | no_call | no_call |
| s5-071 | messages__send_message | over_called: messages__send_message, calendar__create_event | correct: messages__send_message | correct: messages__send_message | correct: messages__send_message |
| s5-074 | messages__send_message | wrong_tool: web__web_search, web__web_search | correct: messages__send_message | correct: messages__send_message | correct: messages__send_message |
| s5-075 | messages__send_message | over_called: messages__send_message, calendar__create_event | over_called: messages__send_message, reminders__create_reminder | correct: messages__send_message | correct: messages__send_message |
| s5-083 | weather__get_weather | correct: weather__get_weather | no_call | no_call | no_call |
| s5-095 | clock__set_timer | correct: clock__set_timer | correct: clock__set_timer | wrong_tool: reminders__create_reminder | wrong_tool: reminders__create_reminder |
| s5-096 | notes__create_note | wrong_tool: notes__search_notes, notes__search_notes | wrong_tool: web_fetch | wrong_tool: web_fetch | wrong_tool: web_fetch |
| s5-098 | web__web_search | correct: web__web_search, web__web_search | wrong_tool: ?web_search, web_fetch | correct: web__web_search | correct: web__web_search |
| d-003 | list_files | wrong_tool: shell, shell | correct, args miss: list_files, list_files; list_files.path: regex ^(\./)?tests/?$ (got ".") | correct, args miss: list_files, list_files; list_files.path: regex ^(\./)?tests/?$ (got ".") | correct, args miss: list_files, list_files; list_files.path: regex ^(\./)?tests/?$ (got ".") |
| d-004 | list_files | wrong_tool: patch_file, patch_file | wrong_tool: shell, shell | clarified | clarified |
| d-005 | search_files | over_called: search_files, shell | over_called: search_files, shell | over_called: search_files, shell | over_called: search_files, shell |
| d-006 | search_files | over_called, args miss: search_files, shell; search_files.regex: equals true (got nothing) | correct: search_files | correct: search_files | correct: search_files |
| d-007 | search_files | over_called, args miss: search_files, shell; search_files.path: regex ^(\./)?docs/?$ (got ".") | correct: search_files, search_files | correct: search_files, search_files | correct: search_files, search_files |
| d-008 | write_file | over_called: write_file, shell | correct: write_file, write_file | correct: write_file, write_file | correct: write_file, write_file |
| d-009 | patch_file | wrong_tool: read_file, patch_file | correct, args miss: patch_file; patch_file.new_text: contains "5050" (got "{"); patch_file.old_text: contains "5000" (got "{") | correct, args miss: patch_file; patch_file.new_text: contains "5050" (got "{"); patch_file.old_text: contains "5000" (got "{") | correct, args miss: patch_file; patch_file.new_text: contains "5050" (got "{"); patch_file.old_text: contains "5000" (got "{") |
| d-011 | patch_file | correct, args miss: patch_file; patch_file.old_text: contains "colour" (got "/* style.css */"); patch_file.replace_all: equals true (got nothing) | correct, args miss: patch_file; patch_file.old_text: contains "colour" (got "color"); patch_file.replace_all: equals true (got nothing) | correct, args miss: patch_file; patch_file.old_text: contains "colour" (got "color"); patch_file.replace_all: equals true (got nothing) | correct, args miss: patch_file; patch_file.old_text: contains "colour" (got "color"); patch_file.replace_all: equals true (got nothing) |
| d-012 | shell | correct: shell | correct: shell | no_call | no_call |
| d-013 | shell | correct: shell, shell | correct: shell, shell | no_call | no_call |
| d-014 | shell | correct, args miss: shell; shell.command: regex \bdu\b\|\bdf\b (got "ls -la") | wrong_tool: list_files, shell; shell.command: regex \bdu\b\|\bdf\b (got "ls -la") | wrong_tool: list_files, shell; shell.command: regex \bdu\b\|\bdf\b (got "ls -la") | wrong_tool: list_files, shell; shell.command: regex \bdu\b\|\bdf\b (got "ls -la") |
| d-015 | shell | wrong_tool: read_file, shell | wrong_tool: list_files, shell | wrong_tool: list_files, shell | wrong_tool: list_files, shell |
| d-016 | shell | correct: shell | correct: shell | no_call | no_call |
| d-019 | web_fetch | over_called: web_fetch, read_file | over_called: web_fetch, read_file | correct: web_fetch | correct: web_fetch |
| d-020 | memory_search | correct: memory_search | no_call | no_call | no_call |
| d-021 | memory_search | no_call | no_call | no_call | no_call |
| d-022 | memory_search | no_call | no_call | no_call | no_call |
| d-026 | memory_pending | over_called: memory_pending, memory_search | over_called: memory_pending, memory_search | over_called: memory_pending, memory_search | over_called: memory_pending, memory_search |
| d-027 | memory_pending | wrong_tool: memory_search, memory_search | wrong_tool: memory_search, memory_search | wrong_tool: memory_search, memory_search | wrong_tool: memory_search, memory_search |
| d-028 | memory_pending | over_called: memory_pending, memory_search | correct: memory_pending, memory_pending | correct: memory_pending, memory_pending | correct: memory_pending, memory_pending |
| d-030 | memory_approve | over_called: memory_approve, memory_search | over_called: memory_approve, memory_reject | correct: memory_approve | correct: memory_approve |
| d-031 | memory_approve | wrong_tool: memory_add, memory_approve | correct: memory_approve | correct: memory_approve | correct: memory_approve |
| d-036 | cron_create | over_called, args miss: cron_create, cron_list; cron_create.schedule: regex (?i)mon.*(5\s*pm\|17)\|17 \* \* 1 (got "every 2 hours") | correct, args miss: cron_create; cron_create.schedule: regex (?i)mon.*(5\s*pm\|17)\|17 \* \* 1 (got "every 2 hours") | correct, args miss: cron_create; cron_create.schedule: regex (?i)mon.*(5\s*pm\|17)\|17 \* \* 1 (got "every 2 hours") | correct, args miss: cron_create; cron_create.schedule: regex (?i)mon.*(5\s*pm\|17)\|17 \* \* 1 (got "every 2 hours") |
| d-037 | cron_create | no_call | no_call | no_call | no_call |
| d-038 | cron_create | correct, args miss: cron_create; cron_create.schedule: regex (?i)(daily\|every day).*(midnight\|00:00\|12\s*am)\|0 0 \* \* \*\|@daily\|@midnight (got "daily at 9am") | correct: cron_create | correct: cron_create | correct: cron_create |
| nm-001 | memory_add | over_called: memory_add, memory_reject | correct: memory_add | correct: memory_add | correct: memory_add |
| nm-002 | memory_add | over_called: memory_add, memory_reject | correct: memory_add | correct: memory_add | correct: memory_add |
| nm-003 | memory_add | wrong_tool: calendar__list_events, calendar__list_events | no_call | no_call | no_call |
| nm-006 | write_file | over_called: write_file, read_file | correct: write_file | correct: write_file | correct: write_file |
| nm-007 | cron_create | wrong_tool: notes__search_notes, notes__search_notes | no_call | no_call | no_call |
| nm-010 | calendar__create_event | over_called: calendar__create_event, calendar__list_events | correct: calendar__create_event | correct: calendar__create_event | correct: calendar__create_event |
| nm-011 | cron_create | wrong_tool: shell, shell | wrong_tool: shell, memory_pending | wrong_tool: shell, memory_pending | wrong_tool: shell, memory_pending |
| nm-013 | search_files | over_called: search_files, shell | correct: search_files, search_files | correct: search_files, search_files | correct: search_files, search_files |
| nm-015 | memory_search | correct: memory_search, memory_search | no_call | no_call | no_call |
| nm-017 | any_of(search_files, shell) | correct: search_files, shell | over_called: search_files, web_fetch | over_called: search_files, web_fetch | over_called: search_files, web_fetch |
| nm-018 | memory_search | no_call | no_call | no_call | no_call |
| nm-020 | web__web_search | correct: web__web_search, web__web_search | wrong_tool: web_fetch | correct: web__web_search | correct: web__web_search |
| nm-022 | list_files | over_called, args miss: list_files, shell; list_files.path: regex ^(\./)?docs/?$ (got ".") | correct, args miss: list_files, list_files; list_files.path: regex ^(\./)?docs/?$ (got ".") | correct, args miss: list_files, list_files; list_files.path: regex ^(\./)?docs/?$ (got ".") | correct, args miss: list_files, list_files; list_files.path: regex ^(\./)?docs/?$ (got ".") |
| nm-023 | read_file | over_called: read_file, shell | over_called: read_file, shell | over_called: read_file, shell | over_called: read_file, shell |
| nm-024 | list_files | wrong_tool: shell, shell | correct: list_files, list_files | correct: list_files, list_files | correct: list_files, list_files |
| nm-026 | write_file | wrong_tool: read_file, patch_file | correct: write_file | correct: write_file | correct: write_file |
| nm-029 | messages__send_message | correct: messages__send_message | no_call | no_call | no_call |
| nt-012 | none | called_on_no_tool: patch_file, patch_file | correct | correct | correct |
| nt-015 | none | called_on_no_tool: search_files | correct | correct | correct |
| ms-001 | read_file → notes__create_note | correct: read_file, notes__create_note | correct: read_file, notes__create_note | wrong_tool: notes__create_note | wrong_tool: notes__create_note |
| ms-003 | weather__get_weather → any_of(reminders__create_reminder, cron_create) | correct: weather__get_weather, reminders__create_reminder | wrong_tool: reminders__create_reminder | wrong_tool: reminders__create_reminder | wrong_tool: reminders__create_reminder |
| ms-006 | search_files → read_file | correct: search_files, read_file | correct: search_files, read_file | wrong_tool: search_files | wrong_tool: search_files |
| ms-007 | web_fetch → write_file | correct: web_fetch, write_file | correct: web_fetch, write_file | wrong_tool: web_fetch | wrong_tool: web_fetch |
