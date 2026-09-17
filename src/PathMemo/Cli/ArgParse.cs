using System.Globalization;
using PathMemo.Analysis;

namespace PathMemo.Cli;

/// <summary>
/// Value parsers shared by the commands.
/// </summary>
/// <remarks>
/// Hand-rolled rather than System.CommandLine for now: the dispatcher is ~80 lines, it
/// adds no dependency to trim around, and every accepted spelling is visible in one place.
/// Revisited once the command set stops changing shape.
/// </remarks>
internal static class ArgParse
{
    /// <summary>Accepts "1048576", "1GB", "512mb", "4 GiB".</summary>
    internal static long Size(string text)
    {
        var trimmed = text.Trim();
        var digits = 0;
        while (digits < trimmed.Length && (char.IsAsciiDigit(trimmed[digits]) || trimmed[digits] is '.' or ','))
            digits++;

        if (digits == 0) throw new ArgumentException($"not a size: '{text}'");

        var number = double.Parse(
            trimmed[..digits].Replace(',', '.'), CultureInfo.InvariantCulture);

        var unit = trimmed[digits..].Trim().ToLowerInvariant();

        var multiplier = unit switch
        {
            "" or "b" => 1L,
            "k" or "kb" or "kib" => 1L << 10,
            "m" or "mb" or "mib" => 1L << 20,
            "g" or "gb" or "gib" => 1L << 30,
            "t" or "tb" or "tib" => 1L << 40,
            _ => throw new ArgumentException($"unknown size unit '{unit}' in '{text}'"),
        };

        return (long)(number * multiplier);
    }

    /// <summary>Accepts "30d", "12h", "90m", "45s", "2w".</summary>
    internal static TimeSpan Duration(string text)
    {
        var trimmed = text.Trim().ToLowerInvariant();
        var digits = 0;
        while (digits < trimmed.Length && char.IsAsciiDigit(trimmed[digits])) digits++;

        if (digits == 0) throw new ArgumentException($"not a duration: '{text}'");

        var value = long.Parse(trimmed[..digits], CultureInfo.InvariantCulture);

        return trimmed[digits..] switch
        {
            "s" => TimeSpan.FromSeconds(value),
            "m" => TimeSpan.FromMinutes(value),
            "h" => TimeSpan.FromHours(value),
            "" or "d" => TimeSpan.FromDays(value),
            "w" => TimeSpan.FromDays(value * 7),
            var unit => throw new ArgumentException($"unknown duration unit '{unit}' in '{text}'"),
        };
    }

    internal static int Count(string text) =>
        int.TryParse(text, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : throw new ArgumentException($"not a positive number: '{text}'");

    internal static long Id(string text) =>
        long.TryParse(text, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : throw new ArgumentException($"not a scan id: '{text}'");

    internal static SizeMode Mode(string text) => text.ToLowerInvariant() switch
    {
        "unique" => SizeMode.Unique,
        "allocated" => SizeMode.Allocated,
        "logical" => SizeMode.Logical,
        _ => throw new ArgumentException($"size mode must be unique, allocated or logical, not '{text}'"),
    };

    /// <summary>Comma-separated extension list, normalised to lower case without dots.</summary>
    internal static IReadOnlySet<string> Extensions(string text) =>
        text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(e => e.TrimStart('.').ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>Returns the value after <paramref name="index"/>, or throws.</summary>
    internal static string Value(string[] args, ref int index)
    {
        if (index + 1 >= args.Length) throw new ArgumentException($"'{args[index]}' needs a value");
        return args[++index];
    }
}
