using System.Diagnostics;
using PathMemo.Platform;
using PathMemo.Scanning;
using PathMemo.Snapshots;
using Xunit;

namespace PathMemo.Tests;

/// <summary>
/// Scanner tests against a real directory tree in %TEMP%.
/// </summary>
/// <remarks>
/// There is deliberately no <c>IFileSystem</c> abstraction to fake. All the value of this
/// scanner is in Win32 edge cases - reparse points, hard links, sharing violations, long
/// paths, names a fake filesystem cannot even represent - and a fake would produce green
/// tests with a red production build (README section 22.2).
/// </remarks>
public sealed class ScanIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "pathmemo-test-" + Guid.NewGuid().ToString("N")[..12]);

    public ScanIntegrationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        // Reparse points must be unlinked before the recursive delete: the BCL's recursive
        // delete walks into a junction, which either fails or - with a junction pointing
        // outside the tree - would delete somewhere else entirely. This is the same hazard
        // that HandleTreeDeleter exists to close (README section 9.5, threat T2).
        RemoveReparsePoints(_root);

        try { Directory.Delete(_root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort; %TEMP% cleanup will get it.
        }
    }

    private static void RemoveReparsePoints(string directory)
    {
        if (!Directory.Exists(directory)) return;

        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            var attributes = System.IO.File.GetAttributes(child);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                // Deletes the link, never its target.
                Directory.Delete(child, recursive: false);
                continue;
            }

            RemoveReparsePoints(child);
        }
    }

    private string Dir(params string[] parts)
    {
        var path = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(path);
        return path;
    }

    private string File_(int bytes, params string[] parts)
    {
        var path = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    private async Task<ScanResult> Scan()
    {
        var owner = VolumeInfo.Enumerate()
            .First(v => _root.StartsWith(v.Root, StringComparison.OrdinalIgnoreCase));

        var volume = owner with { Root = _root + Path.DirectorySeparatorChar };

        return await new WalkScanner().ScanAsync(
            new ScanRequest { Roots = [volume.Root], Parallelism = 2 },
            [volume],
            progress: null,
            CancellationToken.None);
    }

    [Fact]
    public async Task Aggregates_sizes_up_the_tree()
    {
        File_(10_000, "a.bin");
        File_(20_000, "sub", "b.bin");
        File_(30_000, "sub", "deep", "c.bin");

        var result = await Scan();
        var tree = result.Tree;
        var root = tree.Roots[0];

        Assert.Equal(3, result.FileCount);
        Assert.Equal(3, tree.FileCount[root]);

        // Allocated is cluster-rounded, so assert the ordering and a floor rather than an
        // exact byte count: the cluster size varies by volume.
        Assert.True(tree.Allocated[root] >= 60_000);
        Assert.Equal(60_000, tree.Logical[root]);

        var sub = Child(tree, root, "sub");
        Assert.Equal(50_000, tree.Logical[sub]);
        Assert.Equal(2, tree.FileCount[sub]);

        var deep = Child(tree, sub, "deep");
        Assert.Equal(30_000, tree.Logical[deep]);
    }

    [Fact]
    public async Task Children_of_a_node_are_a_contiguous_index_range()
    {
        for (var i = 0; i < 50; i++) File_(100, "many", $"f{i}.bin");

        var tree = (await Scan()).Tree;
        var many = Child(tree, tree.Roots[0], "many");

        Assert.Equal(50, tree.ChildCount[many]);

        // The contiguity invariant is what makes descending into a huge directory a slice.
        var first = tree.FirstChild[many];
        for (var i = 0; i < 50; i++) Assert.Equal(many, tree.Parent[first + i]);
    }

    [Fact]
    public async Task Rebuilds_full_paths_from_the_tree()
    {
        var expected = File_(1, "x", "y", "z.bin");

        var tree = (await Scan()).Tree;
        var node = Analysis.TreeQuery.Find(tree, expected);

        Assert.NotEqual(NodeStore.NoNode, node);
        Assert.Equal(expected, tree.GetPath(node), ignoreCase: true);
    }

    [Fact]
    public async Task A_junction_loop_does_not_recurse_forever()
    {
        var target = Dir("real");
        File_(5_000, "real", "payload.bin");

        if (!TryCreateJunction(Path.Combine(_root, "loop"), _root)) return;
        if (!TryCreateJunction(Path.Combine(_root, "alias"), target)) return;

        // Completing at all is the assertion: following either junction would recurse
        // until the path limit or forever (README section 4.6, threat T18).
        var tree = (await Scan()).Tree;

        var loop = Child(tree, tree.Roots[0], "loop");
        Assert.True((tree.Flags[loop] & NodeFlags.Reparse) != 0);
        Assert.Equal(0, tree.ChildCount[loop]);
        Assert.Equal(0, tree.Allocated[loop]);

        // The real directory is still counted exactly once.
        Assert.Equal(1, tree.FileCount[tree.Roots[0]]);
    }

    [Fact]
    public async Task Reports_an_unreadable_directory_instead_of_silently_skipping_it()
    {
        var blocked = Dir("blocked");
        File_(1_000, "blocked", "secret.bin");

        // Removing read access from the current user is the closest reproduction of the
        // AccessDenied case without needing another account.
        var info = new DirectoryInfo(blocked);
        var acl = info.GetAccessControl();
        var identity = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
            identity,
            System.Security.AccessControl.FileSystemRights.ListDirectory,
            System.Security.AccessControl.AccessControlType.Deny));
        info.SetAccessControl(acl);

        try
        {
            var result = await Scan();

            Assert.Contains(result.Errors, e =>
                e.Kind == ScanErrorKind.AccessDenied &&
                e.Path.Equals(blocked, StringComparison.OrdinalIgnoreCase));

            // The node still exists and is flagged, so its subtree is not silently
            // reported as empty.
            var node = Child(result.Tree, result.Tree.Roots[0], "blocked");
            Assert.True((result.Tree.Flags[node] & NodeFlags.Incomplete) != 0);
        }
        finally
        {
            acl.RemoveAccessRuleAll(new System.Security.AccessControl.FileSystemAccessRule(
                identity,
                System.Security.AccessControl.FileSystemRights.ListDirectory,
                System.Security.AccessControl.AccessControlType.Deny));
            info.SetAccessControl(acl);
        }
    }

    [Fact]
    public async Task Does_not_skip_hidden_or_system_files()
    {
        var hidden = File_(4_000, "hidden.bin");
        System.IO.File.SetAttributes(hidden, FileAttributes.Hidden | FileAttributes.System);

        var tree = (await Scan()).Tree;

        // The BCL default skips Hidden|System, which would hide pagefile.sys, hiberfil.sys
        // and most of AppData - i.e. the whole point of the tool.
        Assert.Equal(1, tree.FileCount[tree.Roots[0]]);
        Assert.Equal(4_000, tree.Logical[tree.Roots[0]]);
    }

    [Fact]
    public async Task Snapshot_round_trips_through_the_file_format()
    {
        File_(11_111, "a.bin");
        File_(22_222, "sub", "b.bin");
        File_(1, "sub", "unicode-写真-😀.bin");

        var original = await Scan();
        var path = Path.Combine(_root, "test.pmsnap");
        SnapshotFile.Write(path, original);

        var reloaded = SnapshotFile.Read(path);

        Assert.Equal(original.Scanner, reloaded.Scanner);
        Assert.Equal(original.Flags, reloaded.Flags);
        Assert.Equal(original.StartedUtc, reloaded.StartedUtc, TimeSpan.FromSeconds(1));
        Assert.Equal(original.Volumes.Count, reloaded.Volumes.Count);
        Assert.Equal(original.Tree.Count, reloaded.Tree.Count);

        for (var i = 0; i < original.Tree.Count; i++)
        {
            Assert.Equal(original.Tree.Parent[i], reloaded.Tree.Parent[i]);
            Assert.Equal(original.Tree.Allocated[i], reloaded.Tree.Allocated[i]);
            Assert.Equal(original.Tree.Logical[i], reloaded.Tree.Logical[i]);
            Assert.Equal(original.Tree.FileCount[i], reloaded.Tree.FileCount[i]);
            Assert.Equal(original.Tree.Flags[i], reloaded.Tree.Flags[i]);
            Assert.Equal(original.Tree.Name(i), reloaded.Tree.Name(i));
            Assert.Equal(original.Tree.GetPath(i), reloaded.Tree.GetPath(i));
        }
    }

    [Fact]
    public void Rejects_a_file_that_is_not_a_snapshot()
    {
        var path = Path.Combine(_root, "garbage.pmsnap");
        System.IO.File.WriteAllBytes(path, new byte[256]);

        Assert.Throws<InvalidDataException>(() => SnapshotFile.Read(path));
    }

    private static int Child(NodeStore tree, int parent, string name)
    {
        var children = tree.Children(parent);
        for (var i = children.Start.Value; i < children.End.Value; i++)
            if (tree.Name(i).Equals(name, StringComparison.OrdinalIgnoreCase)) return i;

        throw new Xunit.Sdk.XunitException($"no child named '{name}' under '{tree.GetPath(parent)}'");
    }

    /// <summary>
    /// Junctions, unlike symlinks, need no elevation or developer mode. Returns false if
    /// the platform refuses anyway, so the test skips rather than failing spuriously.
    /// </summary>
    private static bool TryCreateJunction(string link, string target)
    {
        var psi = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add("mklink");
        psi.ArgumentList.Add("/J");
        psi.ArgumentList.Add(link);
        psi.ArgumentList.Add(target);

        using var process = Process.Start(psi);
        if (process is null) return false;
        process.WaitForExit(10_000);
        return process.ExitCode == 0 && Directory.Exists(link);
    }
}
