using D4LootBench.Core.Compare;
using D4LootBench.Core.Models;
using Shouldly;

namespace D4LootBench.Core.Tests.Compare;

public sealed class ItemChoiceComparerTests
{
    private const uint Gloves = 100;
    private const uint CritChance = 1, AttackSpeed = 2, MaxLife = 3, Willpower = 4, JunkAffix = 9;
    private const uint TargetUnique = 500, OtherUnique = 501;

    /// <summary>A guide-shaped reference: gloves want crit > attack speed > max life, GA on crit.</summary>
    private static FilterRuleset Reference() => new("Test", [
        new FilterRule("Target Uniques", Visibility.Show, 0,
            [new SpecificUniqueCondition([TargetUnique])]),
        new FilterRule("Gloves", Visibility.Show, 0, [
            new ItemTypeCondition([Gloves]),
            new AffixCondition([CritChance, AttackSpeed, MaxLife], 2)
            {
                GreaterEntries = [new GreaterAffixEntry(CritChance, CritChance)],
            },
        ]),
        new FilterRule("Hide All", Visibility.HideAll, 0, []),
    ]);

    private static CandidateItem Item(string label, params CandidateAffix[] affixes) =>
        new(label, Gloves, null, affixes);

    private static CandidateAffix Affix(uint id, double? value = null, bool greater = false) =>
        new(id, $"affix-{id}", value, greater);

    [Fact]
    public void HigherPriorityAffixesWin()
    {
        var outcome = ItemChoiceComparer.Compare(
            [
                Item("A", Affix(CritChance), Affix(AttackSpeed)),   // priorities 1+2
                Item("B", Affix(MaxLife), Affix(JunkAffix)),        // priority 3 + junk
            ],
            Reference(), []);

        outcome.Scores[0].Total.ShouldBeGreaterThan(outcome.Scores[1].Total);
        outcome.Verdict.ShouldStartWith("A is the better choice");
        outcome.Scores[1].Lines.ShouldContain(l => l.Reason.Contains("not sought"));
    }

    [Fact]
    public void GreaterAffixOnDesiredStatScoresExtra()
    {
        var outcome = ItemChoiceComparer.Compare(
            [
                Item("GA", Affix(CritChance, greater: true)),
                Item("Plain", Affix(CritChance)),
            ],
            Reference(), []);

        outcome.Scores[0].Total.ShouldBe(outcome.Scores[1].Total * 1.5, 0.001);
        outcome.Scores[0].Lines.ShouldContain(l => l.Reason.Contains("exactly where the guide wants one"));
    }

    [Fact]
    public void HigherRollOfTheSameAffixWins()
    {
        var outcome = ItemChoiceComparer.Compare(
            [
                Item("Low", Affix(CritChance, value: 4)),
                Item("High", Affix(CritChance, value: 8)),
            ],
            Reference(), []);

        outcome.Scores[1].Total.ShouldBe(outcome.Scores[0].Total * 2, 0.001);
        outcome.Verdict.ShouldStartWith("High is the better choice");
    }

    [Fact]
    public void TargetUniqueBeatsGoodAffixItem()
    {
        var outcome = ItemChoiceComparer.Compare(
            [
                new CandidateItem("Unique", Gloves, TargetUnique, []),
                Item("Rare", Affix(CritChance), Affix(AttackSpeed)),
            ],
            Reference(), []);

        outcome.Scores[0].Total.ShouldBeGreaterThan(outcome.Scores[1].Total);
        outcome.Scores[0].Lines.ShouldContain(l => l.Reason.Contains("Target unique"));
    }

    [Fact]
    public void UntargetedUniqueScoresNothing()
    {
        var outcome = ItemChoiceComparer.Compare(
            [new CandidateItem("Off-build unique", Gloves, OtherUnique, [])],
            Reference(), []);

        outcome.Scores[0].Total.ShouldBe(0);
        outcome.Scores[0].Lines.ShouldContain(l => l.Reason.Contains("not one the build is hunting"));
    }

    [Fact]
    public void GreaterAffixWithoutValueDerivesMaxRollTimes1_5()
    {
        var outcome = ItemChoiceComparer.Compare(
            [
                Item("GA", Affix(CritChance, greater: true)),      // roll derives to 8 × 1.5 = 12
                Item("Natural", Affix(CritChance, value: 8)),
            ],
            Reference(), []);

        // GA: weight 4 × GA factor 1.5 × ratio 12/12; natural: weight 4 × ratio 8/12.
        outcome.Scores[0].Total.ShouldBe(6.0, 0.001);
        outcome.Scores[1].Total.ShouldBe(8.0 / 3, 0.001);
        outcome.Scores[0].Lines.ShouldContain(l => l.Reason.Contains("roll derived as max × 1.5"));
    }

    [Fact]
    public void GreaterAffixDerivedValueFeedsThresholdDeficits()
    {
        var needs = new[] { new StatNeed(Willpower, "Willpower", 300, "Tenacity") };
        var outcome = ItemChoiceComparer.Compare(
            [
                Item("GA", Affix(Willpower, greater: true)),      // derives to 100 × 1.5 = 150
                Item("Natural", Affix(Willpower, value: 100)),
            ],
            reference: null, needs);

        outcome.Scores[0].Total.ShouldBe(4.0 * 150 / 300, 0.001);
        outcome.Scores[1].Total.ShouldBe(4.0 * 100 / 300, 0.001);
    }

