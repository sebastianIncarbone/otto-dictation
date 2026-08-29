using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Otto.Core;

namespace Otto.Tts;

/// <summary>
/// Reads text aloud with Piper, one child process per fragment.
///
/// <para>
/// Piper is a 2023 VITS model shipped as a ~110 MB ONNX file with one fixed voice per
/// file. It cannot clone a voice and its prosody is a generation behind what a 2026
/// model produces. What it has instead is speed and reach: measured at x4,6 real time on
/// the CPU, against x0,69 for Qwen3-TTS with a whole RTX 4060 behind it. A reading that
/// generates slower than it plays is not a lower tier of the same feature, it is a
/// broken one — it falls further behind the longer it reads — so the faster engine is not
/// a compromise here, it is the only candidate.
/// </para>
/// <para>
/// The reach matters as much as the speed. Piper runs on any CPU, which means read-aloud
/// is the first optional thing Otto has ever offered that does not quietly disappear on
/// machines without a GPU — where <c>Program.cs</c> wires <c>NullPostProcessor</c> and
/// the user gets nothing.
/// </para>
/// <para>
/// <b>Out of process, and knowingly so.</b> Piper is ONNX and ONNX Runtime has a perfectly
/// good .NET package, so an in-process integration is possible and would remove the
/// per-fragment launch cost that the spike measured as the dominant term — Piper climbed
/// from x3,09 to x4,6 on chunk size alone. It is not free, though: Piper phonemises with
/// espeak-ng, and ONNX Runtime supplies the neural network, not the phonemiser. That
/// route means a native espeak-ng binding plus the VITS pre- and post-processing written
/// by hand, against a measured x4,6 that already has four times the headroom the feature
/// needs. Shipping the subprocess first and moving in-process later behind this same port
/// is the deliberate order, not an oversight.
/// </para>
/// </summary>
public sealed class PiperSynthesizer(TtsOptions options, ILogger<PiperSynthesizer> log) : ISpeechSynthesizer
{
    /// <summary>
    /// Which voice to read with. Settable at runtime because the settings window can
    /// change it while Otto is running — see <see cref="TtsOptions"/> for why this is not
    /// on the options record. Reference assignment is atomic, so a change landing while a
    /// fragment renders takes effect on the next fragment rather than corrupting this one.
    /// </summary>
    public Voice Voice { get; set; } = Voices.Default;

    /// <summary>
    /// How that voice is sampled. Settable at runtime for the same reason as
    /// <see cref="Voice"/>, and with the same guarantee: a change lands on the next
    /// fragment, never halfway through the one being rendered.
    /// </summary>
    public PiperVoicing Voicing { get; set; } = PiperVoicing.Natural;

    /// <summary>
    /// Recomputed on every read, never cached.
    ///
    /// <para>
    /// A cached answer here would repeat the defect that made Ollama look permanently
    /// unavailable: the probe ran once at startup, so a model that arrived afterwards was
    /// never noticed and the only cure was restarting Otto. A voice can finish downloading
    /// at any moment, and this has to become true the instant it does.
    /// </para>
    /// </summary>
    public bool IsAvailable => File.Exists(options.ExecutablePath) && Voice.IsInstalled(options.VoicesDirectory);

