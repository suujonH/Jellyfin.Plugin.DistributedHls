using System.Globalization;
using System.Text;

namespace DistributedHls.Core;

// Ported from the Worker's PlaylistWriter.Master; renditions come from published summaries and URIs are relative to the token directory.
public static class MasterPlaylistWriter
{
    // quality 0 lists every published video rendition, audioIndex null every published audio track.
    public static string Write(MediaView m, int quality, int? audioIndex)
    {
        var variants = m.Video.Select(v => v.Summary).Where(s => quality == 0 || s.Quality == quality).ToArray();
        var audio = m.Audio.Select(a => a.Summary).Where(s => audioIndex is null || s.SourceIndex == audioIndex).ToArray();
        var subtitles = m.Subtitles.Select(s => s.Summary).ToArray();
        var text = new StringBuilder("#EXTM3U\n#EXT-X-VERSION:7\n#EXT-X-INDEPENDENT-SEGMENTS\n");
        var defaultAudio = audioIndex ?? audio.FirstOrDefault(a => a.Default)?.SourceIndex ?? audio.FirstOrDefault()?.SourceIndex;
        foreach (var track in audio)
            text.AppendLine($"#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"audio\",NAME=\"{Quote(track.Title.Length == 0 ? $"音轨 {track.SourceIndex}" : track.Title)} [{track.SourceIndex}]\",LANGUAGE=\"{Quote(Language(track.Language))}\",DEFAULT={(track.SourceIndex == defaultAudio ? "YES" : "NO")},AUTOSELECT=YES,CHANNELS=\"{track.Channels}\",URI=\"r/audio/{track.SourceIndex}/index.m3u8\"");
        foreach (var subtitle in subtitles)
            text.AppendLine($"#EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID=\"subs\",NAME=\"{Quote(subtitle.Title.Length == 0 ? $"字幕 {subtitle.SourceIndex}" : subtitle.Title)} [{subtitle.SourceIndex}]\",LANGUAGE=\"{Quote(Language(subtitle.Language))}\",DEFAULT=NO,AUTOSELECT=YES,FORCED=NO,URI=\"r/subtitles/{subtitle.SourceIndex}/index.m3u8\"");
        var audioRate = audio.Length == 0 ? 0 : audio.Max(a => a.Bandwidth);
        foreach (var variant in variants.OrderBy(v => v.Bandwidth))
        {
            text.Append($"#EXT-X-STREAM-INF:BANDWIDTH={variant.Bandwidth + audioRate},AVERAGE-BANDWIDTH={variant.AverageBandwidth + audioRate},RESOLUTION={variant.Width}x{variant.Height},FRAME-RATE={variant.FramesPerSecond.ToString("0.###", CultureInfo.InvariantCulture)},CODECS=\"{variant.Codec}{(audio.Length == 0 ? "" : ",mp4a.40.2")}\"");
            if (audio.Length > 0) text.Append(",AUDIO=\"audio\"");
            if (subtitles.Length > 0) text.Append(",SUBTITLES=\"subs\"");
            text.AppendLine().AppendLine($"r/video/{variant.Quality}/index.m3u8");
        }
        return text.ToString().Replace("\r\n", "\n");
    }

    public static string Language(string code) => code switch
    {
        "eng" => "en", "jpn" => "ja", "zho" or "chi" => "zh", "fra" or "fre" => "fr", "deu" or "ger" => "de", "spa" => "es", "kor" => "ko", "" => "und", _ => code
    };

    private static string Quote(string value) => value.Replace('"', '\'').Replace('\r', ' ').Replace('\n', ' ');
}
