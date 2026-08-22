using System.Diagnostics;
using System.Text;
using LLama;
using LLama.Common;
using LLama.Native;
using Microsoft.Extensions.Logging;

namespace Otto.PostProcessing;

/// <summary>
/// <see cref="ICorrectionEngine"/> over LLamaSharp — the only class in
/// Otto.PostProcessing that touches LLamaSharp types, so <see cref="LlamaPostProcessor"/>
/// stays testable without a GGUF or a GPU. Modeled on the spike in
/// <c>tools/Otto.Bench/LlamaCorrector.cs</c>, which validated that this coexists in
/// the same process as Whisper's own Vulkan runtime.
///
/// Uses <see cref="StatelessExecutor"/>, not LLamaSharp's <c>ChatSession</c> +
/// <c>InteractiveExecutor</c> pairing the first cut of this file used — see
/// <see cref="ChatMlPrompt"/>'s own doc comment for the two independent bugs that
/// combination had (wrong prompt template, and conversation state silently shared
/// across unrelated dictations). <see cref="StatelessExecutor"/> "infers the input
/// as one-time job" by design: a fresh <c>LLamaContext</c> per call, so every
/// correction is genuinely independent of every other one, matching what
/// <see cref="ICorrectionEngine.ChatAsync"/>'s own contract already promised.
/// </summary>
public sealed class LlamaEngine : ICorrectionEngine, IDisposable
{
    private readonly PostProcessingOptions options;
    private readonly ILogger<LlamaEngine> log;

    // See LoadGenerationTracker's own doc comment for the race it exists to
    // close: this is a DI singleton, LoadAsync is retryable after a failed or
    // ProbeTimeout-canceled attempt (LlamaPostProcessor.loadSucceeded), and
    // CancelableWork cannot truly cancel the blocking native load — so an
    // orphaned attempt from a PREVIOUS LoadAsync call can still finish and try
    // to write these fields AFTER a retry has already published its own.
    private readonly LoadGenerationTracker generation = new();

    // See InFlightGate's own doc comment for the SEPARATE race it exists to
    // close: ChatAsync (and WarmUpAsync, which drives the same ChatAsync) has
    // no synchronization against Dispose of its own — without this, Dispose
    // could free weights while a ChatAsync call on another thread was still
    // running its native inference through them, which is a native access
    // violation rather than a managed exception. generation only protects
    // LoadAsync-vs-Dispose; it has nothing to do with ChatAsync.
    private readonly InFlightGate inFlight = new();

    // Bound for Dispose's wait on an in-flight ChatAsync call. Anchored to
    // PostProcessingOptions.Timeout's own 2 s default: a normal correction is
    // already bounded by that budget on the CALLER's side (LlamaPostProcessor.
    // ProcessAsync), so a short wait here gives it room to unwind on its own
    // rather than being force-freed underneath it. WarmUpAsync has no
    // comparably tight budget of its own — it shares ProbeAsync's much longer
    // ProbeTimeout — so a slow warm-up simply exceeds this wait and is left
    // leaked rather than disposed live; see InFlightGate's own doc comment for
    // why that trade is the correct one on this shutdown-only path. Never
    // unbounded: "Salir" must not hang on a hung native call.
    private static readonly TimeSpan DisposeWaitTimeout = TimeSpan.FromSeconds(3);

    private LLamaWeights? weights;
    private StatelessExecutor? executor;

    public LlamaEngine(PostProcessingOptions options, ILogger<LlamaEngine> log)
    {
        this.options = options;
        this.log = log;
    }

