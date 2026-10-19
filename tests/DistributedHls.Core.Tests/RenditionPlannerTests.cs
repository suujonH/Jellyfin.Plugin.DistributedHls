using Xunit;

namespace DistributedHls.Core.Tests;

public sealed class RenditionPlannerTests
{
    private static ProbeInfo Probe(int width = 1920, int height = 1080, AudioTrack[]? audio = null, SubtitleTrack[]? subtitles = null) =>
        new(width, height, 25, 60, "h264", "yuv420p", "bt709", "progressive", 0, audio ?? [], subtitles ?? []);

    private static AudioTrack Audio(int index, int channels) => new(index, "aac", channels, 48000, "eng", "", false);

    private static SubtitleTrack Subtitle(int index, string codec) => new(index, codec, "eng", "", false);

    [Fact]
    public void PlansVideoThenAudioThenTextSubtitlesThenPreview()
    {
        var probe = Probe(audio: [Audio(1, 6), Audio(2, 1)], subtitles: [Subtitle(3, "subrip"), Subtitle(4, "hdmv_pgs_subtitle"), Subtitle(5, "ass")]);
        var specs = RenditionPlanner.Desired(probe, [1080, 720]);
        Assert.Equal(new RenditionSpec[]
        {
            new("v1080", RenditionKind.Video, new Quality(1080, 1080, 1920, 25), -1, 0),
            new("v720", RenditionKind.Video, new Quality(720, 720, 1280, 25), -1, 0),
            new("a1", RenditionKind.Audio, null, 1, 2),
            new("a2", RenditionKind.Audio, null, 2, 1),
            new("s3", RenditionKind.Subtitle, null, 3, 0),
            new("s5", RenditionKind.Subtitle, null, 5, 0),
            new("preview", RenditionKind.Preview, null, -1, 0)
        }, specs);
    }

    [Fact]
    public void VideoQualitiesComeFromTheLadder()
    {
        var probe = Probe(1920, 800);
        var specs = RenditionPlanner.Desired(probe, [720, 1080, 2160]);
        Assert.Equal(EncodingLadder.Select(probe, [720, 1080, 2160]), specs.Where(s => s.Kind == RenditionKind.Video).Select(s => s.Video!));
        Assert.Equal(["v1080", "v720", "preview"], specs.Select(s => s.Key));
    }

    [Fact]
    public void NothingIsPlannedWhenNoQualityFits()
    {
        // 1280x720 would have to be enlarged for 1080: no video rendition, so audio, subtitles and preview are not encoded either.
        var probe = Probe(1280, 720, [Audio(1, 2)], [Subtitle(2, "subrip")]);
        Assert.Empty(RenditionPlanner.Desired(probe, [1080]));
        Assert.Empty(RenditionPlanner.Desired(probe, []));
    }

    [Fact]
    public void SourceWithoutAudioOrSubtitlesStillGetsVideoAndPreview() =>
        Assert.Equal(["v1080", "preview"], RenditionPlanner.Desired(Probe(), [1080]).Select(s => s.Key));

    [Theory]
    [InlineData("subrip", true)]
    [InlineData("srt", true)]
    [InlineData("ass", true)]
    [InlineData("ssa", true)]
    [InlineData("webvtt", true)]
    [InlineData("mov_text", true)]
    [InlineData("text", true)]
    [InlineData("hdmv_pgs_subtitle", false)]
    [InlineData("dvd_subtitle", false)]
    [InlineData("dvb_subtitle", false)]
    [InlineData("xsub", false)]
    [InlineData("", false)]
    public void OnlyTextSubtitlesArePlanned(string codec, bool planned)
    {
        var keys = RenditionPlanner.Desired(Probe(subtitles: [Subtitle(7, codec)]), [1080]).Select(s => s.Key);
        Assert.Equal(planned, keys.Contains("s7"));
    }

    [Fact]
    public void TextSubtitleCodecsListsTheSevenTextFormats() =>
        Assert.Equal(["subrip", "srt", "ass", "ssa", "webvtt", "mov_text", "text"], RenditionPlanner.TextSubtitleCodecs);

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(6, 2)]
    [InlineData(8, 2)]
    public void AudioChannelsAreClampedToOneOrTwo(int channels, int expected)
    {
        var audio = RenditionPlanner.Desired(Probe(audio: [Audio(1, channels)]), [1080]).Single(s => s.Kind == RenditionKind.Audio);
        Assert.Equal(expected, audio.Channels);
    }

    [Fact]
    public void AudioAndSubtitleKeysUseTheSourceStreamIndex()
    {
        var specs = RenditionPlanner.Desired(Probe(audio: [Audio(4, 2), Audio(9, 2)], subtitles: [Subtitle(12, "srt")]), [1080]);
        Assert.Equal(["v1080", "a4", "a9", "s12", "preview"], specs.Select(s => s.Key));
        Assert.Equal([4, 9], specs.Where(s => s.Kind == RenditionKind.Audio).Select(s => s.SourceIndex));
        Assert.Equal(12, specs.Single(s => s.Kind == RenditionKind.Subtitle).SourceIndex);
    }

    [Fact]
    public void EncodingProfileVersionIsPinned() =>
        // Changing any encoding parameter or ladder rule must change this value (it forces re-encoding of published renditions).
        Assert.Equal("h264-aac-sdr-v3", EncodingProfile.Version);
}
