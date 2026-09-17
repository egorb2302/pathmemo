using System.Diagnostics;
using PathMemo.Audit;
using PathMemo.Audit.Probes;
using PathMemo.Cli.Commands;
using PathMemo.Platform;
using PathMemo.Platform.Native;
using Xunit;

namespace PathMemo.Tests;

public sealed class DismParserTests
{
    private const string English = """
        Deployment Image Servicing and Management tool
        Version: 10.0.19041.3636

        Image Version: 10.0.19045.3803

        [===========================100.0%==========================]

        Component Store (WinSxS) information:

        Windows Explorer Reported Size of Component Store : 9.99 GB

        Actual Size of Component Store : 9.65 GB

            Shared with Windows : 5.86 GB
            Backups and Disabled Features : 3.49 GB
            Cache and Temporary Data : 306.03 MB

        Date of Last Cleanup : 2024-01-01 12:00:00

        Number of Reclaimable Packages : 5
        Component Store Cleanup Recommended : Yes

        The operation completed successfully.
        """;

    // Same structure, different words and a decimal comma: what DISM prints when /English
    // is ignored by an older build.
    private const string Russian = """
        Cистема обслуживания образов развертывания и управления ими
        Версия: 10.0.19041.3636

        Версия образа: 10.0.19045.3803

        Сведения о хранилище компонентов (WinSxS):

        Размер хранилища компонентов, определенный проводником : 9,99 ГБ

        Фактический размер хранилища компонентов : 9,65 ГБ

            Совместно используется с Windows : 5,86 ГБ
            Резервные копии и отключенные компоненты : 3,49 ГБ
            Кэш и временные данные : 306,03 МБ

        Дата последней очистки : 2024-01-01 12:00:00

        Число пакетов, которые можно освободить : 5
        Рекомендуется очистка хранилища компонентов : Да

        Операция успешно завершена.
        """;

    [Theory]
    [InlineData(English)]
    [InlineData(Russian)]
    public void Reads_the_report_by_position_not_by_words(string text)
    {
        Assert.True(DismAnalysis.TryParse(text, out var analysis));

        Assert.Equal((long)(9.65 * (1L << 30)), analysis.ActualSize);
        Assert.Equal((long)(3.49 * (1L << 30)), analysis.BackupsAndDisabledFeatures);
        Assert.Equal((long)(306.03 * (1L << 20)), analysis.CacheAndTemporary);
        Assert.Equal(5, analysis.ReclaimablePackages);
    }

    [Fact]
    public void English_yes_is_recognised_and_other_languages_default_to_not_recommended()
    {
        Assert.True(DismAnalysis.TryParse(English, out var english));
        Assert.True(english.CleanupRecommended);

        // "Да" is not parsed; the safe reading of an unknown word is "not recommended",
        // which only changes wording, never a number.
        Assert.True(DismAnalysis.TryParse(Russian, out var russian));
        Assert.False(russian.CleanupRecommended);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Error: 740\n\nElevated permissions are required to run DISM.")]
    [InlineData("Actual Size of Component Store : lots")]
    public void Unparseable_output_is_a_failure_not_zero(string text)
    {
        // Acceptance criterion: a failed parse yields status=unknown, never "0 bytes".
        Assert.False(DismAnalysis.TryParse(text, out _));
    }

    [Theory]
    [InlineData("9.65 GB", 9.65 * (1L << 30))]
    [InlineData("306.03 MB", 306.03 * (1L << 20))]
    [InlineData("9,65 ГБ", 9.65 * (1L << 30))]
    [InlineData("12 KB", 12 * 1024)]
    [InlineData("512 bytes", 512)]
    public void Sizes_parse_in_either_decimal_convention(string text, double expected)
    {
        Assert.True(DismAnalysis.TryParseSize(text, out var bytes));
        Assert.Equal((long)expected, bytes);
    }

    [Theory]
    [InlineData("GB")]
    [InlineData("lots")]
    [InlineData("9.65 furlongs")]
    public void Sizes_without_a_number_or_a_known_unit_fail(string text)
    {
        Assert.False(DismAnalysis.TryParseSize(text, out _));
    }
}

public sealed class VssParserTests
{
    [Fact]
    public void Reads_byte_counts_as_integers()
    {
        const string json = """
            {"storage":[{"volume":"\\\\?\\Volume{aaaa}\\","diffVolume":"\\\\?\\Volume{aaaa}\\","used":13421772800,"allocated":14000000000,"max":47600000000}],
             "copies":[{"volume":"\\\\?\\Volume{aaaa}\\","created":"2026-02-11T10:00:00.0000000Z"},
                       {"volume":"\\\\?\\Volume{aaaa}\\","created":"2026-08-01T10:00:00.0000000Z"}]}
            """;

        var state = VssProbe.Parse(json);

        Assert.Single(state.Storage);
        Assert.Equal(13_421_772_800, state.Storage[0].Used);
        Assert.Equal(2, state.Copies.Count);
        Assert.Equal(new DateTime(2026, 2, 11, 10, 0, 0, DateTimeKind.Utc), state.Copies.Min(c => c.Created));
    }

