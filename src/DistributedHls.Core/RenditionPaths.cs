using System.Text.RegularExpressions;

namespace DistributedHls.Core;

public static partial class RenditionPaths
{
    // Big-endian, so the "N" text (the media directory name) is the first 32 hex characters of the hash, in lower case.
    public static Guid MediaUuid(string sha) => new(Convert.FromHexString(sha[..32]), bigEndian: true);

    public static string RelativeDirectory(Guid uuid, RenditionSpec spec, string attempt)
    {
        var media = uuid.ToString("N");
        return spec.Kind switch
        {
            RenditionKind.Video => $"{media}/video/{(spec.Video ?? throw new ArgumentException("视频档位缺少质量信息。")).Name}/{attempt}",
            RenditionKind.Audio => $"{media}/audio/{spec.SourceIndex}/{attempt}",
            RenditionKind.Subtitle => $"{media}/subtitles/{spec.SourceIndex}/{attempt}",
            RenditionKind.Preview => $"{media}/preview/{attempt}",
            _ => throw new ArgumentOutOfRangeException(nameof(spec))
        };
    }

    public static string Full(string outputRoot, string rel) => Path.Combine(outputRoot, rel.Replace('/', Path.DirectorySeparatorChar));

    // Gate for every directory that is stored or deleted. ASCII digits only and "\z" instead of "$": "\d" would accept
    // other scripts' digits and "$" a trailing line feed.
    public static bool IsValidRelativeDirectory(string? rel) => rel is not null && ValidDirectory().IsMatch(rel);

    public static string[] RequiredFiles(RenditionKind kind) => kind switch
    {
        RenditionKind.Video or RenditionKind.Audio => ["index.m3u8", "init.mp4"],
        RenditionKind.Subtitle => ["index.m3u8", "subtitle.vtt"],
        RenditionKind.Preview => ["preview.jpg"],
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    [GeneratedRegex(@"^[0-9a-f]{32}/(video/[0-9]{3,4}|audio/[0-9]{1,3}|subtitles/[0-9]{1,3}|preview)/[0-9a-f]{32}\z")]
    private static partial Regex ValidDirectory();
}
