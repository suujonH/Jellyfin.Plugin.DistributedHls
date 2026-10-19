namespace DistributedHls.Core;

public sealed record PublishedRendition(string Key, string RelDir, RenditionSummary Summary);

// Video is ordered by quality descending, Audio and Subtitles by SourceIndex ascending; the callers guarantee the order.
public sealed record MediaView(
    string Sha256, Guid MediaUuid, double DurationSeconds, int VideoIndex, int ExpectedAudio,
    IReadOnlyList<PublishedRendition> Video, IReadOnlyList<PublishedRendition> Audio, IReadOnlyList<PublishedRendition> Subtitles)
{
    public bool Playable => Video.Count > 0 && (ExpectedAudio == 0 || Audio.Count > 0);
}
