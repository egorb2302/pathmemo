using System.IO.Enumeration;
using System.Runtime.InteropServices;
using System.Text;
using PathMemo.Platform.Native;
using PathMemo.Snapshots;

namespace PathMemo.Scanning;

/// <summary>One directory entry as read straight out of the kernel's directory buffer.</summary>
/// <remarks>
/// <see cref="NameOffset"/> points into the owning worker's <see cref="NameBlobBuilder"/>
/// rather than holding a <c>string</c>. A million retained name strings measured ~100 MB
/// of live data and, worse, drove the GC heap to 430 MB; interning from the span the
/// kernel already gave us allocates nothing (README section 17.3).
/// </remarks>
internal struct RawEntry
{
    internal int NameOffset;
    internal long Logical;
    internal long Allocated;
    internal uint Mtime;
    internal uint Attributes;
    internal NodeFlags Flags;

    /// <summary>
    /// Hard link count, or 0 when it was not looked up (files under the threshold).
    /// When it is above 1 the file id is in the lister's <see cref="DirectoryLister.PendingLinks"/>,
    /// kept apart so the million entries that have one link pay nothing for it.
    /// </summary>
    internal byte LinkCount;
}

/// <summary>A multi-link file's identity: which entry of the directory, and its 128-bit id.</summary>
internal readonly record struct HardlinkRef(int EntryIndex, ulong IdLow, ulong IdHigh);

/// <summary>
/// Enumerates one directory level, projecting each entry directly from the
/// <c>FILE_FULL_DIR_INFO</c> the kernel already returned.
/// </summary>
/// <remarks>
/// <para>
/// This exists instead of <c>Directory.EnumerateFileSystemEntries</c> for two reasons
/// (README section 4.4):
/// </para>
/// <list type="bullet">
/// <item>that API returns a full path <c>string</c> per entry - a million allocations;</item>
/// <item>getting a size from it then costs a second syscall per file, although the size
/// was already sitting in the buffer the kernel filled.</item>
/// </list>
/// <para>
/// One instance per scan worker: it owns that worker's name blob and a single cached
/// transform delegate, so neither is allocated per directory.
/// </para>
/// </remarks>
internal sealed class DirectoryLister
{
    private static readonly EnumerationOptions Options = new()
    {
        // Recursion is driven by the work queue so it can be work-stolen, not by the BCL:
        // a single recursive enumeration cannot be partitioned across workers.
        RecurseSubdirectories = false,

        // Errors are part of the report, not something to swallow (README section 4.9).
        IgnoreInaccessible = false,

        // Nothing is hidden. Skipping Hidden|System - the BCL default - would hide
        // hiberfil.sys, pagefile.sys and most of AppData, i.e. the whole point.
        AttributesToSkip = 0,

        // FIND_FIRST_EX_LARGE_FETCH: fewer round trips into the kernel.
        BufferSize = 64 * 1024,

        MatchType = MatchType.Win32,
        ReturnSpecialDirectories = false,
    };

    private readonly FileSystemEnumerable<RawEntry>.FindTransform _transform;
    private readonly bool _resolveAllocatedSize;
    private List<RawEntry>? _into;

    /// <summary>Multi-link files seen by the last <see cref="TryList"/>, in entry order.</summary>
    internal List<HardlinkRef> PendingLinks { get; } = new(8);

    internal DirectoryLister(NameBlobBuilder blob, byte blobId, bool resolveAllocatedSize)
    {
        Blob = blob;
        BlobId = blobId;
        _resolveAllocatedSize = resolveAllocatedSize;

        // Bound once per worker. A closure created per directory would be 360k
        // delegate allocations on a full C: scan.
        _transform = Project;
    }

    internal NameBlobBuilder Blob { get; }
    internal byte BlobId { get; }

    /// <summary>
    /// Reads one directory. Returns false with <paramref name="error"/> set if the
    /// directory could not be enumerated at all.
    /// </summary>
    internal bool TryList(string directory, List<RawEntry> into, out ScanError? error)
    {
        error = null;
        into.Clear();
        PendingLinks.Clear();
        _into = into;

        try
        {
            foreach (var entry in new FileSystemEnumerable<RawEntry>(directory, _transform, Options))
                into.Add(entry);

            return true;
        }
        catch (Exception ex)
        {
            error = Classify(directory, ex);
            return false;
        }
        finally
        {
            _into = null;
        }
    }

