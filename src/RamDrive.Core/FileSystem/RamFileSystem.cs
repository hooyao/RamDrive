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
    private readonly HashSet<FileNode> _unlinkedNodes = new();
    private bool _disposed;

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
    /// This is a borrowed namespace lookup; use OpenNode/CloseNode to retain
    /// backing content across concurrent unlink or replacement.
    /// </summary>
    public FileNode? FindNode(string path)
    {
        lock (_structureLock)
        {
            return FindNodeInternal(path);
        }
    }

    /// <summary>Find a linked node and retain its open context atomically with lookup.</summary>
    public FileNode? OpenNode(string path)
    {
        lock (_structureLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var node = FindNodeInternal(path);
            if (node != null) node.OpenReferences++;
            return node;
        }
    }

    /// <summary>Release an open context. Unlinked data survives until the final Close.</summary>
    public void CloseNode(FileNode node)
    {
        lock (_structureLock)
        {
            // Shutdown follows host stop and has already reclaimed every retained node.
            if (_disposed) return;
            if (node.OpenReferences <= 0)
                throw new InvalidOperationException("The node has no retained open context.");
            node.OpenReferences--;
            if (!node.IsLinked && node.OpenReferences == 0)
            {
                _unlinkedNodes.Remove(node);
                node.Dispose();
            }
        }
    }

    /// <summary>
    /// Create a file at the given path. Parent directory must exist.
    /// Returns the new FileNode, or null if parent not found or name already exists.
    /// </summary>
    public FileNode? CreateFile(string path, byte[]? securityDescriptor = null)
        => CreateFile(path, securityDescriptor, acquireReference: false);

    /// <summary>Create with an optional open reference that must be released with CloseNode.</summary>
    public FileNode? CreateFile(string path, byte[]? securityDescriptor, bool acquireReference)
    {
        lock (_structureLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var (parent, name) = ResolvePath(path);
            if (parent == null || !parent.IsDirectory || name == null) return null;
            if (parent.Children!.ContainsKey(name)) return null;

            var node = FileNode.CreateFile(name, _pool);
            if (acquireReference) node.OpenReferences = 1;
            // When the caller does not supply an SD, inherit the parent's by reference.
            // Copy-on-write contract: SetFileSecurity allocates a fresh byte[] before
            // assigning, so this share is broken on the first explicit ACL change.
            node.SecurityDescriptor = securityDescriptor ?? parent.SecurityDescriptor;
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
        => CreateDirectory(path, securityDescriptor, acquireReference: false);

    /// <summary>Create with an optional open reference that must be released with CloseNode.</summary>
    public FileNode? CreateDirectory(string path, byte[]? securityDescriptor, bool acquireReference)
    {
        lock (_structureLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var (parent, name) = ResolvePath(path);
            if (parent == null || !parent.IsDirectory || name == null) return null;
            if (parent.Children!.ContainsKey(name)) return null;

            var node = FileNode.CreateDirectory(name);
            if (acquireReference) node.OpenReferences = 1;
            // See CreateFile for the inherit-by-reference / copy-on-write contract.
            node.SecurityDescriptor = securityDescriptor ?? parent.SecurityDescriptor;
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
            return node != null && UnlinkNode(node);
        }
    }

    /// <summary>Unlink this exact object, without deleting a new occupant of its old name.</summary>
    public bool Delete(FileNode node)
    {
        lock (_structureLock)
            return !_disposed && UnlinkNode(node);
    }

    private bool UnlinkNode(FileNode node)
    {
        if (node == _root || !node.IsLinked ||
            (node.IsDirectory && node.Children!.Count > 0)) return false;
        var parent = node.Parent;
        if (parent?.Children == null ||
            !parent.Children.TryGetValue(node.Name, out var current) ||
            !ReferenceEquals(current, node)) return false;

        parent.Children.Remove(node.Name);
        parent.LastWriteTime = DateTime.UtcNow;
        node.Parent = null;
        node.IsLinked = false;
        if (node.OpenReferences == 0) node.Dispose();
        else _unlinkedNodes.Add(node);
        return true;
    }

    /// <summary>
    /// Move/rename a file or directory.
    /// </summary>
    public bool Move(string oldPath, string newPath, bool replace)
    {
        lock (_structureLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var sourceNode = FindNodeInternal(oldPath);
            if (sourceNode == null || sourceNode == _root) return false;

            var (newParent, newName) = ResolvePath(newPath);
            if (newParent == null || !newParent.IsDirectory || newName == null) return false;

            if (newParent.Children!.TryGetValue(newName, out var existing))
            {
                if (!ReferenceEquals(existing, sourceNode))
                {
                    if (!replace || !UnlinkNode(existing)) return false;
                }
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
            if (_disposed) return;
            _disposed = true;
            _root.Dispose();
            foreach (var node in _unlinkedNodes) node.Dispose();
            _unlinkedNodes.Clear();
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
