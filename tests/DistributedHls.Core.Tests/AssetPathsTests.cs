using Xunit;

namespace DistributedHls.Core.Tests;

public sealed class AssetPathsTests
{
    private static readonly ProbeInfo Probe = new(1920, 1080, 25, 60, "h264", "yuv420p", "bt709", "progressive", 0, [], []);

    private static EncodePlan Plan() => new(new string('a', 64), Guid.Empty, Probe, null,
    [
        new("v1080", RenditionKind.Video, "/out/v", "m/video/1080/t", new Quality(1080, 1080, 1920, 25)),
        new("a1", RenditionKind.Audio, "/out/a", "m/audio/1/t", null, 1, 2),
        new("s3", RenditionKind.Subtitle, "/out/s", "m/subtitles/3/t", null, 3),
        new("preview", RenditionKind.Preview, "/out/p", "m/preview/t")
    ]);

    private static OutputFile Entry(string path, long length = 10) => new(path, length, DateTimeOffset.UnixEpoch);

    private static RenditionOutput[] Renditions() =>
    [
        new("v1080", new RenditionSummary(Quality: 1080, Width: 1920, Height: 1080, FramesPerSecond: 25, Bandwidth: 5_000_000, AverageBandwidth: 4_000_000, Codec: "avc1.640028"),
            [Entry("index.m3u8"), Entry("init.mp4"), Entry("segment-000001.m4s")]),
        new("a1", new RenditionSummary(SourceIndex: 1, Language: "en", Channels: 2, Bandwidth: 192_000),
            [Entry("index.m3u8"), Entry("init.mp4"), Entry("segment-000001.m4s")]),
        new("s3", new RenditionSummary(SourceIndex: 3, Language: "en"), [Entry("index.m3u8"), Entry("subtitle.vtt")]),
        new("preview", new RenditionSummary(), [Entry("preview.jpg")])
    ];

    private static EncodeCompletion Mutate(string key, Func<RenditionOutput, RenditionOutput> change) =>
        new("token", Renditions().Select(r => r.Key == key ? change(r) : r).ToArray());

    private static EncodeCompletion WithSummary(string key, Func<RenditionSummary, RenditionSummary> change) =>
        Mutate(key, r => r with { Summary = change(r.Summary) });

    private static EncodeCompletion WithFiles(string key, Func<OutputFile[], OutputFile[]> change) =>
        Mutate(key, r => r with { Files = change(r.Files) });

    private static void AssertRejected(EncodeCompletion completion, string? mention = null)
    {
        var error = Assert.Throws<ArgumentException>(() => AssetPaths.ValidateCompletion(Plan(), completion));
        if (mention is not null) Assert.Contains(mention, error.Message);
    }

    [Fact]
    public void AcceptsACompletionThatMatchesThePlan() =>
        AssetPaths.ValidateCompletion(Plan(), new("token", Renditions()));

    [Fact]
    public void RejectsAMissingRendition() =>
        AssertRejected(new("token", Renditions().Where(r => r.Key != "a1").ToArray()), "a1");

    [Fact]
    public void RejectsARenditionThatIsNotInThePlan() =>
        AssertRejected(new("token", [.. Renditions(), new("v720", new RenditionSummary(), [Entry("index.m3u8")])]), "v720");

    [Fact]
    public void RejectsADifferentKeySetOfTheSameSize() =>
        AssertRejected(new("token", Renditions().Select(r => r.Key == "preview" ? r with { Key = "thumbnail" } : r).ToArray()), "thumbnail");

    [Fact]
    public void RejectsDuplicateRenditions() =>
        AssertRejected(new("token", [.. Renditions(), Renditions()[3]]), "preview");

    [Fact]
    public void RejectsAnEmptyOrMissingRenditionList()
    {
        AssertRejected(new("token", []));
        AssertRejected(new("token", null!));
    }

    [Theory]
    [InlineData("v1080", "index.m3u8")]
    [InlineData("v1080", "init.mp4")]
    [InlineData("a1", "index.m3u8")]
    [InlineData("a1", "init.mp4")]
    [InlineData("s3", "index.m3u8")]
    [InlineData("s3", "subtitle.vtt")]
    [InlineData("preview", "preview.jpg")]
    public void RejectsARenditionWithoutItsRequiredFile(string key, string required) =>
        AssertRejected(WithFiles(key, files => [.. files.Where(f => f.Path != required), Entry("other.bin")]), required);

    [Fact]
    public void RequiredFilesMustMatchTheExactSpelling() =>
        // The server later opens "index.m3u8"; on a case-sensitive file system "INDEX.M3U8" would be a dead link.
        AssertRejected(WithFiles("v1080", files => [Entry("INDEX.M3U8"), .. files.Where(f => f.Path != "index.m3u8")]), "index.m3u8");

    [Fact]
    public void RejectsAnEmptyOrMissingFileList()
    {
        AssertRejected(WithFiles("v1080", _ => []), "v1080");
        AssertRejected(Mutate("v1080", r => r with { Files = null! }), "v1080");
    }

