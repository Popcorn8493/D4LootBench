using Shouldly;

namespace D4LootBench.Ai.Tests;

public sealed class NameResolverTests
{
    private readonly NameResolver _resolver = new(TestSetup.Data);

    private static uint AffixHash(string name)
    {
        TestSetup.Data.Affixes.TryGetByName(name, out var entry).ShouldBeTrue();
        return entry.Hash;
    }

    [Fact]
    public void Affix_ExactName_Resolves()
    {
        _resolver.TryResolveAffix("+Critical Strike Chance", out var hash, out var suggestions).ShouldBeTrue();
        hash.ShouldBe(AffixHash("+Critical Strike Chance"));
        suggestions.ShouldBeEmpty();
    }

    [Fact]
    public void Affix_DifferentCase_Resolves()
    {
        _resolver.TryResolveAffix("+CRITICAL strike chance", out var hash, out _).ShouldBeTrue();
        hash.ShouldBe(AffixHash("+Critical Strike Chance"));
    }

    [Fact]
    public void Affix_MissingPlusSign_ResolvesFuzzily()
    {
        _resolver.TryResolveAffix("Critical Strike Chance", out var hash, out _).ShouldBeTrue();
        hash.ShouldBe(AffixHash("+Critical Strike Chance"));
    }

    [Fact]
    public void Affix_AmbiguousPartial_FailsWithSuggestions()
    {
        // "critical strike" is contained in both Chance and Damage — no unique auto-resolve.
        _resolver.TryResolveAffix("critical strike", out _, out var suggestions).ShouldBeFalse();
        suggestions.ShouldContain("+Critical Strike Chance");
        suggestions.ShouldContain("Critical Strike Damage");
    }

    [Fact]
    public void Affix_NoMatch_FailsWithoutSuggestions()
    {
        _resolver.TryResolveAffix("Zzqx Quantum Flux", out var hash, out var suggestions).ShouldBeFalse();
        hash.ShouldBe(0u);
        suggestions.ShouldBeEmpty();
    }

    [Fact]
    public void ItemType_ExactAndCaseInsensitive_Resolve()
    {
        _resolver.TryResolveItemType("Gloves", out var exact, out _).ShouldBeTrue();
        _resolver.TryResolveItemType("gloves", out var lower, out _).ShouldBeTrue();
        lower.ShouldBe(exact);
    }

    [Fact]
    public void Unique_PunctuationInsensitive_Resolves()
    {
        _resolver.TryResolveUnique("Harlequin Crest", out var exact, out _).ShouldBeTrue();
        _resolver.TryResolveUnique("harlequin-crest", out var fuzzy, out _).ShouldBeTrue();
        fuzzy.ShouldBe(exact);
    }

    [Fact]
    public void Unique_NoMatch_Fails()
    {
        _resolver.TryResolveUnique("Definitely Not A Unique Item", out _, out _).ShouldBeFalse();
    }

    [Fact]
    public void TalismanSet_CaseInsensitive_Resolves()
    {
        var set = TestSetup.Data.TalismanSets.All.First();
        _resolver.TryResolveTalismanSet(set.Name.ToUpperInvariant(), out var hash, out _).ShouldBeTrue();
        hash.ShouldBe(set.Hash);
    }
}
