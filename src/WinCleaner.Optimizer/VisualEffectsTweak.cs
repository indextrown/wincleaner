using System.Runtime.InteropServices;
using Microsoft.Win32;
using WinCleaner.Core;
using WinCleaner.Core.Optimization;

namespace WinCleaner.Optimizer;

/// <summary>
/// 끄는 시각 효과. 시스템 속성 > 성능 옵션의 항목 중 체감 속도에 영향을 주는 것만 골랐다.
/// 글꼴 다듬기와 아이콘 대신 미리보기 표시는 꺼도 빨라지지 않고 보기만 나빠져서 건드리지 않는다.
/// </summary>
public enum VisualEffect
{
    WindowAnimation,
    MenuAnimation,
    ComboBoxAnimation,
    ListBoxSmoothScrolling,
    TooltipAnimation,
    SelectionFade,
    CursorShadow,
    DropShadow,
    ClientAreaAnimation,
}

/// <summary>시각 효과 설정을 읽고 바꾸는 부분. 테스트에서 실제 설정을 건드리지 않도록 분리했다.</summary>
public interface IVisualEffects
{
    bool Get(VisualEffect effect);

    void Set(VisualEffect effect, bool enabled);

    /// <summary>성능 옵션 창의 선택(0 자동, 1 모양 우선, 2 성능 우선, 3 사용자 지정). 값이 없으면 null.</summary>
    int? ReadPreset();

    void WritePreset(int? value);
}

/// <summary>시각 효과 줄이기 (사양서 3.3 <c>visual.effects</c>). 현재 사용자에게만 적용되고 바로 반영된다.</summary>
public sealed class VisualEffectsTweak(IVisualEffects effects) : ITweak
{
    private const string PresetKey = "preset";
    private const int CustomPreset = 3;

    public VisualEffectsTweak() : this(new SystemVisualEffects())
    {
    }

    public string Id => "visual.effects";
    public string Name => "시각 효과 줄이기";
    public string Description => "창·메뉴 애니메이션, 그림자, 페이드 효과를 끕니다. 글꼴 다듬기와 미리보기 그림은 그대로 둡니다. 사양이 낮은 PC에서 반응이 빨라집니다.";
    public RiskLevel Risk => RiskLevel.Low;
    public bool RequiresElevation => false;
    public bool RequiresRestart => false;

    public Task<TweakState> GetStateAsync(CancellationToken ct)
    {
        var all = Enum.GetValues<VisualEffect>();
        var on = all.Count(effects.Get);
        return Task.FromResult(new TweakState(IsApplied: on == 0, on == 0 ? "효과 모두 꺼짐" : $"효과 {all.Length}개 중 {on}개 켜짐"));
    }

    public Task<TweakBackup> ApplyAsync(CancellationToken ct)
    {
        var values = Enum.GetValues<VisualEffect>()
            .ToDictionary(e => e.ToString(), e => (string?)(effects.Get(e) ? "1" : "0"));
        values[PresetKey] = effects.ReadPreset()?.ToString();

        foreach (var effect in Enum.GetValues<VisualEffect>())
            effects.Set(effect, false);
        effects.WritePreset(CustomPreset);

        return Task.FromResult(new TweakBackup(Id, values));
    }

    public Task RevertAsync(TweakBackup? backup, CancellationToken ct)
    {
        if (backup is not null && backup.TweakId != Id)
            throw new ArgumentException($"다른 항목({backup.TweakId})의 백업입니다.", nameof(backup));

        // 기록에 "0"으로 남은 것만 끄고 나머지는 Windows 기본값(켜짐)으로 둔다.
        foreach (var effect in Enum.GetValues<VisualEffect>())
            effects.Set(effect, backup?.Values.GetValueOrDefault(effect.ToString()) != "0");

        var preset = backup?.Values.GetValueOrDefault(PresetKey);
        effects.WritePreset(int.TryParse(preset, out var value) && value is >= 0 and <= 3 ? value : null);
        return Task.CompletedTask;
    }
}

/// <summary>SystemParametersInfo로 현재 사용자의 시각 효과를 바꾼다. 관리자 권한은 필요 없다.</summary>
public sealed partial class SystemVisualEffects : IVisualEffects
{
    private const string PresetPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects";
    private const string PresetValue = "VisualFXSetting";
    private const uint UpdateIniFileAndBroadcast = 0x01 | 0x02;
    private const uint GetAnimation = 0x0048;
    private const uint SetAnimation = 0x0049;

    // (읽기, 쓰기) SPI 코드. 창 애니메이션은 구조체를 써서 따로 다룬다.
    private static readonly Dictionary<VisualEffect, (uint Get, uint Set)> Codes = new()
    {
        [VisualEffect.MenuAnimation] = (0x1002, 0x1003),
        [VisualEffect.ComboBoxAnimation] = (0x1004, 0x1005),
        [VisualEffect.ListBoxSmoothScrolling] = (0x1006, 0x1007),
        [VisualEffect.SelectionFade] = (0x1014, 0x1015),
        [VisualEffect.TooltipAnimation] = (0x1016, 0x1017),
        [VisualEffect.CursorShadow] = (0x101A, 0x101B),
        [VisualEffect.DropShadow] = (0x1024, 0x1025),
        [VisualEffect.ClientAreaAnimation] = (0x1042, 0x1043),
    };

    public bool Get(VisualEffect effect)
    {
        if (effect == VisualEffect.WindowAnimation)
        {
            var info = new AnimationInfo { Size = (uint)Marshal.SizeOf<AnimationInfo>() };
            Check(SystemParametersInfo(GetAnimation, info.Size, ref info, 0));
            return info.MinAnimate != 0;
        }

        var value = 0;
        Check(SystemParametersInfo(Codes[effect].Get, 0, ref value, 0));
        return value != 0;
    }

    public void Set(VisualEffect effect, bool enabled)
    {
        if (effect == VisualEffect.WindowAnimation)
        {
            var info = new AnimationInfo { Size = (uint)Marshal.SizeOf<AnimationInfo>(), MinAnimate = enabled ? 1 : 0 };
            Check(SystemParametersInfo(SetAnimation, info.Size, ref info, UpdateIniFileAndBroadcast));
            return;
        }

        // 이 SPI들은 값을 포인터 자리에 그대로 넘긴다.
        Check(SystemParametersInfo(Codes[effect].Set, 0, enabled ? 1 : 0, UpdateIniFileAndBroadcast));
    }

    public int? ReadPreset()
    {
        using var key = Registry.CurrentUser.OpenSubKey(PresetPath);
        return key?.GetValue(PresetValue) as int?;
    }

    public void WritePreset(int? value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(PresetPath);
        if (value is { } preset)
            key.SetValue(PresetValue, preset, RegistryValueKind.DWord);
        else
            key.DeleteValue(PresetValue, throwOnMissingValue: false);
    }

    private static void Check(bool ok)
    {
        if (!ok)
            throw new InvalidOperationException($"SystemParametersInfo 실패 (오류 코드 {Marshal.GetLastPInvokeError()})");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AnimationInfo
    {
        public uint Size;
        public int MinAnimate;
    }

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfo(uint action, uint param, ref int value, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfo(uint action, uint param, nint value, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfo(uint action, uint param, ref AnimationInfo value, uint flags);
}
