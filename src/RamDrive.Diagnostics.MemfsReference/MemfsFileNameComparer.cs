// Path comparator — ports MemfsFileNameCompare from winfsp/tst/memfs/memfs.cpp.
//
// memfs orders keys with a custom rule that treats ':' (named-stream separator)
// and '\\' (path separator) specially so that sibling files sort before subtree
// children, and named streams sort right after their main file. SortedDictionary
// using this comparator gives the same upper_bound / iteration order as
// std::map<PWSTR, ..., MEMFS_FILE_NODE_LESS>.

namespace RamDrive.Diagnostics.MemfsReference;

internal sealed class MemfsFileNameComparer : IComparer<string>
{
    public static readonly MemfsFileNameComparer CaseInsensitive = new(true);
    public static readonly MemfsFileNameComparer CaseSensitive   = new(false);

    private readonly bool _ci;
    private MemfsFileNameComparer(bool ci) { _ci = ci; }

    public int Compare(string? a, string? b) => CompareStatic(a!, b!, _ci);

    public static int CompareStatic(ReadOnlySpan<char> a, ReadOnlySpan<char> b, bool ci)
    {
        int ai = 0, bi = 0;
        while (ai < a.Length && bi < b.Length)
        {
            char c = (char)0, d = (char)0;
            while (ai < a.Length && (a[ai] == ':' || a[ai] == '\\')) c = a[ai++];
            while (bi < b.Length && (b[bi] == ':' || b[bi] == '\\')) d = b[bi++];

            int cv = c == ':' ? 1 : c == '\\' ? 2 : 0;
            int dv = d == ':' ? 1 : d == '\\' ? 2 : 0;
            if (cv != dv) return cv - dv;

            int aStart = ai, bStart = bi;
            while (ai < a.Length && a[ai] != ':' && a[ai] != '\\') ai++;
            while (bi < b.Length && b[bi] != ':' && b[bi] != '\\') bi++;

            int aLen = ai - aStart, bLen = bi - bStart;
            int n = Math.Min(aLen, bLen);

            int res = ci
                ? a.Slice(aStart, n).CompareTo(b.Slice(bStart, n), StringComparison.OrdinalIgnoreCase)
                : a.Slice(aStart, n).CompareTo(b.Slice(bStart, n), StringComparison.Ordinal);
            if (res == 0) res = aLen - bLen;
            if (res != 0) return res;
        }
        return -(ai >= a.Length ? 1 : 0) + (bi >= b.Length ? 1 : 0);
    }

    public static bool HasPrefix(string a, string b, bool ci)
    {
        if (a.Length < b.Length) return false;
        if (CompareStatic(a.AsSpan(0, b.Length), b.AsSpan(), ci) != 0) return false;
        return a.Length == b.Length
            || (b.Length == 1 && b[0] == '\\')
            || a[b.Length] == '\\' || a[b.Length] == ':';
    }
}
