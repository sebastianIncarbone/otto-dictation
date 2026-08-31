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
dotnet run -- voseo                        # measure the in-process Rioplatense corrector
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
| `Otto.PostProcessing` | `net10.0` | In-process Rioplatense correction (LLamaSharp + Vulkan), plus `EditGuard` |
| `Otto.Tts` | `net10.0` | Reading aloud with Piper as a child process: voice catalogue, download, voicing presets |
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

- **Offline is the product claim, and it is stronger now than it used to be.** The only
  network calls in Otto's entire life are the first-run model downloads (speech, VAD and —
  GPU-only — correction, see below) and the *manually triggered* update path
  (`Settings.CheckForUpdates` is off by default). There used to be a carve-out here for
  Ollama running on `localhost`; it is gone because there is nothing left to carve out —
  correction runs in-process now, so after the downloads finish there is no network call of
  any kind, ever, unless the user clicks "check for updates". Do not add startup network
  traffic. That update path now has *two* steps that reach the network — the version check,
  and downloading the installer if the user then asks for it — and both are behind their own
  explicit click; see the self-install invariant below for why that second click is not
  optional.
- **Everything optional degrades to nothing.** No GPU at all (`Program.cs` wires
  `IPostProcessor` to `NullPostProcessor` whenever `acceleration != Acceleration.Gpu`, full
  stop) → raw Whisper output. `CorrectVoseo` off, the correction GGUF missing or still
  downloading, a failed load, or an idle-timed-out unload → `LlamaPostProcessor.IsAvailable`
  stays (or goes back to) false and dictation runs on raw Whisper output too — same
  degradation, reached a different way; `Enabled` and `IsAvailable` are deliberately two
  separate booleans for exactly this reason, see the runtime-toggle invariant below. No tray
  icon → open the window instead. Character window throws → log and keep dictating. A failed
  correction or save can never cost the user their dictation.
- **The version comes from the git tag.** `Directory.Build.props` holds `<Version>`; CI
  overrides it from the tag via `publicar.ps1 -Version`. If the assembly version diverges
  from the published tag, `UpdateChecker` answers "up to date" forever and never fails
  loudly. `UpdateChecker.Current` reads `InformationalVersion`, not `AssemblyVersion`.
- **"Could not check" is not "up to date."** `UpdateResult` has three states for exactly
  this reason. Do not collapse them.
- **Otto can install its own update, and every constraint around that is deliberate.**
  `UpdateInstaller` downloads the release's `Otto-Setup.exe`, checks it against the
  `SHA256SUMS` published beside it, and runs it with `/SILENT /CLOSEAPPLICATIONS`; `App`
  then disposes the pipeline and shuts down so Inno can overwrite the running executable.
  Four things hold it up, and none is decoration.
  **(1) Two clicks, never zero.** The check is manual and off by default already; fetching
  ~58 MB and *executing* it is the larger act, not the smaller one, so it inherits that rule
  and adds its own confirmation. There is no background update path and there should not be.
  **(2) It fails closed on the hash.** `UpdateChecker.InstallerFrom` returns null unless the
  release published *both* the installer and `SHA256SUMS`, so `UpdateStatus.CanInstall` is
  false for every release cut before this existed and the UI falls back to the link.
  `UpdateInstaller.HashesMatch` rejects anything that is not a full 64-char hex digest —
  without that length check an unreadable checksums file and a failed hash computation both
  produce empty strings, which compare equal, and the only gate between a downloaded
  executable and running it opens by accident. `UpdateInstallerTests` pins that case by name.
  Be honest about what the hash buys: it catches truncation, corruption and mismatched
  assets, but it is fetched from the same host over the same connection as the file it
  describes, so it is **not** a signature. TLS to GitHub is the real guarantee, and code
  signing is still the unsolved problem — do not let this feature get described as making the
  download "verified" in the security sense.
  **(3) Installed copies only.** Gated on `Uninstaller.InstalledUninstaller()`, the same
  HKCU key that already tells the two distributions apart. The portable ZIP has no installer
  to run, and a program replacing its own running executable in place is a mess that fails
  halfway.
  **(4) `build/otto.iss` has a second `[Run]` line and it is load-bearing.** The original one
  carries `skipifsilent`, and `RestartApplications=no` means the Restart Manager will not
  bring Otto back either — so without `Filename: "{app}\{#Exe}"; Flags: nowait; Check:
  WizardSilent` a self-install leaves Otto installed and *closed*, gone from the tray with
  the hotkey dead. Solving it with `RestartApplications=yes` instead would also fire on
  interactive installs, where the existing postinstall checkbox already covers it, and launch
  two instances.
  Note that the release workflow now uploads `SHA256SUMS` as an asset and derives the hashes
  printed in the release notes *from that same file* — computing them twice is two chances to
  disagree, and the copy inside the prose is the one nobody re-checks.
