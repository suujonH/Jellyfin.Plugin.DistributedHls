using System.Text.Json;
using Xunit;

namespace DistributedHls.Core.Tests;

public sealed class ProbeReaderTests
{
    private static string VideoJson(string? sampleAspectRatio, int index = 0) =>
        "{\"format\":{\"duration\":\"10\"},\"streams\":[{\"index\":" + index + ",\"codec_type\":\"video\",\"codec_name\":\"h264\",\"width\":720,\"height\":480,\"avg_frame_rate\":\"25/1\"" +
        (sampleAspectRatio is null ? "" : ",\"sample_aspect_ratio\":" + JsonSerializer.Serialize(sampleAspectRatio)) + "}]}";

    [Fact]
    public void ProbeIgnoresAttachedCoverImageAndNormalizesRelevantInformation()
    {
        var probe = ProbeReader.Parse("""
            {"format":{"duration":"42.5"},"streams":[
              {"index":0,"codec_type":"video","codec_name":"mjpeg","disposition":{"attached_pic":1}},
              {"index":1,"codec_type":"video","codec_name":"h264","width":1280,"height":720,"avg_frame_rate":"30000/1001"},
              {"index":2,"codec_type":"audio","codec_name":"aac","channels":2,"sample_rate":"48000","tags":{"language":"jpn"},"disposition":{"default":1}}
            ]}
            """);
        Assert.Equal(1280, probe.Width);
        Assert.Equal(30000d / 1001, probe.FramesPerSecond);
        Assert.Equal("jpn", Assert.Single(probe.Audio).Language);
        Assert.Equal(42.5, probe.DurationSeconds);
    }

    [Fact]
    public void VideoIndexIsTheStreamIndexOfTheRealVideoStream()
    {
        var withCover = ProbeReader.Parse("""
            {"format":{"duration":"10"},"streams":[
              {"index":0,"codec_type":"audio","codec_name":"aac","channels":2},
              {"index":1,"codec_type":"video","codec_name":"mjpeg","disposition":{"attached_pic":1}},
              {"index":2,"codec_type":"video","codec_name":"h264","width":1280,"height":720,"avg_frame_rate":"25/1"}
            ]}
            """);
        Assert.Equal(2, withCover.VideoIndex);
        Assert.Equal(0, ProbeReader.Parse(VideoJson(null, index: 0)).VideoIndex);
    }

    [Theory]
    [InlineData("32:27", 32, 27)]
    [InlineData("1:1", 1, 1)]
    [InlineData("16:11", 16, 11)]
    [InlineData("0:1", 1, 1)]
    [InlineData("N/A", 1, 1)]
    [InlineData("", 1, 1)]
    [InlineData("4:0", 1, 1)]
    [InlineData("-4:3", 1, 1)]
    [InlineData("4:-3", 1, 1)]
    [InlineData("a:b", 1, 1)]
    [InlineData("4", 1, 1)]
    [InlineData("4:3:2", 1, 1)]
    [InlineData("1.5:1", 1, 1)]
    [InlineData(" 4:3", 1, 1)]
    [InlineData("99999999999:1", 1, 1)]
    public void SampleAspectRatioMustBeTwoPositiveIntegers(string text, int numerator, int denominator)
    {
        var probe = ProbeReader.Parse(VideoJson(text));
        Assert.Equal(numerator, probe.SarNumerator);
        Assert.Equal(denominator, probe.SarDenominator);
    }

    [Fact]
    public void MissingSampleAspectRatioMeansSquarePixels()
    {
        var probe = ProbeReader.Parse(VideoJson(null));
        Assert.Equal(1, probe.SarNumerator);
        Assert.Equal(1, probe.SarDenominator);
    }
}
