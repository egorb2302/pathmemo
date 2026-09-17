namespace PathMemo.Snapshots;

/// <summary>
/// Per-node facts that change how a node is counted and displayed.
/// One byte in the snapshot (README section 5.3).
/// </summary>
[Flags]
internal enum NodeFlags : byte
{
    None = 0,

    /// <summary>Directory rather than file.</summary>
    Directory = 1 << 0,

    /// <summary>Junction, symlink or mount point. Never recursed into; counts as 0 bytes.</summary>
    Reparse = 1 << 1,

    /// <summary>
    /// A second (or later) hard link to a file already counted elsewhere in this snapshot.
    /// Contributes 0 to unique-allocated totals (README section 3.2).
    /// </summary>
    HardlinkAlias = 1 << 2,

    /// <summary>Cloud placeholder: data lives in OneDrive/Dropbox, not on this disk.</summary>
    CloudOnly = 1 << 3,

    /// <summary>Allocated is materially below logical: sparse or NTFS-compressed.</summary>
    Sparse = 1 << 4,

    /// <summary>Inside pathmemo's own data directory. Shown, but never a deletion target.</summary>
    SelfData = 1 << 5,

    /// <summary>EFS-encrypted. Cannot be hashed for duplicate detection.</summary>
    Encrypted = 1 << 6,

    /// <summary>Enumeration of this directory failed; its subtree total is incomplete.</summary>
    Incomplete = 1 << 7,
}