    [Fact]
    public void ParagonDeficitWeighsCoreStats_EvenWithoutAFilter()
    {
        var needs = new[] { new StatNeed(Willpower, "Willpower", 200, "Tenacity") };
        var outcome = ItemChoiceComparer.Compare(
            [
                Item("Willpower gloves", Affix(Willpower, value: 100)),
                Item("Junk gloves", Affix(JunkAffix, value: 100)),
            ],
            reference: null, needs);

        outcome.Scores[0].Total.ShouldBe(2.0, 0.001); // 4 points × (100 of 200 closed)
        outcome.Scores[0].Lines.ShouldContain(l => l.Reason.Contains("Tenacity"));
        outcome.Scores[1].Total.ShouldBe(0);
    }

    [Fact]
    public void TransfiguredStatScoresLikeAnAffix_AndIsAnnotated()
    {
        var needs = new[] { new StatNeed(Willpower, "Willpower", 200, "Tenacity") };
        var outcome = ItemChoiceComparer.Compare(
            [
                Item("Transfigured", new CandidateAffix(Willpower, "affix-4", 100, false, IsTransfigured: true)),
                Item("Natural", Affix(Willpower, value: 100)),
            ],
            reference: null, needs);

        outcome.Scores[0].Total.ShouldBe(outcome.Scores[1].Total, 0.001);
        outcome.Scores[0].Lines.ShouldContain(l => l.Reason.Contains("(transfigured)"));
    }

    [Fact]
    public void TransfiguredStatNeverCountsAsGreater()
    {
        // Even a (bogus) greater flag on a transfigured line earns no GA factor or derived roll.
        var outcome = ItemChoiceComparer.Compare(
            [
                Item("Transfigured", new CandidateAffix(CritChance, "affix-1", 8, true, IsTransfigured: true)),
                Item("Natural", Affix(CritChance, value: 8)),
            ],
            Reference(), []);

        outcome.Scores[0].Total.ShouldBe(outcome.Scores[1].Total, 0.001);
        outcome.Scores[0].Lines.ShouldNotContain(l => l.Reason.Contains("Greater Affix"));
    }

    [Fact]
    public void AggregateStatScoresThroughItsComponents()
    {
        // "All Stats" expands to Strength+Willpower here: it should satisfy the wish-list slot
        // for Willpower(=4) AND feed the Willpower threshold need, with no per-component noise.
        var allStats = new CandidateAffix(
            AggregateStats.AllStatsId, "All Stats", 100, false, ComponentIds: [Willpower, 77]);
        var needs = new[] { new StatNeed(Willpower, "Willpower", 200, "Tenacity") };
        var custom = new CompareWishList([new WishEntry([Willpower], false, "Willpower")], []);

        var outcome = ItemChoiceComparer.Compare(
            [Item("Aggregate", allStats), Item("Plain", Affix(Willpower, value: 100))],
            reference: null, needs, custom);

        // Both close 50% of the threshold and hit priority 1; identical totals.
        outcome.Scores[0].Total.ShouldBe(outcome.Scores[1].Total, 0.001);
        outcome.Scores[0].Lines.ShouldContain(l => l.Reason.Contains("priority 1"));
        outcome.Scores[0].Lines.ShouldContain(l => l.Reason.Contains("Tenacity"));
        outcome.Scores[0].Lines.ShouldNotContain(l => l.Reason.Contains("not sought"));
    }

    [Fact]
    public void CustomWishListOverridesTheLoadedFilter()
    {
        // The filter wants crit on gloves; the user's own list wants max life above all.
        var custom = new CompareWishList(
            [new WishEntry([MaxLife], false, "Maximum Life"), new WishEntry([CritChance], false, "Crit")],
            []);

        var outcome = ItemChoiceComparer.Compare(
            [
                Item("Life gloves", Affix(MaxLife)),
                Item("Crit gloves", Affix(CritChance)),
            ],
            Reference(), [], custom);

        outcome.Scores[0].Total.ShouldBeGreaterThan(outcome.Scores[1].Total);
        outcome.Verdict.ShouldStartWith("Life gloves is the better choice");
        outcome.Scores[0].Lines.ShouldContain(l => l.Reason.Contains("your reference list"));
    }

    [Fact]
    public void CustomWishListTargetUniqueScores()
    {
        var custom = new CompareWishList([], [OtherUnique]);

        var outcome = ItemChoiceComparer.Compare(
            [
                new CandidateItem("Hunted", Gloves, OtherUnique, []),
                new CandidateItem("Filter's unique", Gloves, TargetUnique, []),
            ],
            Reference(), [], custom);

        outcome.Scores[0].Lines.ShouldContain(l => l.Reason.Contains("Target unique of your reference list"));
        outcome.Scores[1].Lines.ShouldContain(l => l.Reason.Contains("not one your reference list is hunting"));
        outcome.Scores[0].Total.ShouldBeGreaterThan(outcome.Scores[1].Total);
    }

    [Fact]
    public void CustomWishListGreaterWantedIsCalledOut()
    {
        var custom = new CompareWishList([new WishEntry([CritChance], true, "Crit")], []);

        var outcome = ItemChoiceComparer.Compare(
            [Item("GA", Affix(CritChance, value: 8, greater: true))],
            reference: null, [], custom);

        outcome.Scores[0].Lines.ShouldContain(l =>
            l.Reason.Contains("exactly where your reference list wants one"));
    }

    [Fact]
    public void CloseScoresAreCalledATie()
    {
        var outcome = ItemChoiceComparer.Compare(
            [
                Item("A", Affix(CritChance)),
                Item("B", Affix(CritChance)),
            ],
            Reference(), []);

        outcome.Verdict.ShouldStartWith("Too close to call");
    }
}
