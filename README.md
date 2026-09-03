<p align="center">
  <img src="docs/banner.png" alt="Otto — Grumpy Imp" width="100%">
</p>

<p align="center">
  <b>Offline Transcription, Totally Open</b><br>
  Hold a key, talk, let go. The text lands wherever your cursor was — in any app.
</p>

<p align="center">
  <a href="https://github.com/sebastianIncarbone/otto-dictation/actions/workflows/build.yml"><img src="https://github.com/sebastianIncarbone/otto-dictation/actions/workflows/build.yml/badge.svg" alt="build"></a>
  <a href="https://github.com/sebastianIncarbone/otto-dictation/releases/latest"><img src="https://img.shields.io/github/v/release/sebastianIncarbone/otto-dictation" alt="release"></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-blue" alt="MIT"></a>
  <img src="https://img.shields.io/badge/platform-Windows%2010%2B-lightgrey" alt="Windows 10+">
</p>

<p align="center">
  <a href="README.es.md">🇦🇷 Leer en español</a>
</p>

---

Transcription is 100% local. Your voice never leaves your machine.

> **The proof: unplug the internet and Otto keeps working.**

> **A note on language.** Otto's interface is in Spanish, and that is deliberate —
> it exists because Windows dictation is bad at Rioplatense Spanish, and correcting
> that is the whole point. The code, the docs and the issues are in English.

## Why it exists

Windows dictation is not accurate enough in Spanish, especially with technical
vocabulary and sentences that mix Spanish and English. Commercial alternatives fix
that, but they charge a subscription and send your audio to somebody else's servers.

Otto sends nothing anywhere. The only network calls in its entire life are downloading the
models it runs on: speech the first time, correction alongside it on a GPU machine, and a
reading voice if you turn reading on and press the button that fetches it. Nothing after
that, ever, unless you manually check for updates — and then only the download you asked
for, if you tell it to install one. Every one of those is something you started.

## What it does

- **Dictates into any program.** Editor, browser, chat, terminal. Wherever the
  caret is.
- **Fixes Rioplatense conjugation.** Writes `Instalá` and `corré`, not `Instala`
  and `corre`. Optional, using a local model.
- **Reads back what you selected.** Select text anywhere, press a key, and hear it
  in an Argentine voice. Optional, and the only optional feature that does not need
  a GPU.
- **Keeps everything.** Every dictation becomes an editable note with a title,
  full-text search and one-click copy. Dictating and saving are the same action.
- **Lives in the tray**, with a character that shows what it is doing.

<p align="center">
  <img src="docs/poses.png" alt="Otto's states" width="100%">
</p>

## Measured performance

On an RTX 4060 Laptop, running `large-v3-turbo` on Vulkan:

| | |
|---|---:|
| Transcription | 0.57 s |
| Rioplatense correction | 0.33 s |
| Text injection | 0.13 s |
| **Total, from releasing the key to seeing text** | **1.02 s** |

**Latency does not depend on how much you dictate.** `whisper.cpp` processes a fixed
30-second window, so one sentence and one paragraph cost the same:

| Audio dictated | Transcription |
|---:|---:|
| 4 s | 0.45 s |
| 12 s | 0.53 s |
| 30 s | 0.56 s |

Without a GPU the story is different — between 17× and 37× slower — which is why
Otto probes the hardware at startup and downloads a smaller model when it has to.

Every number and how it was obtained: [milestone 0](docs/hito-0-resultados.md),
[milestone 0.5](docs/hito-0-5-resultados.md), [milestone 4](docs/hito-4-resultados.md).

## Installation

