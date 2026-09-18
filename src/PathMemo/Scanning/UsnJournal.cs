using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using PathMemo.Platform.Native;

namespace PathMemo.Scanning;

/// <summary>One record of the change journal, reduced to what a rescan needs.</summary>
/// <remarks>
/// <see cref="ParentFileId"/> is the whole point. Every reason a record can carry -
/// created, deleted, renamed, written, truncated, attributes changed - is a change to the
/// contents of one directory, so a rescan does not need to understand the reasons at all:
/// it needs the set of directories that have to be read again (README section 4.5).
/// </remarks>
internal readonly record struct UsnChange(
    ulong FileId, ulong ParentFileId, uint Reason, uint Attributes, string Name)
{
    internal bool IsDirectory => (Attributes & 0x10) != 0;   // FILE_ATTRIBUTE_DIRECTORY
}

/// <summary>The journal cannot answer for this volume; the caller falls back to a full scan.</summary>
internal sealed class UsnUnavailableException(string message, int win32Code = 0) : Exception(message)
{
    internal int Win32Code { get; } = win32Code;
}

/// <summary>
/// Parsing of the raw <c>FSCTL_READ_USN_JOURNAL</c> output.
/// </summary>
/// <remarks>
/// A pure function over bytes, like <see cref="Mft.MftParser"/> and for the same reason:
/// the shapes that break a parser - a truncated tail, a record length that walks off the
/// end, a version this build has never seen - are all expressible as a byte array in a
/// test, and none of them are expressible as a real volume on demand.
/// </remarks>
internal static class UsnRecords
{
    /// <summary>Output buffer layout: the next USN to ask for, then packed records.</summary>
    internal const int HeaderBytes = 8;

    /// <summary>
    /// Reads every record in one buffer. Returns the USN to continue from, which the
    /// kernel writes into the first eight bytes whether or not any record followed.
    /// </summary>
    /// <remarks>
    /// A record whose length is absurd - zero, negative when read as an int, or reaching
    /// past the returned bytes - stops the walk rather than being guessed at. The records
    /// already collected are good, and the next call starts from a USN the kernel gave us.
    /// </remarks>
    internal static long Parse(ReadOnlySpan<byte> buffer, List<UsnChange> into)
    {
        if (buffer.Length < HeaderBytes) return 0;

        var next = BinaryPrimitives.ReadInt64LittleEndian(buffer);
        var at = HeaderBytes;

        while (at + 8 <= buffer.Length)
        {
            var length = BinaryPrimitives.ReadInt32LittleEndian(buffer[at..]);
            if (length <= 8 || at + length > buffer.Length) break;

            var record = buffer.Slice(at, length);
            if (TryRead(record, out var change)) into.Add(change);

            at += length;
        }

        return next;
    }

    /// <summary>
    /// Decodes one record. V2 (NTFS) and V3 (128-bit ids) differ only in the width of the
    /// two reference numbers and therefore in every offset after them; on NTFS the upper
    /// 64 bits of a V3 id are zero, so both collapse to the same 64-bit number.
    /// </summary>
    internal static bool TryRead(ReadOnlySpan<byte> record, out UsnChange change)
    {
        change = default;
        if (record.Length < 12) return false;

        var major = BinaryPrimitives.ReadUInt16LittleEndian(record[4..]);

        // (file id, parent id, then the fixed tail: usn, timestamp, reason, source,
        // security id, attributes, name length, name offset)
        int idBytes = major switch { 2 => 8, 3 or 4 => 16, _ => 0 };
        if (idBytes == 0) return false;

        var tail = 8 + idBytes * 2;
        if (record.Length < tail + 36) return false;

        var fileId = BinaryPrimitives.ReadUInt64LittleEndian(record[8..]);
        var parentId = BinaryPrimitives.ReadUInt64LittleEndian(record[(8 + idBytes)..]);

        var reason = BinaryPrimitives.ReadUInt32LittleEndian(record[(tail + 16)..]);
        var attributes = BinaryPrimitives.ReadUInt32LittleEndian(record[(tail + 28)..]);
        var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(record[(tail + 32)..]);
        var nameOffset = BinaryPrimitives.ReadUInt16LittleEndian(record[(tail + 34)..]);

        var name = nameOffset + nameLength <= record.Length && nameLength > 0
            ? Encoding.Unicode.GetString(record.Slice(nameOffset, nameLength))
            : "";

        change = new UsnChange(fileId, parentId, reason, attributes, name);
        return true;
    }
}

