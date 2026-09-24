using System.IO;
using System.Runtime.CompilerServices;
using D4LootBench.App.Services;

namespace D4LootBench.App.Tests;

internal static class TestSetup
{
    /// <summary>Tests deliberately trip logged failures (corrupt files) — keep them out of the
    /// user's real %AppData% error log.</summary>
    [ModuleInitializer]
    internal static void Initialize() =>
        ErrorLog.LogPath = Path.Combine(Path.GetTempPath(), "d4lb-app-tests-error.log");
}
