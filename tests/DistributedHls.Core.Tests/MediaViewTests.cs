using Xunit;

namespace DistributedHls.Core.Tests;

public sealed class MediaViewTests
{
    private static PublishedRendition Rendition(string key) => new(key, $"m/{key}/t", new RenditionSummary());

    private static MediaView View(int video, int expectedAudio, int audio) =>
        new(new string('a', 64), Guid.Empty, 60, 0, expectedAudio,
            Enumerable.Range(0, video).Select(i => Rendition($"v{i}")).ToArray(),
            Enumerable.Range(0, audio).Select(i => Rendition($"a{i}")).ToArray(), []);

    [Theory]
    [InlineData(0, 0, 0, false)]    // no video rendition: never playable
    [InlineData(0, 1, 1, false)]
    [InlineData(1, 0, 0, true)]     // a source without audio needs no audio rendition
    [InlineData(1, 0, 1, true)]
    [InlineData(1, 2, 0, false)]    // audio exists in the source but none is published yet
    [InlineData(1, 2, 1, true)]     // one published audio track is enough
    [InlineData(2, 2, 2, true)]
    public void PlayableNeedsVideoAndAudioWhenTheSourceHasAudio(int video, int expectedAudio, int audio, bool playable) =>
        Assert.Equal(playable, View(video, expectedAudio, audio).Playable);
}
