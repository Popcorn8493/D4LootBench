using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using D4LootBench.App.Services;
using D4LootBench.Paragon.Solver;

namespace D4LootBench.App.Views;

/// <summary>
/// Review step of the stat-sheet scan: every OCR'd row with its guessed bucket, all of it
/// editable — bucket, stat name, and the "Adds %" number — plus manual rows and in-dialog
/// rescans (another panel snip merges by stat name; a single stat's hover tooltip replaces
/// that stat's row with the game's own exact "items and Paragon" decomposition). The
/// subtraction row removes the planner build's own paragon additive (the in-game sheet
/// aggregates paragon and gear together; the planner re-adds the paragon slice from the
/// boards, so leaving it in would count it twice). Apply hands back the two field values.
/// </summary>
public partial class StatSheetImportWindow : Window
{
    private readonly List<SheetRow> _rows = [];
    private readonly double _paragonAlwaysOn;
    private readonly double _paragonSituational;

    /// <summary>The Additive dmg % field value (percent), valid when DialogResult is true.</summary>
    public double AdditiveResult { get; private set; }

    /// <summary>The Situational dmg % field value (percent), valid when DialogResult is true.</summary>
    public double SituationalResult { get; private set; }

    /// <param name="paragonAlwaysOnPercent">The planner build's always-on paragon additive
    /// (percent, situational slice excluded).</param>
    /// <param name="paragonSituationalPercent">Its situational paragon additive (percent).</param>
    public StatSheetImportWindow(
        IReadOnlyList<SheetStatLine> lines,
        double paragonAlwaysOnPercent,
        double paragonSituationalPercent)
    {
        InitializeComponent();
        _paragonAlwaysOn = paragonAlwaysOnPercent;
        _paragonSituational = paragonSituationalPercent;
        Merge(lines);
        RowList.ItemsSource = _rows;
        ScanStatus.Text = lines.Count == 0
            ? "Nothing on the clipboard was recognizable — add rows manually, or snip the stats panel and Scan clipboard again."
            : "";

        bool hasParagon = paragonAlwaysOnPercent > 0.05 || paragonSituationalPercent > 0.05;
        SubtractParagon.IsChecked = hasParagon;
        SubtractParagon.IsEnabled = hasParagon;
        SubtractLabel.Text = hasParagon
            ? $"Subtract the planner build's own paragon additive (−{paragonAlwaysOnPercent:0.#}% " +
              $"always-on, −{paragonSituationalPercent:0.#}% situational) — the sheet folds your " +
              "board into these totals, and the planner re-adds its slice from the boards. Uncheck " +
              "if the screenshot predates your current paragon allocation."
            : "No paragon build in the planner — nothing to subtract. Note the sheet still folds " +
              "your LIVE in-game board into these totals; load or solve the matching build first " +
              "for an accurate gear-only split.";
        Recompute();
    }

    private void Recompute()
    {
        if (Totals is null)
            return;
        double alwaysOn = _rows.Where(r => r.Bucket == SheetBucket.AlwaysOn).Sum(r => r.Contribution);
        double situational = _rows.Where(r => r.Bucket == SheetBucket.Situational).Sum(r => r.Contribution);
        bool subtract = SubtractParagon.IsChecked == true;
        double additive = alwaysOn - (subtract ? _paragonAlwaysOn : 0);
        double situationalResult = situational - (subtract ? _paragonSituational : 0);
        AdditiveResult = Math.Max(0, additive);
        SituationalResult = Math.Max(0, situationalResult);
        string clamped = additive < 0 || situationalResult < 0
            ? "   (negative clamped to 0 — the sheet shows less than the planner build supplies; is the screenshot current?)"
            : "";
        Totals.Text = $"Additive dmg % = {AdditiveResult:0.#}   ·   Situational dmg % = {SituationalResult:0.#}{clamped}";
    }

