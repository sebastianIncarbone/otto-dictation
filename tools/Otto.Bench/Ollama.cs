using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Otto.Bench;

/// <summary>
/// Minimal client for a local Ollama. Loopback only — this never leaves the
/// machine, which is why it does not break the offline promise.
/// </summary>
public sealed class Ollama(string endpoint = "http://localhost:11434", TimeSpan? timeout = null) : IDisposable
{
    private readonly HttpClient http = new()
    {
        BaseAddress = new Uri(endpoint),
        // Generous on purpose: the point of the benchmark is to find out how long
        // this actually takes, not to cut it off at the product's budget.
        Timeout = timeout ?? TimeSpan.FromSeconds(60),
    };

    public async Task<bool> IsAvailableAsync()
    {
        try
        {
            using var response = await http.GetAsync("/api/tags");
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<string>> ModelsAsync()
    {
        var tags = await http.GetFromJsonAsync<TagsResponse>("/api/tags");
        return tags?.Models?.Select(m => m.Name).ToArray() ?? [];
    }

    public async Task<string> GenerateAsync(string model, string prompt, CancellationToken cancellationToken = default)
    {
        var request = new
        {
            model,
            prompt,
            stream = false,
            // Deterministic: two runs of the benchmark have to be comparable, and a
            // dictation tool that rewrites the same sentence differently each time
            // would be unusable anyway.
            options = new { temperature = 0.0, num_predict = 512 },
        };

        using var response = await http.PostAsJsonAsync("/api/generate", request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<GenerateResponse>(cancellationToken);
        return body?.Response?.Trim() ?? "";
    }

    /// <summary>
    /// Chat rather than raw completion, so instructions can sit in the system role
    /// and worked examples can be shown as real turns. Both matter a lot for small
    /// models: they follow a demonstrated pattern far better than a described rule.
    /// </summary>
    public async Task<string> ChatAsync(
        string model,
        IReadOnlyList<(string Role, string Content)> messages,
        CancellationToken cancellationToken = default)
    {
        var request = new
        {
            model,
            messages = messages.Select(m => new { role = m.Role, content = m.Content }),
            stream = false,
            options = new { temperature = 0.0, num_predict = 512 },
        };

        using var response = await http.PostAsJsonAsync("/api/chat", request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<ChatResponse>(cancellationToken);
        return body?.Message?.Content?.Trim() ?? "";
    }

    private sealed record ChatResponse([property: JsonPropertyName("message")] ChatMessage? Message);
    private sealed record ChatMessage([property: JsonPropertyName("content")] string? Content);

    private sealed record TagsResponse([property: JsonPropertyName("models")] TagModel[]? Models);
    private sealed record TagModel([property: JsonPropertyName("name")] string Name);
    private sealed record GenerateResponse([property: JsonPropertyName("response")] string? Response);

    public void Dispose() => http.Dispose();
}
