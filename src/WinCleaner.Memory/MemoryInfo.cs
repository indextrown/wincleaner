using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WinCleaner.Memory;

public sealed record MemoryStatus(long TotalBytes, long AvailableBytes)
{
    public long UsedBytes => TotalBytes - AvailableBytes;

    public double UsedRatio => TotalBytes == 0 ? 0 : (double)UsedBytes / TotalBytes;
}

public static class MemoryInfo
{
    /// <summary>물리 메모리 현황. 사용 가능 메모리에는 대기 목록(캐시)도 포함된다.</summary>
    public static MemoryStatus GetStatus()
    {
        var status = new NativeMethods.MemoryStatusEx
        {
            Length = (uint)Marshal.SizeOf<NativeMethods.MemoryStatusEx>(),
        };

        if (!NativeMethods.GlobalMemoryStatusEx(ref status))
            throw new Win32Exception(Marshal.GetLastPInvokeError());

        return new MemoryStatus((long)status.TotalPhys, (long)status.AvailPhys);
    }
}
