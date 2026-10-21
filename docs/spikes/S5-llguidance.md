# Spike S5: Qwen3.5 tool calls under an llguidance grammar

C-003, spike S5 of [the plan](../plan/corvid-agent.md#phase-0-prove-the-fabric-about-2-weeks), run 2026-10-21.

**Question.** Can Starling's Qwen client (`Starlings.Models.Qwen35`) constrain Qwen3.5-0.8B's tool-call output with
an llguidance grammar built from the offered tools (a tool-name enum plus each tool's argument schema), and what does
that cost? Upstream ask U-2 (§19) depends on the answer.

**Pass condition.** 0 invalid tool names, with no added latency over 10%.

## Result

**Feasible on the version Starling already pins. Fail as written on latency.** The condition names no percentile, so it is read as holding at the tail as well as the median. The p90 adds 15.5–20.5%; the median alone would pass (+6–8.5%).

- **Feasible, no upgrade.** ONNX Runtime GenAI 0.17.1, the version Starling pins exactly, has a public
  `GeneratorParams.SetGuidance(type, data, enableFFTokens)`. llguidance is compiled into both linux-x64 natives, CPU
  and CUDA. A 40-line change on a Starling branch passes it through, and a Lark grammar for Qwen's tool-call format runs
  end to end on the 3090. One packaging gap: GenAI's guidance needs `tokenizer.json` **and** `tokenizer_config.json` in
  the GenAI model directory, and Starling's published bundle has neither there.
- **Invalid tool names: 0. This meets the condition, but proves nothing,** because the model without the grammar also
  produced 0 invalid names in 200 runs on this set. That includes 48 prompts written to tempt invented names ("email
  Alice", "Google ...", "jot down"). On single-turn prompts, the failure the grammar removes is a different one: the
  model **refusing to call** a tool that is right there. On the 5-tool shortlist the unconstrained model answered in
  prose 15 times in 100. With the grammar (tool choice "required") it did so 0 times, and the right tool rose from 83
  to 95.
- **Latency: passes at p50, fails at p90.** Time to first token is unchanged, so grammar compilation is not visible
  even when the grammar changes every turn. Decode is 7–8% slower at the median, about 2 ms more per token. Total request time is +6% (all 12 tools) and +8.5% (5-tool shortlist) at the median of paired per-case
  ratios, and +15.5% and +20.5% at p90. An A/A control of the same cases without the grammar puts run-to-run noise at
  0% for the median and 3.2–3.7% for p90, so the tail cost is real.

**Recommendation.** Keep U-2 and make it concrete: no GenAI upgrade, a guidance option on Starling's generation
options, the two tokenizer files in the GenAI bundle, and a Qwen tool-grammar builder (see
[the U-2 ask](#the-u-2-ask)). In Corvid, use the grammar where a call is known to be wanted (the fabric's `tool` pick
and §7 step 6's "require that tool" re-prompt), not on every turn. Keep validation and repair (§7 steps 3–5). The
latency condition needs the fast-forward follow-up below before the grammar can go on every turn.

## Phase 1: what the pinned version exposes

Evidence comes from the packages Starling restores, not from memory or release notes.

**The pin.** `starling/ai-dotnet/Directory.Packages.props` at `442e0e4` (main, 2026-10-21):

```xml
<PackageVersion Include="Microsoft.ML.OnnxRuntimeGenAI.Managed" Version="[0.17.1]" />
<PackageVersion Include="Microsoft.ML.OnnxRuntimeGenAI" Version="[0.17.1]" />
<PackageVersion Include="Microsoft.ML.OnnxRuntimeGenAI.Cuda" Version="[0.17.1]" />
```

The props file explains why the pin is exact: the pilot bundle's graph and `genai_config.json` come from the 0.17.1
builder, and GenAI and builder versions must match (`Qwen35ModelAssets.BuilderVersion = "0.17.1"`). nuget.org's newest
version is 0.18.0, which S5 does not need.

**Managed API** (reflection over `microsoft.ml.onnxruntimegenai.managed/0.17.1/lib/net8.0/Microsoft.ML.OnnxRuntimeGenAI.dll`):

```
Void   Microsoft.ML.OnnxRuntimeGenAI.GeneratorParams.SetGuidance(String type, String data, Boolean enableFFTokens)  public
IntPtr Microsoft.ML.OnnxRuntimeGenAI.NativeMethods.OgaGeneratorParamsSetGuidance(IntPtr, Byte[] type, Byte[] data, Boolean)
```

**Native libraries** (`nm -D --defined-only` and `strings`):

| Library | Guidance exports | llguidance |
| --- | --- | --- |
| `microsoft.ml.onnxruntimegenai/0.17.1/runtimes/linux-x64/native/libonnxruntime-genai.so` (CPU) | `OgaGeneratorParamsSetGuidance`, `OgaTurnOptionsSetGuidance`, `OgaTurnOptionsClearGuidance`; `Generators::GuidanceLogitsProcessor` | linked in (`llguidance::earley::...`, `llguidance::lark::compiler`, `llguidance::tokenizer_json`) |
| `microsoft.ml.onnxruntimegenai.cuda/0.17.1/runtimes/linux-x64/native/libonnxruntime-genai.so` (CUDA package) | the same (`OgaGeneratorParamsSetGuidance`, 38 `GuidanceLogitsProcessor` symbols) | 416 exported llguidance symbols |
| `.../cuda/0.17.1/.../libonnxruntime-genai-cuda.so` | none (the CUDA provider plug-in; guidance lives in the main library) | — |

The guidance type strings in the native are `json_schema`, `regex` and `lark_grammar`. Its error strings include
"Guidance type and data must be provided together", "Unable to open guidance tokenizer data" and "Failed to parse
tokenizer json". No separate build flag is needed: the shipped packages were built with guidance.

**How Starling drives generation.** `GenAIModel.CreateSequence` builds a `GeneratorParams` (search options only), and
`GenAISequence.GenerateNextToken` calls GenAI's `Generator.GenerateNextToken()` and then reads `GetNextTokens()[0]`.
Starling does not sample logits itself, so a mask set on `GeneratorParams` applies inside GenAI's own step. The
Extensions.AI mapper still throws on any `ResponseFormat` other than text (`Qwen35ChatRequestMapper.cs:111`, `NotSupportedException`), as §2 records; nothing above
the GenAI adapter knows about guidance.

## The prototype (Starling branch `spike/s5-llguidance`)

Branch: <https://github.com/starlinghq/starling/tree/spike/s5-llguidance> (pushed, no PR, not for main).

**Library change (commit `1bb1ba9`).**

- `GenerationGuidance(Type, Data)` (with `Lark`, `JsonSchema` and `Regex` factories) and `GenerationOptions.Guidance`
  in `Starlings.Generation.OnnxGenAI`. These are carried into `GenAISequenceOptions.Guidance`, and `CreateSequence`
  calls `parameters.SetGuidance(type, data, enableFFTokens: false)` before `new Generator`. Fast-forward tokens are off
  because Starling's loop takes one token per step and counts usage that way; with them on, GenAI can append several
  tokens per step.
- `Qwen35ChatOptions.Guidance`, a pass-through.
- `Qwen35ToolGrammar.Build(tools, Required | Auto)` in `Starlings.Models.Qwen35`. It writes a Lark grammar for the
  format the pinned chat template teaches and `Qwen35ToolCallParser` reads, which is **not JSON**:

  ```
  start: call
  call: "<tool_call>\n" (f0 | f1 | ...) "</tool_call>"
  f9: "<function=send_message>\n" P9_0 P9_1 P9_2 "</function>\n"
  P9_2: /<parameter=channel>\n(sms|email|telegram)\n<\/parameter>\n/
  P10_1: /<parameter=units>\n(celsius|fahrenheit)\n<\/parameter>\n/
  P11_0: /<parameter=minutes>\n-?[0-9]+\n<\/parameter>\n/
  ```

  Parameters come in schema order. Required parameters must appear and optional ones carry `?`. Values are
  constrained by `enum`, `integer`, `number` and `boolean`; any other value (a string, object or array) is any text
  without `<`. Each parameter, from its opening tag to its closing tag, is a single terminal. llguidance's lexer is
  greedy, so a separate value terminal would swallow the value's final line feed and then fail to match
  `</parameter>`. A `json_schema` guidance would make the model emit JSON, which the parser returns as text, so §7's
  "`{name: const, arguments: schema}` union" has to be written in the template's XML form.

**Spike tool (commit `7f626ca`).** `ai-dotnet/tools/Starlings.Qwen35.GuidanceSpike` (not in `AI.slnx`, built with
`-p:UseCuda=true`) holds the fixtures, the runner, `summarize.py` and the raw runs in `results/`.

**Packaging gap found on the first run.** With the published `ortgenai-fp32x` directory, creating a guided generator
fails:

```
ONNX Runtime GenAI could not create a generator: Failed to open a json file:
.../model-notok/tokenizer_config.json
```

GenAI builds its own tokenizer for guidance from the model directory. The llguidance token trie reads
`tokenizer.json`, and GenAI's tokenizer also wants `tokenizer_config.json`. The Starling bundle keeps `tokenizer.json`
in `transformers-5.18.0/` (Starling tokenizes with `Starlings.Tokenizers`) and has no `tokenizer_config.json`. The spike
ran on a scratch directory of symlinks to the three published GenAI files, plus `transformers-5.18.0/tokenizer.json`
(SHA-256 `a5cd9732…`, the `qwen35-bpe-chat` asset) and `tokenizer_config.json` from `Qwen/Qwen3.5-0.8B@2fc06364`
(SHA-256 `49e2b6e3…`). Unguided generation is unaffected.

**Smoke check.** A `regex` guidance of `FORCED-[0-9]{3}` produced `FORCED-100` and stopped. That shows the mask is
applied through Starling's `GenerateNextToken` path. The Lark grammar on "What's the weather in Porto?" produced
`get_weather {"city":"Porto","units":"celsius"}` through the parser.

## Measurement

**Set.** No S2 golden set exists yet (the only Corvid spike branch is `spike/s1-kev-latency`), so S5 has its own: 12
personal-assistant tools and 100 single-turn prompts, each labelled with the expected tool and, for 80 of them, the
key arguments. The tools have deliberate near-duplicates (`search_notes`, `search_files` and `web_search`;
`create_note` and `create_reminder`), an integer id, an enum channel and an enum unit. 48 prompts are written to tempt
an invented name ("Text Bob", "Email Alice", "Google ...", "Cat the contents of ...", "Start a 25 minute pomodoro").
The set is in the tool's `fixtures/` and was committed before any run.

**Conditions.** Every case runs four interleaved generations (greedy, thinking off, at most 256 new tokens):

| Condition | Tools offered | Grammar |
| --- | --- | --- |
| free-all | all 12 | none |
| guided-all | all 12 | `Qwen35ToolGrammar` Required; the same grammar for every case |
| free-subset | the expected tool plus 4 others chosen by case id (a stand-in for §5.4's shortlist) | none |
| guided-subset | the same 5 | Required; the grammar differs per case, so compile-cache reuse cannot hide compile cost |

**Scoring.** An *invalid name* is a parsed call, or a well-formed `<function=X>` block, naming a tool that was not
offered. *Malformed* is a block naming an offered tool that the parser cannot read. *Args valid* means required
parameters are present, no undeclared parameters appear, and every value has its declared type and enum. *Args
match* means every labelled argument is equal (strings compared ignoring case and surrounding whitespace).
*Latency* is TTFT (call to first token), total, and decode tok/s ((tokens − 1) / (total − TTFT)). Guided against free
is compared as a per-case ratio, because the two can produce different lengths.

**Environment.** RTX 3090 (pinned by UUID under the shared GPU lock), CUDA provider, `ortgenai-fp32x`,
Microsoft.ML.OnnxRuntimeGenAI.Cuda 0.17.1, CUDA 13 and cuDNN 9 libraries. Two chunks of 50 cases (4 min 17 s and
5 min 15 s), then a 100-case A/A run (8 min 52 s) in which each guided condition is replaced by a repeat of its free
one.

### Outcomes

| Condition | Correct tool | Wrong tool | Invalid name | Malformed | No call | Args valid | Labelled args match |
| --- | --- | --- | --- | --- | --- | --- | --- |
| free-all | 88 | 8 | **0** | 1 | 3 | 96/96 | 85/88 |
| guided-all | 91 | 8 | **0** | 1 | 0 | 99/99 | 89/91 |
| free-subset | 83 | 2 | **0** | 0 | 15 | 85/85 | 79/83 |
| guided-subset | **95** | 5 | **0** | 0 | **0** | 100/100 | 92/95 |

- The unconstrained model never invented a name and never wrote an undeclared parameter or an out-of-enum value. Its
  failures were prose instead of a call ("I can't send messages directly, but I can help you with that...",
  "The 2022 FIFA World Cup was won by **Argentina**...") and wrong-but-offered tools.
- The grammar converts every no-call into a call, and most of those are the right tool (subset: 15 no-calls became 12
  correct and 3 wrong). It does not fix wrong-tool choices: guided-all has the same 8 as free-all.
- Case 23 ("Save a note called 'Book ideas'...") is *malformed* in both all-tools conditions. The model writes a
  run-on `body` until it hits 256 tokens, inside a free-text value the grammar allows. A grammar with
  `[^<]*` values does not bound length. A `max_tokens=` on value terminals (llguidance supports it) or a per-tool
  value-length cap would.
- Required mode removes "answer in text" by construction. That is only right when a call is known to be wanted, so the
  no-call improvement belongs to the fabric's "require that tool" path, not to every turn. `Qwen35ToolGrammarMode.Auto`
  (a call, or text not starting with `<`) is built but was not measured.

### Latency

Medians and p90s over 100 cases (ms; tok/s):

| Condition | TTFT p50 | TTFT p90 | Total p50 | Total p90 | Tokens p50 | Decode tok/s p50 |
| --- | --- | --- | --- | --- | --- | --- |
| free-all | 353.3 | 520.4 | 1,419.0 | 2,163.2 | 39 | 38.6 |
| guided-all | 341.7 | 522.4 | 1,517.6 | 2,199.5 | 39 | 35.3 |
| free-subset | 158.8 | 219.6 | 944.3 | 1,624.7 | 39 | 51.7 |
| guided-subset | 158.7 | 219.3 | 1,048.3 | 1,573.1 | 40 | 47.0 |

Paired per-case ratios, guided / free (and the A/A control, free repeat / free):

| Pair | TTFT p50 | TTFT p90 | Total p50 | Total p90 | Total p50, equal token count | Decode tok/s p50 |
| --- | --- | --- | --- | --- | --- | --- |
| guided-all / free-all | 0.983 | 1.069 | **1.060** | **1.155** | 1.053 (n = 59) | 0.930 |
| guided-subset / free-subset | 0.976 | 1.069 | **1.085** | **1.205** | 1.080 (n = 35) | 0.916 |
| A/A all | 1.003 | 1.097 | 1.000 | 1.032 | 1.000 (n = 100) | 1.002 |
| A/A subset | 0.998 | 1.098 | 1.000 | 1.037 | 1.000 (n = 100) | 0.999 |

- **TTFT is unchanged** (ratios within A/A noise), including guided-subset, whose grammar changes every case. Compiling
  a 5-tool or 12-tool grammar does not show up in time to first token.
- **The cost is per decoded token**: about 2.4 ms per token with 12 tools (25.9 → 28.3 ms) and 1.9 ms with 5 (19.3 →
  21.3 ms). llguidance is a CPU library inside the native, so the mask is most likely computed on the CPU (inferred, not profiled); if so, the relative cost grows as the GPU step gets faster. On a model or
  device that decodes faster than about 40 tok/s, expect more than 8%.
- Against "no added latency over 10%", the median passes (+6.0% and +8.5%), and so does the median on cases where both
  runs produced the same number of tokens (+5.3% and +8.0%). The p90 fails (+15.5% and +20.5%, against an A/A p90 of
  +3.2% and +3.7%).
- Fast-forward tokens (`enableFFTokens: true`) would emit the grammar's forced runs, such as `<parameter=channel>\n`
  and `</function>\n</tool_call>`, without a model step each, which should recover part of the cost. That needs
  Starling's decode loop to accept several tokens per step. It was not measured.

## What the result means

- **U-2 is not blocked on ONNX Runtime GenAI.** The pinned 0.17.1 has everything, on CPU and on CUDA. There is no
  version to wait for and no qualification rerun of the bundle's graph.
- **Invalid tool names are not this model's problem on single-turn prompts with ≤ 12 tools.** The grammar's value
  for Corvid is (a) a guaranteed call when the fabric has decided a call is wanted, which §7 step 6 currently gets by
  re-prompting, and (b) guaranteed enum and integer validity, which makes §7 step 5 rarer. It is not a
  replacement for the fabric's tool choice: wrong tools stay wrong. S2's multi-turn, longer-context set may still show
  invented names; the grammar would cover those too.
- **§7's sketch needs one correction**: the grammar has to describe the template's `<tool_call><function=...>` XML,
  not a JSON `{name, arguments}` object.

### The U-2 ask

Proposed text for §19 U-2, to raise on starlinghq/starling. None of it needs a GenAI upgrade; all of it was prototyped
on `spike/s5-llguidance` against `Microsoft.ML.OnnxRuntimeGenAI` 0.17.1:

1. A guidance option on `GenerationOptions`, `GenAISequenceOptions` and `Qwen35ChatOptions`, applied with
   `GeneratorParams.SetGuidance` (types `lark_grammar`, `json_schema`, `regex`).
2. `tokenizer.json` and `tokenizer_config.json` inside each GenAI variant directory of the Qwen3.5 bundle (asset locks
   and `Starlinghq/qwen3.5-0.8b-ortgenai`), or an acquisition step that places them there. Without them, a guided
   generator cannot be created.
3. A tool-grammar builder in `Starlings.Models.Qwen35` (Required and Auto) for the template's format, with value-length
   bounds, and `ChatToolMode.RequireSpecific`/`RequireAny` in the Extensions.AI adapter mapped to it. `ResponseFormat`
   JSON Schema maps to `json_schema`.
4. Fast-forward tokens supported in the decode loop, measured against the 10% budget at p90.

## Reproduce

```
git clone -b spike/s5-llguidance https://github.com/starlinghq/starling
cd starling/ai-dotnet/tools/Starlings.Qwen35.GuidanceSpike
dotnet build -c Release -p:UseCuda=true
# MODEL: symlinks to ortgenai-fp32x/{genai_config.json,model.onnx,model.onnx.data} + tokenizer.json + tokenizer_config.json
dotnet bin/Release/net10.0/Starlings.Qwen35.GuidanceSpike.dll --model-dir MODEL --cuda --smoke
dotnet bin/Release/net10.0/Starlings.Qwen35.GuidanceSpike.dll --model-dir MODEL --cuda --out runs.jsonl
dotnet bin/Release/net10.0/Starlings.Qwen35.GuidanceSpike.dll --model-dir MODEL --cuda --aa --out aa.jsonl
python3 -I summarize.py runs.jsonl
```

The CUDA build needs the CUDA 13 runtime, cuBLAS and cuDNN 9 on `LD_LIBRARY_PATH`. Building with `-p:UseCuda=true`
rewrites the CPU lock files of the referenced libraries; revert them before committing. The spike tool has only
`packages.cuda.lock.json`; a CPU build creates its own lock file.
