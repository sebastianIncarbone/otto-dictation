# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

Otto is a Windows push-to-talk dictation app: hold `Ctrl+Alt+Space`, speak, release, and
the transcription is pasted into whatever window had focus. Transcription is fully local
(Whisper.net over Vulkan). Every dictation is also saved as a searchable note.

## Commands

```bash
dotnet build                       # solution is Otto.slnx (.NET 10 SDK required)
dotnet test
.\build\publicar.ps1               # → dist\Otto-Setup.exe AND dist\Otto-windows-x64.zip
.\build\publicar.ps1 -Version 0.2.0
.\build\publicar.ps1 -NoInstaller  # portable ZIP only
.\build\icono.ps1                  # regenerate src\Otto.App\Otto.ico on its own
```

Packaging needs Inno Setup (`winget install JRSoftware.InnoSetup`). `publicar.ps1`
looks for `ISCC.exe` **before** compiling anything and throws if it is missing —
skipping the installer silently would ship a release nobody notices is incomplete.
CI installs it with `choco install innosetup`.

Single test / single class:

```bash
dotnet test --filter "FullyQualifiedName~EditGuardTests"
dotnet test --filter "FullyQualifiedName~Otto.Core.Tests.EditGuardTests.El_acento_cuenta_como_cambio"
```

Run the app against a recording instead of the microphone — the only way to exercise the
full pipeline without speaking into it:

```bash
dotnet run --project src/Otto.App -- --selftest path\to\clip.wav
```

Measurement harness (`tools/Otto.Bench`, run from its own directory because `clips/` and
`models/` are resolved relative to the working directory):

```bash
dotnet run -- probe                        # which native runtimes actually load here
dotnet run -- models --models large-v3-turbo,base
dotnet run -- record                       # record the fixed clip set once
dotnet run -- review                       # regenerate clips/referencias.json
dotnet run -- bench --runtime vulkan       # one runtime per process, never a loop
dotnet run -- voseo                        # compare Ollama correction models
```

Releasing: push a tag. `git tag v0.2.0 && git push origin v0.2.0` — CI builds, tests,
packages and publishes the GitHub release.

## Architecture

Hexagonal, with the boundary enforced by the compiler rather than by convention:

| Project | TFM | Role |
|---|---|---|
| `Otto.Core` | `net10.0` | Ports (`Ports.cs`) + orchestration (`DictationPipeline`). No OS code. |
| `Otto.Speech` | `net10.0` | Whisper.net transcription, Silero VAD, per-context `initial_prompt`, resumable model download |
| `Otto.Storage` | `net10.0` | SQLite + FTS5 notes, embedded SQL migrations |
| `Otto.PostProcessing` | `net10.0` | Ollama over HTTP for Rioplatense correction, plus `EditGuard` |
| `Otto.Platform.Windows` | `net10.0-windows` | P/Invoke: hotkey, WASAPI capture, clipboard injection, overlay, GPU probe |
| `Otto.App` | `net10.0-windows` | Avalonia tray app, notes window, settings, updates, uninstall |

`Otto.Core` targets `net10.0` on purpose: a P/Invoke leaking into the core does not
compile, and `CA1416` fires on anything Windows-only that drifts into a portable project.
**Do not add a Windows TFM or `SupportedOSPlatform` suppression to a portable project to
make something compile** — that erases the only mechanism holding the boundary.

Composition happens in one place: `src/Otto.App/Program.cs` wires every port to its
adapter, builds the `ServiceProvider`, and hands it to Avalonia via `App.Services`.

### Dictation flow

`DictationPipeline` is the whole product in one class, and the ordering inside it is
load-bearing:

1. **On press** — capture the foreground window *first*, then start recording. The prompt
   has to match where the user was speaking from, and they may switch windows mid-sentence.
2. **On release** — stop capture and fire the async work *without awaiting it*. The hotkey
   callback runs on a Win32 message loop; blocking it freezes every hotkey in the system.
3. Transcribe → post-process → inject. Correction is on the critical path deliberately:
   fixing text already in the user's document would mean rewriting what they are looking at.
4. **Save the note after injection, never before.** The latency budget is spent once the
   text appears; a disk write must not be able to add to it.
