# ADR 0002 — In-process Rioplatense correction (LLamaSharp)

- **Status:** Accepted
- **Date:** 2026-08-22
- **Supersedes:** [ADR 0001](0001-stack-tecnologico.md) §3's "LLM opcional | Ollama por
  HTTP" row, along with the places that row echoes: the `Otto.PostProcessing` line in §3's
  project tree, §5.3's reasoning about Ollama's timeout against the latency budget, and the
  "instalar Ollama" item still open in §9. The post-processing decision only. Everything else
  in ADR 0001 (the platform choice, the five native primitives, Avalonia/Whisper.net/SQLite,
  why Linux is out of v1) is unaffected and not revisited here.
- **Product context:** [Product vision](../vision-producto.md)
- **Evidence:** `docs/hito-4-resultados.md` remains the historical record of what
  Ollama-based correction measured — §5 below explains why it is not rewritten. The numbers
  in this document come from real Vulkan GPU hardware runs of `tools/Otto.Bench`'s `voseo`
  command against the same bench corpus, recorded through the SDD change
  `drop-ollama-llamasharp-inprocess`.

---

## 1. Context

ADR 0001 §3 put Rioplatense correction behind Ollama, reached over HTTP at
`http://localhost:11434`, "fuera del camino crítico" — an optional service the user installs,
pulls a model into, and keeps running. Milestone 4 measured that this worked: 18% → 13% WER
on the bench corpus, 1.02 s dictation total.

It also meant correction was a property of the *machine*, not of Otto. `Settings.CorrectVoseo`
could be on and correction could still not run, because Ollama was not installed, was not
running, or had a different model pulled — a state the tray had to represent as
"reconectar," and one that contradicted the install-and-dictate promise every other part of
Otto keeps: no separate download, no separate service, no separate step. Whisper.net already
proved the alternative shape works — a model bundled and loaded by Otto itself, over Vulkan,
with no install step of its own.

A spike (2026-08-21, referenced in the `drop-ollama-llamasharp-inprocess` proposal) closed
the two open questions that made the alternative look implausible:

- **Does a second Vulkan-backed model coexist with Whisper.net's in the same process?**
  Yes — no native symbol clash, no crash, both load and run.
- **Is it fast enough and small enough?** Yes, on the spike's hardware (RTX 4060 Laptop, 8 GB
  VRAM): correction averaged 0.35 s / 0.87 s max against the 2 s hot-path budget, and combined
  VRAM usage (4.4–4.5 GB) came in *under* the existing Ollama setup's measured 4.8 GB.

## 2. Options evaluated

### A. Keep Ollama over HTTP — status quo, rejected

Still the easiest option for anyone who already has Ollama running for other tools, and it
cost Otto nothing further to keep (already shipped). Rejected anyway: it is the only external
runtime dependency Otto has, the only place "it works on my machine" can mean something
different for two users running the identical Otto binary, and the only reason the tray
needed a "reconectar" affordance at all. None of that is inherent to correction — it is
inherent to reaching it over a network socket, even a loopback one.

### B. In-process via LLamaSharp + Vulkan — chosen

