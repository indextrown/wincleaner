using Microsoft.Win32;

namespace WinCleaner.Optimizer.Startup;

/// <summary>시작 프로그램 목록과 켜짐·꺼짐 값을 읽고 쓰는 부분. 테스트에서 실제 레지스트리를 건드리지 않도록 분리했다.</summary>
public interface IStartupStore
{
    IEnumerable<(string Name, string Command)> ReadEntries(StartupLocation location);

    byte[]? ReadApproval(StartupLocation location, string name);

    void WriteApproval(StartupLocation location, string name, byte[] value);

    void DeleteApproval(StartupLocation location, string name);
}

public sealed class StartupManager(IStartupStore store)
{
    public StartupManager() : this(new RegistryStartupStore())
    {
    }

    public IStartupStore Store => store;

    public IReadOnlyList<StartupEntry> List() =>
        Enum.GetValues<StartupLocation>()
            .SelectMany(location => store.ReadEntries(location)
                .Select(e => new StartupEntry(location, e.Name, e.Command,
                    StartupApproval.IsEnabled(store.ReadApproval(location, e.Name)))))
            .ToList();
}

/// <summary>실제 레지스트리와 시작 폴더. 모든 사용자 항목을 바꾸려면 관리자 권한이 필요하다.</summary>
public sealed class RegistryStartupStore : IStartupStore
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Run32Key = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    public IEnumerable<(string Name, string Command)> ReadEntries(StartupLocation location)
    {
        switch (location)
        {
            case StartupLocation.UserFolder:
                return ReadFolder(Environment.GetFolderPath(Environment.SpecialFolder.Startup));
            case StartupLocation.MachineFolder:
                return ReadFolder(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup));
        }

        using var key = OpenHive(location).OpenSubKey(location == StartupLocation.MachineRun32 ? Run32Key : RunKey);
        if (key is null)
            return [];

        return key.GetValueNames()
            .Where(name => name.Length > 0)
            .Select(name => (name, key.GetValue(name)?.ToString() ?? ""))
            .ToList();
    }

    public byte[]? ReadApproval(StartupLocation location, string name)
    {
        using var key = OpenHive(location).OpenSubKey(ApprovalPath(location));
        return key?.GetValue(name) as byte[];
    }

    public void WriteApproval(StartupLocation location, string name, byte[] value)
    {
        using var key = OpenHive(location).CreateSubKey(ApprovalPath(location));
        key.SetValue(name, value, RegistryValueKind.Binary);
    }

    public void DeleteApproval(StartupLocation location, string name)
    {
        using var key = OpenHive(location).OpenSubKey(ApprovalPath(location), writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }

    private static RegistryKey OpenHive(StartupLocation location) =>
        location is StartupLocation.UserRun or StartupLocation.UserFolder
            ? Registry.CurrentUser
            : RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

    private static string ApprovalPath(StartupLocation location) => location switch
    {
        StartupLocation.MachineRun32 => $@"{ApprovedKey}\Run32",
        StartupLocation.UserFolder or StartupLocation.MachineFolder => $@"{ApprovedKey}\StartupFolder",
        _ => $@"{ApprovedKey}\Run",
    };

    private static IEnumerable<(string Name, string Command)> ReadFolder(string folder)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            return [];

        return new DirectoryInfo(folder)
            .EnumerateFiles("*", new EnumerationOptions { AttributesToSkip = FileAttributes.Hidden | FileAttributes.System })
            .Select(f => (f.Name, f.FullName))
            .ToList();
    }
}
