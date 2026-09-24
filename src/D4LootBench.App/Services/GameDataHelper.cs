using System.IO;
using System.Windows;
using D4LootBench.Core.Data;

namespace D4LootBench.App.Services;

internal static class GameDataHelper
{
    public static async Task ExtractAsync(IDialogService dialogs)
    {
        var targetPath = Path.Combine(AppContext.BaseDirectory, "d4-data.json");

        if (File.Exists(targetPath))
        {
            var result = dialogs.ShowMessage(
                $"d4-data.json already exists at:\n{targetPath}\n\nOverwrite it?",
                "Overwrite?",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;
        }

        try
        {
            await CopyEmbeddedToAsync(targetPath);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // Typically the exe lives somewhere read-only (Program Files). The loader only reads
            // an override from next to the exe, so a copy elsewhere is a starting point, not a fix.
            var saveElsewhere = dialogs.ShowMessage(
                $"Couldn't write to the app folder:\n{targetPath}\n\n{ex.Message}\n\n" +
                "D4LootBench only picks up a d4-data.json sitting next to its .exe, so to customize the " +
                "data, move the app to a folder you can write to (e.g. Documents) and extract again.\n\n" +
                "Save a copy somewhere else now to start editing?",
                "Extract Failed",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (saveElsewhere != MessageBoxResult.Yes)
                return;
            if (dialogs.ShowSaveFileDialog("Save d4-data.json", "Game Data JSON|*.json|All Files|*.*",
                    ".json", "d4-data.json") is not string otherPath)
                return;
            try
            {
                await CopyEmbeddedToAsync(otherPath);
            }
            catch (Exception retry) when (retry is UnauthorizedAccessException or IOException)
            {
                dialogs.ShowMessage($"Couldn't write {otherPath}:\n\n{retry.Message}", "Extract Failed",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            dialogs.ShowMessage(
                $"Saved to:\n{otherPath}\n\nWhen you're done editing, place it next to D4LootBench.exe " +
                "(in a writable folder) and restart to apply your changes.",
                "Game Data Extracted",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        dialogs.ShowMessage(
            $"Extracted to:\n{targetPath}\n\nEdit the file, then restart D4LootBench to apply your changes.",
            "Game Data Extracted",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private static async Task CopyEmbeddedToAsync(string path)
    {
        using var src = FilterDataExporter.OpenEmbeddedStream();
        await using var dst = File.Create(path);
        await src.CopyToAsync(dst);
    }
}
