using WinCleaner.Core.Optimization;
using WinCleaner.Optimizer;
using WinCleaner.Optimizer.Startup;

namespace WinCleaner.App.Services;

/// <summary>
/// 변경 기록의 ID로 최적화 항목을 찾는다. 시작 프로그램처럼 PC마다 다른 항목은 ID에서 다시 만든다.
/// 기록 화면의 되돌리기에 쓴다.
/// </summary>
public sealed class TweakCatalog(IReadOnlyList<ITweak> fixedTweaks, IStartupStore startupStore, IPowerSchemes powerSchemes)
{
    /// <summary>최적화 화면에 카드로 보여 주는 고정 항목.</summary>
    public IReadOnlyList<ITweak> Fixed => fixedTweaks;

    public IStartupStore StartupStore => startupStore;

    public IPowerSchemes PowerSchemes => powerSchemes;

    public ITweak? Find(string? id)
    {
        if (id is null)
            return null;
        if (fixedTweaks.FirstOrDefault(t => t.Id == id) is { } known)
            return known;
        if (StartupEntryTweak.TryParseId(id, out var location, out var name))
            return new StartupEntryTweak(startupStore, location, name);
        return id == PowerPlanTweak.TweakId ? new PowerPlanTweak(powerSchemes, null) : null;
    }
}