Same shape Otto already uses for transcription: `LLamaSharp` (a `llama.cpp` binding, the LLM
analogue of Whisper.net's `whisper.cpp` binding) plus `LLamaSharp.Backend.Vulkan`, loading a
quantized GGUF Otto downloads and owns. `Otto.PostProcessing.csproj` stays `net10.0` — the
same portable-project precedent `Otto.Speech` already set carrying
`Whisper.net.Runtime.Vulkan`, so this does not introduce a new kind of project to the
solution, only a second native package in an existing one.

Rejected alternatives *within* this option, from the design's own architecture-decisions
table:

- **Fold correction into `Otto.Speech`.** Rejected — `Otto.PostProcessing` already exists as
  the seam for this exact responsibility; merging projects to save one `.csproj` is not a
  reason to blur it.
- **Allow CPU fallback.** Rejected — a silent CPU fallback on a 3B model blows the 2 s budget
  on every single dictation while still reporting `IsAvailable: true`. That is exactly the
  shape of failure CLAUDE.md's "'could not check' is not 'up to date'" rule exists to prevent
  elsewhere. The fix is the same one Whisper.net already gets — `WithAutoFallback(false)` —
  and the feature is gated on GPU presence instead (see §3).
- **Widen `IPostProcessor.IsAvailable` to a state enum, or rename `ProbeAsync`.** Rejected —
  `Otto.Core` is the boundary this architecture protects hardest, and the caller's real
  question ("can you correct, right now?") is still a boolean. The tray's richer four-state
  vocabulary lives entirely in `Otto.App.CorrectionTrayStates`, derived from that one boolean
  plus file-existence and load-settled state — not pushed down into the port.

### C. A different in-process runtime (raw `llama.cpp` P/Invoke, ONNX Runtime GenAI)

Not seriously evaluated as separate options. `LLamaSharp` was chosen for the same reason
`Whisper.net` was in ADR 0001 §2.A: a precompiled native runtime with a working Vulkan backend
already sits in Otto's dependency tree, so this is one more package on an already-proven
pattern, not a new toolchain decision.

## 3. Decision

| Component | Choice | Note |
|---|---|---|
| Correction engine | `LLamaSharp` + `LLamaSharp.Backend.Vulkan` 0.27.0, `WithAutoFallback(false)` | Same Vulkan-only-no-fallback stance as Whisper.net |
| Model | Qwen2.5-3B-Instruct, GGUF, Q4_K_M quantization, from Hugging Face | Same model family Milestone 4 already validated for this prompt |
| Context window | `ContextSize = 4096`, not the model's native 32k | `VoseoPrompt` (~700 tokens fixed) + 1,024-token dictation allowance + 512-token output cap = 2,236, rounded up. Qwen2.5-3B's GQA KV cache costs ~36 KB/token: ~147 MB at 4096 against ~1.2 GB at 32k, on an 8 GB budget shared with Whisper |
| Prompting | Hand-formatted ChatML (`ChatMlPrompt.cs`), not LLamaSharp's `ChatSession` default history transform | See §4 — the default transform is not real ChatML, and Qwen imitated it back as literal output text |
| Execution | `StatelessExecutor` — a fresh `LLamaContext` per call | See §4 — the alternative silently accumulated conversation history across unrelated dictations |
| Gating | `Settings.CorrectVoseo && HardwareProbe.Detect() == Acceleration.Gpu` | No GPU → not downloaded, not loaded, not offered. A "reintentar" that can never succeed on that hardware is worse than no button |
| Provisioning | Third leg of the existing `ModelProvisioner`, sequential after speech + VAD, non-fatal on its own failure | Zero migration code: an upgrading user is already missing the GGUF, which is itself `NeedsProvisioning`'s trigger |
| Loading | Deferred: `DictationPipeline.StartAsync` reaches `Idle` on Whisper alone; the corrector loads in the background afterward | Blocking would add the corrector's full load time to every launch of an autostart tray app |
| Safety net | `EditGuard` (unchanged) discards any correction that drifts >25% in length or touches >20% of words | Already existed for Ollama; now the only thing standing between an unsupervised 3B model and the user's document |
| Settings | `Settings.PostProcessingModel` deleted, not repurposed | It held an Ollama *tag* (`"qwen2.5:3b"`), meaningless as a local GGUF path; model selection stays out of scope |
| Tray | Four derived states — Loading / Ready / Missing / Failed / Unsupported — replacing the two-state connect/reconnect model | `CorrectionTrayStates.For`; no "reconnect" exists for a model with no connection to lose |
| Bench | `tools/Otto.Bench`'s `voseo` re-pointed at the real production `LlamaEngine`/`ICorrectionEngine` stack; `Ollama.cs` deleted | The only thing that exercises the real prompt/engine against real hardware — see §6 |

## 4. What the spike did not catch — found only against real hardware

The spike closed the two feasibility questions in §1, but the first real end-to-end `voseo`
run against the finished implementation surfaced a correction-quality regression the spike
could not have: **11 of 12 bench clips were rejected by `EditGuard`**, and the net WER was 19%
"corrected" against 18% untouched — worse than doing nothing, the opposite of Milestone 4's
result.

Two independent bugs, both from the first implementation pass, both confirmed by decompiling
the installed LLamaSharp 0.27.0 with `ilspycmd` rather than trusting its documentation:

1. **`ChatSession` defaulted to `LLamaTransforms.DefaultHistoryTransform`** — plain
   `"[Author]: message"` text, not real ChatML. Qwen2.5-Instruct was never trained on that
   shape, so it imitated the format back as literal output text (`"Assistant: ..."` prefixes,
   `"(No se requieren cambios)"` meta-commentary) instead of just correcting the sentence.
2. **`InteractiveExecutor`'s private `_is_prompt_run` flag** permanently flips `false` after
   its first-ever inference, and `LlamaEngine`'s `executor` field is a long-lived DI singleton
   reused across every dictation — so `ChatSession`'s "fresh prompt" branch only ever ran on
   the process's very first correction. Every correction after that silently continued one
   ever-growing shared conversation, violating `ICorrectionEngine.ChatAsync`'s own per-call
   contract.

Fix: a new pure `ChatMlPrompt.cs` (hand-formats real ChatML) and `LLama.StatelessExecutor` in
place of `ChatSession`/`InteractiveExecutor` — `StatelessExecutor` builds a genuinely fresh
`LLamaContext` per call, closing both bugs at once. A separate finding — dictated text
containing a literal `<|im_start|>`/`<|im_end|>` substring could forge a fake role boundary
inside the composed prompt, since the tokenizer parses special tokens by default — was closed
by neutralizing those exact substrings in every message's content before building the prompt
(`ChatMlPrompt.Neutralize`).

Measured after both fixes, across three full `dotnet run -- voseo` runs on real Vulkan GPU
hardware with the real GGUF:

| | Untouched (Whisper raw) | Ollama, Milestone 4 | In-process, pre-fix | In-process, post-fix |
|---|---:|---:|---:|---:|
| WER | 18% | 13% | 19% | 13–17% (median 14%) |
| `EditGuard` rejections | — | not measured | 11/12 | 0/12 |
| Correction latency, median | — | 0.33 s | 0.33 s | 0.51–0.62 s |
| Combined VRAM | — | 4.8 GB | ~4.5 GB | 4.4–4.5 GB |

The latency increase (0.33 s → ~0.5 s median) is expected and budget-compatible:
`StatelessExecutor` re-prefills the full system prompt and few-shot examples on every single
call instead of continuing a cached — but silently corrupted — KV cache; every clip still
lands well inside the 2 s hot-path budget. Sampling is non-deterministic, so WER varies run to
run; 13–17% is the observed range, not a single fixed number, and the model still occasionally
mis-conjugates or misses a voseo conversion — flagged as a follow-up in §7, not treated as
solved.

## 5. Why the historical record does not change

`docs/hito-4-resultados.md` and `docs/brief.md` measured what was true when Ollama ran
correction, and stay exactly as written. Rewriting them to describe LLamaSharp would not
correct history, it would erase it — the same principle ADR 0001 already applies to itself,
marking superseded decisions rather than rewriting them: §8 strikes through the milestones it
outgrew and notes what replaced them, and §3 now carries a blockquote pointing here instead of
having its decision table edited. This ADR, and §4's table
above, are where the record of *what changed and why* lives; the milestone documents remain
the record of *what was measured, when, against what was actually running at the time*.

## 6. Consequences

**In favor**

- No external dependency, no separate install step, no separate process to keep running —
  correction is a property of Otto again, matching every other optional feature ("everything
  optional degrades to nothing").
- The offline promise gets strictly stronger: ADR 0001 and CLAUDE.md's "Ollama runs on
  `localhost`, which does not break the promise" carve-out is gone because there is nothing
  left to carve out — after the first-run downloads, Otto opens zero sockets of any kind
  unless the user manually checks for updates.
- `ICorrectionEngine` gives the correction adapter test coverage it never had against Ollama
  (the HTTP client was welded directly into the adapter); `LlamaPostProcessorTests` now
  exercises timeout, `EditGuard` rejection, oversized input, and concurrent-load behavior
  headlessly.
- Combined GPU memory measured *lower* than the Ollama setup it replaces (4.4–4.5 GB vs.
  4.8 GB).

**Against, accepted**

- The installer's native payload roughly doubles (~58 MB → ~116 MB of Vulkan-related
  natives) — the direct cost of carrying the dependency inside Otto instead of asking the
  user to install it separately.
- First run grows from one download to up to two: ~1.6 GB (speech) to ~3.6 GB total on a GPU
  machine with correction enabled — see [Distribution and first
  run](../distribucion-y-primer-arranque.md) for how that is surfaced.
- Correction is now strictly GPU-only. Ollama could, in principle, run a 3B model on CPU
  (slowly); the in-process version does not even try — `WithAutoFallback(false)` and
  `HardwareProbe`-based gating mean a CPU-only user simply never sees the feature, rather than
  seeing it work badly. Judged the better trade: a feature that is honestly absent beats one
  that silently misses its own latency budget.
- `Otto.PostProcessing` gained real concurrency to get right — `LoadGenerationTracker` and
  `InFlightGate` exist only because a long-lived engine singleton with a retryable,
  uncancelable native load needs them. Ollama's HTTP client needed none of this; a stateless
  request either finished or timed out.
- The unit-test/real-hardware split described in §4 is now a standing risk, not a one-time
  incident: `LlamaEngine` itself has no unit tests (by design — no LLamaSharp type is
  reachable without a real GGUF and a real GPU), so `tools/Otto.Bench`'s `voseo` command is
  the only thing that can catch a correction-quality regression before a user does.

## 7. Open items / follow-ups

- **Sampling variance is unresolved, not just undocumented.** WER varying 13–17% run to run
  (§4) was invisible before the fixes in §4 because `EditGuard` was rejecting almost every
  correction anyway; now that corrections mostly land, the variance is visible and
  unaddressed. Worth a dedicated look at temperature/sampling parameters, not guessed at
  inside this change.
- **No checksum on either download.** Pre-existing gap for the Whisper model, inherited
  unchanged by the correction GGUF — `ModelDownloader` skips a file once `File.Exists` is
  true, so a corrupt download has no automatic recovery path; manual deletion is the
  current workaround.
- **A settings change to `CorrectVoseo` needs a relaunch** to trigger (de)provisioning, since
  `ProvisioningOptions` is built once from the startup snapshot — consistent with how
  `Language`/speech-model changes already behave, not a new limitation this change introduced.
