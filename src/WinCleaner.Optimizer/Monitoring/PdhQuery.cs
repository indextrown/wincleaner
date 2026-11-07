using System.Runtime.InteropServices;

namespace WinCleaner.Optimizer.Monitoring;

/// <summary>
/// Windows 성능 카운터(PDH) 쿼리. 카운터는 Windows 언어와 상관없이 영어 이름으로 추가하고(PdhAddEnglishCounter),
/// <c>*</c>가 들어간 경로는 읽을 때마다 지금 있는 인스턴스(프로세스·그래픽 엔진 등)를 모두 돌려준다.
/// 한 번에 한 스레드에서만 부른다.
/// </summary>
internal sealed partial class PdhQuery : IDisposable
{
    private const uint FormatDouble = 0x00000200;
    private const uint FormatNoCap100 = 0x00008000;
    private const uint MoreData = 0x800007D2;

    private readonly nint _query;

    public PdhQuery()
    {
        if (PdhOpenQuery(null, 0, out _query) != 0)
            throw new InvalidOperationException("성능 카운터 쿼리를 열지 못했습니다.");
    }

    /// <summary>카운터를 추가한다. 이 PC에 없는 카운터면 0.</summary>
    public nint Add(string englishPath) => PdhAddEnglishCounter(_query, englishPath, 0, out var counter) == 0 ? counter : 0;

    public void Remove(nint counter)
    {
        if (counter != 0)
            PdhRemoveCounter(counter);
    }

    /// <summary>모든 카운터의 새 값을 모은다. 비율 카운터(초당 바이트, %)는 두 번째 모은 뒤부터 값이 나온다.</summary>
    public bool Collect() => PdhCollectQueryData(_query) == 0;

    public static double? Read(nint counter)
    {
        if (counter == 0 || PdhGetFormattedCounterValue(counter, FormatDouble | FormatNoCap100, out _, out var value) != 0 || value.CStatus > 1)
            return null;
        return value.DoubleValue;
    }

    /// <summary>와일드카드 카운터의 인스턴스별 값.</summary>
    public static IReadOnlyList<(string Instance, double Value)> ReadArray(nint counter)
    {
        if (counter == 0)
            return [];
        uint size = 0;
        if (PdhGetFormattedCounterArray(counter, FormatDouble | FormatNoCap100, ref size, out _, 0) != MoreData || size == 0)
            return [];

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArray(counter, FormatDouble | FormatNoCap100, ref size, out var count, buffer) != 0)
                return [];

            // PDH_FMT_COUNTERVALUE_ITEM_W: 이름 포인터(8) + CStatus(4) + 맞춤(4) + double(8) = 24바이트.
            const int itemSize = 24;
            var items = new List<(string, double)>((int)count);
            for (var i = 0; i < count; i++)
            {
                var item = buffer + i * itemSize;
                var status = (uint)Marshal.ReadInt32(item, 8);
                if (status > 1)
                    continue;
                var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item)) ?? "";
                items.Add((name, BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item, 16))));
            }
            return items;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public void Dispose() => PdhCloseQuery(_query);

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
    private static partial uint PdhRemoveCounter(nint counter);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhCollectQueryData(nint query);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhGetFormattedCounterValue(nint counter, uint format, out uint type, out FormattedValue value);

    [LibraryImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW")]
    private static partial uint PdhGetFormattedCounterArray(nint counter, uint format, ref uint bufferSize, out uint itemCount, nint items);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhCloseQuery(nint query);
}
