using System.Globalization;
using System.Text;
using PathMemo.Cli.Output;
using PathMemo.Config;
using PathMemo.Platform;

namespace PathMemo.Cli.Commands;

internal enum ConfigAction
{
    /// <summary>Where the file is, whether it is being obeyed, and what it sets.</summary>
    Show,

    /// <summary>The path alone, for a script that wants to pipe it somewhere.</summary>
    Path,

    Edit,
    Reset,
}

/// <summary>
/// <c>pathmemo config</c>: find, create, open or reset <c>config.json</c> (README section 12).
/// </summary>
/// <remarks>
/// <para>
/// There is no <c>config set key value</c>, and there will not be. The file is the interface
/// - it holds globs, rule objects and nested sections, and a setter for that would be a
/// second, worse syntax for JSON. What the tool owes the user instead is the answer to
/// "where is it, is it being read, and what is it doing", which is what the default output is.
/// </para>
/// <para>
/// <c>--edit</c> and <c>--reset</c> refuse to run elevated. An administrator process writing
/// into a path derived from the user profile is the shape of the problem section 12.1 is
/// about, and the elevated run ignores most of the file anyway - editing it from there would
/// write a document the writer would not obey.
/// </para>
/// </remarks>
internal static class ConfigCommand
{
    internal static int Run(ConfigAction action) => action switch
    {
        ConfigAction.Path => PrintPath(),
        ConfigAction.Edit => Edit(),
        ConfigAction.Reset => Reset(),
        _ => Show(),
    };

    private static int PrintPath()
    {
        Console.WriteLine(AppPaths.ConfigPath);
        return File.Exists(AppPaths.ConfigPath) ? ExitCode.Ok : ExitCode.NoData;
    }

