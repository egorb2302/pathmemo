using System.Buffers.Binary;
using System.Text;
using PathMemo.Cli;
using PathMemo.Platform;
using PathMemo.Scanning;
using PathMemo.Snapshots;
using Xunit;

namespace PathMemo.Tests;

/// <summary>
/// The incremental rescan: reading the change journal, and rebuilding a tree from a
/// previous one plus the directories it named (README sections 4.5, 22.1).
/// </summary>
/// <remarks>
/// The record parser is fed bytes and the rebuild is fed a fake disk, for the same reason
/// the MFT parser is: the cases that matter - a truncated buffer, a record version this
/// build has never seen, a subtree that moved in without a record of its own - cannot be
/// produced on a real volume to order, and every one of them is three lines here.
/// </remarks>
public sealed class IncrementalTests
{
    private static readonly DateTime Monday = new(2026, 9, 14, 3, 0, 0, DateTimeKind.Utc);

    // ---- the journal's records -------------------------------------------------------

    [Fact]
    public void A_version_2_record_gives_up_its_parent_and_its_name()
    {
        var buffer = Buffer(nextUsn: 4096, RecordV2(0x2000_0000_0041, 0x2000_0000_0005, 0x100, 0x20, "notes.txt"));

        var changes = new List<UsnChange>();
        var next = UsnRecords.Parse(buffer, changes);

        Assert.Equal(4096, next);
        var change = Assert.Single(changes);
        Assert.Equal(0x2000_0000_0005UL, change.ParentFileId);
        Assert.Equal("notes.txt", change.Name);
        Assert.False(change.IsDirectory);
    }

    [Fact]
    public void A_version_3_record_reads_the_low_half_of_its_128_bit_ids()
    {
        // On NTFS the upper 64 bits are zero, so a V3 record means the same thing as a V2
        // one - but every offset after the ids has moved by sixteen bytes.
        var buffer = Buffer(nextUsn: 9, RecordV3(0x41, 0x1234_5678, 0x8000, 0x10, "node_modules"));

        var changes = new List<UsnChange>();
        UsnRecords.Parse(buffer, changes);

        var change = Assert.Single(changes);
        Assert.Equal(0x1234_5678UL, change.ParentFileId);
        Assert.Equal("node_modules", change.Name);
        Assert.True(change.IsDirectory);
    }

    [Fact]
    public void Every_record_in_one_buffer_is_read()
    {
        var buffer = Buffer(nextUsn: 100,
            RecordV2(1, 10, 0x100, 0, "a"),
            RecordV2(2, 11, 0x200, 0, "bb"),
            RecordV2(3, 12, 0x800, 0, "ccc"));

        var changes = new List<UsnChange>();
        UsnRecords.Parse(buffer, changes);

        Assert.Equal(3, changes.Count);
        Assert.Equal(["a", "bb", "ccc"], changes.Select(c => c.Name));
    }

    [Fact]
    public void A_record_reaching_past_the_returned_bytes_stops_the_walk_and_keeps_the_rest()
    {
        var whole = Buffer(nextUsn: 77, RecordV2(1, 10, 0x100, 0, "kept"), RecordV2(2, 11, 0x100, 0, "cut"));

        // What a short DeviceIoControl return looks like: the last record is half there.
        var truncated = whole.AsSpan(0, whole.Length - 16).ToArray();

        var changes = new List<UsnChange>();
        var next = UsnRecords.Parse(truncated, changes);

        Assert.Equal("kept", Assert.Single(changes).Name);
        Assert.Equal(77, next);
    }

    [Fact]
    public void A_record_version_this_build_does_not_know_is_stepped_over()
    {
        var unknown = RecordV2(1, 10, 0x100, 0, "future");
        BinaryPrimitives.WriteUInt16LittleEndian(unknown.AsSpan(4), 9);

        var buffer = Buffer(nextUsn: 5, unknown, RecordV2(2, 11, 0x100, 0, "known"));

        var changes = new List<UsnChange>();
        UsnRecords.Parse(buffer, changes);

        // Skipped, not fatal, and not the end of the buffer either: RecordLength is
        // trustworthy even when the body is not.
        Assert.Equal("known", Assert.Single(changes).Name);
    }

