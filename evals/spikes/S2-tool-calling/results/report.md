# S2 tool-calling report

Set `2026-10-21.1` (28 tools, catalogue `945c3558e38f`, cases `5b8a1d1f4a29`), floors `2026-10-21.1`, Corvid `67f090a`, 2026-10-22 20:10–21:10 UTC.

- embeddinggemma-2: embeddinggemma-2@cpu, cpu, embeddinggemma-2/onnx-fp32-cpu, google/embeddinggemma-2@914f7f89, Starlings 0.3.0
- generator profile: qwen3.5-0.8b@cpu
- gliner2-base-v1: gliner2-base-v1@cpu, cpu, gliner2-base-v1/onnx-fp32-cpu, fastino/gliner2-base-v1@f9634218, Starlings 0.3.0
- kev-0.8b: kev-0.8b@cuda:0 (KevDecider.Create, C-029); calibrated temperature, jaredpalmer/kev-0.8b@9a45d25e, Starlings 0.3.0
- qwen3.5-0.8b: cuda:0 (GenAI CUDA provider, spike-only); Qwen35ChatClientOptions defaults: greedy, thinking off, Qwen/Qwen3.5-0.8B@2fc06364, Starlings 0.3.0
- cases: 208 scored of 208 selected of 208
- configurations: qwen-all, shortlist, shortlist-kev, shortlist-kev-no-desc
- escalation: generator slot escalates to a stub that answers with fixed text
- extractor: gliner2-base-v1@cpu
- kev outcomes appended: 243 accepted, 207 corrected (first tool decision of a turn: 108 accepted, 100 corrected; later: 135 accepted, 107 corrected)
- shortlist: qwen-all: 28 (every tool, no embedder); others: 5, built-in examples
- turn options: production defaults (MaxOutputTokens 1024, approval mode smart), MaxSteps = expected steps + 1
- warm-up: one untimed case per configuration: What's the weather like in Paris today?
- CUDA_VISIBLE_DEVICES: GPU-9e21a72e-3a6f-e082-3d74-4b8d361de70c
- load average after: 6.21 6.89 8.29
- load average before: 46.25 51.73 53.31
- os: Omarchy
- processors: 12
- runtime: .NET 10.0.12

## Headline

Plan §18: (c) beats (a) by ≥ 25 points on tool choice and argument match; (c)'s escalation rate below 30%. An escalation counts as a miss.

| Measure | (c) − (a), all cases | (c) − (a), non-escalated cases |
| --- | --- | --- |
| Tool choice | −30.8 points | −30.8 points |
| Argument match | −28.4 points | −28.4 points |

Escalation rate of (c): 0.0%. **Fail.**

## Outcomes

| Configuration | n | Correct tool | Args match | Wrong tool | Invalid name | Malformed | No call | Called on no-tool | Clarified | Escalated | Failed | Correct, non-escalated | Args valid (first offered call) | First response correct | Repaired | Nudged | MCP tools withheld |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| qwen-all | 208 | 179 (86.1%) | 168 (80.8%) | 22 | 0 | 0 | 5 of 191 | 2 | 0 | 0 | 0 | 179 (86.1%) | 188/188 | 168 | 0 | 0 | 0 |
| shortlist | 208 | 169 (81.3%) | 164 (78.8%) | 22 | 0 | 0 | 17 of 191 | 0 | 0 | 0 | 0 | 169 (81.3%) | 174/174 | 162 | 2 | 0 | 0 |
| shortlist-kev | 208 | 115 (55.3%) | 109 (52.4%) | 48 | 0 | 0 | 44 of 191 | 0 | 1 | 0 | 0 | 115 (55.3%) | 146/146 | 109 | 0 | 2 | 113 |
| shortlist-kev-no-desc | 208 | 168 (80.8%) | 161 (77.4%) | 19 | 0 | 0 | 20 of 191 | 0 | 1 | 0 | 0 | 168 (80.8%) | 170/170 | 160 | 1 | 11 | 0 |

## By tag

Correct tool / argument match, per tag (a case has several tags).