/// <summary>
/// The USN journal of one volume: its state, and the records written since a saved
/// watermark (README section 4.5).
/// </summary>
internal sealed unsafe class UsnJournal : IDisposable
{
    /// <summary>
    /// One FSCTL returns as much as fits here. 64 KB holds several hundred records, so a
    /// quiet week is one call and a noisy one is a handful.
    /// </summary>
    private const int BufferBytes = 64 * 1024;

    private readonly nint _volume;

    internal UsnJournalData Data { get; }

    private UsnJournal(nint volume, UsnJournalData data)
    {
        _volume = volume;
        Data = data;
    }

    /// <summary>
    /// The journal's state, read through a handle to the volume's root directory. Needs no
    /// privilege, which is what lets <c>doctor</c> report it for an ordinary user.
    /// </summary>
    internal static bool TryQuery(string root, out UsnJournalData data, out int error)
    {
        data = default;
        error = 0;

        var handle = Usn.OpenRoot(root);
        if (handle == -1 || handle == 0)
        {
            error = Marshal.GetLastWin32Error();
            return false;
        }

        try
        {
            bool ok;
            fixed (UsnJournalData* p = &data)
            {
                ok = Kernel32Extra.DeviceIoControl(handle, Usn.FsctlQueryUsnJournal,
                    null, 0, p, (uint)sizeof(UsnJournalData), out _, 0);
            }

            if (!ok) error = Marshal.GetLastWin32Error();
            return ok;
        }
        finally
        {
            Kernel32Extra.CloseHandle(handle);
        }
    }

    /// <summary>
    /// Opens the volume for reading records. Administrator only: <c>\\.\C:</c> with
    /// <c>GENERIC_READ</c> is what the read FSCTL requires, and that is the same door the
    /// MFT scanner goes through (README section 4.3).
    /// </summary>
    internal static UsnJournal Open(string letter, string root)
    {
        if (!TryQuery(root, out var data, out var queryError))
            throw new UsnUnavailableException(Explain(letter, queryError), queryError);

        var volume = Usn.OpenVolume(letter);
        if (volume == -1 || volume == 0)
        {
            var error = Marshal.GetLastWin32Error();
            throw new UsnUnavailableException(
                error == Usn.ErrorAccessDenied
                    ? $"reading {letter}'s change journal needs administrator rights"
                    : $"cannot open {letter} to read its change journal: {new Win32Exception(error).Message}",
                error);
        }

        return new UsnJournal(volume, data);
    }

    /// <summary>
    /// Every record from <paramref name="startUsn"/> up to the journal's current end.
    /// </summary>
    /// <remarks>
    /// The end is the <see cref="UsnJournalData.NextUsn"/> read when the journal was
    /// opened, not "until the kernel stops giving records": on a live system something is
    /// always writing, and a loop that chased the end would follow the disk forever. The
    /// records written during the rescan belong to the next one, which is why the
    /// watermark saved is that same <c>NextUsn</c>.
    /// </remarks>
    internal List<UsnChange> Read(long startUsn, CancellationToken ct)
    {
        var changes = new List<UsnChange>(1024);
        var buffer = new byte[BufferBytes];
        var from = startUsn;

        while (from < Data.NextUsn)
        {
            ct.ThrowIfCancellationRequested();

            var request = new ReadUsnJournalData
            {
                StartUsn = from,
                ReasonMask = Usn.ReasonAll,
                ReturnOnlyOnClose = 0,
                Timeout = 0,
                BytesToWaitFor = 0,
                UsnJournalId = Data.UsnJournalId,
            };

            uint returned;
            bool ok;
            fixed (byte* output = buffer)
            {
                ok = Kernel32Extra.DeviceIoControl(_volume, Usn.FsctlReadUsnJournal,
                    &request, (uint)sizeof(ReadUsnJournalData),
                    output, (uint)buffer.Length, out returned, 0);
            }

            if (!ok)
            {
                var error = Marshal.GetLastWin32Error();
                throw new UsnUnavailableException(Explain(startUsn, error), error);
            }

            if (returned < UsnRecords.HeaderBytes) break;

            var before = changes.Count;
            var next = UsnRecords.Parse(buffer.AsSpan(0, (int)returned), changes);

            // No progress means the journal has nothing more to give: either the buffer
            // held only the header, or a malformed record stopped the walk. Either way,
            // looping on the same USN would spin forever.
            if (next <= from || (changes.Count == before && next == from)) break;
            from = next;
        }

        return changes;
    }

