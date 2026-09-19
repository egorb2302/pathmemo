using System.Text.Json;
using PathMemo.Cli.Commands;
using PathMemo.Cli.Output;
using PathMemo.Config;
using PathMemo.Platform;
using PathMemo.Scanning;
using PathMemo.Snapshots;
using Xunit;

namespace PathMemo.Tests;

/// <summary>
/// P10: lazy snapshot sections, the full export, the error list and the config command
/// (README sections 5.2, 13, 20).
/// </summary>
/// <remarks>
/// The section tests are the ones with teeth. Reading a section straight into the node
/// arrays instead of through a byte[] copy is what brought an open snapshot under its
/// memory budget, and it is also the change most able to produce a tree that is subtly
/// wrong - a misplaced array, a short read - which is why the round trip is asserted array
/// by array rather than by a total.
/// </remarks>
public sealed class PolishTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "pathmemo-p10-" + Guid.NewGuid().ToString("N")[..12]);

    public PolishTests()
    {
        Directory.CreateDirectory(_directory);
        AppConfig.Reset();
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        AppConfig.Reset();
    }

    private string Path_(string name) => System.IO.Path.Combine(_directory, name);

    private static ScanResult Sample(IReadOnlyList<ScanError>? errors = null)
    {
        var result = new TestTree()
            .File(@"C:\movies\clip.mp4", 3L << 30)
            .File(@"C:\movies\notes.txt", 700)
            .File(@"C:\src\app\main.cs", 2048, links: 2)
            .File(@"C:\src\app\deep\a\b\c\leaf.bin", 4096)
            .Directory(@"C:\empty")
            .Result(new DateTime(2026, 9, 18, 7, 30, 0, DateTimeKind.Utc), ScannerKind.Walk);

        return errors is null ? result : result with { Errors = errors };
    }

    /// <summary>The same tree as <see cref="Sample"/>, as an opened snapshot.</summary>
    private static SnapshotContents SampleSnapshot() => new TestTree()
        .File(@"C:\movies\clip.mp4", 3L << 30)
        .File(@"C:\movies\notes.txt", 700)
        .File(@"C:\src\app\main.cs", 2048, links: 2)
        .File(@"C:\src\app\deep\a\b\c\leaf.bin", 4096)
        .Directory(@"C:\empty")
        .Snapshot(new DateTime(2026, 9, 18, 7, 30, 0, DateTimeKind.Utc), ScannerKind.Walk);

    // ---- README section 5.2: sections are loaded only when they are wanted -------------

    [Fact]
    public void The_error_list_can_be_read_without_the_tree()
    {
        var path = Path_("errors.pmsnap");
        SnapshotFile.Write(path, Sample([
            new ScanError(@"C:\Windows\PLA", ScanErrorKind.AccessDenied, 5, "denied"),
            new ScanError(@"C:\pagefile.sys", ScanErrorKind.SharingViolation, 32, "in use"),
        ]));

        var contents = SnapshotFile.Read(path, SnapshotParts.Errors);

        Assert.Equal(2, contents.Errors.Count);
        Assert.Equal(SnapshotParts.Errors, contents.Parts);

        // Not "an empty tree by accident": the file has one, and it was not touched.
        Assert.Equal(0, contents.Tree.Count);
        Assert.Empty(contents.Volumes);
        Assert.True(SnapshotFile.Read(path).Tree.Count > 0);
    }

    [Fact]
    public void The_header_alone_still_answers_what_kind_of_scan_it_was()
    {
        var path = Path_("meta.pmsnap");
        SnapshotFile.Write(path, Sample());

        var contents = SnapshotFile.Read(path, SnapshotParts.Meta);

        Assert.Equal(ScannerKind.Walk, contents.Scanner);
        Assert.Equal(new DateTime(2026, 9, 18, 7, 30, 0, DateTimeKind.Utc), contents.StartedUtc);
        Assert.Equal(0, contents.Tree.Count);
        Assert.Empty(contents.Errors);
        Assert.Empty(contents.Volumes);
    }

    [Fact]
    public void A_streamed_read_reproduces_every_array_of_the_tree()
    {
        var path = Path_("tree.pmsnap");
        var original = Sample();
        SnapshotFile.Write(path, original);

        var read = SnapshotFile.Read(path).Tree;
        var expected = original.Tree;

        Assert.Equal(SnapshotParts.All, SnapshotFile.Read(path).Parts);
        Assert.Equal(expected.Count, read.Count);
        Assert.Equal(expected.Parent, read.Parent);
        Assert.Equal(expected.NameOffset, read.NameOffset);
        Assert.Equal(expected.FirstChild, read.FirstChild);
        Assert.Equal(expected.ChildCount, read.ChildCount);
        Assert.Equal(expected.Allocated, read.Allocated);
        Assert.Equal(expected.Logical, read.Logical);
        Assert.Equal(expected.FileCount, read.FileCount);
        Assert.Equal(expected.Mtime, read.Mtime);
        Assert.Equal(expected.Attributes, read.Attributes);
        Assert.Equal(expected.Flags, read.Flags);
        Assert.Equal(expected.LinkCount, read.LinkCount);
        Assert.Equal(expected.VolumeIndex, read.VolumeIndex);
        Assert.Equal(expected.Roots, read.Roots);
        Assert.Equal(expected.NameBlob, read.NameBlob);

        // And the names really are readable through the offsets, which a byte-identical
        // blob does not by itself prove.
        for (var i = 0; i < read.Count; i++) Assert.Equal(expected.Name(i), read.Name(i));
    }

    [Fact]
    public void An_unloaded_tree_is_the_shared_empty_one()
    {
        var path = Path_("empty.pmsnap");
        SnapshotFile.Write(path, Sample());

        Assert.Same(NodeStore.Empty, SnapshotFile.Read(path, SnapshotParts.Volumes).Tree);
        Assert.Equal(0, NodeStore.Empty.Count);
    }

    // ---- README section 13: export ----------------------------------------------------

    [Fact]
    public void Csv_writes_one_row_per_node_and_names_its_columns()
    {
        var path = Path_("all.csv");
        var snapshot = new TestTree()
            .File(@"C:\movies\clip.mp4", 3L << 30)
            .File(@"C:\notes.txt", 700)
            .Snapshot(DateTime.UtcNow);

        var rows = ExportCommand.WriteCsv(path, snapshot, redact: false);
        var lines = File.ReadAllLines(path);

        Assert.Equal(snapshot.Tree.Count, rows);
        Assert.Equal(snapshot.Tree.Count + 1, lines.Length);
        Assert.Equal("kind,path,allocated_bytes,logical_bytes,file_count,modified_utc,flags", lines[0]);
        Assert.Contains(lines, l => l.StartsWith(@"directory,""C:\""", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains(@"C:\movies\clip.mp4", StringComparison.Ordinal)
                                    && l.Contains((3L << 30).ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public void Csv_survives_a_name_with_a_comma_a_quote_and_a_leading_equals()
    {
        var path = Path_("awkward.csv");
        var snapshot = new TestTree()
            .File("C:\\odd\\a,b \"c\".txt", 10)
            .File(@"C:\odd\=cmd.txt", 20)
            .Snapshot(DateTime.UtcNow);

        ExportCommand.WriteCsv(path, snapshot, redact: false);
        var text = File.ReadAllText(path);

        Assert.Contains("\"C:\\odd\\a,b \"\"c\"\".txt\"", text, StringComparison.Ordinal);

        // A path is not a formula, and a spreadsheet must not be able to decide otherwise.
        Assert.Contains("\"C:\\odd\\=cmd.txt\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain(",=", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Json_export_is_one_valid_document_that_counts_what_it_wrote()
    {
        var path = Path_("all.json");
        var snapshot = SampleSnapshot();

        var rows = ExportCommand.WriteJson(path, snapshot, id: 42, redact: false);

        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = document.RootElement;

        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(42, root.GetProperty("scanId").GetInt64());
        Assert.False(root.GetProperty("redacted").GetBoolean());
        Assert.Equal(rows, root.GetProperty("entryCount").GetInt64());

        var entries = root.GetProperty("entries").EnumerateArray().ToList();
        Assert.Equal(snapshot.Tree.Count, entries.Count);

        var clip = entries.Single(e => e.GetProperty("path").GetString() == @"C:\movies\clip.mp4");
        Assert.Equal(3L << 30, clip.GetProperty("allocatedBytes").GetInt64());
        Assert.Equal("file", clip.GetProperty("kind").GetString());

        // A hard-linked name says so, because its 0 in a unique total is otherwise a mystery.
        Assert.Contains(entries, e => e.TryGetProperty("linkCount", out var links) && links.GetInt32() == 2);
    }

    [Fact]
    public void Redaction_keeps_the_shape_the_sizes_and_the_extensions()
    {
        var plain = Path_("plain.csv");
        var hidden = Path_("hidden.csv");
        var snapshot = new TestTree()
            .File(@"C:\secret-project\design.docx", 1024)
            .File(@"C:\secret-project\sub\design.docx", 1024)
            .Directory(@"C:\secret-project\archive.old")
            .Snapshot(DateTime.UtcNow);

        Assert.Equal(
            ExportCommand.WriteCsv(plain, snapshot, redact: false),
            ExportCommand.WriteCsv(hidden, snapshot, redact: true));

        var text = File.ReadAllText(hidden);

        Assert.DoesNotContain("secret-project", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("design", text, StringComparison.OrdinalIgnoreCase);

        // The root is not private, and an export with no roots cannot be read at all.
        Assert.Contains(@"""C:\""", text, StringComparison.Ordinal);

        // Sizes are the point of an export, redacted or not.
        Assert.Contains(",1024,1024,", text, StringComparison.Ordinal);

        // A file keeps its extension; a directory whose name merely contains a dot does not
        // get one invented for it.
        Assert.Contains(".docx", text, StringComparison.Ordinal);
        Assert.DoesNotContain(".old", text, StringComparison.Ordinal);

        // The same name is the same token wherever it appears - that is what keeps a
        // redacted tree a tree (README threat T13).
        var tokens = File.ReadAllLines(hidden)
            .Where(l => l.Contains(".docx", StringComparison.Ordinal))
            .Select(l => l.Split('\\').Last().Trim('"'))
            .Distinct()
            .ToList();
        Assert.Single(tokens);
    }

    [Fact]
    public void Redaction_changes_the_names_and_nothing_else_about_the_row()
    {
        var plain = Path_("p.csv");
        var hidden = Path_("h.csv");
        var snapshot = SampleSnapshot();

        ExportCommand.WriteCsv(plain, snapshot, redact: false);
        ExportCommand.WriteCsv(hidden, snapshot, redact: true);

        var left = File.ReadAllLines(plain);
        var right = File.ReadAllLines(hidden);
        Assert.Equal(left.Length, right.Length);

        for (var i = 1; i < left.Length; i++)
        {
            // Everything after the quoted path is byte-identical.
            Assert.Equal(left[i][(left[i].LastIndexOf('"') + 1)..], right[i][(right[i].LastIndexOf('"') + 1)..]);
            Assert.Equal(left[i].Split(',')[0], right[i].Split(',')[0]);

            // And the depth is the same, so the structure is preserved.
            Assert.Equal(Count(left[i], '\\'), Count(right[i], '\\'));
        }

        static int Count(string text, char c) => text.Count(ch => ch == c);
    }

    // ---- README section 4.9: the error list -------------------------------------------

    [Fact]
    public void An_error_message_that_only_repeats_its_path_is_not_worth_printing()
    {
        var noisy = new ScanError(@"C:\Windows\PLA", ScanErrorKind.AccessDenied, 0,
            @"Access to the path 'C:\Windows\PLA' is denied.");
        Assert.Null(ErrorsCommand.Reason(noisy));

        var useful = new ScanError(@"C:\x", ScanErrorKind.NameInvalid, 123, "the name contains ':'");
        Assert.Equal("Win32 123: the name contains ':'", ErrorsCommand.Reason(useful));

        Assert.Equal("Win32 32", ErrorsCommand.Reason(
            new ScanError(@"C:\y", ScanErrorKind.SharingViolation, 32, "")));
    }

    [Fact]
    public void The_bytes_behind_a_refused_path_come_from_the_tree_or_not_at_all()
    {
        var tree = new TestTree()
            .File(@"C:\locked\big.bin", 5000)
            .Result(DateTime.UtcNow).Tree;

        var known = ErrorsCommand.Known(tree, [
            new ScanError(@"C:\locked", ScanErrorKind.AccessDenied, 5, "denied"),
            new ScanError(@"C:\not-in-this-scan", ScanErrorKind.AccessDenied, 5, "denied"),
        ]);

        Assert.NotNull(known);
        Assert.Equal(1, known!.Value.Found);
        Assert.Equal(5000, known.Value.Bytes);

        // With no tree loaded there is no answer, which is not the same as zero.
        Assert.Null(ErrorsCommand.Known(NodeStore.Empty, []));
    }

    // ---- README section 12: the config command ----------------------------------------

    [Fact]
    public void The_config_template_parses_to_exactly_the_defaults()
    {
        var path = Path_("config.json");
        File.WriteAllText(path, ConfigCommand.Template);

        var config = AppConfig.Load(path);

        Assert.True(config.Loaded);
        Assert.Empty(config.Warnings);

        // Value by value rather than record by record: the settings records hold lists, and
        // a record's generated equality compares those by reference, so comparing the whole
        // record would be a test that can only fail.
        var defaults = new AppConfig();
        Assert.Equal(defaults.Scan, config.Scan);
        Assert.Equal(defaults.Delete, config.Delete);
        Assert.Equal(defaults.Duplicates, config.Duplicates);
        Assert.Equal(defaults.Export, config.Export);
        Assert.Equal(defaults.Protect.Keep, config.Protect.Keep);
        Assert.Equal(defaults.Protect.AllowInsideProtected, config.Protect.AllowInsideProtected);
        Assert.Empty(config.Rules.Disabled);
        Assert.Empty(config.Rules.Custom);
    }

    [Fact]
    public void A_changed_template_value_is_actually_read()
    {
        var path = Path_("changed.json");
        File.WriteAllText(path, ConfigCommand.Template.Replace(
            "\"redactPaths\": false", "\"redactPaths\": true", StringComparison.Ordinal));

        Assert.True(AppConfig.Load(path).Export.RedactPaths);
    }

    /// <summary>
    /// Section 12.1: while elevated, <c>--data-dir</c> is ignored rather than obeyed.
    /// </summary>
    /// <remarks>
    /// This rule had no test, because the suite could only ever observe it by being run as
    /// an administrator - and when it was, the redirect stopped working underneath every
    /// storage test and they wrote into the real store instead of a temporary one. Asserting
    /// it here, with the answer stated rather than inherited, is what makes the rule visible
    /// without needing an elevated test run. The refusal is the whole of threat T7: an
    /// administrator process that writes wherever its command line points is a
    /// privilege-escalation gadget.
    /// </remarks>
    [Fact]
    public void Elevated_the_data_directory_cannot_be_moved_by_a_command_line()
    {
        var before = AppPaths.DataDirectory;
        var elsewhere = Path.Combine(Path.GetTempPath(), "pathmemo-should-not-move-" + Guid.NewGuid().ToString("N")[..8]);

        Elevation.Assume(true);
        try
        {
            AppPaths.Redirect(elsewhere);
            Assert.Equal(before, AppPaths.DataDirectory);
        }
        finally
        {
            // Restored for the rest of the suite, which is only safe because the tests run
            // sequentially - see TestParallelism.cs.
            Elevation.Assume(false);
        }

        // And unelevated the same call is obeyed, or the assertion above would pass for the
        // wrong reason: a Redirect that never works at all.
        Directory.CreateDirectory(elsewhere);
        try
        {
            AppPaths.Redirect(elsewhere);
            Assert.Equal(Path.GetFullPath(elsewhere), Path.GetFullPath(AppPaths.DataDirectory));
        }
        finally
        {
            AppPaths.Redirect(before);
            Directory.Delete(elsewhere, recursive: true);
        }
    }

    // ---- README section 13.1: the global options --------------------------------------

    [Fact]
    public void No_color_is_taken_out_of_the_arguments_before_the_verb_sees_them()
    {
        var kept = Program.TakeGlobalOptions(["top", "--limit", "5", "--no-color"]);

        Assert.Equal(["top", "--limit", "5"], kept);
        Assert.False(Colors.Enabled);
    }
}