    /// <summary>
    /// Forces Vulkan and disables the automatic CPU fallback. Must run after
    /// Whisper's own <c>RuntimeOptions.RuntimeLibraryOrder</c> is set — a real
    /// conflict between the two engines' native libraries has to throw here
    /// instead of silently landing on CPU, where a 3B model blows the whole
    /// two-second budget on every dictation while reporting healthy.
    ///
    /// Run through <see cref="CancelableWork"/> rather than inline: none of this
    /// is awaitable or cooperatively cancellable — <c>LLamaWeights.LoadFromFile</c>
    /// is a blocking native call with no token of its own — so without it,
    /// <paramref name="cancellationToken"/> was accepted but silently ignored, and
    /// <see cref="LlamaPostProcessor.ProbeAsync"/>'s <c>ProbeTimeout</c> could
    /// never actually cancel a hung load.
    ///
    /// <see cref="CancelableWork"/> only bounds the CALLER's wait — the worker
    /// keeps loading in the background even after this method's task completes.
    /// A retried call therefore claims its own generation via
    /// <see cref="generation"/> before starting, and once the blocking load
    /// finishes, publishes <see cref="weights"/>/<see cref="executor"/> only if
    /// its generation is still current — an orphaned attempt that finishes
    /// after being superseded disposes what it just built instead of leaking
    /// it or racing the retry's own writes.
    ///
    /// Builds a <see cref="StatelessExecutor"/>, not a <c>LLamaContext</c> of our
    /// own — the executor creates a fresh one internally on every
    /// <c>InferAsync</c> call, which is exactly the "every correction is
    /// independent" behaviour <see cref="ChatAsync"/> needs (see this class's own
    /// doc comment). Nothing here needs to hold a context open between calls.
    /// </summary>
    public Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var attempt = generation.ClaimGeneration();

