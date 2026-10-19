using System.Globalization;
using Xunit;

namespace DistributedHls.Core.Tests;

public sealed class MasterPlaylistWriterTests
{
    private static readonly PublishedRendition Video1080 = Video(1080, 1920, 1080, 5_000_000, 4_000_000, "avc1.640028");
    private static readonly PublishedRendition Video720 = Video(720, 1280, 720, 2_800_000, 2_100_000, "avc1.64001f");
    private static readonly PublishedRendition Japanese = Audio(1, "jpn", "", 2, 128_000);
    private static readonly PublishedRendition Commentary = Audio(2, "eng", "Commentary", 2, 192_000, isDefault: true);
    private static readonly PublishedRendition English = Subtitle(3, "eng", "English");
    private static readonly PublishedRendition Chinese = Subtitle(4, "chi", "");

    private static PublishedRendition Video(int quality, int width, int height, int bandwidth, int average, string codec) =>
        new($"v{quality}", $"m/video/{quality}/t", new RenditionSummary(Quality: quality, Width: width, Height: height, FramesPerSecond: 30000d / 1001, Bandwidth: bandwidth, AverageBandwidth: average, Codec: codec));

    private static PublishedRendition Audio(int index, string language, string title, int channels, int bandwidth, bool isDefault = false) =>
        new($"a{index}", $"m/audio/{index}/t", new RenditionSummary(SourceIndex: index, Language: language, Title: title, Channels: channels, Bandwidth: bandwidth, Default: isDefault));

    private static PublishedRendition Subtitle(int index, string language, string title) =>
        new($"s{index}", $"m/subtitles/{index}/t", new RenditionSummary(SourceIndex: index, Language: language, Title: title));

    private static MediaView Media(PublishedRendition[] video, PublishedRendition[] audio, PublishedRendition[] subtitles) =>
        new(new string('a', 64), Guid.Empty, 120, 0, audio.Length, video, audio, subtitles);

    private static MediaView Everything() => Media([Video1080, Video720], [Japanese, Commentary], [English, Chinese]);

    // Playlists are compared line by line: the expected text must not depend on the line endings of this source file.
    private static string Lines(params string[] lines) => string.Join("\n", lines) + "\n";

    private const string Head = "#EXTM3U\n#EXT-X-VERSION:7\n#EXT-X-INDEPENDENT-SEGMENTS\n";
    private const string JapaneseLine = "#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"audio\",NAME=\"音轨 1 [1]\",LANGUAGE=\"ja\",DEFAULT={0},AUTOSELECT=YES,CHANNELS=\"2\",URI=\"r/audio/1/index.m3u8\"";
    private const string CommentaryLine = "#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"audio\",NAME=\"Commentary [2]\",LANGUAGE=\"en\",DEFAULT={0},AUTOSELECT=YES,CHANNELS=\"2\",URI=\"r/audio/2/index.m3u8\"";
    private const string EnglishLine = "#EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID=\"subs\",NAME=\"English [3]\",LANGUAGE=\"en\",DEFAULT=NO,AUTOSELECT=YES,FORCED=NO,URI=\"r/subtitles/3/index.m3u8\"";
    private const string ChineseLine = "#EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID=\"subs\",NAME=\"字幕 4 [4]\",LANGUAGE=\"zh\",DEFAULT=NO,AUTOSELECT=YES,FORCED=NO,URI=\"r/subtitles/4/index.m3u8\"";

    [Fact]
    public void AutomaticMasterListsEveryRenditionWithRelativeUris()
    {
        Assert.Equal(Head + Lines(
            string.Format(JapaneseLine, "NO"),
            string.Format(CommentaryLine, "YES"),
            EnglishLine,
            ChineseLine,
            // Variants are listed by ascending video bandwidth; BANDWIDTH adds the largest audio bandwidth (192000).
            "#EXT-X-STREAM-INF:BANDWIDTH=2992000,AVERAGE-BANDWIDTH=2292000,RESOLUTION=1280x720,FRAME-RATE=29.97,CODECS=\"avc1.64001f,mp4a.40.2\",AUDIO=\"audio\",SUBTITLES=\"subs\"",
            "r/video/720/index.m3u8",
            "#EXT-X-STREAM-INF:BANDWIDTH=5192000,AVERAGE-BANDWIDTH=4192000,RESOLUTION=1920x1080,FRAME-RATE=29.97,CODECS=\"avc1.640028,mp4a.40.2\",AUDIO=\"audio\",SUBTITLES=\"subs\"",
            "r/video/1080/index.m3u8"), MasterPlaylistWriter.Write(Everything(), 0, null));
    }

    [Fact]
    public void FixedQualityKeepsOnlyThatVideoRendition()
    {
        Assert.Equal(Head + Lines(
            string.Format(JapaneseLine, "NO"),
            string.Format(CommentaryLine, "YES"),
            EnglishLine,
            ChineseLine,
            "#EXT-X-STREAM-INF:BANDWIDTH=2992000,AVERAGE-BANDWIDTH=2292000,RESOLUTION=1280x720,FRAME-RATE=29.97,CODECS=\"avc1.64001f,mp4a.40.2\",AUDIO=\"audio\",SUBTITLES=\"subs\"",
            "r/video/720/index.m3u8"), MasterPlaylistWriter.Write(Everything(), 720, null));
    }

