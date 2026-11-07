using WinCleaner.App.Services;
using WinCleaner.App.ViewModels;
using WinCleaner.Memory;
using WinCleaner.Optimizer.Cpu;
using WinCleaner.Optimizer.Fans;

namespace WinCleaner.Tests;

public class OverlayViewModelTests
{
    private static readonly SystemMemory Memory = new(16L << 30, 6L << 30, 3L << 30, 12L << 30, 24L << 30);

    [Fact]
    public void Values_are_formatted_for_the_overlay()
    {
        var vm = new OverlayViewModel(new FakeSampler(), _ => { });

        vm.Apply(new OverlaySnapshot(new CpuSample(37.4, 3.876), Memory, FanGuardTests.Reading(celsius: 86, percent: 100)));

        Assert.Equal("37%", vm.CpuText);
        Assert.Equal("3.9GHz", vm.ClockText);
        Assert.Equal("86°C", vm.TemperatureText);
        Assert.True(vm.IsWarm);
        Assert.False(vm.IsHot);
        Assert.Equal("10.0 / 16.0GB", vm.MemoryText);
        Assert.Equal("63%", vm.MemoryPercentText);
        Assert.True(vm.HasFan);
        Assert.Equal("6,494 RPM", vm.FanText);
    }

    [Fact]
    public void Missing_values_are_left_blank()
    {
        var vm = new OverlayViewModel(new FakeSampler(), _ => { });

        vm.Apply(new OverlaySnapshot(null, null, null));

        Assert.Equal("–", vm.CpuText);
        Assert.Equal("", vm.ClockText);
        Assert.Equal("", vm.TemperatureText);
        Assert.Equal("–", vm.MemoryText);
        Assert.False(vm.HasFan);
    }

    [Fact]
    public void Overheating_is_marked_hot()
    {
        var vm = new OverlayViewModel(new FakeSampler(), _ => { });

        vm.Apply(new OverlaySnapshot(new CpuSample(90, 2.6), Memory, FanGuardTests.Reading(celsius: 97, percent: 100)));

        Assert.True(vm.IsHot);
        Assert.False(vm.IsWarm);
    }

    [Fact]
    public async Task Toggle_shows_the_window_reads_values_and_hides_again()
    {
        var shown = new List<bool>();
        var sampler = new FakeSampler { Snapshot = new OverlaySnapshot(new CpuSample(50, 3.0), Memory, null) };
        var vm = new OverlayViewModel(sampler, shown.Add);

        vm.ToggleCommand.Execute(null);
        for (var i = 0; i < 100 && sampler.Calls == 0; i++)
            await Task.Delay(10);
        for (var i = 0; i < 100 && vm.CpuText != "50%"; i++)
            await Task.Delay(10);

        Assert.Equal([true], shown);
        Assert.Equal("오버레이 숨기기", vm.ToggleText);
        Assert.Equal("50%", vm.CpuText);

        vm.ToggleCommand.Execute(null);

        Assert.Equal([true, false], shown);
        Assert.Equal("오버레이 보이기", vm.ToggleText);
    }

    [Fact]
    public void Hotkey_failure_is_explained()
    {
        var vm = new OverlayViewModel(new FakeSampler(), _ => { });

        vm.SetHotkeyAvailable(false);

        Assert.Contains("등록하지 못했습니다", vm.HotkeyStatusText);
    }

    internal sealed class FakeSampler : IOverlaySampler
    {
        public OverlaySnapshot Snapshot { get; set; } = new(null, null, null);
        public int Calls { get; private set; }

        public OverlaySnapshot Sample()
        {
            Calls++;
            return Snapshot;
        }
    }
}

public class OverlaySamplerTests
{
    [Fact]
    public void Fans_are_not_read_without_admin()
    {
        var fans = new CountingFans();
        var sampler = new OverlaySampler(null, new QuickMemoryOptimizerTests.FakeMemory([]), fans, readFans: false);

        var snapshot = sampler.Sample();

        Assert.Equal(0, fans.Calls);
        Assert.Null(snapshot.Cpu);
        Assert.NotNull(snapshot.Memory);
    }

    [Fact]
    public void Real_cpu_counters_give_usage_and_speed()
    {
        // 읽기만 한다. 사용률은 두 번 읽은 사이의 평균이라 조금 기다린 뒤 읽는다.
        using var monitor = new PdhCpuMonitor();
        Thread.Sleep(300);

        var sample = monitor.Sample();

        Assert.NotNull(sample);
        Assert.InRange(sample.UsagePercent, 0, 100);
        Assert.True(sample.Ghz is null or > 0.3 and < 7, $"{sample.Ghz}");
    }

    private sealed class CountingFans : IFanMonitor
    {
        public int Calls { get; private set; }

        public FanReading? Read()
        {
            Calls++;
            return null;
        }
    }
}
