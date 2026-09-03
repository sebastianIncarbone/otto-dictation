# Third-party notices

Otto itself is MIT (see `LICENSE`). It ships alongside several third-party components,
listed here with the terms they carry. This file is copied into the installed program
directory by `build/publicar.ps1`, so a user who has Otto has these notices too.

This covers the components Otto **redistributes as files** — the ones whose licences ask
for a notice to travel with the binary. It is not a full dependency graph; run
`dotnet list package --include-transitive` for that.

## SoundTouch.Net — LGPL-2.1-or-later

<https://github.com/owoudenberg/soundtouch.net>, a managed C# rewrite of Olli
Parviainen's SoundTouch. Otto uses it for the reading transport's speed control: it
changes playback tempo without shifting pitch.

**This one has terms the others do not, and they are met by construction rather than by
promise.** LGPL section 4 requires that the user be able to replace the library with a
modified version. Otto is published self-contained but **not** single-file and **not**
trimmed, so `SoundTouch.Net.dll` sits in the program directory as its own assembly and can
be swapped for another build of the same API. Do not turn on `PublishSingleFile` or
`PublishTrimmed` without dealing with this first — both would bury the library inside
Otto's own executable and the relinking freedom would stop being available.

The library is used unmodified, through its public API, with no source changes.

## Piper — MIT

<https://github.com/rhasspy/piper>, © Michael Hansen. The reading engine, shipped in
`piper/` beside Otto's executable. Fetched at packaging time by `build/publicar.ps1`, never
committed to this repository.

### espeak-ng — GPL-3.0-or-later

Bundled inside Piper's own release archive as `piper/espeak-ng-data` plus its library, and
used by Piper for phonemisation. It runs as part of `piper.exe`, a separate process Otto
starts and communicates with over its own standard input — Otto does not link against it.
Source: <https://github.com/espeak-ng/espeak-ng>.

## Piper voices — CC-BY-4.0 / MIT, per voice

Downloaded on demand from <https://huggingface.co/rhasspy/piper-voices> into
`%LOCALAPPDATA%\Otto\models\voices`, never shipped in the installer. Each voice carries its
own licence in the model card beside it; `es_AR-daniela-high` derives from a corpus with
its own attribution terms.

## NAudio — MIT

<https://github.com/naudio/NAudio>, © Mark Heath. WASAPI capture and playback.

## Avalonia — MIT

<https://github.com/AvaloniaUI/Avalonia>. The UI framework.

## Whisper.net — MIT

<https://github.com/sandrohanea/whisper.net>, wrapping ggerganov's whisper.cpp (MIT).
Transcription.

## LLamaSharp — MIT

<https://github.com/SciSharp/LLamaSharp>, wrapping ggerganov's llama.cpp (MIT). The
in-process Rioplatense correction.

## Models

The speech, VAD and correction models are downloaded on first run rather than shipped, and
each carries its own terms: Whisper (MIT), Silero VAD (MIT), and Qwen2.5-3B-Instruct
(Qwen Research Licence).

## Fonts — SIL Open Font License 1.1

Archivo and JetBrains Mono, in `src/Otto.App/Assets/fonts`, with their licences beside
them. They are committed rather than fetched at startup because fetching them would break
the offline promise outright.
