using System.Runtime.InteropServices;

namespace PathMemo.Platform.Native;

/// <summary>
/// The change journal FSCTLs and the two calls that turn a file reference number back
/// into a path (README section 4.5).
/// </summary>
/// <remarks>
/// <para>
/// Querying the journal needs no privilege at all while reading records from it is
/// administrator-only, and no choice of handle changes that. Measured on this machine,
/// unelevated:
/// </para>
/// <list type="table">
/// <item><term>root directory, any access up to GENERIC_READ</term>
///   <description>query succeeds, read fails with ERROR_ACCESS_DENIED</description></item>
/// <item><term><c>\\.\C:</c> with no access or FILE_READ_ATTRIBUTES</term>
///   <description>query fails with ERROR_INVALID_FUNCTION - such a handle never reaches
///   NTFS at all</description></item>
/// <item><term><c>\\.\C:</c> with GENERIC_READ</term>
///   <description>cannot even be opened</description></item>
/// </list>
/// <para>
/// So the query goes through a root directory handle and the read through the volume, which
/// is the only pair that can work; and that split is why <c>doctor</c> reports the journal
/// state for every user while an incremental scan is an elevated path, like the MFT scan.
/// </para>
/// <para>
/// <c>FSCTL_CREATE_USN_JOURNAL</c> is deliberately absent. Creating a journal changes the
/// volume, and a tool whose whole promise is that looking costs nothing does not quietly
/// enable a kernel facility; the command to do it is printed for the user instead
/// (README sections 4.5, 6.3).
/// </para>
/// </remarks>
internal static partial class Usn
{
    private const string Dll = "kernel32.dll";

    internal const uint FsctlQueryUsnJournal = 0x000900F4;
    internal const uint FsctlReadUsnJournal = 0x000900BB;

    // The journal's own failure codes, each of which means "fall back to a full scan"
    // for a different reason worth saying out loud.
    internal const int ErrorInvalidFunction = 1;            // not NTFS
    internal const int ErrorAccessDenied = 5;
    internal const int ErrorJournalDeleteInProgress = 1178;
    internal const int ErrorJournalNotActive = 1179;
    internal const int ErrorJournalEntryDeleted = 1181;      // the watermark aged out

    /// <summary>Every reason bit. A record for any reason means its directory changed.</summary>
    internal const uint ReasonAll = 0xFFFFFFFF;

    /// <summary>FILE_NAME_NORMALIZED | VOLUME_NAME_DOS.</summary>
    internal const uint FinalPathNormalizedDos = 0x0;

    [LibraryImport(Dll, EntryPoint = "OpenFileById", SetLastError = true)]
    internal static unsafe partial nint OpenFileById(
        nint hVolumeHint,
        FileIdDescriptor* lpFileId,
        uint dwDesiredAccess,
        uint dwShareMode,
        nint lpSecurityAttributes,
        uint dwFlagsAndAttributes);

    [LibraryImport(Dll, EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint GetFinalPathNameByHandle(
        nint hFile, Span<char> lpszFilePath, uint cchFilePath, uint dwFlags);

    /// <summary>
    /// Opens the volume's root directory: the handle that answers
    /// <see cref="FsctlQueryUsnJournal"/> without elevation, and the volume hint
    /// <see cref="OpenFileById"/> needs.
    /// </summary>
    internal static nint OpenRoot(string root) => Kernel32Extra.CreateFile(
        root, Kernel32Extra.FileReadAttributes,
        Kernel32Extra.FileShareRead | Kernel32Extra.FileShareWrite | Kernel32Extra.FileShareDelete,
        0, Kernel32Extra.OpenExisting, Kernel32Extra.FileFlagBackupSemantics, 0);

    /// <summary>
    /// Opens <c>\\.\C:</c> for reading, which is what <see cref="FsctlReadUsnJournal"/>
    /// insists on. Fails with <see cref="ErrorAccessDenied"/> for an ordinary user.
    /// </summary>
    internal static nint OpenVolume(string letter) => Kernel32Extra.CreateFile(
        @"\\.\" + letter.TrimEnd('\\'), Kernel32Extra.GenericRead,
        Kernel32Extra.FileShareRead | Kernel32Extra.FileShareWrite,
        0, Kernel32Extra.OpenExisting, 0, 0);
}

/// <summary>USN_JOURNAL_DATA_V0, the answer to FSCTL_QUERY_USN_JOURNAL. 56 bytes.</summary>
/// <remarks>
/// V0 on purpose. V1 and V2 only add the supported record versions, and asking for a
/// longer buffer than the volume's NTFS version knows how to fill is how this call starts
/// returning ERROR_INVALID_PARAMETER on an older system for no gain.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct UsnJournalData
{
    public ulong UsnJournalId;

    /// <summary>First record still in the journal.</summary>
    public long FirstUsn;

    /// <summary>Where the next record will be written: the watermark to store.</summary>
    public long NextUsn;

    /// <summary>
    /// Records below this are gone for good. Zero means the journal has never been
    /// trimmed, which is what a freshly created one reports.
    /// </summary>
    public long LowestValidUsn;

    public long MaxUsn;
    public ulong MaximumSize;
    public ulong AllocationDelta;
}

/// <summary>READ_USN_JOURNAL_DATA_V0. Exactly 40 bytes, which is how NTFS knows it is V0.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct ReadUsnJournalData
{
    public long StartUsn;
    public uint ReasonMask;
    public uint ReturnOnlyOnClose;
    public ulong Timeout;
    public ulong BytesToWaitFor;
    public ulong UsnJournalId;
}

/// <summary>
/// FILE_ID_DESCRIPTOR for <see cref="Usn.OpenFileById"/>: 24 bytes on x64, of which the
/// last 16 are a union whose first 8 hold the 64-bit NTFS file reference number.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FileIdDescriptor
{
    public uint Size;

    /// <summary>FILE_ID_TYPE: 0 = FileIdType (the 64-bit reference number).</summary>
    public uint Type;

    public ulong FileId;
    public ulong FileIdHigh;
}