- **VAD is a gate, not a splitter** (`WhisperTranscriber.Trim`). No speech at all → return
  empty without invoking the model, which is what stops Whisper inventing text out of
  silence. Otherwise trim to first-to-last speech region and run *one* inference. Splitting
  per region measured ~10× slower and transcribed worse.
- **Whisper still warms up at startup; the corrector warms up in the background, deferred —
  and only when the user actually wants it on.** Vulkan compiles compute pipelines on first
  use (measured >10 s cold vs 0,8 s warm), so `WhisperTranscriber.LoadAsync` is awaited before
  the hotkey registers — that cost is paid once, before `Idle`, not on the first dictation. The
  correction model is different on purpose: `DictationPipeline.StartAsync` reaches `Idle` on
  Whisper alone, then fires `LoadCorrectorAsync` unawaited in the background (see its own doc
  comment — blocking on it would add the corrector's full load time, plausibly several seconds
  cold, to every launch of an autostart tray app). `LoadCorrectorAsync` checks
  `IPostProcessor.Enabled` **first**, before calling `ProbeAsync` at all: `Program.cs` now wires
  `IPostProcessor` to the real `LlamaPostProcessor` on any GPU machine regardless of
  `Settings.CorrectVoseo` (see the runtime-toggle invariant below for why), so this check is
  what still keeps a correction-disabled launch from loading a ~2 GB model nobody asked for.
  `LlamaPostProcessor.WarmUpAsync` runs one throwaway correction right after `LoadAsync`
  succeeds, for the same reason Whisper's pipeline gets compiled ahead of time: the first real
  correction should not be the one paying Vulkan's first-use cost. Until that background load
  settles, or if it ever fails, or the model has since idle-unloaded, dictation runs on raw
  Whisper output — `IsAvailable` is false, not slow.
- **The correction model's context window is bounded on purpose, not left at the model's
  native size.** `PostProcessingOptions.ContextSize` is **4096**, not Qwen2.5-3B's native
  32k. The math: `VoseoPrompt`'s system message plus its few-shot examples costs ~700 tokens
  fixed, 1,024 tokens are reserved for the dictation itself, and 512 for the model's own
  output — 2,236 tokens, rounded up with margin. Qwen2.5-3B's GQA KV cache costs ~36 KB/token,
  so 4096 is ~147 MB of VRAM against ~1.2 GB at the native 32k — on an 8 GB budget shared with
  Whisper, that difference is not decoration. `LlamaPostProcessor` returns the raw
  transcription instead of letting llama.cpp truncate or throw when a dictation does not fit
  inside the 1,024-token allowance; do not "fix" that by raising `ContextSize` back up.