    [Fact]
    public void A_record_claiming_no_length_stops_the_walk_rather_than_spinning()
    {
        var zero = RecordV2(1, 10, 0x100, 0, "x");
        BinaryPrimitives.WriteInt32LittleEndian(zero.AsSpan(0), 0);

        var changes = new List<UsnChange>();
        var next = UsnRecords.Parse(Buffer(nextUsn: 8, zero), changes);

        Assert.Empty(changes);
        Assert.Equal(8, next);
    }

    [Fact]
    public void A_buffer_holding_only_the_header_is_just_a_new_watermark()
    {
        var changes = new List<UsnChange>();
        var next = UsnRecords.Parse(Buffer(nextUsn: 123456), changes);

        Assert.Empty(changes);
        Assert.Equal(123456, next);
    }

    // ---- whether a snapshot may be the base -----------------------------------------

    [Fact]
    public void Without_a_previous_scan_there_is_nothing_to_rescan_from()
    {
        Assert.False(UsnIncrementalScanner.CanBase(null, Volumes(@"C:\"), out var why));
        Assert.Contains("no previous scan", why, StringComparison.Ordinal);
    }

    [Fact]
    public void A_cancelled_scan_is_never_the_base_of_a_rescan()
    {
        var snapshot = Base(@"C:\a.bin") with { Flags = ScanFlags.Partial };

        Assert.False(UsnIncrementalScanner.CanBase(snapshot, Volumes(@"C:\"), out var why));
        Assert.Contains("cancelled", why, StringComparison.Ordinal);
    }

    [Fact]
    public void A_snapshot_written_before_journal_positions_were_recorded_is_not_a_base()
    {
        // Exactly what every snapshot from before this phase looks like: a complete tree
        // and no watermark, so the next scan is a full one and says so.
        var snapshot = new TestTree().File(@"C:\a.bin", 1 << 20).Snapshot(Monday);

        Assert.False(UsnIncrementalScanner.CanBase(snapshot, Volumes(@"C:\"), out var why));
        Assert.Contains("no journal position", why, StringComparison.Ordinal);
    }

    [Fact]
    public void A_base_that_covered_other_roots_is_refused_by_name()
    {
        var snapshot = Base(@"C:\a.bin");

        Assert.False(UsnIncrementalScanner.CanBase(snapshot, Volumes(@"D:\"), out var why));
        Assert.Contains(@"C:\", why, StringComparison.Ordinal);
    }

    [Fact]
    public void A_complete_snapshot_of_the_same_roots_is_accepted()
    {
        Assert.True(UsnIncrementalScanner.CanBase(Base(@"C:\a.bin"), Volumes(@"C:\"), out _));
    }

    // ---- rebuilding the tree ---------------------------------------------------------

    [Fact]
    public void A_directory_the_journal_did_not_mention_is_never_even_opened()
    {
        var old = new TestTree()
            .File(@"C:\keep\big.bin", 4L << 20)
            .File(@"C:\work\small.bin", 1L << 20)
            .Build();

        var disk = new FakeDisk().Dir(@"C:\", Folder("keep"), Folder("work"));

        var tree = Rebuild(old, disk, @"C:\");

        Assert.DoesNotContain(@"C:\keep", disk.Reads);
        Assert.Equal(5L << 20, tree.Allocated[tree.Roots[0]]);
    }

    [Fact]
    public void A_file_added_to_a_changed_directory_appears_and_lifts_every_total_above_it()
    {
        var old = new TestTree().File(@"C:\work\a.bin", 1L << 20).Build();

        var disk = new FakeDisk()
            .Dir(@"C:\work", File("a.bin", 1L << 20), File("b.bin", 3L << 20));

        var tree = Rebuild(old, disk, @"C:\work");

        Assert.Equal(4L << 20, tree.Allocated[tree.Roots[0]]);
        Assert.Contains("b.bin", Names(tree));
        Assert.Equal(2, tree.FileCount[tree.Roots[0]]);
    }

    [Fact]
    public void A_file_deleted_from_a_changed_directory_takes_its_bytes_with_it()
    {
        var old = new TestTree()
            .File(@"C:\work\a.bin", 1L << 20)
            .File(@"C:\work\gone.bin", 8L << 20)
            .Build();

        var disk = new FakeDisk().Dir(@"C:\work", File("a.bin", 1L << 20));

        var tree = Rebuild(old, disk, @"C:\work");

        Assert.DoesNotContain("gone.bin", Names(tree));
        Assert.Equal(1L << 20, tree.Allocated[tree.Roots[0]]);
    }

    [Fact]
    public void A_file_that_grew_is_re_measured_rather_than_remembered()
    {
        var old = new TestTree().File(@"C:\work\log.txt", 1L << 20).Build();
        var disk = new FakeDisk().Dir(@"C:\work", File("log.txt", 9L << 20));

        var tree = Rebuild(old, disk, @"C:\work");

        Assert.Equal(9L << 20, tree.Allocated[tree.Roots[0]]);
    }

    [Fact]
    public void A_subtree_that_moved_in_is_walked_in_full_although_only_its_parent_changed()
    {
        // The case that makes "read the directories the journal named" insufficient on its
        // own: moving a folder on the same volume is one rename record, and its thousand
        // descendants produce nothing at all. Being absent from the old tree is the signal.
        var old = new TestTree().File(@"C:\work\a.bin", 1L << 20).Build();

        var disk = new FakeDisk()
            .Dir(@"C:\work", File("a.bin", 1L << 20), Folder("moved"))
            .Dir(@"C:\work\moved", Folder("deep"), File("x.bin", 2L << 20))
            .Dir(@"C:\work\moved\deep", File("y.bin", 5L << 20));

        var tree = Rebuild(old, disk, @"C:\work");

        Assert.Equal(8L << 20, tree.Allocated[tree.Roots[0]]);
        Assert.Contains(@"C:\work\moved\deep", disk.Reads);
    }

    [Fact]
    public void A_directory_removed_from_a_changed_parent_takes_its_whole_subtree()
    {
        var old = new TestTree()
            .File(@"C:\work\stay.bin", 1L << 20)
            .File(@"C:\work\build\out\big.bin", 40L << 20)
            .Build();

        var disk = new FakeDisk().Dir(@"C:\work", File("stay.bin", 1L << 20));

        var tree = Rebuild(old, disk, @"C:\work");

        Assert.DoesNotContain("big.bin", Names(tree));
        Assert.Equal(1L << 20, tree.Allocated[tree.Roots[0]]);
    }

    [Fact]
    public void A_changed_directory_that_cannot_be_read_is_unknown_rather_than_empty()
    {
        var old = new TestTree().File(@"C:\work\a.bin", 1L << 20).Build();
        var disk = new FakeDisk().Unreadable(@"C:\work");

        var errors = new List<ScanError>();
        var tree = IncrementalTree.Rebuild(old, Set(@"C:\work"), disk.Reader, errors, default);

        var work = Analysis.TreeQuery.Find(tree, @"C:\work");
        Assert.NotEqual(NodeStore.NoNode, work);
        Assert.True((tree.Flags[work] & NodeFlags.Incomplete) != 0);
        Assert.Equal(ScanErrorKind.AccessDenied, Assert.Single(errors).Kind);
    }

    [Fact]
    public void A_reparse_point_in_a_changed_directory_is_counted_and_not_entered()
    {
        var old = new TestTree().File(@"C:\work\a.bin", 1L << 20).Build();

        var disk = new FakeDisk()
            .Dir(@"C:\work", Folder("link", NodeFlags.Reparse));

        var tree = Rebuild(old, disk, @"C:\work");

        Assert.DoesNotContain(@"C:\work\link", disk.Reads);
        Assert.Equal(0, tree.Allocated[tree.Roots[0]]);
    }

    [Fact]
    public void Every_nodes_children_stay_one_contiguous_range()
    {
        // The invariant the whole snapshot format rests on (README section 5.3): break it
        // and entering a directory stops being a slice.
        var old = new TestTree()
            .File(@"C:\a\one.bin", 1 << 20)
            .File(@"C:\a\b\two.bin", 1 << 20)
            .File(@"C:\c\three.bin", 1 << 20)
            .Build();

        var disk = new FakeDisk()
            .Dir(@"C:\a", File("one.bin", 1 << 20), Folder("b"), Folder("new"))
            .Dir(@"C:\a\new", File("four.bin", 1 << 20));

        var tree = Rebuild(old, disk, @"C:\a");

        for (var node = 0; node < tree.Count; node++)
        {
            if (tree.ChildCount[node] == 0) continue;

            for (var c = tree.FirstChild[node]; c < tree.FirstChild[node] + tree.ChildCount[node]; c++)
            {
                Assert.Equal(node, tree.Parent[c]);
                Assert.True(c > node, "a child must come after its parent");
            }
        }
    }

    [Fact]
    public void The_untouched_part_of_the_tree_keeps_the_name_offsets_it_had()
    {
        // What makes a rescan of 1.6M nodes cheap: the old blob is adopted whole, so a
        // copied node's name costs an int copy rather than a hash lookup.
        var old = new TestTree()
            .File(@"C:\keep\deep\file.bin", 1 << 20)
            .File(@"C:\work\a.bin", 1 << 20)
            .Build();

        var disk = new FakeDisk().Dir(@"C:\work", File("a.bin", 1 << 20));

        var tree = Rebuild(old, disk, @"C:\work");

        var before = Analysis.TreeQuery.Find(old, @"C:\keep\deep\file.bin");
        var after = Analysis.TreeQuery.Find(tree, @"C:\keep\deep\file.bin");

        Assert.Equal(old.NameOffset[before], tree.NameOffset[after]);
        Assert.StartsWith(Encoding.UTF8.GetString(old.NameBlob), Encoding.UTF8.GetString(tree.NameBlob),
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_trailing_separator_does_not_make_a_second_directory()
    {
        // A scan root is stored with its separator and the kernel answers a file id without
        // one, so a file created directly in the scan root depends on this being one key.
        Assert.Equal(IncrementalTree.Key(@"C:\lab"), IncrementalTree.Key(@"C:\lab\"));
        Assert.Equal(@"C:\", IncrementalTree.Key(@"C:\"));
    }

    [Fact]
    public void Too_many_changed_directories_is_a_number_the_rescan_refuses_at()
    {
        // Not a test of behaviour so much as of the decision being stated somewhere a
        // reader can find it: past this many enumerations a full scan is the cheaper answer.
        Assert.True(IncrementalTree.MaxChangedDirectories > 1000);
        Assert.True(IncrementalTree.MaxChangedDirectories < 100_000);
    }

    // ---- the name blob it all rests on ----------------------------------------------

    [Fact]
    public void A_seeded_name_blob_hands_back_the_offsets_it_already_held()
    {
        var first = new NameBlobBuilder(16);
        var windows = first.Intern("Windows");
        var system32 = first.Intern("System32");

        var seeded = new NameBlobBuilder(first.ToBlob());

        Assert.Equal(windows, seeded.Intern("Windows"));
        Assert.Equal(system32, seeded.Intern("System32"));
    }

    [Fact]
    public void A_seeded_name_blob_grows_only_by_what_is_new()
    {
        var first = new NameBlobBuilder(16);
        first.Intern("Windows");
        var blob = first.ToBlob();

        var seeded = new NameBlobBuilder(blob);
        seeded.Intern("Windows");

        Assert.Equal(blob.Length, seeded.Length);

        var added = seeded.Intern("Temp");
        Assert.Equal(blob.Length, added);
        Assert.Equal("Temp", Encoding.UTF8.GetString(seeded.Read(added)));
    }

    // ---- the watermark in the snapshot ----------------------------------------------

    [Fact]
    public void The_journal_position_survives_a_snapshot_round_trip()
    {
        var result = new TestTree().File(@"C:\a.bin", 1 << 20).Result(Monday) with
        {
            Usn = [new UsnState("C:", 0x1234, 0xDEAD_BEEF_CAFE, 0x1315_6BE7_E8)],
        };

        var path = Path.Combine(Path.GetTempPath(), "pathmemo-usn-" + Guid.NewGuid().ToString("N")[..8] + ".pmsnap");

        try
        {
            SnapshotFile.Write(path, result);
            var read = SnapshotFile.Read(path);

            var state = Assert.Single(read.Usn);
            Assert.Equal("C:", state.Letter);
            Assert.Equal(0xDEAD_BEEF_CAFEUL, state.JournalId);
            Assert.Equal(0x1315_6BE7_E8, state.NextUsn);
        }
        finally
        {
            try { System.IO.File.Delete(path); } catch (IOException) { }
        }
    }

    [Fact]
    public void A_scan_that_found_no_journal_writes_a_snapshot_that_simply_has_none()
    {
        var result = new TestTree().File(@"C:\a.bin", 1 << 20).Result(Monday);
        var path = Path.Combine(Path.GetTempPath(), "pathmemo-usn-" + Guid.NewGuid().ToString("N")[..8] + ".pmsnap");

        try
        {
            SnapshotFile.Write(path, result);
            Assert.Empty(SnapshotFile.Read(path).Usn);
        }
        finally
        {
            try { System.IO.File.Delete(path); } catch (IOException) { }
        }
    }

    // ---- what a diff says about one -------------------------------------------------

    [Fact]
    public void A_diff_involving_an_incremental_scan_says_where_its_numbers_came_from()
    {
        // And does not say "the two scanners see different things", which would be wrong:
        // what an incremental scan re-read is as good as a walk's, and what it did not is an
        // earlier scan's verbatim. That is a different caveat (README section 4.5).
        var before = new TestTree().File(@"C:\a.bin", 1 << 20).Snapshot(Monday, ScannerKind.Mft);
        var after = new TestTree().File(@"C:\a.bin", 2 << 20).Snapshot(Monday.AddDays(1),
            ScannerKind.Incremental, ScanFlags.Elevated | ScanFlags.Incremental);

        var diff = Analysis.SnapshotDiff.Compare(7, before, 8, after);
        var warnings = Cli.Commands.DiffCommand.Incomparability(diff, before, after);

        Assert.Contains(warnings, w => w.Contains("scan 8 was built from the change journal", StringComparison.Ordinal));
        Assert.DoesNotContain(warnings, w => w.Contains("scanner and scan", StringComparison.Ordinal));
    }

    // ---- the scheduled scan ---------------------------------------------------------

    [Fact]
    public void The_task_document_carries_the_four_settings_that_make_it_polite()
    {
        var xml = ScheduledScan.BuildXml(ScheduleKind.Weekly, new TimeOnly(3, 0), DayOfWeek.Monday,
            @"C:\Tools\pathmemo.exe", @"PC\me", elevated: false);

        Assert.Contains("<RunOnlyIfIdle>true</RunOnlyIfIdle>", xml, StringComparison.Ordinal);
        Assert.Contains("<StartWhenAvailable>true</StartWhenAvailable>", xml, StringComparison.Ordinal);
        Assert.Contains("<DisallowStartIfOnBatteries>true</DisallowStartIfOnBatteries>", xml, StringComparison.Ordinal);
        Assert.Contains("<Priority>7</Priority>", xml, StringComparison.Ordinal);
        Assert.Contains("<LogonType>InteractiveToken</LogonType>", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void A_weekly_task_names_its_day_and_a_daily_one_has_no_day_to_name()
    {
        var weekly = ScheduledScan.BuildXml(ScheduleKind.Weekly, new TimeOnly(3, 0), DayOfWeek.Sunday,
            "pathmemo.exe", "me", elevated: false);
        var daily = ScheduledScan.BuildXml(ScheduleKind.Daily, new TimeOnly(4, 30), DayOfWeek.Monday,
            "pathmemo.exe", "me", elevated: false);

        Assert.Contains("<Sunday />", weekly, StringComparison.Ordinal);
        Assert.Contains("<DaysInterval>1</DaysInterval>", daily, StringComparison.Ordinal);
        Assert.DoesNotContain("DaysOfWeek", daily, StringComparison.Ordinal);
    }

    [Fact]
    public void What_is_written_is_what_is_read_back()
    {
        // The document is the interface: it is what schtasks is given and what it returns,
        // and its element names are the same in every display language (README section 6.3).
        var xml = ScheduledScan.BuildXml(ScheduleKind.Weekly, new TimeOnly(2, 15), DayOfWeek.Friday,
            @"C:\Tools\pathmemo.exe", @"PC\me", elevated: true);

        var info = ScheduledScan.Parse(xml);

        Assert.NotNull(info);
        Assert.Equal(ScheduleKind.Weekly, info.Kind);
        Assert.Equal(new TimeOnly(2, 15), info.Time);
        Assert.Equal(DayOfWeek.Friday, info.Day);
        Assert.True(info.Elevated);
        Assert.Equal(@"C:\Tools\pathmemo.exe", info.Command);
        Assert.Equal("scan --all-volumes --quiet", info.Arguments);
        Assert.Contains("every Friday at 02:15", info.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_daily_task_reads_back_as_a_daily_one()
    {
        var info = ScheduledScan.Parse(ScheduledScan.BuildXml(
            ScheduleKind.Daily, new TimeOnly(23, 45), DayOfWeek.Monday, "pathmemo.exe", "me", false));

        Assert.NotNull(info);
        Assert.Equal(ScheduleKind.Daily, info.Kind);
        Assert.Equal("every day at 23:45", info.Describe());
        Assert.False(info.Elevated);
    }

    [Fact]
    public void An_ampersand_in_the_install_path_does_not_break_the_document()
    {
        var xml = ScheduledScan.BuildXml(ScheduleKind.Daily, new TimeOnly(3, 0), DayOfWeek.Monday,
            @"C:\Program Files\R & D\pathmemo.exe", @"PC\me", elevated: false);

        var info = ScheduledScan.Parse(xml);

        Assert.NotNull(info);
        Assert.Equal(@"C:\Program Files\R & D\pathmemo.exe", info.Command);
    }

    [Fact]
    public void Something_that_is_not_a_task_document_is_no_schedule_rather_than_a_crash()
    {
        Assert.Null(ScheduledScan.Parse("ERROR: The system cannot find the file specified."));
        Assert.Null(ScheduledScan.Parse("<Task><Triggers /></Task>"));
    }

    // ---- the two new option values --------------------------------------------------

    [Fact]
    public void A_time_of_day_is_read_on_a_24_hour_clock_whatever_the_locale()
    {
        Assert.Equal(new TimeOnly(3, 0), ArgParse.Time("03:00"));
        Assert.Equal(new TimeOnly(3, 0), ArgParse.Time("3:00"));
        Assert.Equal(new TimeOnly(23, 30), ArgParse.Time("23:30"));
        Assert.Throws<ArgumentException>(() => ArgParse.Time("3pm"));
        Assert.Throws<ArgumentException>(() => ArgParse.Time("25:00"));
    }

    [Fact]
    public void A_day_name_is_accepted_in_any_case_and_refused_when_it_is_not_one()
    {
        Assert.Equal(DayOfWeek.Monday, ArgParse.Day("monday"));
        Assert.Equal(DayOfWeek.Sunday, ArgParse.Day("SUNDAY"));
        Assert.Throws<ArgumentException>(() => ArgParse.Day("payday"));
    }

    // ---- helpers ---------------------------------------------------------------------

    private static NodeStore Rebuild(NodeStore old, FakeDisk disk, params string[] changed) =>
        IncrementalTree.Rebuild(old, Set(changed), disk.Reader, new List<ScanError>(), default);

    private static IReadOnlySet<string> Set(params string[] paths) =>
        paths.ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<string> Names(NodeStore tree) =>
        Enumerable.Range(0, tree.Count).Select(tree.Name);

    private static SnapshotContents Base(string file) =>
        new TestTree().File(file, 1 << 20).Snapshot(Monday) with
        {
            Usn = [new UsnState(file[..2], 0x1234, 7, 1000)],
        };

    private static IReadOnlyList<VolumeInfo> Volumes(params string[] roots) =>
        [.. roots.Select(r => new VolumeInfo(r, r[..2], null, "NTFS", 0x1234, null,
            4096, 500UL << 30, 100UL << 30, DriveType.Fixed))];

    private static LiveEntry File(string name, long bytes) =>
        new(name, bytes, bytes, 0, 0x20, NodeFlags.None, 1);

    private static LiveEntry Folder(string name, NodeFlags extra = NodeFlags.None) =>
        new(name, 0, 0, 0, 0x10, NodeFlags.Directory | extra, 1);

    /// <summary>A READ_USN_JOURNAL output buffer: the next USN, then the records.</summary>
    private static byte[] Buffer(long nextUsn, params byte[][] records)
    {
        var bytes = new byte[8 + records.Sum(r => r.Length)];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, nextUsn);

        var at = 8;
        foreach (var record in records)
        {
            record.CopyTo(bytes, at);
            at += record.Length;
        }
        return bytes;
    }

    private static byte[] RecordV2(ulong fileId, ulong parentId, uint reason, uint attributes, string name)
    {
        var encoded = Encoding.Unicode.GetBytes(name);
        var record = new byte[Align(60 + encoded.Length)];

        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(0), record.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(8), fileId);
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(16), parentId);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(40), reason);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(52), attributes);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(56), (ushort)encoded.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(58), 60);
        encoded.CopyTo(record, 60);

        return record;
    }

    private static byte[] RecordV3(ulong fileId, ulong parentId, uint reason, uint attributes, string name)
    {
        var encoded = Encoding.Unicode.GetBytes(name);
        var record = new byte[Align(76 + encoded.Length)];

        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(0), record.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4), 3);
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(8), fileId);
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(24), parentId);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(56), reason);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(68), attributes);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(72), (ushort)encoded.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(74), 76);
        encoded.CopyTo(record, 76);

        return record;
    }

    /// <summary>Records sit at eight-byte boundaries in the journal's buffer.</summary>
    private static int Align(int length) => (length + 7) & ~7;

    /// <summary>
    /// A disk that answers only the directories a test spelled out, and remembers which
    /// ones were asked for - which is how "a directory nobody touched is never opened"
    /// becomes something a test can check rather than something a comment claims.
    /// </summary>
    private sealed class FakeDisk
    {
        private readonly Dictionary<string, List<LiveEntry>?> _directories = new(StringComparer.OrdinalIgnoreCase);

        internal List<string> Reads { get; } = [];

        internal FakeDisk Dir(string path, params LiveEntry[] entries)
        {
            _directories[path] = [.. entries];
            return this;
        }

        internal FakeDisk Unreadable(string path)
        {
            _directories[path] = null;
            return this;
        }

        internal LiveDirectoryReader Reader => (string path, List<LiveEntry> into, out ScanError? error) =>
        {
            Reads.Add(path);
            into.Clear();

            if (!_directories.TryGetValue(path, out var entries))
            {
                error = new ScanError(path, ScanErrorKind.NotFound, 2, "not in this fake disk");
                return false;
            }

            if (entries is null)
            {
                error = new ScanError(path, ScanErrorKind.AccessDenied, 5, "denied");
                return false;
            }

            error = null;
            into.AddRange(entries);
            return true;
        };
    }
}
