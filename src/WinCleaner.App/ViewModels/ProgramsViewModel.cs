using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinCleaner.App.Services;
using WinCleaner.Core;
using WinCleaner.Optimizer.Programs;
using WinCleaner.Safety;

namespace WinCleaner.App.ViewModels;

/// <summary>
/// 프로그램 제거 화면. 설치된 프로그램을 크기순으로 보여 주고, 고른 프로그램의 제거 프로그램을 실행한다.
/// 실행할 명령을 확인 창에 그대로 보여 준 뒤에만 실행한다.
/// </summary>
public partial class ProgramsViewModel : ObservableObject
{
    private readonly IProgramCatalog _catalog;
    private readonly IProgramUninstaller _uninstaller;
    private readonly IUserDialogs _dialogs;
    private readonly ChangeJournal _journal;
    private IReadOnlyList<InstalledProgram> _programs = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VisiblePrograms))]
    private string _filter = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = "설치된 프로그램을 크기순으로 보여 줍니다. 제거를 누르면 그 프로그램의 제거 창이 열립니다.";

    public ProgramsViewModel(IProgramCatalog catalog, IProgramUninstaller uninstaller, IUserDialogs dialogs, ChangeJournal journal)
    {
        _catalog = catalog;
        _uninstaller = uninstaller;
        _dialogs = dialogs;
        _journal = journal;
    }

    public bool IsIdle => !IsBusy;

    public IReadOnlyList<ProgramRowViewModel> VisiblePrograms =>
        _programs
            .Where(p => Filter.Length == 0
                        || p.Name.Contains(Filter, StringComparison.CurrentCultureIgnoreCase)
                        || (p.Publisher?.Contains(Filter, StringComparison.CurrentCultureIgnoreCase) ?? false))
            .Select(p => new ProgramRowViewModel(p))
            .ToList();

    public string SummaryText
    {
        get
        {
            var known = _programs.Where(p => p.SizeBytes is not null).ToList();
            return $"설치된 프로그램 {_programs.Count}개 · 크기를 알려 준 {known.Count}개의 합계 {ByteSize.Format(known.Sum(p => p.SizeBytes!.Value))}. " +
                   "크기는 프로그램이 설치할 때 알려 준 값이라 실제와 다를 수 있습니다.";
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        try
        {
            _programs = await Task.Run(_catalog.List);
        }
        catch (Exception ex)
        {
            StatusText = $"프로그램 목록을 읽지 못했습니다: {ex.Message}";
        }
        OnPropertyChanged(nameof(VisiblePrograms));
        OnPropertyChanged(nameof(SummaryText));
    }

    [RelayCommand]
    private async Task UninstallAsync(ProgramRowViewModel? row)
    {
        if (row is not { CanUninstall: true } || IsBusy)
            return;

        var (file, arguments) = row.Command!.Value;
        var perUser = row.Program.IsPerUser
            ? "\n\n현재 사용자에게만 설치된 프로그램입니다. 실행할 명령이 아는 프로그램의 것인지 확인하세요."
            : "";
        if (!_dialogs.Confirm("프로그램 제거",
                $"{row.Program.Name}{(row.Program.Publisher is { } p ? $" ({p})" : "")}을(를) 제거합니다.\n\n" +
                $"실행할 제거 명령:\n{file} {arguments}{perUser}\n\n" +
                "프로그램의 제거 창이 열리면 안내에 따라 진행하세요. 계속할까요?"))
            return;

        IsBusy = true;
        StatusText = $"제거 중: {row.Program.Name}. 열린 제거 창에서 진행하세요.";
        try
        {
            var exitCode = await _uninstaller.RunAsync(file, arguments, CancellationToken.None);
            await RefreshAsync();

            if (_programs.Any(p => p.Key == row.Program.Key))
            {
                StatusText = $"제거 창이 닫혔지만 {row.Program.Name}이(가) 아직 목록에 있습니다 (코드 {exitCode}). " +
                             "제거를 취소했거나 다른 창에서 아직 진행 중일 수 있습니다. 끝난 뒤 새로고침하세요.";
                return;
            }

            StatusText = $"제거했습니다: {row.Program.Name}";
            try
            {
                _journal.Add(new ChangeEntry
                {
                    Kind = ChangeKind.Uninstalled,
                    Title = $"프로그램 제거: {row.Program.Name}",
                    Detail = string.Join(" · ", new[] { row.Program.Publisher, row.Program.Version, row.SizeText }.Where(s => !string.IsNullOrEmpty(s))),
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        catch (Exception ex)
        {
            StatusText = $"제거 프로그램을 실행하지 못했습니다: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }
}

public sealed class ProgramRowViewModel(InstalledProgram program)
{
    public InstalledProgram Program => program;
    public string Name => program.Name;
    public (string FileName, string Arguments)? Command { get; } = UninstallCommand.Parse(program.UninstallCommand);
    public bool CanUninstall => Command is not null;
    public string SizeText => program.SizeBytes is { } size ? ByteSize.Format(size) : "";
    public string UninstallToolTip => CanUninstall ? program.UninstallCommand : $"제거 명령을 해석할 수 없습니다: {program.UninstallCommand}";

    public string Details => string.Join(" · ", new[]
    {
        program.Publisher,
        program.Version is { } version ? $"버전 {version}" : null,
        program.InstallDate is { } date ? $"설치 {date:yyyy-MM-dd}" : null,
        program.IsPerUser ? "현재 사용자" : program.Is32Bit ? "32비트" : null,
    }.Where(s => !string.IsNullOrEmpty(s)));
}
