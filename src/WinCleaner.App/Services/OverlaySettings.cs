using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinCleaner.App.Services;

public enum OverlayPosition
{
    TopLeft,
    TopCenter,
    TopRight,
    BottomLeft,
    BottomRight,
}

/// <summary>오버레이에 무엇을 어디에 보일지. 게임 모드 화면에서 바꾸고 %LOCALAPPDATA%\WinCleaner\overlay.json에 저장한다.</summary>
public sealed record OverlaySettings
{
    public bool ShowClock { get; init; } = true;
    public bool ShowCpu { get; init; } = true;
    public bool ShowTemperature { get; init; } = true;
    public bool ShowThrottle { get; init; } = true;
    public bool ShowGpu { get; init; } = true;
    public bool ShowMemory { get; init; } = true;
    public bool ShowFan { get; init; } = true;
    public bool ShowStatus { get; init; } = true;
    public bool ShowTopProcesses { get; init; }
    public bool ShowDiskNetwork { get; init; }

    public OverlayPosition Position { get; init; } = OverlayPosition.TopRight;

    /// <summary>화면 가장자리에서 띄울 거리(가로·세로, 화면 단위 px).</summary>
    public int MarginX { get; init; } = 16;

    public int MarginY { get; init; } = 16;

    /// <summary>글자·상자 크기 배율.</summary>
    public double Scale { get; init; } = 1.0;

    /// <summary>배경 불투명도(0.1~1). 낮을수록 게임 화면이 비쳐 보인다.</summary>
    public double BackgroundOpacity { get; init; } = 0.72;

    /// <summary>파일을 손으로 고쳐도 화면 밖으로 나가거나 보이지 않게 되지 않도록 값을 맞춘다.</summary>
    public OverlaySettings Clamped() => this with
    {
        Position = Enum.IsDefined(Position) ? Position : OverlayPosition.TopRight,
        MarginX = Math.Clamp(MarginX, 0, 800),
        MarginY = Math.Clamp(MarginY, 0, 800),
        Scale = Math.Clamp(Scale, 0.7, 2.0),
        BackgroundOpacity = Math.Clamp(BackgroundOpacity, 0.1, 1.0),
    };
}

public sealed class OverlaySettingsStore(string path)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinCleaner", "overlay.json");

    public OverlaySettings Load()
    {
        try
        {
            return File.Exists(path)
                ? (JsonSerializer.Deserialize<OverlaySettings>(File.ReadAllText(path), JsonOptions) ?? new OverlaySettings()).Clamped()
                : new OverlaySettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // 망가진 파일은 무시하고 기본값을 쓴다. 다음에 저장할 때 새로 쓴다.
            return new OverlaySettings();
        }
    }

    public void Save(OverlaySettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings.Clamped(), JsonOptions));
        File.Move(temp, path, overwrite: true);
    }
}