| Tag | n | qwen-all | shortlist | shortlist-kev | shortlist-kev-no-desc |
| --- | --- | --- | --- | --- | --- |
| direct | 110 | 94 / 84 | 87 / 83 | 49 / 45 | 88 / 82 |
| near_miss | 71 | 60 / 59 | 56 / 55 | 46 / 44 | 57 / 56 |
| no_tool | 17 | 15 / 15 | 17 / 17 | 17 / 17 | 17 / 17 |
| required | 57 | 48 / 45 | 42 / 42 | 26 / 26 | 39 / 39 |
| tempting | 48 | 39 / 36 | 33 / 33 | 20 / 20 | 33 / 33 |
| multi_step | 10 | 10 / 10 | 9 / 9 | 3 / 3 | 6 / 6 |
| s5 | 100 | 88 / 84 | 77 / 77 | 41 / 40 | 81 / 79 |

## Latency

Milliseconds per case, nearest-rank p50 (p90). Generator and decider are the turn's totals; prompt tokens are the first generator call's input.

| Configuration | Turn | Generator | Generator calls (mean) | Decider | Embed | Prompt tokens |
| --- | --- | --- | --- | --- | --- | --- |
| qwen-all | 8446 (12171) | 8259 (11957) | 2.40 | 0 (0) | – | 3334 (3342) |
| shortlist | 2295 (4000) | 2105 (3778) | 2.16 | 0 (0) | 47 (77) | 834 (1042) |
| shortlist-kev | 2320 (4228) | 1887 (3756) | 1.93 | 252 (341) | 46 (75) | 779 (950) |
| shortlist-kev-no-desc | 2401 (4089) | 1998 (3618) | 2.05 | 234 (326) | 44 (73) | 801 (1027) |

## Disagreements

Every case some configuration did not get right, with what each called (`!` rejected, `?` invalid name) and the argument misses.

