using System.Management;

namespace WinCleaner.Optimizer.Fans;

/// <summary>
/// Clevo 계열 노트북 제조사 WMI 인터페이스(root\WMI:CLEVO_GET). Control Center 2.0이 쓰는 것과 같다.
/// 메서드마다 UInt32 하나(<c>Data</c>)를 주고받는다. 관리자 권한이 필요하다.
/// </summary>
internal static class ClevoWmi
{
    public static T Use<T>(Func<ManagementObject, T> action)
    {
        using var clevo = new ManagementClass(@"\\.\root\WMI", "CLEVO_GET", null);
        using var instances = clevo.GetInstances();
        using var instance = instances.Cast<ManagementObject>().FirstOrDefault()
            ?? throw new InvalidOperationException("이 PC에서 Clevo 팬 인터페이스를 찾지 못했습니다.");
        return action(instance);
    }

    public static uint? Read(ManagementObject instance, string method)
    {
        using var result = instance.InvokeMethod(method, null, null);
        return result?["Data"] is { } data ? Convert.ToUInt32(data) : null;
    }

    public static void Write(ManagementObject instance, string method, uint value)
    {
        using var input = instance.GetMethodParameters(method);
        input["Data"] = value;
        using var result = instance.InvokeMethod(method, input, null);
    }
}
