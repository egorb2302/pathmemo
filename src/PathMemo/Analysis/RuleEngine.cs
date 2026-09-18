using System.IO.Enumeration;
using System.Text;
using PathMemo.Audit;
using PathMemo.Config;
using PathMemo.Snapshots;

namespace PathMemo.Analysis;

/// <summary>
/// Runs the reclaim rules over a snapshot (README sections 7.2, 12.2).
/// </summary>
/// <remarks>
/// <para>
/// One pass over the node arrays, and for the overwhelming majority of nodes that pass
/// costs a single hash lookup on the last path segment. Building the full path of every
/// node and matching it against thirty rules would be a million string concatenations and
/// several seconds; the prefilter rejects 99% of nodes before a path is ever materialised
/// (README section 12.2).
/// </para>
/// <para>
/// Three buckets, because that is how the patterns actually look: an exact final segment
/// (<c>node_modules</c>, <c>obj</c>), an extension (<c>*.pyc</c>, <c>*.log</c>), and the
/// handful with a wildcard in the middle (<c>Cache*</c>, <c>thumbcache_*.db</c>) - and
/// even those are screened by their literal prefix before the glob runs.
/// </para>
/// </remarks>
internal sealed class RuleEngine
{
    /// <summary>One pattern of one rule, with everything precomputed that can be.</summary>
    private sealed record Compiled(ReclaimRule Rule, PathGlob Glob, PathGlob[] NotUnder)
    {
        /// <summary>Literal text the name must start with, for the wildcard bucket.</summary>
        internal string Prefix { get; init; } = "";

        internal string Suffix { get; init; } = "";

        internal string LastSegment { get; init; } = "";

        /// <summary>
        /// True for a pattern whose last segment is bare <c>*.ext</c>.
        /// </summary>
        /// <remarks>
        /// Such a pattern is about a file even when the rule as a whole says
        /// <see cref="MatchKind.Any"/>: <c>dev.pycache</c> means the <c>__pycache__</c>
        /// directory and the <c>.pyc</c> files, not a directory somebody named
        /// <c>weird.pyc</c>. A rule that really does mean a directory with an extension in
        /// its name says so with <c>kind: directory</c>.
        /// </remarks>
        internal bool ExtensionOnly { get; init; }
    }

    /// <summary>
    /// Longest name worth transcoding for a lookup. Past this no rule pattern can match,
    /// and the buffer stays on the stack.
    /// </summary>
    private const int MaxNameLength = 260;

    private readonly Dictionary<string, List<Compiled>> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<Compiled>> _byExtension = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Compiled> _wildcards = [];

    internal IReadOnlyList<ReclaimRule> Rules { get; }

    internal RuleEngine(IReadOnlyList<ReclaimRule> rules)
    {
        Rules = rules;

        foreach (var rule in rules)
        {
            var notUnder = rule.NotUnder.Select(PathGlob.Parse).ToArray();

            foreach (var pattern in rule.Patterns)
            {
                var glob = PathGlob.Parse(pattern);
                var last = LastSegment(PathGlob.Expand(pattern));
                var compiled = new Compiled(rule, glob, notUnder) { LastSegment = last };

                if (!HasWildcard(last))
                {
                    Add(_byName, last, compiled);
                    continue;
                }

                // "*.pyc" and friends: the extension is the whole filter, and every node
                // already has to have its extension read for other reasons.
                if (last.Length > 2 && last[0] == '*' && last.IndexOfAny(['*', '?'], 1) < 0)
                {
                    Add(_byExtension, last[1..], compiled with { ExtensionOnly = true });
                    continue;
                }

                var star = last.IndexOfAny(['*', '?']);
                _wildcards.Add(compiled with
                {
                    Prefix = last[..star],
                    Suffix = last[^1] is '*' or '?' ? "" : last[(last.LastIndexOfAny(['*', '?']) + 1)..],
                });
            }
        }
    }

    internal static RuleEngine Current() => new(RuleSet.Current());

