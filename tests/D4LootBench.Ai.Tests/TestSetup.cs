using System.Runtime.CompilerServices;
using D4LootBench.Core.Data;
using D4LootBench.Core.Serialization;

namespace D4LootBench.Ai.Tests;

internal static class TestSetup
{
    /// <summary>One catalog load shared by every test (the embedded d4-data.json).</summary>
    public static readonly FilterDataService Data = new();

    [ModuleInitializer]
    public static void Init() => FilterDataContext.Set(Data);
}
