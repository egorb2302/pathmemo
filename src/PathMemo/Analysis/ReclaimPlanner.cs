using PathMemo.Config;
using PathMemo.Deletion;
using PathMemo.Snapshots;

namespace PathMemo.Analysis;

/// <summary>What to ask the rules for.</summary>
internal sealed record ReclaimQuery
{
    /// <summary>Only this rule, when the user asked about one.</summary>
    internal string? RuleId { get; init; }

    /// <summary>Drop matches smaller than this, on top of each rule's own floor.</summary>
    internal long MinBytes { get; init; }

    /// <summary>
    /// The keep patterns. Null means the configured ones, which is what every caller
    /// outside a test wants; passing them explicitly is what makes the precedence rule
    /// assertable without a <c>config.json</c> on disk (README section 22.1).
    /// </summary>
    internal IReadOnlyList<string>? Keep { get; init; }

    /// <summary>
    /// Whether to open every match and ask the guard about it. Off in unit tests, which
    /// have a synthetic tree and no filesystem behind it (README section 22.1).
    /// </summary>
    internal bool Verify { get; init; } = true;

    internal IProgress<string>? Progress { get; init; }
}

/// <summary>
/// Turns rule matches into the report of README section 7.3.
/// </summary>
/// <remarks>
/// <para>
/// The work that is not matching: dropping a match that lives inside another match,
/// dropping what the keep list claims, asking the guard whether each remaining path could
/// actually be deleted, and grouping the survivors by rule.
/// </para>
/// <para>
/// The guard is consulted <b>here</b>, while the report is being built, rather than left
/// to the deletion that may follow. A recommendation the guard would refuse is worse than
/// no recommendation: it is a number in the total that never arrives (README section 7.3).
/// The cost is one handle per match, which for the few hundred matches a real disk
/// produces is well under a second.
/// </para>
/// </remarks>
internal static class ReclaimPlanner
{
    internal static ReclaimReport Plan(
        NodeStore tree,
        long scanId,
        DateTime scanStartedUtc,
        IReadOnlyList<ReclaimRule> rules,
        ReclaimQuery? query = null,
        CancellationToken ct = default)
    {
        query ??= new ReclaimQuery();

        if (query.RuleId is { } only)
            rules = [.. rules.Where(r => r.Id.Equals(only, StringComparison.OrdinalIgnoreCase))];

        var matches = new RuleEngine(rules).Match(tree, ct);

        matches = DropNested(matches);

        var keep = (query.Keep ?? AppConfig.Current.Protect.Keep).Select(PathGlob.Parse).ToList();
        var (kept, keptBytes) = (0, 0L);

        var survivors = new List<ReclaimMatch>(matches.Count);
        foreach (var match in matches)
        {
            if (match.Bytes < query.MinBytes) continue;

            // The keep list removes a path from the recommendations entirely rather than
            // showing it as refused: "never appear in recommendations" is what it promises
            // (README section 7.4).
            if (keep.Any(k => k.Matches(match.Path)))
            {
                kept++;
                keptBytes += match.Bytes;
                continue;
            }

            survivors.Add(match);
        }

        if (query.Verify) survivors = Verify(survivors, query.Progress, ct);

        var groups = survivors
            .GroupBy(m => m.Rule.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ReclaimGroup(g.First().Rule,
                [.. g.OrderByDescending(m => m.Reclaimable).ThenBy(m => m.Path, StringComparer.OrdinalIgnoreCase)]))
            .OrderByDescending(g => g.Reclaimable)
            .ThenBy(g => g.Rule.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ReclaimReport
        {
            Groups = groups,
            ScanId = scanId,
            ScanStartedUtc = scanStartedUtc,
            KeptCount = kept,
            KeptBytes = keptBytes,
            DisabledRules = AppConfig.Current.Rules.Disabled,
        };
    }

    /// <summary>
    /// Removes matches that live inside another match.
    /// </summary>
    /// <remarks>
    /// An <c>obj</c> inside a <c>node_modules</c> is already accounted for by the
    /// <c>node_modules</c> row, and counting it again would inflate the one number the
    /// whole report exists to produce. The outer match wins because that is what would
    /// actually be deleted.
    /// </remarks>
    internal static List<ReclaimMatch> DropNested(List<ReclaimMatch> matches)
    {
        var kept = new List<ReclaimMatch>(matches.Count);

        // Shortest path first, so a container is always seen before what it contains.
        foreach (var match in matches.OrderBy(m => m.Path.Length).ThenBy(m => m.Path, StringComparer.OrdinalIgnoreCase))
        {
            var covered = false;

            foreach (var outer in kept)
            {
                if (!outer.IsDirectory) continue;
                if (!IsUnder(match.Path, outer.Path)) continue;

                covered = true;
                break;
            }

            if (!covered) kept.Add(match);
        }

        return kept;
    }

    /// <summary>Segment-aware containment over display paths, not a string prefix test.</summary>
    private static bool IsUnder(string path, string container)
    {
        var trimmed = container.TrimEnd('\\');
        if (path.Length <= trimmed.Length) return false;
        if (!path.StartsWith(trimmed, StringComparison.OrdinalIgnoreCase)) return false;

        return path[trimmed.Length] == '\\';
    }

    /// <summary>
    /// Opens every match and asks the guard. A path that has gone since the scan drops out
    /// of the report; one the guard refuses stays, with the refusal beside it, so the
    /// reason is visible instead of the row silently being absent.
    /// </summary>
    private static List<ReclaimMatch> Verify(
        List<ReclaimMatch> matches, IProgress<string>? progress, CancellationToken ct)
    {
        var set = ProtectedSet.Build();
        var verified = new List<ReclaimMatch>(matches.Count);

        foreach (var match in matches)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(match.Path);

            // A rule that empties a directory is judged by what it deletes, and that is the
            // children, one at a time, by the deletion itself. The directory would be
            // refused here for the very reason it is marked contents-only.
            if (match.Rule.ContentsOnly)
            {
                if (Directory.Exists(match.Path)) verified.Add(match);
                continue;
            }

            var canonical = Canonical.TryOf(match.Path);
            if (canonical is null) continue;        // gone, or unopenable: not a recommendation

            var verdict = set.Check(canonical, match.Path);
            verified.Add(verdict.IsProtected ? match with { Refusal = verdict.Reason } : match);
        }

        return verified;
    }

    /// <summary>
    /// The paths a deletion would be given for this match.
    /// </summary>
    /// <remarks>
    /// One path for an ordinary match. For a contents-only rule the directory has to stay -
    /// <c>%TEMP%</c> is expected to exist by Windows and by half the installed software -
    /// so the targets are its current children, read from the live filesystem rather than
    /// from a snapshot that may be days old.
    /// </remarks>
    internal static IReadOnlyList<string> TargetsOf(ReclaimMatch match)
    {
        if (!match.Rule.ContentsOnly) return [match.Path];

        try
        {
            return [.. Directory.EnumerateFileSystemEntries(match.Path)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Every path the given groups would hand to <c>rm</c>, refusals excluded.</summary>
    internal static List<string> TargetsOf(IEnumerable<ReclaimGroup> groups)
    {
        var paths = new List<string>();

        foreach (var group in groups)
        {
            if (group.Rule.Action != ReclaimAction.Delete) continue;

            foreach (var match in group.Matches)
            {
                if (!match.Offered) continue;
                paths.AddRange(TargetsOf(match));
            }
        }

        return paths;
    }
}
