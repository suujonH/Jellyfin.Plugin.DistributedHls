using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace DistributedHls.Core;

public sealed partial class JobStore
{
    private const string RenditionColumns = "sha256,key,state,job_id,published_job_id,rel_dir,summary,profile";

    // A rendition needs a job when no job holds it and it is missing or published under another encoding profile.
    private const string NeedsJob = "job_id IS NULL AND (state=0 OR (state=2 AND (profile IS NULL OR profile<>$profile)))";

    // Returns whether any rendition was added or removed.
    public bool SyncRenditions(string sha, IReadOnlyList<RenditionSpec> desired, DateTimeOffset now) => Write(cmd =>
    {
        var wanted = desired.Select(s => s.Key).Distinct().ToList();
        var stored = Rows(Sql(cmd, "SELECT key,rel_dir,published_job_id FROM renditions WHERE sha256=$sha ORDER BY key", ("$sha", sha)),
            r => (Key: r.GetString(0), RelDir: Text(r, 1), PublishedJob: Text(r, 2)));
        var changed = false;
        foreach (var row in stored.Where(row => !wanted.Contains(row.Key)))
        {
            if (row.RelDir is not null)
            {
                QueueDeletion(cmd, row.RelDir, now, "档位已取消");
                Sql(cmd, "DELETE FROM job_outputs WHERE job_id=$job AND key=$key", ("$job", row.PublishedJob), ("$key", row.Key)).ExecuteNonQuery();
            }
            Sql(cmd, "DELETE FROM renditions WHERE sha256=$sha AND key=$key", ("$sha", sha), ("$key", row.Key)).ExecuteNonQuery();
            changed = true;
        }
        foreach (var key in wanted.Where(key => stored.All(row => row.Key != key)))
        {
            Sql(cmd, "INSERT INTO renditions(sha256,key,state) VALUES($sha,$key,0)", ("$sha", sha), ("$key", key)).ExecuteNonQuery();
            changed = true;
        }
        return changed;
    });

    public IReadOnlyList<RenditionRow> Renditions(string sha) =>
        Read(cmd => Rows(Sql(cmd, $"SELECT {RenditionColumns} FROM renditions WHERE sha256=$sha ORDER BY key", ("$sha", sha)), ReadRendition));

    public IReadOnlyList<RenditionRow> AllPublished() =>
        Read(cmd => Rows(Sql(cmd, $"SELECT {RenditionColumns} FROM renditions WHERE state=2 ORDER BY sha256,key"), ReadRendition));

    public IReadOnlyList<string> ShasNeedingJobs(string profile) =>
        Read(cmd => Rows(Sql(cmd, $"SELECT DISTINCT sha256 FROM renditions WHERE {NeedsJob} ORDER BY sha256", ("$profile", profile)), r => r.GetString(0)));

    // What a full re-verification goes through, one media at a time.
    public IReadOnlyList<string> PublishedShas() =>
        Read(cmd => Rows(Sql(cmd, "SELECT DISTINCT sha256 FROM renditions WHERE state=2 ORDER BY sha256"), r => r.GetString(0)));

    // The most recently published directories, which an output root that predates its marker must still contain.
    public IReadOnlyList<string> PublishedSample(int limit) => Read(cmd => Rows(Sql(cmd,
        "SELECT rel_dir FROM renditions WHERE state=2 AND rel_dir IS NOT NULL ORDER BY published_at DESC LIMIT $limit", ("$limit", limit)), r => r.GetString(0)));

    // Published renditions joined with the manifest their publishing job submitted; sha null means every media.
    public IReadOnlyList<PublishedManifest> PublishedManifests(string? sha) => Read(cmd => Rows(Sql(cmd, """
        SELECT r.sha256,r.key,r.rel_dir,o.files FROM renditions r JOIN job_outputs o ON o.job_id=r.published_job_id AND o.key=r.key
        WHERE r.state=2 AND ($sha IS NULL OR r.sha256=$sha) ORDER BY r.sha256,r.key
        """, ("$sha", sha)), r => new PublishedManifest(r.GetString(0), r.GetString(1), r.GetString(2), JsonSerializer.Deserialize<OutputFile[]>(r.GetString(3))!)));

    // Only retires the directory that was verified: a rendition published again in the meantime is left alone.
    public bool RetireRendition(string sha, string key, string expectedRelDir, string reason, DateTimeOffset now) => Write(cmd =>
    {
        (string, object?)[] values = [("$sha", sha), ("$key", key), ("$rel", expectedRelDir)];
        var published = Rows(Sql(cmd, "SELECT published_job_id FROM renditions WHERE sha256=$sha AND key=$key AND rel_dir=$rel", values), r => Text(r, 0));
        if (published.Count == 0) return false;
        Sql(cmd, "UPDATE renditions SET state=0,rel_dir=NULL,summary=NULL,published_job_id=NULL WHERE sha256=$sha AND key=$key", values).ExecuteNonQuery();
        QueueDeletion(cmd, expectedRelDir, now, reason);
        Sql(cmd, "DELETE FROM job_outputs WHERE job_id=$job AND key=$key", ("$job", published[0]), ("$key", key)).ExecuteNonQuery();
        return true;
    });

    private static RenditionRow ReadRendition(SqliteDataReader r) =>
        new(r.GetString(0), r.GetString(1), (RenditionState)r.GetInt32(2), OptionalId(r, 3), OptionalId(r, 4), Text(r, 5),
            r.IsDBNull(6) ? null : JsonSerializer.Deserialize<RenditionSummary>(r.GetString(6)), Text(r, 7));
}
