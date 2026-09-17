namespace PathMemo.Scanning;

/// <summary>
/// What one enumerated directory produced during a walk scan.
/// Consumed by <see cref="Snapshots.SnapshotBuilder"/>.
/// </summary>
internal sealed class WalkDirResult
{
    internal RawEntry[] Entries = [];

    /// <summary>
    /// Parallel to <see cref="Entries"/>: the directory id of a subdirectory that will be
    /// enumerated in turn, or -1 for a file or an un-entered reparse point.
    /// </summary>
    internal int[] SubdirIds = [];

    /// <summary>Enumeration failed; the subtree under this node is unknown.</summary>
    internal bool Failed;

    /// <summary>
    /// Which worker's name blob <see cref="RawEntry.NameOffset"/> refers to. Workers intern
    /// into their own blob to avoid contending on a shared table; the builder merges them.
    /// </summary>
    internal byte BlobId;
}
