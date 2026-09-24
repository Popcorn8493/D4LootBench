using System.Net;
using System.Text;
using System.Text.Json;
using D4LootBench.Ai.Providers;
using Shouldly;

namespace D4LootBench.Ai.Tests;

public sealed class OllamaProviderTests
{
    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return await respond(request, ct);
        }
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static LlmSettings SettingsFor(string model) => new()
    {
        Provider = LlmProviderType.Ollama, BaseUrl = "http://localhost:11434/", ModelName = model,
    };

    private static readonly LlmSettings Settings = new()
    {
        Provider = LlmProviderType.Ollama, BaseUrl = "http://localhost:11434/", ModelName = "test-model",
    };

    [Fact]
    public async Task PostsNativeChatRequest_WithSchemaFormatAndOptions()
    {
        var handler = new FakeHandler((_, _) => Task.FromResult(
            Json("""{"model":"test-model","message":{"role":"assistant","content":"{\"name\":\"x\"}"},"done":true}""")));
        var provider = new OllamaProvider(Settings, new HttpClient(handler));

        var completion = await provider.GetCompletionAsync("sys", "user", TestContext.Current.CancellationToken);

        completion.IsSuccess.ShouldBeTrue(completion.Error);
        completion.Content.ShouldBe("""{"name":"x"}""");

        handler.Request!.RequestUri!.ToString().ShouldBe("http://localhost:11434/api/chat");
        using var body = JsonDocument.Parse(handler.Body!);
        var root = body.RootElement;
        root.GetProperty("model").GetString().ShouldBe("test-model");
        root.GetProperty("stream").GetBoolean().ShouldBeFalse();
        root.GetProperty("format").GetProperty("type").GetString().ShouldBe("object");
        root.GetProperty("options").GetProperty("temperature").GetDouble().ShouldBe(0.1);
        root.TryGetProperty("temperature", out _).ShouldBeFalse();
        root.GetProperty("messages").GetArrayLength().ShouldBe(2);
    }

    [Fact]
    public async Task StripsMarkdownFences()
    {
        var handler = new FakeHandler((_, _) => Task.FromResult(
            Json("""{"message":{"content":"```json\n{\"a\":1}\n```"}}""")));
        var completion = await new OllamaProvider(Settings, new HttpClient(handler))
            .GetCompletionAsync("s", "u", TestContext.Current.CancellationToken);

        completion.Content.ShouldBe("""{"a":1}""");
    }

    [Fact]
    public async Task NonSuccessStatus_Fails()
    {
        var handler = new FakeHandler((_, _) => Task.FromResult(
            Json("""{"error":"model 'nope' not found"}""", HttpStatusCode.NotFound)));
        var completion = await new OllamaProvider(Settings, new HttpClient(handler))
            .GetCompletionAsync("s", "u", TestContext.Current.CancellationToken);

        completion.IsSuccess.ShouldBeFalse();
        completion.Error.ShouldNotBeNull().ShouldContain("404");
        completion.Error.ShouldContain("not found");
    }

    [Fact]
    public async Task UnexpectedShape_Fails()
    {
        var handler = new FakeHandler((_, _) => Task.FromResult(Json("""{"choices":[]}""")));
        var completion = await new OllamaProvider(Settings, new HttpClient(handler))
            .GetCompletionAsync("s", "u", TestContext.Current.CancellationToken);

        completion.Error.ShouldNotBeNull().ShouldContain("Unexpected response shape");
    }

    [Fact]
    public async Task ConnectionFailure_ReportsUnreachable()
    {
        var handler = new FakeHandler((_, _) => throw new HttpRequestException("No connection could be made"));
        var completion = await new OllamaProvider(Settings, new HttpClient(handler))
            .GetCompletionAsync("s", "u", TestContext.Current.CancellationToken);

        completion.Error.ShouldNotBeNull().ShouldContain("unreachable");
    }

    [Fact]
    public async Task SlowResponse_ReportsTimedOut_NotUnreachable()
    {
        var handler = new FakeHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Json("{}");
        });
        var provider = new OllamaProvider(Settings, new HttpClient(handler))
        {
            RequestTimeout = TimeSpan.FromMilliseconds(50),
        };

        var completion = await provider.GetCompletionAsync("s", "u", TestContext.Current.CancellationToken);

        completion.Error.ShouldNotBeNull().ShouldContain("timed out");
        completion.Error.ShouldNotContain("unreachable");
    }

    [Fact]
    public async Task CallerCancellation_ReportsCancelled()
    {
        var handler = new FakeHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Json("{}");
        });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var completion = await new OllamaProvider(Settings, new HttpClient(handler))
            .GetCompletionAsync("s", "u", cts.Token);

        completion.Error.ShouldBe("Request cancelled.");
    }

    [Fact]
    public void DefaultTimeout_IsGenerousForCpuInference()
    {
        new OllamaProvider(Settings).RequestTimeout.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMinutes(5));
    }

    /// <summary>Routes by path: /api/show answers with the given capabilities, /api/tags with
    /// the given installed models, /api/chat with a valid rule.</summary>
    private static FakeHandler Server(string[] capabilities, string[] installed, List<string> chatBodies) =>
        new(async (request, ct) =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/api/show"))
                return Json(JsonSerializer.Serialize(new { capabilities }));
            if (path.EndsWith("/api/tags"))
                return Json(JsonSerializer.Serialize(new { models = installed.Select(n => new { name = n }) }));
            chatBodies.Add(await request.Content!.ReadAsStringAsync(ct));
            return Json("""{"message":{"content":"{\"name\":\"x\"}"}}""");
        });

    [Fact]
    public async Task Thinking_models_get_think_false_and_others_omit_the_field()
    {
        var chats = new List<string>();
        var thinking = new OllamaProvider(SettingsFor("thinker:27b"),
            new HttpClient(Server(["completion", "thinking"], ["thinker:27b"], chats)));
        var plain = new OllamaProvider(SettingsFor("plain:14b"),
            new HttpClient(Server(["completion"], ["plain:14b"], chats)));

        (await thinking.GetCompletionAsync("sys", "user", TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();
        (await plain.GetCompletionAsync("sys", "user", TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();

        using (var first = JsonDocument.Parse(chats[0]))
            first.RootElement.GetProperty("think").GetBoolean().ShouldBeFalse();
        using (var second = JsonDocument.Parse(chats[1]))
            second.RootElement.TryGetProperty("think", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Check_reports_missing_model_and_installed_alternatives()
    {
        var provider = new OllamaProvider(SettingsFor("qwen2.5-coder:14b"),
            new HttpClient(Server(["completion", "thinking"], ["qwen3.8:latest"], [])));

        var status = await provider.CheckAsync(TestContext.Current.CancellationToken);

        status.Reachable.ShouldBeTrue();
        status.ModelInstalled.ShouldBeFalse();
        status.InstalledModels.ShouldBe(["qwen3.8:latest"]);
    }

    [Fact]
    public async Task Check_matches_the_latest_tag_and_reports_thinking()
    {
        var provider = new OllamaProvider(SettingsFor("qwen3.8"),
            new HttpClient(Server(["completion", "thinking"], ["qwen3.8:latest"], [])));

        var status = await provider.CheckAsync(TestContext.Current.CancellationToken);

        status.ModelInstalled.ShouldBeTrue();
        status.IsThinkingModel.ShouldBeTrue();
    }

    [Fact]
    public async Task Check_reports_an_unreachable_server()
    {
        var provider = new OllamaProvider(Settings, new HttpClient(new FakeHandler((_, _) =>
            throw new HttpRequestException("No connection could be made"))));

        var status = await provider.CheckAsync(TestContext.Current.CancellationToken);

        status.Reachable.ShouldBeFalse();
        status.Error.ShouldNotBeNull().ShouldContain("Can't reach Ollama");
    }
}
