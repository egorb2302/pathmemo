using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Microsoft.Win32;
using PathMemo.Cli.Output;
using PathMemo.Platform.Native;

namespace PathMemo.Deletion;

/// <summary>What the bin on one volume currently holds, and what it is allowed to hold.</summary>
internal sealed record BinState(bool Exists, long UsedBytes, long ItemCount, long? MaxBytes, bool NukeOnDelete)
{
    internal long? FreeCapacity => MaxBytes is { } max ? Math.Max(0, max - UsedBytes) : null;
}

/// <summary>
/// The Recycle Bin, entered only through <c>IFileOperation</c> (README section 9.6).
/// </summary>
/// <remarks>
/// <para>
/// The bin is the least useful of the three modes and the most surprising: it frees
/// nothing, because <c>$Recycle.Bin</c> is on the same volume (README section 9.1), and
/// anything over its quota is <b>destroyed outright</b> by the shell with only a dialog in
/// the way. So the quota is checked here, ahead of time, and an operation that would be
/// silently nuked never gets offered this mode.
/// </para>
/// <para>
/// COM needs an apartment, so the whole operation runs on its own STA thread.
/// </para>
/// </remarks>
internal static partial class RecycleBin
{
    private const string BitBucketKey =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\BitBucket\Volume";

    internal static BinState Query(string volumeRoot)
    {
        var info = new Shell32.QueryRecycleBinInfo { Size = Shell32.QueryRecycleBinInfo.NativeSize };
        var hr = Shell32.SHQueryRecycleBin(volumeRoot, ref info);

        var (max, nuke) = Policy(volumeRoot);

        // S_OK with counts, or a failure meaning "no bin here" - network and some
        // removable volumes have none at all (README section 9.1).
        return hr == 0
            ? new BinState(true, info.Bytes, info.Items, max, nuke)
            : new BinState(false, 0, 0, max, nuke);
    }

    /// <summary>
    /// Why this plan must not go to the bin, or null when it may.
    /// </summary>
    internal static string? WhyUnsuitable(IReadOnlyList<PlanItem> items, int maxItems, long maxBytes)
    {
        if (items.Count == 0) return null;

        var files = items.Sum(i => i.FileCount);
        if (files > maxItems)
            return $"{files} files is more than the bin is worth using for "
                 + $"(delete.recycleMaxItems = {maxItems.ToString(CultureInfo.InvariantCulture)})";

        var bytes = items.Sum(i => i.Bytes);
        if (bytes > maxBytes)
            return $"{SizeFormat.Bytes(bytes)} is over delete.recycleMaxBytes ({SizeFormat.Bytes(maxBytes)})";

        foreach (var volume in items.Select(i => i.VolumeRoot).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var state = Query(volume);
            var volumeBytes = items.Where(i => i.VolumeRoot.Equals(volume, StringComparison.OrdinalIgnoreCase))
                                   .Sum(i => i.Bytes);

            if (!state.Exists) return $"{volume} has no Recycle Bin";
            if (state.NukeOnDelete) return $"{volume} is configured to delete immediately instead of recycling";

            // The shell's answer to "too big for the bin" is to destroy it and say so in a
            // dialog nobody reads. We refuse the mode instead.
            if (state.FreeCapacity is { } room && volumeBytes > room)
                return $"{SizeFormat.Bytes(volumeBytes)} does not fit in the bin on {volume} "
                     + $"({SizeFormat.Bytes(room)} left of {SizeFormat.Bytes(state.MaxBytes ?? 0)}); "
                     + "the shell would delete it outright";
        }

        return null;
    }

    /// <summary>
    /// Per-volume quota and the "do not recycle at all" setting, from the registry.
    /// </summary>
    /// <remarks>
    /// <c>MaxCapacity</c> is in megabytes and only present once the user has changed it or
    /// the shell has written a default. With no value, Windows sizes the bin at about 5%
    /// of the volume, which is what is assumed here - an estimate, and treated as one.
    /// </remarks>
    private static (long? MaxBytes, bool Nuke) Policy(string volumeRoot)
    {
        try
        {
            var guid = VolumeGuid(volumeRoot);
            if (guid is null) return (Estimate(volumeRoot), false);

            using var key = Registry.CurrentUser.OpenSubKey($@"{BitBucketKey}\{guid}");
            if (key is null) return (Estimate(volumeRoot), false);

            var nuke = key.GetValue("NukeOnDelete") is int n && n != 0;
            var max = key.GetValue("MaxCapacity") is int megabytes && megabytes > 0
                ? megabytes * (1L << 20)
                : Estimate(volumeRoot);

            return (max, nuke);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return (Estimate(volumeRoot), false);
        }
    }

