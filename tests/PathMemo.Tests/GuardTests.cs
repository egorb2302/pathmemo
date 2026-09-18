using PathMemo.Config;
using PathMemo.Deletion;
using Xunit;

namespace PathMemo.Tests;

/// <summary>
/// The parts of the deletion guard that are pure functions: the protected set over
/// canonical strings, the glob syntax, the confirmation token and the configuration
/// reader (README sections 9.3, 12, 13.4, 22.1).
/// </summary>
/// <remarks>
/// These need no filesystem because canonicalisation is the step <b>before</b> them. What
/// the twelve bypasses of README section 9.3 have in common is that they all canonicalise
/// to the same string; that they do is asserted against the real filesystem in
/// <see cref="DeletionTests"/>, and what happens to that string is asserted here.
/// </remarks>
public sealed class GuardTests
{
    private const string Volume = @"\\?\Volume{11112222-3333-4444-5555-666677778888}\";

    private static ProtectedSet Set() => new(
        folders:
        [
            (Volume + "Windows", "the Windows directory", true),
            (Volume + @"Windows\System32", "the system directory", true),
            (Volume + "Program Files", "Program Files", true),
            (Volume + "Users", "the user profiles directory", false),
            (Volume + @"Users\me", "the user profile", false),
        ],
        keep: [@"D:\archive\**", "*.kdbx"],
        allowInside: [@"C:\Windows\Temp\**", @"C:\Windows\Logs\**\*.log"],
        dataDirectory: Volume + @"Users\me\AppData\Local\pathmemo",
        quarantineRoots: [Volume + @"Users\me\AppData\Local\pathmemo\quarantine", Volume + "pathmemo-quarantine"]);

