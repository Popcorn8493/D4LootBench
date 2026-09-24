using System.Collections.ObjectModel;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using D4LootBench.Ai;
using D4LootBench.Ai.Providers;
using D4LootBench.App.Services;
using D4LootBench.Core.Models;

namespace D4LootBench.App.ViewModels;

public partial class AiAssistantViewModel : ObservableObject
{
    private readonly RuleAssistant _assistant;
    private readonly LlmSettingsService _settingsService;
    private readonly Action<FilterRule> _onAddRule;
    private CancellationTokenSource? _cts;

    public AiAssistantViewModel(
        RuleAssistant assistant,
        LlmSettingsService settingsService,
        Action<FilterRule> onAddRule)
    {
        _assistant       = assistant;
        _settingsService = settingsService;
        _onAddRule       = onAddRule;

        var s = settingsService.Current;
        _provider  = s.Provider == LlmProviderType.Mock ? LlmProviderType.Ollama : s.Provider;
        _baseUrl   = s.BaseUrl;
        _modelName = s.ModelName;
    }

    // ── Prompt / generation ───────────────────────────────────────────────

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GenerateRuleCommand))]
    private string _userPrompt = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GenerateRuleCommand))]
    private bool _isGenerating;

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingRule))]
    [NotifyPropertyChangedFor(nameof(PendingRuleSummary))]
    [NotifyCanExecuteChangedFor(nameof(AddRuleCommand))]
    [NotifyCanExecuteChangedFor(nameof(DiscardRuleCommand))]
    private FilterRule? _pendingRule;

    public bool HasPendingRule => PendingRule is not null;

    public string PendingRuleSummary => PendingRule is null
        ? ""
        : $"\"{PendingRule.Name}\" — {PendingRule.Conditions.Count} condition(s)";

    [RelayCommand(CanExecute = nameof(CanGenerate))]
    private async Task GenerateRule(CancellationToken ct)
    {
        _cts?.Cancel();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        IsGenerating = true;
        PendingRule  = null;
        StatusText   = "";
        HasError     = false;

        try
        {
            var result = await _assistant.GenerateAsync(UserPrompt.Trim(), _cts.Token);

            if (result.Success)
            {
                PendingRule = result.Rule;
                var warnings = result.Warnings.Count > 0
                    ? " Warnings: " + string.Join("; ", result.Warnings)
                    : "";
                StatusText = "Rule generated." + warnings;
                HasError   = false;
            }
            else
            {
                var suggestions = result.Suggestions.Count > 0
                    ? "\nSuggestions: " + string.Join(", ", result.Suggestions.Take(5))
                    : "";
                StatusText = (result.ErrorMessage ?? "Unknown error.") + suggestions;
                HasError   = true;
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = "Cancelled.";
            HasError   = false;
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
            HasError   = true;
        }
        finally
        {
            IsGenerating = false;
        }
    }

    private bool CanGenerate() => !IsGenerating && !string.IsNullOrWhiteSpace(UserPrompt);

    [RelayCommand(CanExecute = nameof(HasPendingRule))]
    private void AddRule()
    {
        if (PendingRule is null) return;
        _onAddRule(PendingRule);
        PendingRule = null;
        StatusText  = "";
        UserPrompt  = "";
    }

    [RelayCommand(CanExecute = nameof(HasPendingRule))]
    private void DiscardRule()
    {
        PendingRule = null;
        StatusText  = "";
    }

    // ── Provider settings ─────────────────────────────────────────────────

    // Mock is excluded — it's a dev tool, not a user-facing option.
    public static IReadOnlyList<LlmProviderType> Providers { get; } = [LlmProviderType.Ollama];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConfigurableProvider))]
    private LlmProviderType _provider;

    [ObservableProperty] private string _baseUrl;
    [ObservableProperty] private string _modelName;

    public bool IsConfigurableProvider => Provider != LlmProviderType.Mock;

    partial void OnProviderChanged(LlmProviderType value)
    {
        if (value == LlmProviderType.Ollama)
            _ = RefreshModels(CancellationToken.None);
    }

    // ── Model list ────────────────────────────────────────────────────────

    public ObservableCollection<string> AvailableModels { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshModelsCommand))]
    private bool _isRefreshingModels;

    [RelayCommand(CanExecute = nameof(CanRefreshModels))]
    private async Task RefreshModels(CancellationToken ct)
    {
        IsRefreshingModels = true;
        try
        {
            var models = await FetchOllamaModels(ct);
            AvailableModels.Clear();
            foreach (var m in models) AvailableModels.Add(m);
        }
        catch { /* Ollama not running — list stays empty, user can type a model name */ }
        finally
        {
            IsRefreshingModels = false;
        }
    }

    private bool CanRefreshModels() => !IsRefreshingModels;

    /// <summary>Shared across calls (a client per request leaks sockets into TIME_WAIT); the
    /// short per-request timeout rides on a linked token instead of the client.</summary>
    private static readonly HttpClient OllamaHttp = new();

    private async Task<IEnumerable<string>> FetchOllamaModels(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var response = await OllamaHttp.GetFromJsonAsync<OllamaTagsResponse>(
            $"{BaseUrl.TrimEnd('/')}/api/tags", timeout.Token);
        return response?.Models?.Select(m => m.Name) ?? [];
    }

    private sealed record OllamaTagsResponse(
        [property: JsonPropertyName("models")] List<OllamaModelEntry>? Models);
    private sealed record OllamaModelEntry(
        [property: JsonPropertyName("name")] string Name);

    // ── Test connection ───────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestConnectionCommand))]
    private bool _isTesting;

    [RelayCommand]
    private void SaveSettings()
    {
        _settingsService.Save(new LlmSettings
        {
            Provider  = Provider,
            BaseUrl   = BaseUrl,
            ModelName = ModelName,
        });
        StatusText = "Settings saved.";
        HasError   = false;
    }

    [RelayCommand(CanExecute = nameof(CanTest))]
    private async Task TestConnection(CancellationToken ct)
    {
        IsTesting  = true;
        StatusText = "Testing connection…";
        HasError   = false;

        var tempSettings = new LlmSettings
        {
            Provider  = Provider,
            BaseUrl   = BaseUrl,
            ModelName = ModelName,
        };

        try
        {
            var provider = LlmProviderFactory.Create(tempSettings);
            if (provider is OllamaProvider ollama)
            {
                // Metadata only: a real generation would first load the model, which for a large
                // model (or one sharing the GPU with the game) takes minutes and looks hung.
                var status = await ollama.CheckAsync(ct);
                (StatusText, HasError) = status switch
                {
                    { Reachable: false } =>
                        ($"{status.Error} — is Ollama running? Start it from the Start menu, then test again.", true),
                    { ModelInstalled: false } =>
                        ($"Connected, but model '{ModelName}' isn't installed. " +
                         (status.InstalledModels.Count > 0
                             ? $"Installed: {string.Join(", ", status.InstalledModels)} — pick one, or run: ollama pull {ModelName}"
                             : $"No models installed yet — run: ollama pull {ModelName}"), true),
                    _ =>
                        ($"Connected — '{ModelName}' is installed{(status.IsThinkingModel ? " (thinking model; reasoning is switched off for rule generation)" : "")}. " +
                         "The first rule request loads the model into memory, which can take a few minutes for large " +
                         "models; later requests are much faster.", false),
                };
            }
            else
            {
                var completion = await provider.GetCompletionAsync(
                    "Respond with: {\"ok\":true}", "ping", ct);
                StatusText = completion.IsSuccess ? "Connection OK." : $"Failed: {completion.Error}";
                HasError   = !completion.IsSuccess;
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = "Test cancelled.";
            HasError   = false;
        }
        catch (Exception ex)
        {
            StatusText = $"Connection failed: {ex.Message}";
            HasError   = true;
        }
        finally
        {
            IsTesting = false;
        }
    }

    private bool CanTest() => !IsTesting;
}