    [Fact]
    public void AcceptsExactlyOneHundredThousandFilesButNotMore()
    {
        static OutputFile[] Many(int count) => [Entry("index.m3u8"), Entry("init.mp4"), .. Enumerable.Range(1, count - 2).Select(i => Entry($"segment-{i:000000}.m4s"))];
        AssetPaths.ValidateCompletion(Plan(), WithFiles("v1080", _ => Many(100000)));
        AssertRejected(WithFiles("v1080", _ => Many(100001)), "v1080");
    }

    [Theory]
    [InlineData("../escape.bin")]
    [InlineData("a/../escape.bin")]
    [InlineData("/etc/passwd")]
    [InlineData("a\\b.bin")]
    [InlineData("C:/windows/x.bin")]
    [InlineData("a//b.bin")]
    [InlineData("./a.bin")]
    [InlineData("a/")]
    [InlineData("")]
    [InlineData(" ")]
    public void RejectsFilePathsThatLeaveTheRenditionDirectory(string path) =>
        AssertRejected(WithFiles("v1080", files => [.. files, Entry(path)]), "v1080");

    [Fact]
    public void RejectsNegativeLengths() =>
        AssertRejected(WithFiles("v1080", files => [.. files, Entry("segment-000002.m4s", -1)]), "segment-000002.m4s");

    [Fact]
    public void RejectsFilesThatDifferOnlyByCase() =>
        AssertRejected(WithFiles("v1080", files => [.. files, Entry("SEGMENT-000001.M4S")]), "SEGMENT-000001.M4S");

    [Fact]
    public void RejectsAVideoSummaryThatDiffersFromTheTarget()
    {
        AssertRejected(WithSummary("v1080", s => s with { Quality = 720 }), "v1080");
        AssertRejected(WithSummary("v1080", s => s with { Width = 1280 }), "v1080");
        AssertRejected(WithSummary("v1080", s => s with { Height = 720 }), "v1080");
    }

    [Fact]
    public void RejectsAVideoWithoutBandwidth()
    {
        AssertRejected(WithSummary("v1080", s => s with { Bandwidth = 0 }), "v1080");
        AssertRejected(WithSummary("v1080", s => s with { AverageBandwidth = 0 }), "v1080");
        AssertRejected(WithSummary("v1080", s => s with { Bandwidth = -1 }), "v1080");
    }

    [Theory]
    [InlineData("avc1.640028")]
    [InlineData("avc1.64001F")]
    [InlineData("avc1.64001f")]
    [InlineData("avc1.42E01E")]
    public void AcceptsAvcCodecStrings(string codec) =>
        AssetPaths.ValidateCompletion(Plan(), WithSummary("v1080", s => s with { Codec = codec }));

    [Theory]
    [InlineData("")]
    [InlineData("hvc1.1.6.L93.B0")]
    [InlineData("avc1.64002")]
    [InlineData("avc1.6400280")]
    [InlineData("avc1.64002g")]
    [InlineData("AVC1.640028")]
    [InlineData("avc1.640028\n")]
    [InlineData("mp4a.40.2")]
    public void RejectsOtherCodecStrings(string codec) =>
        AssertRejected(WithSummary("v1080", s => s with { Codec = codec }), "v1080");

    [Fact]
    public void RejectsAnAudioSummaryThatDiffersFromTheTarget()
    {
        AssertRejected(WithSummary("a1", s => s with { SourceIndex = 2 }), "a1");
        AssertRejected(WithSummary("a1", s => s with { Channels = 0 }), "a1");
        AssertRejected(WithSummary("a1", s => s with { Channels = 3 }), "a1");
        AssertRejected(WithSummary("a1", s => s with { Bandwidth = 0 }), "a1");
    }

    [Fact]
    public void AcceptsMonoAudio() =>
        AssetPaths.ValidateCompletion(Plan(), WithSummary("a1", s => s with { Channels = 1 }));

    [Fact]
    public void RejectsASubtitleSummaryThatDiffersFromTheTarget() =>
        AssertRejected(WithSummary("s3", s => s with { SourceIndex = 4 }), "s3");

    [Fact]
    public void PreviewSummaryIsNotConstrained() =>
        AssetPaths.ValidateCompletion(Plan(), WithSummary("preview", s => s with { Width = 480, Height = 270, Codec = "whatever" }));

    [Fact]
    public void RejectsAMissingSummary() =>
        AssertRejected(Mutate("a1", r => r with { Summary = null! }), "a1");

    [Fact]
    public void ResolveStillRejectsEverythingOutsideTheDirectory()
    {
        Assert.Equal(Path.Combine("root", "a", "b.m4s"), AssetPaths.Resolve("root", "a/b.m4s"));
        Assert.Throws<ArgumentException>(() => AssetPaths.Resolve("root", "../b"));
    }
}
