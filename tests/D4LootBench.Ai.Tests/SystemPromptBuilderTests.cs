using Shouldly;

namespace D4LootBench.Ai.Tests;

public sealed class SystemPromptBuilderTests
{
    [Fact]
    public void Prompt_DescribesSchemaAndListsCatalogNames()
    {
        var prompt = new SystemPromptBuilder(TestSetup.Data).Prompt;

        prompt.ShouldContain("\"visibility\"");
        prompt.ShouldContain("\"conditions\"");
        prompt.ShouldContain("Critical Strike Chance");
        prompt.ShouldContain("Gloves");
        prompt.ShouldNotContain("0x001B"); // names only — hash IDs never reach the model
    }

    [Fact]
    public void Prompt_IsBuiltOnceAndCached()
    {
        var builder = new SystemPromptBuilder(TestSetup.Data);
        builder.Prompt.ShouldBeSameAs(builder.Prompt);
    }
}
