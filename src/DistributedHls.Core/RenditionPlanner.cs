namespace DistributedHls.Core;

public sealed record RenditionSpec(string Key, RenditionKind Kind, Quality? Video, int SourceIndex, int Channels);

public static class EncodingProfile
{
    // Must change together with any encoding parameter or ladder rule; renditions published under another profile are encoded again.
    public const string Version = "h264-aac-sdr-v3";
}

public static class RenditionPlanner
{
    // Only text subtitles can be converted to WebVTT; graphic ones (pgs, dvd, dvb) are skipped.
    public static readonly string[] TextSubtitleCodecs = ["subrip", "srt", "ass", "ssa", "webvtt", "mov_text", "text"];

    public static IReadOnlyList<RenditionSpec> Desired(ProbeInfo p, IEnumerable<int> qualities)
    {
        var video = EncodingLadder.Select(p, qualities);
        // Without a video rendition nothing is encoded at all.
        if (video.Length == 0) return [];
        return
        [
            .. video.Select(q => new RenditionSpec($"v{q.Name}", RenditionKind.Video, q, -1, 0)),
            .. p.Audio.Select(a => new RenditionSpec($"a{a.Index}", RenditionKind.Audio, null, a.Index, Math.Clamp(a.Channels, 1, 2))),
            .. p.Subtitles.Where(s => TextSubtitleCodecs.Contains(s.Codec)).Select(s => new RenditionSpec($"s{s.Index}", RenditionKind.Subtitle, null, s.Index, 0)),
            new RenditionSpec("preview", RenditionKind.Preview, null, -1, 0)
        ];
    }
}