    [Fact]
    public void SingleAudioKeepsOnlyThatTrackAndMakesItTheDefault()
    {
        // Track 1 is not marked default and has the smaller bandwidth: it becomes the only and default track, and BANDWIDTH adds 128000.
        Assert.Equal(Head + Lines(
            string.Format(JapaneseLine, "YES"),
            EnglishLine,
            ChineseLine,
            "#EXT-X-STREAM-INF:BANDWIDTH=2928000,AVERAGE-BANDWIDTH=2228000,RESOLUTION=1280x720,FRAME-RATE=29.97,CODECS=\"avc1.64001f,mp4a.40.2\",AUDIO=\"audio\",SUBTITLES=\"subs\"",
            "r/video/720/index.m3u8",
            "#EXT-X-STREAM-INF:BANDWIDTH=5128000,AVERAGE-BANDWIDTH=4128000,RESOLUTION=1920x1080,FRAME-RATE=29.97,CODECS=\"avc1.640028,mp4a.40.2\",AUDIO=\"audio\",SUBTITLES=\"subs\"",
            "r/video/1080/index.m3u8"), MasterPlaylistWriter.Write(Everything(), 0, 1));
    }

    [Fact]
    public void FixedQualityAndSingleAudioCombine()
    {
        Assert.Equal(Head + Lines(
            string.Format(CommentaryLine, "YES"),
            EnglishLine,
            ChineseLine,
            "#EXT-X-STREAM-INF:BANDWIDTH=5192000,AVERAGE-BANDWIDTH=4192000,RESOLUTION=1920x1080,FRAME-RATE=29.97,CODECS=\"avc1.640028,mp4a.40.2\",AUDIO=\"audio\",SUBTITLES=\"subs\"",
            "r/video/1080/index.m3u8"), MasterPlaylistWriter.Write(Everything(), 1080, 2));
    }

    [Fact]
    public void FirstTrackIsDefaultWhenNoneIsMarked()
    {
        var text = MasterPlaylistWriter.Write(Media([Video720], [Japanese, Audio(2, "eng", "Main", 2, 192_000)], []), 0, null);
        var tracks = text.Split('\n').Where(l => l.StartsWith("#EXT-X-MEDIA:TYPE=AUDIO", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, tracks.Length);
        Assert.Contains("DEFAULT=YES", tracks[0]);
        Assert.Contains("DEFAULT=NO", tracks[1]);
    }

    [Fact]
    public void WithoutAudioThereIsNoAudioGroupAndNoAudioCodec()
    {
        Assert.Equal(Head + Lines(
            EnglishLine,
            "#EXT-X-STREAM-INF:BANDWIDTH=2800000,AVERAGE-BANDWIDTH=2100000,RESOLUTION=1280x720,FRAME-RATE=29.97,CODECS=\"avc1.64001f\",SUBTITLES=\"subs\"",
            "r/video/720/index.m3u8"), MasterPlaylistWriter.Write(Media([Video720], [], [English]), 0, null));
    }

    [Fact]
    public void WithoutSubtitlesThereIsNoSubtitleGroup()
    {
        Assert.Equal(Head + Lines(
            string.Format(CommentaryLine, "YES"),
            "#EXT-X-STREAM-INF:BANDWIDTH=2992000,AVERAGE-BANDWIDTH=2292000,RESOLUTION=1280x720,FRAME-RATE=29.97,CODECS=\"avc1.64001f,mp4a.40.2\",AUDIO=\"audio\"",
            "r/video/720/index.m3u8"), MasterPlaylistWriter.Write(Media([Video720], [Commentary], []), 0, null));
    }

    [Fact]
    public void UnpublishedSelectionsDegradeInsteadOfThrowing()
    {
        // The delivery endpoint validates quality and audio index; the writer itself only leaves out what is not there.
        var noVideo = MasterPlaylistWriter.Write(Everything(), 480, null);
        Assert.DoesNotContain("#EXT-X-STREAM-INF", noVideo);
        Assert.DoesNotContain("r/video", noVideo);
        var noAudio = MasterPlaylistWriter.Write(Everything(), 0, 9);
        Assert.DoesNotContain("TYPE=AUDIO", noAudio);
        Assert.DoesNotContain("mp4a", noAudio);
        Assert.Contains("r/video/1080/index.m3u8", noAudio);
    }

    [Fact]
    public void NamesAreQuotedSoTheyCannotBreakTheAttributeList()
    {
        var text = MasterPlaylistWriter.Write(Media([Video720], [Audio(5, "eng", "He said \"hi\"\r\nnext\nline", 2, 128_000)], []), 0, null);
        Assert.Contains("NAME=\"He said 'hi'  next line [5]\",", text);
        Assert.DoesNotContain('\r', text);
        Assert.Equal(1, text.Split('\n').Count(l => l.StartsWith("#EXT-X-MEDIA:TYPE=AUDIO", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("eng", "en")]
    [InlineData("jpn", "ja")]
    [InlineData("zho", "zh")]
    [InlineData("chi", "zh")]
    [InlineData("fra", "fr")]
    [InlineData("fre", "fr")]
    [InlineData("deu", "de")]
    [InlineData("ger", "de")]
    [InlineData("spa", "es")]
    [InlineData("kor", "ko")]
    [InlineData("", "und")]
    [InlineData("por", "por")]
    [InlineData("en", "en")]
    public void LanguageCodesAreMappedToTwoLetterTags(string code, string expected) => Assert.Equal(expected, MasterPlaylistWriter.Language(code));

    [Fact]
    public void OutputUsesLineFeedsAndEndsWithOne()
    {
        var text = MasterPlaylistWriter.Write(Everything(), 0, null);
        Assert.DoesNotContain('\r', text);
        Assert.EndsWith("r/video/1080/index.m3u8\n", text);
        Assert.StartsWith("#EXTM3U\n#EXT-X-VERSION:7\n#EXT-X-INDEPENDENT-SEGMENTS\n", text);
    }

    [Fact]
    public void FrameRateAndNumbersDoNotDependOnTheCurrentCulture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Contains("FRAME-RATE=29.97,", MasterPlaylistWriter.Write(Everything(), 0, null));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
