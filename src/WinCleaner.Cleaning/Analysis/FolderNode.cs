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

    internal FolderNode AddChild(string name)
    {
        var child = new FolderNode(name, this);
        (_children ??= []).Add(child);
        return child;
    }

    internal void SortChildrenBySize() => _children?.Sort((a, b) => b.SizeBytes.CompareTo(a.SizeBytes));
}