| Case | Expected | qwen-all | shortlist | shortlist-kev | shortlist-kev-no-desc |
| --- | --- | --- | --- | --- | --- |
| s5-002 | any_of(reminders__create_reminder, cron_create) | correct: reminders__create_reminder | correct: reminders__create_reminder | correct, args miss: cron_create; cron_create.schedule: contains "2026-11-02", "18:00" (got "in 30m") | correct: reminders__create_reminder |
| s5-004 | any_of(reminders__create_reminder, cron_create) | wrong_tool: messages__send_message | no_call | no_call | no_call |
| s5-009 | any_of(reminders__list_reminders, cron_list) | correct: reminders__list_reminders, reminders__list_reminders | correct: reminders__list_reminders, memory_search | wrong_tool: memory_search, memory_search | correct: reminders__list_reminders |
| s5-015 | any_of(reminders__delete_reminder, cron_remove) | correct: reminders__delete_reminder | correct: reminders__delete_reminder | wrong_tool: memory_reject | correct: reminders__delete_reminder |
| s5-019 | any_of(reminders__delete_reminder, cron_remove) | wrong_tool: reminders__list_reminders, reminders__delete_reminder | correct: reminders__delete_reminder | correct: cron_remove | correct: reminders__delete_reminder |
| s5-021 | notes__create_note | correct: notes__create_note | correct: notes__create_note | wrong_tool: write_file | correct: notes__create_note |
| s5-022 | any_of(notes__create_note, memory_add) | wrong_tool: shell, shell | no_call | no_call | no_call |
| s5-023 | notes__create_note | correct: notes__create_note | correct: notes__create_note | wrong_tool: write_file | correct, args miss: notes__create_note; notes__create_note.title: equals "Book ideas" (got "Book ideas: Mars Detective Story") |
| s5-024 | notes__create_note | correct: notes__create_note, memory_add | correct: notes__create_note | no_call | correct: notes__create_note |
| s5-025 | any_of(notes__create_note, memory_add) | wrong_tool: patch_file | no_call | no_call | no_call |
| s5-027 | notes__create_note | correct: notes__create_note | correct: notes__create_note | wrong_tool: write_file | correct: notes__create_note |
| s5-028 | notes__create_note | correct: notes__create_note | correct: notes__create_note | wrong_tool: memory_add | correct: notes__create_note |
| s5-029 | notes__search_notes | correct: notes__search_notes | correct: notes__search_notes | wrong_tool: search_files | correct: notes__search_notes |
| s5-030 | any_of(notes__search_notes, memory_search) | correct: memory_search, memory_search | no_call | no_call | no_call |
| s5-031 | notes__search_notes | correct: notes__search_notes, notes__search_notes | correct: notes__search_notes, read_file | no_call | correct: notes__search_notes |
| s5-032 | notes__search_notes | correct: notes__search_notes, notes__search_notes | correct: notes__search_notes | wrong_tool: search_files, read_file | correct: notes__search_notes |
| s5-033 | notes__search_notes | correct: notes__search_notes, memory_search | wrong_tool: memory_add | no_call | wrong_tool: memory_add |
| s5-034 | notes__search_notes | correct: notes__search_notes | wrong_tool: memory_search | wrong_tool: search_files, memory_search | wrong_tool: memory_search |
| s5-035 | any_of(notes__search_notes, memory_search) | correct: memory_search, memory_search | no_call | no_call | no_call |
| s5-040 | read_file | correct, args miss: read_file, shell; read_file.path: regex ^(\./)?package\.json$ (got ".") | correct: read_file, read_file | correct: read_file | correct: read_file |
| s5-042 | read_file | correct, args miss: read_file, shell; read_file.path: regex ^(\./)?LICENSE$ (got "/workspace/LICENSE") | correct: read_file | correct: read_file | correct: read_file |
| s5-047 | write_file | wrong_tool: patch_file, patch_file | correct: write_file | correct: write_file | correct: write_file |
| s5-048 | write_file | wrong_tool: patch_file, patch_file | correct: write_file, write_file | wrong_tool: read_file, write_file | correct: write_file |
| s5-049 | write_file | wrong_tool: patch_file | wrong_tool: patch_file | wrong_tool: patch_file | wrong_tool: patch_file |
| s5-053 | any_of(list_files, shell) | correct: shell, shell | wrong_tool: search_files | wrong_tool: search_files | wrong_tool: search_files |
| s5-054 | any_of(list_files, shell) | correct: shell, shell | wrong_tool: search_files, list_files | wrong_tool: search_files | wrong_tool: search_files |
| s5-059 | web__web_search | correct: web__web_search, web__web_search | correct: web__web_search, memory_search | wrong_tool: memory_search | correct: web__web_search |
| s5-060 | web__web_search | correct: web__web_search | no_call | no_call | no_call |
| s5-061 | web__web_search | correct, args miss: web__web_search, web__web_search; web__web_search.max_results: equals 3 (got 10) | wrong_tool: web_fetch, web_fetch | wrong_tool: web_fetch | correct, args miss: web__web_search; web__web_search.max_results: equals 3 (got 10) |
| s5-062 | web__web_search | correct: web__web_search, web__web_search | wrong_tool: ?web_search, web_fetch | wrong_tool: web_fetch | wrong_tool: ?web_search, web_fetch |
| s5-063 | web__web_search | correct: web__web_search, web_fetch | wrong_tool: web_fetch, web_fetch | wrong_tool: web_fetch | correct: web__web_search |
| s5-064 | web__web_search | correct: web__web_search, web__web_search | wrong_tool: web_fetch | wrong_tool: web_fetch | correct: web__web_search |
| s5-065 | web__web_search | correct, args miss: web__web_search, web_fetch; web__web_search.max_results: equals 5 (got 10) | wrong_tool: web_fetch, web_fetch | wrong_tool: web_fetch, web_fetch | wrong_tool: web_fetch, web_fetch |
| s5-066 | web__web_search | correct: web__web_search, web_fetch | wrong_tool: web_fetch, web_fetch | wrong_tool: web_fetch | correct: web__web_search |
| s5-067 | web__web_search | wrong_tool: web_fetch, web_fetch | wrong_tool: web_fetch | wrong_tool: web_fetch | wrong_tool: web_fetch |
| s5-068 | web__web_search | wrong_tool: web_fetch, web_fetch | wrong_tool: ?web_search, web_fetch | no_call | wrong_tool: ?web_search, web_fetch |
| s5-069 | messages__send_message | correct: messages__send_message | correct: messages__send_message | no_call | correct: messages__send_message |
| s5-070 | any_of(messages__send_message, email__send_email) | no_call | no_call | no_call | no_call |
| s5-071 | messages__send_message | correct: messages__send_message, calendar__create_event | correct: messages__send_message | wrong_tool: email__send_email, reminders__create_reminder | correct: messages__send_message |
| s5-072 | messages__send_message | correct: messages__send_message | correct: messages__send_message | no_call | correct: messages__send_message |
| s5-073 | any_of(messages__send_message, email__send_email) | correct: email__send_email | correct: email__send_email | no_call | correct: email__send_email |
| s5-074 | messages__send_message | wrong_tool: web__web_search, web__web_search | correct: messages__send_message | no_call | correct: messages__send_message |
| s5-075 | messages__send_message | correct: messages__send_message, calendar__create_event | correct: messages__send_message, reminders__create_reminder | wrong_tool: reminders__create_reminder | correct: messages__send_message |
| s5-077 | messages__send_message | correct: messages__send_message, messages__send_message | correct: messages__send_message | wrong_tool: write_file, write_file | correct: messages__send_message |
| s5-078 | messages__send_message | correct: messages__send_message | correct: messages__send_message | no_call | correct: messages__send_message |
| s5-079 | weather__get_weather | correct: weather__get_weather | correct: weather__get_weather | no_call | correct: weather__get_weather |
| s5-080 | weather__get_weather | correct: weather__get_weather, weather__get_weather | correct: weather__get_weather | no_call | correct: weather__get_weather |
| s5-081 | weather__get_weather | correct: weather__get_weather | correct: weather__get_weather | no_call | correct: weather__get_weather |
| s5-082 | weather__get_weather | correct: weather__get_weather | correct: weather__get_weather | no_call | correct: weather__get_weather |
| s5-083 | weather__get_weather | correct: weather__get_weather | no_call | no_call | no_call |
| s5-084 | weather__get_weather | correct: weather__get_weather | correct: weather__get_weather | no_call | correct: weather__get_weather |
| s5-086 | weather__get_weather | correct: weather__get_weather | correct: weather__get_weather | no_call | correct: weather__get_weather |
| s5-087 | weather__get_weather | correct: weather__get_weather | correct: weather__get_weather | no_call | correct: weather__get_weather |
| s5-088 | clock__set_timer | correct: clock__set_timer | correct: clock__set_timer | wrong_tool: cron_create | correct: clock__set_timer |
| s5-089 | clock__set_timer | correct: clock__set_timer | correct: clock__set_timer | wrong_tool: cron_create | correct: clock__set_timer |
| s5-090 | clock__set_timer | correct: clock__set_timer | correct: clock__set_timer | wrong_tool: cron_create, cron_create | correct: clock__set_timer |
| s5-091 | clock__set_timer | correct: clock__set_timer | correct: clock__set_timer | wrong_tool: shell | correct: clock__set_timer |
| s5-093 | clock__set_timer | correct: clock__set_timer | correct: clock__set_timer | wrong_tool: cron_create | correct: clock__set_timer |
| s5-094 | clock__set_timer | correct: clock__set_timer | correct: clock__set_timer | wrong_tool: cron_create | correct: clock__set_timer |
| s5-095 | clock__set_timer | correct: clock__set_timer | correct: clock__set_timer | wrong_tool: cron_create | wrong_tool: reminders__create_reminder |
| s5-096 | notes__create_note | wrong_tool: notes__search_notes, notes__search_notes | wrong_tool: web_fetch | no_call | wrong_tool: web_fetch |
| s5-097 | web__web_search | correct: web__web_search, web__web_search | correct: ?web_search, web__web_search | wrong_tool: web_fetch | correct: ?web_search, web__web_search |
| s5-098 | web__web_search | correct: web__web_search, web__web_search | wrong_tool: ?web_search, web_fetch | wrong_tool: web_fetch | correct: web__web_search |
| s5-099 | notes__search_notes | correct: notes__search_notes | correct: notes__search_notes | no_call | correct: notes__search_notes |
| d-003 | list_files | wrong_tool: shell, shell | correct, args miss: list_files, list_files; list_files.path: regex ^(\./)?tests/?$ (got ".") | correct, args miss: list_files, list_files; list_files.path: regex ^(\./)?tests/?$ (got ".") | correct, args miss: list_files, list_files; list_files.path: regex ^(\./)?tests/?$ (got ".") |
| d-004 | list_files | wrong_tool: patch_file, patch_file | wrong_tool: shell, shell | clarified | clarified |
| d-006 | search_files | correct, args miss: search_files, shell; search_files.regex: equals true (got nothing) | correct: search_files | correct: search_files | correct: search_files |
| d-007 | search_files | correct, args miss: search_files, shell; search_files.path: regex ^(\./)?docs/?$ (got ".") | correct: search_files, search_files | correct: search_files, search_files | correct: search_files, search_files |
| d-009 | patch_file | wrong_tool: read_file, patch_file | correct, args miss: patch_file; patch_file.new_text: contains "5050" (got "{"); patch_file.old_text: contains "5000" (got "{") | correct, args miss: patch_file; patch_file.new_text: contains "5050" (got "{"); patch_file.old_text: contains "5000" (got "{") | correct, args miss: patch_file; patch_file.new_text: contains "5050" (got "{"); patch_file.old_text: contains "5000" (got "{") |
| d-011 | patch_file | correct, args miss: patch_file; patch_file.old_text: contains "colour" (got "/* style.css */"); patch_file.replace_all: equals true (got nothing) | correct, args miss: patch_file; patch_file.old_text: contains "colour" (got "color"); patch_file.replace_all: equals true (got nothing) | correct, args miss: patch_file; patch_file.old_text: contains "colour" (got "color"); patch_file.replace_all: equals true (got nothing) | correct, args miss: patch_file; patch_file.old_text: contains "colour" (got "color"); patch_file.replace_all: equals true (got nothing) |
| d-012 | shell | correct: shell | correct: shell | no_call | no_call |
| d-013 | shell | correct: shell, shell | correct: shell, shell | no_call | no_call |
| d-014 | shell | correct, args miss: shell; shell.command: regex \bdu\b\|\bdf\b (got "ls -la") | wrong_tool: list_files, shell | wrong_tool: list_files, shell | wrong_tool: list_files, shell |
| d-015 | shell | wrong_tool: read_file, shell | wrong_tool: list_files, shell | wrong_tool: list_files, shell | wrong_tool: list_files, shell |
| d-016 | shell | correct: shell | correct: shell | no_call | no_call |
| d-020 | memory_search | correct: memory_search | no_call | no_call | no_call |
| d-021 | memory_search | no_call | no_call | no_call | no_call |
| d-022 | memory_search | no_call | no_call | no_call | no_call |
| d-027 | memory_pending | wrong_tool: memory_search, memory_search | wrong_tool: memory_search, memory_search | wrong_tool: memory_search, memory_search | wrong_tool: memory_search, memory_search |
| d-031 | memory_approve | wrong_tool: memory_add, memory_approve | correct: memory_approve | correct: memory_approve | correct: memory_approve |
| d-036 | cron_create | correct, args miss: cron_create, cron_list; cron_create.schedule: regex (?i)mon.*(5\s*pm\|17)\|17 \* \* 1 (got "every 2 hours") | correct, args miss: cron_create; cron_create.schedule: regex (?i)mon.*(5\s*pm\|17)\|17 \* \* 1 (got "every 2 hours") | correct, args miss: cron_create; cron_create.schedule: regex (?i)mon.*(5\s*pm\|17)\|17 \* \* 1 (got "every 2 hours") | correct, args miss: cron_create; cron_create.schedule: regex (?i)mon.*(5\s*pm\|17)\|17 \* \* 1 (got "every 2 hours") |
| d-037 | cron_create | no_call | no_call | no_call | no_call |
| d-038 | cron_create | correct, args miss: cron_create; cron_create.schedule: regex (?i)(daily\|every day).*(midnight\|00:00\|12\s*am)\|0 0 \* \* \*\|@daily\|@midnight (got "daily at 9am") | correct: cron_create | correct: cron_create | correct: cron_create |
| d-047 | calendar__list_events | correct: calendar__list_events, calendar__list_events | correct: calendar__list_events, calendar__list_events | no_call | correct: calendar__list_events, calendar__list_events |
| d-048 | calendar__list_events | correct: calendar__list_events, calendar__list_events | correct: calendar__list_events, calendar__list_events | wrong_tool: cron_list, cron_list | correct: calendar__list_events |
| nm-003 | memory_add | wrong_tool: calendar__list_events, calendar__list_events | no_call | no_call | no_call |
| nm-004 | notes__create_note | correct: notes__create_note, notes__create_note | correct: notes__create_note | wrong_tool: write_file | correct: notes__create_note, notes__create_note |
| nm-007 | cron_create | wrong_tool: notes__search_notes, notes__search_notes | no_call | no_call | no_call |
| nm-008 | calendar__create_event | correct: calendar__create_event | correct: calendar__create_event | wrong_tool: cron_create | correct: calendar__create_event |
| nm-009 | tasks__add_task | correct: tasks__add_task | correct: tasks__add_task | wrong_tool: write_file | correct: tasks__add_task |
| nm-011 | cron_create | wrong_tool: shell, shell | wrong_tool: shell, memory_pending | wrong_tool: shell, memory_pending | wrong_tool: shell, memory_pending |
| nm-012 | tasks__add_task | correct: tasks__add_task | correct: tasks__add_task | wrong_tool: write_file | correct: tasks__add_task |
| nm-014 | notes__search_notes | correct: notes__search_notes | correct: notes__search_notes | no_call | correct: notes__search_notes |
| nm-015 | memory_search | correct: memory_search, memory_search | no_call | no_call | no_call |
| nm-016 | web__web_search | correct: web__web_search | correct: ?web_search, web__web_search | no_call | correct: web__web_search |
| nm-018 | memory_search | no_call | no_call | no_call | no_call |
| nm-020 | web__web_search | correct: web__web_search, web__web_search | wrong_tool: web_fetch | no_call | correct: web__web_search |
| nm-022 | list_files | correct, args miss: list_files, shell; list_files.path: regex ^(\./)?docs/?$ (got ".") | correct, args miss: list_files, list_files; list_files.path: regex ^(\./)?docs/?$ (got ".") | correct, args miss: list_files, list_files; list_files.path: regex ^(\./)?docs/?$ (got ".") | correct, args miss: list_files, list_files; list_files.path: regex ^(\./)?docs/?$ (got ".") |
| nm-024 | list_files | wrong_tool: shell, shell | correct: list_files, list_files | correct: list_files, list_files | correct: list_files, list_files |
| nm-026 | write_file | wrong_tool: read_file, patch_file | correct: write_file | correct: write_file | correct: write_file |
| nm-028 | email__send_email | correct: email__send_email | correct: email__send_email | no_call | correct: email__send_email |
| nm-029 | messages__send_message | correct: messages__send_message | no_call | no_call | no_call |
| nt-012 | none | called_on_no_tool: patch_file, patch_file | correct | correct | correct |
| nt-015 | none | called_on_no_tool: search_files | correct | correct | correct |
| ms-001 | read_file → notes__create_note | correct: read_file, notes__create_note | correct: read_file, notes__create_note | wrong_tool: read_file, write_file | wrong_tool: notes__create_note |
| ms-002 | notes__search_notes → messages__send_message | correct: notes__search_notes, messages__send_message | correct: notes__search_notes, messages__send_message | no_call | correct: notes__search_notes, messages__send_message |
| ms-003 | weather__get_weather → any_of(reminders__create_reminder, cron_create) | correct: weather__get_weather, reminders__create_reminder | wrong_tool: reminders__create_reminder | wrong_tool: cron_create | wrong_tool: reminders__create_reminder |
| ms-006 | search_files → read_file | correct: search_files, read_file | correct: search_files, read_file | wrong_tool: search_files | wrong_tool: search_files |
| ms-007 | web_fetch → write_file | correct: web_fetch, write_file | correct: web_fetch, write_file | wrong_tool: web_fetch | wrong_tool: web_fetch |
| ms-008 | weather__get_weather → any_of(email__send_email, messages__send_message) | correct: weather__get_weather, email__send_email | correct: weather__get_weather, email__send_email | wrong_tool: weather__get_weather, notes__create_note, patch_file | correct: weather__get_weather, email__send_email |
| ms-010 | memory_search → any_of(reminders__create_reminder, cron_create) | correct: memory_search, reminders__create_reminder | correct: memory_search, reminders__create_reminder | wrong_tool: memory_search, memory_add, cron_create | correct: memory_search, reminders__create_reminder |
