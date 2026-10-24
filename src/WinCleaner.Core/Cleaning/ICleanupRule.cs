namespace WinCleaner.Core.Cleaning;

/// <summary>
/// 디스크 청소 규칙 하나 (사양서 3.1).
/// 스캔은 아무것도 지우지 않고, 정리는 사용자가 미리보기에서 고른 항목만 받는다 (사양서 3.4).
/// </summary>
public interface ICleanupRule
{
    string Id { get; }
    string Name { get; }
    string Description { get; }
    string Category { get; }
    RiskLevel Risk { get; }
    bool RequiresElevation { get; }

    Task<ScanResult> ScanAsync(CancellationToken ct);

    /// <summary>스캔 결과의 파일을 지운다. 취소되면 그때까지 정리한 결과를 돌려준다.</summary>
    Task<CleanResult> CleanAsync(ScanResult selected, CancellationToken ct);
}

/// <summary>
/// 스캔으로 찾은 파일 하나. 정리할 때 수정 시각이 달라졌으면 지우지 않는다.
/// <paramref name="DisplayPath"/>는 실제로 지울 경로와 사용자에게 보여 줄 경로가 다를 때 쓴다 (예: 휴지통 항목의 원래 위치).
/// </summary>
public sealed record ScanItem(string Path, long SizeBytes, DateTime LastWriteTimeUtc, string? DisplayPath = null)
{
    public string ShownPath => DisplayPath ?? Path;
}

/// <summary>스캔 결과. <see cref="Warnings"/>에는 건너뛴 이유처럼 사용자에게 알릴 내용이 들어간다.</summary>
public sealed record ScanResult(string RuleId, IReadOnlyList<ScanItem> Items, IReadOnlyList<string> Warnings)
{
    public long TotalBytes => Items.Sum(i => i.SizeBytes);
}

public sealed record SkippedItem(string Path, string Reason);

/// <summary>정리 결과. 사용 중이라 지우지 못한 파일은 <see cref="Skipped"/>에 이유와 함께 남는다.</summary>
public sealed record CleanResult(string RuleId, IReadOnlyList<ScanItem> Deleted, IReadOnlyList<SkippedItem> Skipped)
{
    public int DeletedCount => Deleted.Count;

    public long FreedBytes => Deleted.Sum(i => i.SizeBytes);
}
