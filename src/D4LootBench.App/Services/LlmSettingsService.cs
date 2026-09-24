using System.Text.Json.Serialization;
using D4LootBench.Ai;

namespace D4LootBench.App.Services;

public sealed class LlmSettingsService
{
    private readonly JsonFileStore<StoredSettings> _store;

    public LlmSettingsService(string? path = null)
    {
        _store = new JsonFileStore<StoredSettings>(path ?? JsonFileStore.AppDataPath("ai-settings.json"));
        Current = Load();
    }

    public LlmSettings Current { get; private set; }

    /// <summary>Applies and persists the settings. Never throws — persistence is best-effort
    /// (the store logs a failed write) and the new settings apply for this session regardless.</summary>
    public void Save(LlmSettings settings)
    {
        Current = settings;
        _store.Save(new StoredSettings(settings.Provider, settings.BaseUrl, settings.ModelName));
    }

    private LlmSettings Load()
    {
        // A corrupt file is moved aside by the store — fall through to defaults.
        if (_store.Load() is not { } stored)
            return new LlmSettings();
        var defaults = new LlmSettings();
        return new LlmSettings
        {
            Provider  = stored.Provider,
            BaseUrl   = string.IsNullOrEmpty(stored.BaseUrl)   ? defaults.BaseUrl   : stored.BaseUrl,
            ModelName = string.IsNullOrEmpty(stored.ModelName) ? defaults.ModelName : stored.ModelName,
        };
    }

    private sealed record StoredSettings(
        [property: JsonPropertyName("provider")]  LlmProviderType Provider,
        [property: JsonPropertyName("baseUrl")]   string BaseUrl,
        [property: JsonPropertyName("modelName")] string ModelName);
}
