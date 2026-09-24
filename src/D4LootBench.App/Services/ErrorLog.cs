using System.IO;

namespace D4LootBench.App.Services;

/// <summary>
/// Best-effort crash/error log at %AppData%\D4LootBench\error.log. Logging must never throw —
/// it runs from the global exception handlers, where a second failure would take the app down.
/// </summary>
public static class ErrorLog
{
    /// <summary>Past this size the log restarts, so a crash loop can't fill the disk.</summary>
    private const long MaxBytes = 1024 * 1024;

    private static readonly object Gate = new();

    /// <summary>Settable for tests, so they don't write into the user's real log.</summary>
    public static string LogPath { get; internal set; } = JsonFileStore.AppDataPath("error.log");

    public static void Write(Exception exception, string context)
    {
        try
        {
            string entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {context}{Environment.NewLine}" +
                           $"{exception}{Environment.NewLine}{Environment.NewLine}";
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                var info = new FileInfo(LogPath);
                if (info.Exists && info.Length > MaxBytes)
                    File.WriteAllText(LogPath, entry);
                else
                    File.AppendAllText(LogPath, entry);
            }
        }
        catch
        {
            // Nowhere left to report it.
        }
    }
}
