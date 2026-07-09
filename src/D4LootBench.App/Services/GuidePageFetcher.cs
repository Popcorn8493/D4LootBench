using System.IO;
using System.Net.Http;

namespace D4LootBench.App.Services;

/// <summary>
/// Fetches guide pages for the build importers (loot filter and paragon alike).
/// Mobalytics sits behind Cloudflare, which 403s HttpClient by TLS fingerprint but lets the
/// in-box Windows curl.exe through — prefer it, and fall back to HttpClient without it.
/// </summary>
public static class GuidePageFetcher
{
    public const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36";

    public static async Task<string> FetchPageAsync(string url)
    {
        string curl = Path.Combine(Environment.SystemDirectory, "curl.exe");
        if (File.Exists(curl))
        {
            var psi = new System.Diagnostics.ProcessStartInfo(curl)
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
            };
            foreach (var arg in new[] { "-sL", "--compressed", "--max-time", "20", "-H", $"User-Agent: {BrowserUserAgent}", url })
                psi.ArgumentList.Add(arg);
            using var process = System.Diagnostics.Process.Start(psi)
                ?? throw new InvalidOperationException("curl.exe failed to start.");
            string output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode == 0 && output.Length > 0)
                return output;
        }
        return await Http.GetStringAsync(url);
    }

    private static readonly HttpClient Http = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        })
        {
            Timeout = TimeSpan.FromSeconds(20),
            DefaultRequestVersion = System.Net.HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher,
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(BrowserUserAgent);
        return client;
    }
}
