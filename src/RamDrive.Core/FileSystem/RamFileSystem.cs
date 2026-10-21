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
            return FindNodeInternal(StreamPath.Normalize(path));
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
    /// Enumerate the named streams (Alternate Data Streams) attached to the object at
    /// <paramref name="basePath"/>. A stream path is accepted and normalized to its base.
    /// Returns stream-name/node pairs sorted by stream name. Streams of files, directories
    /// and the volume root are all supported (NTFS semantics).
    /// </summary>
    public IReadOnlyList<(string StreamName, FileNode Node)> GetNamedStreams(string basePath)
    {
        basePath = StreamPath.GetBasePath(StreamPath.Normalize(basePath));
        lock (_structureLock)
        {
            if (string.IsNullOrEmpty(basePath) || basePath == "\\")
                return StreamsOf(_root, string.Empty);

            var (parent, baseName) = ResolvePath(basePath);
            if (parent == null || baseName == null) return Array.Empty<(string, FileNode)>();
            return StreamsOf(parent, baseName);
        }
    }

    /// <summary>
    /// Named streams of the object addressed by the node itself, keyed off its CURRENT
    /// parent and name (taken under the structure lock). Use this when a caller holds a
    /// node/handle rather than a pathname: the pathname may be stale after another handle
    /// renamed the object or its parent, whereas <see cref="FileNode.Name"/> and
    /// <see cref="FileNode.Parent"/> are kept current by <see cref="Move"/>.
    ///
    /// <para>A node that is ITSELF a named stream (name <c>base:stream</c>) reports the
    /// streams of its BASE object, mirroring NTFS: querying the streams of a stream handle
    /// lists the owning file's streams, not nothing.</para>
    ///
    /// <para>The volume root has no parent; its streams are the root's own stream children
    /// (keys starting with <c>':'</c>), so they are collected directly.</para>
    /// </summary>
    public IReadOnlyList<(string StreamName, FileNode Node)> GetNamedStreamsForNode(FileNode node)
    {
        lock (_structureLock)
        {
            // A named stream node ("base:stream") is addressed as its base object.
            int colon = node.Name.IndexOf(':');
            if (colon >= 0)
            {
                var owner = node.Parent;
                if (owner == null) return Array.Empty<(string, FileNode)>();
                string baseName = node.Name[..colon];
                // An empty base name means a stream on the volume root ("\:stream").
                if (baseName.Length == 0) return StreamsOf(_root, string.Empty);
                if (!owner.Children!.TryGetValue(baseName, out var baseNode))
                    return Array.Empty<(string, FileNode)>();
                node = baseNode;
            }

            // Root: Parent is null and Name is empty; its streams live in its own Children.
            if (ReferenceEquals(node, _root))
                return StreamsOf(_root, string.Empty);

            var parent = node.Parent;
            if (parent?.Children == null) return Array.Empty<(string, FileNode)>();
            return StreamsOf(parent, node.Name);
        }
    }

    /// <summary>
    /// Collect the STREAM children of <paramref name="parent"/> whose name is
    /// <c>baseName + ":" + ...</c> (i.e. the named streams of the sibling named
    /// <paramref name="baseName"/>). Caller must hold <see cref="_structureLock"/>.
    /// </summary>
    private static IReadOnlyList<(string StreamName, FileNode Node)> StreamsOf(FileNode parent, string baseName)
    {
        string prefix = baseName + ":";
        var result = new List<(string, FileNode)>();
        foreach (var kv in parent.Children!)
        {
            if (kv.Key.IndexOf(':') < 0) continue;
            if (!kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            result.Add((kv.Key[prefix.Length..], kv.Value));
        }
        result.Sort(static (a, b) => string.Compare(a.Item1, b.Item1, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    /// <summary>
    /// Collect the STREAM children of <paramref name="parent"/> whose name is
    /// <c>baseName + ":" + ...</c> (i.e. the named streams of the sibling named
    /// <paramref name="baseName"/>). Caller must hold <see cref="_structureLock"/>.
    /// </summary>
    private static List<KeyValuePair<string, FileNode>> CollectStreams(FileNode parent, string baseName)
    {
        string prefix = baseName + ":";
        var result = new List<KeyValuePair<string, FileNode>>();
        foreach (var kv in parent.Children!)
        {
            if (kv.Key.IndexOf(':') < 0) continue;
            if (!kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            result.Add(kv);
        }
        return result;
    }

    /// <summary>
    /// Count the REAL (non-stream) children of <paramref name="dir"/>. Named streams are
    /// attached data, not directory entries, so they never make a directory "non-empty".
    /// </summary>
    private static int RealChildCount(FileNode dir)
    {
        if (dir.Children == null) return 0;
        int count = 0;
        foreach (var name in dir.Children.Keys)
            if (name.IndexOf(':') < 0) count++;
        return count;
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
        path = StreamPath.Normalize(path);
        lock (_structureLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var (parent, name) = ResolvePath(path);
            if (parent == null || !parent.IsDirectory || name == null) return null;
            if (parent.Children!.ContainsKey(name)) return null;

            // Named stream: the base object (file or directory — including the root) must
            // already exist. Streams are always data streams (never directories), and they
            // live as siblings of the base object in the tree, keyed by "base:stream".
            //
            // An EMPTY base name (path shape "\:stream") is only valid when the parent is
            // the volume root: NTFS allows a stream directly on the root ("C:\:ads") but
            // REJECTS the same syntax inside any subdirectory ("C:\dir\:ads" →
            // ERROR_INVALID_NAME). Streams on named objects ("dir:ads", "file.txt:ads")
            // remain valid anywhere.
            int colon = name.IndexOf(':');
            if (colon >= 0)
            {
                string baseName = name[..colon];
                if (baseName.Length == 0)
                {
                    if (parent != _root) return null;
                }
                else if (!parent.Children!.ContainsKey(baseName))
                {
                    return null;
                }
            }

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
        path = StreamPath.Normalize(path);
        lock (_structureLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var (parent, name) = ResolvePath(path);
            if (parent == null || !parent.IsDirectory || name == null) return null;
            // A named stream is a data stream and can never be a directory.
            if (name.IndexOf(':') >= 0) return null;
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
    ///
    /// <para>Deleting a base object (no named stream in the path) also deletes ALL of its
    /// named streams (NTFS semantics). Deleting a single named stream removes only that
    /// stream and leaves the base object intact.</para>
    /// </summary>
    public bool Delete(string path)
    {
        path = StreamPath.Normalize(path);
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
        // Named streams are attached data, not directory entries: a directory whose only
        // "children" are streams (keys containing ':') is still empty and deletable.
        if (node == _root || !node.IsLinked || !IsEmptyForDeletionUnlocked(node)) return false;
        var parent = node.Parent;
        if (parent?.Children == null ||
            !parent.Children.TryGetValue(node.Name, out var current) ||
            !ReferenceEquals(current, node)) return false;

        string name = node.Name;
        parent.Children.Remove(name);
        parent.LastWriteTime = DateTime.UtcNow;
        RetireNode(node);

        // Deleting a base object cascades to its named streams (NTFS semantics); deleting
        // a single stream leaves its base intact.
        if (!StreamPath.HasStream(name))
        {
            foreach (var (sname, snode) in CollectStreams(parent, name))
            {
                parent.Children.Remove(sname);
                RetireNode(snode);
            }
        }
        return true;
    }

    /// <summary>
    /// Mark <paramref name="node"/> as unlinked and dispose it now, or retain its backing
    /// content until the final open context closes. Caller must hold <see cref="_structureLock"/>.
    /// </summary>
    private void RetireNode(FileNode node)
    {
        node.Parent = null;
        node.IsLinked = false;
        if (node.OpenReferences == 0) node.Dispose();
        else _unlinkedNodes.Add(node);
    }

    /// <summary>
    /// Move/rename a file or directory.
    ///
    /// <para>Only base objects may be moved. Moving a base object renames all of its named
    /// streams along with it (<c>old:s</c> → <c>new:s</c>); a single named stream cannot be
    /// relocated on its own (NTFS semantics).</para>
    /// </summary>
    public bool Move(string oldPath, string newPath, bool replace)
    {
        oldPath = StreamPath.Normalize(oldPath);
        newPath = StreamPath.Normalize(newPath);
        lock (_structureLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var sourceNode = FindNodeInternal(oldPath);
            if (sourceNode == null || sourceNode == _root) return false;

            // Moving an individual named stream is not supported.
            if (StreamPath.HasStream(sourceNode.Name)) return false;

            var (newParent, newName) = ResolvePath(newPath);
            if (newParent == null || !newParent.IsDirectory || newName == null) return false;
            // Destination must be a base object too.
            if (StreamPath.HasStream(newName)) return false;

            // A destination that resolves to the source node itself is not a replacement:
            // Children is case-insensitive, so a case-only rename ("a.txt" -> "A.txt")
            // finds the node being moved. Treating it as a replacement would remove and
            // Dispose the source (and then its streams), corrupting the tree. The memfs
            // reference guards with ReferenceEquals for the same reason; a case-only
            // rename must succeed regardless of the replace flag, as on NTFS.
            if (newParent.Children!.TryGetValue(newName, out var existing)
                && !ReferenceEquals(existing, sourceNode))
            {
                if (!replace || !UnlinkNode(existing)) return false;
            }

            string oldName = sourceNode.Name;
            var oldParent = sourceNode.Parent;
            oldParent?.Children?.Remove(oldName);
            if (oldParent != null) oldParent.LastWriteTime = DateTime.UtcNow;

            sourceNode.Name = newName;
            sourceNode.Parent = newParent;
            newParent.Children[newName] = sourceNode;
            newParent.LastWriteTime = DateTime.UtcNow;

            // Carry the named streams over to the new base name.
            if (oldParent != null)
            {
                foreach (var (sname, snode) in CollectStreams(oldParent, oldName))
                {
                    oldParent.Children!.Remove(sname);
                    string suffix = sname[oldName.Length..]; // ":stream"
                    snode.Name = newName + suffix;
                    snode.Parent = newParent;
                    newParent.Children[snode.Name] = snode;
                }
            }

            return true;
        }
    }

    /// <summary>
    /// List immediate children of a directory.
    /// </summary>
    public IReadOnlyList<FileNode>? ListDirectory(string path)
    {
        path = StreamPath.Normalize(path);
        lock (_structureLock)
        {
            var node = FindNodeInternal(path);
            if (node == null || !node.IsDirectory) return null;
            // Must be sorted by name (case-insensitive) for WinFsp marker-based
            // directory enumeration pagination to work correctly.
            // Named streams (ADS) are attached data, never directory entries.
            return node.Children!.Values
                .Where(n => n.Name.IndexOf(':') < 0)
                .OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    /// <summary>
    /// True when a directory carries no deletable entries. Named streams are attached
    /// data and do not count. Takes the structure lock and works on the node itself, so a
    /// caller holding a handle (rather than a pathname) can ask safely: enumerating
    /// <c>Children</c> outside the lock, or re-resolving a cached pathname, races the
    /// create/move/delete paths. Non-directories are always "empty" for deletion.
    /// </summary>
    public bool IsEmptyForDeletion(FileNode node)
    {
        lock (_structureLock)
        {
            return IsEmptyForDeletionUnlocked(node);
        }
    }

    /// <summary>Caller must hold <see cref="_structureLock"/>.</summary>
    private static bool IsEmptyForDeletionUnlocked(FileNode node)
    {
        if (!node.IsDirectory) return true;
        foreach (var child in node.Children!)
            if (child.Key.IndexOf(':') < 0) return false;
        return true;
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
