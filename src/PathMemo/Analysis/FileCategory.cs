namespace PathMemo.Analysis;

/// <summary>
/// The eight buckets stored per scan in <c>scan_category_totals</c> (README section 11).
/// </summary>
/// <remarks>
/// These drive the "what is this disk made of" chart, which has to keep working years
/// after the snapshot itself was dropped by retention. They are a coarse summary, not a
/// deletion decision: <c>reclaim</c> (P7) classifies by rule with a path and a remedy,
/// never by extension.
/// </remarks>
internal enum FileCategory
{
    Other,
    Media,
    Archive,
    Cache,
    Source,
    Document,
    App,
    System,
}

internal static class FileCategories
{
    internal static string Name(FileCategory category) => category switch
    {
        FileCategory.Media => "media",
        FileCategory.Archive => "archive",
        FileCategory.Cache => "cache",
        FileCategory.Source => "source",
        FileCategory.Document => "document",
        FileCategory.App => "app",
        FileCategory.System => "system",
        _ => "other",
    };

    /// <summary>
    /// A directory whose whole subtree belongs to one bucket regardless of what the
    /// files inside are named. Windows is full of media files that are not the user's
    /// media, and a cache full of .zip is still a cache.
    /// </summary>
    internal static FileCategory? Inherited(ReadOnlySpan<char> directoryName, int depth)
    {
        // Checked first because it is one hash rather than a walk down the list, and
        // because most directories on a real disk are not special at all.
        if (CacheLookup.Contains(directoryName)) return FileCategory.Cache;

        // Depth 1 means directly under a volume root: "C:\Windows" is the system, while
        // "C:\projects\app\Windows" is a source directory that happens to be named so.
        if (depth == 1)
        {
            if (Same(directoryName, "Windows")) return FileCategory.System;
            if (Same(directoryName, "Program Files")) return FileCategory.App;
            if (Same(directoryName, "Program Files (x86)")) return FileCategory.App;
            if (Same(directoryName, "$Recycle.Bin")) return FileCategory.System;
            if (Same(directoryName, "System Volume Information")) return FileCategory.System;
            if (Same(directoryName, "$Extend")) return FileCategory.System;
            if (Same(directoryName, "Recovery")) return FileCategory.System;
        }

        return null;
    }

    private static readonly HashSet<string> CacheDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "__pycache__", ".cache", "cache", "caches", "cache2",
        ".gradle", ".nuget", ".m2", ".cargo", ".pub-cache", ".ivy2",
        "temp", "tmp", "TempState", "INetCache", "WebCache", "CrashDumps",
        "packagecache", "ShaderCache", "GPUCache", "Code Cache", "CacheStorage",
    };

    private static readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> CacheLookup =
        CacheDirectories.GetAlternateLookup<ReadOnlySpan<char>>();

    /// <summary>
    /// Bucket for a file name, by extension. Used when no directory claimed it.
    /// </summary>
    /// <remarks>
    /// One hash lookup, not a walk over two hundred candidates: this runs once per file,
    /// and the list-scan version cost half a second on a 1.2 million file drive. The
    /// alternate lookup takes the extension as a span, so no string is allocated either
    /// (README section 17.3).
    /// </remarks>
    internal static FileCategory ByExtension(ReadOnlySpan<char> extension) =>
        !extension.IsEmpty && ExtensionLookup.TryGetValue(extension, out var category)
            ? category
            : FileCategory.Other;

    // The table is the source of truth; the map below is built from it at first use.
    // Ordered coarsest-value-first only for readability.
    private static readonly (FileCategory Category, string[] Extensions)[] Extensions =
    [
        (FileCategory.Media,
        [
            "mp4", "mkv", "avi", "mov", "wmv", "flv", "webm", "m4v", "mpg", "mpeg", "mts", "ts", "vob", "3gp",
            "mp3", "flac", "wav", "aac", "ogg", "opus", "m4a", "wma", "aiff", "mid",
            "jpg", "jpeg", "png", "gif", "bmp", "tif", "tiff", "webp", "heic", "avif",
            "raw", "cr2", "cr3", "nef", "arw", "dng", "psd", "ai", "xcf",
        ]),
        (FileCategory.Archive,
        [
            "zip", "7z", "rar", "gz", "tgz", "bz2", "xz", "zst", "tar", "lz4", "cab", "arj", "lzh",
            "iso", "img", "wim", "esd", "swm", "vhd", "vhdx", "vmdk", "vdi", "qcow2", "ova",
            "nupkg", "whl", "crate", "deb", "rpm", "apk", "aab",
        ]),
        (FileCategory.Source,
        [
            "c", "h", "cc", "cpp", "cxx", "hpp", "hxx", "cs", "fs", "vb", "java", "kt", "kts", "scala",
            "py", "pyi", "js", "mjs", "cjs", "jsx", "ts", "tsx", "go", "rs", "rb", "php", "pl", "lua",
            "swift", "m", "mm", "dart", "ex", "exs", "erl", "hs", "clj", "r", "jl",
            "sh", "bash", "ps1", "psm1", "bat", "cmd", "sql", "css", "scss", "less", "html", "htm",
            "vue", "svelte", "razor", "cshtml", "proto", "gradle", "cmake", "make", "mk",
            "yml", "yaml", "toml", "sln", "csproj", "vcxproj", "patch", "diff",
        ]),
        (FileCategory.Document,
        [
            "pdf", "doc", "docx", "xls", "xlsx", "xlsm", "ppt", "pptx", "odt", "ods", "odp",
            "txt", "rtf", "md", "rst", "tex", "csv", "tsv", "epub", "mobi", "azw3", "djvu",
            "chm", "one", "onepkg", "vsd", "vsdx", "svg", "eml", "msg", "pst", "ost",
        ]),
        (FileCategory.App,
        [
            "exe", "dll", "msi", "msix", "appx", "msixbundle", "appxbundle", "com", "ocx", "cpl",
            "pyd", "node", "jar", "war", "so", "dylib", "lib", "a", "obj", "o", "winmd", "nls",
        ]),
        (FileCategory.System,
        [
            "sys", "dmp", "hdmp", "mdmp", "etl", "evtx", "mui", "cat", "inf", "pnf",
            "hiberfil", "pagefile", "swapfile", "efi", "wdi", "blf", "regtrans-ms", "bcd",
        ]),
        (FileCategory.Cache,
        [
            "pdb", "ilk", "ncb", "ipch", "pch", "tlog", "idb", "bak", "old", "log", "tmp", "temp",
            "crdownload", "part", "partial", "download",
        ]),
    ];

    private static readonly Dictionary<string, FileCategory> ExtensionMap = BuildExtensionMap();

    private static readonly Dictionary<string, FileCategory>.AlternateLookup<ReadOnlySpan<char>> ExtensionLookup =
        ExtensionMap.GetAlternateLookup<ReadOnlySpan<char>>();

    private static Dictionary<string, FileCategory> BuildExtensionMap()
    {
        var map = new Dictionary<string, FileCategory>(256, StringComparer.OrdinalIgnoreCase);

        // First entry wins, so an extension listed twice keeps the earlier category.
        foreach (var (category, list) in Extensions)
            foreach (var extension in list)
                map.TryAdd(extension, category);

        return map;
    }

    private static bool Same(ReadOnlySpan<char> a, string b) =>
        a.Equals(b, StringComparison.OrdinalIgnoreCase);
}
