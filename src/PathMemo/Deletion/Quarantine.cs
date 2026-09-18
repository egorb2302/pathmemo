using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using PathMemo.Config;
using PathMemo.Platform.Native;

namespace PathMemo.Deletion;

/// <summary>One entry of a quarantine manifest: where it came from and what it is called now.</summary>
internal sealed record QuarantineEntry
{
    internal required int Seq { get; init; }
    internal required string OriginalPath { get; init; }
    internal required string StoreRoot { get; init; }
    internal required string StoredName { get; init; }
    internal required long Bytes { get; init; }
    internal bool IsDirectory { get; init; }
    internal DateTime LastWriteUtc { get; init; }

    internal string StoredPath => Path.Combine(StoreRoot, Quarantine.DataFolder, StoredName);
}

/// <summary>The manifest file, which is the authority on what a quarantine holds.</summary>
internal sealed record QuarantineManifest
{
    internal required long OpId { get; init; }
    internal required DateTime CreatedUtc { get; init; }
    internal DateTime? PurgeAfterUtc { get; init; }
    internal string ToolVersion { get; init; } = AppInfo.Version;
    internal string? Reason { get; init; }
    internal required IReadOnlyList<QuarantineEntry> Items { get; init; }

    internal long Bytes => Items.Sum(i => i.Bytes);
}

/// <summary>
/// Moving things aside instead of deleting them (README section 9.4).
/// </summary>
/// <remarks>
/// <para>
/// A quarantine lives <b>on the volume the file came from</b>, because the move has to be
/// a rename: within a volume that is a metadata operation, instant whatever the size, and
/// atomic. Copying 18 GB to another disk to call it a "safe delete" is not a trade anyone
/// asked for, so a cross-volume quarantine is refused rather than emulated.
/// </para>
/// <para>
/// The manifest on disk is the fact and the database row is an index, exactly as with
/// snapshots (README section 11.1): a restore works from a store whose database was
/// deleted, and the journal can be rebuilt from the manifests.
/// </para>
/// </remarks>
internal static class Quarantine
{
    /// <summary>Directory created at the root of every other volume the operation touches.</summary>
    internal const string VolumeStoreName = "pathmemo-quarantine";

    internal const string DataFolder = "data";
    internal const string ManifestName = "manifest.json";

    private const int ManifestSchema = 1;

    internal static string FolderName(long opId) =>
        "op-" + opId.ToString("D6", CultureInfo.InvariantCulture);

    /// <summary>
    /// Where a given volume's items are kept. The data directory when they are on the
    /// same volume as the store, a hidden directory at the volume root otherwise.
    /// </summary>
    internal static string StoreRootFor(string volumeRoot, long opId)
    {
        var main = AppPaths.QuarantineDirectory;

        return OnSameVolume(main, volumeRoot)
            ? Path.Combine(main, FolderName(opId))
            : Path.Combine(volumeRoot, VolumeStoreName, FolderName(opId));
    }

