using System.Globalization;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using PathMemo.Analysis;
using PathMemo.Audit;
using PathMemo.Deletion;
using PathMemo.Platform;

namespace PathMemo.Config;

/// <summary>Paths the user declared off limits, and the holes punched in the built-in list.</summary>
internal sealed record ProtectSettings
{
    /// <summary>Patterns that must never be deleted, whatever else says otherwise.</summary>
    internal IReadOnlyList<string> Keep { get; init; } = [];

    /// <summary>
    /// A whitelist inside the blacklist: <c>%WINDIR%\Temp\**</c> is deletable although
    /// <c>%WINDIR%</c> is protected (README section 9.3, step 3).
    /// </summary>
    internal IReadOnlyList<string> AllowInsideProtected { get; init; } =
    [
        @"%WINDIR%\Temp\**",
        @"%WINDIR%\SoftwareDistribution\Download\**",
        @"%WINDIR%\Logs\**",
        @"%WINDIR%\Prefetch\**",
    ];
}

/// <summary>Thresholds for the deletion module (README sections 9.2, 12).</summary>
internal sealed record DeleteSettings
{
    internal DeleteMode DefaultMode { get; init; } = DeleteMode.Quarantine;
    internal int RecycleMaxItems { get; init; } = 100;
    internal long RecycleMaxBytes { get; init; } = 500L << 20;
    internal int QuarantineRetentionDays { get; init; } = 7;
    internal bool AutoPurgeExpired { get; init; } = true;
    internal long RequireTypedConfirmationOverBytes { get; init; } = 1L << 30;
    internal bool VerifyBeforeDelete { get; init; } = true;
}

/// <summary>
/// Which cleanup rules are in force, and the user's own (README section 7.4).
/// </summary>
internal sealed record RulesSettings
{
    /// <summary>Ids of built-in rules that must not appear in recommendations.</summary>
    internal IReadOnlyList<string> Disabled { get; init; } = [];

    /// <summary>
    /// Rules from the configuration. One with a built-in id replaces that rule, which is
    /// how a threshold is changed without retyping the table.
    /// </summary>
    internal IReadOnlyList<ReclaimRule> Custom { get; init; } = [];
}

/// <summary>
/// <c>config.json</c>, read once (README section 12).
/// </summary>
/// <remarks>
/// <para>
/// Read with <see cref="JsonDocument"/> and explicit lookups rather than a deserialiser:
/// the same reason the rest of the application avoids reflection (README section 18), plus
/// an unknown key must be a warning, not a crash - a configuration file is edited by hand.
/// </para>
/// <para>
/// A file that cannot be parsed yields the defaults and one warning. The alternative -
/// refusing to start - turns a stray comma into "the disk tool no longer runs".
/// </para>
/// </remarks>
internal sealed class AppConfig
{
    private static AppConfig? _current;

    internal ProtectSettings Protect { get; private init; } = new();
    internal DeleteSettings Delete { get; private init; } = new();
    internal RulesSettings Rules { get; private init; } = new();

    /// <summary>Problems found while loading, printed once by the command that needs them.</summary>
    internal IReadOnlyList<string> Warnings { get; private init; } = [];

    /// <summary>False when the file was ignored: missing, broken, or not trustworthy here.</summary>
    internal bool Loaded { get; private init; }

    internal static AppConfig Current => _current ??= Load(AppPaths.ConfigPath);

    /// <summary>Drops the cached copy. Tests and <c>config --reset</c> need this.</summary>
    internal static void Reset() => _current = null;

