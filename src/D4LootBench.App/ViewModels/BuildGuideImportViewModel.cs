using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using D4LootBench.Ai.Import;
using D4LootBench.App.Services;
using D4LootBench.Core.Codec;
using D4LootBench.Core.Import;
using D4LootBench.Core.Models;

namespace D4LootBench.App.ViewModels;

public sealed record FormatOption(BuildGuideFormat Format, string Label);

public partial class BuildGuideImportViewModel(
    BuildGuideImporter importer,
    BuildGuideFilterGenerator generator) : ObservableObject
{
    public static IReadOnlyList<FormatOption> FormatOptions { get; } =
    [
        new(BuildGuideFormat.Auto,       "Auto-detect"),
        new(BuildGuideFormat.Mobalytics, "Mobalytics"),
        new(BuildGuideFormat.Maxroll,    "Maxroll"),
        new(BuildGuideFormat.IcyVeins,   "Icy Veins"),
    ];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ImportCommand))]
    private string _pastedText = "";

    [ObservableProperty]
    private FormatOption _selectedFormatOption = FormatOptions[0];

    [ObservableProperty]
    private IReadOnlyList<string> _warnings = [];

    [ObservableProperty]
    private bool _hasWarnings;

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    private bool _hasStatus;

    /// <summary>Set after a successful import; read by the dialog owner to apply the result.</summary>
    public FilterRuleset? ImportedRuleset { get; private set; }

    /// <summary>Raised when the import succeeds. The dialog code-behind uses this to set DialogResult=true.</summary>
    public event Action? ImportSucceeded;

    /// <summary>
    /// Shows a pick-one dialog for multi-variant guides (set by the dialog code-behind);
    /// returns the chosen index or -1 on cancel. Null falls back to the first option.
    /// </summary>
    public Func<IReadOnlyList<string>, int>? PickOption { get; set; }

    [RelayCommand(CanExecute = nameof(CanImport))]
    private async Task Import()
    {
        HasError    = false;
        Warnings    = [];
        HasWarnings = false;
        HasStatus   = false;

        string input = PastedText.Trim();
        try
        {
            if (input.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                await ImportFromUrlAsync(input);
            else if (input.Contains("equipmentPriorityList", StringComparison.Ordinal))
                ImportMobalyticsHtml(input); // pasted page source, the fallback when fetching is blocked
            else
                ImportPastedText(input);
        }
        catch (BuildGuideImportException ex)
        {
            SetError(ex.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            SetError($"Couldn't fetch the page ({ex.Message}). Open the build in a browser, view the " +
                     "page source (Ctrl+U), copy it all, and paste the HTML here instead.");
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException)
        {
            SetError($"Import failed: {ex.Message}");
        }
    }

    // ── URL import: fetch the guide and read its embedded build data ─────

    private async Task ImportFromUrlAsync(string url)
    {
        if (url.Contains("mobalytics.gg", StringComparison.OrdinalIgnoreCase))
        {
            SetProgress("Fetching the Mobalytics page…");
            ImportMobalyticsHtml(await GuidePageFetcher.FetchPageAsync(url.Split('#')[0]));
            return;
        }
        if (url.Contains("maxroll.gg", StringComparison.OrdinalIgnoreCase))
        {
            await ImportMaxrollUrlAsync(url);
            return;
        }
        if (url.Contains("d4builds.gg", StringComparison.OrdinalIgnoreCase))
        {
            await ImportD4BuildsUrlAsync(url);
            return;
        }
        SetError("The URL is not a mobalytics.gg, maxroll.gg, or d4builds.gg page. " +
                 "For other sites, paste the guide's gear text instead.");
    }

    /// <summary>
    /// Mobalytics pages embed each variant's full gear list — slots, uniques, and the author's
    /// affix priorities — so the generated filter covers the whole build, not a pasted fragment.
    /// </summary>
    private void ImportMobalyticsHtml(string html)
    {
        if (html.Contains("cf_chl", StringComparison.Ordinal)
            && !html.Contains("equipmentPriorityList", StringComparison.Ordinal))
        {
            SetError("Cloudflare blocked the fetch. Open the build in a browser, view the page " +
                     "source (Ctrl+U), copy it all, and paste the HTML here instead.");
            return;
        }

        var variants = MobalyticsGearImporter.ExtractVariants(html);
        int index = variants.Count == 1
            ? 0
            : Pick(variants.Select(v => $"{v.Title} — {v.Items.Count} item(s)").ToList());
        if (index < 0)
        {
            SetProgress("Import cancelled.");
            return;
        }

        var result = generator.Generate(MobalyticsGearImporter.ToParsedGuide(variants[index]));
        result.Ruleset.Name = $"Mobalytics {variants[index].Title}";
        Succeed(result.Ruleset, result.Warnings);
    }

    /// <summary>
    /// Maxroll planners carry the guide author's own saved loot filters as in-game share codes —
    /// those import losslessly, no name resolution involved.
    /// </summary>
    private async Task ImportMaxrollUrlAsync(string url)
    {
        if (!MaxrollPlannerImporter.TryParsePlannerUrl(url, out string plannerId))
        {
            SetProgress("Fetching the Maxroll page…");
            string html = await GuidePageFetcher.FetchPageAsync(url.Split('#')[0]);
            plannerId = MaxrollPlannerImporter.ExtractPlannerIds(html).FirstOrDefault()
                ?? throw new BuildGuideImportException(
                    "No planner link found in the Maxroll page — is it a build guide?");
        }

        SetProgress("Fetching the Maxroll planner data…");
        string json = await GuidePageFetcher.FetchPageAsync(
            string.Format(MaxrollPlannerImporter.ProfileApiFormat, plannerId));
        var (buildName, filters) = MaxrollPlannerImporter.ExtractLootFilters(json);

        int index = filters.Count == 1
            ? 0
            : Pick(filters.Select(f => $"{buildName} — {f.Name}").ToList());
        if (index < 0)
        {
            SetProgress("Import cancelled.");
            return;
        }

        var ruleset = FilterCodec.Decode(filters[index].Code);
        ruleset.Name = $"{buildName} ({filters[index].Name})";
        Succeed(ruleset, []);
    }

    /// <summary>
    /// d4builds.gg pages render client-side from a public Firestore document, so the build's
    /// uuid from the URL fetches the whole thing — gear, uniques, and the author's stat
    /// priorities — without touching the page itself.
    /// </summary>
    private async Task ImportD4BuildsUrlAsync(string url)
    {
        if (!D4BuildsGearImporter.TryParseBuildUrl(url, out string buildId))
        {
            // Curated meta builds use a pretty slug; its prerendered page-data names the uuid.
            if (!D4BuildsGearImporter.TryParseBuildSlugUrl(url, out string slug))
            {
                SetError("The d4builds.gg link has no build id — copy a build page's URL " +
                         "(d4builds.gg/builds/…).");
                return;
            }
            SetProgress("Resolving the d4builds.gg build…");
            buildId = D4BuildsGearImporter.ExtractBuildId(
                await GuidePageFetcher.FetchPageAsync(string.Format(D4BuildsGearImporter.PageDataApiFormat, slug)));
        }

        SetProgress("Fetching the d4builds.gg build…");
        string json = await GuidePageFetcher.FetchPageAsync(
            string.Format(D4BuildsGearImporter.BuildDocumentApiFormat, buildId));
        var variants = D4BuildsGearImporter.ExtractVariants(json);

        int index = variants.Count == 1
            ? 0
            : Pick(variants.Select(v => $"{v.Title} — {v.Slots.Count} slot(s)").ToList());
        if (index < 0)
        {
            SetProgress("Import cancelled.");
            return;
        }

        var result = generator.Generate(D4BuildsGearImporter.ToParsedGuide(variants[index]));
        result.Ruleset.Name = $"d4builds {variants[index].Title}";
        Succeed(result.Ruleset, result.Warnings);
    }

    // ── Pasted-text import (the original path) ───────────────────────────

    private void ImportPastedText(string text)
    {
        var guide  = importer.Import(text, SelectedFormatOption.Format);
        var result = generator.Generate(guide);
        Succeed(result.Ruleset, result.Warnings);
    }

    private int Pick(IReadOnlyList<string> options) => PickOption?.Invoke(options) ?? 0;

    private void Succeed(FilterRuleset ruleset, IReadOnlyList<string> warnings)
    {
        ImportedRuleset = ruleset;
        Warnings        = warnings;
        HasWarnings     = warnings.Count > 0;
        HasStatus       = HasWarnings;
        StatusText      = HasWarnings
            ? $"{warnings.Count} affix name(s) could not be resolved — see warnings below."
            : "";
        ImportSucceeded?.Invoke();
    }

    private void SetProgress(string text)
    {
        StatusText = text;
        HasError   = false;
        HasStatus  = true;
    }

    private void SetError(string text)
    {
        StatusText = text;
        HasError   = true;
        HasStatus  = true;
    }

    private bool CanImport() => !string.IsNullOrWhiteSpace(PastedText);
}
