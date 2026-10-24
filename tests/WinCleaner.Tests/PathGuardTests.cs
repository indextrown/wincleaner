using WinCleaner.Cleaning;

namespace WinCleaner.Tests;

public class PathGuardTests
{
    private static readonly PathGuard Guard = new(
        protectedFolders: [@"C:\Windows", @"C:\Users\me"],
        userDataFolders: [@"C:\Users\me\Documents"]);

    [Theory]
    [InlineData(@"C:\Windows\Temp")]
    [InlineData(@"C:\Users\me\AppData\Local\Temp")]
    [InlineData(@"D:\Cache\")]
    public void Allows_ordinary_cache_folders(string folder)
    {
        Assert.True(Guard.IsAllowedRoot(folder, out _));
    }

    [Theory]
    [InlineData(@"C:\", "드라이브")]
    [InlineData(@"D:\", "드라이브")]
    [InlineData(@"C:\Windows", "보호")]
    [InlineData(@"C:\Windows\", "보호")]
    [InlineData(@"c:\windows", "보호")]
    [InlineData(@"C:\Users", "보호")]
    [InlineData(@"C:\Users\me\AppData\..", "보호")]
    [InlineData(@"C:\Users\me\Documents", "문서")]
    [InlineData(@"C:\Users\me\Documents\Projects", "문서")]
    [InlineData(@"%TEMP%", "환경 변수")]
    [InlineData(@"Temp", "전체 경로")]
    public void Rejects_dangerous_folders(string folder, string expectedReason)
    {
        Assert.False(Guard.IsAllowedRoot(folder, out var reason));
        Assert.Contains(expectedReason, reason);
    }

    [Fact]
    public void Current_user_guard_allows_temp_but_not_profile_or_documents()
    {
        var guard = PathGuard.ForCurrentUser();
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        Assert.True(guard.IsAllowedRoot(Path.GetTempPath(), out _));
        Assert.False(guard.IsAllowedRoot(profile, out _));
        Assert.False(guard.IsAllowedRoot(Path.Combine(documents, "anything"), out _));
        Assert.False(guard.IsAllowedRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows), out _));
    }
}
