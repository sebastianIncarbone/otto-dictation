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

Otto sends nothing anywhere. The only network call in its entire life is downloading
the speech model the first time.

## What it does

- **Dictates into any program.** Editor, browser, chat, terminal. Wherever the
  caret is.
- **Fixes Rioplatense conjugation.** Writes `Instalá` and `corré`, not `Instala`
  and `corre`. Optional, using a local model.
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
(~50 MB) and run it.

It does not ask for administrator: Otto installs per user, into
`%LOCALAPPDATA%\Programs\Otto`. The installer lets you choose whether you want the
Start Menu and desktop shortcuts, and registers the usual entry under *Add or remove
programs*. **You do not need to install .NET or anything else.**

The first run downloads the speech model (~1.6 GB with a GPU, ~150 MB without) and
opens the window. After that it starts straight into the tray.

<details>
<summary><b>Prefer not to install anything?</b></summary>

`Otto-windows-x64.zip` (~78 MB) is the same application, portable: unzip it wherever
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

Open the window from the tray to browse your notes, search them, edit them, or
change the settings.

## Rioplatense correction (optional)

Whisper flattens *voseo*: where you say *"instalá"* it writes *"instala"*. If you
have [Ollama](https://ollama.com) installed, Otto fixes it:

```bash
ollama pull qwen2.5:3b
```

Otto detects it at startup on its own. **Without it, Otto works exactly the same**,
using Whisper's raw output.

[Milestone 4](docs/hito-4-resultados.md) explains why this cannot be a lookup table
of replacements, and why the prompt mattered more than the model.

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
- **Microphone quality matters more than the model does.**

## Documentation

| | |
|---|---|
| [Product vision](docs/vision-producto.md) | What it is, what it is for, what it is not |
| [ADR 0001 — Technology stack](docs/adr/0001-stack-tecnologico.md) | What was chosen, what was rejected, and why |
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
Otto.PostProcessing    Local model over HTTP: Rioplatense correction
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
Ollama (optional)

## License

[MIT](LICENSE). Use it, copy it, modify it, sell it — just keep the copyright notice.

Every dependency is MIT too: Whisper.net, Avalonia, NAudio, Microsoft.Data.Sqlite,
SkiaSharp and CommunityToolkit.Mvvm. The Whisper model does not ship inside the
package; it is downloaded separately, and OpenAI released it under MIT.
