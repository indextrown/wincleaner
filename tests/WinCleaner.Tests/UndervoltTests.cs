using System.Diagnostics;
using System.Text;
using System.Xml.Linq;
using WinCleaner.App.Services;
using WinCleaner.App.ViewModels;
using WinCleaner.Optimizer.Cpu;
using WinCleaner.Safety;

namespace WinCleaner.Tests;

public class XtuUndervoltTests
{
    [Theory]
    [InlineData(-151)]
    [InlineData(1)]
    public void Offsets_outside_minus_150_to_0_are_rejected(int millivolts)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => XtuUndervolt.Validate(millivolts));
        Assert.Throws<ArgumentOutOfRangeException>(() => XtuUndervolt.BuildToolScript(millivolts));
        Assert.Throws<ArgumentOutOfRangeException>(() => ScheduledTaskUndervoltStartup.BuildTaskXml("PC\\user", millivolts));
    }

    [Fact]
    public void Tool_script_sets_core_and_cache_together_and_reads_with_GetControl()
    {
        var apply = XtuUndervolt.BuildToolScript(-125);
        var read = XtuUndervolt.BuildToolScript(null);

        Assert.Contains("$offset = -125", apply);
        Assert.Contains("$offset = $null", read);
        Assert.Contains("foreach ($id in 34, 79)", apply);
        Assert.Contains("GetControl([uint32]79).ActiveValue", apply);
        Assert.DoesNotContain("GetTuningControlByID", apply);
    }

    [Fact]
    public void Results_and_errors_are_parsed()
    {
        Assert.Equal(new UndervoltState(-125, -125), XtuUndervolt.ParseResult("noise\r\nRESULT core=-125 cache=-125\r\n"));
        Assert.True(XtuUndervolt.ParseResult("RESULT core=0 cache=0").IsDefault);

        var error = Assert.Throws<InvalidOperationException>(() => XtuUndervolt.ParseResult("ERROR 액세스가 거부되었습니다."));
        Assert.Contains("액세스가 거부", error.Message);
        Assert.Throws<InvalidOperationException>(() => XtuUndervolt.ParseResult(""));
    }

    [Fact]
    public void Generated_scripts_are_valid_windows_powershell()
    {
        // 실행하지 않고 Windows PowerShell 5.1 파서로 문법만 확인한다.
        foreach (var script in new[] { XtuUndervolt.BuildToolScript(-125), XtuUndervolt.BuildToolScript(null), ScheduledTaskUndervoltStartup.BuildStartupScript(-125) })
            Assert.Equal("0", ParseErrors(script));
    }

    private static string ParseErrors(string script)
    {
        var check = "$e = $null; [void][Management.Automation.Language.Parser]::ParseInput(" +
                    $"[Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('{XtuUndervolt.EncodeCommand(script)}')), [ref]$null, [ref]$e); $e.Count";
        var startInfo = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
            $"-NoProfile -NonInteractive -EncodedCommand {XtuUndervolt.EncodeCommand(check)}")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        return output;
    }
}

public class UndervoltStartupTaskTests
{
    [Fact]
    public void Task_runs_elevated_at_logon_and_resume_without_storing_a_password()
    {
        var xml = ScheduledTaskUndervoltStartup.BuildTaskXml("HANSUNG\\Index<&>", -125);
        var doc = XDocument.Parse(xml);
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

        Assert.Equal("HANSUNG\\Index<&>", doc.Descendants(ns + "LogonTrigger").Single().Element(ns + "UserId")!.Value);
        Assert.Contains("Power-Troubleshooter", doc.Descendants(ns + "Subscription").Single().Value);
        var principal = doc.Descendants(ns + "Principal").Single();
        Assert.Equal("InteractiveToken", principal.Element(ns + "LogonType")!.Value);
        Assert.Equal("HighestAvailable", principal.Element(ns + "RunLevel")!.Value);
        Assert.Contains("offset=-125mV", doc.Descendants(ns + "Description").Single().Value);
        var command = doc.Descendants(ns + "Command").Single().Value;
        Assert.True(Path.IsPathRooted(command) && File.Exists(command), command);
        Assert.Equal("false", doc.Descendants(ns + "DisallowStartIfOnBatteries").Single().Value);
    }

