using D4LootBench.Ai.Providers;
using D4LootBench.Core.Models;
using D4LootBench.Core.Validation;
using Shouldly;

namespace D4LootBench.Ai.Tests;

public sealed class RuleAssistantTests
{
    /// <summary>Returns a canned completion; records what it was sent.</summary>
    private sealed class FakeProvider(LlmCompletion completion) : ILlmProvider
    {
        public string? LastSystemPrompt { get; private set; }
        public string? LastUserPrompt { get; private set; }

        public Task<LlmCompletion> GetCompletionAsync(string systemPrompt, string userPrompt, CancellationToken ct = default)
        {
            LastSystemPrompt = systemPrompt;
            LastUserPrompt = userPrompt;
            return Task.FromResult(completion);
        }
    }

    private static RuleAssistant Assistant(ILlmProvider provider) => new(
        provider,
        new SystemPromptBuilder(TestSetup.Data),
        new NameResolver(TestSetup.Data),
        new FilterValidator());

    private static Task<RuleGenerationResult> Generate(string json) =>
        Assistant(new FakeProvider(LlmCompletion.Ok(json))).GenerateAsync("anything", TestContext.Current.CancellationToken);

    [Fact]
    public async Task MockProvider_ProducesValidLegendaryGlovesRule()
    {
        var result = await Assistant(new MockLlmProvider()).GenerateAsync("legendary gloves", TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue(result.ErrorMessage);
        var rule = result.Rule.ShouldNotBeNull();
        rule.Name.ShouldBe("Legendary Gloves");
        rule.Visibility.ShouldBe(Visibility.Show);
        rule.Conditions.OfType<ItemTypeCondition>().ShouldHaveSingleItem().TypeIds.Count.ShouldBe(1);
        rule.Conditions.OfType<RarityCondition>().ShouldHaveSingleItem().Mask.ShouldBe(RarityFlags.Legendary);
        result.RawResponse.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task PassesSystemPromptAndUserPromptToProvider()
    {
        var provider = new FakeProvider(LlmCompletion.Ok("""{"name":"x","visibility":"Show","conditions":[]}"""));
        await Assistant(provider).GenerateAsync("show me rings", TestContext.Current.CancellationToken);

        provider.LastUserPrompt.ShouldBe("show me rings");
        provider.LastSystemPrompt.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ResolvesAffixesAndGreaterAffixes()
    {
        var result = await Generate("""
            {"name":"Crit Rings","visibility":"Recolor","conditions":[
              {"type":"ItemType","items":["Ring"]},
              {"type":"RequiredAffixes","affixes":["Critical Strike Chance","Critical Strike Damage"],
               "greaterAffixes":["Critical Strike Damage"],"minimumCount":2}
            ]}
            """);

        result.Success.ShouldBeTrue(result.ErrorMessage);
        var rule = result.Rule!;
        rule.Visibility.ShouldBe(Visibility.Recolor);
        var affix = rule.Conditions.OfType<AffixCondition>().ShouldHaveSingleItem();
        affix.AffixIds.Count.ShouldBe(2);
        affix.MinimumCount.ShouldBe(2);
        affix.GreaterEntries.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task MalformedJson_FailsWithRawResponse()
    {
        var result = await Generate("{ this is not json");

        result.Success.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNull().ShouldContain("invalid JSON");
        result.RawResponse.ShouldBe("{ this is not json");
    }

    [Fact]
    public async Task NullJson_Fails()
    {
        var result = await Generate("null");
        result.Success.ShouldBeFalse();
    }

    [Fact]
    public async Task ProviderError_IsSurfaced()
    {
        var result = await Assistant(new FakeProvider(LlmCompletion.Fail("Ollama request timed out")))
            .GenerateAsync("x", TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.ErrorMessage.ShouldBe("Ollama request timed out");
    }

    [Fact]
    public async Task UnknownName_FailsWithSuggestions()
    {
        var result = await Generate("""
            {"name":"Crit","visibility":"Show","conditions":[
              {"type":"RequiredAffixes","affixes":["critical strike"]}
            ]}
            """);

        result.Success.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNull().ShouldContain("Unknown name 'critical strike'");
        result.Suggestions.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task UnknownConditionType_Fails()
    {
        var result = await Generate("""{"name":"X","visibility":"Show","conditions":[{"type":"Sparkles"}]}""");
        result.Success.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNull().ShouldContain("Sparkles");
    }

    [Fact]
    public async Task LongName_IsTruncatedWithWarning()
    {
        var result = await Generate(
            """{"name":"Extremely Long Rule Name That Exceeds Limit","visibility":"HideAll","conditions":[]}""");

        result.Success.ShouldBeTrue(result.ErrorMessage);
        result.Rule!.Name.Length.ShouldBeLessThanOrEqualTo(FilterValidator.MaxRuleNameLength);
        result.Warnings.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task UnknownVisibility_DefaultsToShow()
    {
        var result = await Generate("""{"name":"X","visibility":"Glow","conditions":[{"type":"Codex"}]}""");
        result.Success.ShouldBeTrue(result.ErrorMessage);
        result.Rule!.Visibility.ShouldBe(Visibility.Show);
    }
}
