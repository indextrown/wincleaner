using WinCleaner.Safety;

namespace WinCleaner.Tests;

public sealed class ChangeJournalTests : IDisposable
{
    private readonly Sandbox _box = new();
    private readonly string _path;

    public ChangeJournalTests()
    {
        _path = Path.Combine(_box.Root, "journal.json");
    }

    public void Dispose() => _box.Dispose();

    [Fact]
    public void Entries_survive_reload_and_come_newest_first()
    {
        var journal = new ChangeJournal(_path);
        journal.Add(new ChangeEntry { Kind = ChangeKind.Cleanup, Title = "old", Time = DateTime.Now.AddHours(-1) });
        journal.Add(new ChangeEntry
        {
            Kind = ChangeKind.Tweak,
            Title = "new",
            TweakId = "hibernate",
            Backup = new Dictionary<string, string?> { ["enabled"] = "true", ["fileType"] = null },
        });

        var reloaded = new ChangeJournal(_path).Entries;

        Assert.Equal(["new", "old"], reloaded.Select(e => e.Title));
        Assert.Equal("true", reloaded[0].Backup?["enabled"]);
        Assert.Null(reloaded[0].Backup?["fileType"]);
        Assert.True(reloaded[0].CanRevert);
        Assert.False(reloaded[1].CanRevert);
    }

    [Fact]
    public void Mark_reverted_stops_further_reverts_and_raises_changed()
    {
        var journal = new ChangeJournal(_path);
        var entry = journal.Add(new ChangeEntry { Kind = ChangeKind.Tweak, Title = "t", TweakId = "hibernate" });
        var changes = 0;
        journal.Changed += (_, _) => changes++;

        Assert.Equal(entry.Id, journal.LatestRevertible("hibernate")?.Id);
        journal.MarkReverted(entry.Id, DateTime.Now);

        Assert.Null(journal.LatestRevertible("hibernate"));
        Assert.NotNull(new ChangeJournal(_path).Entries.Single().RevertedAt);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void Broken_file_is_set_aside_instead_of_crashing()
    {
        File.WriteAllText(_path, "{ this is not json");
        var journal = new ChangeJournal(_path);

        Assert.Empty(journal.Entries);
        journal.Add(new ChangeEntry { Kind = ChangeKind.Cleanup, Title = "after" });

        Assert.Single(new ChangeJournal(_path).Entries);
        Assert.Single(Directory.GetFiles(_box.Root, "journal.json.broken-*"));
    }
}
