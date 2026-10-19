namespace DistributedHls.Core;

public static class OutputVerifier
{
    // Returns null when every reported file exists with the reported length (and modified time), otherwise the first problem found.
    public static string? Verify(string dir, IReadOnlyList<OutputFile> files, bool checkMtime)
    {
        if (!Directory.Exists(dir)) return $"目录不存在：{dir}";
        try
        {
            foreach (var file in files)
            {
                var info = new FileInfo(AssetPaths.Resolve(dir, file.Path));
                if (!info.Exists) return $"缺少文件 {file.Path}";
                if (info.Length != file.Length) return $"{file.Path} 长度为 {info.Length}，期望 {file.Length}";
                if (checkMtime && Math.Abs((new DateTimeOffset(info.LastWriteTimeUtc) - file.LastModified).TotalSeconds) > 2) return $"{file.Path} 修改时间不一致";
            }
            return null;
        }
        // An unsafe path (ArgumentException from Resolve) is a failed verification too, never an exception for the caller.
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return $"读取失败：{e.Message}";
        }
    }
}
