# ADR 0003 — Reading aloud with Piper

- **Status:** Accepted
- **Date:** 2026-08-28
- **Supersedes:** nothing. This is a new capability, not a revision. ADR 0001's stack
  decisions and ADR 0002's in-process correction are both unaffected — reading sits beside
  them rather than replacing anything.
- **Evidence:** the `spike/tts-poc` branch (commits `885143a`..`b26be25`), which measured
  both candidate engines over the same corpus, the same fragmentation and the same
  stopwatch, and whose `matriz` command produced the table in §2.

---

## 1. Context

Otto listens. This is Otto reading back — the user selects text anywhere on screen, presses
a key, and hears it.

The use case that drove the spike is accessibility: somebody who cannot comfortably read the
screen wants it read to them, in their own accent, without sending the text anywhere. That
frames the whole decision, because it sets a hard floor no amount of quality can compensate
for. A reader that generates slower than a person speaks does not "sound better and take
longer" — it falls progressively further behind for as long as it reads, and the gap never
closes.

Two engines were plausible in 2026:

- **Qwen3-TTS** (12 Hz, 0.6B/1.7B). Apache 2.0 weights, supported by llama.cpp mainline
  through its `llama-tts` tool. Modern prosody, and it can clone a voice from about three
  seconds of reference audio.
- **Piper** (2023, VITS). A ~110 MB ONNX file per fixed voice. No cloning, no speaker
  reference, prosody a generation behind.

## 2. What was measured

`matriz`, over 641 characters, on an RTX 4060 Laptop:

| configuration | generation | audio | real-time factor |
|---|---|---|---|
| qwen | 57,6 s | 39,9 s | **x0,69** |
| piper-default | 6,8 s | 31,5 s | **x4,63** |
| piper-medio | 6,7 s | 31,1 s | x4,62 |
| piper-estable | 6,6 s | 31,0 s | x4,69 |
| piper-lento | 6,9 s | 31,9 s | x4,65 |

Two things in that table matter more than the headline.

**Qwen is below 1,0.** With a whole GPU behind it. That is the floor from §1, and it fails
it — not narrowly, by a factor of one and a half.

**Qwen also speaks more slowly**: 39,9 s of audio against Piper's 31,0 s for identical text.
The gap a listener perceives is wider than the generation factors alone suggest.

Two earlier findings from the same spike are worth recording because both corrected a wrong
conclusion:

- `llama-tts -ngl 99` does not produce slow audio, it **aborts**. The GPU path for the audio
  codec asserts; the codec is pinned to the CPU deliberately.
- The bottleneck is **not** the vocoder. Its own timings say so: prompt eval 0,09 s +
  generation 32,61 s + vocoder 2,55 s. Generation is 92% of the work. An earlier note in this
  spike claimed otherwise and was wrong.

## 3. Decision

**Piper, run as a child process, with the Argentine voice as the default.**

The engine choice is not a compromise between quality and speed; it is the only candidate
that clears the floor. And the reach matters as much as the speed: Piper is ONNX on the CPU,
so reading is the first optional thing Otto has ever offered that does *not* disappear on a
machine without a GPU, where `Program.cs` wires `NullPostProcessor` and the user gets
nothing.

The cost is not symmetric either, and one row decides it:

| | Piper | Qwen3-TTS |
|---|---|---|
| download | ~110 MB voice + ~21 MB runtime | ~1,2 GB |
| hardware | CPU, any machine | GPU or hopeless |
| speed | x4,6 | x0,69 |
| .NET path | ONNX Runtime has a NuGet package | none exists |

LLamaSharp binds `libllama`; Qwen3-TTS runs through `libmtmd`. Shipping it would mean
bundling `llama-tts.exe` and spawning a child per sentence from a tray application, or
writing the bindings by hand.

### 3.1 Out of process, knowingly

Piper is ONNX and ONNX Runtime has a perfectly good .NET package, so an in-process
integration is possible and would remove the per-fragment launch cost — which the spike
measured as the dominant term, Piper climbing from x3,09 to x4,6 on chunk size alone.

