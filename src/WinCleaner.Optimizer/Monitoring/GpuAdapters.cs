using System.Runtime.InteropServices;

namespace WinCleaner.Optimizer.Monitoring;

/// <param name="Luid">성능 카운터 인스턴스 이름에 쓰이는 형식. 예: <c>0x00000000_0x00015aec</c>.</param>
public sealed record GpuAdapter(string Name, uint VendorId, string Luid)
{
    public const uint NvidiaVendorId = 0x10DE;

    public bool IsNvidia => VendorId == NvidiaVendorId;
}

/// <summary>
/// 그래픽 어댑터 목록(DXGI). 성능 카운터는 어댑터를 LUID로만 구분해서 이름을 붙이는 데 쓴다.
/// 어댑터 정보만 읽고 장치를 만들지 않으므로 꺼져 있는 그래픽카드(옵티머스)를 깨우지 않는다.
/// </summary>
public static class GpuAdapters
{
    private const uint SoftwareAdapterFlag = 2;
    private const uint MicrosoftVendorId = 0x1414;

    public static IReadOnlyList<GpuAdapter> List()
    {
        var adapters = new List<GpuAdapter>();
        var iid = typeof(IDxgiFactory1).GUID;
        if (CreateDXGIFactory1(ref iid, out var factory) != 0)
            return adapters;
        try
        {
            for (uint i = 0; factory.EnumAdapters1(i, out var adapter) == 0; i++)
            {
                try
                {
                    if (adapter.GetDesc1(out var desc) != 0 || (desc.Flags & SoftwareAdapterFlag) != 0 || desc.VendorId == MicrosoftVendorId)
                        continue;
                    adapters.Add(new GpuAdapter(desc.Description.Trim(), desc.VendorId, FormatLuid(desc.LuidHigh, desc.LuidLow)));
                }
                finally
                {
                    Marshal.ReleaseComObject(adapter);
                }
            }
        }
        finally
        {
            Marshal.ReleaseComObject(factory);
        }
        return adapters;
    }

    public static string FormatLuid(int high, uint low) => $"0x{(uint)high:x8}_0x{low:x8}";

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IDxgiFactory1 factory);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct AdapterDesc1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;

        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public uint LuidLow;
        public int LuidHigh;
        public uint Flags;
    }

    // vtable 순서만 맞으면 되므로 쓰지 않는 메서드는 자리만 채운다.
    [ComImport]
    [Guid("770aae78-f26f-4dba-a829-253c83d1b387")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDxgiFactory1
    {
        void SetPrivateData();
        void SetPrivateDataInterface();
        void GetPrivateData();
        void GetParent();
        void EnumAdapters();
        void MakeWindowAssociation();
        void GetWindowAssociation();
        void CreateSwapChain();
        void CreateSoftwareAdapter();

        [PreserveSig]
        int EnumAdapters1(uint index, out IDxgiAdapter1 adapter);
    }

    [ComImport]
    [Guid("29038f61-3839-4626-91fd-086879011a05")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDxgiAdapter1
    {
        void SetPrivateData();
        void SetPrivateDataInterface();
        void GetPrivateData();
        void GetParent();
        void EnumOutputs();
        void GetDesc();
        void CheckInterfaceSupport();

        [PreserveSig]
        int GetDesc1(out AdapterDesc1 desc);
    }
}
