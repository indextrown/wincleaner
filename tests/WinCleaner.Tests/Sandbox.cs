using System.Diagnostics;

namespace WinCleaner.Tests;

/// <summary>테스트마다 임시 폴더 안에 따로 만드는 작업 공간. 실제 파일은 건드리지 않는다.</summary>
internal sealed class Sandbox : IDisposable
{
    private readonly List<string> _junctions = [];

    public Sandbox()
    {
        Root = Path.Combine(Path.GetTempPath(), "WinCleanerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string Folder(string relative, TimeSpan? age = null)
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(path);
        if (age is { } a)
            Directory.SetCreationTimeUtc(path, DateTime.UtcNow - a);
        return path;
    }

    public string File(string relative, TimeSpan age, int size = 100)
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, new byte[size]);
        System.IO.File.SetLastWriteTimeUtc(path, DateTime.UtcNow - age);
        return path;
    }

    /// <summary>관리자 권한 없이 만들 수 있는 디렉터리 정션을 만든다.</summary>
    public string Junction(string relative, string target)
    {
        var link = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
        })!;
        process.WaitForExit();
        Assert.True(Directory.Exists(link), "정션을 만들지 못했습니다.");
        _junctions.Add(link);
        return link;
    }

    public void Dispose()
    {
        // 정션은 링크만 지우고 대상은 남긴다.
        foreach (var junction in _junctions.Where(Directory.Exists))
            Directory.Delete(junction);

        try
        {
            foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                System.IO.File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