    public async Task<SynthesizedSpeech> SpeakAsync(
        string text, string destinationPath, CancellationToken cancellationToken = default)
    {
        var voice = Voice;
        var voicing = Voicing;

        if (!File.Exists(options.ExecutablePath))
            throw new InvalidOperationException($"The reading engine is not installed at {options.ExecutablePath}.");

        if (!voice.IsInstalled(options.VoicesDirectory))
            throw new InvalidOperationException($"The voice {voice.Id} is not installed in {options.VoicesDirectory}.");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destinationPath))!);

        var info = new ProcessStartInfo(options.ExecutablePath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,

            // Load-bearing, and the nastiest failure in this whole file if it is wrong.
            // Piper resolves espeak-ng-data relative to the WORKING DIRECTORY, not to its
            // own location. Launched from anywhere else it starts, reports no error, exits
            // zero, and produces silence. There is no diagnostic anywhere pointing at the
            // cause — you get a valid WAV full of nothing.
            WorkingDirectory = options.EngineDirectory,

            // Hygiene rather than a fix. Without it the text goes out in the console's
            // codepage — cp1252 on the machine this was measured on — and every accented
            // character reaches espeak as a replacement byte.
            //
            // That was the obvious suspect for "it pronounces accented words weirdly" and
            // it is NOT the culprit: feeding espeak deliberately mangled cp1252 bytes and
            // dumping the phonemes with --debug produces the same output character for
            // character as clean UTF-8. espeak-ng recovers. So this buys correctness on
            // some future input it would not recover from, and nothing audible today. The
            // accents live in the acoustic model, which is what PiperVoicing reaches.
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };

        info.ArgumentList.Add("-m");
        info.ArgumentList.Add(voice.ModelPath(options.VoicesDirectory));
        info.ArgumentList.Add("-f");
        info.ArgumentList.Add(Path.GetFullPath(destinationPath));

        foreach (var argument in voicing.Arguments())
            info.ArgumentList.Add(argument);

        var diagnostics = new StringBuilder();

        using var process = new Process { StartInfo = info };

        void Capture(string? line)
        {
            if (line is null) return;

            lock (diagnostics) diagnostics.AppendLine(line);
        }

        process.OutputDataReceived += (_, e) => Capture(e.Data);
        process.ErrorDataReceived += (_, e) => Capture(e.Data);

        var clock = Stopwatch.StartNew();

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(options.RenderTimeout);

        // Piper treats newlines as utterance boundaries and, with -f, only the last
        // utterance reaches the file. The caller already flattens in Sentences.Split, but
        // this is the layer that knows the quirk, so it is enforced here too: a caller
        // that hands over a raw paragraph gets all of it read, not just its final line.
        await process.StandardInput.WriteLineAsync(text.ReplaceLineEndings(" "));
        process.StandardInput.Close();

        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            // WaitForExitAsync stops waiting; it does not stop the process. Without this
            // kill, stopping a long reading would leave one orphaned piper.exe per
            // fragment still rendering audio nobody will ever hear — on a tray app that
            // runs for weeks, that is a real leak and not a tidy-up.
            Kill(process);

            if (cancellationToken.IsCancellationRequested) throw;

            throw new TimeoutException(
                $"Piper took longer than {options.RenderTimeout.TotalSeconds:N0} s on a fragment and was stopped.");
        }

        clock.Stop();

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"piper exited with code {process.ExitCode}.{Environment.NewLine}{Diagnostics(diagnostics)}");

        // Exit code zero is not proof of audio — the missing-phoneme-data case above is
        // exactly a clean exit with nothing to show for it.
        if (!File.Exists(destinationPath) || new FileInfo(destinationPath).Length == 0)
            throw new InvalidOperationException(
                $"piper exited cleanly but left no audio at {destinationPath}. " +
                $"The usual cause is espeak-ng-data missing from {options.EngineDirectory}." +
                $"{Environment.NewLine}{Diagnostics(diagnostics)}");

        var duration = WavFile.Duration(destinationPath);
        var speech = new SynthesizedSpeech(destinationPath, duration, clock.Elapsed);

        log.LogDebug("Read {Characters} characters as {Duration:N1}s of audio in {Elapsed:N1}s (x{Factor:N2})",
            text.Length, duration.TotalSeconds, clock.Elapsed.TotalSeconds, speech.RealTimeFactor);

        return speech;
    }

    private void Kill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            // Racing a process that exited on its own between the check and the kill is
            // the expected case, not a problem worth surfacing: the goal — no orphan —
            // has been met either way.
            log.LogDebug(ex, "Could not kill piper; it had most likely already exited");
        }
    }

    private static string Diagnostics(StringBuilder diagnostics)
    {
        lock (diagnostics) return diagnostics.ToString();
    }
}
