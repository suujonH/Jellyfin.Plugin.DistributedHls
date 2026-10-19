using Xunit;

namespace DistributedHls.Core.Tests;

public sealed class RenditionPathsTests
{
    private const string Sha = "00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff";
    private const string Uuid = "00112233445566778899aabbccddeeff";   // MediaUuid(Sha) in "N" format: the first 32 hex characters of Sha
    private const string Attempt = "0123456789abcdef0123456789abcdef";

    [Fact]
    public void MediaUuidIsTheFirstSixteenBytesOfTheHashAndPrintsAsItsHexPrefix()
    {
        var uuid = RenditionPaths.MediaUuid(Sha);
        // Big-endian: the "N" text, which names the media directory, is the first 32 hex characters of the hash.
        // Published directories are named after it: changing this mapping orphans every published rendition.
        Assert.Equal(Uuid, uuid.ToString("N"));
        Assert.Equal(Sha[..32], uuid.ToString("N"));
        Assert.Equal(Convert.FromHexString(Sha[..32]), uuid.ToByteArray(bigEndian: true));
        Assert.Equal(uuid, RenditionPaths.MediaUuid(Sha[..32]));
    }

    [Fact]
    public void MediaUuidIgnoresTheCaseOfTheHashAndPrintsLowerCase()
    {
        var uuid = RenditionPaths.MediaUuid(Sha.ToUpperInvariant());
        Assert.Equal(RenditionPaths.MediaUuid(Sha), uuid);
        Assert.Equal(Sha[..32], uuid.ToString("N"));
    }

    [Theory]
    [InlineData("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")]   // SHA-256 of the empty input
    [InlineData("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")]   // SHA-256 of "abc"
    [InlineData("0102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f20")]
    [InlineData("ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")]
    public void MediaUuidTextIsTheHexPrefixOfAnyHash(string sha) => Assert.Equal(sha[..32], RenditionPaths.MediaUuid(sha).ToString("N"));

    [Fact]
    public void RelativeDirectoryFollowsTheLayoutOfEachKind()
    {
        var uuid = RenditionPaths.MediaUuid(Sha);
        Assert.Equal($"{Uuid}/video/1080/{Attempt}", RenditionPaths.RelativeDirectory(uuid, new("v1080", RenditionKind.Video, new Quality(1080, 1080, 1920, 25), -1, 0), Attempt));
        Assert.Equal($"{Uuid}/audio/3/{Attempt}", RenditionPaths.RelativeDirectory(uuid, new("a3", RenditionKind.Audio, null, 3, 2), Attempt));
        Assert.Equal($"{Uuid}/subtitles/12/{Attempt}", RenditionPaths.RelativeDirectory(uuid, new("s12", RenditionKind.Subtitle, null, 12, 0), Attempt));
        Assert.Equal($"{Uuid}/preview/{Attempt}", RenditionPaths.RelativeDirectory(uuid, new("preview", RenditionKind.Preview, null, -1, 0), Attempt));
    }

    [Fact]
    public void RelativeDirectoryRejectsSpecsThatCannotBePlaced()
    {
        var uuid = RenditionPaths.MediaUuid(Sha);
        Assert.Throws<ArgumentException>(() => RenditionPaths.RelativeDirectory(uuid, new("v1080", RenditionKind.Video, null, -1, 0), Attempt));
        Assert.Throws<ArgumentOutOfRangeException>(() => RenditionPaths.RelativeDirectory(uuid, new("x", (RenditionKind)99, null, -1, 0), Attempt));
    }

    [Fact]
    public void EveryPlannedRenditionGetsAValidDirectory()
    {
        var uuid = RenditionPaths.MediaUuid(Sha);
        var specs = EncodingLadder.Heights.Select(h => new RenditionSpec($"v{h}", RenditionKind.Video, new Quality(h, h, h * 16 / 9, 25), -1, 0))
            .Append(new("a0", RenditionKind.Audio, null, 0, 2)).Append(new("a999", RenditionKind.Audio, null, 999, 2))
            .Append(new("s7", RenditionKind.Subtitle, null, 7, 0)).Append(new("preview", RenditionKind.Preview, null, -1, 0));
        Assert.All(specs, spec => Assert.True(RenditionPaths.IsValidRelativeDirectory(RenditionPaths.RelativeDirectory(uuid, spec, Attempt)), spec.Key));
    }