    private static int Show()
    {
        var path = AppPaths.ConfigPath;
        var w = Console.Out;

        w.WriteLine();
        w.WriteLine("CONFIGURATION");
        w.WriteLine($"  file       {path}");

        if (!File.Exists(path))
        {
            w.WriteLine("  state      does not exist - every setting is at its default");
            w.WriteLine();
            w.WriteLine("  'pathmemo config --edit' writes a documented starting point and opens it.");
            return ExitCode.Ok;
        }

        var info = new FileInfo(path);
        w.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "  state      {0}, changed {1:yyyy-MM-dd HH:mm} local",
            SizeFormat.Bytes(info.Length), info.LastWriteTime));

        var config = AppConfig.Current;
        w.WriteLine($"  honoured   {(config.Loaded ? "yes" : "no - the defaults are in force")}");

        if (AppPaths.IsRedirected)
            w.WriteLine("  note       --data-dir moved the store, so this is not the usual location");

        foreach (var warning in config.Warnings)
            w.WriteLine($"  warning    {warning}");

        // Only the keys that are actually read. A listing that showed the whole document of
        // section 12 would imply the tool obeys all of it, which it does not yet.
        w.WriteLine();
        w.WriteLine("IN FORCE");
        w.WriteLine($"  scan.useUsnIncremental            {Yes(config.Scan.UseUsnIncremental)}");
        w.WriteLine($"  protect.keep                      {config.Protect.Keep.Count} pattern(s)");
        w.WriteLine($"  protect.allowInsideProtected      {config.Protect.AllowInsideProtected.Count} pattern(s)");
        w.WriteLine($"  delete.defaultMode                {config.Delete.DefaultMode.ToString().ToLowerInvariant()}");
        w.WriteLine($"  delete.quarantineRetentionDays    {config.Delete.QuarantineRetentionDays}");
        w.WriteLine($"  delete.verifyBeforeDelete         {Yes(config.Delete.VerifyBeforeDelete)}");
        w.WriteLine($"  duplicates.minSize                {SizeFormat.Bytes(config.Duplicates.MinSize)}");
        w.WriteLine($"  duplicates.byteForByteVerify      {Yes(config.Duplicates.ByteForByteVerify)}");
        w.WriteLine($"  duplicates.hashCacheMaxEntries    {config.Duplicates.HashCacheMaxEntries:N0}");
        w.WriteLine($"  rules.disabled                    {config.Rules.Disabled.Count} rule(s)");
        w.WriteLine($"  rules.custom                      {config.Rules.Custom.Count} rule(s)");
        w.WriteLine($"  export.redactPaths                {Yes(config.Export.RedactPaths)}");
        w.WriteLine();
        w.WriteLine("  Everything else in README section 12 is documented but not read yet, and a key");
        w.WriteLine("  that is read but ignored would be worse than one that is not read at all.");

        return ExitCode.Ok;
    }

    private static int Edit()
    {
        if (Refused(out var code)) return code;

        var path = AppPaths.ConfigPath;
        if (!EnsureFile(out var error))
        {
            Console.Error.WriteLine($"pathmemo: {error}");
            return ExitCode.Failure;
        }

        if (!FileLaunch.TryOpen(path, out var why))
        {
            Console.Error.WriteLine($"pathmemo: could not open {path}: {why}");
            return ExitCode.Failure;
        }

        Console.WriteLine($"Opened {path} in its default editor.");
        return ExitCode.Ok;
    }

    private static int Reset()
    {
        if (Refused(out var code)) return code;

        var path = AppPaths.ConfigPath;

        // The old file is moved aside rather than overwritten: it may hold the only copy of
        // a keep list somebody built up over months, and "reset" is a word people type
        // faster than they read it.
        if (File.Exists(path))
        {
            var backup = path + ".bak";
            try
            {
                File.Move(path, backup, overwrite: true);
                Console.WriteLine($"Kept the previous file as {backup}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"pathmemo: could not move {path} aside: {ex.Message}");
                return ExitCode.Failure;
            }
        }

        if (!TryWriteTemplate(path, out var error))
        {
            Console.Error.WriteLine($"pathmemo: {error}");
            return ExitCode.Failure;
        }

        AppConfig.Reset();
        Console.WriteLine($"Wrote a default {path}");
        return ExitCode.Ok;
    }

    /// <summary>
    /// Makes sure there is something to open, writing the template if there is not. Shared
    /// with the TUI's <c>c</c> key, so both doors into the configuration behave the same.
    /// </summary>
    internal static bool EnsureFile(out string? error)
    {
        error = null;
        return File.Exists(AppPaths.ConfigPath) || TryWriteTemplate(AppPaths.ConfigPath, out error);
    }

    private static bool Refused(out int code)
    {
        code = ExitCode.Ok;
        if (!Elevation.IsElevated) return false;

        Console.Error.WriteLine(
            "pathmemo: config --edit and --reset are refused while running as administrator." +
            " An elevated run ignores most of the file anyway (README section 12.1);" +
            " edit it from an ordinary prompt.");
        code = ExitCode.Unsafe;
        return true;
    }

    private static bool TryWriteTemplate(string path, out string? error)
    {
        error = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, Template, new UTF8Encoding(false));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"could not write {path}: {ex.Message}";
            return false;
        }
    }

    private static string Yes(bool value) => value ? "yes" : "no";

    /// <summary>
    /// The starting point <c>--edit</c> and <c>--reset</c> write.
    /// </summary>
    /// <remarks>
    /// Every value here is the default, so the file changes nothing until something in it is
    /// changed - a template that silently altered behaviour would make "reset" a lie. It
    /// carries comments, which the reader allows and which are the only documentation
    /// somebody editing this file at 2 a.m. is going to have.
    /// </remarks>
    internal const string Template = """
        // pathmemo configuration. Every value below is already the default, so this file
        // changes nothing until you change something in it. Full reference: README section 12.
        //
        // Sizes accept "1GB" or 1073741824. Paths are globs, with ** for any number of
        // segments and %WINDIR%-style variables (README section 12.2).
        {
          "scan": {
            // A rescan may be built from the NTFS change journal instead of a full
            // traversal. Needs administrator rights; without them every scan is full.
            "useUsnIncremental": true
          },

          "protect": {
            // Never deleted, whatever a rule or the TUI says.
            "keep": [],

            // Deletable although they sit inside a protected directory.
            "allowInsideProtected": [
              "%WINDIR%\\Temp\\**",
              "%WINDIR%\\SoftwareDistribution\\Download\\**",
              "%WINDIR%\\Logs\\**",
              "%WINDIR%\\Prefetch\\**"
            ]
          },

          "delete": {
            // quarantine | recycle | permanent. Quarantine is a rename and is reversible.
            "defaultMode": "quarantine",
            "quarantineRetentionDays": 7,
            "requireTypedConfirmationOverBytes": "1GB",
            "verifyBeforeDelete": true
          },

          "duplicates": {
            "minSize": "1MB",
            "byteForByteVerify": true,
            "crossVolume": true,
            "skipCloudOnly": true,
            "hashCacheMaxEntries": 200000
          },

          "rules": {
            // Ids from 'pathmemo reclaim' that should never be recommended.
            "disabled": [],

            // Your own rules. One with a built-in id replaces it (README section 7.4).
            "custom": []
          },

          "export": {
            // Hash names in every export, keeping structure, sizes and extensions.
            "redactPaths": false
          }
        }

        """;
}