    /// <summary>
    /// Every node the rules claim, before nesting, the keep list or the guard have had
    /// their say - that is <see cref="ReclaimPlanner"/>'s work.
    /// </summary>
    internal List<ReclaimMatch> Match(NodeStore tree, CancellationToken ct = default)
    {
        var matches = new List<ReclaimMatch>(256);
        var candidates = new List<Compiled>(8);

        Span<char> buffer = stackalloc char[MaxNameLength];

        for (var node = 0; node < tree.Count; node++)
        {
            if ((node & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();

            // pathmemo's own store is never a recommendation, whatever it looks like
            // (README section 16, threat T16).
            if ((tree.Flags[node] & NodeFlags.SelfData) != 0) continue;

            var utf8 = tree.NameUtf8(node);
            if (utf8.Length == 0 || utf8.Length > buffer.Length) continue;

            var length = Encoding.UTF8.GetChars(utf8, buffer);
            var name = buffer[..length];

            candidates.Clear();
            Collect(name, candidates);
            if (candidates.Count == 0) continue;

            // The path is built once, here, for the few nodes that got this far.
            var path = tree.GetPath(node);

            foreach (var compiled in candidates)
            {
                if (!Accepts(compiled, tree, node, path)) continue;

                var (bytes, shared, files) = Measure(tree, node);
                if (bytes < compiled.Rule.MinSizeBytes) continue;

                matches.Add(new ReclaimMatch
                {
                    Rule = compiled.Rule,
                    Path = path,
                    IsDirectory = tree.IsDirectory(node),
                    Bytes = bytes,
                    SharedBytes = shared,
                    FileCount = files,
                    ModifiedUtc = tree.ModifiedUtc(node),
                    Node = node,
                });

                break;      // one rule per node; the first in configuration order wins
            }
        }

        return matches;
    }

    /// <summary>Which patterns could possibly match this name.</summary>
    private void Collect(ReadOnlySpan<char> name, List<Compiled> into)
    {
        if (_byName.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(name, out var exact))
            into.AddRange(exact);

        var dot = name.LastIndexOf('.');
        if (dot > 0 && _byExtension.GetAlternateLookup<ReadOnlySpan<char>>()
                .TryGetValue(name[dot..], out var byExtension))
            into.AddRange(byExtension);

        foreach (var wildcard in _wildcards)
        {
            // Ordinal prefix and suffix first: a string comparison that fails on the first
            // character costs a fraction of a glob that fails on the last.
            if (wildcard.Prefix.Length > 0
                && !name.StartsWith(wildcard.Prefix, StringComparison.OrdinalIgnoreCase)) continue;

            if (wildcard.Suffix.Length > 0
                && !name.EndsWith(wildcard.Suffix, StringComparison.OrdinalIgnoreCase)) continue;

            if (FileSystemName.MatchesSimpleExpression(wildcard.LastSegment, name, ignoreCase: true))
                into.Add(wildcard);
        }
    }

    /// <summary>Everything the rule asks of a node beyond its name matching.</summary>
    private static bool Accepts(Compiled compiled, NodeStore tree, int node, string path)
    {
        var rule = compiled.Rule;
        var isDirectory = tree.IsDirectory(node);

        if (rule.Kind == MatchKind.Directory && !isDirectory) return false;
        if (rule.Kind == MatchKind.File && isDirectory) return false;
        if (compiled.ExtensionOnly && rule.Kind == MatchKind.Any && isDirectory) return false;

        // A junction is a name, not the data behind it. Deleting one on a rule's advice
        // would remove somebody's link to a directory the rule never looked at.
        if ((tree.Flags[node] & NodeFlags.Reparse) != 0) return false;

        if (!compiled.Glob.Matches(path)) return false;

        foreach (var veto in compiled.NotUnder)
            if (veto.Matches(path)) return false;

        if (rule.OlderThan is { } age && tree.ModifiedUtc(node) > DateTime.UtcNow - age) return false;

        if (rule.RequiresChild is { } child && FindChild(tree, node, child) == NodeStore.NoNode) return false;

        if (rule.RequiresSibling is { } sibling)
        {
            var parent = tree.Parent[node];
            if (parent == NodeStore.NoNode || FindChild(tree, parent, sibling) == NodeStore.NoNode) return false;
        }

        if (rule.RequiresChildOver is { } big)
        {
            var found = FindChild(tree, node, big);
            if (found == NodeStore.NoNode || tree.Allocated[found] < rule.ChildOverBytes) return false;
        }

        return true;
    }

    private static int FindChild(NodeStore tree, int node, string name)
    {
        var children = tree.Children(node);
        for (var i = children.Start.Value; i < children.End.Value; i++)
            if (name.Equals(tree.Name(i), StringComparison.OrdinalIgnoreCase)) return i;

        return NodeStore.NoNode;
    }

    /// <summary>
    /// What this subtree occupies, and how much of it is shared with names outside it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The size is the snapshot's unique-allocated total, which the scanner already
    /// aggregated bottom-up. The shared part has to be walked for, because it is a property
    /// of the individual files: a file with more than one hard link keeps its data until
    /// the last name goes, so deleting this one may free nothing at all (README section 3.2).
    /// </para>
    /// <para>
    /// Every multiply-linked file counts as shared, even when the other links are inside
    /// this same subtree. Proving otherwise needs file identity, which <c>.pmsnap</c> v1
    /// does not carry, and the direction of the error is the one to be wrong in: an
    /// underestimate disappoints, an overestimate is a promise of space that never comes
    /// (README section 7.5).
    /// </para>
    /// </remarks>
    internal static (long Bytes, long Shared, int Files) Measure(NodeStore tree, int node)
    {
        if (!tree.IsDirectory(node))
        {
            var alias = (tree.Flags[node] & NodeFlags.HardlinkAlias) != 0;
            var size = alias ? 0 : tree.Allocated[node];
            return (size, tree.LinkCount[node] > 1 ? size : 0, 1);
        }

        long shared = 0;
        var stack = new Stack<int>();
        stack.Push(node);

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            var children = tree.Children(current);

            for (var i = children.Start.Value; i < children.End.Value; i++)
            {
                if (tree.IsDirectory(i)) { stack.Push(i); continue; }
                if ((tree.Flags[i] & NodeFlags.HardlinkAlias) != 0) continue;
                if (tree.LinkCount[i] > 1) shared += tree.Allocated[i];
            }
        }

        return (tree.Allocated[node], shared, tree.FileCount[node]);
    }

    /// <summary>
    /// The risk of the strictest rule that claims this path, for a deletion that did not
    /// come through <c>reclaim</c> at all (README section 13.1).
    /// </summary>
    internal (Risk Risk, string? RuleId) RiskOf(string path, bool isDirectory)
    {
        var worst = Risk.Safe;
        string? id = null;

        foreach (var rule in Rules)
        {
            if (rule.Risk <= worst && id is not null) continue;
            if (rule.Kind == MatchKind.Directory && !isDirectory) continue;
            if (rule.Kind == MatchKind.File && isDirectory) continue;

            foreach (var pattern in rule.Patterns)
            {
                if (!PathGlob.Parse(pattern).Matches(path)) continue;

                if (rule.Risk >= worst) { worst = rule.Risk; id = rule.Id; }
                break;
            }
        }

        return (worst, id);
    }

    private static void Add(Dictionary<string, List<Compiled>> map, string key, Compiled value)
    {
        if (!map.TryGetValue(key, out var list)) map[key] = list = [];
        list.Add(value);
    }

    private static string LastSegment(string pattern)
    {
        var normalised = pattern.Replace('/', '\\').TrimEnd('\\');
        var slash = normalised.LastIndexOf('\\');
        return slash < 0 ? normalised : normalised[(slash + 1)..];
    }

    private static bool HasWildcard(string text) => text.AsSpan().IndexOfAny('*', '?') >= 0;
}
