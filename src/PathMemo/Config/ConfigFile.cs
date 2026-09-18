using System.Text.Json;
using System.Text.Json.Nodes;

namespace PathMemo.Config;

/// <summary>
/// The few edits pathmemo makes to <c>config.json</c> on the user's behalf
/// (README section 7.4).
/// </summary>
/// <remarks>
/// <para>
/// <c>config.json</c> is the single source of truth, so adding a path to the keep list
/// from the TUI has to end up in that file and nowhere else - a parallel store would mean
/// two answers to "what is protected", and the wrong one would be the one the guard reads.
/// </para>
/// <para>
/// Edited through <see cref="JsonNode"/> rather than rewritten from the parsed settings:
/// a user's comments are lost either way, but their keys are not. A file this tool did not
/// write keeps every section it does not understand.
/// </para>
/// </remarks>
internal static class ConfigFile
{
    /// <summary>
    /// Adds a path to <c>protect.keep</c>, creating the file if it does not exist.
    /// </summary>
    /// <returns>False with a reason; true with a message saying what happened.</returns>
    internal static bool AddKeep(string path, out string message)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            message = "there is nothing to keep";
            return false;
        }

        return Edit(root =>
        {
            var protect = Object(root, "protect");
            var keep = Array_(protect, "keep");

            foreach (var existing in keep)
                if (existing?.GetValue<string>() is { } text
                    && text.Equals(path, StringComparison.OrdinalIgnoreCase))
                    return $"{path} is already in the keep list";

            keep.Add((JsonNode)path);
            return $"added to protect.keep: {path}";
        }, out message);
    }

    /// <summary>Adds a rule id to <c>rules.disabled</c>.</summary>
    internal static bool Disable(string ruleId, out string message) =>
        Edit(root =>
        {
            var rules = Object(root, "rules");
            var disabled = Array_(rules, "disabled");

            foreach (var existing in disabled)
                if (existing?.GetValue<string>() is { } text
                    && text.Equals(ruleId, StringComparison.OrdinalIgnoreCase))
                    return $"rule {ruleId} is already disabled";

            disabled.Add((JsonNode)ruleId);
            return $"rule {ruleId} will no longer appear in recommendations";
        }, out message);

    /// <summary>Removes a rule id from <c>rules.disabled</c>.</summary>
    internal static bool Enable(string ruleId, out string message) =>
        Edit(root =>
        {
            if (root["rules"] is not JsonObject rules || rules["disabled"] is not JsonArray disabled)
                return $"rule {ruleId} was not disabled";

            for (var i = 0; i < disabled.Count; i++)
            {
                if (disabled[i]?.GetValue<string>() is not { } text) continue;
                if (!text.Equals(ruleId, StringComparison.OrdinalIgnoreCase)) continue;

                disabled.RemoveAt(i);
                return $"rule {ruleId} is enabled again";
            }

            return $"rule {ruleId} was not disabled";
        }, out message);

    private static bool Edit(Func<JsonObject, string> change, out string message)
    {
        try
        {
            var root = Read();
            message = change(root);

            var path = AppPaths.ConfigPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            // Written through a temporary file and moved into place: a half-written
            // config.json is a tool that will not start, and this runs while the user is
            // looking at a screen rather than at a prompt.
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, overwrite: true);

            AppConfig.Reset();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            message = $"could not update {AppPaths.ConfigPath}: {ex.Message}";
            return false;
        }
    }

    private static JsonObject Read()
    {
        var path = AppPaths.ConfigPath;
        if (!File.Exists(path)) return [];

        var text = File.ReadAllText(path);
        if (text.AsSpan().Trim().IsEmpty) return [];

        return JsonNode.Parse(text, documentOptions: new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        }) as JsonObject ?? [];
    }

    private static JsonObject Object(JsonObject parent, string name)
    {
        if (parent[name] is JsonObject existing) return existing;

        var created = new JsonObject();
        parent[name] = created;
        return created;
    }

    private static JsonArray Array_(JsonObject parent, string name)
    {
        if (parent[name] is JsonArray existing) return existing;

        var created = new JsonArray();
        parent[name] = created;
        return created;
    }
}
