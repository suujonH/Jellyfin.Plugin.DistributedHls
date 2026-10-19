using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace DistributedHls.Core;

public sealed partial class JobStore
{
    private const string MediaColumns = "sha256,media_uuid,library_id,probe,orphaned_at";

    // Media submitted without an item start as orphans; associating an item clears that. A SHA keeps its first probe forever: a different
    // probe is refused, or with keepExistingProbe ignored while the item is still associated (set for a probe the server dispatched,
    // where another FFprobe build may describe the same file differently). The returned record always carries the stored probe.
    public MediaRecord AssociateProbe(Guid? itemId, string sha, ProbeInfo probe, Guid libraryId, DateTimeOffset now, bool keepExistingProbe = false)
    {
        sha = ProbeReader.NormalizeSha256(sha);
        var json = JsonSerializer.Serialize(probe);
        return Write(cmd =>
        {
            if (Sql(cmd, "SELECT probe FROM media WHERE sha256=$sha", ("$sha", sha)).ExecuteScalar() is string stored)
            {
                if (stored != json && !keepExistingProbe) throw new ProbeMismatchException("同一 SHA256 的媒体信息与已保存的信息不一致");
            }
            else
                Sql(cmd, "INSERT INTO media(sha256,media_uuid,library_id,probe,orphaned_at,created_at) VALUES($sha,$uuid,$library,$probe,$orphaned,$now)",
                    ("$sha", sha), ("$uuid", Id(RenditionPaths.MediaUuid(sha))), ("$library", Id(libraryId)), ("$probe", json),
                    ("$orphaned", itemId is null ? Ms(now) : null), ("$now", Ms(now))).ExecuteNonQuery();
            if (itemId is { } item)
            {
                if (Sql(cmd, "UPDATE sources SET sha256=$sha WHERE item_id=$item", ("$sha", sha), ("$item", Id(item))).ExecuteNonQuery() == 0)
                    throw new KeyNotFoundException("素材尚未登记");
                Sql(cmd, "UPDATE media SET orphaned_at=NULL WHERE sha256=$sha", ("$sha", sha)).ExecuteNonQuery();
            }
            return Rows(Sql(cmd, $"SELECT {MediaColumns} FROM media WHERE sha256=$sha", ("$sha", sha)), ReadMedia).Single();
        });
    }

    public MediaRecord? GetMedia(string sha) =>
        Read(cmd => Rows(Sql(cmd, $"SELECT {MediaColumns} FROM media WHERE sha256=$sha", ("$sha", sha)), ReadMedia).SingleOrDefault());

    public IReadOnlyList<string> AllMediaShas() => Read(cmd => Rows(Sql(cmd, "SELECT sha256 FROM media ORDER BY sha256"), r => r.GetString(0)));

    public void RefreshOrphans(DateTimeOffset now) => Write(cmd => Sql(cmd, """
        UPDATE media SET orphaned_at=$now WHERE orphaned_at IS NULL AND NOT EXISTS(SELECT 1 FROM sources WHERE sources.sha256=media.sha256);
        UPDATE media SET orphaned_at=NULL WHERE orphaned_at IS NOT NULL AND EXISTS(SELECT 1 FROM sources WHERE sources.sha256=media.sha256);
        """, ("$now", Ms(now))).ExecuteNonQuery());

    // Only a job that may still produce or own outputs keeps an expired orphan: Running, Verifying, or Cancelled while its worker
    // still holds the lease (Release or ReapExpiredLeases must still find it to queue its directories). Pending jobs own nothing
    // and are deleted with the media; without a library file a general claim would never take them anyway.
    public IReadOnlyList<string> DeleteExpiredOrphans(DateTimeOffset cutoff, DateTimeOffset now) => Write(cmd =>
    {
        var shas = Rows(Sql(cmd, """
            SELECT sha256 FROM media WHERE orphaned_at<=$cutoff
              AND NOT EXISTS(SELECT 1 FROM sources WHERE sources.sha256=media.sha256)
              AND NOT EXISTS(SELECT 1 FROM jobs WHERE jobs.sha256=media.sha256 AND (jobs.status IN (1,2) OR (jobs.status=5 AND jobs.lease_until IS NOT NULL)))
            ORDER BY sha256
            """, ("$cutoff", Ms(cutoff))), r => r.GetString(0));
        foreach (var sha in shas)
        {
            foreach (var dir in Rows(Sql(cmd, "SELECT rel_dir FROM renditions WHERE sha256=$sha AND rel_dir IS NOT NULL ORDER BY key", ("$sha", sha)), r => r.GetString(0)))
                QueueDeletion(cmd, dir, now, "孤儿媒体");
            // Published manifests may belong to an earlier run of the job: a re-created job gets a new id (see EnsureEncodeJob).
            Sql(cmd, """
                DELETE FROM job_outputs WHERE job_id IN (SELECT id FROM jobs WHERE sha256=$sha UNION SELECT published_job_id FROM renditions WHERE sha256=$sha);
                DELETE FROM jobs WHERE sha256=$sha AND status IN (0,3,4,5);
                DELETE FROM renditions WHERE sha256=$sha;
                DELETE FROM media WHERE sha256=$sha;
                """, ("$sha", sha)).ExecuteNonQuery();
        }
        return shas;
    });

    private static MediaRecord ReadMedia(SqliteDataReader r) =>
        new(r.GetString(0), Guid.Parse(r.GetString(1)), Guid.Parse(r.GetString(2)), JsonSerializer.Deserialize<ProbeInfo>(r.GetString(3))!,
            r.IsDBNull(4) ? null : DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(4)));
}
