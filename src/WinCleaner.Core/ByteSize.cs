using System.Globalization;

namespace WinCleaner.Core;

public static class ByteSize
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    /// <summary>1024 단위로 읽기 쉬운 크기 문자열을 만든다. 예: 17824112640 → "16.6 GB"</summary>
    public static string Format(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);

        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes} B"
            : $"{value.ToString("0.#", CultureInfo.InvariantCulture)} {Units[unit]}";
    }
}
