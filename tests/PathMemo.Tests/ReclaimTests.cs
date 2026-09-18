using PathMemo.Analysis;
using PathMemo.Audit;
using PathMemo.Config;
using PathMemo.Snapshots;
using Xunit;

namespace PathMemo.Tests;

/// <summary>
/// The reclaim rules over synthetic trees (README sections 7, 22.1).
/// </summary>
/// <remarks>
/// No filesystem here on purpose. Matching, nesting, the keep list and the two axes are
/// decisions about paths and numbers, and a synthetic tree can state the awkward cases -
/// a <c>node_modules</c> inside a <c>node_modules</c>, a hard-linked package store, a
/// directory called <c>Library</c> that is not a Unity project - far more precisely than
/// a real disk ever will. What needs the filesystem is in <see cref="ReclaimFileTests"/>.
/// </remarks>
public sealed class ReclaimTests
{
    /// <summary>Runs rules over a tree without opening a single handle.</summary>
    private static ReclaimReport Plan(NodeStore tree, IReadOnlyList<ReclaimRule> rules,
                                      IReadOnlyList<string>? keep = null) =>
        ReclaimPlanner.Plan(tree, 1, DateTime.UtcNow, rules,
            new ReclaimQuery { Verify = false, Keep = keep ?? [] });

    private static ReclaimRule Rule(string id, params string[] patterns) => new()
    {
        Id = id,
        Patterns = patterns,
        Risk = Risk.Safe,
        Recoverability = Recoverability.Rebuild,
    };

    private static ReclaimGroup? Group(ReclaimReport report, string id) =>
        report.Groups.FirstOrDefault(g => g.Rule.Id == id);

    [Fact]
    public void A_rule_matches_its_directory_anywhere_in_the_tree()
    {
        var tree = new TestTree()
            .File(@"C:\work\app\node_modules\react\index.js", 4096)
            .File(@"C:\work\app\src\main.ts", 1024)
            .File(@"D:\other\node_modules\lodash\index.js", 8192)
            .Build();

        var report = Plan(tree, [Rule("dev.node_modules", @"**\node_modules")]);
        var group = Group(report, "dev.node_modules");

        Assert.NotNull(group);
        Assert.Equal(2, group.Count);
        Assert.Equal(4096 + 8192, group.Bytes);
    }

    [Fact]
    public void A_match_inside_another_match_is_not_counted_twice()
    {
        // The inner one would be deleted by the outer one; counting both would inflate the
        // single number the whole report exists to produce (README section 7.3).
        var tree = new TestTree()
            .File(@"C:\work\node_modules\a\node_modules\b\index.js", 1 << 20)
            .File(@"C:\work\node_modules\a\index.js", 1 << 20)
            .Build();

        var report = Plan(tree, [Rule("dev.node_modules", @"**\node_modules")]);
        var group = Group(report, "dev.node_modules");

        Assert.NotNull(group);
        Assert.Equal(1, group.Count);
        Assert.Equal(2 << 20, group.Bytes);
        Assert.Equal(@"C:\work\node_modules", group.Matches[0].Path);
    }

    [Fact]
    public void A_match_of_one_rule_inside_a_match_of_another_also_goes()
    {
        var tree = new TestTree()
            .File(@"C:\work\node_modules\pkg\obj\build.dll", 1 << 20)
            .Build();

        var report = Plan(tree,
        [
            Rule("dev.node_modules", @"**\node_modules"),
            Rule("dev.dotnet_artifacts", @"**\obj"),
        ]);

        Assert.Single(report.Groups);
        Assert.Equal("dev.node_modules", report.Groups[0].Rule.Id);
    }