It is not free. Piper phonemises with **espeak-ng**, and ONNX Runtime supplies the neural
network, not the phonemiser. In-process means a native espeak-ng binding plus VITS pre- and
post-processing written by hand, against a measured x4,6 that already has four times the
headroom the feature needs. Subprocess first, in-process later behind the same
`ISpeechSynthesizer` port, is the deliberate order.

### 3.2 The effort ladder does not survive

The feature was sketched with a quality ladder: a cheap reading and an expensive one. Both
halves of that idea died against measurement.

The expensive engine cannot read in real time at all (§2), so it is not a premium tier — it
is a broken one.

And Piper's own `x_low`/`low`/`medium`/`high` ladder cannot be descended here. The complete
Spanish catalogue on `rhasspy/piper-voices`:

| voice | tiers | accent |
|---|---|---|
| `es_AR/daniela` | **high only** | **Rioplatense** |
| `es_MX/claude` | high | Mexican |
| `es_MX/ald` | medium, x_low | Mexican |
| `es_ES/davefx` | medium | Peninsular |
| `es_ES/sharvard` | medium | Peninsular |
| `es_ES/carlfm` | x_low | Peninsular |

**There is exactly one Argentine voice and it exists at one tier.** A cheaper tier costs the
accent, and CLAUDE.md is explicit that Otto exists because Windows dictation is bad at
Rioplatense and that an English UI would contradict the product. A reading voice is that
argument with the volume up. Voice selection is offered — somebody who wants a male voice is
entitled to one — but the default is not in question, and the ladder is not on the table.

What survives as "effort" is `PiperVoicing`: how one model is sampled (`--noise_w`,
`--noise_scale`, `--length_scale`), at no cost in speed or accent. Labelled *Entonación* in
Ajustes, not *Calidad*, for exactly this reason.

## 4. Consequences

- The ZIP grows from 105 MB to 127 MB; the installer by roughly the same. The engine is
  fetched by `build/publicar.ps1` and cached, never committed — CLAUDE.md's bar for a third
  binary in the repository is high, and a 21 MB third-party executable does not clear it.
- A voice is a separate ~110 MB download that deliberately does **not** happen at first run.
  Reading ships off; turning it on in Ajustes is what asks for the download, and that is its
  own button rather than something *Guardar* does quietly.
- `piper.exe` resolves `espeak-ng-data` **relative to the working directory, not to its own
  location**. Launched from anywhere else it starts, exits zero, and produces silence with no
  error. This is the feature's nastiest failure mode; `PiperSynthesizer` pins
  `WorkingDirectory`, checks the output file even after a clean exit, and `publicar.ps1`
  verifies both halves are packaged.
- Reaching the on-screen selection means a synthetic Ctrl+C, so the clipboard is borrowed and
  restored — the same two obligations `ClipboardTextInjector` already carries, which is why
  the Win32 calls moved into a shared `WindowsClipboard`. **Residual limitation:** the copy is
  performed by the source application, so the exclusion formats cannot be applied to it and a
  clipboard manager may still record the selection. Restoring afterwards does not undo that.
- Accented words sometimes land oddly. It is **not** encoding: feeding espeak-ng deliberately
  mangled cp1252 bytes and dumping phonemes with `--debug` produces the same output as clean
  UTF-8, character for character. It is the acoustic model's stochastic duration prediction,
  which is what the voicing presets reach.

## 5. What is deferred, not rejected

**Qwen3-TTS is not dropped; it is the wrong engine for this feature.** Reading a screen has a
clock. *Exporting a note as an audio file* does not — nobody waits on it, sixty seconds for a
better voice is fine, and it is the only engine that can read in the user's **own** voice.
That is a separate feature with a separate gesture, and this ADR does not decide it.

Also deferred: rebinding the reading hotkey. It is fixed at Ctrl+Alt+L, because a second copy
of the capture state machine is not worth introducing in the same change as the feature.
**Known hole:** if another application already holds that combination, registration fails,
Otto degrades to nothing as designed, and there is no way to change it from the UI — only by
editing `config.json`.
