using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace D4LootBench.App.Views;

/// <summary>
/// Fetches a page through an embedded Edge (WebView2) browser when a site answers plain HTTP
/// requests with a Cloudflare JavaScript challenge ("Just a moment…"). The challenge passes as
/// in a normal browser (or with one user click); the page's RAW source is then re-fetched from
/// inside the page with the clearance cookie — the same HTML "view source" shows, which is
/// what the importers parse — and the window closes itself.
/// </summary>
public partial class BrowserFetchWindow : Window
{
    private const string ErrorPrefix = "__D4LB_FETCH_ERROR__";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);

    private readonly string _url;
    private readonly TaskCompletionSource<string> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly DispatcherTimer _timeout;
    private bool _sourceRequested;

    private BrowserFetchWindow(string url)
    {
        _url = url;
        InitializeComponent();
        Title = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? $"Loading {uri.Host}…" : "Loading guide page…";
        _timeout = new DispatcherTimer { Interval = Timeout };
        _timeout.Tick += (_, _) => Fail(new TimeoutException(
            $"The page didn't finish loading within {Timeout.TotalSeconds:0} seconds."));
        Loaded += async (_, _) => await StartAsync();
        Closed += (_, _) =>
        {
            _timeout.Stop();
            _result.TrySetCanceled();
        };
    }

    /// <summary>
    /// Shows the browser window (owned by <paramref name="owner"/>) and returns the page's raw
    /// HTML. Throws <see cref="OperationCanceledException"/> when the user cancels or closes it,
    /// and <see cref="HttpRequestException"/> when the embedded browser isn't available.
    /// </summary>
    public static async Task<string> FetchAsync(string url, Window? owner, CancellationToken cancellationToken)
    {
        var window = new BrowserFetchWindow(url);
        if (owner is { IsLoaded: true })
            window.Owner = owner;
        using var registration = cancellationToken.Register(() => window.Dispatcher.BeginInvoke(window.Close));
        window.Show();
        try
        {
            return await window._result.Task;
        }
        finally
        {
            if (window.IsLoaded)
                window.Close();
        }
    }

    private async Task StartAsync()
    {
        try
        {
            // The default profile folder sits next to the exe, which isn't writable under
            // Program Files — keep it with the app's other per-user data.
            string profile = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "D4LootBench", "WebView2");
            var environment = await CoreWebView2Environment.CreateAsync(null, profile);
            await Browser.EnsureCoreWebView2Async(environment);
        }
        catch (WebView2RuntimeNotFoundException ex)
        {
            Fail(new HttpRequestException(
                "The embedded browser (Microsoft Edge WebView2 runtime) isn't available: " + ex.Message, ex));
            return;
        }
        catch (Exception ex)
        {
            Fail(new HttpRequestException("The embedded browser couldn't start: " + ex.Message, ex));
            return;
        }

        var core = Browser.CoreWebView2;
        core.NavigationCompleted += OnNavigationCompleted;
        core.WebMessageReceived += OnWebMessage;
        _timeout.Start();
        core.Navigate(_url);
    }

    private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        try
        {
            if (_sourceRequested || _result.Task.IsCompleted)
                return;
            string title = JsonSerializer.Deserialize<string>(
                await Browser.CoreWebView2.ExecuteScriptAsync("document.title")) ?? "";
            if (IsChallenge(title))
            {
                StatusText.Text = "Waiting for the site's browser check — click the checkbox if one appears.";
                return; // the challenge navigates again once it passes
            }
            _sourceRequested = true;
            StatusText.Text = "Page loaded — reading it…";
            // Re-fetch the page from inside it: the clearance cookie now rides along, and the
            // response is the raw server HTML (what the importers parse), not the live DOM.
            await Browser.CoreWebView2.ExecuteScriptAsync($$"""
                fetch(location.href, { credentials: 'include' })
                    .then(r => r.text())
                    .then(t => chrome.webview.postMessage(t))
                    .catch(e => chrome.webview.postMessage('{{ErrorPrefix}}' + e));
                """);
        }
        catch (Exception ex)
        {
            Fail(new HttpRequestException("Reading the page in the embedded browser failed: " + ex.Message, ex));
        }
    }

    private async void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string? message = e.TryGetWebMessageAsString();
        if (message is null || !message.StartsWith(ErrorPrefix, StringComparison.Ordinal))
        {
            Succeed(message ?? "");
            return;
        }
        // The in-page re-fetch failed; the rendered document still carries the page's data.
        try
        {
            string html = JsonSerializer.Deserialize<string>(
                await Browser.CoreWebView2.ExecuteScriptAsync("document.documentElement.outerHTML")) ?? "";
            Succeed(html);
        }
        catch (Exception ex)
        {
            Fail(new HttpRequestException("Reading the page in the embedded browser failed: " + ex.Message, ex));
        }
    }

    internal static bool IsChallenge(string titleOrHtml) =>
        titleOrHtml.Contains("Just a moment", StringComparison.OrdinalIgnoreCase)
        || titleOrHtml.Contains("cf_chl", StringComparison.Ordinal)
        || titleOrHtml.Contains("challenge-platform", StringComparison.Ordinal);

    private void Succeed(string html)
    {
        _timeout.Stop();
        _result.TrySetResult(html);
        Close();
    }

    private void Fail(Exception error)
    {
        _timeout.Stop();
        _result.TrySetException(error);
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}
