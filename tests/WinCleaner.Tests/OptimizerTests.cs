using WinCleaner.Core.Optimization;
using WinCleaner.Optimizer;
using WinCleaner.Optimizer.Drives;
using WinCleaner.Optimizer.Startup;

namespace WinCleaner.Tests;

public class StartupTests
{
    private readonly FakeStartupStore _store = new();

    [Fact]
    public void Approval_bytes_follow_task_manager_format()
    {
        Assert.True(StartupApproval.IsEnabled(null));
        Assert.True(StartupApproval.IsEnabled([0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]));
        Assert.True(StartupApproval.IsEnabled([0x06, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]));
        Assert.False(StartupApproval.IsEnabled([0x03, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8]));
        Assert.False(StartupApproval.IsEnabled([0x07, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8]));

        var disabled = StartupApproval.Disabled(new DateTime(2026, 10, 25, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(12, disabled.Length);
        Assert.Equal(0x03, disabled[0]);
        Assert.Equal(new DateTime(2026, 10, 25, 0, 0, 0, DateTimeKind.Utc), DateTime.FromFileTimeUtc(BitConverter.ToInt64(disabled, 4)));
    }

    [Fact]
    public void Manager_lists_entries_with_their_state()
    {
        _store.Add(StartupLocation.UserRun, "Discord", @"""C:\Apps\Discord\Update.exe"" --processStart Discord.exe");
        _store.Add(StartupLocation.MachineRun32, "Steam", @"D:\steam\steam.exe -silent", StartupApproval.Disabled(DateTime.UtcNow));
        _store.Add(StartupLocation.UserFolder, "OneNote.lnk", @"C:\Startup\OneNote.lnk");

        var entries = new StartupManager(_store).List();

        Assert.Equal(3, entries.Count);
        var discord = entries.Single(e => e.Name == "Discord");
        Assert.True(discord.IsEnabled);
        Assert.False(discord.IsMachineWide);
        Assert.Equal(@"C:\Apps\Discord\Update.exe", discord.ExecutablePath);
        var steam = entries.Single(e => e.Name == "Steam");
        Assert.False(steam.IsEnabled);
        Assert.True(steam.IsMachineWide);
        Assert.Equal(@"D:\steam\steam.exe", steam.ExecutablePath);
        Assert.Equal("OneNote", entries.Single(e => e.IsFolderItem).DisplayName);
    }

    [Fact]
    public async Task Disabling_and_reverting_restores_the_original_value()
    {
        var original = StartupApproval.Enabled();
        original[0] = 0x06;
        _store.Add(StartupLocation.UserRun, "App", "C:\\app.exe", original);
        var tweak = new StartupEntryTweak(_store, StartupLocation.UserRun, "App");

        var backup = await tweak.ApplyAsync(default);
        Assert.True((await tweak.GetStateAsync(default)).IsApplied);

        await tweak.RevertAsync(backup, default);
        Assert.Equal(original, _store.ReadApproval(StartupLocation.UserRun, "App"));
    }

    [Fact]
    public async Task Reverting_an_entry_that_had_no_value_removes_the_value()
    {
        _store.Add(StartupLocation.UserRun, "App", "C:\\app.exe");
        var tweak = new StartupEntryTweak(_store, StartupLocation.UserRun, "App");

        var backup = await tweak.ApplyAsync(default);
        await tweak.RevertAsync(backup, default);

        Assert.Null(_store.ReadApproval(StartupLocation.UserRun, "App"));
    }

    [Fact]
    public async Task Tampered_backup_turns_entry_on_instead_of_writing_unknown_bytes()
    {
        _store.Add(StartupLocation.UserRun, "App", "C:\\app.exe", StartupApproval.Disabled(DateTime.UtcNow));
        var tweak = new StartupEntryTweak(_store, StartupLocation.UserRun, "App");
        var tampered = new TweakBackup(tweak.Id, new Dictionary<string, string?> { ["approval"] = "FFFF" });

        await tweak.RevertAsync(tampered, default);

        Assert.Equal(StartupApproval.Enabled(), _store.ReadApproval(StartupLocation.UserRun, "App"));
    }

    [Theory]
    [InlineData(StartupLocation.UserRun, "Discord", false)]
    [InlineData(StartupLocation.MachineRun32, "name:with:colons", true)]
    [InlineData(StartupLocation.UserFolder, "OneNote(으)로 보내기.lnk", false)]
    public void Tweak_id_round_trips_and_reports_elevation(StartupLocation location, string name, bool needsAdmin)
    {
        var tweak = new StartupEntryTweak(_store, location, name);

        Assert.True(StartupEntryTweak.TryParseId(tweak.Id, out var parsedLocation, out var parsedName));
        Assert.Equal(location, parsedLocation);
        Assert.Equal(name, parsedName);
        Assert.Equal(needsAdmin, tweak.RequiresElevation);
    }

    [Theory]
    [InlineData("hibernate")]
    [InlineData("startup:")]
    [InlineData("startup:Nowhere:App")]
    [InlineData("startup:UserRun:")]
    [InlineData("startup:99:App")]
    public void Invalid_ids_are_rejected(string id)
    {
        Assert.False(StartupEntryTweak.TryParseId(id, out _, out _));
    }

    [Fact]
    public void Real_store_lists_this_pcs_startup_entries_without_changing_them()
    {
        var entries = new StartupManager(new RegistryStartupStore()).List();

        Assert.All(entries, e => Assert.False(string.IsNullOrEmpty(e.Name)));
    }
}

public class PowerAndVisualTests
{
    private static readonly PowerScheme Balanced = new(PowerPlanTweak.Balanced, "균형 조정");
    private static readonly PowerScheme High = new(Guid.NewGuid(), "고성능");

    [Fact]
    public async Task Power_plan_switches_and_reverts_to_previous_scheme()
    {
        var schemes = new FakePowerSchemes([Balanced, High], Balanced.Id);
        var tweak = new PowerPlanTweak(schemes, High);

        Assert.Equal("현재: 균형 조정", (await tweak.GetStateAsync(default)).StatusText);
        var backup = await tweak.ApplyAsync(default);
        Assert.Equal(High.Id, schemes.Active);
        Assert.True((await tweak.GetStateAsync(default)).IsApplied);

        await new PowerPlanTweak(schemes, null).RevertAsync(backup, default);
        Assert.Equal(Balanced.Id, schemes.Active);
    }

    [Fact]
    public async Task Power_plan_revert_ignores_schemes_that_do_not_exist()
    {
        var schemes = new FakePowerSchemes([Balanced, High], High.Id);
        var tampered = new TweakBackup(PowerPlanTweak.TweakId, new Dictionary<string, string?> { ["scheme"] = Guid.NewGuid().ToString() });

        await new PowerPlanTweak(schemes, null).RevertAsync(tampered, default);

        Assert.Equal(Balanced.Id, schemes.Active);
    }

    [Fact]
    public async Task Visual_effects_turn_off_and_restore_individually()
    {
        var effects = new FakeVisualEffects { Preset = null };
        effects.Values[VisualEffect.DropShadow] = false;
        var tweak = new VisualEffectsTweak(effects);

        var backup = await tweak.ApplyAsync(default);
        Assert.All(Enum.GetValues<VisualEffect>(), e => Assert.False(effects.Get(e)));
        Assert.Equal(3, effects.Preset);
        Assert.True((await tweak.GetStateAsync(default)).IsApplied);

        await tweak.RevertAsync(backup, default);
        Assert.False(effects.Get(VisualEffect.DropShadow));
        Assert.True(effects.Get(VisualEffect.MenuAnimation));
        Assert.Null(effects.Preset);
    }

    [Fact]
    public async Task Visual_effects_revert_without_backup_turns_everything_on()
    {
        var effects = new FakeVisualEffects { Preset = 3 };
        foreach (var effect in Enum.GetValues<VisualEffect>())
            effects.Values[effect] = false;

        await new VisualEffectsTweak(effects).RevertAsync(null, default);

        Assert.All(Enum.GetValues<VisualEffect>(), e => Assert.True(effects.Get(e)));
        Assert.Null(effects.Preset);
    }

    [Fact]
    public void Real_power_and_visual_settings_can_be_read()
    {
        var schemes = new PowrProfSchemes();
        var list = schemes.List();

        Assert.Contains(list, s => s.Id == schemes.GetActive());
        Assert.All(list, s => Assert.False(string.IsNullOrWhiteSpace(s.Name)));

        var effects = new SystemVisualEffects();
        foreach (var effect in Enum.GetValues<VisualEffect>())
            effects.Get(effect);
    }

    private sealed class FakePowerSchemes(IReadOnlyList<PowerScheme> schemes, Guid active) : IPowerSchemes
    {
        public Guid Active { get; private set; } = active;
        public IReadOnlyList<PowerScheme> List() => schemes;
        public Guid GetActive() => Active;
        public void SetActive(Guid scheme) => Active = scheme;
    }

    private sealed class FakeVisualEffects : IVisualEffects
    {
        public Dictionary<VisualEffect, bool> Values { get; } = Enum.GetValues<VisualEffect>().ToDictionary(e => e, _ => true);
        public int? Preset { get; set; }

        public bool Get(VisualEffect effect) => Values[effect];
        public void Set(VisualEffect effect, bool enabled) => Values[effect] = enabled;
        public int? ReadPreset() => Preset;
        public void WritePreset(int? value) => Preset = value;
    }
}

public class DriveTests
{
    [Fact]
    public void Storage_codes_map_to_media_and_health()
    {
        Assert.Equal(MediaKind.Hdd, DriveMaintenance.ToMedia(3));
        Assert.Equal(MediaKind.Ssd, DriveMaintenance.ToMedia(4));
        Assert.Equal(MediaKind.Unknown, DriveMaintenance.ToMedia(0));
        Assert.Equal(DiskHealth.Healthy, DriveMaintenance.ToHealth(0));
        Assert.Equal(DiskHealth.Unhealthy, DriveMaintenance.ToHealth(2));
    }

    [Fact]
    public void Defrag_uses_media_aware_optimization()
    {
        Assert.Equal("C: /O /U", DriveMaintenance.DefragArguments('C'));
    }

    [Fact]
    public void Real_drives_include_the_system_drive()
    {
        var system = Path.GetPathRoot(Environment.SystemDirectory)![0];

        Assert.Contains(new DriveMaintenance().ReadDrives(), d => d.Letter == system);
    }
}

internal sealed class FakeStartupStore : IStartupStore
{
    private readonly List<(StartupLocation Location, string Name, string Command)> _entries = [];
    private readonly Dictionary<(StartupLocation, string), byte[]> _approvals = [];

    public void Add(StartupLocation location, string name, string command, byte[]? approval = null)
    {
        _entries.Add((location, name, command));
        if (approval is not null)
            _approvals[(location, name)] = approval;
    }

    public IEnumerable<(string Name, string Command)> ReadEntries(StartupLocation location) =>
        _entries.Where(e => e.Location == location).Select(e => (e.Name, e.Command));

    public byte[]? ReadApproval(StartupLocation location, string name) => _approvals.GetValueOrDefault((location, name));

    public void WriteApproval(StartupLocation location, string name, byte[] value) => _approvals[(location, name)] = value;

    public void DeleteApproval(StartupLocation location, string name) => _approvals.Remove((location, name));
}