    [Fact]
    public void FullJoinsTheOutputRootAndTheRelativeDirectoryWithPlatformSeparators() =>
        Assert.Equal(Path.Combine("root", Uuid, "video", "1080", Attempt), RenditionPaths.Full("root", $"{Uuid}/video/1080/{Attempt}"));

    [Theory]
    [InlineData($"{Uuid}/video/1080/{Attempt}")]
    [InlineData($"{Uuid}/video/144/{Attempt}")]
    [InlineData($"{Uuid}/video/2160/{Attempt}")]
    [InlineData($"{Uuid}/audio/0/{Attempt}")]
    [InlineData($"{Uuid}/audio/123/{Attempt}")]
    [InlineData($"{Uuid}/subtitles/5/{Attempt}")]
    [InlineData($"{Uuid}/preview/{Attempt}")]
    public void AcceptsTheDirectoriesTheServerGenerates(string directory) => Assert.True(RenditionPaths.IsValidRelativeDirectory(directory));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("x")]
    [InlineData("00112233445566778899AABBCCDDEEFF/preview/0123456789abcdef0123456789abcdef")]   // upper-case uuid
    [InlineData("00112233445566778899aabbccddeef/preview/0123456789abcdef0123456789abcdef")]    // 31 characters
    [InlineData("00112233445566778899aabbccddeeff0/preview/0123456789abcdef0123456789abcdef")]  // 33 characters
    [InlineData($"{Uuid}/preview/0123456789abcdef0123456789abcde")]                              // attempt of 31 characters
    [InlineData($"{Uuid}/preview/0123456789abcdef0123456789abcdef0")]                            // attempt of 33 characters
    [InlineData($"{Uuid}/preview/0123456789ABCDEF0123456789abcdef")]                             // upper-case attempt
    [InlineData($"{Uuid}/preview/0123456789abcdeg0123456789abcdef")]                             // not hexadecimal
    [InlineData($"{Uuid}/video/12/{Attempt}")]                                                   // quality needs 3 to 4 digits
    [InlineData($"{Uuid}/video/12345/{Attempt}")]
    [InlineData($"{Uuid}/video/abc/{Attempt}")]
    [InlineData($"{Uuid}/audio/1000/{Attempt}")]                                                 // stream index has at most 3 digits
    [InlineData($"{Uuid}/audio//{Attempt}")]
    [InlineData($"{Uuid}/thumbs/1/{Attempt}")]
    [InlineData($"{Uuid}/preview")]
    [InlineData($"{Uuid}/video/1080")]
    [InlineData($"{Uuid}/video/1080/{Attempt}/x")]
    [InlineData($"{Uuid}/video/1080/{Attempt}/..")]
    [InlineData($"../{Uuid}/video/1080/{Attempt}")]
    [InlineData($"/{Uuid}/preview/{Attempt}")]
    [InlineData($"{Uuid}/preview/{Attempt}/")]
    [InlineData($"{Uuid}\\preview\\{Attempt}")]
    [InlineData($"{Uuid}/preview/{Attempt}\n")]                                                  // "$" would let a trailing line feed through
    [InlineData($"{Uuid}/video/١٠٨٠/{Attempt}")]                             // "\d" would accept non-ASCII digits
    public void RejectsEverythingElse(string? directory) => Assert.False(RenditionPaths.IsValidRelativeDirectory(directory));

    [Theory]
    [InlineData(RenditionKind.Video, "index.m3u8,init.mp4")]
    [InlineData(RenditionKind.Audio, "index.m3u8,init.mp4")]
    [InlineData(RenditionKind.Subtitle, "index.m3u8,subtitle.vtt")]
    [InlineData(RenditionKind.Preview, "preview.jpg")]
    public void RequiredFilesDependOnTheKind(RenditionKind kind, string expected) =>
        Assert.Equal(expected, string.Join(",", RenditionPaths.RequiredFiles(kind)));

    [Fact]
    public void RequiredFilesRejectsUnknownKinds() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => RenditionPaths.RequiredFiles((RenditionKind)99));
}
