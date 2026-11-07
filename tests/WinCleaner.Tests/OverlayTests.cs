using System.Windows;
using WinCleaner.App.Services;
using WinCleaner.App.ViewModels;
using WinCleaner.App.Views;
using WinCleaner.Memory;
using WinCleaner.Optimizer.Fans;
using WinCleaner.Optimizer.Monitoring;

namespace WinCleaner.Tests;

public class OverlayViewModelTests
{
    private static readonly SystemMemory Memory = new(16L << 30, 6L << 30, 3L << 30, 12L << 30, 24L << 30);
    private static readonly DateTime Now = new(2026, 11, 7, 21, 5, 0);

    private static SystemSample Cpu(double usage, double ghz) => new() { CpuUsage = usage, CpuGhz = ghz, PerformanceLimit = 100 };

    [Fact]
    public void Values_are_formatted_for_the_overlay()
    {
        var vm = new OverlayViewModel(new FakeSampler(), _ => { });

        vm.Apply(new OverlaySnapshot(Cpu(37.4, 3.876) with { Gpu = new GpuSample("GTX 1050 Ti", 45.2, 72, 1.392) },
            Memory, FanGuardTests.Reading(celsius: 86, percent: 100)), Now);

        Assert.Equal("21:05", vm.TimeText);
        Assert.Equal("37%", vm.CpuText);
        Assert.Equal("3.9GHz", vm.CpuSpeedText);
        Assert.Equal("86°C", vm.TemperatureText);
        Assert.True(vm.IsWarm);
        Assert.False(vm.IsThrottled);
        Assert.Equal("45%", vm.GpuText);
        Assert.Equal("72°C  1.4GHz", vm.GpuDetailText);
        Assert.Equal("10.0 / 16.0GB", vm.MemoryText);
        Assert.Equal("63%", vm.MemoryPercentText);
        Assert.Equal("6,494 RPM", vm.FanText);
        Assert.True(vm.IsCpuRowVisible && vm.IsGpuRowVisible && vm.IsMemoryRowVisible && vm.IsFanRowVisible && vm.IsClockRowVisible);
    }

    [Fact]
    public void Heat_limit_and_performance_limit_are_shown()
    {
        var vm = new OverlayViewModel(new FakeSampler(), _ => { });

        vm.Apply(new OverlaySnapshot(Cpu(100, 2.6), Memory, FanGuardTests.Reading(celsius: 96, percent: 100)), Now);
        Assert.Equal("열 한계", vm.ThrottleText);
        Assert.True(vm.IsHot && vm.IsThrottleVisible);

        vm.Apply(new OverlaySnapshot(Cpu(50, 1.5) with { PerformanceLimit = 60 }, Memory, FanGuardTests.Reading(celsius: 60, percent: 40)), Now);
        Assert.Equal("속도 제한 60%", vm.ThrottleText);

        vm.Apply(new OverlaySnapshot(Cpu(50, 3.0), Memory, FanGuardTests.Reading(celsius: 60, percent: 40)), Now);
        Assert.False(vm.IsThrottleVisible);
    }

    [Fact]
    public void Idle_gpu_says_resting_and_missing_values_hide_rows()
    {
        var vm = new OverlayViewModel(new FakeSampler(), _ => { });

        vm.Apply(new OverlaySnapshot(new SystemSample { Gpu = new GpuSample("GTX 1050 Ti", 0, null, null) }, null, null), Now);
        Assert.Equal("쉬는 중", vm.GpuDetailText);

        // 실제로 본 상태: 롤 클라이언트가 내장 그래픽을 27% 쓰고 NVIDIA는 쉬는 중.
        vm.Apply(new OverlaySnapshot(new SystemSample { Gpu = new GpuSample("GTX 1050 Ti", 0, null, null, 27.0) }, null, null), Now);
        Assert.Equal("쉬는 중  내장 27%", vm.GpuDetailText);

        vm.Apply(new OverlaySnapshot(new SystemSample { Gpu = new GpuSample("GTX 1050 Ti", 3, 48, 0.3, 0) }, null, null), Now);
        Assert.Equal("48°C  0.3GHz", vm.GpuDetailText);

        // "쉬고 있어도 읽기"를 켰을 때 실제로 본 값: 39°C, 0MHz.
        vm.Apply(new OverlaySnapshot(new SystemSample { Gpu = new GpuSample("GTX 1050 Ti", 0, 39, 0, 0) }, null, null), Now);
        Assert.Equal("39°C", vm.GpuDetailText);

        vm.Apply(new OverlaySnapshot(null, null, null), Now);
        Assert.Equal("–", vm.CpuText);
        Assert.Equal("–", vm.MemoryText);
        Assert.False(vm.IsGpuRowVisible);
        Assert.False(vm.IsFanRowVisible);
        Assert.False(vm.IsTemperatureVisible);
    }

