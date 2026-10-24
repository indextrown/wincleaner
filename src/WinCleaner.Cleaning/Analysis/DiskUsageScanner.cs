using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Enumeration;

namespace WinCleaner.Cleaning.Analysis;

public sealed record LargeFile(string Path, long SizeBytes, DateTime LastWriteTimeUtc);

public sealed record DiskUsageProgress(long Files, long Bytes, string CurrentFolder);

public sealed record DiskUsageReport(
    FolderNode Root,
    IReadOnlyList<LargeFile> LargestFiles,
    long FileCount,
    int InaccessibleFolders,
    int SkippedLinks,
    long CloudOnlyFiles,
    TimeSpan Duration);

/// <summary>
/// 폴더 아래 모든 폴더의 크기를 계산하고 가장 큰 파일을 찾는다. 아무것도 바꾸지 않는다.
/// 링크·정션은 따라가지 않는다. 따라가면 같은 공간을 두 번 세게 된다 (예: C:\Users\All Users).
/// </summary>
public sealed class DiskUsageScanner
{
    // OneDrive 같은 클라우드 파일이 내용 없이 자리만 있을 때 붙는 특성. 디스크 공간을 쓰지 않으므로 세지 않는다.
    private const FileAttributes RecallOnOpen = (FileAttributes)0x40000;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x400000;
    private const FileAttributes CloudOnly = FileAttributes.Offline | RecallOnOpen | RecallOnDataAccess;

    private static readonly EnumerationOptions ListOptions = new()
    {
        AttributesToSkip = 0,
        IgnoreInaccessible = false,
        RecurseSubdirectories = false,
    };

    public int LargestFileCount { get; init; } = 200;

    public int Parallelism { get; init; } = Math.Clamp(Environment.ProcessorCount, 1, 8);

    public async Task<DiskUsageReport> ScanAsync(string rootPath, IProgress<DiskUsageProgress>? progress, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        // 드라이브 최상위(C:\)는 끝의 \가 남는다.
        var root = new FolderNode(Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath)), null);
        var state = new ScanState(LargestFileCount);
        state.Pending = 1;
        state.Queue.Push(root);

        var workers = Task.WhenAll(Enumerable.Range(0, Parallelism)
            .Select(_ => Task.Run(() => Work(state, ct), CancellationToken.None)));

        while (await Task.WhenAny(workers, Task.Delay(200, CancellationToken.None)) != workers)
            progress?.Report(state.Snapshot());
        await workers;
        ct.ThrowIfCancellationRequested();

        Summarize(root);
        progress?.Report(state.Snapshot());

        return new DiskUsageReport(root, state.TopFiles.ToList(), root.FileCount, state.Inaccessible,
            state.SkippedLinks, state.CloudOnlyFiles, stopwatch.Elapsed);
    }

    private static void Work(ScanState state, CancellationToken ct)
    {
        var idle = new SpinWait();
        while (Volatile.Read(ref state.Pending) > 0 && !ct.IsCancellationRequested)
        {
            if (!state.Queue.TryPop(out var folder))
            {
                idle.SpinOnce();
                continue;
            }

            idle.Reset();
            try
            {
                ScanFolder(folder, state);
            }
            finally
            {
                Interlocked.Decrement(ref state.Pending);
            }
        }
    }

    private static void ScanFolder(FolderNode folder, ScanState state)
    {
        var path = folder.FullPath;
        state.CurrentFolder = path;

        List<Entry> entries;
        try
        {
            entries = new FileSystemEnumerable<Entry>(path,
                    static (ref FileSystemEntry e) =>
                        new Entry(e.FileName.ToString(), e.IsDirectory, e.Length, e.Attributes, e.LastWriteTimeUtc.UtcDateTime),
                    ListOptions)
                .ToList();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            folder.IsInaccessible = true;
            Interlocked.Increment(ref state.Inaccessible);
            return;
        }

        long ownBytes = 0;
        var ownCount = 0;
        foreach (var entry in entries)
        {
            if (entry.IsDirectory)
            {
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    Interlocked.Increment(ref state.SkippedLinks);
                    continue;
                }

                Interlocked.Increment(ref state.Pending);
                state.Queue.Push(folder.AddChild(entry.Name));
                continue;
            }

            ownCount++;
            if ((entry.Attributes & CloudOnly) != 0)
            {
                Interlocked.Increment(ref state.CloudOnlyFiles);
                continue;
            }

            ownBytes += entry.Length;
            state.TopFiles.Offer(path, entry.Name, entry.Length, entry.LastWriteTimeUtc);
        }

        folder.OwnFilesBytes = ownBytes;
        folder.OwnFileCount = ownCount;
        Interlocked.Add(ref state.Files, ownCount);
        Interlocked.Add(ref state.Bytes, ownBytes);
    }

    /// <summary>깊은 폴더부터 크기를 위로 더하고, 하위 폴더를 큰 순서로 정렬한다.</summary>
    private static void Summarize(FolderNode root)
    {
        // 전위 순서로 모은 뒤 거꾸로 돌면 항상 자식이 부모보다 먼저 계산된다.
        var order = new List<FolderNode>();
        var pending = new Stack<FolderNode>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var folder = pending.Pop();
            order.Add(folder);
            foreach (var child in folder.Children)
                pending.Push(child);
        }

        for (var i = order.Count - 1; i >= 0; i--)
        {
            var folder = order[i];
            long size = folder.OwnFilesBytes;
            long files = folder.OwnFileCount;
            foreach (var child in folder.Children)
            {
                size += child.SizeBytes;
                files += child.FileCount;
            }

            folder.SizeBytes = size;
            folder.FileCount = files;
            folder.SortChildrenBySize();
        }
    }

    private readonly record struct Entry(string Name, bool IsDirectory, long Length, FileAttributes Attributes, DateTime LastWriteTimeUtc);

    private sealed class ScanState(int largestFileCount)
    {
        public readonly ConcurrentStack<FolderNode> Queue = new();
        public readonly TopFiles TopFiles = new(largestFileCount);
        public int Pending;
        public long Files;
        public long Bytes;
        public int Inaccessible;
        public int SkippedLinks;
        public long CloudOnlyFiles;
        public volatile string CurrentFolder = "";

        public DiskUsageProgress Snapshot() => new(Interlocked.Read(ref Files), Interlocked.Read(ref Bytes), CurrentFolder);
    }

    /// <summary>여러 스레드에서 넣어도 가장 큰 파일 N개만 남긴다.</summary>
    private sealed class TopFiles(int capacity)
    {
        private readonly PriorityQueue<LargeFile, long> _heap = new();
        private long _threshold = -1;

        public void Offer(string folder, string name, long size, DateTime lastWriteTimeUtc)
        {
            // 대부분의 파일은 잠금 없이 여기서 걸러진다.
            if (size <= Volatile.Read(ref _threshold) || capacity == 0)
                return;

            lock (_heap)
            {
                var file = new LargeFile(Path.Join(folder, name), size, lastWriteTimeUtc);
                if (_heap.Count < capacity)
                    _heap.Enqueue(file, size);
                else if (_heap.TryPeek(out _, out var smallest) && size > smallest)
                    _heap.EnqueueDequeue(file, size);
                else
                    return;

                if (_heap.Count == capacity && _heap.TryPeek(out _, out var newSmallest))
                    Volatile.Write(ref _threshold, newSmallest);
            }
        }

        public IReadOnlyList<LargeFile> ToList()
        {
            lock (_heap)
                return _heap.UnorderedItems.Select(i => i.Element).OrderByDescending(f => f.SizeBytes).ToList();
        }
    }
}
