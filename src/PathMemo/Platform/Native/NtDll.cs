using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PathMemo.Platform.Native;

/// <summary>
/// The one thing Win32 cannot express: opening a child <b>relative to an already open
/// directory handle</b> (README section 9.5).
/// </summary>
/// <remarks>
/// <c>CreateFileW</c> only takes a path, and a path is re-resolved from the volume root on
/// every call - which is what lets an attacker swap a directory for a junction between the
/// check and the delete. <c>NtOpenFile</c> with <c>RootDirectory</c> set resolves one name
/// inside one open directory: there is no prefix left to redirect.
/// </remarks>
internal static partial class NtDll
{
    private const string Dll = "ntdll.dll";

    internal const uint ObjCaseInsensitive = 0x00000040;

    // CreateOptions / OpenOptions.
    internal const uint FileDirectoryFile = 0x00000001;
    internal const uint FileSynchronousIoNonAlert = 0x00000020;
    internal const uint FileNonDirectoryFile = 0x00000040;
    internal const uint FileOpenForBackupIntent = 0x00004000;
    internal const uint FileOpenReparsePoint = 0x00200000;

    internal const int StatusSuccess = 0;

    [LibraryImport(Dll, EntryPoint = "NtOpenFile")]
    internal static unsafe partial int NtOpenFile(
        nint* fileHandle,
        uint desiredAccess,
        ObjectAttributes* objectAttributes,
        IoStatusBlock* ioStatusBlock,
        uint shareAccess,
        uint openOptions);

    /// <summary>FILE_RENAME_INFORMATION, the NT-level class behind a rename.</summary>
    internal const int FileRenameInformation = 10;

    [LibraryImport(Dll, EntryPoint = "NtSetInformationFile")]
    internal static unsafe partial int NtSetInformationFile(
        nint fileHandle,
        IoStatusBlock* ioStatusBlock,
        void* fileInformation,
        uint length,
        int fileInformationClass);

    [LibraryImport(Dll, EntryPoint = "RtlNtStatusToDosError")]
    internal static partial int RtlNtStatusToDosError(int status);

    /// <summary>
    /// Opens <paramref name="name"/> inside <paramref name="parent"/>, never following a
    /// reparse point and never leaving that directory.
    /// </summary>
    /// <param name="name">A single path segment. Anything else is refused by the caller.</param>
    internal static unsafe SafeFileHandle? OpenRelative(
        SafeFileHandle parent, ReadOnlySpan<char> name, uint access, uint options, out int error)
    {
        var added = false;
        parent.DangerousAddRef(ref added);

        try
        {
            // An empty name is not a mistake: with RootDirectory set it reopens that same
            // object with different access - the only way to widen a handle without going
            // back to a path (see Reopen).
            Span<char> empty = stackalloc char[1];
            fixed (char* namePtr = name.IsEmpty ? empty : name)
            {
                var unicode = new UnicodeString
                {
                    Length = (ushort)(name.Length * sizeof(char)),
                    MaximumLength = (ushort)(name.Length * sizeof(char)),
                    Buffer = (nint)namePtr,
                };

                var attributes = new ObjectAttributes
                {
                    Length = (uint)sizeof(ObjectAttributes),
                    RootDirectory = parent.DangerousGetHandle(),
                    ObjectName = (nint)(&unicode),
                    Attributes = ObjCaseInsensitive,
                };

                nint handle = 0;
                var iosb = default(IoStatusBlock);

                var status = NtOpenFile(&handle, access, &attributes, &iosb,
                    FileApi.ShareAll, options | FileOpenReparsePoint);

                if (status != StatusSuccess)
                {
                    error = RtlNtStatusToDosError(status);
                    return null;
                }

                error = 0;
                return new SafeFileHandle(handle, ownsHandle: true);
            }
        }
        finally
        {
            if (added) parent.DangerousRelease();
        }
    }

    /// <summary>
    /// Reopens what <paramref name="handle"/> already refers to, with different access.
    /// </summary>
    /// <remarks>
    /// The guard opens a path once and judges the result; the deleter then needs
    /// <c>FILE_LIST_DIRECTORY</c> on the directories it has to walk. Asking for it up front
    /// would mean asking for read access to every file too, and files another process holds
    /// open would start failing for no reason. Reopening from the handle costs one call and
    /// resolves no path, so the identity established by the guard still holds.
    /// </remarks>
    internal static SafeFileHandle? Reopen(SafeFileHandle handle, uint access, uint options, out int error) =>
        OpenRelative(handle, ReadOnlySpan<char>.Empty, access, options, out error);
}

/// <summary>UNICODE_STRING. Lengths are in bytes, and the buffer is not NUL-terminated.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct UnicodeString
{
    public ushort Length;
    public ushort MaximumLength;
    public nint Buffer;
}

/// <summary>OBJECT_ATTRIBUTES. <see cref="Length"/> must be the struct size or the call fails.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct ObjectAttributes
{
    public uint Length;
    public nint RootDirectory;
    public nint ObjectName;
    public uint Attributes;
    public nint SecurityDescriptor;
    public nint SecurityQualityOfService;
}

[StructLayout(LayoutKind.Sequential)]
internal struct IoStatusBlock
{
    public nint Status;
    public nint Information;
}