    private static string Explain(string letter, int error) => error switch
    {
        Usn.ErrorJournalNotActive =>
            $"{letter} has no change journal (enable it with: fsutil usn createjournal m=32000000 a=8000000 {letter})",
        Usn.ErrorJournalDeleteInProgress => $"{letter}'s change journal is being deleted",
        Usn.ErrorInvalidFunction => $"{letter} does not support a change journal",
        _ => $"{letter}'s change journal could not be read: {new Win32Exception(error).Message} (error {error})",
    };

    private static string Explain(long startUsn, int error) => error switch
    {
        Usn.ErrorJournalEntryDeleted =>
            $"the journal no longer reaches back to USN {startUsn}: too much has changed since the last scan",
        Usn.ErrorJournalNotActive => "the change journal was switched off since the last scan",
        _ => $"reading the change journal from USN {startUsn} failed: {new Win32Exception(error).Message} (error {error})",
    };

    public void Dispose() => Kernel32Extra.CloseHandle(_volume);
}

/// <summary>
/// Turns file reference numbers back into paths, which is the one thing the journal does
/// not give and the snapshot cannot supply.
/// </summary>
/// <remarks>
/// <para>
/// <c>.pmsnap</c> v1 stores no file identity (README section 5.3), so a record's parent id
/// cannot be looked up in the tree - it has to be asked of the file system.
/// <c>OpenFileById</c> plus <c>GetFinalPathNameByHandle</c> is two syscalls per changed
/// directory, and the answers are cached, so a rescan pays them a few hundred times rather
/// than a few hundred thousand.
/// </para>
/// <para>
/// A reference number that will not open is not an error: a directory that was deleted
/// since the record was written has no path any more, and the change is accounted for
/// anyway when its own parent is re-enumerated. The sequence number in the high bits of
/// the reference number is what makes this safe - a slot reused by a different file fails
/// to open rather than resolving to the wrong path.
/// </para>
/// </remarks>
internal sealed unsafe class FileIdPaths : IDisposable
{
    private readonly nint _hint;
    private readonly Dictionary<ulong, string?> _cache = new(512);

    internal FileIdPaths(string root) => _hint = Usn.OpenRoot(root);

    internal bool Usable => _hint != -1 && _hint != 0;

    /// <summary>How many ids were asked of the file system rather than the cache.</summary>
    internal int Resolved { get; private set; }

    internal string? TryResolve(ulong fileId)
    {
        if (_cache.TryGetValue(fileId, out var cached)) return cached;

        var path = Lookup(fileId);
        _cache[fileId] = path;
        Resolved++;
        return path;
    }

    private string? Lookup(ulong fileId)
    {
        if (!Usable) return null;

        var descriptor = new FileIdDescriptor
        {
            Size = (uint)sizeof(FileIdDescriptor),
            Type = 0,
            FileId = fileId,
        };

        var handle = Usn.OpenFileById(_hint, &descriptor, Kernel32Extra.FileReadAttributes,
            Kernel32Extra.FileShareRead | Kernel32Extra.FileShareWrite | Kernel32Extra.FileShareDelete,
            0, Kernel32Extra.FileFlagBackupSemantics);

        if (handle == -1 || handle == 0) return null;

        try
        {
            Span<char> buffer = stackalloc char[1024];
            var length = Usn.GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length,
                Usn.FinalPathNormalizedDos);

            if (length == 0 || length >= buffer.Length) return null;

            var path = new string(buffer[..(int)length]);

            // GetFinalPathNameByHandle always answers in the \\?\ form. The tree speaks
            // ordinary paths, so the prefix comes off here rather than at every comparison.
            return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
        }
        finally
        {
            Kernel32Extra.CloseHandle(handle);
        }
    }

    public void Dispose()
    {
        if (Usable) Kernel32Extra.CloseHandle(_hint);
    }
}