    [Theory]
    [InlineData("Windows", "the Windows directory")]
    [InlineData(@"Windows\System32", "the system directory")]
    [InlineData(@"Windows\System32\drivers\etc\hosts", "the system directory")]
    [InlineData("Users", "the user profiles directory")]
    [InlineData(@"Users\me", "the user profile")]
    [InlineData("Program Files", "Program Files")]
    public void Refuses_protected_folders_and_everything_under_them(string tail, string reason)
    {
        var verdict = Set().Check(Volume + tail, @"C:\" + tail);

        Assert.True(verdict.IsProtected);
        Assert.Contains(reason, verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Protects_the_profile_directories_without_sealing_what_is_inside_them()
    {
        var set = Set();

        // The structure is protected...
        Assert.True(set.Check(Volume + @"Users\me", @"C:\Users\me").IsProtected);
        Assert.True(set.Check(Volume + "Users", @"C:\Users").IsProtected);

        // ...and the 44 GB disk image inside it is not. A cleaner that refuses everything
        // under %LOCALAPPDATA% cannot do the one job it exists for (README section 9.3).
        Assert.False(set.Check(Volume + @"Users\me\AppData\Local\Docker\wsl\ext4.vhdx",
                               @"C:\Users\me\AppData\Local\Docker\wsl\ext4.vhdx").IsProtected);
        Assert.False(set.Check(Volume + @"Users\me\Downloads\win11.iso",
                               @"C:\Users\me\Downloads\win11.iso").IsProtected);

        // Sealed folders stay sealed all the way down.
        Assert.True(set.Check(Volume + @"Windows\assembly\x", @"C:\Windows\assembly\x").IsProtected);
    }

    [Fact]
    public void Refuses_a_volume_root_and_anything_directly_under_it()
    {
        Assert.True(Set().Check(Volume, @"C:\").IsProtected);

        // Not on any list, and still refused: the blast radius of "delete D:\something"
        // is a whole top-level tree, and its contents remain deletable.
        var verdict = Set().Check(Volume + "games", @"D:\games");
        Assert.True(verdict.IsProtected);
        Assert.Contains("top-level", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Refuses_a_directory_that_contains_a_protected_one()
    {
        // Deleting this would take Windows\System32 with it.
        var verdict = new ProtectedSet([(Volume + @"a\b\Windows", "the Windows directory", true)])
            .Check(Volume + @"a\b", @"C:\a\b");

        Assert.True(verdict.IsProtected);
        Assert.Contains("contains", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Allows_the_whitelist_inside_the_blacklist()
    {
        var set = Set();

        Assert.False(set.Check(Volume + @"Windows\Temp\big.tmp", @"C:\Windows\Temp\big.tmp").IsProtected);
        Assert.False(set.Check(Volume + @"Windows\Logs\CBS\cbs.log", @"C:\Windows\Logs\CBS\cbs.log").IsProtected);

        // The door is exactly as wide as the rule: Logs allows *.log, not everything.
        Assert.True(set.Check(Volume + @"Windows\Logs\CBS\cbs.cab", @"C:\Windows\Logs\CBS\cbs.cab").IsProtected);

        // And never the directory the rule is anchored on.
        Assert.True(set.Check(Volume + @"Windows\Temp", @"C:\Windows\Temp").IsProtected);
    }

    [Fact]
    public void Refuses_system_directories_named_by_the_filesystem()
    {
        var set = Set();

        Assert.True(set.Check(Volume + @"System Volume Information\x", @"D:\System Volume Information\x").IsProtected);

        var bin = set.Check(Volume + @"$Recycle.Bin\S-1-5-21\$R1", @"D:\$Recycle.Bin\S-1-5-21\$R1");
        Assert.True(bin.IsProtected);

        // Emptying the bin is the one operation that may go in there.
        Assert.False(set.Check(Volume + @"$Recycle.Bin\S-1-5-21\$R1", @"D:\$Recycle.Bin\S-1-5-21\$R1",
            GuardOperation.EmptyRecycleBin).IsProtected);
    }

    [Fact]
    public void Refuses_its_own_store_except_when_purging_the_quarantine()
    {
        var set = Set();
        const string database = @"Users\me\AppData\Local\pathmemo\pathmemo.db";
        const string quarantined = @"Users\me\AppData\Local\pathmemo\quarantine\op-000007";

        Assert.True(set.Check(Volume + database, @"C:\" + database).IsProtected);
        Assert.True(set.Check(Volume + quarantined, @"C:\" + quarantined).IsProtected);

        // Purge may empty a quarantine, and still not the database beside it.
        Assert.False(set.Check(Volume + quarantined, @"C:\" + quarantined, GuardOperation.Purge).IsProtected);
        Assert.True(set.Check(Volume + database, @"C:\" + database, GuardOperation.Purge).IsProtected);
    }

    [Fact]
    public void Honours_the_keep_list()
    {
        var set = Set();

        var verdict = set.Check(Volume + @"archive\2019\books", @"D:\archive\2019\books");
        Assert.True(verdict.IsProtected);
        Assert.Contains("keep rule", verdict.Reason, StringComparison.Ordinal);

        Assert.True(set.Check(Volume + @"work\secrets.kdbx", @"D:\work\secrets.kdbx").IsProtected);
        Assert.False(set.Check(Volume + @"work\secrets.txt", @"D:\work\secrets.txt").IsProtected);
    }

    [Theory]
    [InlineData(@"C:\a\b\c.txt", @"C:\a\**", true)]
    [InlineData(@"C:\a\b\c.txt", @"C:\a\*", false)]
    [InlineData(@"C:\a\b\c.txt", @"C:\a\*\c.txt", true)]
    [InlineData(@"C:\a\b\c.txt", @"**\*.txt", true)]
    [InlineData(@"C:\a\b\c.txt", @"**\node_modules\**", false)]
    [InlineData(@"C:\a\node_modules\x\y", @"**\node_modules\**", true)]
    [InlineData(@"C:\a\node_modules", @"**\node_modules", true)]
    [InlineData(@"C:\A\B", @"c:\a\b", true)]
    public void Matches_globs_by_segment(string path, string pattern, bool expected) =>
        Assert.Equal(expected, PathGlob.Parse(pattern).Matches(path));

    [Fact]
    public void Expands_variables_through_the_known_folder_api()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        Assert.StartsWith(windows, PathGlob.Expand(@"%WINDIR%\Temp\**"), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain('%', PathGlob.Expand(@"%LOCALAPPDATA%\x"));
    }

    private static PlanItem Item(string path, long bytes, DateTime? written = null) => new()
    {
        RequestedPath = path,
        DisplayPath = path,
        CanonicalPath = Volume + path[3..],
        VolumeRoot = path[..3],
        IsDirectory = false,
        IsReparsePoint = false,
        Bytes = bytes,
        LastWriteUtc = written ?? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    private static DeletePlan PlanOf(params PlanItem[] items) => new()
    {
        Items = items,
        Refusals = [],
        Mode = DeleteMode.Permanent,
    };

    [Fact]
    public void The_confirmation_token_describes_one_exact_list()
    {
        var plan = PlanOf(Item(@"D:\a.iso", 100), Item(@"D:\b.iso", 200));

        // Same list, different order: the same question, so the same answer.
        Assert.Equal(plan.Token, PlanOf(Item(@"D:\b.iso", 200), Item(@"D:\a.iso", 100)).Token);

        // Anything else is a different question.
        Assert.NotEqual(plan.Token, PlanOf(Item(@"D:\a.iso", 101), Item(@"D:\b.iso", 200)).Token);
        Assert.NotEqual(plan.Token, PlanOf(Item(@"D:\a.iso", 100)).Token);
        Assert.NotEqual(plan.Token, (plan with { Mode = DeleteMode.Quarantine }).Token);
        Assert.NotEqual(plan.Token, PlanOf(
            Item(@"D:\a.iso", 100, new DateTime(2026, 2, 2, 0, 0, 0, DateTimeKind.Utc)),
            Item(@"D:\b.iso", 200)).Token);

        Assert.Equal(10, plan.Token.Length);
    }

    [Fact]
    public void Reads_the_delete_settings_from_config_json()
    {
        var path = Path.Combine(Path.GetTempPath(), "pathmemo-config-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        File.WriteAllText(path, """
            {
              "protect": { "keep": ["D:\\keepme\\**"] },
              "delete": { "defaultMode": "permanent", "recycleMaxBytes": "250MB",
                          "quarantineRetentionDays": 3, "verifyBeforeDelete": false }
            }
            """);

        try
        {
            var config = AppConfig.Load(path);

            Assert.True(config.Loaded);
            Assert.Empty(config.Warnings);
            Assert.Equal(DeleteMode.Permanent, config.Delete.DefaultMode);
            Assert.Equal(250L << 20, config.Delete.RecycleMaxBytes);
            Assert.Equal(3, config.Delete.QuarantineRetentionDays);
            Assert.False(config.Delete.VerifyBeforeDelete);
            Assert.Equal([@"D:\keepme\**"], config.Protect.Keep);

            // Untouched keys keep their defaults rather than resetting to zero.
            Assert.Equal(100, config.Delete.RecycleMaxItems);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_broken_config_warns_and_uses_defaults()
    {
        var path = Path.Combine(Path.GetTempPath(), "pathmemo-config-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        File.WriteAllText(path, "{ \"delete\": { \"defaultMode\": ");

        try
        {
            var config = AppConfig.Load(path);

            Assert.False(config.Loaded);
            Assert.Single(config.Warnings);
            Assert.Equal(DeleteMode.Quarantine, config.Delete.DefaultMode);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_nonsense_value_warns_about_that_key_only()
    {
        var path = Path.Combine(Path.GetTempPath(), "pathmemo-config-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        File.WriteAllText(path, """
            { "delete": { "defaultMode": "shred", "recycleMaxBytes": "lots", "autoPurgeExpired": false } }
            """);

        try
        {
            var config = AppConfig.Load(path);

            Assert.True(config.Loaded);
            Assert.Equal(2, config.Warnings.Count);
            Assert.Equal(DeleteMode.Quarantine, config.Delete.DefaultMode);
            Assert.Equal(500L << 20, config.Delete.RecycleMaxBytes);
            Assert.False(config.Delete.AutoPurgeExpired);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
