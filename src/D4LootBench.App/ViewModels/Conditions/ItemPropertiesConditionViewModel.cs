using CommunityToolkit.Mvvm.ComponentModel;
using D4LootBench.Core.Models;

namespace D4LootBench.App.ViewModels.Conditions;

public sealed partial class ItemPropertiesConditionViewModel : ConditionViewModel
{
    // The in-game editor exposes these as independent checkboxes that OR into one bitmask
    // (e.g. Ancestral + Mythic = 36). Unknown bits are preserved for lossless round-trips.
    private const int NoneBit      = 1;
    private const int AncestralBit = 4;
    private const int MythicBit    = 32;
    private const int KnownBits    = NoneBit | AncestralBit | MythicBit;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    [NotifyPropertyChangedFor(nameof(IsNone))]
    [NotifyPropertyChangedFor(nameof(IsAncestral))]
    [NotifyPropertyChangedFor(nameof(IsMythic))]
    [NotifyPropertyChangedFor(nameof(HasUnknownBits))]
    private int _propertyMask = NoneBit;

    public ItemPropertiesConditionViewModel() { }

    public ItemPropertiesConditionViewModel(ItemPropertiesCondition m) =>
        _propertyMask = m.PropertyMask;

    public bool IsNone
    {
        get => (PropertyMask & NoneBit) != 0;
        set => SetBit(NoneBit, value);
    }

    public bool IsAncestral
    {
        get => (PropertyMask & AncestralBit) != 0;
        set => SetBit(AncestralBit, value);
    }

    public bool IsMythic
    {
        get => (PropertyMask & MythicBit) != 0;
        set => SetBit(MythicBit, value);
    }

    public bool HasUnknownBits => (PropertyMask & ~KnownBits) != 0;

    private void SetBit(int bit, bool on) =>
        PropertyMask = on ? PropertyMask | bit : PropertyMask & ~bit;

    public override string TypeName => "Item Properties";

    public override string Summary
    {
        get
        {
            var parts = new List<string>(4);
            if (IsNone)      parts.Add("None");
            if (IsAncestral) parts.Add("Ancestral");
            if (IsMythic)    parts.Add("Mythic");
            if (HasUnknownBits || parts.Count == 0)
                parts.Add($"mask {PropertyMask}");
            return string.Join(" + ", parts);
        }
    }

    public override Condition BuildModel() => new ItemPropertiesCondition(PropertyMask);
}
