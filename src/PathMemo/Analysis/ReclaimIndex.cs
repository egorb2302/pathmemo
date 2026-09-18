using PathMemo.Config;
using PathMemo.Snapshots;

namespace PathMemo.Analysis;

/// <summary>
/// Which rule claims which node of the loaded snapshot, for the screens
/// (README sections 14.1, 14.2).
/// </summary>
/// <remarks>
/// <para>
/// Built on a background thread the moment a snapshot is loaded, because the tree screen
/// draws a frame in well under a millisecond and running the rules takes a few hundred:
/// doing it on the render thread would put a visible stall into the first frame after
/// every scan (README section 20). Until it is ready the badges are simply absent, which
/// is what the screen would show anyway for a node no rule claims.
/// </para>
/// <para>
/// The index is <b>not</b> verified against the guard, unlike the CLI's report
/// (README sections 7.3, 7.5). Verification is a handle per match and would turn a background
/// pass into seconds of disk work behind a screen the user may never open; the delete
/// dialog opens every path through the guard before anything happens, and shows the
/// refusals there (README section 9.3).
/// </para>
/// </remarks>
internal sealed class ReclaimIndex
{
    private readonly Dictionary<int, ReclaimMatch> _byNode = [];

    /// <summary>The rule groups, largest first - what the reclaim screen lists.</summary>
    internal IReadOnlyList<ReclaimGroup> Groups { get; private init; } = [];

    internal long TotalReclaimable => Groups.Sum(g => g.Reclaimable);

    internal int MatchCount => _byNode.Count;

    internal ReclaimMatch? For(int node) => _byNode.GetValueOrDefault(node);

    /// <summary>The badge the tree shows on a matched row: the risk, then the way back.</summary>
    internal string BadgeFor(int node) =>
        _byNode.TryGetValue(node, out var match)
            ? $"{ReclaimNames.Of(match.Rule.Risk)} {ReclaimNames.Of(match.Rule.Recoverability)}"
            : "";

    internal static ReclaimIndex Build(NodeStore tree, CancellationToken ct = default)
    {
        var rules = RuleSet.Current();
        var keep = AppConfig.Current.Protect.Keep.Select(PathGlob.Parse).ToList();

        var matches = ReclaimPlanner
            .DropNested(new RuleEngine(rules).Match(tree, ct))
            .Where(m => !keep.Any(k => k.Matches(m.Path)))
            .ToList();

        var index = new ReclaimIndex
        {
            Groups = [.. matches
                .GroupBy(m => m.Rule.Id, StringComparer.OrdinalIgnoreCase)
                .Select(g => new ReclaimGroup(g.First().Rule,
                    [.. g.OrderByDescending(m => m.Reclaimable)]))
                .OrderByDescending(g => g.Reclaimable)],
        };

        foreach (var match in matches)
            if (match.Node >= 0) index._byNode[match.Node] = match;

        return index;
    }

    /// <summary>An index that claims nothing, for before the work has finished.</summary>
    internal static ReclaimIndex Empty { get; } = new();
}