- **`Otto.PostProcessing` has three purpose-built concurrency primitives — do not collapse
  them into a single lock.** They exist because one blocking, uncancellable native call sits
  under a retryable load on a disposable singleton, and each solves a different consequence
  of that. `CancelableWork` bounds the *caller's* wait on `LLamaWeights.LoadFromFile`, which
  takes no token of its own — without it `ProbeTimeout` was accepted and silently ignored, so
  a hung load could never be given up on. It bounds the wait only: the worker keeps running.
  `LoadGenerationTracker` exists because of exactly that — `LlamaEngine` is a long-lived DI
  singleton whose `LoadAsync` is retryable, so an abandoned attempt can still finish after a
  newer one superseded it; without generation tracking whichever native call returns *last*
  wins arbitrarily, and the loser's handles leak. `InFlightGate` covers a different race
  again: llama.cpp's token loop has no cooperative cancellation once inside it, so disposing
  the context or weights mid-call is a native access violation rather than a catchable
  managed exception — `Dispose()` therefore waits for in-flight calls (bounded, 3 s) and
  skips the free rather than tearing down underneath one. (Separately and more ordinarily,
  `LlamaPostProcessor` holds a plain `SemaphoreSlim` so `ProbeAsync` loads once under
  concurrent callers rather than once per caller.) All three are deliberately free of any
  LLamaSharp type, so the races themselves are unit-testable without a GGUF or a GPU even
  though the calls they guard are not.
- **Correction is a runtime on/off toggle now, not just a startup decision — and unloading the
  model is deliberately a different operation from disposing it.** `Settings.CorrectVoseo` (the
  Ajustes checkbox) and the tray's correction item both call `IPostProcessor.SetEnabledAsync`,
  and an idle timer calls the same unload path automatically: `Settings.CorrectionIdleUnloadMinutes`
  (Ajustes, default **15**, **0 means "never"**) becomes `PostProcessingOptions.IdleUnloadInterval`
  — a `TimeSpan?`, where **null**, not `TimeSpan.Zero`, is what "never" has to mean, since zero
  would be a real, immediately-due deadline instead of the absence of one. `IdleUnloadScheduler`
  (a fourth purpose-built, LLamaSharp-free primitive, tested the same way as the three above —
  see `IdleUnloadSchedulerTests`, driven by an injected `TimeProvider` instead of real minutes)
  measures idle time from the last correction *or* the last successful load, and on expiry calls
  `LlamaPostProcessor.UnloadAsync`, which frees the native weights but — unlike `Dispose()`,
  terminal, called once at process shutdown — leaves the object reusable: a later `ProbeAsync`
  loads again on the SAME instance. That distinction runs all the way down into `LlamaEngine`:
  `InFlightGate` gained `TryUnload`/`Reopen` (closes the gate like `TryDispose` does, but a
  later successful `LoadAsync` reopens it instead of it staying closed forever) and
  `LoadGenerationTracker` gained `Unload` (bumps the generation to orphan an in-flight load the
  same way `Dispose` does, but does **not** set the tracker's permanent `disposed` flag, so a
  later `LoadAsync` can `ClaimGeneration` and publish normally). Do not "simplify" `UnloadAsync`
  into calling `Dispose()` and rebuilding the engine — that would defeat the entire point of
  reusing the same instance and would race a concurrent `ProbeAsync` the way the original
  `Dispose()`-only design never had to consider. After an idle unload, the very next dictation
  still degrades to raw text (`IsAvailable` is false — the existing, already-documented
  behaviour) and `LlamaPostProcessor.ProcessAsync` fires a background `ProbeAsync` so the
  *following* dictation is corrected again; it never makes a dictation wait on the reload. The
  DI composition root reflects the same split: `Program.cs` always wires the real
  `LlamaPostProcessor` on GPU hardware regardless of `CorrectVoseo`'s startup value (see the
  deferred-load invariant above) — the alternative, gating the DI decision on `CorrectVoseo`,
  is exactly the trap that made turning correction back on at runtime impossible, since a fixed
  DI graph cannot swap `NullPostProcessor` for a real corrector after the process has started.
- **`EditGuard` rejects on proportion, not correctness.** In production there is no
  reference to compare against, so a correction that moves too much of the sentence
  (>25% length drift or >20% of words touched) is discarded and the raw text goes in. The
  failure that matters is a silent rewrite of meaning, not a missed conjugation. This is the
  entire reason an unsupervised in-process 3B model is safe to ship: `EditGuard` does not
  need the correction to be *right*, only bounded.
