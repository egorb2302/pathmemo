using System.Globalization;
using PathMemo.Analysis;
using PathMemo.Cli.Output;
using PathMemo.Snapshots;

namespace PathMemo.Cli.Commands;

/// <summary>
/// The scriptable workhorse: largest files or directories, with filters.
/// </summary>
/// <remarks>
/// <c>--paths-only</c> makes this pipeable, which is the point of a semi-CLI tool:
/// <c>pathmemo top --min 1GB --paths-only | pathmemo rm --from-stdin --dry-run</c>.
/// </remarks>
internal static class TopCommand
{
    internal static int Run(TopOptions options)
    {
        if (!SnapshotLoader.TryLoad(options.ScanId, out var snapshot, out var id)) return ExitCode.NoData;

        var tree = snapshot.Tree;

        var under = NodeStore.NoNode;
        if (options.Under is { } path)
        {
            under = TreeQuery.Find(tree, Path.GetFullPath(path));
            if (under == NodeStore.NoNode)
            {
                Console.Error.WriteLine($"pathmemo: not in scan {id}: {path}");
                return ExitCode.NoData;
            }
        }

        var filter = new TreeQuery.TopFilter
        {
            Directories = options.Directories,
            Limit = options.Limit,
            MinBytes = options.MinBytes,
            Under = under == NodeStore.NoNode ? null : under,
            Extensions = options.Extensions,
            ModifiedBefore = options.OlderThan is { } older ? DateTime.UtcNow - older : null,
            ModifiedAfter = options.NewerThan is { } newer ? DateTime.UtcNow - newer : null,
        };

        var nodes = TreeQuery.Top(tree, filter, options.SizeMode);

        if (options.PathsOnly)
        {
            foreach (var node in nodes) Console.Out.WriteLine(tree.GetPath(node));
            return nodes.Length == 0 ? ExitCode.NoData : ExitCode.Ok;
        }

        Print(tree, nodes, options, id, snapshot.StartedUtc);
        return ExitCode.Ok;
    }

    private static void Print(
        NodeStore tree, int[] nodes, TopOptions options, long id, DateTime startedUtc)
    {
        var w = Console.Out;
        var width = ConsoleWidth();

        w.WriteLine();
        if (nodes.Length == 0)
        {
            w.WriteLine("No matches.");
            return;
        }

        w.WriteLine("     SIZE  MODIFIED    PATH");
        w.WriteLine("  " + new string('-', Math.Min(width - 4, 100)));

        long total = 0;
        foreach (var node in nodes)
        {
            var size = TreeQuery.Size(tree, node, options.SizeMode);
            total += size;

            w.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "  {0,7}  {1:yyyy-MM-dd}  {2}",
                SizeFormat.Bytes(size),
                tree.ModifiedUtc(node),
                PathDisplay.Shorten(tree.GetPath(node), Math.Max(30, width - 24))));
        }

        w.WriteLine("  " + new string('-', Math.Min(width - 4, 100)));
        w.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "  {0} {1} · {2} · scan {3} of {4:yyyy-MM-dd HH:mm} UTC",
            nodes.Length,
            options.Directories ? "directories" : "files",
            SizeFormat.Bytes(total), id, startedUtc));
    }

    private static int ConsoleWidth()
    {
        try { return Console.IsOutputRedirected ? 120 : Math.Max(80, Console.WindowWidth); }
        catch (IOException) { return 120; }
    }
}

internal sealed record TopOptions
{
    internal long? ScanId { get; init; }
    internal bool Directories { get; init; }
    internal int Limit { get; init; } = 40;
    internal long MinBytes { get; init; }
    internal string? Under { get; init; }
    internal IReadOnlySet<string>? Extensions { get; init; }
    internal TimeSpan? OlderThan { get; init; }
    internal TimeSpan? NewerThan { get; init; }
    internal bool PathsOnly { get; init; }
    internal SizeMode SizeMode { get; init; } = SizeMode.Unique;
}
