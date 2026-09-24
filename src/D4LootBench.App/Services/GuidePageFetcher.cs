using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;

namespace D4LootBench.App.Services;

/// <summary>
/// Fetches guide pages for the build importers (loot filter and paragon alike).
/// Mobalytics sits behind Cloudflare, which 403s HttpClient by TLS fingerprint but lets the
/// in-box Windows curl.exe through — prefer it, and fall back to HttpClient without it.
/// Both paths are bounded: an HTTP error status fails with that status in the message, a page
/// larger than <see cref="MaxResponseBytes"/> is rejected, a request is capped at
/// <see cref="TimeoutSeconds"/>, and cancelling kills the curl process.
/// </summary>
public static class GuidePageFetcher
{
    public const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36";

    /// <summary>Guide pages are a few MB at most; anything past this is not a build page.</summary>
    public const int MaxResponseBytes = 20 * 1024 * 1024;

    private const int TimeoutSeconds = 20;

    /// <summary>Appended to curl's stdout via --write-out; works on every curl version (older
    /// in-box builds lack %{stderr}), and the marker can't plausibly occur in page content.</summary>
    internal const string StatusMarker = "\n<<D4LB_HTTP_STATUS:";

    public static async Task<string> FetchPageAsync(string url, CancellationToken cancellationToken = default)
    {
        string curl = Path.Combine(Environment.SystemDirectory, "curl.exe");
        if (File.Exists(curl))
        {
            var (exitCode, status, body) = await RunCurlAsync(curl, url, cancellationToken);
            if (status >= 400)
                throw HttpError(status, url);
            if (exitCode == 0 && status is >= 200 and < 400 && body.Length > 0)
                return body;
            // No status or an aborted transfer (DNS/TLS failure, curl timeout, --max-filesize —
            // which may leave a partial body): try HttpClient.
        }

        using var response = await Http.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw HttpError((int)response.StatusCode, url);
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private static HttpRequestException HttpError(int status, string url)
    {
        string host = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;
        string reason = Enum.IsDefined(typeof(HttpStatusCode), status) ? $" {(HttpStatusCode)status}" : "";
        return new HttpRequestException($"{host} answered HTTP {status}{reason}", null, (HttpStatusCode)status);
    }

    /// <summary>curl's exit code, the HTTP status (0 when curl never got one), and the body
    /// with the status marker removed.</summary>
    private static async Task<(int ExitCode, int Status, string Body)> RunCurlAsync(
        string curl, string url, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo(curl)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var arg in new[]
                 {
                     "-sL", "--compressed",
                     "--max-time", TimeoutSeconds.ToString(),
                     "--max-filesize", MaxResponseBytes.ToString(),
                     "-H", $"User-Agent: {BrowserUserAgent}",
                     // curl expands the "\n" escape itself; the output then matches StatusMarker.
                     "-w", @"\n" + StatusMarker[1..] + "%{http_code}>>",
                     url,
                 })
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("curl.exe failed to start.");
        await using var kill = cancellationToken.Register(() => TryKill(process));
        // Drain stderr so a chatty failure can't block the process on a full pipe.
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            string output = await ReadBoundedAsync(process.StandardOutput, MaxResponseBytes, cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            await stderr;
            var (status, body) = ParseCurlOutput(output);
            return (process.ExitCode, status, body);
        }
        catch (InvalidDataException)
        {
            TryKill(process);
            throw new HttpRequestException(
                $"The page is larger than {MaxResponseBytes / (1024 * 1024)} MB — not a build guide page?");
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
    }

    /// <summary>Reads to the end, throwing <see cref="InvalidDataException"/> past the cap
    /// (curl's --max-filesize misses chunked responses without a Content-Length).</summary>
    private static async Task<string> ReadBoundedAsync(
        StreamReader reader, int maxChars, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        var buffer = new char[16 * 1024];
        int read;
        while ((read = await reader.ReadAsync(buffer, cancellationToken)) > 0)
        {
            // The marker suffix is tiny; allow for it on top of the page cap.
            if (text.Length + read > maxChars + 64)
                throw new InvalidDataException();
            text.Append(buffer, 0, read);
        }
        return text.ToString();
    }

    /// <summary>
    /// Splits curl's stdout into the body and the --write-out status suffix. A missing marker
    /// or unparseable status reads as 0 ("no HTTP status"), like curl's own "000".
    /// </summary>
    internal static (int Status, string Body) ParseCurlOutput(string output)
    {
        int at = output.LastIndexOf(StatusMarker, StringComparison.Ordinal);
        if (at < 0)
            return (0, output);
        string suffix = output[(at + StatusMarker.Length)..].TrimEnd();
        if (suffix.EndsWith(">>", StringComparison.Ordinal))
            suffix = suffix[..^2];
        int status = int.TryParse(suffix, out int parsed) ? parsed : 0;
        return (status, output[..at]);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }
    }

    private static readonly HttpClient Http = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
        })
        {
            Timeout = TimeSpan.FromSeconds(TimeoutSeconds),
            MaxResponseContentBufferSize = MaxResponseBytes,
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher,
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(BrowserUserAgent);
        return client;
    }
}
