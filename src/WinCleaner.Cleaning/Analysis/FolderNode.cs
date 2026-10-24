namespace WinCleaner.Cleaning.Analysis;

/// <summary>용량 분석 결과의 폴더 하나. 분석이 끝나면 하위 폴더는 큰 순서로 정렬돼 있다.</summary>
public sealed class FolderNode
{
    private List<FolderNode>? _children;

    internal FolderNode(string name, FolderNode? parent)
    {
        Name = name;
        Parent = parent;
    }

    /// <summary>폴더 이름. 맨 위 폴더는 전체 경로(예: <c>C:\</c>)다.</summary>
    public string Name { get; }

    public FolderNode? Parent { get; }

    public IReadOnlyList<FolderNode> Children => _children ?? (IReadOnlyList<FolderNode>)[];

    /// <summary>하위 폴더까지 모두 더한 크기.</summary>
    public long SizeBytes { get; internal set; }

    /// <summary>하위 폴더까지 모두 더한 파일 수.</summary>
    public long FileCount { get; internal set; }

    /// <summary>이 폴더에 바로 있는 파일의 크기.</summary>
    public long OwnFilesBytes { get; internal set; }

    public int OwnFileCount { get; internal set; }

    /// <summary>권한이 없어 열지 못한 폴더. 크기에 들어가지 않는다.</summary>
    public bool IsInaccessible { get; internal set; }

    // 폴더가 수십만 개일 수 있어 경로는 저장하지 않고 필요할 때 만든다.
    public string FullPath => Parent is null ? Name : Path.Join(Parent.FullPath, Name);

    /// <summary>경로로 하위 폴더를 찾는다. 이 폴더 아래가 아니거나 없으면 null.</summary>
    public FolderNode? Find(string fullPath)
    {
        var target = Path.TrimEndingDirectorySeparator(fullPath);
        var current = this;
        while (!string.Equals(current.FullPath, target, StringComparison.OrdinalIgnoreCase))
        {
            if (!PathGuard.IsUnder(target, current.FullPath))
                return null;
            var next = current.Children.FirstOrDefault(c =>
                string.Equals(c.FullPath, target, StringComparison.OrdinalIgnoreCase) || PathGuard.IsUnder(target, c.FullPath));
            if (next is null)
                return null;
            current = next;
        }
        return current;
    }

    /// <summary>지운 하위 폴더를 빼고, 이 폴더부터 맨 위까지 크기를 줄인다. 다시 분석하지 않아도 화면이 맞게 보이도록 한다.</summary>
    public void RemoveChild(FolderNode child)
    {
        if (_children is null || !_children.Remove(child))
            return;
        Subtract(child.SizeBytes, child.FileCount);
    }

    /// <summary>이 폴더에 바로 있던 파일 하나를 지웠을 때 크기를 줄인다.</summary>
    public void RemoveOwnFile(long sizeBytes)
    {
        OwnFilesBytes = Math.Max(0, OwnFilesBytes - sizeBytes);
        OwnFileCount = Math.Max(0, OwnFileCount - 1);
        Subtract(sizeBytes, 1);
    }

    private void Subtract(long bytes, long files)
    {
        for (var folder = this; folder is not null; folder = folder.Parent)
        {
            folder.SizeBytes = Math.Max(0, folder.SizeBytes - bytes);
            folder.FileCount = Math.Max(0, folder.FileCount - files);
        }
    }

    internal FolderNode AddChild(string name)
    {
        var child = new FolderNode(name, this);
        (_children ??= []).Add(child);
        return child;
    }

    internal void SortChildrenBySize() => _children?.Sort((a, b) => b.SizeBytes.CompareTo(a.SizeBytes));
}
