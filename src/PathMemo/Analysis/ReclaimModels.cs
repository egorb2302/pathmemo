using PathMemo.Audit;
using PathMemo.Config;

namespace PathMemo.Analysis;

/// <summary>
/// How the space is meant to come back - the "Right way" column of README section 7.2.
/// </summary>
/// <remarks>
/// Named for the action rather than for the remedy, because <see cref="Audit.Remedy"/> is
/// already the audit's word for the same idea one layer up: a finding carries a remedy, a
/// rule carries the action that remedy resolves to.
/// </remarks>
internal enum ReclaimAction
{
    /// <summary>pathmemo can remove it itself.</summary>
    Delete,

    /// <summary>
    /// Another tool must do it. <c>git gc</c> repacks a repository; deleting
    /// <c>.git\objects</c> destroys it. pathmemo reports these and refuses to delete them.
    /// </summary>
    Command,

    /// <summary>A person has to decide. Reported, never acted on.</summary>
    Manual,
}

/// <summary>Whether a rule is about directories, files, or does not care.</summary>
internal enum MatchKind
{
    Directory,
    File,
    Any,
}

/// <summary>
/// One cleanup rule (README section 7.2). Data, never code.
/// </summary>
/// <remarks>
/// <para>
/// Every condition here is expressible in <c>config.json</c>, because the built-in rules
/// and a user's own go through the same evaluator: a built-in rule that could do something
/// a custom one cannot would be a rule engine with a private back door
/// (README section 7.4).
/// </para>
/// <para>
/// Patterns are globs (README section 12.2). Brace alternation - the <c>{Debug,Release}</c>
/// of the table in README section 7.2 - is written out as separate patterns rather than
/// added to the glob syntax: one more metacharacter buys one line of table and costs
/// every reader of every pattern.
/// </para>
/// </remarks>
internal sealed record ReclaimRule
{
    internal required string Id { get; init; }
    internal required IReadOnlyList<string> Patterns { get; init; }
    internal required Risk Risk { get; init; }
    internal required Recoverability Recoverability { get; init; }

    /// <summary>One line, shown next to the rule: what it is and why it is safe.</summary>
    internal string What { get; init; } = "";

    internal ReclaimAction Action { get; init; } = ReclaimAction.Delete;

    /// <summary>
    /// The tool's own command. Mandatory for <see cref="ReclaimAction.Command"/>; on a
    /// <see cref="ReclaimAction.Delete"/> rule it is the tidier way to reach the same place,
    /// printed as advice.
    /// </summary>
    internal string? Command { get; init; }

    internal MatchKind Kind { get; init; } = MatchKind.Directory;

    /// <summary>Matches below this are ignored - noise, not space.</summary>
    internal long MinSizeBytes { get; init; }

    /// <summary>Only matches last modified longer ago than this.</summary>
    internal TimeSpan? OlderThan { get; init; }

    /// <summary>A directory that must sit beside the match: Unity's <c>Assets</c>.</summary>
    internal string? RequiresSibling { get; init; }

    /// <summary>A child the match must contain: Unity's <c>Library\ArtifactDB</c>.</summary>
    internal string? RequiresChild { get; init; }

    /// <summary>A child that must itself be over <see cref="ChildOverBytes"/>.</summary>
    internal string? RequiresChildOver { get; init; }

    internal long ChildOverBytes { get; init; }

    /// <summary>Patterns that veto a match: <c>sys.old_logs</c> stays out of ProgramData.</summary>
    internal IReadOnlyList<string> NotUnder { get; init; } = [];

    /// <summary>
    /// True when the directory has to stay and only its contents go. <c>%TEMP%</c> is the
    /// case that matters: Windows and half the installed software expect it to exist, and
    /// the guard refuses to delete it anyway (README section 9.3).
    /// </summary>
    internal bool ContentsOnly { get; init; }

    /// <summary>True for a rule that came from <c>config.json</c>.</summary>
    internal bool Custom { get; init; }

    /// <summary>The rule's family, which is what the report groups its subtotals by.</summary>
    internal string Family => Id.IndexOf('.') is var dot && dot > 0 ? Id[..dot] : "other";
}

/// <summary>Names for the two axes, as they appear on screen and in JSON.</summary>
internal static class ReclaimNames
{
    internal static string Of(Risk risk) => risk.ToString().ToLowerInvariant();

    internal static string Of(Recoverability recoverability) => recoverability.ToString().ToLowerInvariant();

    internal static bool TryRisk(string text, out Risk risk)
    {
        switch (text.Trim().ToLowerInvariant())
        {
            case "safe": risk = Risk.Safe; return true;
            case "caution": risk = Risk.Caution; return true;
            case "danger": risk = Risk.Danger; return true;
            default: risk = Risk.Safe; return false;
        }
    }

