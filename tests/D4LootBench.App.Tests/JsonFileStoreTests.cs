using System.IO;
using D4LootBench.App.Services;
using Shouldly;

namespace D4LootBench.App.Tests;

public sealed class JsonFileStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "d4lb-store-" + Guid.NewGuid().ToString("N"));

    public JsonFileStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string PathOf(string name) => Path.Combine(_dir, name);

    public sealed record Sample(string Name, int Count, List<string> Tags);

    [Fact]
    public void RoundTrip_SaveThenLoad_ReturnsTheSameValue()
    {
        var store = new JsonFileStore<Sample>(PathOf("sample.json"));

        store.Save(new Sample("gloves", 3, ["a", "b"])).ShouldBeTrue();
        var loaded = new JsonFileStore<Sample>(PathOf("sample.json")).Load();

        loaded.ShouldNotBeNull();
        loaded.Name.ShouldBe("gloves");
        loaded.Count.ShouldBe(3);
        loaded.Tags.ShouldBe(["a", "b"]);
    }

    [Fact]
    public void Save_ReplacesAtomically_LeavingNoTempFile()
    {
        string path = PathOf("sample.json");
        var store = new JsonFileStore<Sample>(path);

        store.Save(new Sample("first", 1, [])).ShouldBeTrue();
        store.Save(new Sample("second", 2, [])).ShouldBeTrue();

        File.Exists(path + ".tmp").ShouldBeFalse();
        store.Load()!.Name.ShouldBe("second");
    }

    [Fact]
    public void Save_CreatesMissingDirectory()
    {
        string path = Path.Combine(_dir, "nested", "deeper", "sample.json");

        new JsonFileStore<Sample>(path).Save(new Sample("x", 0, [])).ShouldBeTrue();

        File.Exists(path).ShouldBeTrue();
    }

    [Fact]
    public void Load_MissingOrEmptyFile_ReturnsDefaultWithoutBackup()
    {
        var missing = new JsonFileStore<Sample>(PathOf("missing.json"));
        missing.Load().ShouldBeNull();
        missing.LastBackupPath.ShouldBeNull();

        File.WriteAllText(PathOf("empty.json"), "   ");
        var empty = new JsonFileStore<Sample>(PathOf("empty.json"));
        empty.Load().ShouldBeNull();
        empty.LastBackupPath.ShouldBeNull();
        File.Exists(PathOf("empty.json")).ShouldBeTrue();
    }

    [Fact]
    public void Load_CorruptFile_MovesItAsideAsBad_SoTheNextSaveCantDestroyIt()
    {
        string path = PathOf("sample.json");
        File.WriteAllText(path, "{ this is not json");
        var store = new JsonFileStore<Sample>(path);

        store.Load().ShouldBeNull();

        store.LastBackupPath.ShouldBe(path + ".bad");
        File.ReadAllText(path + ".bad").ShouldBe("{ this is not json");
        File.Exists(path).ShouldBeFalse();

        // The next save writes fresh data; the user's unreadable original is still preserved.
        store.Save(new Sample("fresh", 1, [])).ShouldBeTrue();
        File.ReadAllText(path + ".bad").ShouldBe("{ this is not json");
    }

    [Fact]
    public void Load_CorruptFile_WithExistingBackup_UsesATimestampedName()
    {
        string path = PathOf("sample.json");
        File.WriteAllText(path + ".bad", "older backup");
        File.WriteAllText(path, "[1, 2");
        var store = new JsonFileStore<Sample>(path);

        store.Load().ShouldBeNull();

        File.ReadAllText(path + ".bad").ShouldBe("older backup");
        store.LastBackupPath.ShouldNotBeNull();
        store.LastBackupPath.ShouldNotBe(path + ".bad");
        store.LastBackupPath.ShouldEndWith(".bad");
        File.ReadAllText(store.LastBackupPath).ShouldBe("[1, 2");
    }

    [Fact]
    public void Load_WrongShape_IsTreatedAsCorrupt()
    {
        string path = PathOf("sample.json");
        File.WriteAllText(path, "\"a string, not an object\"");
        var store = new JsonFileStore<Sample>(path);

        store.Load().ShouldBeNull();
        File.Exists(path + ".bad").ShouldBeTrue();
    }

    [Fact]
    public void SavedGearService_ReadsItsExistingFileFormat()
    {
        // Pre-store format: default System.Text.Json (PascalCase) list of items.
        string path = PathOf("saved-gear.json");
        File.WriteAllText(path, """
            [
              {
                "Name": "Equipped gloves",
                "ItemTypeId": 12,
                "UniqueId": null,
                "Affixes": [ { "AffixId": 5, "Name": "+Strength", "Value": "80", "IsGreater": true, "IsTransfigured": false } ]
              }
            ]
            """);

        var service = new SavedGearService(path);

        var item = service.Find("equipped gloves");
        item.ShouldNotBeNull();
        item.ItemTypeId.ShouldBe(12u);
        item.Affixes.Single().IsGreater.ShouldBeTrue();
    }

    [Fact]
    public void RecentProjectsService_PersistsAcrossInstances()
    {
        string path = PathOf("recent.json");
        var first = new RecentProjectsService(path);
        first.Touch(@"C:\a.paragon.json");
        first.Touch(@"C:\b.paragon.json");

        new RecentProjectsService(path).Paths.ShouldBe([@"C:\b.paragon.json", @"C:\a.paragon.json"]);
    }

    [Fact]
    public void SavedCharacterService_CorruptFile_StartsEmptyAndKeepsBackup()
    {
        string path = PathOf("saved-characters.json");
        File.WriteAllText(path, "{\"Active\": \"x\", \"Characters\": [");

        var service = new SavedCharacterService(path);

        service.Characters.ShouldBeEmpty();
        File.Exists(path + ".bad").ShouldBeTrue();
    }
}
