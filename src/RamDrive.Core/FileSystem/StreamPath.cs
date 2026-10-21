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
/// <c>file.txt:s1:$DATA</c> == <c>file.txt:s1</c>). Only a SECOND colon introduces the
/// type: <c>file.txt:$DATA</c> is a stream literally named <c>$DATA</c>, not the main
/// stream.</para>
///
/// <para>The stream separator is the FIRST <c>':'</c> in the leaf component: Windows file
/// names cannot contain <c>':'</c>, so any <c>':'</c> necessarily starts a stream name.
/// This mirrors WinFsp memfs (<c>wcschr(FileName, L':')</c>). Paths handled here are the
/// internal, root-relative form (leading <c>'\'</c>, no drive letter), so there is no
/// drive-letter colon ambiguity to worry about.</para>
/// </summary>
internal static class StreamPath
{
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
    /// Split the text following the stream-separating <c>':'</c> into
    /// <c>(StreamName, IsMainStream)</c>. The text is <c>name[:type]</c>; a leading
    /// <c>':'</c> means the name is empty (the main stream), and a trailing
    /// <c>:type</c> suffix is discarded (only <c>$DATA</c> is a valid stream type, so in
    /// practice that is what it drops).
    ///
    /// <para>NTFS semantics: the main stream has an EMPTY name, written <c>f::$DATA</c>
    /// or <c>f:</c>. A literal <c>$DATA</c> in the NAME position (<c>f:$DATA</c>) is an
    /// ordinary named stream called <c>$DATA</c>, NOT the main stream -- only a second
    /// colon introduces the stream type.</para>
    /// </summary>
    private static (string StreamName, bool IsMainStream) ParseStream(string afterColon)
    {
        // "f::..." or "f:" -- empty name, main stream. The "f::anything" shape is
        // lenient here; Win32 rejects a second ':' in the leaf, so it is unreachable.
        if (afterColon.Length == 0 || afterColon[0] == ':')
            return (string.Empty, true);

        // "name[:type]" -- split on a second colon; anything after it is the type.
        int typeColon = afterColon.IndexOf(':');
        string name = typeColon < 0 ? afterColon : afterColon[..typeColon];

        // A leading empty name is impossible here (handled above), so this is a real
        // named stream -- including one literally called "$DATA".
        return (name, false);
    }

    /// <summary>
    /// Split a leaf name into <c>(baseName, streamName)</c>. When there is no stream,
    /// <paramref name="streamName"/> is <c>""</c> and <c>baseName == name</c>. The
    /// main stream yields <c>""</c> for both the plain and the explicit
    /// <c>name::$DATA</c> forms; a stream literally named <c>$DATA</c> is preserved.
    /// </summary>
    public static (string BaseName, string StreamName) SplitLeaf(string name)
    {
        int idx = name.IndexOf(':');
        if (idx < 0) return (name, string.Empty);
        var (streamName, _) = ParseStream(name[(idx + 1)..]);
        return (name[..idx], streamName);
    }

    /// <summary>Base (main-stream) path of <paramref name="path"/>; equals the input when there is no stream.</summary>
    public static string GetBasePath(string path)
    {
        int idx = SeparatorIndex(path);
        return idx < 0 ? path : path[..idx];
    }

    /// <summary>
    /// Canonicalize a stream path: map the explicit main-stream forms to the plain base
    /// (<c>\f::$DATA</c> and <c>\f:</c> to <c>\f</c>) and drop an explicit
    /// <c>:$DATA</c> type from a named stream (<c>\f:s:$DATA</c> to <c>\f:s</c>). A
    /// stream whose NAME is literally <c>$DATA</c> is left as-is (<c>\f:$DATA</c>),
    /// because only a second colon introduces the stream type.
    /// </summary>
    public static string Normalize(string path)
    {
        int idx = SeparatorIndex(path);
        if (idx < 0) return path;
        var (streamName, isMain) = ParseStream(path[(idx + 1)..]);
        if (isMain) return path[..idx];
        return streamName.Length == path.Length - idx - 1
            ? path                                    // no type suffix: already canonical
            : path[..(idx + 1 + streamName.Length)];  // drop the ":type" suffix
    }
}