5. Any exception is logged and swallowed, and the state returns to `Idle`. A background
   dictation tool that dies on one bad transcription leaves the user with nothing running.

Tests in `tests/Otto.Core.Tests/DictationPipelineTests.cs` exercise all of this with
NSubstitute doubles — no microphone, no GPU, no focused window. Keep it that way; if the
pipeline stops being testable headlessly, the port separation has become decoration.

## Invariants worth knowing before you change anything

These are decisions with measurements or incidents behind them. Most are documented inline
at the point of enforcement — read the surrounding comment before overriding one.

- **Offline is the product claim.** The only network calls are the first-run model download
  and the *manually triggered* update check (`Settings.CheckForUpdates` is off by default).
  Ollama runs on `localhost`, which does not break the promise. Do not add startup network
  traffic.
- **Everything optional degrades to nothing.** No Ollama → `NullPostProcessor` and raw
  Whisper output. No tray icon → open the window instead. Character window throws → log and
  keep dictating. A failed correction or save can never cost the user their dictation.
- **The version comes from the git tag.** `Directory.Build.props` holds `<Version>`; CI
  overrides it from the tag via `publicar.ps1 -Version`. If the assembly version diverges
  from the published tag, `UpdateChecker` answers "up to date" forever and never fails
  loudly. `UpdateChecker.Current` reads `InformationalVersion`, not `AssemblyVersion`.
- **"Could not check" is not "up to date."** `UpdateResult` has three states for exactly
  this reason. Do not collapse them.
- **VAD is a gate, not a splitter** (`WhisperTranscriber.Trim`). No speech at all → return
  empty without invoking the model, which is what stops Whisper inventing text out of
  silence. Otherwise trim to first-to-last speech region and run *one* inference. Splitting
  per region measured ~10× slower and transcribed worse.
- **Both models get warmed up at startup.** Vulkan compiles compute pipelines on first use
  (measured >10 s cold vs 0,8 s warm) and Ollama cold-loads past its own 2-second budget.
  Without the warm-up, the first dictation after install looks broken.
- **`EditGuard` rejects on proportion, not correctness.** In production there is no
  reference to compare against, so a correction that moves too much of the sentence
  (>25% length drift or >20% of words touched) is discarded and the raw text goes in. The
  failure that matters is a silent rewrite of meaning, not a missed conjugation.
- **The hotkey polls for release on purpose.** `RegisterHotKey` never signals release, and
  the alternative — `WH_KEYBOARD_LL` — installs a system-wide keyboard hook that is
  structurally a keylogger and draws antivirus attention. Consequence: bindings must include
  a non-modifier key.
- **Clipboard injection has two obligations**, not one: restore what the user had, *and*
  set the exclusion formats so clipboard managers and Windows Clipboard History never
  record the dictation.
- **The GPU probe runs before the download.** `HardwareProbe` checks for `vulkan-1.dll` and
  picks `large-v3-turbo` (~1,6 GB) or `base` (~150 MB). Same phrase: 0,7 s on GPU, 17 s on
  CPU — someone who gets the wrong model concludes the tool is broken.
- **The overlay character window must never take focus and must stay click-through.**
  Stealing focus right before injection sends the dictation into Otto instead of the user's
  document.
- **Program files and user data are sibling directories, never nested.** The installer owns
  `%LOCALAPPDATA%\Programs\Otto`; the data lives in `%LOCALAPPDATA%\Otto` and
  `%APPDATA%\Otto`. Nest them and `Uninstaller.Run()` — which deletes the data directories
  recursively — would be deleting the running executable.
- **There are two uninstall paths and they must not both fire.**
  `Uninstaller.InstalledUninstaller()` reads Inno's `HKCU` key to tell the distributions
  apart. Installed → hand off to Windows, which asks about the data itself. Portable →
  Otto does it. Doing both would wipe the notes while orphaning the program files and the
  Add/Remove Programs entry.
- **A silent uninstall keeps the user's data.** Inno answers *yes* to Yes/No message boxes
  suppressed with `/SUPPRESSMSGBOXES`, ignoring the default button — so asking
  unconditionally would delete the notes and a 1,6 GB model with no prompt whenever
  someone uninstalls from a script. `CurUninstallStepChanged` checks `WizardSilent()` and
  preserves; `/DELETEDATA` is the explicit opt-in.
