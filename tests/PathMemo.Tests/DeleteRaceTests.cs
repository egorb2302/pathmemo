using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using PathMemo.Deletion;
using PathMemo.Platform.Native;
using Xunit;

namespace PathMemo.Tests;

/// <summary>
/// The race test for threat T2 (README sections 9.5, 22.3).
/// </summary>
/// <remarks>
/// <para>
/// One thread deletes a tree recursively while another repeatedly swaps a directory inside
/// it for a junction pointing at a canary outside it. The canary must survive every round.
/// Without this test <c>HandleTreeDeleter</c> cannot be called done: the whole design -
/// opening children relative to an open parent handle, never following a reparse point,
/// and judging a child by the attributes of the handle rather than by what the listing said
/// a moment earlier - exists for this one scenario.
/// </para>
/// <para>
/// The iteration count is a compromise. Each round creates and destroys a small tree, so
/// 10,000 of them is minutes, not seconds; the suite runs a lower count with a wall-clock
/// budget, and the full run is done by hand before a release (README section 22.3).
/// </para>
/// </remarks>
public sealed class DeleteRaceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "pathmemo-race-" + Guid.NewGuid().ToString("N")[..12]);

    private readonly string _canary;
    private readonly string _canaryFile;

    public DeleteRaceTests()
    {
        _canary = Path.Combine(_root, "canary");
        _canaryFile = Path.Combine(_canary, "must-survive.bin");

        Directory.CreateDirectory(_canary);
        File.WriteAllBytes(_canaryFile, new byte[4096]);
    }

    public void Dispose()
    {
        foreach (var directory in Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories).Reverse())
        {
            try
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                    Directory.Delete(directory, recursive: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException) { }
        }

        try { Directory.Delete(_root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException) { }
    }

    [Fact]
    public async Task A_junction_swapped_in_mid_delete_does_not_redirect_the_deleter()
    {
        var asked = Environment.GetEnvironmentVariable("PATHMEMO_RACE_ITERATIONS") is { } text
            && int.TryParse(text, out var requested) ? requested : 0;

        var iterations = asked > 0 ? asked : 600;

        // The suite keeps its 20-second budget; an explicit count is the release-time run
        // and is allowed to take as long as it takes (README section 22.3).
        var budget = asked > 0 ? TimeSpan.FromHours(1) : TimeSpan.FromSeconds(20);
        var clock = Stopwatch.StartNew();
        var guard = new PathGuard(ProtectedSet.Build());
        var swaps = 0;
        var completed = 0;

        for (var round = 0; round < iterations && clock.Elapsed < budget; round++)
        {
            var doomed = Path.Combine(_root, "round" + round);
            var bait = Path.Combine(doomed, "swapme");

            Directory.CreateDirectory(bait);
            File.WriteAllBytes(Path.Combine(bait, "inside.bin"), new byte[64]);

            for (var i = 0; i < 6; i++)
            {
                var branch = Path.Combine(doomed, "d" + i);
                Directory.CreateDirectory(branch);
                File.WriteAllBytes(Path.Combine(branch, "f.bin"), new byte[64]);
            }

            using var stop = new CancellationTokenSource();

            var attacker = Task.Run(() =>
            {
                while (!stop.IsCancellationRequested)
                {
                    try
                    {
                        if (Directory.Exists(bait) &&
                            (File.GetAttributes(bait) & FileAttributes.ReparsePoint) == 0)
                            Directory.Delete(bait, recursive: true);

                        if (!Directory.Exists(bait) && TryCreateJunction(bait, _canary)) swaps++;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
            });

            using (var target = guard.Open(doomed, PathGuard.DeleteAccess).Target)
            {
                if (target is not null)
                {
                    HandleTreeDeleter.Delete(target);
                    completed++;
                }
            }

            stop.Cancel();
            await attacker.WaitAsync(TimeSpan.FromSeconds(5));

            // The assertion, every round: whatever the two threads did to each other, the
            // directory the junction pointed at is untouched.
            Assert.True(File.Exists(_canaryFile),
                $"the canary was deleted through a junction on round {round} (after {swaps} swaps)");

            Assert.Equal(4096, new FileInfo(_canaryFile).Length);

            Cleanup(doomed);
        }

        // A run where the attacker never managed a single swap would pass without testing
        // anything, so the test says so rather than reporting a hollow green.
        Assert.True(swaps > 0, "the attacker never managed to swap in a junction; the race was not exercised");
        Assert.True(completed > 0, "no deletion ran");

        // Printed rather than asserted: how far the run got is what makes the result
        // meaningful, and the number depends on the machine.
        Console.WriteLine($"race: {completed} deletions, {swaps} junction swaps, {clock.Elapsed.TotalSeconds:F1} s");
    }

    /// <summary>
    /// Removes what is left of a round, unlinking reparse points first - the BCL's
    /// recursive delete is exactly the thing this test is about, and it would walk in.
    /// </summary>
    private static void Cleanup(string directory)
    {
        if (!Directory.Exists(directory)) return;

        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            try
            {
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                {
                    Directory.Delete(child, recursive: false);
                    continue;
                }

                Cleanup(child);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException) { }
        }

        try { Directory.Delete(directory, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException) { }
    }

    /// <summary>
    /// A junction, created through the reparse-point IOCTL rather than <c>mklink</c>: this
    /// runs thousands of times and a process launch per attempt would make the race a
    /// contest between the deleter and the process loader.
    /// </summary>
    private static unsafe bool TryCreateJunction(string link, string target)
    {
        try
        {
            Directory.CreateDirectory(link);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        using var handle = FileApi.CreateFile(link,
            FileApi.Delete | FileApi.FileReadAttributes | FileApi.FileWriteAttributes | 0x0002 /* FILE_WRITE_DATA */,
            FileApi.ShareAll, 0, FileApi.OpenExisting,
            FileApi.FileFlagBackupSemantics | FileApi.FileFlagOpenReparsePoint, 0);

        if (handle.IsInvalid) return false;

        var substitute = @"\??\" + Path.GetFullPath(target);
        var print = Path.GetFullPath(target);

        var substituteBytes = substitute.Length * 2;
        var printBytes = print.Length * 2;

        // REPARSE_DATA_BUFFER for IO_REPARSE_TAG_MOUNT_POINT: an 8-byte header, four
        // USHORT offsets, then both names NUL-terminated.
        var pathBufferBytes = substituteBytes + 2 + printBytes + 2;
        var buffer = new byte[8 + 8 + pathBufferBytes];

        fixed (byte* p = buffer)
        fixed (char* sub = substitute)
        fixed (char* pr = print)
        {
            *(uint*)p = 0xA0000003;                      // ReparseTag
            *(ushort*)(p + 4) = (ushort)(8 + pathBufferBytes);   // ReparseDataLength
            *(ushort*)(p + 6) = 0;                       // Reserved
            *(ushort*)(p + 8) = 0;                       // SubstituteNameOffset
            *(ushort*)(p + 10) = (ushort)substituteBytes;
            *(ushort*)(p + 12) = (ushort)(substituteBytes + 2);  // PrintNameOffset
            *(ushort*)(p + 14) = (ushort)printBytes;

            Buffer.MemoryCopy(sub, p + 16, substituteBytes, substituteBytes);
            Buffer.MemoryCopy(pr, p + 16 + substituteBytes + 2, printBytes, printBytes);

            const uint fsctlSetReparsePoint = 0x000900A4;

            var added = false;
            handle.DangerousAddRef(ref added);
            try
            {
                return Kernel32Extra.DeviceIoControl(handle.DangerousGetHandle(), fsctlSetReparsePoint,
                    p, (uint)(8 + *(ushort*)(p + 4)), null, 0, out _, 0);
            }
            finally
            {
                if (added) handle.DangerousRelease();
            }
        }
    }
}