    [Fact]
    public void A_sibling_condition_separates_a_unity_project_from_any_other_library()
    {
        var tree = new TestTree()
            .File(@"C:\games\proj\Assets\scene.unity", 512)
            .File(@"C:\games\proj\Library\ArtifactDB", 4 << 20)
            .File(@"C:\docs\Library\ArtifactDB", 4 << 20)         // no Assets beside it
            .File(@"C:\music\Library\songs.db", 4 << 20)          // no ArtifactDB inside it
            .Build();

        var report = Plan(tree, [DefaultRules.ById("dev.unity_library")!]);
        var group = Group(report, "dev.unity_library");

        Assert.NotNull(group);
        Assert.Single(group.Matches);
        Assert.Equal(@"C:\games\proj\Library", group.Matches[0].Path);
    }

    [Fact]
    public void A_git_repository_is_only_reported_once_its_objects_have_grown()
    {
        var tree = new TestTree()
            .File(@"C:\work\big\.git\objects\pack\pack-1.pack", 900L << 20)
            .File(@"C:\work\small\.git\objects\pack\pack-2.pack", 4L << 20)
            .Build();

        var report = Plan(tree, [DefaultRules.ById("dev.git_gc")!]);
        var group = Group(report, "dev.git_gc");

        Assert.NotNull(group);
        Assert.Single(group.Matches);
        Assert.Equal(@"C:\work\big\.git", group.Matches[0].Path);
    }

    [Fact]
    public void The_git_rule_never_offers_to_delete_anything()
    {
        // Deleting .git\objects destroys the repository; the rule exists to find the ones
        // worth repacking, and no flag may turn it into a deletion (README section 7.2).
        var tree = new TestTree()
            .File(@"C:\work\big\.git\objects\pack\pack-1.pack", 900L << 20)
            .Build();

        var report = Plan(tree, [DefaultRules.ById("dev.git_gc")!]);

        Assert.Equal(ReclaimAction.Command, report.Groups[0].Rule.Action);
        Assert.False(report.Groups[0].Matches[0].Offered);
        Assert.Empty(ReclaimPlanner.TargetsOf(report.Groups));
    }

    [Fact]
    public void A_virtual_environment_needs_its_configuration_file()
    {
        var tree = new TestTree()
            .File(@"C:\work\a\.venv\pyvenv.cfg", 120)
            .File(@"C:\work\a\.venv\Lib\site-packages\numpy\core.pyd", 8 << 20)
            .File(@"C:\media\venv\clip.mp4", 900 << 20)          // somebody's folder, not a venv
            .Build();

        var report = Plan(tree, [DefaultRules.ById("dev.venv")!]);
        var group = Group(report, "dev.venv");

        Assert.NotNull(group);
        Assert.Single(group.Matches);
        Assert.Equal(@"C:\work\a\.venv", group.Matches[0].Path);
    }

    [Fact]
    public void An_age_condition_leaves_recent_files_alone()
    {
        var old = SnapshotTime.FromDateTime(DateTime.UtcNow.AddDays(-400));
        var recent = SnapshotTime.FromDateTime(DateTime.UtcNow.AddDays(-2));

        var tree = new TestTree()
            .File(@"C:\apps\logs\old.log", 8 << 20, mtime: old)
            .File(@"C:\apps\logs\today.log", 8 << 20, mtime: recent)
            .File(@"C:\apps\logs\tiny.log", 400, mtime: old)          // under the 1 MB floor
            .Build();

        var report = Plan(tree, [DefaultRules.ById("sys.old_logs")!]);
        var group = Group(report, "sys.old_logs");

        Assert.NotNull(group);
        Assert.Single(group.Matches);
        Assert.Equal(@"C:\apps\logs\old.log", group.Matches[0].Path);
    }

    [Fact]
    public void A_veto_pattern_keeps_a_rule_out_of_a_directory()
    {
        var old = SnapshotTime.FromDateTime(DateTime.UtcNow.AddDays(-400));

        var rule = DefaultRules.ById("sys.old_logs")! with { NotUnder = [@"C:\keepout\**"] };

        var tree = new TestTree()
            .File(@"C:\apps\a.log", 8 << 20, mtime: old)
            .File(@"C:\keepout\b.log", 8 << 20, mtime: old)
            .Build();

        var group = Group(Plan(tree, [rule]), "sys.old_logs");

        Assert.NotNull(group);
        Assert.Single(group.Matches);
        Assert.Equal(@"C:\apps\a.log", group.Matches[0].Path);
    }