- **`tools/Otto.Bench`'s `voseo` command is the only thing that can catch a
  correction-quality regression.** `LlamaPostProcessorTests` mocks `ICorrectionEngine` by
  design, so the adapter logic (timeouts, `EditGuard` rejection, degradation to raw text) is
  testable headlessly — which also means no unit test ever runs a real GGUF through a real
  prompt. `dotnet run -- voseo` is what actually exercises the production
  `LlamaEngine`/`ChatMlPrompt` stack against the bench corpus. Skipping it before a change to
  the prompt, the engine, or the model is how a regression ships silently: it happened once
  already during this feature's own implementation (an unescaped ChatML prompt plus a
  singleton executor silently accumulating history across dictations pushed WER from 18%
  untouched to 19% "corrected" — worse than doing nothing — while every mocked unit test
  stayed green throughout).
- **The tray's correction indicator is derived, never stored, and there is deliberately no
  "reconnect."** `CorrectionTrayStates.For` checks `HasGpu` **first**: no GPU short-circuits
  to `Unsupported` ahead of every other input, because a "reintentar" that can never succeed
  on that hardware is worse than no button at all. `CorrectVoseo` is checked **second**, ahead
  of `IsAvailable` — a machine with correction switched off reads as `Off` regardless of
  whether the model happens to still be resident (the brief window between a toggle click and
  `UnloadAsync` actually settling): the header has to say what the user just asked for, not
  what has not caught up yet. Only past both of those does it read `IPostProcessor.IsAvailable`,
  whether the GGUF file exists, and whether the deferred load has settled, to return
  Ready / Missing / Loading / Failed. There is no "connecting" state left over from the Ollama
  era: an in-process model has no connection to lose. The item itself is built on the tray menu
  whenever `HasGpu` is true now — **not** gated on `CorrectVoseo` the way it used to be, because
  a switched-off user still has to be able to turn correction back on from here, and its click
  handler is a genuine two-way toggle (`Off` → on, `Ready`/`Loading` → off) that only falls back
  to the original single "reintentar" action for `Missing`/`Failed`, where the user already
  wants correction on and a click means "try again," not "give up."
- **Reading aloud is the first optional thing that does not vanish without a GPU, and that
  is the point of the engine choice.** `Otto.Tts` runs `piper.exe` as a child process, one
  per fragment. It was measured against Qwen3-TTS over the same corpus: Piper x4,6, Qwen
  **x0,69** — slower than speech itself, so it falls further behind the longer it reads.
  That is not a premium tier, it is a broken one, and it is why correction's
  `acceleration == Gpu` gate has no equivalent here (see ADR 0003). Three consequences are
  load-bearing. **(1)** `piper.exe` resolves `espeak-ng-data` **relative to the working
  directory, not to its own location** — launched from anywhere else it starts, exits zero,
  and writes a WAV full of silence with no error anywhere. `PiperSynthesizer` pins
  `WorkingDirectory`, checks the output file even after a clean exit, and `publicar.ps1`
  verifies both halves are packaged. **(2)** The "effort level" the feature was sketched with
  is *not* a quality ladder. Piper ships voices at x_low/low/medium/high, but `es_AR/daniela`
  — the only Argentine voice in the entire catalogue — exists at `high` and nowhere else, so
  descending a tier costs the accent. What survives is `PiperVoicing`, which changes how one
  model is sampled; Ajustes calls it *Entonación*, never *Calidad*. **(3)** The engine is
  fetched by `publicar.ps1` and cached under `build/.piper`, never committed — a 21 MB
  third-party executable does not clear this repo's bar for a binary.