    private RawEntry Project(ref FileSystemEntry entry)
    {
        var attributes = entry.Attributes;
        var flags = NodeFlags.None;

        if (entry.IsDirectory) flags |= NodeFlags.Directory;
        if ((attributes & FileAttributes.ReparsePoint) != 0) flags |= NodeFlags.Reparse;
        if ((attributes & FileAttributes.Encrypted) != 0) flags |= NodeFlags.Encrypted;

        // A cloud placeholder occupies ~0 bytes locally. Reading one would download it,
        // so it is flagged here and never opened later (README section 4.6, threat T10).
        if ((attributes & (FileAttributes)0x00400000) != 0 ||   // RECALL_ON_DATA_ACCESS
            (attributes & (FileAttributes)0x00040000) != 0 ||   // RECALL_ON_OPEN
            (attributes & FileAttributes.Offline) != 0)
            flags |= NodeFlags.CloudOnly;

        var isDirectory = entry.IsDirectory;
        var logical = isDirectory ? 0 : entry.Length;
        var untracked = (flags & (NodeFlags.Reparse | NodeFlags.CloudOnly)) != 0;

        var allocated = untracked || isDirectory ? 0 : logical;
        byte linkCount = 0;
        ulong idLow = 0, idHigh = 0;

        if (_resolveAllocatedSize && !isDirectory && !untracked && logical > 0)
        {
            allocated = QueryAllocatedSize(entry.Directory, entry.FileName, logical);

            // A file id needs a handle, which is too dear per file but cheap for the few
            // percent of files above 1 MB - and those are the ones whose double counting
            // would move a total (README section 3.2).
            if (logical >= HardlinkThreshold)
            {
                QueryLinks(entry.Directory, entry.FileName, out linkCount, out idLow, out idHigh);

                // The transform runs just before the caller appends this entry, so the
                // list's current count is this entry's index.
                if (linkCount > 1 && _into is { } into)
                    PendingLinks.Add(new HardlinkRef(into.Count, idLow, idHigh));
            }
        }

        if (allocated < logical - (logical >> 4)) flags |= NodeFlags.Sparse;

        return new RawEntry
        {
            NameOffset = Blob.Intern(entry.FileName),
            Logical = logical,
            Allocated = allocated,
            Mtime = SnapshotTime.FromDateTime(entry.LastWriteTimeUtc.UtcDateTime),
            Attributes = (uint)attributes,
            Flags = flags,
            LinkCount = linkCount,
        };
    }

    /// <summary>Files at or above this size get their hard link count resolved in walk mode.</summary>
    internal const long HardlinkThreshold = 1L << 20;

