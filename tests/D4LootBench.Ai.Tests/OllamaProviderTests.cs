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
}
