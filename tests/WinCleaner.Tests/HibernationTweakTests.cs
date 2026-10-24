using WinCleaner.Core.Optimization;
using WinCleaner.Optimizer;

namespace WinCleaner.Tests;

public class HibernationTweakTests
{
    [Fact]
    public async Task Apply_turns_hibernation_off_and_backs_up_previous_settings()
    {
        var control = new FakeHibernation(new HibernationStatus(true, HiberFileType.Reduced, 6_800_000_000));
        var tweak = new HibernationTweak(control);

        var before = await tweak.GetStateAsync(default);
        var backup = await tweak.ApplyAsync(default);

        Assert.False(before.IsApplied);
        Assert.Contains("6.3 GB", before.StatusText);
        Assert.False(control.Status.Enabled);
        Assert.Equal("true", backup.Values["enabled"]);
        Assert.Equal("Reduced", backup.Values["fileType"]);
        Assert.True((await tweak.GetStateAsync(default)).IsApplied);
    }

    [Fact]
    public async Task Revert_restores_backed_up_file_type()
    {
        var control = new FakeHibernation(new HibernationStatus(true, HiberFileType.Reduced, 100));
        var tweak = new HibernationTweak(control);
        var backup = await tweak.ApplyAsync(default);

        await tweak.RevertAsync(backup, default);

        Assert.True(control.Status.Enabled);
        Assert.Equal(HiberFileType.Reduced, control.Status.FileType);
        Assert.Equal(["off", "on", "type Reduced"], control.Calls);
    }

    [Fact]
    public async Task Revert_without_backup_turns_hibernation_on_with_windows_default_type()
    {
        var control = new FakeHibernation(new HibernationStatus(false, null, null));

        await new HibernationTweak(control).RevertAsync(null, default);

        Assert.Equal(["on"], control.Calls);
    }

    [Fact]
    public async Task Apply_when_already_off_changes_nothing_and_revert_keeps_it_off()
    {
        var control = new FakeHibernation(new HibernationStatus(false, null, null));
        var tweak = new HibernationTweak(control);

        var backup = await tweak.ApplyAsync(default);
        await tweak.RevertAsync(backup, default);

        Assert.Empty(control.Calls);
    }

    [Fact]
    public async Task Revert_ignores_unknown_backup_values()
    {
        var control = new FakeHibernation(new HibernationStatus(false, null, null));
        var tampered = new TweakBackup("hibernate", new Dictionary<string, string?>
        {
            ["enabled"] = "maybe",
            ["fileType"] = "99",
        });

        await new HibernationTweak(control).RevertAsync(tampered, default);

        Assert.Equal(["on"], control.Calls);
    }

    [Fact]
    public async Task Revert_rejects_backup_of_another_tweak()
    {
        var tweak = new HibernationTweak(new FakeHibernation(new HibernationStatus(false, null, null)));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            tweak.RevertAsync(new TweakBackup("other", new Dictionary<string, string?>()), default));
    }

    [Fact]
    public void Real_control_reads_current_state_without_changing_it()
    {
        var status = new PowerCfgHibernationControl().Read();

        // 이 PC의 실제 설정을 읽기만 한다. hiberfil.sys가 있으면 켜진 상태여야 한다.
        if (status.FileBytes is not null)
            Assert.True(status.Enabled);
    }

    private sealed class FakeHibernation(HibernationStatus initial) : IHibernationControl
    {
        public HibernationStatus Status { get; private set; } = initial;
        public List<string> Calls { get; } = [];

        public HibernationStatus Read() => Status;

        public Task SetEnabledAsync(bool enabled, CancellationToken ct)
        {
            Calls.Add(enabled ? "on" : "off");
            Status = enabled
                ? Status with { Enabled = true, FileBytes = 100 }
                : Status with { Enabled = false, FileBytes = null };
            return Task.CompletedTask;
        }

        public Task SetFileTypeAsync(HiberFileType type, CancellationToken ct)
        {
            Calls.Add($"type {type}");
            Status = Status with { FileType = type };
            return Task.CompletedTask;
        }
    }
}
