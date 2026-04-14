using RamDrive.Core.Memory;

namespace RamDrive.Core.FileSystem;

public enum FileNodeType
{
    File,
    Directory
}

/// <summary>
/// Represents a file or directory in the RAM file system.
/// </summary>
public sealed class FileNode : IDisposable
{
    public string Name { get; set; }
    public FileNodeType NodeType { get; }
    public FileAttributes Attributes { get; set; }
    public DateTime CreationTime { get; set; }
    public DateTime LastWriteTime { get; set; }
    public DateTime LastAccessTime { get; set; }

    /// <summary>File content. Null for directories.</summary>
    public PagedFileContent? Content { get; }

    /// <summary>Children (directories only). Case-insensitive on Windows.</summary>
    public Dictionary<string, FileNode>? Children { get; }

    /// <summary>Self-relative security descriptor (binary form). Null = no ACL.</summary>
    public byte[]? SecurityDescriptor { get; set; }

    public FileNode? Parent { get; set; }

    public long Size => Content?.Length ?? 0;

    /// <summary>Actual bytes backed by allocated pages. 0 for directories and sparse regions.</summary>
    public long AllocatedBytes => Content?.AllocatedBytes ?? 0;

    private FileNode(string name, FileNodeType nodeType, PagedFileContent? content)
    {
        Name = name;
        NodeType = nodeType;
        Content = content;
        var now = DateTime.UtcNow;
        CreationTime = now;
        LastWriteTime = now;
        LastAccessTime = now;

        if (nodeType == FileNodeType.Directory)
        {
            Attributes = FileAttributes.Directory;
            Children = new Dictionary<string, FileNode>(StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            Attributes = FileAttributes.Normal;
        }
    }

    public static FileNode CreateFile(string name, PagePool pool)
        => new(name, FileNodeType.File, new PagedFileContent(pool));

    public static FileNode CreateDirectory(string name)
        => new(name, FileNodeType.Directory, null);

    public bool IsDirectory => NodeType == FileNodeType.Directory;
    public bool IsFile => NodeType == FileNodeType.File;

    public void Dispose()
    {
        Content?.Dispose();
        if (Children != null)
        {
            foreach (var child in Children.Values)
                child.Dispose();
            Children.Clear();
        }
    }
}
