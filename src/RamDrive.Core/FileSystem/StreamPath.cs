namespace RamDrive.Core.FileSystem;

/// <summary>
/// Parsing helpers for NTFS named streams (Alternate Data Streams).
///
/// <para>A named stream is addressed with a <c>':'</c> in the LAST path component, e.g.
/// <c>\dir\file.txt:stream1</c>; the main (unnamed) stream is <c>\dir\file.txt</c>.
/// Streams may be attached to files, directories and the volume root (mirroring NTFS).</para>
///
/// <para>The stream part may carry an explicit <c>:$DATA</c> type suffix, which is
/// equivalent to the plain name (<c>file.txt::$DATA</c> == <c>file.txt</c>,
/// <c>file.txt:s1:$DATA</c> == <c>file.txt:s1</c>).</para>
///
/// <para>The stream separator is the FIRST <c>':'</c> in the leaf component: Windows file
/// names cannot contain <c>':'</c>, so any <c>':'</c> necessarily starts a stream name.
/// This mirrors WinFsp memfs (<c>wcschr(FileName, L':')</c>). Paths handled here are the
/// internal, root-relative form (leading <c>'\'</c>, no drive letter), so there is no
/// drive-letter colon ambiguity to worry about.</para>
/// </summary>
internal static class StreamPath
{
    private const string DataSuffix = ":$DATA";
    private const string DataOnly = "$DATA";

    /// <summary>
    /// Index of the stream-separating <c>':'</c> within the final path component,
    /// or <c>-1</c> when the path has no named stream.
    /// </summary>
    public static int SeparatorIndex(string path)
    {
        if (string.IsNullOrEmpty(path)) return -1;
        int leafStart = path.LastIndexOf('\\') + 1;
        return path.IndexOf(':', leafStart);
    }

    /// <summary>True when the path addresses a named stream (has a <c>':'</c> in its leaf).</summary>
    public static bool HasStream(string path) => SeparatorIndex(path) >= 0;

    /// <summary>
    /// Split a leaf name into <c>(baseName, streamName)</c>. When there is no stream,
    /// <paramref name="streamName"/> is <c>""</c> and <c>baseName == name</c>.
    /// The returned <c>streamName</c> has any <c>:$DATA</c> suffix removed, so the implicit
    /// main stream yields <c>""</c> even for the explicit <c>name::$DATA</c> form.
    /// </summary>
    public static (string BaseName, string StreamName) SplitLeaf(string name)
    {
        int idx = name.IndexOf(':');
        if (idx < 0) return (name, string.Empty);
        string stream = name[(idx + 1)..];
        if (stream.Equals(DataOnly, StringComparison.OrdinalIgnoreCase)) return (name[..idx], string.Empty);
        if (stream.EndsWith(DataSuffix, StringComparison.OrdinalIgnoreCase)) stream = stream[..^DataSuffix.Length];
        return (name[..idx], stream);
    }

    /// <summary>Base (main-stream) path of <paramref name="path"/>; equals the input when there is no stream.</summary>
    public static string GetBasePath(string path)
    {
        int idx = SeparatorIndex(path);
        return idx < 0 ? path : path[..idx];
    }

    /// <summary>
    /// Normalize an explicit <c>:$DATA</c> type suffix away, mapping
    /// <c>\f::$DATA</c> to <c>\f</c> and <c>\f:s:$DATA</c> to <c>\f:s</c>.
    /// </summary>
    public static string Normalize(string path)
    {
        int idx = SeparatorIndex(path);
        if (idx < 0) return path;
        string stream = path[(idx + 1)..];
        if (stream.Equals(DataOnly, StringComparison.OrdinalIgnoreCase)) return path[..idx];
        if (stream.EndsWith(DataSuffix, StringComparison.OrdinalIgnoreCase)) return path[..^DataSuffix.Length];
        return path;
    }
}
