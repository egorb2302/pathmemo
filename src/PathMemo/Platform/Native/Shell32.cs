using System.Runtime.InteropServices;

namespace PathMemo.Platform.Native;

internal static partial class Shell32
{
    private const string Dll = "shell32.dll";

    /// <summary>
    /// <c>SHQUERYRBINFO</c>: natural alignment, so on x64 the two 64-bit fields follow a
    /// padded 32-bit size, 24 bytes in all. Shell checks <see cref="Size"/> and answers
    /// E_INVALIDARG for anything else.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct QueryRecycleBinInfo
    {
        internal uint Size;
        internal long Bytes;
        internal long Items;

        internal static unsafe uint NativeSize => (uint)sizeof(QueryRecycleBinInfo);
    }

    /// <summary>Per-volume Recycle Bin totals, no elevation needed.</summary>
    [LibraryImport(Dll, EntryPoint = "SHQueryRecycleBinW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int SHQueryRecycleBin(string? pszRootPath, ref QueryRecycleBinInfo pSHQueryRBInfo);

    /// <summary>
    /// Path to PIDL. The shell's own parser, so a name containing a quote or a comma is
    /// data rather than syntax - unlike <c>explorer.exe /select,"path"</c>
    /// (README section 15.2).
    /// </summary>
    [LibraryImport(Dll, EntryPoint = "SHParseDisplayName", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int SHParseDisplayName(
        string pszName, nint pbc, out nint ppidl, uint sfgaoIn, out uint psfgaoOut);

    /// <summary>Opens the folder and selects the given children in one existing window.</summary>
    [LibraryImport(Dll, EntryPoint = "SHOpenFolderAndSelectItems")]
    internal static unsafe partial int SHOpenFolderAndSelectItems(
        nint pidlFolder, uint cidl, nint* apidl, uint dwFlags);

    [LibraryImport(Dll, EntryPoint = "ILFree")]
    internal static partial void ILFree(nint pidl);
}

internal static partial class Ole32
{
    private const string Dll = "ole32.dll";

    internal const uint ApartmentThreaded = 0x2;
    internal const uint DisableOle1Dde = 0x4;

    [LibraryImport(Dll, EntryPoint = "CoInitializeEx")]
    internal static partial int CoInitializeEx(nint pvReserved, uint dwCoInit);

    [LibraryImport(Dll, EntryPoint = "CoUninitialize")]
    internal static partial void CoUninitialize();
}
