namespace DistributedHls.Core;

public static class EncodingLadder
{
    public static readonly int[] Heights = [2160, 1440, 1080, 720, 480, 360, 240, 144];

    public static Quality[] Select(ProbeInfo input, IEnumerable<int> selectedHeights)
    {
        // Display size: the stored width corrected by the pixel aspect ratio, swapped for 90/270 degree rotation.
        var width = input.Width * (double)input.SarNumerator / input.SarDenominator;
        var height = (double)input.Height;
        if (Math.Abs(input.Rotation % 180) == 90) (width, height) = (height, width);
        var portrait = height > width;
        var ladder = new List<Quality>();
        foreach (var name in selectedHeights.Distinct().OrderDescending().Where(x => Heights.Contains(x)))
        {
            // A quality is a 16:9 box (9:16 for portrait material) that the picture is fitted into without padding.
            // Up to 5% of enlargement is tolerated and ignored (the picture keeps its own size); anything more is skipped.
            var boxWidth = (double)Even(name * 16.0 / 9);
            var boxHeight = (double)name;
            if (portrait) (boxWidth, boxHeight) = (boxHeight, boxWidth);
            var fit = Math.Min(boxWidth / width, boxHeight / height);
            if (fit > 1.05) continue;
            var scale = Math.Min(fit, 1);
            var outWidth = Even(width * scale);
            var outHeight = Even(height * scale);
            if (ladder.Any(q => q.Width == outWidth && q.Height == outHeight)) continue;
            var fps = Math.Min(input.FramesPerSecond, 60);
            if (name < 720 && fps > 30) fps /= Math.Ceiling(fps / 30);
            ladder.Add(new Quality(name, outHeight, outWidth, fps));
        }
        return ladder.ToArray();
    }

    private static int Even(double x) => Math.Max(2, (int)Math.Round(x / 2, MidpointRounding.AwayFromZero) * 2);
}
