using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using D4LootBench.App.Services;
using D4LootBench.App.Views;
using D4LootBench.Paragon;
using D4LootBench.Paragon.Data;
using D4LootBench.Paragon.Import;
using D4LootBench.Paragon.Models;
using D4LootBench.Paragon.Serialization;
using D4LootBench.Paragon.Solver;

namespace D4LootBench.App.ViewModels;

public partial class ParagonPlannerViewModel
{
    // ── Saved characters: durable level / stats / glyph levels ───────────

    /// <summary>The character library; the selected one is re-imposed after imports and loads.</summary>
    public ObservableCollection<SavedCharacter> Characters { get; } = [];

    [ObservableProperty]
    private SavedCharacter? _selectedCharacter;

    /// <summary>Name for saving; follows the selection so Save updates the active character.</summary>
    [ObservableProperty]
    private string _characterName = "";

    /// <summary>Character level, stored with the character (informational).</summary>
    [ObservableProperty]
    private int _characterLevel = 60;

    partial void OnSelectedCharacterChanged(SavedCharacter? value)
    {
        _characterLibrary.ActiveName = value?.Name;
        DeleteCharacterCommand.NotifyCanExecuteChanged();
        if (value is null)
            return;
        CharacterName = value.Name;
        ImposeCharacter(value);
        SetStatus($"Character '{value.Name}' active — its stats, point pool and glyph levels now apply." +
                  (string.Equals(value.ClassName, SelectedClass, StringComparison.OrdinalIgnoreCase)
                      ? ""
                      : $" (Saved for {value.ClassName}; the current layout is {SelectedClass}.)"));
    }

    /// <summary>Re-applies the character's durable facts to the session (stats, pool, glyph levels).</summary>
    private void ImposeCharacter(SavedCharacter character)
    {
        CharacterLevel = character.Level;
        if (character.TotalPoints > 0)
            TotalPoints = character.TotalPoints;
        SheetStrength = character.SheetStrength;
        SheetIntelligence = character.SheetIntelligence;
        SheetWillpower = character.SheetWillpower;
        SheetDexterity = character.SheetDexterity;
        SheetAdditiveDamage = character.SheetAdditiveDamage;
        SheetSituationalDamage = character.SheetSituationalDamage;
        ApplyCharacterGlyphLevels(character);
    }

    /// <summary>Sets every socketed glyph the character owns to ITS recorded level; returns how many.</summary>
    private int ApplyCharacterGlyphLevels(SavedCharacter character)
    {
        int applied = 0;
        foreach (var socket in GlyphSockets)
        {
            if (socket.SelectedGlyph is null
                || !character.GlyphLevels.TryGetValue(socket.SelectedGlyph.InternalName, out int level))
                continue;
            socket.Level = level;
            applied++;
        }
        return applied;
    }

    [RelayCommand]
    private void SaveCharacter()
    {
        string name = CharacterName.Trim();
        if (name.Length == 0)
        {
            SetStatus("Give the character a name first (Character section on the Build tab).", error: true);
            return;
        }
        // Glyph levels ACCUMULATE: what's socketed now merges over what the character already
        // recorded, so a glyph saved earlier isn't forgotten while it's not in use.
        var levels = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (glyph, level) in _characterLibrary.Find(name)?.GlyphLevels
                                       ?? new Dictionary<string, int>())
            levels[glyph] = level;
        foreach (var socket in GlyphSockets.Where(s => s.SelectedGlyph is not null))
            levels[socket.SelectedGlyph!.InternalName] = socket.Level;

        _characterLibrary.Save(new SavedCharacter(name, SelectedClass, CharacterLevel, TotalPoints,
            SheetStrength, SheetIntelligence, SheetWillpower, SheetDexterity, levels)
        {
            SheetAdditiveDamage = SheetAdditiveDamage,
            SheetSituationalDamage = SheetSituationalDamage,
        });
        RefreshCharacters(selectName: name);
        SetStatus($"Saved character '{name}': level {CharacterLevel}, {TotalPoints}-point pool, " +
                  $"{levels.Count} glyph level(s) recorded.");
    }

    [RelayCommand(CanExecute = nameof(CanDeleteCharacter))]
    private void DeleteCharacter()
    {
        if (SelectedCharacter is not { } character)
            return;
        _characterLibrary.Delete(character.Name);
        RefreshCharacters(selectName: null);
        SetStatus($"Deleted character '{character.Name}'.");
    }

    private bool CanDeleteCharacter() => SelectedCharacter is not null;

    private void RefreshCharacters(string? selectName)
    {
        Characters.Clear();
        foreach (var character in _characterLibrary.Characters)
            Characters.Add(character);
        SelectedCharacter = selectName is null
            ? null
            : Characters.FirstOrDefault(c =>
                string.Equals(c.Name, selectName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Fills the two gear-damage fields from a screenshot of the in-game stats details panel
    /// (Offense section). The sheet aggregates every source per category but never splits
    /// always-on from conditional or gear from paragon, so OCR'd rows are classified by
    /// <see cref="StatSheetParser"/> (built-in bases excluded), the current build's own paragon
    /// additive is subtracted, and every row is reviewed in a dialog before anything is applied.
    /// </summary>
    [RelayCommand]
    private Task ScanSheetDamage() => RunGuardedAsync("Stat-sheet scan", ScanSheetDamageCoreAsync);

    private async Task ScanSheetDamageCoreAsync()
    {
        // No screenshot is fine — the dialog supports manual rows and in-dialog rescans.
        IReadOnlyList<SheetStatLine> parsed = [];
        if (ClipboardHelper.TryGetImage() is { } image)
        {
            try
            {
                SetStatus("Reading the stat-sheet screenshot…");
                var lines = await TooltipOcrService.ReadLineInfosAsync(image);
                // The panel's label and value columns come back as separate OCR lines — the
                // parser rebuilds the rows from their vertical positions.
                parsed = StatSheetParser.Parse(
                    [.. lines.Select(l => new SheetOcrLine(l.Text, l.CenterY, l.Height))]);
            }
            catch (Exception ex)
            {
                SetStatus($"Scan failed ({ex.Message}) — enter the rows manually.", error: true);
            }
        }

        var dialog = new StatSheetImportWindow(parsed,
            paragonAlwaysOnPercent: (_paragonAdditiveSlices.Additive - _paragonAdditiveSlices.Situational) * 100,
            paragonSituationalPercent: _paragonAdditiveSlices.Situational * 100);
        if (_dialogs.ShowDialog(dialog, this) != true)
        {
            SetStatus("Stat-sheet scan discarded — the gear damage fields are unchanged.");
            return;
        }
        SheetAdditiveDamage = Math.Round(dialog.AdditiveResult, 1);
        SheetSituationalDamage = Math.Round(dialog.SituationalResult, 1);
        SetStatus($"Gear damage set from the stat sheet: +{SheetAdditiveDamage:0.#}% always-on, " +
                  $"+{SheetSituationalDamage:0.#}% situational.");
    }
}