- **Reaching the selection means waiting for the modifiers to come up first, and it is not
  politeness.** Synthetic input is merged with the real keyboard state rather than replacing
  it, so the Ctrl+C `ClipboardSelectionReader` sends while the user is still holding
  Ctrl+Alt+L arrives at the target application as **Ctrl+Alt+C**, which almost nothing treats
  as copy — every reading would silently fall back to the old clipboard and the selection
  would never be read once. Synthesising key-ups is not the fix: the physical keys are still
  down, so Windows contradicts the released state on its next sample and the target sees a
  keyup its user never performed. The clipboard is then restored, including being *emptied*
  when it started empty. **Residual limitation, stated rather than hidden:** the copy is
  performed by the source application, so the exclusion formats `ClipboardTextInjector`
  relies on cannot be applied to it, and a clipboard manager may still record the selection.
- **A reading renders exactly one fragment ahead of the one playing.** The overlap is the
  entire reason `Sentences.Split` exists — the listener waits for the first fragment and
  nothing else. One ahead rather than all of them is equally deliberate: rendering the whole
  document up front spends a process launch and a temp file on every fragment of a text the
  user is about to stop three sentences in. Both halves have a test. The audio is temporary
  and deleted; only an explicit "keep this" ever moves a file somewhere permanent.
- **One rule governs every reading trigger: while a reading is in progress, anything that
  would start one stops it instead.** The hotkey, the notes button, whatever comes later. Two
  rules for one feature would mean the user has to remember which control they pressed to
  know what happens next. A failed *reading* hotkey registration is swallowed, unlike the
  dictation one which is surfaced: dictation is the product, reading is optional and obeys
  "everything optional degrades to nothing."
- **The hotkey polls for release on purpose.** `RegisterHotKey` never signals release, and
  the alternative — `WH_KEYBOARD_LL` — installs a system-wide keyboard hook that is
  structurally a keylogger and draws antivirus attention. Consequence: bindings must include
  a non-modifier key.
- **Clipboard injection has two obligations**, not one: restore what the user had, *and*
  set the exclusion formats so clipboard managers and Windows Clipboard History never
  record the dictation.
- **The GPU probe runs before the download, and now it gates a second model.**
  `HardwareProbe` checks for `vulkan-1.dll` and picks `large-v3-turbo` (~1,6 GB) or `base`
  (~150 MB) for transcription. Same phrase: 0,7 s on GPU, 17 s on CPU — someone who gets the
  wrong model concludes the tool is broken. The same probe result now also decides whether
  the correction model gets downloaded at all: `ProvisioningOptions.CorrectionCoordinates`
  returns real GGUF coordinates whenever `HasGpu` is true — **not** gated on `CorrectVoseo`
  since correction can be switched on at runtime and `ProvisioningOptions` is built once at
  startup, see the runtime-toggle invariant below for the trap this closes; on CPU-only
  hardware the ~2 GB Qwen2.5-3B-Instruct correction model is never fetched, never loaded, and
  the tray never offers it. Whether a download actually *runs* right now is a separate,
  live question `ModelProvisioner.NeedsProvisioning`/`ProvisionAsync` answer instead, via their
  own `correctionEnabled` parameter — both read from the same `ProvisioningOptions` fields, so
  the two stay in agreement by construction rather than by convention. On a GPU machine with
  correction enabled, first run grows from ~1,6 GB to ~3,6 GB.
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
- **Otto is a `WinExe`, so the console sink logs into nothing.** `AddSimpleConsole`
  only produces output when someone runs Otto from a terminal, which is never how a
  user runs it. `LogFile` is the record that survives, and `Crash.Install` /
  `Crash.InstallUiHandler` are what put a failure into it — before this, the process
  vanished from the tray leaving no trace at all. The UI-thread handler marks the
  exception handled on purpose: a broken notes window is not worth ending the
  dictation service the user actually launched, the same trade `DictationPipeline`
  already makes.
