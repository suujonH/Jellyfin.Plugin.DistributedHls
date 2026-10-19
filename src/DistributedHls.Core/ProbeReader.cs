using System.Globalization;
using System.Text.Json;

namespace DistributedHls.Core;

public static class ProbeReader
{
    public static ProbeInfo Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var streams = root.GetProperty("streams").EnumerateArray().ToArray();
        var video = streams.FirstOrDefault(s => Text(s, "codec_type") == "video" && !Flag(s, "attached_pic"));
        if (video.ValueKind == JsonValueKind.Undefined) throw new ArgumentException("未找到视频轨道。");
        var format = root.GetProperty("format");
        var duration = Number(format, "duration");
        var width = Integer(video, "width");
        var height = Integer(video, "height");
        var fps = Rational(Text(video, "avg_frame_rate"));
        if (fps <= 0) fps = Rational(Text(video, "r_frame_rate"));
        if (width <= 0 || height <= 0 || duration <= 0 || fps <= 0 || !double.IsFinite(duration) || !double.IsFinite(fps))
            throw new ArgumentException("视频尺寸、时长和帧率必须为有效正数。");
        var rotation = 0;
        if (video.TryGetProperty("side_data_list", out var sideData))
            foreach (var side in sideData.EnumerateArray())
                if (side.TryGetProperty("rotation", out var value)) rotation = (int)value.GetDouble();
        var (sarNumerator, sarDenominator) = AspectRatio(Text(video, "sample_aspect_ratio"));
        return new(width, height, fps, duration, Text(video, "codec_name"), Text(video, "pix_fmt"), Text(video, "color_transfer"), Text(video, "field_order"), rotation,
            streams.Where(s => Text(s, "codec_type") == "audio").Select(s => new AudioTrack(Integer(s, "index"), Text(s, "codec_name"), Integer(s, "channels"), Integer(s, "sample_rate"), Tag(s, "language"), Tag(s, "title"), Flag(s, "default"))).ToArray(),
            streams.Where(s => Text(s, "codec_type") == "subtitle").Select(s => new SubtitleTrack(Integer(s, "index"), Text(s, "codec_name"), Tag(s, "language"), Tag(s, "title"), Flag(s, "default"))).ToArray(),
            Integer(video, "index"), sarNumerator, sarDenominator);
    }

    public static string NormalizeSha256(string sha256)
    {
        if (sha256.Length != 64 || !sha256.All(char.IsAsciiHexDigit)) throw new ArgumentException("SHA256 必须为 64 位十六进制字符串。");
        return sha256.ToLowerInvariant();
    }

    private static string Text(JsonElement element, string name) => element.TryGetProperty(name, out var value) ? value.ToString() : "";
    private static int Integer(JsonElement element, string name) => int.TryParse(Text(element, name), CultureInfo.InvariantCulture, out var value) ? value : 0;
    private static double Number(JsonElement element, string name) => double.TryParse(Text(element, name), CultureInfo.InvariantCulture, out var value) ? value : 0;
    private static string Tag(JsonElement element, string name) => element.TryGetProperty("tags", out var tags) ? Text(tags, name) : "";
    private static bool Flag(JsonElement element, string name) => element.TryGetProperty("disposition", out var flags) && Integer(flags, name) == 1;
    // ffprobe prints "a:b"; "0:1" and "N/A" mean unknown, so everything that is not two positive integers counts as square pixels.
    private static (int Numerator, int Denominator) AspectRatio(string text)
    {
        var parts = text.Split(':');
        return parts.Length == 2
            && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var numerator) && numerator > 0
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var denominator) && denominator > 0
            ? (numerator, denominator) : (1, 1);
    }

    private static double Rational(string text)
    {
        var parts = text.Split('/');
        if (!double.TryParse(parts[0], CultureInfo.InvariantCulture, out var numerator)) return 0;
        return parts.Length == 2 && double.TryParse(parts[1], CultureInfo.InvariantCulture, out var denominator) ? denominator == 0 ? 0 : numerator / denominator : numerator;
    }
}
