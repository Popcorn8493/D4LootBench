using D4LootBench.Ai;
using D4LootBench.Ai.Import;
using D4LootBench.App.ViewModels;
using D4LootBench.App.ViewModels.Conditions;
using D4LootBench.Core.Data;
using D4LootBench.Core.Import;
using D4LootBench.Core.Validation;
using Microsoft.Extensions.DependencyInjection;

namespace D4LootBench.App.Services;

internal static class ServiceConfiguration
{
    public static IServiceProvider Build()
    {
        var services = new ServiceCollection();

        services.AddSingleton<IFilterDataService, FilterDataService>();
        services.AddSingleton<IFilterValidator, FilterValidator>();
        services.AddSingleton<IConditionViewModelFactory, ConditionViewModelFactory>();

        services.AddSingleton<IDialogService, DialogService>();

        // Settings and libraries: one instance per app, so every window sees the same state
        // and each file has a single writer. Factories pick the default %AppData% paths.
        services.AddSingleton(_ => new LlmSettingsService());
        services.AddSingleton(_ => new WindowSettingsService());
        services.AddSingleton(_ => new RecentProjectsService());
        services.AddSingleton(_ => new SavedCharacterService());
        services.AddSingleton(_ => new SavedGearService());
        services.AddSingleton(_ => new CompareReferenceService());

        services.AddSingleton<SystemPromptBuilder>();
        services.AddSingleton<NameResolver>();
        services.AddSingleton<ILlmProvider, SettingsAwareLlmProvider>();
        services.AddSingleton<RuleAssistant>();
        services.AddSingleton<BuildGuideImporter>();
        services.AddSingleton<BuildGuideFilterGenerator>();

        services.AddTransient<MainWindowViewModel>();
        services.AddTransient<ParagonPlannerViewModel>();
        services.AddTransient<MainWindow>();

        return services.BuildServiceProvider();
    }
}