    [Fact]
    public void Optional_rows_show_only_when_turned_on()
    {
        var vm = new OverlayViewModel(new FakeSampler(), _ => { }) { StatusProvider = () => "팬 최대 · 전압 −125mV" };
        var snapshot = new OverlaySnapshot(new SystemSample
        {
            DiskReadBytesPerSecond = 1.5 * (1 << 20), DiskWriteBytesPerSecond = 300 * 1024,
            NetworkReceiveBytesPerSecond = 2 * (1 << 20), NetworkSendBytesPerSecond = 10 * 1024,
            TopCpuProcess = "chrome", TopCpuPercent = 12.4,
        }, Memory, null, ("LeagueClient", 1L << 30));

        vm.Apply(snapshot, Now);
        Assert.False(vm.IsDiskNetworkRowVisible);
        Assert.False(vm.IsTopRowVisible);
        Assert.True(vm.IsStatusRowVisible);
        Assert.Equal("팬 최대 · 전압 −125mV", vm.StatusText);

        vm.ShowDiskNetwork = true;
        vm.ShowTopProcesses = true;
        vm.ShowStatus = false;
        vm.Apply(snapshot, Now);

        Assert.Equal("읽기 1.5MB/s · 쓰기 300KB/s", vm.DiskText);
        Assert.Equal("↓ 2.0MB/s · ↑ 10KB/s", vm.NetworkText);
        Assert.Equal("CPU chrome 12%", vm.TopCpuText);
        Assert.Equal("RAM LeagueClient 1.0GB", vm.TopMemoryText);
        Assert.True(vm.IsDiskNetworkRowVisible && vm.IsTopRowVisible);
        Assert.False(vm.IsStatusRowVisible);
    }

