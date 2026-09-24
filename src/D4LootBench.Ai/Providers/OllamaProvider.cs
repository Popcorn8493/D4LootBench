using System.Collections.Concurrent;
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

    /// <summary>Lookups (tags, model capabilities) are quick metadata calls — never minutes.</summary>
    private static readonly TimeSpan MetadataTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Whether a model thinks, per server + model; only successful lookups are cached.</summary>
    private static readonly ConcurrentDictionary<string, bool> ThinkingSupport = new(StringComparer.OrdinalIgnoreCase);

    private readonly HttpClient _http;
    private readonly Uri _baseUri;
    private readonly Uri _chatUri;
    private readonly string _model;

    public OllamaProvider(LlmSettings settings) : this(settings, SharedHttp) { }

    /// <summary>Test seam: supply the <see cref="HttpClient"/> (e.g. over a fake handler).</summary>
    internal OllamaProvider(LlmSettings settings, HttpClient http)
    {
        _model   = settings.ModelName;
        _http    = http;
        _baseUri = new Uri(settings.BaseUrl.TrimEnd('/') + "/");
        _chatUri = new Uri(_baseUri, "api/chat");
    }

    /// <summary>Per-request timeout (default <see cref="DefaultRequestTimeout"/>).</summary>
    public TimeSpan RequestTimeout { get; init; } = DefaultRequestTimeout;

    public async Task<LlmCompletion> GetCompletionAsync(
        string systemPrompt, string userPrompt, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object>
        {
            ["model"]    = _model,
            ["messages"] = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user",   content = userPrompt   }
            },
            ["format"] = new
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
            ["stream"]  = false,
            ["options"] = new { temperature = 0.1 },
        };
        // Thinking models (qwen3, deepseek-r1 …) reason before answering by default — several
        // times slower for no gain on a schema-constrained JSON task. "think" is only sent to
        // models that support it; older servers and other models may reject the field.
        try
        {
            if (await SupportsThinkingAsync(ct))
                body["think"] = false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return LlmCompletion.Fail("Request cancelled.");
        }

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

    /// <summary>
    /// A quick health check for the settings screen — server reachable, model installed, and
    /// whether it's a thinking model — using metadata calls only. It deliberately doesn't run a
    /// generation: loading a large model can take minutes, which reads as a hung test.
    /// </summary>
    public async Task<OllamaStatus> CheckAsync(CancellationToken ct = default)
    {
        IReadOnlyList<string> installed;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(MetadataTimeout);
            using var response = await _http.GetAsync(new Uri(_baseUri, "api/tags"), timeout.Token);
            if (!response.IsSuccessStatusCode)
                return new OllamaStatus(false, false, [], false, $"Ollama returned {(int)response.StatusCode}.");
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            installed = doc.RootElement.TryGetProperty("models", out var models)
                ? models.EnumerateArray()
                    .Select(m => m.TryGetProperty("name", out var n) ? n.GetString() : null)
                    .OfType<string>()
                    .ToList()
                : [];
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
        {
            return new OllamaStatus(false, false, [], false,
                $"Can't reach Ollama at {_baseUri.GetLeftPart(UriPartial.Authority)}: {ex.Message}");
        }

        bool modelInstalled = installed.Any(name => SameModel(name, _model));
        bool thinking = modelInstalled && await SupportsThinkingAsync(ct);
        return new OllamaStatus(true, modelInstalled, installed, thinking, null);
    }

    /// <summary>"qwen3.8" and "qwen3.8:latest" name the same model.</summary>
    private static bool SameModel(string installed, string configured) =>
        string.Equals(installed, configured, StringComparison.OrdinalIgnoreCase)
        || string.Equals(installed, configured + ":latest", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the configured model lists the "thinking" capability (POST /api/show).
    /// Unknown (server too old, lookup failed) reads as false and isn't cached.</summary>
    private async Task<bool> SupportsThinkingAsync(CancellationToken ct)
    {
        string key = _baseUri + "|" + _model;
        if (ThinkingSupport.TryGetValue(key, out bool known))
            return known;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(MetadataTimeout);
            using var response = await _http.PostAsJsonAsync(
                new Uri(_baseUri, "api/show"), new { model = _model }, timeout.Token);
            if (!response.IsSuccessStatusCode)
                return false;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            bool thinking = doc.RootElement.TryGetProperty("capabilities", out var caps)
                && caps.ValueKind == JsonValueKind.Array
                && caps.EnumerateArray().Any(c => c.GetString() == "thinking");
            ThinkingSupport[key] = thinking;
            return thinking;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
        {
            return false;
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

/// <summary>Result of <see cref="OllamaProvider.CheckAsync"/>.</summary>
public sealed record OllamaStatus(
    bool Reachable, bool ModelInstalled, IReadOnlyList<string> InstalledModels, bool IsThinkingModel, string? Error);