Download **`Otto-Setup.exe`** from [the latest release](https://github.com/sebastianIncarbone/otto-dictation/releases/latest)
(~75 MB) and run it.

It does not ask for administrator: Otto installs per user, into
`%LOCALAPPDATA%\Programs\Otto`. The installer lets you choose whether you want the
Start Menu and desktop shortcuts, and registers the usual entry under *Add or remove
programs*. **You do not need to install .NET or anything else.**

The first run downloads the speech model (~1.6 GB with a GPU, ~150 MB without), and — on a
GPU machine, since Rioplatense correction is on by default — the ~2 GB correction model too,
for **~3.6 GB total**. Both downloads are resumable and show progress. A machine with no GPU
never fetches the correction model at all: a 3B model cannot answer inside the dictation
budget on CPU, so Otto does not offer to try. After the download(s) finish, Otto opens the
window once, then starts straight into the tray on every later launch.

The reading voice is **not** part of that: reading aloud is off by default, and its ~110 MB
voice is downloaded by a button in Settings when you turn it on. Nothing about ticking a
checkbox should start a transfer you did not ask for.

<details>
<summary><b>Prefer not to install anything?</b></summary>

`Otto-windows-x64.zip` (~127 MB) is the same application, portable: unzip it wherever
you like and run `Otto.App.exe`. Useful for a USB stick, or a machine where you
cannot install software.

The difference is how you remove it: the installed copy is uninstalled from *Add or
remove programs*; the portable one cleans up after itself from Otto's own settings,
and you delete the folder yourself.
</details>

Uninstalling **does not delete your notes or the downloaded model unless you say
so.** Reinstalling gives everything back exactly as it was, without another 1.6 GB
download.

> **Windows will show the blue SmartScreen warning.** Otto is not signed with a code
> signing certificate, which costs several hundred dollars a year. Click **More
> info** → **Run anyway**.
>
> Being suspicious is reasonable. That is why the source is public and readable, and
> why **every release publishes its VirusTotal analysis next to the file's SHA-256**
> — so you can confirm the analysis belongs to exactly the binary you downloaded and
> not some other one. Both are in each release's notes.
>
> Some engines will flag it, and that is not a mistake: Otto registers a global
> hotkey and synthesises keystrokes, which is functionally the description of a
> keylogger. Publishing the report with those flags visible beats asking you to
> trust us.

## Usage

Hold **Ctrl+Alt+Space**, talk, release. The text appears where the caret was.

Select text anywhere and press **Ctrl+Alt+L** to have it read back to you; press it
again to stop. Both combinations can be rebound in Settings.

Open the window from the tray to browse your notes, search them, edit them, or
change the settings. The character can be dragged anywhere on screen and stays where
you leave it.

## Rioplatense correction (optional)

Whisper flattens *voseo*: where you say *"instalá"* it writes *"instala"*. Otto fixes it
itself, in-process — no separate service to install or keep running. On first run, if your
machine has a GPU, Otto downloads a small local model (Qwen2.5-3B-Instruct, ~2 GB) alongside
the speech model and corrects every dictation with it.

**Correction needs a GPU.** A 3B model cannot finish inside the ~2 s dictation budget on CPU,
so on a machine with no GPU the model is never downloaded, never loaded, and the setting has
no effect — Otto just uses Whisper's raw output. You can also turn correction off yourself,
in Settings or from the tray, which unloads the model and gives the VRAM back straight away.

**It also gets out of the way on its own.** Keeping a 3B model resident costs around 2 GB of
VRAM, which is a lot to hold while you are not dictating — so after 15 minutes idle Otto
unloads it. The next dictation goes in uncorrected and quietly reloads the model in the
background; the one after that is corrected again. That trade is configurable in Settings,
including turning it off so the model stays loaded.

A correction that goes wrong is never worse than doing nothing: `EditGuard` discards any
result that rewrites too much of the sentence, and the raw transcription goes in instead —
see [ADR 0002](docs/adr/0002-in-process-correction-llamasharp.md) for how that is measured.

[Milestone 4](docs/hito-4-resultados.md) explains why this cannot be a lookup table of
replacements, and why the prompt mattered more than the model — measured back when correction
ran over Ollama; the mechanism moved in-process since ([ADR 0002](docs/adr/0002-in-process-correction-llamasharp.md)), the reasoning did not.

## Reading aloud (optional)

Select text in any application, press **Ctrl+Alt+L**, and Otto reads it back. Press it
again to stop. It exists for accessibility first: somebody who cannot comfortably read the
screen gets it read to them, in their own accent, without the text leaving the machine.

**This is the one optional feature that survives without a GPU**, and that is the whole
reason for the engine behind it. Otto uses Piper, measured at **×4.6** faster than real
time on the same hardware where the modern-sounding alternative, Qwen3-TTS, manages
**×0.69** — slower than speech itself, so it falls further behind for as long as it reads.
A reader that cannot keep up is not a premium tier, it is a broken one. The measurements
are in [ADR 0003](docs/adr/0003-read-aloud-piper.md).

The engine ships inside the package. **The voice does not**, and downloading it is a
button in Settings rather than something ticking a checkbox does for you: `es_AR/daniela`
is ~110 MB, and the one rule Otto has about the network is that a person decides, not the
application. Five other Spanish voices — Mexican and Peninsular — are in the same list.

While a reading is in progress a small card floats over the screen with pause, repeat and
speed (×1, ×1.5, ×2), and the character wears a different face. Speed is a time-stretch
applied to audio that is already rendered, not a synthesis parameter, so it takes effect on
the sentence you are hearing rather than the one after next — and the pitch does not shift
with it. *Entonación* is the other dial, and it is deliberately not called *Calidad*: Piper
publishes voices at four quality tiers, but `daniela` exists only at the top one, so
descending a tier would cost the accent. What is left changes how a single model is
sampled, which costs neither.

## Known limitations

- **Windows only.** Linux is out of v1 for a concrete reason: Wayland blocks, by
  design, three of the five primitives Otto needs.
  [The details](docs/adr/0001-stack-tecnologico.md). macOS is out permanently —
  there is no hardware to maintain it on.
- **Some antivirus software will flag it.** Otto registers a global hotkey and
  synthesises keystrokes, which is functionally the description of a keylogger. The
  default hotkey mode installs no system-wide hook, precisely for this reason.
- **Elevated terminals reject the injection.** An unprivileged process cannot type
  into a window that is running elevated.
- **Reading the selection goes through the clipboard.** Otto sends Ctrl+C to the
  application you were in, reads what comes back, and restores your clipboard exactly as
  it was — but that copy is performed by the other application, so the exclusion flags
  Otto sets on its own dictations cannot be applied to it. A clipboard manager may
  still record the selection.
- **Microphone quality matters more than the model does.**

## Documentation

| | |
|---|---|
| [Product vision](docs/vision-producto.md) | What it is, what it is for, what it is not |
| [ADR 0001 — Technology stack](docs/adr/0001-stack-tecnologico.md) | What was chosen, what was rejected, and why |
| [ADR 0002 — In-process correction](docs/adr/0002-in-process-correction-llamasharp.md) | Why Ollama was dropped for an in-process model, and what changed |
| [ADR 0003 — Reading aloud with Piper](docs/adr/0003-read-aloud-piper.md) | Why the faster engine beat the better-sounding one |
| [Milestone 0 — Latency and accuracy](docs/hito-0-resultados.md) | The viability gate |
| [Milestone 0.5 — `initial_prompt`](docs/hito-0-5-resultados.md) | Technical vocabulary |
| [Milestone 4 — Voseo correction](docs/hito-4-resultados.md) | Why the prompt mattered more than the model |
| [Distribution and first run](docs/distribucion-y-primer-arranque.md) | The seven traps between "it builds" and "a stranger can use it" |
| [Otto.Bench](tools/Otto.Bench/README.md) | The measurement harness |

## Architecture

```
Otto.Core              Ports and orchestration. No operating system code.
Otto.Speech            Whisper.net: transcription, VAD, per-context prompt
Otto.Storage           SQLite with FTS5: notes and search
Otto.PostProcessing    LLamaSharp + Vulkan, in-process: Rioplatense correction
Otto.Tts               Piper as a child process: voices, download, reading aloud
Otto.Platform.Windows  P/Invoke: global hotkey, injection, clipboard, overlay
Otto.App               Avalonia: tray, notes window, character
```

`Otto.Core` knows nothing about Windows, and **the compiler enforces that boundary**:
portable projects target `net10.0` and the ones touching Win32 target
`net10.0-windows`, so a P/Invoke in the core does not compile.

The whole pipeline is tested without a microphone, without a GPU and without a
focused window. If that were not possible, the separation would be decoration.

## Building

```bash
dotnet build                        # requires the .NET 10 SDK
dotnet test
.\build\publicar.ps1                # builds dist\Otto-Setup.exe and dist\Otto-windows-x64.zip
.\build\publicar.ps1 -NoInstaller   # portable ZIP only
```

The installer needs Inno Setup (`winget install JRSoftware.InnoSetup`). If it is
missing, `publicar.ps1` fails instead of skipping it: a release that ships without an
installer because a step was silently skipped is one nobody notices until somebody
asks where the file went.

### Publishing a version

```bash
git tag v0.2.0
git push origin v0.2.0
```

CI builds, tests, packages both artifacts and creates the release. **The version
comes from the tag**, not from the source: if the application reported a version
different from the published one, the update check would lie silently forever.

## Stack

.NET 10 · Avalonia UI · Whisper.net (`large-v3-turbo`, Vulkan runtime) · SQLite ·
LLamaSharp (Qwen2.5-3B-Instruct, Vulkan, optional, GPU-only) · Piper (VITS, optional) ·
NAudio + SoundTouch.Net for playback

## License

[MIT](LICENSE). Use it, copy it, modify it, sell it — just keep the copyright notice.

Most dependencies are MIT too: Whisper.net, Avalonia, NAudio, Microsoft.Data.Sqlite,
SkiaSharp, CommunityToolkit.Mvvm and Piper itself. The Whisper model does not ship inside
the package; it is downloaded separately, and OpenAI released it under MIT.

Two of the components Otto **redistributes as files** carry other terms, and
[`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md) — copied into the install directory,
not merely committed here — states them in full. `SoundTouch.Net`, which drives the
reading speed control, is LGPL-2.1-or-later: it sits in the program directory as its own
assembly precisely so it can be replaced, which is why Otto is published self-contained
but neither single-file nor trimmed. `espeak-ng`, GPL-3.0-or-later, is bundled inside
Piper's own release and runs as part of `piper.exe`, a separate process Otto talks to over
standard input rather than links against. The reading voices are CC-BY-4.0 or MIT
depending on the voice, and are downloaded rather than shipped.