    internal static bool OnSameVolume(string a, string b)
    {
        var rootA = PathGuard.VolumeRootOf(Path.GetFullPath(a)) ?? "";
        var rootB = PathGuard.VolumeRootOf(Path.GetFullPath(b)) ?? "";

        return rootA.Length > 0 && rootA.Equals(rootB, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Creates <c>&lt;store&gt;\data</c> and returns a handle to it, ready to receive renames.</summary>
    internal static SafeFileHandle? OpenData(string storeRoot, out string? error)
    {
        try
        {
            var data = Path.Combine(storeRoot, DataFolder);
            Directory.CreateDirectory(data);

            // A store at a volume root is ours, not the user's: hidden and system keeps it
            // out of the way and out of Explorer's search results.
            var top = Path.GetDirectoryName(Path.GetFullPath(storeRoot));
            if (top is not null && Path.GetFileName(top).Equals(VolumeStoreName, StringComparison.OrdinalIgnoreCase))
                FileApi.SetFileAttributes(top, FileApi.FileAttributeHidden | FileApi.FileAttributeSystem);

            var handle = FileApi.TryOpen(data,
                FileApi.FileListDirectory | FileApi.FileTraverse | FileApi.FileAddFile |
                FileApi.FileAddSubdirectory | FileApi.FileReadAttributes | FileApi.Synchronize,
                out var code);

            if (handle is null)
            {
                error = $"the quarantine at {data} could not be opened ({code})";
                return null;
            }

            error = null;
            return handle;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"the quarantine could not be created: {ex.Message}";
            return null;
        }
    }

    internal static string StoredName(int seq) => seq.ToString("D6", CultureInfo.InvariantCulture);

    /// <summary>
    /// Moves one approved target into the open quarantine directory.
    /// </summary>
    /// <remarks>
    /// Both ends are handles (README section 9.5): the source is the object the guard
    /// checked, the destination the directory we just created. A rename cannot cross
    /// volumes, so <c>ERROR_NOT_SAME_DEVICE</c> here means the store ended up on the wrong
    /// disk - reported as a refusal rather than turned into an 18 GB copy.
    /// </remarks>
    internal static string? Move(GuardedTarget target, SafeFileHandle data, string storedName)
    {
        if (FileApi.TryRename(target.Handle, data, storedName, out var error)) return null;

        return error switch
        {
            FileApi.ErrorNotSameDevice =>
                "the quarantine is on another volume, and a quarantine must be a rename, not a copy "
                + "(use --mode permanent, or move the data directory onto this volume)",
            FileApi.ErrorAccessDenied => "access denied while moving it aside",
            FileApi.ErrorSharingViolation => "held open by another process without sharing delete",
            _ => new System.ComponentModel.Win32Exception(error).Message.TrimEnd('.'),
        };
    }

    internal static void WriteManifest(QuarantineManifest manifest)
    {
        var directory = Path.Combine(AppPaths.QuarantineDirectory, FolderName(manifest.OpId));
        Directory.CreateDirectory(directory);

        using var stream = new MemoryStream(1024);
        using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            json.WriteNumber("schemaVersion", ManifestSchema);
            json.WriteNumber("opId", manifest.OpId);
            json.WriteString("createdUtc", Iso(manifest.CreatedUtc));
            if (manifest.PurgeAfterUtc is { } purge) json.WriteString("purgeAfterUtc", Iso(purge));
            json.WriteString("toolVersion", manifest.ToolVersion);
            if (manifest.Reason is { } reason) json.WriteString("reason", reason);

            json.WriteStartArray("items");
            foreach (var item in manifest.Items)
            {
                json.WriteStartObject();
                json.WriteNumber("seq", item.Seq);
                json.WriteString("originalPath", item.OriginalPath);
                json.WriteString("store", item.StoreRoot);
                json.WriteString("storedName", item.StoredName);
                json.WriteNumber("bytes", item.Bytes);
                json.WriteBoolean("isDirectory", item.IsDirectory);
                json.WriteString("lastWriteUtc", Iso(item.LastWriteUtc));
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteEndObject();
        }

        File.WriteAllBytes(Path.Combine(directory, ManifestName), stream.ToArray());
    }

    internal static QuarantineManifest? ReadManifest(long opId, out string? error)
    {
        var path = Path.Combine(AppPaths.QuarantineDirectory, FolderName(opId), ManifestName);

        if (!File.Exists(path))
        {
            error = $"no quarantine manifest for op-{opId:D6} (already purged, or from another data directory)";
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = document.RootElement;

            var items = new List<QuarantineEntry>();
            if (root.TryGetProperty("items", out var array))
            {
                foreach (var element in array.EnumerateArray())
                {
                    items.Add(new QuarantineEntry
                    {
                        Seq = element.GetProperty("seq").GetInt32(),
                        OriginalPath = element.GetProperty("originalPath").GetString()!,
                        StoreRoot = element.GetProperty("store").GetString()!,
                        StoredName = element.GetProperty("storedName").GetString()!,
                        Bytes = element.GetProperty("bytes").GetInt64(),
                        IsDirectory = element.TryGetProperty("isDirectory", out var d) && d.GetBoolean(),
                        LastWriteUtc = Time(element, "lastWriteUtc"),
                    });
                }
            }

            error = null;
            return new QuarantineManifest
            {
                OpId = root.GetProperty("opId").GetInt64(),
                CreatedUtc = Time(root, "createdUtc"),
                PurgeAfterUtc = root.TryGetProperty("purgeAfterUtc", out var p)
                    ? DateTime.Parse(p.GetString()!, CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal)
                    : null,
                ToolVersion = root.TryGetProperty("toolVersion", out var v) ? v.GetString() ?? "" : "",
                Reason = root.TryGetProperty("reason", out var r) ? r.GetString() : null,
                Items = items,
            };
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or FormatException
                                      or InvalidOperationException or IOException)
        {
            error = $"the quarantine manifest for op-{opId:D6} is unreadable: {ex.Message}";
            return null;
        }
    }

    /// <summary>Operations with a manifest on disk, newest first.</summary>
    internal static IReadOnlyList<QuarantineManifest> List()
    {
        var root = AppPaths.QuarantineDirectory;
        if (!Directory.Exists(root)) return [];

        var found = new List<QuarantineManifest>();

        foreach (var directory in Directory.EnumerateDirectories(root, "op-*"))
        {
            var name = Path.GetFileName(directory);
            if (!long.TryParse(name.AsSpan(3), NumberStyles.None, CultureInfo.InvariantCulture, out var id)) continue;

            var manifest = ReadManifest(id, out _);
            if (manifest is not null) found.Add(manifest);
        }

        return [.. found.OrderByDescending(m => m.OpId)];
    }

    /// <summary>
    /// Puts one entry back where it came from.
    /// </summary>
    /// <returns>Null on success, or why it could not be restored.</returns>
    internal static string? RestoreEntry(QuarantineEntry entry)
    {
        var parent = Path.GetDirectoryName(entry.OriginalPath);
        if (parent is null) return "its original path has no parent directory";

        if (File.Exists(entry.OriginalPath) || Directory.Exists(entry.OriginalPath))
            return "something already exists at its original path";

        var data = Path.Combine(entry.StoreRoot, DataFolder);
        using var dataHandle = FileApi.TryOpen(data,
            FileApi.FileListDirectory | FileApi.FileTraverse | FileApi.FileReadAttributes | FileApi.Synchronize,
            out var dataError);

        if (dataHandle is null)
            return $"the quarantine directory is gone ({new System.ComponentModel.Win32Exception(dataError).Message.TrimEnd('.')})";

        using var stored = NtDll.OpenRelative(dataHandle, entry.StoredName,
            FileApi.Delete | FileApi.FileReadAttributes | FileApi.Synchronize,
            NtDll.FileSynchronousIoNonAlert | NtDll.FileOpenForBackupIntent, out var openError);

        if (stored is null)
            return $"it is not in the quarantine any more ({new System.ComponentModel.Win32Exception(openError).Message.TrimEnd('.')})";

        try
        {
            Directory.CreateDirectory(parent);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"its original directory could not be recreated: {ex.Message}";
        }

        using var parentHandle = FileApi.TryOpen(parent,
            FileApi.FileListDirectory | FileApi.FileTraverse | FileApi.FileAddFile |
            FileApi.FileAddSubdirectory | FileApi.FileReadAttributes | FileApi.Synchronize,
            out var parentError);

        if (parentHandle is null)
            return $"its original directory could not be opened ({new System.ComponentModel.Win32Exception(parentError).Message.TrimEnd('.')})";

        var name = Path.GetFileName(entry.OriginalPath);
        if (FileApi.TryRename(stored, parentHandle, name, out var renameError)) return null;

        return renameError switch
        {
            FileApi.ErrorAlreadyExists => "something already exists at its original path",
            FileApi.ErrorNotSameDevice => "its volume is not the one the quarantine is on",
            _ => new System.ComponentModel.Win32Exception(renameError).Message.TrimEnd('.'),
        };
    }

    /// <summary>Every store directory an operation used, including the one holding the manifest.</summary>
    internal static IReadOnlyList<string> StoresOf(QuarantineManifest manifest)
    {
        var stores = new List<string>
        {
            Path.Combine(AppPaths.QuarantineDirectory, FolderName(manifest.OpId)),
        };

        foreach (var item in manifest.Items)
            if (!stores.Any(s => s.Equals(item.StoreRoot, StringComparison.OrdinalIgnoreCase)))
                stores.Add(item.StoreRoot);

        return stores;
    }

    private static string Iso(DateTime value) =>
        value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    private static DateTime Time(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.GetString() is { } text
        && DateTime.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : default;

    internal static string Describe(QuarantineManifest manifest) =>
        new StringBuilder()
            .Append(FolderName(manifest.OpId)).Append("  ")
            .Append(manifest.Items.Count).Append(" items  ")
            .Append(Cli.Output.SizeFormat.Bytes(manifest.Bytes))
            .ToString();
}