    [Fact]
    public void A_hard_linked_file_is_counted_as_shared_rather_than_promised()
    {
        // The pnpm case: the store and the project both name the same data, so deleting
        // one of them frees nothing. Without file identity in the snapshot the honest
        // answer is "shared", and the error is in the safe direction (README section 3.2).
        var tree = new TestTree()
            .File(@"C:\store\pkg\big.bin", 100 << 20, links: 2)
            .File(@"C:\store\pkg\own.bin", 20 << 20)
            .Build();

        var group = Group(Plan(tree, [Rule("test.store", @"**\store")]), "test.store");

        Assert.NotNull(group);
        Assert.Equal(120 << 20, group.Bytes);
        Assert.Equal(100 << 20, group.SharedBytes);
        Assert.Equal(20 << 20, group.Reclaimable);
    }

    [Fact]
    public void A_hard_link_alias_adds_a_name_and_no_bytes()
    {
        var tree = new TestTree()
            .File(@"C:\cache\a\file.bin", 40 << 20, links: 2)
            .File(@"C:\cache\b\file.bin", 40 << 20, NodeFlags.HardlinkAlias, links: 2)
            .Build();

        var group = Group(Plan(tree, [Rule("test.cache", @"**\cache")]), "test.cache");

        Assert.NotNull(group);
        Assert.Equal(40 << 20, group.Bytes);
    }

    [Fact]
    public void The_keep_list_removes_a_match_from_the_report_entirely()
    {
        // "never appear in recommendations" - not "appears, greyed out" (README section 7.4).
        var tree = new TestTree()
            .File(@"C:\work\important\node_modules\a\index.js", 1 << 20)
            .File(@"C:\work\scratch\node_modules\b\index.js", 2 << 20)
            .Build();

        var report = Plan(tree, [Rule("dev.node_modules", @"**\node_modules")],
            keep: [@"C:\work\important\**"]);

        var group = Group(report, "dev.node_modules");

        Assert.NotNull(group);
        Assert.Single(group.Matches);
        Assert.Equal(@"C:\work\scratch\node_modules", group.Matches[0].Path);
        Assert.Equal(1, report.KeptCount);
        Assert.Equal(1 << 20, report.KeptBytes);
    }

    [Fact]
    public void A_reparse_point_is_a_name_and_never_a_recommendation()
    {
        var tree = new TestTree()
            .File(@"C:\work\node_modules\a.js", 1 << 20)
            .Directory(@"C:\link\node_modules")
            .Build();

        // Mark the junction after building: the flag belongs to the directory node.
        var junction = TreeQuery.Find(tree, @"C:\link\node_modules");
        tree.Flags[junction] |= NodeFlags.Reparse;

        var group = Group(Plan(tree, [Rule("dev.node_modules", @"**\node_modules")]), "dev.node_modules");

        Assert.NotNull(group);
        Assert.Single(group.Matches);
        Assert.Equal(@"C:\work\node_modules", group.Matches[0].Path);
    }

    [Fact]
    public void The_tools_own_store_is_never_recommended()
    {
        var tree = new TestTree()
            .File(@"C:\Users\me\AppData\Local\pathmemo\snapshots\obj\0001.pmsnap", 20 << 20)
            .Build();

        var node = TreeQuery.Find(tree, @"C:\Users\me\AppData\Local\pathmemo\snapshots\obj");
        tree.Flags[node] |= NodeFlags.SelfData;

        Assert.Empty(Plan(tree, [Rule("dev.dotnet_artifacts", @"**\obj")]).Groups);
    }

