using System.IO;
using WinCleaner.Core;
using WinCleaner.Memory;
using WinCleaner.Safety;

namespace WinCleaner.App.Services;

/// <summary>원클릭 메모리 정리를 실행하고 결과 문구와 변경 기록을 만든다. 대시보드·메모리·게임 모드 화면이 같이 쓴다.</summary>
public sealed class QuickOptimizeRunner(QuickMemoryOptimizer optimizer, ChangeJournal journal, bool isElevated)
{
    public async Task<string> RunAsync()
    {
        var result = await Task.Run(() => optimizer.Run(purgeStandby: isElevated));
        var text = Describe(result);
        try
        {
            journal.Add(new ChangeEntry { Kind = ChangeKind.Maintenance, Title = "원클릭 메모리 정리", Detail = text });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return text;
    }

    public static string Describe(QuickOptimizeResult result)
    {
        var parts = new List<string>
        {
            $"사용 중 {ByteSize.Format(result.UsedBefore)} → {ByteSize.Format(result.UsedAfter)}",
            $"프로그램 {result.Trim.Trimmed}개에서 {ByteSize.Format(result.Trim.FreedBytes)} 내보냄",
            result.StandbyPurged
                ? $"대기 목록 {ByteSize.Format(result.StandbyFreedBytes)} 비움"
                : result.StandbyMessage.TrimEnd('.'),
        };
        if (result.KeptNames.Count > 0)
            parts.Add($"지금 쓰는 프로그램은 그대로 둠: {string.Join(", ", result.KeptNames.Take(3))}");
        return string.Join(" · ", parts);
    }
}