    [Fact]
    public async Task Toggle_shows_the_window_reads_values_and_hides_again()
    {
        var shown = new List<bool>();
        var sampler = new FakeSampler { Snapshot = new OverlaySnapshot(Cpu(50, 3.0), Memory, null) };
        var vm = new OverlayViewModel(sampler, shown.Add);

        vm.ToggleCommand.Execute(null);
        for (var i = 0; i < 200 && vm.CpuText != "50%"; i++)
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

    [Fact]
    public async Task Settings_are_loaded_saved_and_reset()
    {
        using var box = new Sandbox();
        var store = new OverlaySettingsStore(Path.Combine(box.Root, "overlay.json"));
        store.Save(new OverlaySettings { Position = OverlayPosition.TopLeft, ShowGpu = false, MarginY = 120 });

        var vm = new OverlayViewModel(new FakeSampler(), _ => { }, store);
        Assert.Equal(OverlayPosition.TopLeft, vm.Position);
        Assert.False(vm.ShowGpu);
        Assert.Equal(120, vm.MarginY);

        vm.Scale = 1.25;
        vm.ShowDiskNetwork = true;
        for (var i = 0; i < 200 && store.Load().Scale != 1.25; i++)
            await Task.Delay(10);
        Assert.Equal(1.25, store.Load().Scale);
        Assert.True(store.Load().ShowDiskNetwork);

        vm.ResetSettingsCommand.Execute(null);
        Assert.Equal(OverlayPosition.TopRight, vm.Position);
        for (var i = 0; i < 200 && store.Load().Position != OverlayPosition.TopRight; i++)
            await Task.Delay(10);
        Assert.Equal(new OverlaySettings(), store.Load());
    }

    [Fact]
    public void Broken_or_extreme_settings_are_fixed()
    {
        using var box = new Sandbox();
        var path = Path.Combine(box.Root, "overlay.json");
        File.WriteAllText(path, """{ "position": "Nowhere" """);
        Assert.Equal(new OverlaySettings(), new OverlaySettingsStore(path).Load());

        File.WriteAllText(path, """{ "marginX": 99999, "scale": 50, "backgroundOpacity": 0 }""");
        var loaded = new OverlaySettingsStore(path).Load();
        Assert.Equal(800, loaded.MarginX);
        Assert.Equal(2.0, loaded.Scale);
        Assert.Equal(0.1, loaded.BackgroundOpacity);
    }

    [Theory]
    [InlineData(OverlayPosition.TopLeft, 16, 20)]
    [InlineData(OverlayPosition.TopCenter, 860, 20)]
    [InlineData(OverlayPosition.TopRight, 1704, 20)]
    [InlineData(OverlayPosition.BottomLeft, 16, 960)]
    [InlineData(OverlayPosition.BottomRight, 1704, 960)]
    public void Overlay_is_placed_by_position_and_margins(OverlayPosition position, double x, double y)
    {
        // 1920×1080 화면, 오버레이 200×100, 가로 여백 16, 세로 여백 20.
        Assert.Equal(new Point(x, y), OverlayWindow.PlaceOnScreen(position, 16, 20, 200, 100, 1920, 1080));
    }

    [Theory]
    [InlineData(500, "0KB/s")]
    [InlineData(300 * 1024, "300KB/s")]
    [InlineData(1.5 * 1024 * 1024, "1.5MB/s")]
    [InlineData(2.0 * 1024 * 1024 * 1024, "2.0GB/s")]
    public void Rates_use_readable_units(double bytesPerSecond, string expected)
    {
        Assert.Equal(expected, OverlayViewModel.Rate(bytesPerSecond));
    }

    internal sealed class FakeSampler : IOverlaySampler
    {
        public OverlaySnapshot Snapshot { get; set; } = new(null, null, null);
        public List<OverlaySettings> Requests { get; } = [];

        public OverlaySnapshot Sample(OverlaySettings settings)
        {
            lock (Requests)
                Requests.Add(settings);
            return Snapshot;
        }
    }
}

public class OverlaySamplerTests
{
    [Fact]
    public void Only_turned_on_parts_are_read()
    {
        var monitor = new RecordingMonitor();
        var fans = new CountingFans();
        var sampler = new OverlaySampler(monitor, new QuickMemoryOptimizerTests.FakeMemory([]), fans, readFans: true);

        sampler.Sample(new OverlaySettings { ShowGpu = false, ShowTemperature = false, ShowFan = false, ShowThrottle = false });
        Assert.Equal(MonitorParts.Cpu, monitor.Parts[^1]);
        Assert.Equal(0, fans.Calls);

        sampler.Sample(new OverlaySettings { ShowCpu = false, ShowThrottle = false, ShowGpu = true, ShowDiskNetwork = true, ShowTopProcesses = true });
        Assert.Equal(MonitorParts.Gpu | MonitorParts.DiskNetwork | MonitorParts.TopProcess, monitor.Parts[^1]);
        Assert.Equal(1, fans.Calls);

        sampler.Sample(new OverlaySettings { ShowCpu = false, ShowThrottle = false, ReadGpuWhenIdle = true });
        Assert.Equal(MonitorParts.Gpu | MonitorParts.GpuWhenIdle, monitor.Parts[^1]);

        sampler.Sample(new OverlaySettings { ShowCpu = false, ShowThrottle = false, ShowGpu = false, ReadGpuWhenIdle = true });
        Assert.Equal(MonitorParts.None, monitor.Parts[^1]);
    }

