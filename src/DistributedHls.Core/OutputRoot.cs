namespace DistributedHls.Core;

// Whether the output root can be trusted before anything irreversible is decided from a file or directory missing in it.
// Directory.Exists answers false for a root that cannot be read (EACCES of a protected mount point, ENOTCONN of a dropped FUSE mount,
// ESTALE of NFS), so the root is read once and has to carry the marker or at least one of the directories published last, unless nothing
// is published yet.
public static class OutputRoot
{
    public const string MarkerName = ".distributed-hls";

    // Null when the root is usable, otherwise the reason.
    public static string? Check(string root, IReadOnlyList<string> publishedSample)
    {
        try
        {
            if (!Directory.Exists(root)) return $"输出目录不存在或无法访问：{root}";
            // A real read: EACCES and ENOTCONN surface here, not in Directory.Exists.
            _ = Directory.EnumerateFileSystemEntries(root).Any();
            var marker = Path.Combine(root, MarkerName);
            if (File.Exists(marker)) return null;
            // Nothing published yet: usable, but nothing is written into it, since it may be a mount point that has to stay empty until it is
            // mounted.
            if (publishedSample.Count == 0) return null;
            // Not marked and none of the published directories in it: most likely the mount point without the mount, which must not get a marker.
            if (!publishedSample.Any(rel => Directory.Exists(RenditionPaths.Full(root, rel))))
                return $"输出目录缺少标记文件 {MarkerName}，已发布的成果也不在其中，可能尚未挂载：{root}";
            // The genuine root: the first check after the first publication, or a root that predates the marker.
            File.WriteAllText(marker, "Distributed HLS output root. Do not delete.\n");
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "输出目录不可用：" + ex.Message;
        }
    }
}
