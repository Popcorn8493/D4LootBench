using D4LootBench.Core.Data;
using D4LootBench.Core.Models;

namespace D4LootBench.App.ViewModels.Conditions;

public sealed class ConditionViewModelFactory(IFilterDataService data) : IConditionViewModelFactory
{
    private static readonly Dictionary<Type, ConditionType> VmTypeMap = new()
    {
        [typeof(ItemPowerConditionViewModel)]      = ConditionType.ItemPower,
        [typeof(RarityConditionViewModel)]         = ConditionType.Rarity,
        [typeof(ItemPropertiesConditionViewModel)] = ConditionType.ItemProperties,
        [typeof(GreaterAffixConditionViewModel)]   = ConditionType.GreaterAffix,
        [typeof(CodexConditionViewModel)]          = ConditionType.Codex,
        [typeof(ItemTypeConditionViewModel)]       = ConditionType.ItemType,
        [typeof(AffixConditionViewModel)]          = ConditionType.RequiredAffixes,
        [typeof(OptionalAffixConditionViewModel)]  = ConditionType.OptionalAffixes,
        [typeof(SpecificUniqueConditionViewModel)] = ConditionType.SpecificUnique,
        [typeof(TalismanSetConditionViewModel)]    = ConditionType.TalismanSet,
    };

    public ConditionViewModel FromModel(Condition c)
    {
        ConditionViewModel vm = c switch
        {
            ItemPowerCondition m       => new ItemPowerConditionViewModel(m),
            RarityCondition m          => new RarityConditionViewModel(m),
            ItemPropertiesCondition m  => new ItemPropertiesConditionViewModel(m),
            GreaterAffixCondition m    => new GreaterAffixConditionViewModel(m),
            CodexCondition             => new CodexConditionViewModel(),
            ItemTypeCondition m        => new ItemTypeConditionViewModel(data, m),
            AffixCondition m           => new AffixConditionViewModel(data, m),
            OptionalAffixCondition m   => new OptionalAffixConditionViewModel(data, m),
            SpecificUniqueCondition m  => new SpecificUniqueConditionViewModel(data, m),
            TalismanSetCondition m     => new TalismanSetConditionViewModel(data, m),
            UnknownCondition m         => new UnknownConditionViewModel(m),
            _                          => throw new InvalidOperationException($"Unhandled condition type: {c.GetType().Name}")
        };
        // Existing conditions open collapsed to a summary line; new ones (CreateNew) open expanded.
        vm.IsExpanded = false;
        return vm;
    }

    public ConditionViewModel CreateNew(ConditionType type) => type switch
    {
        ConditionType.ItemPower       => new ItemPowerConditionViewModel(),
        ConditionType.Rarity          => new RarityConditionViewModel(),
        ConditionType.ItemProperties  => new ItemPropertiesConditionViewModel(),
        ConditionType.GreaterAffix    => new GreaterAffixConditionViewModel(),
        ConditionType.Codex           => new CodexConditionViewModel(),
        ConditionType.ItemType        => new ItemTypeConditionViewModel(data),
        ConditionType.RequiredAffixes => new AffixConditionViewModel(data),
        ConditionType.OptionalAffixes => new OptionalAffixConditionViewModel(data),
        ConditionType.SpecificUnique  => new SpecificUniqueConditionViewModel(data),
        ConditionType.TalismanSet     => new TalismanSetConditionViewModel(data),
        _                             => throw new InvalidOperationException($"Unhandled condition type: {type}")
    };

    public ConditionType? GetConditionType(ConditionViewModel vm) =>
        VmTypeMap.TryGetValue(vm.GetType(), out var type) ? type : null;
}