    [Fact]
    public void Fans_are_not_read_without_admin()
    {
        var fans = new CountingFans();
        var sampler = new OverlaySampler(null, new QuickMemoryOptimizerTests.FakeMemory([]), fans, readFans: false);

        var snapshot = sampler.Sample(new OverlaySettings());

        Assert.Equal(0, fans.Calls);
        Assert.Null(snapshot.System);
        Assert.NotNull(snapshot.Memory);
    }

    [Fact]
    public void Top_memory_program_adds_up_same_named_processes()
    {
        var top = OverlaySampler.TopMemory(
        [
            new(1, "chrome", 400L << 20, 0), new(2, "chrome", 500L << 20, 0), new(3, "LeagueClient", 800L << 20, 0),
        ]);

        Assert.Equal(("chrome", 900L << 20), top);
        Assert.Null(OverlaySampler.TopMemory([]));
    }

    private sealed class RecordingMonitor : ISystemMonitor
    {
        public List<MonitorParts> Parts { get; } = [];

        public SystemSample Sample(MonitorParts parts)
        {
            Parts.Add(parts);
            return new SystemSample();
        }
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

public class SystemMonitorTests
{
    [Fact]
    public void Gpu_usage_is_grouped_by_adapter_using_the_busiest_engine()
    {
        var usage = PdhSystemMonitor.GpuUsageByAdapter(
        [
            ("pid_100_luid_0x00000000_0x00015aec_phys_0_eng_0_engtype_3D", 30),
            ("pid_200_luid_0x00000000_0x00015aec_phys_0_eng_0_engtype_3D", 25),
            ("pid_200_luid_0x00000000_0x00015aec_phys_0_eng_5_engtype_Copy", 40),
            ("pid_300_luid_0x00000000_0x00015f95_phys_0_eng_0_engtype_3D", 80),
            ("pid_300_luid_0x00000000_0x00015f95_phys_0_eng_0_engtype_3D", 60),
            ("garbage", 99),
        ]);

        Assert.Equal(55, usage["0x00000000_0x00015aec"]);
        Assert.Equal(100, usage["0x00000000_0x00015f95"]);
        Assert.Equal(2, usage.Count);
    }

    [Fact]
    public void Top_process_adds_instances_and_divides_by_cpu_count()
    {
        var top = PdhSystemMonitor.TopProcess(
        [
            ("_Total", 400), ("Idle", 900), ("chrome", 60), ("chrome#1", 36), ("League of Legends", 84),
        ], cpuCount: 12);

        Assert.Equal("chrome", top?.Name);
        Assert.Equal(8, top!.Value.Percent);
        Assert.Null(PdhSystemMonitor.TopProcess([("_Total", 10)], 12));
    }

    [Fact]
    public void Luids_match_performance_counter_names()
    {
        Assert.Equal("0x00000000_0x00015aec", GpuAdapters.FormatLuid(0, 0x15aec));
    }

    [Fact]
    public void Real_counters_and_adapters_can_be_read()
    {
        // 읽기만 한다. 비율 값은 두 번 읽은 사이의 평균이라 조금 기다렸다가 다시 읽는다.
        using var monitor = new PdhSystemMonitor();
        var all = MonitorParts.Cpu | MonitorParts.Gpu | MonitorParts.DiskNetwork | MonitorParts.TopProcess;
        monitor.Sample(all);
        Thread.Sleep(300);

        var sample = monitor.Sample(all);

        Assert.InRange(sample.CpuUsage ?? -1, 0, 100);
        Assert.True(sample.CpuGhz is null or > 0.3 and < 7, $"{sample.CpuGhz}");
        Assert.NotNull(sample.TopCpuProcess);
        Assert.NotNull(sample.DiskReadBytesPerSecond);
        Assert.All(GpuAdapters.List(), a => Assert.Matches("^0x[0-9a-f]{8}_0x[0-9a-f]{8}$", a.Luid));
    }
}