    /// <summary>
    /// Opens the file for attributes only (no data access, reparse points not followed)
    /// and reads its link count; the file id is fetched only when there is more than
    /// one link. Failure leaves the count at 0, meaning "unknown", never "one".
    /// </summary>
    internal static unsafe void QueryLinks(
        ReadOnlySpan<char> directory, ReadOnlySpan<char> name,
        out byte linkCount, out ulong idLow, out ulong idHigh)
    {
        linkCount = 0;
        idLow = 0;
        idHigh = 0;

        var rented = ComposePath(directory, name);
        Span<char> buffer = rented;

        try
        {
            nint handle;
            fixed (char* p = buffer)
            {
                handle = Kernel32Extra.CreateFile(p, Kernel32Extra.FileReadAttributes,
                    Kernel32Extra.FileShareRead | Kernel32Extra.FileShareWrite | Kernel32Extra.FileShareDelete,
                    0, Kernel32Extra.OpenExisting,
                    Kernel32Extra.FileFlagBackupSemantics | Kernel32Extra.FileFlagOpenReparsePoint, 0);
            }

            if (handle == -1 || handle == 0) return;

            try
            {
                FileStandardInfo standard;
                if (!Kernel32Extra.GetFileInformationByHandleEx(handle, Kernel32Extra.FileStandardInfo,
                        &standard, (uint)sizeof(FileStandardInfo)))
                    return;

                linkCount = (byte)Math.Min(standard.NumberOfLinks, 255u);
                if (standard.NumberOfLinks <= 1) return;

                FileIdInfo id;
                if (Kernel32Extra.GetFileInformationByHandleEx(handle, Kernel32Extra.FileIdInfo,
                        &id, (uint)sizeof(FileIdInfo)))
                {
                    idLow = id.FileIdLow;
                    idHigh = id.FileIdHigh;
                }
                else
                {
                    linkCount = 0;      // a count without an identity cannot be deduplicated
                }
            }
            finally
            {
                Kernel32Extra.CloseHandle(handle);
            }
        }
        finally
        {
            System.Buffers.ArrayPool<char>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// Joins directory and name into a NUL-terminated path in a pooled buffer, with the
    /// <c>\\?\</c> prefix once MAX_PATH is exceeded. The caller returns the array.
    /// </summary>
    private static char[] ComposePath(ReadOnlySpan<char> directory, ReadOnlySpan<char> name)
    {
        const string LongPrefix = @"\\?\";

        var needsPrefix = directory.Length + 1 + name.Length >= 255
                       && !directory.StartsWith(LongPrefix);

        var length = (needsPrefix ? LongPrefix.Length : 0)
                   + directory.Length + 1 + name.Length + 1;   // + separator + NUL

        var rented = System.Buffers.ArrayPool<char>.Shared.Rent(Math.Max(length, 512));
        Span<char> buffer = rented;

        var at = 0;
        if (needsPrefix)
        {
            LongPrefix.AsSpan().CopyTo(buffer);
            at += LongPrefix.Length;
        }

        directory.CopyTo(buffer[at..]);
        at += directory.Length;

        if (at > 0 && buffer[at - 1] != Path.DirectorySeparatorChar)
            buffer[at++] = Path.DirectorySeparatorChar;

        name.CopyTo(buffer[at..]);
        at += name.Length;
        buffer[at] = '\0';

        return rented;
    }

    /// <summary>
    /// Physical bytes: cluster-rounded, and lower than logical for sparse or
    /// NTFS-compressed files. This is the number that adds up to "used space"
    /// (README section 3.1).
    /// </summary>
    /// <remarks>
    /// Costs one syscall per file, which is the main reason the MFT scanner - where the
    /// same value comes free - is the preferred path. The path is composed into a stack
    /// buffer so that a million calls allocate nothing.
    /// </remarks>
    internal static unsafe long QueryAllocatedSize(
        ReadOnlySpan<char> directory, ReadOnlySpan<char> name, long fallback)
    {
        const string LongPrefix = @"\\?\";

        var needsPrefix = directory.Length + 1 + name.Length >= 255
                       && !directory.StartsWith(LongPrefix);

        var length = (needsPrefix ? LongPrefix.Length : 0)
                   + directory.Length + 1 + name.Length + 1;   // + separator + NUL

        // Long paths can exceed any sane stack buffer; those are rare enough to rent.
        char[]? rented = length > 512 ? System.Buffers.ArrayPool<char>.Shared.Rent(length) : null;
        Span<char> buffer = rented ?? stackalloc char[512];

        try
        {
            var at = 0;
            if (needsPrefix)
            {
                LongPrefix.AsSpan().CopyTo(buffer);
                at += LongPrefix.Length;
            }

            directory.CopyTo(buffer[at..]);
            at += directory.Length;

            if (at > 0 && buffer[at - 1] != Path.DirectorySeparatorChar)
                buffer[at++] = Path.DirectorySeparatorChar;

            name.CopyTo(buffer[at..]);
            at += name.Length;
            buffer[at] = '\0';

            fixed (char* p = buffer)
            {
                var low = Kernel32Extra.GetCompressedFileSize(p, out var high);

                // INVALID_FILE_SIZE is also a legitimate low word, so the error code has
                // to be checked rather than the return value alone.
                if (low == Kernel32Extra.InvalidFileSize && Marshal.GetLastWin32Error() != 0)
                    return fallback;

                return (long)(((ulong)high << 32) | low);
            }
        }
        finally
        {
            if (rented is not null) System.Buffers.ArrayPool<char>.Shared.Return(rented);
        }
    }

    private static ScanError Classify(string path, Exception ex)
    {
        var code = ex is IOException io ? io.HResult & 0xFFFF : 0;

        var kind = ex switch
        {
            UnauthorizedAccessException => ScanErrorKind.AccessDenied,
            DirectoryNotFoundException => ScanErrorKind.NotFound,
            FileNotFoundException => ScanErrorKind.NotFound,
            PathTooLongException => ScanErrorKind.PathTooLong,
            IOException when code == 32 => ScanErrorKind.SharingViolation,  // ERROR_SHARING_VIOLATION
            IOException when code == 123 => ScanErrorKind.NameInvalid,      // ERROR_INVALID_NAME
            IOException => ScanErrorKind.IoError,
            _ => ScanErrorKind.Unknown,
        };

        return new ScanError(path, kind, code, ex.Message);
    }

    /// <summary>Decodes an interned name back into chars without allocating.</summary>
    internal static int DecodeName(NameBlobBuilder blob, int offset, Span<char> into) =>
        Encoding.UTF8.GetChars(blob.Read(offset), into);
}
