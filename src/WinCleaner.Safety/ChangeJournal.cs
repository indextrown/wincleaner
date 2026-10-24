using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinCleaner.Safety;

public enum ChangeKind
{
    /// <summary>파일 정리. 되돌릴 수 없다.</summary>
    Cleanup,

    /// <summary>설정 변경. 백업 값으로 되돌릴 수 있다.</summary>
    Tweak,

    /// <summary>설정 변경을 되돌림.</summary>
    Revert,

    /// <summary>시스템 복원 지점을 만듦.</summary>
    RestorePoint,

    /// <summary>드라이브 최적화 같은 유지 관리 작업. 되돌릴 대상이 없다.</summary>
    Maintenance,

    /// <summary>용량 분석 화면에서 휴지통으로 보냄. 휴지통에서 되살릴 수 있다.</summary>
    Recycled,

    /// <summary>프로그램 제거. 되돌리려면 다시 설치해야 한다.</summary>
    Uninstalled,
}

public sealed record ChangeEntry
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTime Time { get; init; } = DateTime.Now;
    public required ChangeKind Kind { get; init; }
    public required string Title { get; init; }
    public string Detail { get; init; } = "";

    /// <summary>설정 변경이면 그 항목의 ID.</summary>
    public string? TweakId { get; init; }

    /// <summary>설정 변경이면 바꾸기 전 값.</summary>
    public IReadOnlyDictionary<string, string?>? Backup { get; init; }

    /// <summary>파일 정리면 정리 기록 파일 경로.</summary>
    public string? LogPath { get; init; }

    public DateTime? RevertedAt { get; init; }

    [JsonIgnore]
    public bool CanRevert => Kind == ChangeKind.Tweak && TweakId is not null && RevertedAt is null;
}

/// <summary>
/// WinCleaner가 한 일을 모두 남기는 변경 기록 (사양서 3.4). JSON 파일 하나에 저장한다.
/// 파일이 깨져 있으면 옆에 따로 보관하고 새로 시작한다. 기록 때문에 앱이 멈추면 안 되기 때문이다.
/// </summary>
public sealed class ChangeJournal
{
    private const int MaxEntries = 500;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly object _lock = new();
    private readonly string _path;
    private List<ChangeEntry>? _entries;

    public ChangeJournal(string path)
    {
        _path = path;
    }

    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinCleaner", "journal.json");

    /// <summary>기록이 추가되거나 바뀌면 알린다. 기록 화면과 최적화 화면이 다시 읽는 데 쓴다.</summary>
    public event EventHandler? Changed;

    /// <summary>최근 것이 먼저 오는 기록 목록.</summary>
    public IReadOnlyList<ChangeEntry> Entries
    {
        get
        {
            lock (_lock)
                return Load().OrderByDescending(e => e.Time).ToList();
        }
    }

    public ChangeEntry Add(ChangeEntry entry)
    {
        lock (_lock)
        {
            var entries = Load();
            entries.Add(entry);
            if (entries.Count > MaxEntries)
                entries.RemoveRange(0, entries.Count - MaxEntries);
            Save(entries);
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return entry;
    }

    public void MarkReverted(Guid id, DateTime revertedAt)
    {
        lock (_lock)
        {
            var entries = Load();
            var index = entries.FindIndex(e => e.Id == id);
            if (index < 0)
                return;
            entries[index] = entries[index] with { RevertedAt = revertedAt };
            Save(entries);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>이 항목에 대해 아직 되돌리지 않은 가장 최근 설정 변경.</summary>
    public ChangeEntry? LatestRevertible(string tweakId) =>
        Entries.FirstOrDefault(e => e.CanRevert && e.TweakId == tweakId);

    private List<ChangeEntry> Load()
    {
        if (_entries is not null)
            return _entries;

        if (!File.Exists(_path))
            return _entries = [];

        try
        {
            using var stream = File.OpenRead(_path);
            _entries = JsonSerializer.Deserialize<List<ChangeEntry>>(stream, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            File.Move(_path, $"{_path}.broken-{DateTime.Now:yyyyMMdd-HHmmss}", overwrite: true);
            _entries = [];
        }
        return _entries;
    }

    private void Save(List<ChangeEntry> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

        // 쓰다가 꺼져도 기존 기록이 깨지지 않게 임시 파일에 쓴 뒤 바꿔 끼운다.
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(entries, JsonOptions));
        File.Move(temp, _path, overwrite: true);
        _entries = entries;
    }
}