    internal static AppConfig Load(string path)
    {
        var warnings = new List<string>();

        if (!File.Exists(path)) return new AppConfig { Warnings = warnings };

        // An elevated process must not take deletion policy from a file an ordinary user
        // can rewrite: "allowInsideProtected": ["**"] would be a privilege escalation with
        // a mop (README section 12.1, threat T7).
        if (Elevation.IsElevated && !IsTrustworthy(path, out var why))
        {
            warnings.Add($"config.json is ignored while elevated: {why} (README section 12.1)");
            return new AppConfig { Warnings = warnings };
        }

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"config.json could not be read ({ex.Message}); using defaults");
            return new AppConfig { Warnings = warnings };
        }

        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });

            return new AppConfig
            {
                Protect = ReadProtect(document.RootElement, warnings),
                Delete = ReadDelete(document.RootElement, warnings),
                Rules = ReadRules(document.RootElement, warnings),
                Warnings = warnings,
                Loaded = true,
            };
        }
        catch (JsonException ex)
        {
            warnings.Add($"config.json is not valid JSON ({ex.Message}); using defaults");
            return new AppConfig { Warnings = warnings };
        }
    }

    private static ProtectSettings ReadProtect(JsonElement root, List<string> warnings)
    {
        var defaults = new ProtectSettings();
        if (!root.TryGetProperty("protect", out var protect) || protect.ValueKind != JsonValueKind.Object)
            return defaults;

        return new ProtectSettings
        {
            Keep = Strings(protect, "keep", defaults.Keep, warnings),
            AllowInsideProtected = Strings(protect, "allowInsideProtected", defaults.AllowInsideProtected, warnings),
        };
    }

    private static DeleteSettings ReadDelete(JsonElement root, List<string> warnings)
    {
        var defaults = new DeleteSettings();
        if (!root.TryGetProperty("delete", out var delete) || delete.ValueKind != JsonValueKind.Object)
            return defaults;

        return new DeleteSettings
        {
            DefaultMode = Mode(delete, "defaultMode", defaults.DefaultMode, warnings),
            RecycleMaxItems = (int)Number(delete, "recycleMaxItems", defaults.RecycleMaxItems, warnings),
            RecycleMaxBytes = Number(delete, "recycleMaxBytes", defaults.RecycleMaxBytes, warnings),
            QuarantineRetentionDays = (int)Number(delete, "quarantineRetentionDays", defaults.QuarantineRetentionDays, warnings),
            AutoPurgeExpired = Boolean(delete, "autoPurgeExpired", defaults.AutoPurgeExpired, warnings),
            RequireTypedConfirmationOverBytes = Number(delete, "requireTypedConfirmationOverBytes",
                defaults.RequireTypedConfirmationOverBytes, warnings),
            VerifyBeforeDelete = Boolean(delete, "verifyBeforeDelete", defaults.VerifyBeforeDelete, warnings),
        };
    }

    /// <summary>
    /// The <c>rules</c> section (README section 7.4).
    /// </summary>
    /// <remarks>
    /// A custom rule that will not parse is skipped with a warning naming it, and the rest
    /// of the file still applies. The alternative - one typo disabling every rule the user
    /// wrote - is how configuration files come to be distrusted.
    /// </remarks>
    private static RulesSettings ReadRules(JsonElement root, List<string> warnings)
    {
        var defaults = new RulesSettings();
        if (!root.TryGetProperty("rules", out var rules) || rules.ValueKind != JsonValueKind.Object)
            return defaults;

        var custom = new List<ReclaimRule>();

        if (rules.TryGetProperty("custom", out var list))
        {
            if (list.ValueKind != JsonValueKind.Array)
                warnings.Add("config: 'rules.custom' should be a list of rule objects; ignored");
            else
                foreach (var element in list.EnumerateArray())
                    if (ReadRule(element, warnings) is { } rule) custom.Add(rule);
        }

        return new RulesSettings
        {
            Disabled = Strings(rules, "disabled", defaults.Disabled, warnings),
            Custom = custom,
        };
    }

    private static ReclaimRule? ReadRule(JsonElement element, List<string> warnings)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            warnings.Add("config: a 'rules.custom' entry is not an object; skipped");
            return null;
        }

        var id = Text(element, "id");
        if (id is null)
        {
            warnings.Add("config: a 'rules.custom' entry has no 'id'; skipped");
            return null;
        }

        var patterns = Strings(element, "patterns", [], warnings);
        if (patterns.Count == 0)
        {
            warnings.Add($"config: rule '{id}' has no patterns; skipped");
            return null;
        }

        var risk = Risk.Safe;
        if (Text(element, "risk") is { } riskText && !ReclaimNames.TryRisk(riskText, out risk))
            warnings.Add($"config: rule '{id}' has risk '{riskText}'; using safe");

        var recoverability = Recoverability.Rebuild;
        if (Text(element, "recoverability") is { } recoveryText
            && !ReclaimNames.TryRecoverability(recoveryText, out recoverability))
            warnings.Add($"config: rule '{id}' has recoverability '{recoveryText}'; using rebuild");

        var kind = MatchKind.Directory;
        if (Text(element, "kind") is { } kindText)
        {
            kind = kindText.Trim().ToLowerInvariant() switch
            {
                "file" => MatchKind.File,
                "any" => MatchKind.Any,
                "directory" or "dir" => MatchKind.Directory,
                _ => Unknown(),
            };

            MatchKind Unknown()
            {
                warnings.Add($"config: rule '{id}' has kind '{kindText}'; using directory");
                return MatchKind.Directory;
            }
        }

        TimeSpan? olderThan = null;
        if (Text(element, "olderThan") is { } age)
        {
            try { olderThan = Cli.ArgParse.Duration(age); }
            catch (ArgumentException) { warnings.Add($"config: rule '{id}' has olderThan '{age}'; ignored"); }
        }

        return new ReclaimRule
        {
            Id = id,
            Patterns = patterns,
            Risk = risk,
            Recoverability = recoverability,
            What = Text(element, "what") ?? "a rule from config.json",
            Command = Text(element, "command"),
            Kind = kind,
            MinSizeBytes = Number(element, "minSizeBytes", 0, warnings),
            OlderThan = olderThan,
            RequiresChild = Text(element, "requiresChild"),
            RequiresSibling = Text(element, "requiresSibling"),
            NotUnder = Strings(element, "notUnder", [], warnings),
            ContentsOnly = Boolean(element, "contentsOnly", false, warnings),
            Custom = true,
        };
    }

    private static string? Text(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static IReadOnlyList<string> Strings(
        JsonElement parent, string name, IReadOnlyList<string> fallback, List<string> warnings)
    {
        if (!parent.TryGetProperty(name, out var value)) return fallback;

        if (value.ValueKind != JsonValueKind.Array)
        {
            warnings.Add($"config: '{name}' should be a list of patterns; keeping the default");
            return fallback;
        }

        var items = new List<string>();
        foreach (var element in value.EnumerateArray())
        {
            if (element.ValueKind == JsonValueKind.String) items.Add(element.GetString()!);
            else warnings.Add($"config: '{name}' contains a non-string entry, skipped");
        }

        return items;
    }

    /// <summary>Accepts a number or a human size ("500MB"), per README section 12.</summary>
    private static long Number(JsonElement parent, string name, long fallback, List<string> warnings)
    {
        if (!parent.TryGetProperty(name, out var value)) return fallback;

        try
        {
            return value.ValueKind switch
            {
                JsonValueKind.Number => value.GetInt64(),
                JsonValueKind.String => Cli.ArgParse.Size(value.GetString()!),
                _ => throw new ArgumentException("not a number"),
            };
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException)
        {
            warnings.Add($"config: '{name}' is not a size; keeping {fallback.ToString(CultureInfo.InvariantCulture)}");
            return fallback;
        }
    }

    private static bool Boolean(JsonElement parent, string name, bool fallback, List<string> warnings)
    {
        if (!parent.TryGetProperty(name, out var value)) return fallback;

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => Warn(),
        };

        bool Warn()
        {
            warnings.Add($"config: '{name}' should be true or false; keeping the default");
            return fallback;
        }
    }

    private static DeleteMode Mode(JsonElement parent, string name, DeleteMode fallback, List<string> warnings)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            return fallback;

        var text = value.GetString() ?? "";
        if (DeleteModes.TryParse(text, out var mode)) return mode;

        warnings.Add($"config: '{name}' must be quarantine, recycle or permanent, not '{text}'");
        return fallback;
    }

    /// <summary>
    /// Whether the file's permissions allow only the owner, Administrators and SYSTEM to
    /// write it. Anything wider means an unprivileged process could have staged its
    /// contents for the elevated one to obey (README section 12.1).
    /// </summary>
    private static bool IsTrustworthy(string path, out string why)
    {
        const FileSystemRights writeMask =
            FileSystemRights.WriteData | FileSystemRights.AppendData |
            FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership |
            FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles;

        try
        {
            var security = new FileInfo(path).GetAccessControl(
                AccessControlSections.Owner | AccessControlSections.Access);

            var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;

            foreach (FileSystemAccessRule rule in
                     security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType != AccessControlType.Allow) continue;
                if ((rule.FileSystemRights & writeMask) == 0) continue;

                if (rule.IdentityReference is not SecurityIdentifier sid)
                {
                    why = "its permissions could not be read";
                    return false;
                }

                if (sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)) continue;
                if (sid.IsWellKnown(WellKnownSidType.LocalSystemSid)) continue;
                if (owner is not null && sid == owner) continue;

                why = $"'{Describe(sid)}' can write it";
                return false;
            }

            why = "";
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException
                                      or PlatformNotSupportedException or PrivilegeNotHeldException)
        {
            why = $"its permissions could not be read ({ex.Message})";
            return false;
        }
    }

    private static string Describe(SecurityIdentifier sid)
    {
        try { return sid.Translate(typeof(NTAccount)).Value; }
        catch (IdentityNotMappedException) { return sid.Value; }
        catch (SystemException) { return sid.Value; }
    }
}
