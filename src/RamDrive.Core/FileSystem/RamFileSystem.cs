using RamDrive.Core.Memory;

namespace RamDrive.Core.FileSystem;

/// <summary>
/// In-memory file system with path resolution, CRUD, and capacity tracking.
/// Thread-safe: uses a global lock for structural operations (create/delete/move)
/// and per-file ReaderWriterLockSlim for content I/O.
/// </summary>
public sealed class RamFileSystem : IDisposable
{
    private readonly PagePool _pool;
    private readonly FileNode _root;
    private readonly object _structureLock = new();

    public RamFileSystem(PagePool pool)
    {
        _pool = pool;
        _root = FileNode.CreateDirectory(string.Empty);
    }

    /// <summary>
    /// Set the security descriptor on the root directory.
    /// Must be called before mounting if ACL support is desired.
    /// </summary>
    public void SetRootSecurityDescriptor(byte[] securityDescriptor)
    {
        _root.SecurityDescriptor = securityDescriptor;
    }

    public long TotalBytes => _pool.CapacityBytes;
    public long UsedBytes => _pool.UsedBytes;
    public long FreeBytes => _pool.FreeBytes;

    /// <summary>
    /// Resolve a path to a FileNode. Returns null if not found.
    /// Path uses backslash separator (Dokan convention). "\" is root.
    /// </summary>
    public FileNode? FindNode(string path)
    {
        lock (_structureLock)
        {
            return FindNodeInternal(path);
        }
    }

    /// <summary>
    /// Create a file at the given path. Parent directory must exist.
    /// Returns the new FileNode, or null if parent not found or name already exists.
    /// </summary>
    public FileNode? CreateFile(string path, byte[]? securityDescriptor = null)
    {
        lock (_structureLock)
        {
            var (parent, name) = ResolvePath(path);
            if (parent == null || !parent.IsDirectory || name == null) return null;
            if (parent.Children!.ContainsKey(name)) return null;

            var node = FileNode.CreateFile(name, _pool);
            node.SecurityDescriptor = securityDescriptor;
            node.Parent = parent;
            parent.Children[name] = node;
            parent.LastWriteTime = DateTime.UtcNow;
            return node;
        }
    }

    /// <summary>
    /// Create a directory at the given path. Parent must exist.
    /// Returns the new FileNode, or null if parent not found or name already exists.
    /// </summary>
    public FileNode? CreateDirectory(string path, byte[]? securityDescriptor = null)
    {
        lock (_structureLock)
        {
            var (parent, name) = ResolvePath(path);
            if (parent == null || !parent.IsDirectory || name == null) return null;
            if (parent.Children!.ContainsKey(name)) return null;

            var node = FileNode.CreateDirectory(name);
            node.SecurityDescriptor = securityDescriptor;
            node.Parent = parent;
            parent.Children[name] = node;
            parent.LastWriteTime = DateTime.UtcNow;
            return node;
        }
    }

    /// <summary>
    /// Delete a file or empty directory. Returns true if deleted.
    /// </summary>
    public bool Delete(string path)
    {
        lock (_structureLock)
        {
            var node = FindNodeInternal(path);
            if (node == null || node == _root) return false;

            if (node.IsDirectory && node.Children!.Count > 0) return false;

            var parent = node.Parent;
            if (parent?.Children?.Remove(node.Name) == true)
            {
                parent.LastWriteTime = DateTime.UtcNow;
                node.Dispose();
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Move/rename a file or directory.
    /// </summary>
    public bool Move(string oldPath, string newPath, bool replace)
    {
        lock (_structureLock)
        {
            var sourceNode = FindNodeInternal(oldPath);
            if (sourceNode == null || sourceNode == _root) return false;

            var (newParent, newName) = ResolvePath(newPath);
            if (newParent == null || !newParent.IsDirectory || newName == null) return false;

            if (newParent.Children!.TryGetValue(newName, out var existing))
            {
                if (!replace) return false;
                if (existing.IsDirectory && existing.Children!.Count > 0) return false;
                existing.Dispose();
            }

            var oldParent = sourceNode.Parent;
            oldParent?.Children?.Remove(sourceNode.Name);
            if (oldParent != null) oldParent.LastWriteTime = DateTime.UtcNow;

            sourceNode.Name = newName;
            sourceNode.Parent = newParent;
            newParent.Children[newName] = sourceNode;
            newParent.LastWriteTime = DateTime.UtcNow;

            return true;
        }
    }

    /// <summary>
    /// List immediate children of a directory.
    /// </summary>
    public IReadOnlyList<FileNode>? ListDirectory(string path)
    {
        lock (_structureLock)
        {
            var node = FindNodeInternal(path);
            if (node == null || !node.IsDirectory) return null;
            // Must be sorted by name (case-insensitive) for WinFsp marker-based
            // directory enumeration pagination to work correctly.
            return node.Children!.Values
                .OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public void Dispose()
    {
        lock (_structureLock)
        {
            _root.Dispose();
        }
    }

    // --- Internals ---

    private FileNode? FindNodeInternal(string path)
    {
        if (string.IsNullOrEmpty(path) || path == "\\") return _root;

        string[] parts = path.TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        FileNode current = _root;

        foreach (string part in parts)
        {
            if (!current.IsDirectory) return null;
            if (!current.Children!.TryGetValue(part, out var child)) return null;
            current = child;
        }

        return current;
    }

    /// <summary>
    /// Split path into parent node + child name.
    /// </summary>
    private (FileNode? parent, string? name) ResolvePath(string path)
    {
        if (string.IsNullOrEmpty(path) || path == "\\") return (null, null);

        string[] parts = path.TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return (null, null);

        FileNode current = _root;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (!current.IsDirectory) return (null, null);
            if (!current.Children!.TryGetValue(parts[i], out var child)) return (null, null);
            current = child;
        }

        return (current, parts[^1]);
    }
}