    [Fact]
    public void Task_script_is_embedded_and_skips_after_a_crash()
    {
        var xml = ScheduledTaskUndervoltStartup.BuildTaskXml("PC\\user", -100);
        var arguments = XDocument.Parse(xml).Descendants().Single(e => e.Name.LocalName == "Arguments").Value;
        var encoded = arguments.Split(' ').Last();
        var script = Encoding.Unicode.GetString(Convert.FromBase64String(encoded));

        Assert.Contains("-EncodedCommand", arguments);
        Assert.Contains("$offset = -100", script);
        Assert.Contains("Id = 41, 6008", script);
        Assert.Contains($"Disable-ScheduledTask -TaskName '{ScheduledTaskUndervoltStartup.TaskName}'", script);
        Assert.Contains("skipped-crash", script);
        // 비정상 종료 확인이 적용보다 먼저 와야 한다.
        Assert.True(script.IndexOf("skipped-crash", StringComparison.Ordinal) < script.IndexOf("$tuning.Tune", StringComparison.Ordinal));
    }

    [Fact]
    public void Task_query_xml_is_parsed()
    {
        var registered = ScheduledTaskUndervoltStartup.BuildTaskXml("PC\\user", -125);
        var state = ScheduledTaskUndervoltStartup.ParseTaskXml(registered, "2026-11-08T09:01:00 applied core=-125 cache=-125");
        Assert.True(state is { IsRegistered: true, IsEnabled: true, Millivolts: -125 });

        var disabled = registered.Replace("<Enabled>true</Enabled>\n    <Hidden>", "<Enabled>false</Enabled>\n    <Hidden>")
            .Replace("<Enabled>true</Enabled>\r\n    <Hidden>", "<Enabled>false</Enabled>\r\n    <Hidden>");
        Assert.False(ScheduledTaskUndervoltStartup.ParseTaskXml(disabled, null).IsEnabled);
    }
}

public sealed class UndervoltViewModelTests : IDisposable
{
    private readonly Sandbox _box = new();
    private readonly FakeUndervolt _undervolt = new();
    private readonly FakeStartup _startup = new();
    private readonly AnswerDialogs _dialogs = new();
    private readonly ChangeJournal _journal;
    private readonly UndervoltViewModel _vm;

    public UndervoltViewModelTests()
    {
        _journal = new ChangeJournal(Path.Combine(_box.Root, "journal.json"));
        _vm = new UndervoltViewModel(_undervolt, _startup, _dialogs, _journal, isElevated: true);
    }

    public void Dispose() => _box.Dispose();

    [Fact]
    public async Task Refresh_shows_current_value_and_auto_apply_state()
    {
        _undervolt.State = new UndervoltState(-125, -125);
        _startup.State = new UndervoltStartupState(true, true, -125, "2026-11-08T09:01:00 applied core=-125 cache=-125");

        await _vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal("지금: 코어·캐시 −125mV", _vm.CurrentText);
        Assert.Equal(-125, _vm.Millivolts);
        Assert.True(_vm.AutoApply);
        Assert.Contains("자동 적용 켜짐", _vm.AutoApplyText);
        Assert.Contains("마지막 적용 11-08 09:01", _vm.AutoApplyText);
        Assert.Empty(_startup.Calls); // 읽기만 했다.
    }

    [Fact]
    public async Task Apply_asks_first_and_records_the_change()
    {
        _vm.Millivolts = -100;
        _dialogs.Answer = false;
        await _vm.ApplyCommand.ExecuteAsync(null);
        Assert.Empty(_undervolt.Applied);

        _dialogs.Answer = true;
        await _vm.ApplyCommand.ExecuteAsync(null);

        Assert.Equal([-100], _undervolt.Applied);
        Assert.Equal("지금: 코어·캐시 −100mV", _vm.CurrentText);
        Assert.Contains(_journal.Entries, e => e.Title == "CPU 전압 낮추기: −100mV");
        Assert.Empty(_startup.Calls); // 자동 적용이 꺼져 있으면 작업은 건드리지 않는다.
    }

    [Fact]
    public async Task Turning_on_auto_apply_registers_the_task_and_new_values_update_it()
    {
        _vm.Millivolts = -125;
        _vm.AutoApply = true;
        await WaitIdleAsync();

        Assert.Equal(["register -125"], _startup.Calls);
        Assert.True(_vm.AutoApply);

        _vm.Millivolts = -100;
        await _vm.ApplyCommand.ExecuteAsync(null);
        Assert.Equal("register -100", _startup.Calls[^1]);

        await _vm.ResetCommand.ExecuteAsync(null);
        Assert.Equal(0, _undervolt.Applied[^1]);
        Assert.Equal("unregister", _startup.Calls[^1]);
        Assert.False(_vm.AutoApply);
    }

