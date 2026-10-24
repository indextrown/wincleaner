using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using WinCleaner.Cleaning.Analysis;

namespace WinCleaner.App.Services;

/// <summary>뷰모델이 창을 직접 띄우지 않도록 분리한 대화 상자. 테스트에서는 가짜로 바꾼다.</summary>
public interface IUserDialogs
{
    bool Confirm(string title, string message);

    void OpenFile(string path);

    /// <summary>파일이면 그 파일을 선택한 채로, 폴더면 그 폴더를 탐색기로 연다.</summary>
    void ShowInExplorer(string path);

    /// <summary>파일이나 폴더를 휴지통으로 보낸다. 실패하거나 사용자가 Windows 창에서 취소하면 false와 이유.</summary>
    bool MoveToRecycleBin(string path, out string? error);
}

public sealed class WpfUserDialogs : IUserDialogs
{
    public bool Confirm(string title, string message) =>
        MessageBox.Show(Application.Current.MainWindow, message, title, MessageBoxButton.OKCancel, MessageBoxImage.Warning)
        == MessageBoxResult.OK;

    public void OpenFile(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

    public void ShowInExplorer(string path)
    {
        var arguments = File.Exists(path) ? $"/select,\"{path}\"" : $"\"{path}\"";
        Process.Start(new ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = true });
    }

    public bool MoveToRecycleBin(string path, out string? error) =>
        ShellRecycleBin.Send(path, new WindowInteropHelper(Application.Current.MainWindow).Handle, out error);
}