    /// <summary>New rows append; a row whose stat is already listed updates it in place — so a
    /// hover-tooltip snip's exact value replaces the panel headline's estimate for that stat.</summary>
    private void Merge(IReadOnlyList<SheetStatLine> lines)
    {
        foreach (var line in lines)
        {
            var existing = _rows.FirstOrDefault(r =>
                string.Equals(r.Label.Trim(), line.Label, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
                existing.SetFrom(line);
            else
                _rows.Add(new SheetRow(line, Recompute));
        }
    }

    private void OnRecompute(object sender, RoutedEventArgs e) => Recompute();

    private void OnAddRow(object sender, RoutedEventArgs e)
    {
        var row = new SheetRow(
            new SheetStatLine("", 0, 0, SheetBucket.Situational, "Manual entry."), Recompute);
        _rows.Add(row);
        RowList.Items.Refresh();
        RowList.ScrollIntoView(row);
    }

    private async void OnScanClipboard(object sender, RoutedEventArgs e)
    {
        if (!Clipboard.ContainsImage() || Clipboard.GetImage() is not { } image)
        {
            ScanStatus.Text = "No screenshot on the clipboard — snip with Win+Shift+S first.";
            return;
        }
        try
        {
            ScanStatus.Text = "Reading the screenshot…";
            var lines = await TooltipOcrService.ReadLineInfosAsync(image);
            var parsed = StatSheetParser.Parse(
                [.. lines.Select(l => new SheetOcrLine(l.Text, l.CenterY, l.Height))]);
            if (parsed.Count == 0)
            {
                ScanStatus.Text = "Nothing recognizable on that snip.";
                return;
            }
            Merge(parsed);
            RowList.Items.Refresh();
            Recompute();
            ScanStatus.Text = $"Merged {parsed.Count} row(s) from the snip.";
        }
        catch (Exception ex)
        {
            ScanStatus.Text = $"Scan failed: {ex.Message}";
        }
    }

    private void OnApply(object sender, RoutedEventArgs e) => DialogResult = true;

    /// <summary>One reviewable row — bucket, label, and contribution all editable.</summary>
    public sealed class SheetRow : INotifyPropertyChanged
    {
        private readonly Action _changed;
        private int _bucketIndex;
        private string _label;
        private string _contributionText;
        private string _sheetText;
        private string _baseText;
        private string? _note;

        public SheetRow(SheetStatLine line, Action changed)
        {
            _changed = changed;
            _label = line.Label;
            _bucketIndex = (int)line.Bucket;
            _contributionText = line.Contribution.ToString("0.#", CultureInfo.CurrentCulture);
            _sheetText = line.Value > 0 ? line.Value.ToString("#,0.#", CultureInfo.CurrentCulture) : "";
            _baseText = line.BaseValue > 0 ? $"−{line.BaseValue:0}% base" : "";
            _note = line.Note;
        }

        public SheetBucket Bucket => (SheetBucket)_bucketIndex;
        public string SheetText => _sheetText;
        public string BaseText => _baseText;
        public string? Note => _note;

        public double Contribution =>
            double.TryParse(_contributionText.Replace(",", "").TrimEnd('%', ' '),
                NumberStyles.Float, CultureInfo.CurrentCulture, out double value)
                ? Math.Max(0, value) : 0;

        /// <summary>A rescan's fresh read of the same stat wins over the stale row.</summary>
        public void SetFrom(SheetStatLine line)
        {
            _label = line.Label;
            _bucketIndex = (int)line.Bucket;
            _contributionText = line.Contribution.ToString("0.#", CultureInfo.CurrentCulture);
            _sheetText = line.Value > 0 ? line.Value.ToString("#,0.#", CultureInfo.CurrentCulture) : "";
            _baseText = line.BaseValue > 0 ? $"−{line.BaseValue:0}% base" : "";
            _note = line.Note;
            OnPropertyChanged(null); // all properties
            _changed();
        }

        public string Label
        {
            get => _label;
            set
            {
                if (value == _label)
                    return;
                _label = value;
                OnPropertyChanged();
            }
        }

        public string ContributionText
        {
            get => _contributionText;
            set
            {
                if (value == _contributionText)
                    return;
                _contributionText = value;
                OnPropertyChanged();
                _changed();
            }
        }

        public int BucketIndex
        {
            get => _bucketIndex;
            set
            {
                if (value == _bucketIndex || value < 0)
                    return;
                _bucketIndex = value;
                OnPropertyChanged();
                _changed();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
