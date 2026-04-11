namespace RamDrive.Core.Configuration;

/// <summary>
/// Represents a node in the initial directory tree configuration.
/// Each key is a subdirectory name; each value is its subtree.
/// An empty node (<c>{}</c>) is a leaf directory with no children.
/// </summary>
public sealed class DirectoryNode : Dictionary<string, DirectoryNode>
{
    public DirectoryNode() : base(StringComparer.OrdinalIgnoreCase) { }
}
