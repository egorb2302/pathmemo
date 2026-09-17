using System.Globalization;

namespace PathMemo.Cli.Output;

internal static class SizeFormat
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    /// <summary>
    /// Human-readable size, e.g. "18.2 GB". Binary units (1 KB = 1024 B) to match
    /// what Explorer reports, so users can cross-check our numbers.
    /// </summary>
    internal static string Bytes(ulong value)
    {
        if (value < 1024) return $"{value} B";

        double v = value;
        var unit = 0;
        while (v >= 1024 && unit < Units.Length - 1)
        {
            v /= 1024;
            unit++;
        }

        // 3 significant figures reads better in a column than a fixed decimal count:
        // "999 MB", "9.99 GB", "99.9 GB", "476 GB"
        var digits = v >= 100 ? 0 : v >= 10 ? 1 : 2;
        return string.Create(CultureInfo.InvariantCulture, $"{Math.Round(v, digits)} {Units[unit]}");
    }

    internal static string Bytes(long value) =>
        value < 0 ? "-" + Bytes((ulong)(-value)) : Bytes((ulong)value);
}
