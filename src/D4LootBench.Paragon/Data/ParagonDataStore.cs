using System.Reflection;

namespace D4LootBench.Paragon.Data;

internal static class ParagonDataStore
{
    public static string LoadText()
    {
        return TryLoadExternal() ?? LoadEmbedded();
    }

    private static string? TryLoadExternal()
    {
        // AppContext.BaseDirectory is the process directory in both normal and single-file builds.
        // Assembly.Location returns "" in single-file bundles, so it cannot be used here.
        var path = Path.Combine(AppContext.BaseDirectory, "paragon-data.json");
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    private static string LoadEmbedded()
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream("D4LootBench.Paragon.Data.paragon-data.json")
            ?? throw new FileNotFoundException("Embedded resource paragon-data.json not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
