namespace DistributedHls.Core;

public static class AssetPaths
{
    private const int MaxFilesPerRendition = 100000;

    public static string Resolve(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Contains('\\') || relative.Contains(':') || relative.StartsWith('/') || relative.Split('/').Any(p => p is "" or "." or ".."))
            throw new ArgumentException("成果物路径必须为目录内的相对路径。");
        return Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    }

    public static void ValidateCompletion(EncodePlan plan, EncodeCompletion c)
    {
        if (c.Renditions is null) throw new ArgumentException("成果物缺少档位列表。");
        var targets = plan.Outputs.ToDictionary(o => o.Key);
        var reported = new HashSet<string>();
        foreach (var rendition in c.Renditions)
        {
            if (rendition.Key is null || !targets.ContainsKey(rendition.Key)) throw new ArgumentException($"成果物包含任务之外的档位 {rendition.Key}。");
            if (!reported.Add(rendition.Key)) throw new ArgumentException($"成果物档位 {rendition.Key} 重复。");
        }
        foreach (var key in targets.Keys)
            if (!reported.Contains(key)) throw new ArgumentException($"成果物缺少档位 {key}。");
        foreach (var rendition in c.Renditions)
        {
            var target = targets[rendition.Key];
            ValidateFiles(rendition, target.Kind);
            ValidateSummary(rendition, target);
        }
    }

    private static void ValidateFiles(RenditionOutput rendition, RenditionKind kind)
    {
        var files = rendition.Files;
        if (files is not { Length: > 0 and <= MaxFilesPerRendition }) throw new ArgumentException($"档位 {rendition.Key} 的文件数量不正确。");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            try { Resolve(".", file.Path); }
            catch (ArgumentException) { throw new ArgumentException($"档位 {rendition.Key} 的文件路径不合法：{file.Path}。"); }
            if (file.Length < 0 || !names.Add(file.Path)) throw new ArgumentException($"档位 {rendition.Key} 的文件列表不正确：{file.Path}。");
        }
        // Required names are matched exactly: the server opens them with this spelling.
        foreach (var required in RenditionPaths.RequiredFiles(kind))
            if (!files.Any(f => f.Path == required)) throw new ArgumentException($"档位 {rendition.Key} 缺少 {required}。");
    }

    private static void ValidateSummary(RenditionOutput rendition, RenditionTarget target)
    {
        var summary = rendition.Summary ?? throw new ArgumentException($"档位 {rendition.Key} 缺少摘要。");
        switch (target.Kind)
        {
            case RenditionKind.Video:
                if (target.Video is not { } video || summary.Quality != video.Name || summary.Width != video.Width || summary.Height != video.Height)
                    throw new ArgumentException($"视频档位 {rendition.Key} 的质量或尺寸与任务不一致。");
                if (summary.Bandwidth <= 0 || summary.AverageBandwidth <= 0) throw new ArgumentException($"视频档位 {rendition.Key} 的码率必须大于 0。");
                if (!IsAvcCodec(summary.Codec)) throw new ArgumentException($"视频档位 {rendition.Key} 的编码标识不正确：{summary.Codec}。");
                break;
            case RenditionKind.Audio:
                if (summary.SourceIndex != target.SourceIndex) throw new ArgumentException($"音轨档位 {rendition.Key} 的轨道索引与任务不一致。");
                if (summary.Channels is < 1 or > 2 || summary.Bandwidth <= 0) throw new ArgumentException($"音轨档位 {rendition.Key} 的声道数或码率不正确。");
                break;
            case RenditionKind.Subtitle:
                if (summary.SourceIndex != target.SourceIndex) throw new ArgumentException($"字幕档位 {rendition.Key} 的轨道索引与任务不一致。");
                break;
        }
    }

    // "avc1." followed by exactly six hexadecimal digits (profile, constraints, level).
    private static bool IsAvcCodec(string? codec) => codec is { Length: 11 } && codec.StartsWith("avc1.", StringComparison.Ordinal) && codec[5..].All(char.IsAsciiHexDigit);
}
