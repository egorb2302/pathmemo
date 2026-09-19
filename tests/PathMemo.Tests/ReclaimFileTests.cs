using PathMemo.Analysis;
using PathMemo.Audit;
using PathMemo.Config;
using Xunit;

namespace PathMemo.Tests;

/// <summary>
/// The parts of reclaim that need a real filesystem (README sections 7.3, 7.4, 22.2).
/// </summary>
/// <remarks>
/// Two things cannot be asserted over a synthetic tree: that the report never recommends
/// what the guard would refuse - which takes a real handle to a real protected directory -
/// and that <c>config.json</c> survives being edited by the tool, which takes a real file.
/// </remarks>
public sealed class ReclaimFileTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "pathmemo-reclaim-" + Guid.NewGuid().ToString("N")[..12]);

    private readonly string _dataDirectory = Path.Combine(
        Path.GetTempPath(), "pathmemo-reclaimdb-" + Guid.NewGuid().ToString("N")[..12]);

    public ReclaimFileTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_dataDirectory);
        TestStore.Use(_dataDirectory);
        AppConfig.Reset();
    }

    public void Dispose()
    {
        Delete(_root);
        Delete(_dataDirectory);
        AppConfig.Reset();
    }

    private static void Delete(string directory)
    {
        try { Directory.Delete(directory, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException) { }
    }

    private static ReclaimRule Rule(string id, params string[] patterns) => new()
    {
        Id = id,
        Patterns = patterns,
        Risk = Risk.Safe,
        Recoverability = Recoverability.Rebuild,
        What = "a test rule",
    };

    [Fact]
    public void Verification_marks_what_the_guard_refuses_and_drops_what_is_gone()
    {
        // The rule is deliberately pointed at a name the system directory also has, so the
        // report has one match it may offer, one the guard refuses, and one that is no
        // longer there. All three would look identical in the snapshot.
        var real = Path.Combine(_root, "project", "System32");
        var gone = Path.Combine(_root, "ghost", "System32");
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System,
                                               Environment.SpecialFolderOption.DoNotVerify);

        Directory.CreateDirectory(real);
        File.WriteAllBytes(Path.Combine(real, "pkg.bin"), new byte[8192]);

        var tree = new TestTree()
            .File(Path.Combine(real, "pkg.bin"), 8192)
            .File(Path.Combine(gone, "pkg.bin"), 4096)
            .File(Path.Combine(system, "kernel32.dll"), 4096)
            .Build();

        var report = ReclaimPlanner.Plan(tree, 1, DateTime.UtcNow,
            [Rule("test.system32", @"**\System32")],
            new ReclaimQuery { Keep = [] });

        var group = Assert.Single(report.Groups);

        // The path that is not there any more simply is not a recommendation.
        Assert.DoesNotContain(group.Matches, m => m.Path.Equals(gone, StringComparison.OrdinalIgnoreCase));

        var refused = Assert.Single(group.Matches, m => m.Refusal is not null);
        Assert.Equal(system, refused.Path, ignoreCase: true);
        Assert.False(refused.Offered);

        var offered = Assert.Single(group.Matches, m => m.Offered);
        Assert.Equal(real, offered.Path, ignoreCase: true);

        // And what is handed to rm is the offered one alone.
        Assert.Equal([real], ReclaimPlanner.TargetsOf(report.Groups));
    }

    [Fact]
    public void A_contents_only_rule_targets_the_children_and_leaves_the_directory()
    {
        var temp = Path.Combine(_root, "Temp");
        Directory.CreateDirectory(Path.Combine(temp, "sub"));
        File.WriteAllBytes(Path.Combine(temp, "a.tmp"), new byte[64]);
        File.WriteAllBytes(Path.Combine(temp, "sub", "b.tmp"), new byte[64]);

        var rule = Rule("sys.temp", temp) with { ContentsOnly = true };

        var tree = new TestTree()
            .File(Path.Combine(temp, "a.tmp"), 64)
            .File(Path.Combine(temp, "sub", "b.tmp"), 64)
            .Build();

        var report = ReclaimPlanner.Plan(tree, 1, DateTime.UtcNow, [rule], new ReclaimQuery { Keep = [] });
        var targets = ReclaimPlanner.TargetsOf(report.Groups);

        Assert.Equal(2, targets.Count);
        Assert.DoesNotContain(temp, targets, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(Path.Combine(temp, "a.tmp"), targets, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(Path.Combine(temp, "sub"), targets, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Keeping_a_path_writes_it_into_the_configuration_and_leaves_the_rest_alone()
    {
        File.WriteAllText(AppPaths.ConfigPath, """
            { "scan": { "preferMftScanner": false }, "protect": { "keep": ["D:\\already\\**"] } }
            """);

        var path = Path.Combine(_root, "important", "node_modules");

        Assert.True(ConfigFile.AddKeep(path, out var message));
        Assert.Contains(path, message, StringComparison.Ordinal);

        var config = AppConfig.Load(AppPaths.ConfigPath);
        Assert.Equal([@"D:\already\**", path], config.Protect.Keep);

        // A section this tool knows nothing about is still there afterwards.
        Assert.Contains("preferMftScanner", File.ReadAllText(AppPaths.ConfigPath), StringComparison.Ordinal);

        // Twice is not an error and does not duplicate the entry.
        Assert.True(ConfigFile.AddKeep(path, out var again));
        Assert.Contains("already in the keep list", again, StringComparison.Ordinal);
        Assert.Equal(2, AppConfig.Load(AppPaths.ConfigPath).Protect.Keep.Count);
    }

    [Fact]
    public void Keeping_a_path_works_when_there_is_no_configuration_file_yet()
    {
        Assert.False(File.Exists(AppPaths.ConfigPath));

        var path = Path.Combine(_root, "keep-me");
        Assert.True(ConfigFile.AddKeep(path, out _));

        Assert.Equal([path], AppConfig.Load(AppPaths.ConfigPath).Protect.Keep);
    }

    [Fact]
    public void Disabling_a_rule_takes_it_out_of_the_set_and_enabling_puts_it_back()
    {
        Assert.True(ConfigFile.Disable("dev.node_modules", out _));
        Assert.Contains("dev.node_modules", AppConfig.Load(AppPaths.ConfigPath).Rules.Disabled);
        Assert.DoesNotContain(RuleSet.For(AppConfig.Load(AppPaths.ConfigPath).Rules),
            r => r.Id == "dev.node_modules");

        Assert.True(ConfigFile.Enable("dev.node_modules", out _));
        Assert.Empty(AppConfig.Load(AppPaths.ConfigPath).Rules.Disabled);
        Assert.Contains(RuleSet.For(AppConfig.Load(AppPaths.ConfigPath).Rules),
            r => r.Id == "dev.node_modules");
    }

    [Fact]
    public void A_kept_path_never_reaches_the_report_even_when_the_rule_matches_it()
    {
        var kept = Path.Combine(_root, "important", "node_modules");
        var other = Path.Combine(_root, "scratch", "node_modules");

        Directory.CreateDirectory(kept);
        Directory.CreateDirectory(other);

        Assert.True(ConfigFile.AddKeep(kept, out _));

        var tree = new TestTree()
            .File(Path.Combine(kept, "a.bin"), 4096)
            .File(Path.Combine(other, "b.bin"), 4096)
            .Build();

        // No explicit keep list: the planner reads the one the TUI just wrote.
        var report = ReclaimPlanner.Plan(tree, 1, DateTime.UtcNow,
            [Rule("dev.node_modules", @"**\node_modules")]);

        var group = Assert.Single(report.Groups);
        Assert.Equal(other, Assert.Single(group.Matches).Path, ignoreCase: true);
        Assert.Equal(1, report.KeptCount);
    }
}