    [Fact]
    public async Task Declining_auto_apply_leaves_it_off()
    {
        _dialogs.Answer = false;

        _vm.AutoApply = true;
        await WaitIdleAsync();

        Assert.False(_vm.AutoApply);
        Assert.Empty(_startup.Calls);
    }

    [Fact]
    public async Task Failures_are_explained()
    {
        _undervolt.Fail = true;

        await _vm.ApplyCommand.ExecuteAsync(null);

        Assert.Contains("바꾸지 못했습니다", _vm.MessageText);
        Assert.Empty(_journal.Entries);
    }

    [Fact]
    public async Task Without_admin_nothing_is_called()
    {
        var vm = new UndervoltViewModel(_undervolt, _startup, _dialogs, _journal, isElevated: false);

        await vm.RefreshCommand.ExecuteAsync(null);
        await vm.ApplyCommand.ExecuteAsync(null);

        Assert.Contains("관리자 권한", vm.CurrentText);
        Assert.Equal(0, _undervolt.Reads);
        Assert.Empty(_undervolt.Applied);
    }

    [Theory]
    [InlineData("2026-11-08T09:01:00 skipped-crash", "비정상 종료")]
    [InlineData("2026-11-08T09:01:00 error 액세스 거부", "자동 적용 실패: 액세스 거부")]
    [InlineData("2026-11-08T09:01:00 applied core=-125 cache=-125", "마지막 적용 11-08 09:01")]
    public void Last_auto_apply_result_is_described(string line, string expected)
    {
        Assert.Contains(expected, UndervoltViewModel.DescribeLastRun(line));
        Assert.Null(UndervoltViewModel.DescribeLastRun(null));
    }

    [Fact]
    public void Task_switched_off_after_a_crash_is_shown_as_off()
    {
        var text = UndervoltViewModel.DescribeStartup(new UndervoltStartupState(true, false, -125, "2026-11-08T09:01:00 skipped-crash"));

        Assert.Contains("꺼져 있습니다", text);
        Assert.Contains("한 단계 덜 낮춰", text);
    }

    private async Task WaitIdleAsync()
    {
        for (var i = 0; i < 100 && (_vm.IsBusy || _startup.Pending > 0); i++)
            await Task.Delay(10);
    }

    private sealed class FakeUndervolt : IUndervolt
    {
        public UndervoltState State { get; set; } = new(0, 0);
        public List<int> Applied { get; } = [];
        public int Reads { get; private set; }
        public bool Fail { get; set; }

        public bool IsAvailable => true;

        public Task<UndervoltState> ReadAsync(CancellationToken ct)
        {
            Reads++;
            return Task.FromResult(State);
        }

        public Task<UndervoltState> ApplyAsync(int millivolts, CancellationToken ct)
        {
            if (Fail)
                throw new InvalidOperationException("XTU 오류");
            Applied.Add(millivolts);
            State = new UndervoltState(millivolts, millivolts);
            return Task.FromResult(State);
        }
    }

    private sealed class FakeStartup : IUndervoltStartup
    {
        public UndervoltStartupState State { get; set; } = new(false, false, null, null);
        public List<string> Calls { get; } = [];
        public int Pending { get; private set; }

        public Task<UndervoltStartupState> ReadAsync(CancellationToken ct) => Task.FromResult(State);

        public async Task RegisterAsync(int millivolts, CancellationToken ct)
        {
            Pending++;
            await Task.Yield();
            Calls.Add($"register {millivolts}");
            State = new UndervoltStartupState(true, true, millivolts, null);
            Pending--;
        }

        public Task UnregisterAsync(CancellationToken ct)
        {
            Calls.Add("unregister");
            State = new UndervoltStartupState(false, false, null, null);
            return Task.CompletedTask;
        }
    }

    private sealed class AnswerDialogs : IUserDialogs
    {
        public bool Answer { get; set; } = true;

        public bool Confirm(string title, string message) => Answer;

        public void OpenFile(string path)
        {
        }

        public void ShowInExplorer(string path)
        {
        }

        public bool MoveToRecycleBin(string path, out string? error)
        {
            error = null;
            return false;
        }
    }
}
