using System.Runtime.InteropServices;

namespace WinCleaner.Optimizer.Monitoring;

/// <summary>
/// NVIDIA 드라이버에 들어 있는 NVML(System32\nvml.dll)로 그래픽카드 온도와 속도를 읽는다.
/// 옵티머스 노트북은 게임을 안 할 때 그래픽카드를 꺼 두는데, NVML로 물어보면 깨어나 열과 전기를 더 쓴다.
/// 그래서 그래픽카드가 이미 일하고 있을 때(<c>active</c>)만 읽고, 10번 연달아 쉬고 있으면 NVML을 닫는다.
/// </summary>
internal sealed partial class NvidiaGpu : IDisposable
{
    private const int IdleSamplesBeforeRelease = 10;

    private bool _initialized;
    private bool _unavailable;
    private nint _device;
    private int _idle;

    public (int? Celsius, double? Ghz) Read(bool active)
    {
        if (!active)
        {
            if (_initialized && ++_idle >= IdleSamplesBeforeRelease)
                Release();
            return (null, null);
        }

        _idle = 0;
        if (_unavailable || (!_initialized && !Initialize()))
            return (null, null);

        int? celsius = nvmlDeviceGetTemperature(_device, 0, out var temperature) == 0 ? (int)temperature : null;
        double? ghz = nvmlDeviceGetClockInfo(_device, 0, out var mhz) == 0 ? mhz / 1000.0 : null;
        return (celsius, ghz);
    }

    public void Dispose() => Release();

    private bool Initialize()
    {
        try
        {
            if (nvmlInit() != 0)
            {
                _unavailable = true;
                return false;
            }
            if (nvmlDeviceGetHandleByIndex(0, out _device) != 0)
            {
                nvmlShutdown();
                _unavailable = true;
                return false;
            }
            _initialized = true;
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // NVIDIA 그래픽카드가 없는 PC.
            _unavailable = true;
            return false;
        }
    }

    private void Release()
    {
        if (!_initialized)
            return;
        nvmlShutdown();
        _initialized = false;
        _idle = 0;
    }

    [LibraryImport("nvml.dll", EntryPoint = "nvmlInit_v2")]
    private static partial int nvmlInit();

    [LibraryImport("nvml.dll")]
    private static partial int nvmlShutdown();

    [LibraryImport("nvml.dll", EntryPoint = "nvmlDeviceGetHandleByIndex_v2")]
    private static partial int nvmlDeviceGetHandleByIndex(uint index, out nint device);

    [LibraryImport("nvml.dll")]
    private static partial int nvmlDeviceGetTemperature(nint device, int sensor, out uint celsius);

    [LibraryImport("nvml.dll")]
    private static partial int nvmlDeviceGetClockInfo(nint device, int clockType, out uint mhz);
}
