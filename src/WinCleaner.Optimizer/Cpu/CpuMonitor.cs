using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WinCleaner.Optimizer.Cpu;

/// <param name="UsagePercent">CPU 사용률(%). 작업 관리자와 같은 기준(% Processor Utility, 100에서 자름).</param>
/// <param name="Ghz">실제 평균 속도(GHz). 기본 속도 × % Processor Performance. 알 수 없으면 null.</param>
public sealed record CpuSample(double UsagePercent, double? Ghz);

/// <summary>CPU 사용률과 실제 속도를 읽는다. 테스트에서 실제 성능 카운터를 쓰지 않도록 분리했다.</summary>
public interface ICpuMonitor
{
    /// <summary>지난번 호출 뒤 평균값. 첫 호출이나 읽기에 실패하면 null.</summary>
    CpuSample? Sample();
}

/// <summary>
/// Windows 성능 카운터(PDH)로 CPU 사용률과 실제 속도를 읽는다. 한 번에 한 스레드에서만 부른다.
/// 카운터 이름은 Windows 언어와 상관없이 영어 이름으로 추가한다(PdhAddEnglishCounter).
/// </summary>
public sealed partial class PdhCpuMonitor : ICpuMonitor, IDisposable
{
    private const uint FormatDouble = 0x00000200;
    private const uint FormatNoCap100 = 0x00008000;

    private readonly nint _query;
    private readonly nint _utility;
    private readonly nint _performance;
    private readonly double _baseGhz;

    public PdhCpuMonitor()
    {
        Check(PdhOpenQuery(null, 0, out _query), "PdhOpenQuery");
        Check(PdhAddEnglishCounter(_query, @"\Processor Information(_Total)\% Processor Utility", 0, out _utility), "% Processor Utility");
        // 속도 카운터는 없는 Windows도 있다. 그때는 사용률만 보여 준다.
        if (PdhAddEnglishCounter(_query, @"\Processor Information(_Total)\% Processor Performance", 0, out _performance) != 0)
            _performance = 0;
        _baseGhz = ReadBaseMhz() / 1000.0;
        // 사용률은 두 번 읽은 사이의 평균이라 첫 값은 기준점으로만 쓴다.
        PdhCollectQueryData(_query);
    }

    public CpuSample? Sample()
    {
        if (PdhCollectQueryData(_query) != 0 || !TryRead(_utility, out var usage))
            return null;
        double? ghz = _performance != 0 && _baseGhz > 0 && TryRead(_performance, out var performance)
            ? _baseGhz * performance / 100
            : null;
        return new CpuSample(Math.Clamp(usage, 0, 100), ghz);
    }

    public void Dispose() => PdhCloseQuery(_query);

    private static bool TryRead(nint counter, out double value)
    {
        value = 0;
        if (PdhGetFormattedCounterValue(counter, FormatDouble | FormatNoCap100, out _, out var formatted) != 0 || formatted.CStatus > 1)
            return false;
        value = formatted.DoubleValue;
        return true;
    }

    /// <summary>CPU 기본 속도(MHz). i7-8750H는 약 2,200.</summary>
    private static double ReadBaseMhz()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        return key?.GetValue("~MHz") is int mhz ? mhz : 0;
    }

    private static void Check(uint status, string what)
    {
        if (status != 0)
            throw new InvalidOperationException($"성능 카운터를 열지 못했습니다 ({what}, 0x{status:X8}).");
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct FormattedValue
    {
        [FieldOffset(0)]
        public uint CStatus;

        [FieldOffset(8)]
        public double DoubleValue;
    }

    [LibraryImport("pdh.dll", EntryPoint = "PdhOpenQueryW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint PdhOpenQuery(string? dataSource, nint userData, out nint query);

    [LibraryImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint PdhAddEnglishCounter(nint query, string path, nint userData, out nint counter);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhCollectQueryData(nint query);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhGetFormattedCounterValue(nint counter, uint format, out uint type, out FormattedValue value);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhCloseQuery(nint query);
}
