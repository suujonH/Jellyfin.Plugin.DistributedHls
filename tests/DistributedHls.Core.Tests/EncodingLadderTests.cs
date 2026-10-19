using Xunit;

namespace DistributedHls.Core.Tests;

public sealed class EncodingLadderTests
{
    private static ProbeInfo Probe(int width, int height, int sarNumerator = 1, int sarDenominator = 1, int rotation = 0, double fps = 25) =>
        new(width, height, fps, 60, "h264", "yuv420p", "bt709", "progressive", rotation, [], [], 0, sarNumerator, sarDenominator);

    private static string Describe(IEnumerable<Quality> ladder) => string.Join(",", ladder.Select(q => $"{q.Name}:{q.Width}x{q.Height}"));

    private static Quality[] Select(ProbeInfo probe, string heights) => EncodingLadder.Select(probe, heights.Split(',').Select(h => int.Parse(h)));

    [Theory]
    [InlineData(1920, 800, 1, 1, 0, "1080,720", "1080:1920x800,720:1280x534")]            // wide film keeps its own aspect inside the 16:9 box
    [InlineData(1920, 800, 1, 1, 0, "1080,720,480", "1080:1920x800,720:1280x534,480:854x356")]
    [InlineData(3840, 2076, 1, 1, 0, "2160", "2160:3840x2076")]                           // taller than 16:9 but still fits the 2160 box
    [InlineData(720, 480, 32, 27, 0, "480", "480:854x480")]                               // anamorphic DVD: SAR widens the display size
    [InlineData(1918, 798, 1, 1, 0, "1080", "1080:1918x798")]                             // 2 px under the box: kept as is, never padded or stretched
    [InlineData(1917, 1079, 1, 1, 0, "1080", "1080:1918x1080")]                           // odd sizes round half away from zero to even
    [InlineData(1280, 720, 1, 1, 0, "1080", "")]                                          // 1.5x upscale is refused
    [InlineData(1920, 1080, 1, 1, 0, "2160", "")]
    [InlineData(1080, 1920, 1, 1, 0, "1080", "1080:1080x1920")]                           // portrait uses the 9:16 box
    [InlineData(1920, 1080, 1, 1, 90, "1080", "1080:1080x1920")]                          // rotation swaps the display size
    [InlineData(1920, 1080, 1, 1, -90, "1080", "1080:1080x1920")]
    [InlineData(1920, 1080, 1, 1, 270, "1080", "1080:1080x1920")]
    [InlineData(1920, 1080, 1, 1, 180, "1080", "1080:1920x1080")]                         // upside down is not a swap
    [InlineData(720, 480, 32, 27, 90, "480", "480:480x854")]                              // SAR is applied before the rotation swap
    public void SizesFollowTheSixteenByNineBoxRule(int width, int height, int sarNumerator, int sarDenominator, int rotation, string selected, string expected) =>
        Assert.Equal(expected, Describe(Select(Probe(width, height, sarNumerator, sarDenominator, rotation), selected)));

    [Fact]
    public void SourceWithinFivePercentOfTheBoxIsKeptAtSourceSize()
    {
        // 1920/1830 = 1.0492, 1080/1030 = 1.0485: the smaller factor 1.0485 does not exceed 1.05.
        var quality = Assert.Single(Select(Probe(1830, 1030), "1080"));
        Assert.Equal(1080, quality.Name);
        Assert.Equal(1830, quality.Width);
        Assert.Equal(1030, quality.Height);
    }

    [Fact]
    public void SourceBeyondFivePercentOfTheBoxIsNotUpscaled() =>
        // 1920/1824 = 1080/1026 = 1.0526 > 1.05.
        Assert.Empty(Select(Probe(1824, 1026), "1080"));

    [Fact]
    public void SelectionIsDistinctDescendingAndLimitedToLadderHeights()
    {
        var ladder = Select(Probe(1920, 1080), "480,1080,720,1080,999,0,-5");
        Assert.Equal([1080, 720, 480], ladder.Select(q => q.Name));
    }

    [Fact]
    public void LadderDoesNotUpscaleAndKeepsFractionalFrameRates()
    {
        var probe = new ProbeInfo(1920, 1080, 60000d / 1001, 60, "h264", "yuv420p", "bt709", "progressive", 0, [], []);
        var ladder = EncodingLadder.Select(probe, [2160, 1440, 1080, 720, 480]);
        Assert.Equal([1080, 720, 480], ladder.Select(q => q.Name));
        Assert.Equal(60000d / 1001, ladder[0].FramesPerSecond);
        Assert.Equal(30000d / 1001, ladder[2].FramesPerSecond);
        Assert.All(ladder, q => { Assert.Equal(0, q.Width % 2); Assert.Equal(0, q.Height % 2); });
    }

    [Fact]
    public void FrameRateIsCappedAtSixtyAndHalvedOnlyBelow720()
    {
        Assert.Equal(60, Assert.Single(Select(Probe(1920, 1080, fps: 120), "1080")).FramesPerSecond);
        var ladder = Select(Probe(1920, 1080, fps: 50), "1080,720,480,360");
        Assert.Equal([50d, 50d, 25d, 25d], ladder.Select(q => q.FramesPerSecond));
    }

    [Fact]
    public void RotatedPortraitKeepsQualityNameSeparateFromOutputHeight()
    {
        var probe = new ProbeInfo(1920, 1080, 25, 60, "h264", "yuv420p", "bt709", "progressive", 90, [], []);
        var quality = Assert.Single(EncodingLadder.Select(probe, [720]));
        Assert.Equal(720, quality.Name);
        Assert.Equal(720, quality.Width);
        Assert.Equal(1280, quality.Height);
    }
}
