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
}
