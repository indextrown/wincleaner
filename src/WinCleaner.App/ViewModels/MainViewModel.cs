using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinCleaner.App.Services;
using WinCleaner.Cleaning;
using WinCleaner.Core.Platform;
using WinCleaner.Memory;
using WinCleaner.Optimizer;
using WinCleaner.Optimizer.Drives;
using WinCleaner.Optimizer.Fans;
using WinCleaner.Optimizer.Programs;
using WinCleaner.Optimizer.Startup;
using WinCleaner.Safety;

namespace WinCleaner.App.ViewModels;

public sealed record NavigationItem(string Title, string Glyph, object Page);

public partial class MainViewModel : ObservableObject
{
    [ObservableProperty]
    private NavigationItem? _selectedItem;

    public MainViewModel()
    {
        var isElevated = Elevation.IsElevated;
        var dialogs = new WpfUserDialogs();
        var journal = new ChangeJournal(ChangeJournal.DefaultPath);
        var restorePoints = new WmiRestorePointService(isElevated);
        var runner = new TweakRunner(journal, restorePoints, dialogs);
        var powerSchemes = new PowrProfSchemes();
        var turbo = new TurboLimitTweak();
        var catalog = new TweakCatalog(
            [new VisualEffectsTweak(), turbo, new HibernationTweak()], new RegistryStartupStore(), powerSchemes);

        var memory = new WindowsMemoryService();
        var foreground = new ForegroundApps();
        foreground.Start();
        var quick = new QuickOptimizeRunner(new QuickMemoryOptimizer(memory, foreground), journal, isElevated);

        var cleaning = new NavigationItem("디스크 청소", Glyph(0xE74D), new CleaningViewModel(
            RuleCatalog.CreateBuiltInRules(), dialogs, isElevated, CleanupLog.DefaultDirectory, journal));

        NavigationItems =
        [
            new NavigationItem("대시보드", Glyph(0xE80F), new DashboardViewModel(() => SelectedItem = cleaning, quick)),
            new NavigationItem("게임 모드", Glyph(0xE7FC), new GameModeViewModel(
                runner, powerSchemes, quick, new ClevoFanMonitor(), turbo, isElevated)),
            cleaning,
            new NavigationItem("용량 분석", Glyph(0xEDA2), new DiskUsageViewModel(
                DiskUsageViewModel.FixedDrives(), dialogs, isElevated, journal)),
            new NavigationItem("프로그램 제거", Glyph(0xE71D), new ProgramsViewModel(
                new RegistryProgramCatalog(), new ProcessUninstaller(), dialogs, journal)),
            new NavigationItem("최적화", Glyph(0xEC4A), new OptimizeViewModel(
                catalog, new DriveMaintenance(), runner, dialogs, isElevated)),
            new NavigationItem("메모리", Glyph(0xE9D9), new MemoryViewModel(memory, dialogs, journal, isElevated, quick)),
            new NavigationItem("기록", Glyph(0xE81C), new HistoryViewModel(
                journal, runner, catalog, restorePoints, dialogs, isElevated)),
        ];

        SelectedItem = NavigationItems[0];
    }

    public IReadOnlyList<NavigationItem> NavigationItems { get; }

    public bool ShowElevationWarning => !Elevation.IsElevated;

    public string VersionText { get; } = $"v{typeof(MainViewModel).Assembly.GetName().Version?.ToString(3)}";

    // 다른 화면에서 바꾼 결과가 바로 보이도록 화면에 들어올 때마다 다시 읽는다.
    partial void OnSelectedItemChanged(NavigationItem? oldValue, NavigationItem? newValue)
    {
        // 메모리·게임 모드 화면은 보이는 동안에만 주기적으로 다시 읽는다.
        switch (oldValue?.Page)
        {
            case MemoryViewModel memory:
                memory.Deactivate();
                break;
            case GameModeViewModel gameMode:
                gameMode.Deactivate();
                break;
        }

        switch (newValue?.Page)
        {
            case MemoryViewModel memory:
                memory.Activate();
                break;
            case GameModeViewModel gameMode:
                gameMode.Activate();
                break;
            case ProgramsViewModel programs:
                programs.RefreshCommand.Execute(null);
                break;
            case DashboardViewModel dashboard:
                dashboard.RefreshCommand.Execute(null);
                break;
            case OptimizeViewModel optimize:
                optimize.RefreshCommand.Execute(null);
                break;
            case HistoryViewModel history:
                history.RefreshRestorePointsCommand.Execute(null);
                break;
        }
    }

    [RelayCommand]
    private void RestartElevated()
    {
        if (Elevation.TryRestartElevated())
            Application.Current.Shutdown();
    }

    private static string Glyph(int codePoint) => char.ConvertFromUtf32(codePoint);
}