    private static long? Estimate(string volumeRoot) =>
        Kernel32.GetDiskFreeSpaceEx(volumeRoot, out _, out var total, out _)
            ? (long)(total / 20)
            : null;

    private static string? VolumeGuid(string volumeRoot)
    {
        Span<char> buffer = stackalloc char[64];
        if (!Kernel32.GetVolumeNameForVolumeMountPoint(volumeRoot, buffer, buffer.Length)) return null;

        var text = new string(buffer[..buffer.IndexOf('\0')]);   // \\?\Volume{guid}\
        var open = text.IndexOf('{');
        var close = text.IndexOf('}');

        return open < 0 || close < open ? null : text[open..(close + 1)];
    }

    /// <summary>
    /// Sends the items to the bin and reports what the shell did with each one.
    /// </summary>
    /// <remarks>
    /// The one place in the deletion module that works from paths rather than handles: the
    /// shell parses a path and there is no handle-taking entry point. That is a real
    /// difference in guarantee, and part of why quarantine - not the bin - is the default
    /// mode (README section 9.2).
    /// </remarks>
    internal static IReadOnlyList<ItemOutcome> Send(IReadOnlyList<PlanItem> items, CancellationToken ct)
    {
        var outcomes = new ItemOutcome[items.Count];
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                Run(items, outcomes, ct);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            for (var i = 0; i < outcomes.Length; i++)
                outcomes[i] ??= new ItemOutcome(items[i], ItemResult.Failed, Message: failure.Message);
        }

