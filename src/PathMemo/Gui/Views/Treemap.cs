using PathMemo.Gui.Render;

namespace PathMemo.Gui.Views;

/// <summary>
/// The squarified treemap layout: sizes in, rectangles out (README section 24.5).
/// </summary>
/// <remarks>
/// <para>
/// A pure function of a list of sizes and a rectangle. It draws nothing and knows nothing
/// about snapshots, which is what lets it be tested against its two properties directly: every
/// block's area is proportional to its value, and no block leaves the frame
/// (README section 24.6).
/// </para>
/// <para>
/// <b>Squarified, not sliced.</b> The naive layout cuts the rectangle into parallel strips,
/// which turns a directory of a hundred items into a hundred slivers one pixel wide - visible,
/// unclickable and unreadable. Squarifying (Bruls, Huizing and van Wijk) fills the short side
/// with a row of blocks whose aspect ratios are as close to square as the next item allows,
/// stopping the row when adding one more would make the worst ratio worse. The result is
/// blocks you can aim at with a mouse, which is the whole reason a treemap is in a window
/// rather than in a terminal.
/// </para>
/// <para>
/// Blocks below a pixel or two are dropped rather than drawn. On a 1.2 million file disk the
/// tail is most of the list and none of the area: drawing it is thousands of rectangles that
/// together cover a hairline, and it would make the map look detailed while showing nothing.
/// The rows underneath remain the complete answer.
/// </para>
/// </remarks>
internal static class Treemap
{
    internal readonly record struct Block(Rect Area, int Index);

    /// <summary>Smaller than this on either side and a block is not worth drawing or aiming at.</summary>
    private const int Smallest = 3;

    internal static List<Block> Layout(ReadOnlySpan<long> sizes, Rect area)
    {
        var blocks = new List<Block>(Math.Min(sizes.Length, 256));
        if (area.Width < Smallest || area.Height < Smallest) return blocks;

        double remainingValue = 0;
        foreach (var size in sizes) remainingValue += size;

        if (remainingValue <= 0) return blocks;

        var rest = area;
        var index = 0;

        while (index < sizes.Length && rest.Width >= Smallest && rest.Height >= Smallest)
        {
            var side = Math.Min(rest.Width, rest.Height);
            var scale = rest.Width * (double)rest.Height / remainingValue;

            // Grow the row while the worst aspect ratio in it keeps improving. The moment one
            // more item would make it worse, this row is as square as it gets.
            var count = 0;
            double sum = 0;
            var worst = double.MaxValue;

            while (index + count < sizes.Length)
            {
                var value = sizes[index + count] * scale;
                if (value <= 0) break;

                var candidate = Worst(sum + value, sizes, index, count + 1, scale, side);
                if (count > 0 && candidate > worst) break;

                worst = candidate;
                sum += value;
                count++;
            }

            if (count == 0) break;

            var thickness = (int)Math.Round(sum / side);
            if (thickness < 1) thickness = 1;

            var horizontal = rest.Width >= rest.Height;

            // The strip runs along the short side; the thickness comes off the long one.
            var strip = horizontal ? rest.TakeLeft(thickness) : rest.TakeTop(thickness);
            Place(blocks, sizes, index, count, strip, horizontal);

            rest = horizontal ? rest.DropLeft(strip.Width) : rest.DropTop(strip.Height);

            for (var i = 0; i < count; i++) remainingValue -= sizes[index + i];
            index += count;

            if (remainingValue <= 0) break;
        }

        return blocks;
    }

    /// <summary>
    /// The worst aspect ratio a row would have: the larger of the two ways a block can be
    /// wrong, too wide or too tall.
    /// </summary>
    private static double Worst(
        double sum, ReadOnlySpan<long> sizes, int from, int count, double scale, int side)
    {
        double largest = 0;
        var smallest = double.MaxValue;

        for (var i = 0; i < count; i++)
        {
            var value = sizes[from + i] * scale;
            if (value > largest) largest = value;
            if (value < smallest) smallest = value;
        }

        if (sum <= 0 || smallest <= 0) return double.MaxValue;

        var squared = sum * sum;
        var sideSquared = (double)side * side;

        return Math.Max(sideSquared * largest / squared, squared / (sideSquared * smallest));
    }

    /// <summary>Divides one strip between the items of a row, in proportion.</summary>
    private static void Place(
        List<Block> blocks, ReadOnlySpan<long> sizes, int from, int count,
        Rect strip, bool horizontal)
    {
        var along = horizontal ? strip.Height : strip.Width;
        var total = Total(sizes, from, count);
        var offset = 0;

        for (var i = 0; i < count; i++)
        {
            // Proportion within the row, and the last block takes whatever rounding left over
            // so the strip is filled exactly rather than ending one pixel short.
            var length = i == count - 1
                ? along - offset
                : (int)Math.Round(along * (sizes[from + i] / total));

            if (length < 1) length = 1;
            if (offset + length > along) length = along - offset;
            if (length <= 0) break;

            var block = horizontal
                ? new Rect(strip.X, strip.Y + offset, strip.Width, length)
                : new Rect(strip.X + offset, strip.Y, length, strip.Height);

            if (block.Width >= Smallest && block.Height >= Smallest)
                blocks.Add(new Block(block, from + i));

            offset += length;
        }
    }

    private static double Total(ReadOnlySpan<long> sizes, int from, int count)
    {
        double total = 0;
        for (var i = 0; i < count; i++) total += sizes[from + i];

        return total <= 0 ? 1 : total;
    }
}
