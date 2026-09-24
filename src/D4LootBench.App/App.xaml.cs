using System.Windows;
using System.Windows.Threading;
using D4LootBench.App.Services;
using D4LootBench.Core.Data;
using D4LootBench.Core.Serialization;
using Microsoft.Extensions.DependencyInjection;

namespace D4LootBench.App;

public partial class App
{
    public static IServiceProvider Services { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Hooked first so even a failing startup leaves a trace in error.log.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        Services = ServiceConfiguration.Build();
        FilterDataContext.Set(Services.GetRequiredService<IFilterDataService>());
        base.OnStartup(e);

        var window = Services.GetRequiredService<MainWindow>();
        window.Show();
    }

    /// <summary>
    /// A UI-thread exception no command caught (async void handlers and async commands land
    /// here too): log it, tell the user, and keep the app alive — an editor losing unsaved
    /// work to one failed click is worse than continuing in a possibly degraded state.
    /// </summary>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ErrorLog.Write(e.Exception, "Unhandled UI exception");
        e.Handled = true;
        try
        {
            MessageBox.Show(
                $"Something went wrong:\n\n{e.Exception.Message}\n\n" +
                $"The details were written to {ErrorLog.LogPath}. " +
                "If the app misbehaves from here, save your work and restart it.",
                "D4LootBench — Unexpected Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch
        {
            // Showing the message failed too (e.g. during shutdown) — the log has it.
        }
    }

    /// <summary>A faulted task nobody awaited: log it and mark it observed so it can't escalate.</summary>
    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        ErrorLog.Write(e.Exception, "Unobserved task exception");
        e.SetObserved();
    }

    /// <summary>A background-thread crash: the process is going down regardless; leave a trace.</summary>
    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
            ErrorLog.Write(exception, $"Unhandled exception (terminating: {e.IsTerminating})");
    }
}
