using System.Text;
using WinCleaner.Core;
using WinCleaner.Core.Cleaning;

namespace WinCleaner.Cleaning;

/// <summary>무엇을 지웠고 무엇을 건너뛰었는지 텍스트 파일로 남긴다.</summary>
public static class CleanupLog
{
    public static string DefaultDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinCleaner", "logs");

    /// <summary>기록 파일을 만들고 그 경로를 돌려준다.</summary>
    public static string Write(
        string directory,
        IReadOnlyList<(ICleanupRule Rule, CleanResult Result)> results,
        DateTime startedAt,
        bool isElevated)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"cleanup-{startedAt:yyyyMMdd-HHmmss}.log");

        // 메모장에서 한글이 깨지지 않게 BOM을 붙인다.
        using var writer = new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        writer.WriteLine("WinCleaner 정리 기록");
        writer.WriteLine($"시작: {startedAt:yyyy-MM-dd HH:mm:ss}");
        writer.WriteLine($"권한: {(isElevated ? "관리자" : "일반")}");
        writer.WriteLine(
            $"합계: {ByteSize.Format(results.Sum(r => r.Result.FreedBytes))} 확보, " +
            $"삭제 {results.Sum(r => r.Result.DeletedCount):N0}개, 건너뜀 {results.Sum(r => r.Result.Skipped.Count):N0}개");

        foreach (var (rule, result) in results)
        {
            writer.WriteLine();
            writer.WriteLine($"[{rule.Id}] {rule.Name}");
            writer.WriteLine(
                $"  삭제 {result.DeletedCount:N0}개, {ByteSize.Format(result.FreedBytes)} 확보, 건너뜀 {result.Skipped.Count:N0}개");

            foreach (var item in result.Deleted)
            {
                var origin = item.DisplayPath is { } shown ? $" [원래 위치: {shown}]" : "";
                writer.WriteLine($"  삭제    {item.Path}{origin} ({ByteSize.Format(item.SizeBytes)})");
            }
            foreach (var item in result.Skipped)
                writer.WriteLine($"  건너뜀  {item.Path} - {item.Reason}");
        }

        return path;
    }
}
