namespace WinCleaner.App.ViewModels;

/// <summary>아직 구현하지 않은 메뉴. 어느 단계에서 무엇이 들어오는지 보여 준다.</summary>
public sealed class PlaceholderViewModel(string title, int phase, IReadOnlyList<string> plannedFeatures)
{
    public string Title { get; } = title;
    public string PhaseText { get; } = $"{phase}단계에서 구현됩니다.";
    public IReadOnlyList<string> PlannedFeatures { get; } = plannedFeatures;
}
