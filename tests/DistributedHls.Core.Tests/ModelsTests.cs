using System.Text.Json;
using Xunit;

namespace DistributedHls.Core.Tests;

public sealed class ModelsTests
{
    [Fact]
    public void ProbeJsonStoredBeforeProtocol2StillDeserializesWithSquarePixels()
    {
        // Probes saved by the previous version have no VideoIndex or sample aspect ratio.
        const string json = """{"Width":1920,"Height":1080,"FramesPerSecond":25,"DurationSeconds":60,"VideoCodec":"h264","PixelFormat":"yuv420p","ColorTransfer":"bt709","FieldOrder":"progressive","Rotation":0,"Audio":[],"Subtitles":[]}""";
        var probe = JsonSerializer.Deserialize<ProbeInfo>(json)!;
        Assert.Equal(0, probe.VideoIndex);
        Assert.Equal(1, probe.SarNumerator);
        Assert.Equal(1, probe.SarDenominator);
    }

    [Fact]
    public void MissingSummaryFieldsMeanNotApplicableRatherThanIndexZero()
    {
        var summary = JsonSerializer.Deserialize<RenditionSummary>("{}")!;
        Assert.Equal(-1, summary.SourceIndex);
        Assert.Equal(0, summary.Quality);
        Assert.Equal("", summary.Codec);
        Assert.Equal("", summary.Title);
        Assert.False(summary.Default);
    }

    [Fact]
    public void PlanAndCompletionSurviveAJsonRoundTrip()
    {
        var probe = new ProbeInfo(1920, 1080, 25, 60, "h264", "yuv420p", "bt709", "progressive", 0, [new(1, "aac", 2, 48000, "eng", "", true)], [new(2, "subrip", "eng", "", false)], 0, 32, 27);
        var plan = new EncodePlan(new string('a', 64), Guid.NewGuid(), probe, null,
        [
            new("v1080", RenditionKind.Video, "/out/v", "m/video/1080/t", new Quality(1080, 1080, 1920, 25)),
            new("a1", RenditionKind.Audio, "/out/a", "m/audio/1/t", null, 1, 2),
            new("preview", RenditionKind.Preview, "/out/p", "m/preview/t")
        ]);
        var completion = new EncodeCompletion("token", [new("v1080", new RenditionSummary(Quality: 1080, Codec: "avc1.640028"), [new("index.m3u8", 10, DateTimeOffset.UnixEpoch)])]);
        foreach (var value in new object[] { plan, completion, new WorkAssignment(new JobLease(Guid.NewGuid(), "encode:x", JobKind.Encode, null, "{}", "token", DateTimeOffset.UnixEpoch, 1), null) })
        {
            var json = JsonSerializer.Serialize(value, value.GetType());
            Assert.Equal(json, JsonSerializer.Serialize(JsonSerializer.Deserialize(json, value.GetType()), value.GetType()));
        }
        var restored = JsonSerializer.Deserialize<EncodePlan>(JsonSerializer.Serialize(plan))!;
        Assert.Equal(new Quality(1080, 1080, 1920, 25), restored.Outputs[0].Video);
        Assert.Null(restored.Outputs[1].Video);
        Assert.Equal(-1, restored.Outputs[2].SourceIndex);
        Assert.Equal((32, 27), (restored.Probe.SarNumerator, restored.Probe.SarDenominator));
        Assert.Null(restored.Source);
    }
}
