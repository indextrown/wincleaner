using WinCleaner.Core;

namespace WinCleaner.Cleaning;

/// <summary><c>rules/cleanup-rules.json</c>의 규칙 하나. 필드 설명은 그 파일 머리말에 있다.</summary>
public sealed class RuleDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string Category { get; init; }
    public RiskLevel Risk { get; init; } = RiskLevel.Low;
    public bool RequiresElevation { get; init; }
    public double MinAgeHours { get; init; }
    public IReadOnlyList<string> SkipIfRunning { get; init; } = [];
    public required IReadOnlyList<RuleTarget> Targets { get; init; }
}

public sealed class RuleTarget
{
    public required string Path { get; init; }
    public string Pattern { get; init; } = "*";
    public bool Recursive { get; init; } = true;
}