- **Autostart is not offered by the installer,** on purpose. Otto's settings already own
  that checkbox and it reflects the real `Run` key; a second writer would be silently
  undone the first time the user hits Save.
- **Settings are amended, never rebuilt.** Both the settings window (`MainViewModel.ApplyTo`)
  and the tray menu (`App.SetCharacterVisible`) write to `config.json`, and neither shows
  every field. Always `store.Load() with { … }`; constructing a fresh `Settings` silently
  resets everything the writer does not know about — that bug reset the hotkey binding on
  every save and stayed invisible only because the default matched.
- **The character switch has two owners that must agree.** The tray item toggles and
  persists; the settings checkbox raises `CharacterVisibilityChanged` and lets `App` apply
  it without persisting again. `App.SetCharacterVisible(persist:)` is what keeps that from
  becoming an infinite bounce, and `ReflectCharacterVisibility` pushes the tray's choice
  back into the window so its checkbox is not stale.
- **Hiding the overlay is `Hide()`, never `Close()`.** The click-through and never-focus
  styles are applied to the native handle in `OnOpened`; closing would destroy it and the
  overlay would come back focus-stealing.
- **The installer's version is read from the published binary**, never from the `-Version`
  parameter, so it cannot diverge from what the app reports about itself.
- **An installer does not fix SmartScreen.** That is a signing problem, and it is unsolved
  and documented. Do not describe the installer as fixing it.

## Conventions

- **`TreatWarningsAsErrors` is on** solution-wide. Fix warnings; don't suppress them.
- **Language split:** identifiers, XML doc comments and code comments in English; strings
  the user sees, log messages, and `docs/` in Rioplatense Spanish. Test method names are
  Spanish snake_case sentences describing behaviour
  (`Guarda_la_nota_despues_de_inyectar_y_no_antes`).
- **Comments explain *why*, at length, where a decision is non-obvious or was measured.**
  This is the dominant style of the codebase — match it rather than stripping it.
- **Schema changes are new numbered files** in `src/Otto.Storage/Migrations/`, embedded as
  resources and applied once each by `Migrator`, one transaction per script. Never edit an
  applied migration. FTS5 runs in external-content mode, so the triggers in `001-notas.sql`
  are what keep the index in sync.
- **Ports go in `Otto.Core/Ports.cs`**, alongside the doc comment explaining what the
  adapter is obliged to do.
- Tests: xunit + NSubstitute. `Otto.Core.Tests` covers the pipeline, `EditGuard`,
  `UpdateChecker` and the SQLite repository.

## Runtime locations

| | |
|---|---|
| Program files (installed) | `%LOCALAPPDATA%\Programs\Otto` — per user, no elevation |
| Settings | `%APPDATA%\Otto\config.json` |
| Notes database | `%LOCALAPPDATA%\Otto\otto.db` (WAL) |
| Models | `%LOCALAPPDATA%\Otto\models\` |
| Autostart | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, value `Otto` |
| Add/Remove Programs | `HKCU\...\Uninstall\{08FD4B32-9406-4142-A528-1E908B2A4A09}_is1` |

The uninstall GUID is the `AppId` in `build/otto.iss` plus Inno's `_is1` suffix, and
`Uninstaller.cs` hardcodes the same string. Changing one without the other makes an
installed Otto believe it is portable.

`models/`, `clips/`, `resultados-*.md`, `dist/` and `src/Otto.App/Otto.ico` are gitignored.
Benchmark clips are the author's voice and models are gigabytes; the icon is generated by
`build/icono.ps1` for the same reason the tray icons are drawn in code — no binary assets
in the repo.

## Background reading

`docs/adr/0001-stack-tecnologico.md` is the stack decision and what was rejected (including
why Linux is out: Wayland blocks three of the five primitives Otto needs).
`docs/distribucion-y-primer-arranque.md` is the packaging checklist `publicar.ps1` satisfies.
The `docs/hito-*.md` files carry the measurements the invariants above rest on.