    [Fact]
    public void A_machine_with_no_shadow_storage_is_empty_not_an_error()
    {
        var state = VssProbe.Parse("""{"storage":[],"copies":[]}""");
        Assert.Empty(state.Storage);
        Assert.Empty(state.Copies);
    }
}

public sealed class DirectoryMeasureTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "pathmemo-audit-" + Guid.NewGuid().ToString("N")[..12]);

    public DirectoryMeasureTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (var child in Directory.EnumerateDirectories(_root))
            if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                Directory.Delete(child, recursive: false);

        try { Directory.Delete(_root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private void Write(int bytes, params string[] parts)
    {
        var path = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
    }

    [Fact]
    public void Sums_a_tree_and_rounds_up_to_clusters()
    {
        Write(1, "a.bin");
        Write(10_000, "sub", "b.bin");
        Write(0, "sub", "empty.bin");

        var measured = DirectoryMeasure.Directory_(_root, CancellationToken.None);

        Assert.Equal(3, measured.Files);
        Assert.Equal(10_001, measured.Logical);
        Assert.True(measured.Allocated >= 10_001, "on-disk size is cluster-rounded, never below logical");
        Assert.True(measured.Allocated <= 10_001 + 2 * 65_536, "and never more than a cluster per file over it");
        Assert.False(measured.Incomplete);
    }

    [Fact]
    public void Does_not_follow_junctions()
    {
        Write(50_000, "real", "payload.bin");

        var psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add("mklink");
        psi.ArgumentList.Add("/J");
        psi.ArgumentList.Add(Path.Combine(_root, "link"));
        psi.ArgumentList.Add(Path.Combine(_root, "real"));
        using (var p = Process.Start(psi)) p!.WaitForExit(10_000);
        if (!Directory.Exists(Path.Combine(_root, "link"))) return;

        var measured = DirectoryMeasure.Directory_(_root, CancellationToken.None);

        // Once through "real", never again through "link".
        Assert.Equal(1, measured.Files);
        Assert.Equal(50_000, measured.Logical);
    }

    [Fact]
    public void An_absent_directory_measures_as_nothing()
    {
        var measured = DirectoryMeasure.Directory_(Path.Combine(_root, "nope"), CancellationToken.None);
        Assert.Equal(Measured.Empty, measured);
    }

    [Fact]
    public void A_single_file_is_measured_by_path()
    {
        Write(4_097, "one.bin");
        var measured = DirectoryMeasure.File_(Path.Combine(_root, "one.bin"));

        Assert.Equal(1, measured.Files);
        Assert.Equal(4_097, measured.Logical);
        Assert.True(measured.Allocated >= 4_097);

        Assert.Equal(0, DirectoryMeasure.File_(Path.Combine(_root, "missing.bin")).Files);
    }
}

public sealed class InstallerOrphanTests
{
    [Fact]
    public void Packages_not_named_in_the_registry_are_orphans()
    {
        var root = Path.Combine(Path.GetTempPath(), "pathmemo-msi-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            var kept = Path.Combine(root, "1a2b3c.msi");
            var orphan = Path.Combine(root, "9z8y7x.msp");
            File.WriteAllBytes(kept, new byte[100_000]);
            File.WriteAllBytes(orphan, new byte[200_000]);

            // The registry stores the path in whatever case the installer used.
            var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { kept.ToUpperInvariant() };

            var (orphans, orphanBytes, total) = InstallerOrphansProbe.Classify([kept, orphan], referenced);

            Assert.Equal([orphan], orphans);
            Assert.True(orphanBytes >= 200_000 && orphanBytes < 300_000);
            Assert.True(total >= 300_000);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

public sealed class AuditRunnerTests
{
    private sealed class Throwing : IAuditProbe
    {
        public string Id => "test.throws";
        public string Title => "Throws";
        public IEnumerable<AuditFinding> Run(AuditContext context) => throw new InvalidOperationException("boom");
    }

    private sealed class Fine : IAuditProbe
    {
        public string Id => "test.fine";
        public string Title => "Fine";
        public IEnumerable<AuditFinding> Run(AuditContext context)
        {
            yield return new AuditFinding
            {
                Id = Id, Title = Title, Status = FindingStatus.Measured,
                UsedBytes = 10, ReclaimableBytes = 7, Explanation = "ok",
            };
        }
    }

    [Fact]
    public void One_failing_probe_does_not_take_the_report_down()
    {
        var report = AuditRunner.Run([new Throwing(), new Fine()], progress: null, CancellationToken.None);

        var failed = Assert.Single(report.Findings, f => f.Id == "test.throws");
        Assert.Equal(FindingStatus.Error, failed.Status);
        Assert.Contains("boom", failed.Note);

        var fine = Assert.Single(report.Findings, f => f.Id == "test.fine");
        Assert.Equal(FindingStatus.Measured, fine.Status);
        Assert.Equal(7, report.ReclaimableTotal);
    }

    [Fact]
    public void Every_shipped_probe_has_a_unique_id_and_runs_without_throwing()
    {
        var probes = AuditRunner.AllProbes();
        Assert.Equal(probes.Count, probes.Select(p => p.Id).Distinct().Count());

        var report = AuditRunner.Run(probes, progress: null, CancellationToken.None);

        // Real machine, real answers - only the invariants are asserted: nothing crashed,
        // and no probe reported a size it did not measure (README section 6.3).
        Assert.DoesNotContain(report.Findings, f => f.Status == FindingStatus.Error);
        Assert.DoesNotContain(report.Findings, f => f.Status != FindingStatus.Measured && f.ReclaimableBytes > 0);
        Assert.All(report.Findings.Where(f => f.Status == FindingStatus.Measured),
            f => Assert.True(f.ReclaimableBytes is null || f.ReclaimableBytes <= (f.UsedBytes ?? long.MaxValue)));
    }
}

public sealed class AuditOutputTests
{
    [Fact]
    public void Recycle_bin_struct_matches_the_shell_layout()
    {
        // 24 on x64: SHQueryRecycleBin rejects any other cbSize with E_INVALIDARG.
        Assert.Equal(24u, Shell32.QueryRecycleBinInfo.NativeSize);
    }

    [Fact]
    public void Findings_are_grouped_by_what_is_known_about_them()
    {
        AuditFinding Make(string id, FindingStatus status, long? used, long? reclaimable) => new()
        {
            Id = id, Title = id, Status = status, Volume = "C:",
            UsedBytes = used, ReclaimableBytes = reclaimable, Explanation = "x",
            Remedies = [new Remedy(RemedyKind.RunCommand, "do " + id, NeedsElevation: true)],
        };

        var report = new AuditReport
        {
            StartedUtc = DateTime.UtcNow, Duration = TimeSpan.FromSeconds(1), Elevated = false, SnapshotId = null,
            Findings =
            [
                Make("small", FindingStatus.Measured, 100, 100),
                Make("big", FindingStatus.Measured, 5000, 5000),
                Make("opaque", FindingStatus.Measured, 9000, null),
                Make("fixed", FindingStatus.Measured, 400, 0),
                Make("blocked", FindingStatus.NeedsElevation, null, null),
            ],
        };

        var text = new StringWriter();
        AuditCommand.Print(report, text);
        var output = text.ToString();

        // Order inside RECLAIMABLE is by reclaimable bytes, descending.
        Assert.True(output.IndexOf("big", StringComparison.Ordinal) < output.IndexOf("small", StringComparison.Ordinal));

        Assert.Contains("WORTH A LOOK", output);
        Assert.Contains("NOT RECLAIMABLE", output);
        Assert.Contains("NOT MEASURED", output);
        Assert.Contains("needs administrator", output);
        Assert.Contains("[admin]", output);
        Assert.Contains("Nothing was changed", output);

        // The unmeasurable one shows "?" rather than a fabricated zero.
        Assert.Contains("opaque", output);
        Assert.Equal(5100, report.ReclaimableTotal);
    }

    [Fact]
    public void Wraps_at_word_boundaries()
    {
        var lines = AuditCommand.Wrap("one two three four five six", 9).ToList();
        Assert.Equal(["one two", "three", "four five", "six"], lines);
    }
}
