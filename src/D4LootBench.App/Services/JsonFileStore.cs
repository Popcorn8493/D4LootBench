using System.IO;
using System.Text.Json;

namespace D4LootBench.App.Services;

/// <summary>Shared pieces of <see cref="JsonFileStore{T}"/>: the settings folder and cached options.</summary>
public static class JsonFileStore
{
    /// <summary>Indented output (the settings files are hand-inspectable), case-insensitive reads.</summary>
    public static JsonSerializerOptions Indented { get; } =
        new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    /// <summary>Compact output, case-insensitive reads.</summary>
    public static JsonSerializerOptions Compact { get; } =
        new() { PropertyNameCaseInsensitive = true };

    /// <summary>%AppData%\D4LootBench\<paramref name="fileName"/> — where every settings file lives.</summary>
    public static string AppDataPath(string fileName) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "D4LootBench", fileName);
}

/// <summary>
/// One JSON settings/library file. Writes are atomic (temp file, then an overwriting move),
/// so a crash or full disk mid-save never leaves a truncated file. A file that exists but
/// can't be read or parsed is renamed to <c>&lt;name&gt;.bad</c> (timestamped if one already
/// exists) before the owner starts empty — the next save then can't silently destroy the
/// user's data. If even the rename fails, saving is disabled for the session for the same
/// reason. Every failure is logged and swallowed: settings persistence is best-effort and
/// must never throw (it runs during shutdown, too).
/// </summary>
public sealed class JsonFileStore<T>
{
    private readonly JsonSerializerOptions _options;

    /// <summary>Set when an unreadable file couldn't be moved aside — writing would clobber it.</summary>
    private bool _saveBlocked;

    public JsonFileStore(string path, JsonSerializerOptions? options = null)
    {
        FilePath = path;
        _options = options ?? JsonFileStore.Indented;
    }

    public string FilePath { get; }

    /// <summary>Where the last unreadable file was moved, if any (for tests and diagnostics).</summary>
    public string? LastBackupPath { get; private set; }

    /// <summary>The stored value; default when the file is missing, empty, or unreadable.</summary>
    public T? Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return default;
            string json = File.ReadAllText(FilePath);
            return string.IsNullOrWhiteSpace(json) ? default : JsonSerializer.Deserialize<T>(json, _options);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException
                                       or NotSupportedException)
        {
            ErrorLog.Write(ex, $"Unreadable settings file {FilePath}");
            MoveAside();
            return default;
        }
    }

    /// <summary>Atomically replaces the file; false (logged) when it couldn't be written.</summary>
    public bool Save(T value)
    {
        if (_saveBlocked)
            return false;
        string temp = FilePath + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, _options);
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, FilePath, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            // Deliberately broad: saves run from window-close and shutdown paths, where any
            // escaping exception would abort the close. The log keeps the cause.
            ErrorLog.Write(ex, $"Couldn't save settings file {FilePath}");
            try
            {
                File.Delete(temp);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                // A stray .tmp is harmless — the next save overwrites it.
            }
            return false;
        }
    }

    private void MoveAside()
    {
        try
        {
            string backup = FilePath + ".bad";
            if (File.Exists(backup))
                backup = $"{FilePath}.{DateTime.Now:yyyyMMdd-HHmmss-fff}.bad";
            File.Move(FilePath, backup);
            LastBackupPath = backup;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Write(ex, $"Couldn't move aside unreadable settings file {FilePath} — saving disabled");
            _saveBlocked = true;
        }
    }
}