        return CancelableWork.Run(() =>
        {
            // NativeLibraryConfig freezes itself the instant the native library
            // actually loads — LibraryHasLoaded flips true inside
            // LLamaWeights.LoadFromFile below, and every With*() call after that
            // throws InvalidOperationException (confirmed by decompiling
            // LLamaSharp 0.27.0 with ilspycmd: NativeLibraryConfig.ThrowIfLoaded()
            // is unconditional and undocumented in the package's own XML docs).
            // Otto itself only ever builds one LlamaEngine per process, so this
            // never fired in production — but tools/Otto.Bench's `voseo --models
            // a,b,c` comparison constructs a fresh LlamaEngine per candidate IN
            // THE SAME PROCESS. Without this guard, every candidate after the
            // first threw here, was swallowed by the harness's own catch, and was
            // reported as "no se pudo cargar" — indistinguishable from a missing
            // or corrupt GGUF, which silently turned a multi-model comparison
            // into a single-model one. NativeLibraryConfig.All applies to both
            // the LLama and Mtmd sub-configs, but only the LLama one is ever
            // loaded here (Mtmd is multimodal, unused by this engine), so
            // checking NativeLibraryConfig.LLama specifically is the right gate.
            if (!NativeLibraryConfig.LLama.LibraryHasLoaded)
                NativeLibraryConfig.All.WithVulkan(true).WithAutoFallback(false).WithLogCallback(log);

            var parameters = new ModelParams(options.ModelPath)
            {
                ContextSize = (uint)options.ContextSize,
                GpuLayerCount = -1,
            };

            var watch = Stopwatch.StartNew();

            var loadedWeights = LLamaWeights.LoadFromFile(parameters);
            var loadedExecutor = new StatelessExecutor(loadedWeights, parameters, log);

            log.LogInformation("Correction model weights loaded in {Seconds:F1} s", watch.Elapsed.TotalSeconds);

            var published = generation.TryPublish(
                attempt,
                publish: () =>
                {
                    // Defensive, not expected in practice: LlamaPostProcessor
                    // never calls LoadAsync again after a success. If it ever
                    // did, the previously published weights get disposed here
                    // rather than overwritten and leaked.
                    weights?.Dispose();
                    weights = loadedWeights;
                    executor = loadedExecutor;
                },
                discard: () =>
                {
                    loadedWeights.Dispose();
                });

            if (!published)
            {
                log.LogInformation(
                    "Discarding a correction model load superseded by a later attempt or by Dispose");
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Registers with <see cref="inFlight"/> before touching <see cref="executor"/>
    /// so a concurrent <see cref="Dispose"/> either waits for this call to finish
    /// (see <see cref="InFlightGate"/>'s own doc comment) or, if it already gave
    /// up and disposed, this call fails fast with a managed
    /// <see cref="ObjectDisposedException"/> instead of running inference
    /// against native handles that may already be freed. Callers — ProcessAsync
    /// and WarmUpAsync — already catch <see cref="Exception"/> broadly and
    /// degrade to the raw transcription, which is exactly what a disposed
    /// engine needs to look like here.
    /// </summary>
    public async Task<string> ChatAsync(
        IReadOnlyList<(string Role, string Content)> messages,
        CancellationToken cancellationToken = default)
    {
        if (!inFlight.TryEnter())
            throw new ObjectDisposedException(nameof(LlamaEngine), "Correction engine was disposed.");

        try
        {
            if (executor is null) throw new InvalidOperationException("Call LoadAsync first.");

            var prompt = ChatMlPrompt.Build(messages);

            var inferenceParams = new InferenceParams
            {
                MaxTokens = options.MaxTokens,

                // Primary stop mechanism is llama.cpp's own end-of-generation
                // check on the sampled token, which StatelessExecutor.InferAsync
                // runs BEFORE decoding it to text — <|im_end|> stops generation
                // without ever reaching the antiprompt processor at all when the
                // model behaves. These two are the belt-and-braces fallback for
                // when it does not: DecodeSpecialTokens stays true (rather than
                // LLamaSharp's own default of false) specifically so that if the
                // model rambles past its turn, the literal marker text is visible
                // to AntiPrompts instead of being silently swallowed — the one
                // place suppressing special-token text would work against us
                // instead of for us. ChatMlPrompt.Sanitize below is what trims
                // the marker back out of whatever this fallback catches.
                AntiPrompts = ["<|im_end|>", "<|im_start|>"],
                DecodeSpecialTokens = true,
            };

            var builder = new StringBuilder();

            await foreach (var token in executor.InferAsync(prompt, inferenceParams, cancellationToken))
                builder.Append(token);

            return ChatMlPrompt.Sanitize(builder.ToString());
        }
        finally
        {
            inFlight.Exit();
        }
    }

    /// <summary>
    /// Waits, bounded by <see cref="DisposeWaitTimeout"/>, for any
    /// <see cref="ChatAsync"/> call in flight — a real correction, or
    /// <see cref="LlamaPostProcessor.ProbeAsync"/>'s warm-up, which drives the
    /// same <c>ChatAsync</c> — before freeing anything; see
    /// <see cref="InFlightGate"/>'s own doc comment for why that has to be a
    /// bounded wait rather than an unbounded one, and why giving up means
    /// "leave it leaked," never "free it live." <see cref="inFlight"/> refuses
    /// every new <c>ChatAsync</c> call the instant this method starts, so the
    /// wait can only shrink from here.
    ///
    /// Only reaches <see cref="generation"/>'s own Load-vs-Dispose handling
    /// (unchanged by this fix — see its own doc comment) once that wait
    /// succeeds: the currently published handles are disposed, and
    /// <see cref="generation"/> is marked disposed under its own lock, so an
    /// in-flight <see cref="LoadAsync"/> attempt's eventual <c>TryPublish</c>
    /// call is guaranteed to discard (and dispose) what it built instead of
    /// racing this disposal or resurrecting fields it just cleared. If the
    /// <see cref="inFlight"/> wait times out instead, <see cref="generation"/>
    /// is never reached at all — one more leaked allocation on a process that
    /// is already exiting, not a crash.
    ///
    /// Idempotent via <see cref="InFlightGate"/>'s own idempotency: a second
    /// call is a no-op.
    /// </summary>
    public void Dispose()
    {
        var freed = inFlight.TryDispose(DisposeWaitTimeout, () =>
            generation.Dispose(disposeCurrent: () =>
            {
                // StatelessExecutor owns no long-lived context of its own to
                // dispose here, so weights is the only native handle
                // LlamaEngine itself is responsible for.
                //
                // Its public Context property makes it look otherwise, which is
                // why this is spelled out rather than assumed: decompiling
                // LLamaSharp 0.27.0 shows the constructor creates a context and
                // disposes it immediately, and InferAsync wraps each call's
                // context in `using` (disposing any still-open one first). The
                // property only ever exposes an already-disposed handle between
                // calls. Disposing it from here would be a double free.
                weights?.Dispose();
                weights = null;
                executor = null;
            }));

        if (!freed)
        {
            log.LogWarning(
                "Correction model still in use after waiting {Seconds:F0}s to shut down; " +
                "leaving its native handles for the process to reclaim on exit instead of freeing them under a live call",
                DisposeWaitTimeout.TotalSeconds);
        }
    }
}
