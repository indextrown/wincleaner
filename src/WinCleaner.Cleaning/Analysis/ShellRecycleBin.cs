using System.Runtime.InteropServices;

namespace WinCleaner.Cleaning.Analysis;

/// <summary>탐색기와 같은 방식(SHFileOperation)으로 파일이나 폴더를 휴지통으로 보낸다.</summary>
public static class ShellRecycleBin
{
    private const uint Delete = 0x0003;
    private const ushort AllowUndo = 0x0040;
    private const ushort NoConfirmation = 0x0010;

    // 휴지통에 넣을 수 없어 완전히 지워야 할 때(너무 큰 파일 등)는 Windows가 따로 묻게 한다.
    private const ushort WantNukeWarning = 0x4000;

    /// <summary>
    /// 휴지통으로 보낸다. 큰 폴더는 Windows의 진행 창이 뜬다.
    /// 사용자가 Windows 창에서 취소했거나 실패하면 false와 이유를 돌려준다.
    /// </summary>
    public static bool Send(string path, nint ownerWindow, out string? error)
    {
        var operation = new FileOperation
        {
            Window = ownerWindow,
            Function = Delete,
            // 경로 목록은 null 두 개로 끝나야 한다.
            From = path + '\0',
            Flags = (ushort)(AllowUndo | NoConfirmation | WantNukeWarning),
        };

        var result = SHFileOperation(ref operation);
        if (result != 0)
        {
            error = $"휴지통으로 보내지 못했습니다 (오류 코드 0x{result:X}).";
            return false;
        }
        if (operation.AnyOperationsAborted)
        {
            error = "취소했습니다.";
            return false;
        }
        if (File.Exists(path) || Directory.Exists(path))
        {
            error = "일부를 휴지통으로 보내지 못했습니다. 사용 중인 파일이 있을 수 있습니다.";
            return false;
        }

        error = null;
        return true;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FileOperation
    {
        public nint Window;
        public uint Function;
        public string From;
        public string? To;
        public ushort Flags;
        [MarshalAs(UnmanagedType.Bool)] public bool AnyOperationsAborted;
        public nint NameMappings;
        public string? ProgressTitle;
    }

    // 문자열이 든 구조체라 소스 생성 P/Invoke 대신 기존 방식을 쓴다.
#pragma warning disable SYSLIB1054
    [DllImport("shell32.dll", EntryPoint = "SHFileOperationW", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref FileOperation operation);
#pragma warning restore SYSLIB1054
}