    internal static bool TryRecoverability(string text, out Recoverability value)
    {
        switch (text.Trim().ToLowerInvariant())
        {
            case "instant": value = Recoverability.Instant; return true;
            case "redownload": value = Recoverability.Redownload; return true;
            case "rebuild": value = Recoverability.Rebuild; return true;
            case "irreversible": value = Recoverability.Irreversible; return true;
            default: value = Recoverability.Instant; return false;
        }
    }
}

/// <summary>
/// One path a rule claims, with the two numbers that matter (README section 7.3).
/// </summary>
/// <param name="Bytes">Unique-allocated size from the snapshot: what this occupies.</param>
/// <param name="SharedBytes">
/// The part of <paramref name="Bytes"/> held by files with more than one hard link, which
/// deleting this name does not necessarily release (README section 3.2).
/// </param>
internal sealed record ReclaimMatch
{
    internal required ReclaimRule Rule { get; init; }
    internal required string Path { get; init; }
    internal required bool IsDirectory { get; init; }
    internal required long Bytes { get; init; }
    internal long SharedBytes { get; init; }
    internal int FileCount { get; init; }
    internal DateTime ModifiedUtc { get; init; }

    /// <summary>Node index in the snapshot this came from, for the TUI.</summary>
    internal int Node { get; init; } = -1;

    /// <summary>What deleting this actually frees.</summary>
    internal long Reclaimable => Math.Max(0, Bytes - SharedBytes);

    /// <summary>Set when the guard has already said no; the match is shown, not offered.</summary>
    internal string? Refusal { get; init; }

    internal bool Offered => Refusal is null && Rule.Action == ReclaimAction.Delete;
}

/// <summary>Every match of one rule, which is how the report is grouped.</summary>
internal sealed record ReclaimGroup(ReclaimRule Rule, IReadOnlyList<ReclaimMatch> Matches)
{
    internal long Bytes => Matches.Sum(m => m.Bytes);
    internal long Reclaimable => Matches.Sum(m => m.Reclaimable);
    internal long OfferedBytes => Matches.Where(m => m.Offered).Sum(m => m.Reclaimable);
    internal long SharedBytes => Matches.Sum(m => m.SharedBytes);
    internal int RefusedCount => Matches.Count(m => m.Refusal is not null);
    internal int Count => Matches.Count;
}

/// <summary>
/// What the rules found in one snapshot (README section 7.3).
/// </summary>
internal sealed record ReclaimReport
{
    internal required IReadOnlyList<ReclaimGroup> Groups { get; init; }
    internal required long ScanId { get; init; }
    internal DateTime ScanStartedUtc { get; init; }

    /// <summary>Matches dropped by <c>protect.keep</c>, counted so the user can see it worked.</summary>
    internal int KeptCount { get; init; }

    internal long KeptBytes { get; init; }

    /// <summary>Rules switched off in the configuration, named so the report is honest.</summary>
    internal IReadOnlyList<string> DisabledRules { get; init; } = [];

    /// <summary>True when the run was cut short and the numbers are a floor.</summary>
    internal bool Partial { get; init; }

    internal IEnumerable<ReclaimGroup> AtMost(Risk risk) => Groups.Where(g => g.Rule.Risk <= risk);

    internal long ReclaimableAtMost(Risk risk) => AtMost(risk).Sum(g => g.Reclaimable);

    internal long OfferedAtMost(Risk risk) => AtMost(risk).Sum(g => g.OfferedBytes);

    internal int CountAtMost(Risk risk) => AtMost(risk).Sum(g => g.Count);

    internal bool IsEmpty => Groups.Count == 0;
}

/// <summary>
/// The rule set in force: the defaults, minus what is disabled, plus what is configured
/// (README section 7.4).
/// </summary>
internal static class RuleSet
{
    internal static IReadOnlyList<ReclaimRule> For(RulesSettings settings)
    {
        var disabled = settings.Disabled.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var rules = new List<ReclaimRule>(DefaultRules.All.Count + settings.Custom.Count);
        rules.AddRange(DefaultRules.All.Where(r => !disabled.Contains(r.Id)));

        // A custom rule with a built-in id replaces it rather than doubling it: that is
        // how a user changes one threshold without retyping the whole table.
        foreach (var custom in settings.Custom)
        {
            if (disabled.Contains(custom.Id)) continue;

            var at = rules.FindIndex(r => r.Id.Equals(custom.Id, StringComparison.OrdinalIgnoreCase));
            if (at >= 0) rules[at] = custom;
            else rules.Add(custom);
        }

        return rules;
    }

    internal static IReadOnlyList<ReclaimRule> Current() => For(AppConfig.Current.Rules);
}
