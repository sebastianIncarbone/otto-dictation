using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Otto.Core;

namespace Otto.PostProcessing;

public sealed record PostProcessingOptions
{
    public string Endpoint { get; init; } = "http://localhost:11434";
    public string Model { get; init; } = "qwen2.5:3b";

    /// <summary>
    /// Hard ceiling on the correction. Past this the raw transcription goes in —
    /// a user waiting on their own words will not trade seconds for conjugations.
    /// </summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Separate from <see cref="Timeout"/>. The probe runs once at startup, where a
    /// couple of extra seconds cost nothing; the correction runs while someone
    /// waits for their own words. Reusing the hot-path budget here made Otto
    /// declare a perfectly healthy Ollama unavailable.
    /// </summary>
    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long Ollama keeps the model in VRAM after a request. The default is
    /// five minutes, which means someone who dictates every ten minutes pays the
    /// load on every single dictation and never sees a correction land inside the
    /// budget.
    /// </summary>
    public string KeepAlive { get; init; } = "2h";
}

/// <summary>
/// Corrects the transcription with a local model over HTTP.
///
/// <c>localhost</c> is not the cloud: the request never leaves the machine, so this
/// keeps the offline promise intact. And it is entirely optional — if nothing is
/// listening, Otto disables the feature at startup and dictation carries on with
/// Whisper's raw output.
/// </summary>
public sealed class OllamaPostProcessor : IPostProcessor, IDisposable
{
    private readonly HttpClient http;
    private readonly PostProcessingOptions options;
    private readonly ILogger<OllamaPostProcessor> log;

    public OllamaPostProcessor(PostProcessingOptions options, ILogger<OllamaPostProcessor> log)
    {
        this.options = options;
        this.log = log;

        // Generous at the client level; the hot path gets its own budget through a
        // cancellation token per request.
        http = new HttpClient
        {
            BaseAddress = new Uri(options.Endpoint),
            Timeout = options.ProbeTimeout,
        };
    }

    public bool IsAvailable { get; private set; }

    public async Task<bool> ProbeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var tags = await http.GetFromJsonAsync<TagsResponse>("/api/tags", cancellationToken);
            var models = tags?.Models?.Select(m => m.Name).ToArray() ?? [];

            IsAvailable = models.Contains(options.Model);

            if (IsAvailable)
            {
                await WarmUpAsync(cancellationToken);
                log.LogInformation("Post-processing active with {Model}", options.Model);
            }
            else if (models.Length > 0)
                log.LogWarning("Ollama responded but does not have {Model}. Run: ollama pull {Model}", options.Model, options.Model);
            else
                log.LogInformation("Ollama has no models; post-processing stays disabled");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Not having Ollama is a perfectly normal way to run Otto.
            IsAvailable = false;
            log.LogInformation("Ollama is not available; using the raw transcription");
        }

        return IsAvailable;
    }

    public async Task<string> ProcessAsync(
        string text,
        DictationContext context,
        CancellationToken cancellationToken = default)
    {
        if (!IsAvailable || string.IsNullOrWhiteSpace(text)) return text;

        // The budget belongs to this call, not to the client: past it the raw
        // transcription goes in rather than making the user wait longer.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(options.Timeout);

        try
        {
            var corrected = await ChatAsync(text, budget.Token);

            if (!EditGuard.IsSafe(text, corrected))
            {
                log.LogWarning(
                    "Correction discarded: it touched {Touched} words. Inserting the raw text",
                    EditGuard.WordsTouched(text, corrected));

                return text;
            }

            return corrected;
        }
        catch (TaskCanceledException)
        {
            log.LogWarning("Post-processing took longer than {Seconds:F0} s; inserting the raw text",
                options.Timeout.TotalSeconds);

            return text;
        }
        catch (Exception ex)
        {
            // Never let a correction cost someone their dictation.
            log.LogWarning(ex, "Post-processing failed; inserting the raw text");
            return text;
        }
    }

    /// <summary>
    /// Loads the model into VRAM before the first dictation needs it.
    ///
    /// Measured cold, the correction takes longer than its entire two-second
    /// budget and gets discarded every time — the feature looks broken while being
    /// perfectly healthy. Warm, it takes about a third of a second. Paying that
    /// cost at startup is the same trick the speech model needed for Vulkan.
    /// </summary>
    private async Task WarmUpAsync(CancellationToken cancellationToken)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            await ChatAsync("Hola.", cancellationToken);
            log.LogInformation("Correction model warmed up in {Seconds:F1} s", watch.Elapsed.TotalSeconds);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not warm up the correction model");
        }
    }

    private async Task<string> ChatAsync(string text, CancellationToken cancellationToken)
    {
        var messages = new List<object> { new { role = "system", content = VoseoPrompt.System } };

        foreach (var (input, output) in VoseoPrompt.Examples)
        {
            messages.Add(new { role = "user", content = input });
            messages.Add(new { role = "assistant", content = output });
        }

        messages.Add(new { role = "user", content = text });

        var request = new
        {
            model = options.Model,
            messages,
            stream = false,
            keep_alive = options.KeepAlive,
            // Temperature zero: the same dictation has to come out the same way
            // twice, or the tool becomes impossible to trust.
            options = new { temperature = 0.0, num_predict = 512 },
        };

        using var response = await http.PostAsJsonAsync("/api/chat", request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<ChatResponse>(cancellationToken);

        return body?.Message?.Content?.Trim() ?? text;
    }

    private sealed record TagsResponse([property: JsonPropertyName("models")] TagModel[]? Models);
    private sealed record TagModel([property: JsonPropertyName("name")] string Name);
    private sealed record ChatResponse([property: JsonPropertyName("message")] ChatMessage? Message);
    private sealed record ChatMessage([property: JsonPropertyName("content")] string? Content);

    public void Dispose() => http.Dispose();
}