- **`[STAThread]` on `Program.Main` is load-bearing, and that is why the entry point
  is a real method rather than top-level statements** — the compiler-generated `Main`
  cannot carry attributes. Without it the process thread is MTA, `OleInitialize`
  answers `RPC_E_CHANGED_MODE`, and every OLE-backed Avalonia surface (the clipboard,
  the file picker) throws on first use. It cannot be corrected at runtime:
  `TrySetApartmentState` returns false once the thread is already MTA. Otto's own
  `ClipboardTextInjector` is unaffected because it uses the raw Win32 clipboard, which
  has no apartment requirement — which is exactly why dictation kept working while
  copying a note killed the app.
- **An installer does not fix SmartScreen.** That is a signing problem, and it is unsolved
  and documented. Do not describe the installer as fixing it.

## Conventions

- **`TreatWarningsAsErrors` is on** solution-wide. Fix warnings; don't suppress them.
- **Language split.** The repository is public and English-facing; the *product* is not.
  - **English:** identifiers, every comment, log and exception messages, `docs/`,
    `README.md`, commit messages, issues.
  - **Rioplatense Spanish:** everything a user of the app reads — window text, tray menu,
    first-run console output, the installer wizard. Otto exists because Windows dictation
    is bad at Rioplatense; an English UI would contradict the product. Do not "fix" these.
  - `README.es.md` is a courtesy translation and says so; `README.md` is the source of
    truth. If they disagree, English wins.
  - `tools/Otto.Bench` keeps Spanish console output *and* Spanish test clips. Those clips
    **are** the Rioplatense corpus being measured — translating them destroys the harness.
  - Test method names stay Spanish snake_case sentences
    (`Guarda_la_nota_despues_de_inyectar_y_no_antes`). They read as sentences, no outside
    reader sees them, and renaming 36 of them is churn.
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
| Reading voices | `%LOCALAPPDATA%\Otto\models\voices\` — user data, survives an upgrade |
| Reading engine | `piper\` beside the executable — ships with Otto, replaced by the installer |
| Log | `%LOCALAPPDATA%\Otto\logs\otto.log` (plus one rotated `otto.log.1`) |
| Autostart | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, value `Otto` |
| Add/Remove Programs | `HKCU\...\Uninstall\{08FD4B32-9406-4142-A528-1E908B2A4A09}_is1` |

The uninstall GUID is the `AppId` in `build/otto.iss` plus Inno's `_is1` suffix, and
`Uninstaller.cs` hardcodes the same string. Changing one without the other makes an
installed Otto believe it is portable.

`models/`, `clips/`, `resultados-*.md`, `dist/` and `src/Otto.App/Otto.ico` are gitignored.
Benchmark clips are the author's voice and models are gigabytes; the icon is generated by
`build/icono.ps1` for the same reason the tray icons are drawn in code.

**Two kinds of binary do live in the repo, both under `src/Otto.App/Assets/`, and the bar
for a third is high.** The character art is authored and there is no way to generate it.
The redesign's fonts — Archivo and JetBrains Mono, OFL, with their licences beside them —
are there because the alternative is fetching them from Google Fonts at startup, which
would break the offline promise outright; they are static instances rather than the
variable files because Avalonia exposes no variation axes, so the expanded width the
design calls for is only reachable from an already-instanced face. Everything else is
still generated or drawn in code.

## Background reading

`docs/adr/0001-stack-tecnologico.md` is the stack decision and what was rejected (including
why Linux is out: Wayland blocks three of the five primitives Otto needs).
`docs/adr/0002-in-process-correction-llamasharp.md` is why Ollama was dropped for an
in-process corrector, superseding 0001's post-processing decision only.
`docs/adr/0003-read-aloud-piper.md` is why reading aloud uses Piper out of process rather
than the better-sounding Qwen3-TTS, and carries the measurements behind it. It supersedes
nothing — reading is a new capability, not a revision.
`docs/distribucion-y-primer-arranque.md` is the packaging checklist `publicar.ps1` satisfies.
The `docs/hito-*.md` files carry the measurements the invariants above rest on —
`docs/hito-4-resultados.md` specifically measured the Ollama-era corrector and stays as
written; ADR 0002 carries the current numbers.
