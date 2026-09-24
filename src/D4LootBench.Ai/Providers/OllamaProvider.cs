using System.Net.Http.Json;
using System.Text.Json;

namespace D4LootBench.Ai.Providers;

/// <summary>
/// Calls an Ollama instance via its native <c>/api/chat</c> endpoint with a JSON-Schema
/// <c>format</c> (Ollama 0.5+ structured outputs: grammar-constrained generation enforces the
/// top-level shape). The OpenAI-compatible <c>/v1/chat/completions</c> endpoint silently ignores
/// Ollama's <c>format</c> field, which is why this provider talks to the native API.
/// </summary>
/// <remarks>
/// All instances share one static <see cref="HttpClient"/>, so creating a provider per call
/// (as the app's settings-aware wrapper does) is cheap and never exhausts sockets. The client
/// itself has no timeout; each request gets its own <see cref="RequestTimeout"/> so a timeout
/// can be told apart from the caller cancelling.
/// </remarks>
public sealed class OllamaProvider : ILlmProvider, IDisposable
{
    /// <summary>Generous default: CPU-only inference of a 14B model can take minutes.</summary>
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromMinutes(5);

    private static readonly HttpClient SharedHttp = new() { Timeout = Timeout.InfiniteTimeSpan };

    private readonly HttpClient _http;
    private readonly Uri _chatUri;
    private readonly string _model;

    public OllamaProvider(LlmSettings settings) : this(settings, SharedHttp) { }

    /// <summary>Test seam: supply the <see cref="HttpClient"/> (e.g. over a fake handler).</summary>
    internal OllamaProvider(LlmSettings settings, HttpClient http)
    {
        _model   = settings.ModelName;
        _http    = http;
        _chatUri = new Uri(new Uri(settings.BaseUrl.TrimEnd('/') + "/"), "api/chat");
    }

    /// <summary>Per-request timeout (default <see cref="DefaultRequestTimeout"/>).</summary>
    public TimeSpan RequestTimeout { get; init; } = DefaultRequestTimeout;

    public async Task<LlmCompletion> GetCompletionAsync(
        string systemPrompt, string userPrompt, CancellationToken ct = default)
    {
        var body = new
        {
            model    = _model,
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user",   content = userPrompt   }
            },
            format = new
            {
                type       = "object",
                required   = new[] { "name", "visibility", "conditions" },
                properties = new
                {
                    name       = new { type = "string" },
                    visibility = new { type = "string", @enum = new[] { "Show", "Recolor", "HideAll" } },
                    conditions = new { type = "array", items = new { type = "object" } }
                }
            },
            stream  = false,
            options = new { temperature = 0.1 }
        };

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(RequestTimeout);

        string raw;
        try
        {
            using var response = await _http.PostAsJsonAsync(_chatUri, body, timeoutCts.Token);
            raw = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            if (!response.IsSuccessStatusCode)
                return LlmCompletion.Fail($"Ollama returned {(int)response.StatusCode}: {raw}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return LlmCompletion.Fail("Request cancelled.");
        }
        catch (OperationCanceledException)
        {
            return LlmCompletion.Fail(
                $"Ollama request timed out after {RequestTimeout.TotalMinutes:0.#} min " +
                $"(model '{_model}' may still be loading or too slow on this hardware).");
        }
        catch (HttpRequestException ex)
        {
            return LlmCompletion.Fail($"Ollama unreachable at {_chatUri.GetLeftPart(UriPartial.Authority)}: {ex.Message}");
        }

        try
        {
            using var doc = JsonDocument.Parse(raw);
            var content = doc.RootElement
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? "";

            return LlmCompletion.Ok(StripFences(content));
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return LlmCompletion.Fail($"Unexpected response shape: {ex.Message}\n{raw}");
        }
    }

    /// <summary>Strips markdown code fences some models add even in JSON mode.</summary>
    internal static string StripFences(string content)
    {
        content = content.Trim();
        if (!content.StartsWith("```")) return content;
        var start = content.IndexOf('\n') + 1;
        var end   = content.LastIndexOf("```", StringComparison.Ordinal);
        return start > 0 && end > start ? content[start..end].Trim() : content;
    }

    /// <summary>No-op: the shared <see cref="HttpClient"/> lives for the process.</summary>
    public void Dispose() { }
}