        return outcomes;
    }

    private static void Run(IReadOnlyList<PlanItem> items, ItemOutcome[] outcomes, CancellationToken ct)
    {
        var hr = Ole32.CoInitializeEx(0, Ole32.ApartmentThreaded | Ole32.DisableOle1Dde);
        if (hr < 0) throw new InvalidOperationException($"COM could not be initialised (0x{hr:X8})");

        try
        {
            var wrappers = new StrategyBasedComWrappers();

            hr = ShellOperations.CoCreateInstance(ShellOperations.FileOperationClsid, 0,
                ShellOperations.ClsctxInprocServer, ShellOperations.FileOperationIid, out var raw);

            if (hr < 0) throw new InvalidOperationException($"the shell's file operation is unavailable (0x{hr:X8})");

            var operation = (IFileOperation)wrappers.GetOrCreateObjectForComInstance(raw, CreateObjectFlags.UniqueInstance);
            Marshal.Release(raw);

            // No confirmation dialogs, no error UI, no progress window, and recycle rather
            // than delete. FOF_WANTNUKEWARNING stays on as a last line of defence; the size
            // check in WhyUnsuitable is what actually keeps us away from that path.
            operation.SetOperationFlags(
                ShellOperations.FofNoConfirmation | ShellOperations.FofNoErrorUi |
                ShellOperations.FofSilent | ShellOperations.FofxRecycleOnDelete |
                ShellOperations.FofWantNukeWarning);

            operation.SetOwnerWindow(0);

            var sinks = new ItemSink[items.Count];

            for (var i = 0; i < items.Count; i++)
            {
                ct.ThrowIfCancellationRequested();

                hr = ShellOperations.SHCreateItemFromParsingName(
                    items[i].DisplayPath, 0, ShellOperations.ShellItemIid, out var itemPtr);

                if (hr < 0)
                {
                    outcomes[i] = new ItemOutcome(items[i], ItemResult.Failed, hr,
                        "the shell could not resolve this path");
                    continue;
                }

                var shellItem = (IShellItem)wrappers.GetOrCreateObjectForComInstance(itemPtr, CreateObjectFlags.UniqueInstance);
                Marshal.Release(itemPtr);

                // A sink per item, so the result comes back attached to the item rather
                // than to a display name we would have to match up afterwards.
                sinks[i] = new ItemSink();
                operation.DeleteItem(shellItem, sinks[i]);
            }

            operation.PerformOperations();
            operation.GetAnyOperationsAborted(out var aborted);

            for (var i = 0; i < items.Count; i++)
            {
                if (outcomes[i] is not null) continue;

                var sink = sinks[i];

                // Success is any HRESULT with the severity bit clear, not S_OK alone: the
                // copy engine answers with its own successes - COPYENGINE_S_DONT_PROCESS_
                // CHILDREN (0x00270008) for a deleted directory, for one - and reading
                // those as failures would report every such item as a failure that worked.
                outcomes[i] = sink switch
                {
                    { Reported: true } when sink.Result >= 0 => new ItemOutcome(items[i], ItemResult.Ok),
                    { Reported: true } => new ItemOutcome(items[i], ItemResult.Failed, sink.Result,
                        Describe(sink.Result)),
                    _ when aborted != 0 => new ItemOutcome(items[i], ItemResult.Skipped, Message: "cancelled"),
                    _ => new ItemOutcome(items[i], ItemResult.Failed, Message: "the shell reported nothing for it"),
                };
            }
        }
        finally
        {
            Ole32.CoUninitialize();
        }
    }

    private static string Describe(int hresult) => hresult switch
    {
        unchecked((int)0x80270000) => "the shell refused it",
        unchecked((int)0x80070005) => "access denied",
        unchecked((int)0x80070020) => "in use by another process",
        _ => $"HRESULT 0x{hresult:X8}",
    };

    /// <summary>
    /// Empties the bin on one volume. The only operation allowed inside
    /// <c>$Recycle.Bin</c>, and the one the audit's remedy runs.
    /// </summary>
    internal static string? Empty(string volumeRoot)
    {
        const uint noConfirmation = 0x1, noProgressUi = 0x2, noSound = 0x4;

        var hr = Shell32.SHEmptyRecycleBin(0, volumeRoot, noConfirmation | noProgressUi | noSound);

        // S_FALSE (1) means there was nothing in it, which is not a failure.
        return hr is 0 or 1 ? null : $"the shell refused to empty the bin (0x{hr:X8})";
    }

    /// <summary>
    /// Records what <c>IFileOperation</c> reports for one item. Only the delete callbacks
    /// carry information here; the rest exist because the interface demands them.
    /// </summary>
    [GeneratedComClass]
    private sealed partial class ItemSink : IFileOperationProgressSink
    {
        internal bool Reported { get; private set; }
        internal int Result { get; private set; }

        public void PostDeleteItem(uint dwFlags, IShellItem psiItem, int hrDelete, IShellItem? psiNewlyCreated)
        {
            Reported = true;
            Result = hrDelete;
        }

        public void StartOperations() { }
        public void FinishOperations(int hrResult) { }
        public void PreRenameItem(uint dwFlags, IShellItem psiItem, string? pszNewName) { }
        public void PostRenameItem(uint dwFlags, IShellItem psiItem, string? pszNewName, int hrRename, IShellItem? psiNewlyCreated) { }
        public void PreMoveItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, string? pszNewName) { }
        public void PostMoveItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, string? pszNewName, int hrMove, IShellItem? psiNewlyCreated) { }
        public void PreCopyItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, string? pszNewName) { }
        public void PostCopyItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, string? pszNewName, int hrCopy, IShellItem? psiNewlyCreated) { }
        public void PreDeleteItem(uint dwFlags, IShellItem psiItem) { }
        public void PreNewItem(uint dwFlags, IShellItem psiDestinationFolder, string? pszNewName) { }
        public void PostNewItem(uint dwFlags, IShellItem psiDestinationFolder, string? pszNewName, string? pszTemplateName,
            uint dwFileAttributes, int hrNew, IShellItem? psiNewItem) { }
        public void UpdateProgress(uint iWorkTotal, uint iWorkSoFar) { }
        public void ResetTimer() { }
        public void PauseTimer() { }
        public void ResumeTimer() { }
    }
}
