using System.Diagnostics;
using PathMemo.Platform;
using PathMemo.Scanning;
using Xunit;

namespace PathMemo.Tests;

/// <summary>
/// What the walk scanner reports as a file's allocated size, checked against real files
/// on a real volume - because the number it used to report was the wrong one, and every
/// test over a synthetic tree agreed with it (README section 22.5).
/// </summary>
/// <remarks>
/// The walk asked <c>GetCompressedFileSize</c> for every file and took the answer as the
/// allocation. For an ordinary file that API returns the logical size: no cluster
/// rounding at all. The MFT scanner, reading the run lists, disagreed by exactly the
/// rounding, and the only test that could have said so needed administrator rights and
/// had never run. These run everywhere.
/// </remarks>
public sealed class WalkSizeTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "pathmemo-walksize-" + Guid.NewGuid().ToString("N")[..12]);

    public WalkSizeTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task An_ordinary_file_is_rounded_up_to_the_cluster()
    {
        int[] sizes = [1, 10, 4096, 4097, 300_000];
        foreach (var size in sizes)
            File.WriteAllBytes(Path.Combine(_root, $"f{size}.bin"), new byte[size]);

        var volume = VolumeFor(_root);
        var cluster = volume.ClusterBytes;
        Assert.True(cluster > 0, "the volume's cluster size is unknown, so nothing here can be checked");

        var tree = (await Scan(volume)).Tree;

        long expectedTotal = 0;
        foreach (var size in sizes)
        {
            var node = Analysis.TreeQuery.Find(tree, Path.Combine(_root, $"f{size}.bin"));
            Assert.NotEqual(Snapshots.NodeStore.NoNode, node);

            var expected = DirectoryLister.RoundUpToCluster(size, cluster);
            Assert.Equal(size, tree.Logical[node]);
            Assert.Equal(expected, tree.Allocated[node]);
            expectedTotal += expected;
        }

        Assert.Equal(expectedTotal, tree.Allocated[tree.Roots[0]]);
    }

    [Fact]
    public async Task A_compressed_file_is_asked_rather_than_rounded()
    {
        const int logical = 300_000;
        var path = Path.Combine(_root, "zeros.bin");
        File.WriteAllBytes(path, new byte[logical]);
        if (!TryCompress(path)) return;                           // not NTFS: nothing to test

        var volume = VolumeFor(_root);
        var tree = (await Scan(volume)).Tree;
        var node = Analysis.TreeQuery.Find(tree, path);
        Assert.NotEqual(Snapshots.NodeStore.NoNode, node);

        // Three hundred thousand zero bytes compress to next to nothing, so a rounded
        // logical size here would mean the compressed branch was never taken.
        Assert.Equal(logical, tree.Logical[node]);
        Assert.True(tree.Allocated[node] < logical,
            $"allocated {tree.Allocated[node]} for a compressed file of {logical} zero bytes");
        Assert.Equal(0, tree.Allocated[node] % volume.ClusterBytes);
    }

    private async Task<ScanResult> Scan(VolumeInfo volume) =>
        await new WalkScanner().ScanAsync(
            new ScanRequest { Roots = [volume.Root], Parallelism = 1 }, [volume], null, CancellationToken.None);

    private static VolumeInfo VolumeFor(string root)
    {
        var owner = VolumeInfo.Enumerate().First(v => root.StartsWith(v.Root, StringComparison.OrdinalIgnoreCase));
        return owner with { Root = root + Path.DirectorySeparatorChar };
    }

    private static bool TryCompress(string path)
    {
        var psi = new ProcessStartInfo("compact.exe")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add(path);

        try
        {
            using var p = Process.Start(psi)!;
            p.WaitForExit();
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            return false;
        }

        return (File.GetAttributes(path) & FileAttributes.Compressed) != 0;
    }
}