    [Fact]
    public void An_extension_pattern_matches_files_and_not_directories()
    {
        var tree = new TestTree()
            .File(@"C:\p\__pycache__\mod.cpython-312.pyc", 4096)
            .File(@"C:\p\loose.pyc", 2048)
            .Directory(@"C:\p\weird.pyc")
            .Build();

        var group = Group(Plan(tree, [DefaultRules.ById("dev.pycache")!]), "dev.pycache");

        Assert.NotNull(group);

        // The __pycache__ directory and the loose file; the directory called *.pyc is a
        // directory and the rule's own nesting rule covers the file inside the cache.
        Assert.Equal([@"C:\p\__pycache__", @"C:\p\loose.pyc"],
            group.Matches.Select(m => m.Path).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_wildcard_in_the_middle_of_a_segment_still_matches()
    {
        var tree = new TestTree()
            .File(@"C:\Users\me\AppData\Local\Microsoft\Windows\Explorer\thumbcache_1024.db", 32 << 20)
            .File(@"C:\Users\me\AppData\Local\Microsoft\Windows\Explorer\notes.db", 32 << 20)
            .Build();

        var group = Group(Plan(tree, [DefaultRules.ById("sys.thumbnails")!]), "sys.thumbnails");

        Assert.NotNull(group);
        Assert.Single(group.Matches);
        Assert.EndsWith("thumbcache_1024.db", group.Matches[0].Path, StringComparison.Ordinal);
    }

    [Fact]
    public void The_first_rule_that_claims_a_node_wins()
    {
        var tree = new TestTree().File(@"C:\a\Cache\x.bin", 1 << 20).Build();

        var first = Rule("app.first", @"**\Cache");
        var second = Rule("app.second", @"**\Cache");

        Assert.Equal("app.first", Plan(tree, [first, second]).Groups[0].Rule.Id);
        Assert.Equal("app.second", Plan(tree, [second, first]).Groups[0].Rule.Id);
    }

    [Fact]
    public void The_risk_ceiling_decides_which_totals_a_caller_gets()
    {
        var tree = new TestTree()
            .File(@"C:\a\node_modules\x.js", 10 << 20)
            .File(@"C:\downloads\big.iso", 4L << 30)
            .Build();

        var report = Plan(tree,
        [
            Rule("dev.node_modules", @"**\node_modules"),
            DefaultRules.ById("user.large_media")!,
        ]);

        Assert.Equal(10 << 20, report.ReclaimableAtMost(Risk.Safe));
        Assert.Equal((10L << 20) + (4L << 30), report.ReclaimableAtMost(Risk.Caution));

        // large_media is a decision, not a rule: it is in the total and not in the offer.
        Assert.Equal(10 << 20, report.OfferedAtMost(Risk.Caution));
    }

    [Fact]
    public void The_strictest_rule_that_claims_a_path_gives_it_its_risk()
    {
        var engine = new RuleEngine(
        [
            Rule("test.cache", @"**\Cache"),
            Rule("test.sharp", @"**\Cache") with { Risk = Risk.Danger },
        ]);

        var (risk, id) = engine.RiskOf(@"C:\app\Cache", isDirectory: true);

        Assert.Equal(Risk.Danger, risk);
        Assert.Equal("test.sharp", id);

        Assert.Equal(Risk.Safe, engine.RiskOf(@"C:\app\Data", isDirectory: true).Risk);
    }

    [Fact]
    public void A_disabled_rule_leaves_the_set()
    {
        var set = RuleSet.For(new RulesSettings { Disabled = ["dev.node_modules"] });

        Assert.DoesNotContain(set, r => r.Id == "dev.node_modules");
        Assert.Contains(set, r => r.Id == "dev.dotnet_artifacts");
    }

    [Fact]
    public void A_custom_rule_with_a_built_in_id_replaces_it_rather_than_doubling_it()
    {
        var mine = Rule("dev.node_modules", @"D:\only\here\node_modules") with { Custom = true };
        var set = RuleSet.For(new RulesSettings { Custom = [mine] });

        Assert.Single(set, r => r.Id == "dev.node_modules");
        Assert.Equal([@"D:\only\here\node_modules"], set.First(r => r.Id == "dev.node_modules").Patterns);
    }

    [Fact]
    public void A_custom_rule_is_read_out_of_the_configuration()
    {
        var path = Path.Combine(Path.GetTempPath(), "pathmemo-rules-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        File.WriteAllText(path, """
            {
              "rules": {
                "disabled": ["dev.venv"],
                "custom": [
                  { "id": "my.renders", "patterns": ["D:\\renders\\**\\frames"],
                    "risk": "caution", "recoverability": "rebuild",
                    "minSizeBytes": 104857600, "kind": "directory", "olderThan": "7d" }
                ]
              }
            }
            """);

        try
        {
            var config = AppConfig.Load(path);

            Assert.True(config.Loaded);
            Assert.Empty(config.Warnings);
            Assert.Equal(["dev.venv"], config.Rules.Disabled);

            var rule = Assert.Single(config.Rules.Custom);
            Assert.Equal("my.renders", rule.Id);
            Assert.Equal(Risk.Caution, rule.Risk);
            Assert.Equal(100L << 20, rule.MinSizeBytes);
            Assert.Equal(TimeSpan.FromDays(7), rule.OlderThan);
            Assert.True(rule.Custom);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_custom_rule_that_will_not_parse_is_skipped_and_the_rest_still_load()
    {
        var path = Path.Combine(Path.GetTempPath(), "pathmemo-rules-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        File.WriteAllText(path, """
            {
              "rules": {
                "custom": [
                  { "patterns": ["D:\\x"] },
                  { "id": "my.empty" },
                  { "id": "my.good", "patterns": ["D:\\y"], "risk": "nonsense" }
                ]
              }
            }
            """);

        try
        {
            var config = AppConfig.Load(path);

            var rule = Assert.Single(config.Rules.Custom);
            Assert.Equal("my.good", rule.Id);
            Assert.Equal(Risk.Safe, rule.Risk);
            Assert.Equal(3, config.Warnings.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Every_built_in_rule_has_a_command_when_only_a_command_will_do()
    {
        foreach (var rule in DefaultRules.All)
        {
            Assert.NotEmpty(rule.Patterns);
            Assert.NotEqual("", rule.What);
            Assert.True(rule.Id.Contains('.'), $"{rule.Id} should be family.name");

            if (rule.Action == ReclaimAction.Command)
                Assert.False(string.IsNullOrWhiteSpace(rule.Command),
                    $"{rule.Id} says another tool must do it but does not say which");
        }
    }

    [Fact]
    public void No_built_in_rule_claims_anything_on_the_deliberately_excluded_list()
    {
        // README section 7.2, "Deliberately not rules": a rule that matched any of these
        // would be a rule that eventually deletes them.
        string[] forbidden =
        [
            @"C:\hiberfil.sys", @"C:\pagefile.sys", @"C:\swapfile.sys",
            @"C:\Windows\WinSxS", @"C:\Windows\Installer",
            @"C:\System Volume Information", @"C:\work\repo\.git\objects",
        ];

        var engine = new RuleEngine(DefaultRules.All);

        var tree = new TestTree();
        foreach (var path in forbidden) tree.File(path + @"\x.bin", 4 << 20);

        var built = tree.Build();
        var matched = engine.Match(built)
            .Select(m => m.Path)
            .Where(p => forbidden.Any(f => p.StartsWith(f, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Assert.Empty(matched);
    }

    [Fact]
    public void The_index_gives_a_row_its_badge()
    {
        var tree = new TestTree()
            .File(@"C:\work\app\node_modules\a\index.js", 1 << 20)
            .File(@"C:\work\app\src\main.ts", 100)
            .Build();

        var index = ReclaimIndex.Build(tree);
        var node = TreeQuery.Find(tree, @"C:\work\app\node_modules");

        Assert.Equal("safe redownload", index.BadgeFor(node));
        Assert.Equal("", index.BadgeFor(TreeQuery.Find(tree, @"C:\work\app\src")));
    }
}
