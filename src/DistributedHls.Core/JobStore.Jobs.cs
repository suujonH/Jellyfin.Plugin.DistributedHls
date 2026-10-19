using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace DistributedHls.Core;

public sealed partial class JobStore
{
    private const int MaxClaimRounds = 20;
    private const string LeasedColumns = "id,kind,status,payload,attempt";

    // A pending encode job carries {"Keys":[…]}; from its first claim on it carries the full EncodePlan (also when it is retried).
    private sealed record PendingPayload(string[] Keys);
    private sealed record Candidate(string Id, string WorkKey, JobKind Kind, Guid? ItemId, string? Sha256, string Payload, int Attempt);
    private sealed record LeasedJob(string Id, JobKind Kind, JobStatus Status, string Payload, int Attempt);

    public void EnsureProbeJob(StoredSource s, DateTimeOffset now)
    {
        var file = s.File;
        // A cancelled or completed probe is queued again with the current file (this is only called for a file without a
        // hash, e.g. one whose path changed while its length and time did not); pending, running and failed ones stay as they are
        // (failed probes return on rebuild).
        Write(cmd => Sql(cmd, """
            INSERT INTO jobs(id,work_key,kind,item_id,payload,status,attempt,created_at,updated_at) VALUES($id,$key,0,$item,$payload,0,0,$now,$now)
            ON CONFLICT(work_key) DO UPDATE SET payload=excluded.payload,status=0,attempt=0,error=NULL,lease_token=NULL,lease_until=NULL,updated_at=excluded.updated_at
            WHERE jobs.status IN (3,5)
            """, ("$id", Id(Guid.NewGuid())), ("$key", $"probe:{Id(file.ItemId)}:{file.Length}:{file.LastWriteTimeUtc.Ticks}"),
            ("$item", Id(file.ItemId)), ("$payload", JsonSerializer.Serialize(file)), ("$now", Ms(now))).ExecuteNonQuery());
    }

    // Any job that has not finished. A verifying job (only encode jobs verify) has no lease left for Release or ReapExpiredLeases,
    // so the outputs it submitted are queued for deletion here; a running job keeps its lease for that (see CancelEncodeJob).
    public void CancelJob(Guid id, DateTimeOffset now) => Write(cmd =>
    {
        var job = Id(id);
        if (Sql(cmd, "SELECT status FROM jobs WHERE id=$id", ("$id", job)).ExecuteScalar() is not long status || status is not (0 or 1 or 2)) return;
        if (status == (int)JobStatus.Verifying) DiscardOutputs(cmd, job, now, "任务已取消");
        Cancel(cmd, job, now);
    });

    public void CancelProbeJobs(Guid itemId, DateTimeOffset now) => Write(cmd =>
        Sql(cmd, "UPDATE jobs SET status=5,updated_at=$now WHERE kind=0 AND item_id=$item AND status=0", ("$item", Id(itemId)), ("$now", Ms(now))).ExecuteNonQuery());

    // A job created again (none yet, or Complete/Failed/Cancelled) gets a new id: job_outputs are keyed by (job_id, key) and the
    // published manifests of the previous run stay under the old id. A cancelled job that still holds its lease is left alone like
    // a running one, until Release or ReapExpiredLeases has queued the directories of its worker.
    public Guid? EnsureEncodeJob(string sha, Guid? preferredItem, string profile, DateTimeOffset now) => Write<Guid?>(cmd =>
    {
        var needed = Rows(Sql(cmd, $"SELECT key FROM renditions WHERE sha256=$sha AND {NeedsJob} ORDER BY key", ("$sha", sha), ("$profile", profile)), r => r.GetString(0));
        if (needed.Count == 0) return null;
        var job = Rows(Sql(cmd, "SELECT id,status,payload,lease_until FROM jobs WHERE work_key=$key", ("$key", EncodeWorkKey(sha))),
            r => new { Id = r.GetString(0), Status = (JobStatus)r.GetInt32(1), Payload = r.GetString(2), Leased = !r.IsDBNull(3) }).SingleOrDefault();
        string id;
        if (job?.Status == JobStatus.Pending)
        {
            id = job.Id;
            Sql(cmd, "UPDATE jobs SET payload=$payload,updated_at=$now WHERE id=$id",
                ("$id", id), ("$payload", PendingJson(PayloadKeys(job.Payload).Union(needed))), ("$now", Ms(now))).ExecuteNonQuery();
        }
        else if (job is { Status: JobStatus.Running or JobStatus.Verifying } or { Status: JobStatus.Cancelled, Leased: true })
            return null;
        else
        {
            id = Id(Guid.NewGuid());
            Sql(cmd, """
                INSERT INTO jobs(id,work_key,kind,item_id,sha256,payload,status,attempt,created_at,updated_at) VALUES($id,$key,1,$item,$sha,$payload,0,0,$now,$now)
                ON CONFLICT(work_key) DO UPDATE SET id=excluded.id,item_id=excluded.item_id,payload=excluded.payload,status=0,worker=NULL,
                  lease_token=NULL,lease_until=NULL,attempt=0,error=NULL,created_at=excluded.created_at,updated_at=excluded.updated_at
                """, ("$id", id), ("$key", EncodeWorkKey(sha)), ("$item", preferredItem is { } item ? Id(item) : null), ("$sha", sha),
                ("$payload", PendingJson(needed)), ("$now", Ms(now))).ExecuteNonQuery();
        }
        // Exactly the renditions selected above: the IMMEDIATE transaction keeps them unchanged.
        Sql(cmd, $"UPDATE renditions SET job_id=$id WHERE sha256=$sha AND {NeedsJob}", ("$id", id), ("$sha", sha), ("$profile", profile)).ExecuteNonQuery();
        return Guid.Parse(id);
    });

    // A running job keeps lease_token and lease_until: its worker gets 409 on the next heartbeat, and Release or
    // ReapExpiredLeases still queue its directories.
    public void CancelEncodeJob(string sha, DateTimeOffset now) => Write(cmd =>
    {
        if (Sql(cmd, "SELECT id FROM jobs WHERE work_key=$key AND status IN (0,1)", ("$key", EncodeWorkKey(sha))).ExecuteScalar() is string id) Cancel(cmd, id, now);
    });

    // Called after a hash may have lost its last library file. A general claim never takes the pending encode job of a hash
    // without a file (see NextCandidate), so it is cancelled; EnsureEncodeJob creates it again for a worker that asks for the hash.
    // Returns whether a job was cancelled.
    public bool CancelUnreferencedEncodeJob(string sha, DateTimeOffset now)
    {
        sha = ProbeReader.NormalizeSha256(sha);
        return Write(cmd =>
        {
            if (Sql(cmd, "SELECT id FROM jobs WHERE work_key=$key AND status=0 AND NOT EXISTS(SELECT 1 FROM sources WHERE sources.sha256=$sha)",
                    ("$key", EncodeWorkKey(sha)), ("$sha", sha)).ExecuteScalar() is not string id) return false;
            Cancel(cmd, id, now);
            return true;
        });
    }

    public void CancelAllJobs(DateTimeOffset now) => Write(cmd => Sql(cmd, """
        UPDATE renditions SET job_id=NULL WHERE job_id IN (SELECT id FROM jobs WHERE status IN (0,1));
        UPDATE jobs SET status=5,updated_at=$now WHERE status IN (0,1);
        """, ("$now", Ms(now))).ExecuteNonQuery());

    // Up to 20 candidates per call: jobs found stale on the way are cancelled and the search goes on.
    public JobLease? Claim(ClaimRequest r) => Write<JobLease?>(cmd =>
    {
        for (var round = 0; round < MaxClaimRounds; round++)
        {
            var job = NextCandidate(cmd, r.Sha256);
            if (job is null) return null;
            var token = Guid.NewGuid().ToString("N");
            var payload = job.Kind == JobKind.Probe
                ? (ProbeSourceUnchanged(cmd, job.Payload) ? job.Payload : null)
                : (r.ShaEligible(job.Sha256!) ? PlanPayload(cmd, job, r, token) : null);
            if (payload is null)
            {
                Cancel(cmd, job.Id, r.Now);
                continue;
            }
            var until = DateTimeOffset.FromUnixTimeMilliseconds(Ms(r.Now + r.Lease));
            Sql(cmd, "UPDATE jobs SET status=1,worker=$worker,lease_token=$token,lease_until=$until,attempt=attempt+1,error=NULL,payload=$payload,updated_at=$now WHERE id=$id",
                ("$id", job.Id), ("$worker", r.Worker), ("$token", token), ("$until", Ms(until)), ("$payload", payload), ("$now", Ms(r.Now))).ExecuteNonQuery();
            return new JobLease(Guid.Parse(job.Id), job.WorkKey, job.Kind, job.ItemId, payload, token, until, job.Attempt + 1);
        }
        return null;
    });

    public bool Heartbeat(Guid id, string token, TimeSpan lease, DateTimeOffset now) => Write(cmd =>
        Sql(cmd, "UPDATE jobs SET lease_until=$until WHERE id=$id AND lease_token=$token AND status=1 AND lease_until>$now",
            ("$id", Id(id)), ("$token", token), ("$now", Ms(now)), ("$until", Ms(now + lease))).ExecuteNonQuery() == 1);

    public JobLease? GetLease(Guid id, string token, DateTimeOffset now) => Read(cmd => Rows(Sql(cmd,
        "SELECT work_key,kind,item_id,payload,lease_until,attempt FROM jobs WHERE id=$id AND lease_token=$token AND status=1 AND lease_until>$now",
        ("$id", Id(id)), ("$token", token), ("$now", Ms(now))),
        r => new JobLease(id, r.GetString(0), (JobKind)r.GetInt32(1), OptionalId(r, 2), r.GetString(3), token, DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(4)), r.GetInt32(5))).SingleOrDefault());

    public bool CompleteProbe(Guid id, string token, DateTimeOffset now) => Write(cmd =>
        Sql(cmd, "UPDATE jobs SET status=3,updated_at=$now WHERE id=$id AND lease_token=$token AND status=1 AND lease_until>$now AND kind=0",
            ("$id", Id(id)), ("$token", token), ("$now", Ms(now))).ExecuteNonQuery() == 1);

    public bool SubmitOutputs(Guid id, string token, EncodePlan plan, EncodeCompletion c, DateTimeOffset now) => Write(cmd =>
    {
        var job = Id(id);
        if (Sql(cmd, "UPDATE jobs SET status=2,lease_until=NULL,updated_at=$now WHERE id=$id AND lease_token=$token AND status=1 AND lease_until>$now AND kind=1",
                ("$id", job), ("$token", token), ("$now", Ms(now))).ExecuteNonQuery() == 0) return false;
        foreach (var rendition in c.Renditions)
        {
            // The directory always comes from the server's plan, never from the worker.
            var target = plan.Outputs.FirstOrDefault(o => o.Key == rendition.Key) ?? throw new ArgumentException($"成果物包含任务之外的档位 {rendition.Key}。");
            Sql(cmd, "INSERT INTO job_outputs(job_id,key,rel_dir,summary,files) VALUES($id,$key,$rel,$summary,$files)", ("$id", job), ("$key", rendition.Key),
                ("$rel", target.RelativeDirectory), ("$summary", JsonSerializer.Serialize(rendition.Summary)), ("$files", JsonSerializer.Serialize(rendition.Files))).ExecuteNonQuery();
        }
        return true;
    });

    // Oldest first, read in one statement so every job comes with all of its outputs.
    public IReadOnlyList<VerifyingJob> VerifyingJobs(int limit) => Read(cmd =>
    {
        var rows = Rows(Sql(cmd, """
            SELECT j.id,j.sha256,j.attempt,o.key,o.rel_dir,o.files
            FROM (SELECT id,sha256,attempt,updated_at FROM jobs WHERE status=2 ORDER BY updated_at,id LIMIT $limit) j
            LEFT JOIN job_outputs o ON o.job_id=j.id ORDER BY j.updated_at,j.id,o.key
            """, ("$limit", limit)), r => (Id: r.GetString(0), Sha: r.GetString(1), Attempt: r.GetInt32(2),
            Output: r.IsDBNull(3) ? null : new VerifyingOutput(r.GetString(3), r.GetString(4), JsonSerializer.Deserialize<OutputFile[]>(r.GetString(5))!)));
        return rows.GroupBy(row => (row.Id, row.Sha, row.Attempt))
            .Select(job => new VerifyingJob(Guid.Parse(job.Key.Id), job.Key.Sha, job.Key.Attempt, [.. job.Where(row => row.Output is not null).Select(row => row.Output!)]))
            .ToList();
    });

    // A directory this publication replaces is deleted only after replacedGrace, so a playback session that already has its
    // playlists keeps working; an output that is no longer wanted at all goes at once.
    public PublishResult? Publish(Guid jobId, string profile, DateTimeOffset now, TimeSpan replacedGrace) => Write(cmd =>
    {
        var job = Id(jobId);
        if (Sql(cmd, "SELECT sha256 FROM jobs WHERE id=$id AND status=2", ("$id", job)).ExecuteScalar() is not string sha) return null;
        var outputs = Rows(Sql(cmd, "SELECT key,rel_dir,summary FROM job_outputs WHERE job_id=$id ORDER BY key", ("$id", job)),
            r => (Key: r.GetString(0), RelDir: r.GetString(1), Summary: r.GetString(2)));
        var published = new List<string>();
        foreach (var output in outputs)
        {
            (string, object?)[] values = [("$id", job), ("$sha", sha), ("$key", output.Key), ("$rel", output.RelDir), ("$summary", output.Summary), ("$profile", profile), ("$now", Ms(now))];
            var current = Rows(Sql(cmd, "SELECT job_id,rel_dir,published_job_id FROM renditions WHERE sha256=$sha AND key=$key", values),
                r => new { JobId = Text(r, 0), RelDir = Text(r, 1), PublishedJob = Text(r, 2) }).SingleOrDefault();
            if (current?.JobId == job)
            {
                if (current.RelDir is not null && current.RelDir != output.RelDir)
                {
                    QueueDeletion(cmd, current.RelDir, now + replacedGrace, "已被新成果替换");
                    Sql(cmd, "DELETE FROM job_outputs WHERE job_id=$job AND key=$key", ("$job", current.PublishedJob), ("$key", output.Key)).ExecuteNonQuery();
                }
                Sql(cmd, "UPDATE renditions SET state=2,job_id=NULL,rel_dir=$rel,summary=$summary,profile=$profile,published_job_id=$id,published_at=$now WHERE sha256=$sha AND key=$key",
                    values).ExecuteNonQuery();
                published.Add(output.Key);
            }
            else
            {
                QueueDeletion(cmd, output.RelDir, now, "档位已不再需要");
                Sql(cmd, "DELETE FROM job_outputs WHERE job_id=$id AND key=$key", values).ExecuteNonQuery();
            }
        }
        Sql(cmd, "UPDATE jobs SET status=3,updated_at=$now WHERE id=$id", ("$id", job), ("$now", Ms(now))).ExecuteNonQuery();
        return new PublishResult(sha, published);
    });

    public bool FailVerification(Guid jobId, string error, int maxAttempts, DateTimeOffset now) => Write(cmd =>
    {
        var job = Id(jobId);
        if (Sql(cmd, "SELECT attempt FROM jobs WHERE id=$id AND status=2", ("$id", job)).ExecuteScalar() is not long attempt) return false;
        DiscardOutputs(cmd, job, now, "校验失败");
        Fail(cmd, job, (int)attempt, error, maxAttempts, now);
        return true;
    });

    public bool Release(Guid id, string token, string error, int maxAttempts, DateTimeOffset now) => Write(cmd =>
    {
        var job = Rows(Sql(cmd, $"SELECT {LeasedColumns} FROM jobs WHERE id=$id AND lease_token=$token AND status IN (1,5)", ("$id", Id(id)), ("$token", token)), ReadLeased).SingleOrDefault();
        if (job is null) return false;
        EndLease(cmd, job, "Worker 报告失败", now, error, maxAttempts, now);
        return true;
    });

    // A worker that has not noticed the expiry may still be uploading, so its directories are deleted only after the grace.
    public int ReapExpiredLeases(int maxAttempts, TimeSpan grace, DateTimeOffset now) => Write(cmd =>
    {
        var expired = Rows(Sql(cmd, $"SELECT {LeasedColumns} FROM jobs WHERE status IN (1,5) AND lease_until<=$now ORDER BY lease_until,id", ("$now", Ms(now))), ReadLeased);
        foreach (var job in expired) EndLease(cmd, job, "租约过期", now + grace, "租约过期", maxAttempts, now);
        return expired.Count;
    });

    // On a rebuild a rendition keeps job_id only while that job is Running or Verifying. That releases the renditions held by a Failed
    // job and also those held by an id that no longer exists, because EnsureEncodeJob gives a re-created job a new id.
    public void ResetForRebuild(DateTimeOffset now) => Write(cmd => Sql(cmd, """
        UPDATE renditions SET job_id=NULL WHERE job_id IN (SELECT id FROM jobs WHERE status=0);
        UPDATE jobs SET status=5,updated_at=$now WHERE status=0;
        UPDATE renditions SET state=0,job_id=NULL WHERE state=3;
        UPDATE renditions SET job_id=NULL WHERE job_id IS NOT NULL AND job_id NOT IN (SELECT id FROM jobs WHERE status IN (1,2));
        UPDATE jobs SET status=5,updated_at=$now WHERE status=4 AND kind=0;
        """, ("$now", Ms(now))).ExecuteNonQuery());

    public IReadOnlyList<JobSummary> List(int limit) => Read(cmd => Rows(Sql(cmd,
        "SELECT id,kind,item_id,sha256,status,attempt,worker,error,updated_at FROM jobs ORDER BY updated_at DESC,id LIMIT $limit", ("$limit", limit)),
        r => new JobSummary(Guid.Parse(r.GetString(0)), (JobKind)r.GetInt32(1), OptionalId(r, 2), Text(r, 3), (JobStatus)r.GetInt32(4), r.GetInt32(5), Text(r, 6), Text(r, 7),
            DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(8)))));

    public JobCounts Counts() => Read(cmd => Rows(Sql(cmd, """
        SELECT (SELECT COUNT(*) FROM jobs WHERE status=0),(SELECT COUNT(*) FROM jobs WHERE status=1),(SELECT COUNT(*) FROM jobs WHERE status=2),
               (SELECT COUNT(*) FROM jobs WHERE status=3),(SELECT COUNT(*) FROM jobs WHERE status=4),(SELECT COUNT(*) FROM jobs WHERE status=5),
               (SELECT COUNT(*) FROM deletions),(SELECT COUNT(*) FROM media),(SELECT COUNT(*) FROM renditions WHERE state=2)
        """), r => new JobCounts(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4), r.GetInt32(5), r.GetInt32(6), r.GetInt32(7), r.GetInt32(8))).Single());

    private static string EncodeWorkKey(string sha) => $"encode:{sha}";

    private static string PendingJson(IEnumerable<string> keys) => JsonSerializer.Serialize(new PendingPayload([.. keys]));

    // Keys of an encode payload in either shape.
    private static string[] PayloadKeys(string payload) =>
        JsonSerializer.Deserialize<PendingPayload>(payload)?.Keys ?? [.. JsonSerializer.Deserialize<EncodePlan>(payload)!.Outputs.Select(o => o.Key)];

    // Target directories of a claimed encode job; a pending payload has none yet.
    private static IEnumerable<string> PlannedDirectories(string payload) =>
        JsonSerializer.Deserialize<EncodePlan>(payload)?.Outputs?.Select(o => o.RelativeDirectory) ?? [];

    private static Candidate? NextCandidate(SqliteCommand cmd, string? sha)
    {
        var filter = sha is null ? "(kind=0 OR EXISTS(SELECT 1 FROM sources WHERE sources.sha256=jobs.sha256))" : "kind=1 AND sha256=$sha";
        return Rows(Sql(cmd, $"SELECT id,work_key,kind,item_id,sha256,payload,attempt FROM jobs WHERE status=0 AND {filter} ORDER BY created_at,id LIMIT 1", ("$sha", sha)),
            r => new Candidate(r.GetString(0), r.GetString(1), (JobKind)r.GetInt32(2), OptionalId(r, 3), Text(r, 4), r.GetString(5), r.GetInt32(6))).SingleOrDefault();
    }

    // A probe job is only handed out while the source row still describes the file it was created for.
    private static bool ProbeSourceUnchanged(SqliteCommand cmd, string payload)
    {
        var file = JsonSerializer.Deserialize<SourceFile>(payload)!;
        var stored = Rows(Sql(cmd, $"SELECT {SourceColumns} FROM sources WHERE item_id=$item", ("$item", Id(file.ItemId))), ReadSource).SingleOrDefault()?.File;
        return stored is not null && stored.Path == file.Path && stored.Length == file.Length && stored.LastWriteTimeUtc.Ticks == file.LastWriteTimeUtc.Ticks;
    }

    // The plan covers the payload keys that are still desired and still held by this job, in the order of the desired renditions.
    // Null when nothing is left to encode.
    private static string? PlanPayload(SqliteCommand cmd, Candidate job, ClaimRequest r, string token)
    {
        var sha = job.Sha256!;
        var media = Rows(Sql(cmd, $"SELECT {MediaColumns} FROM media WHERE sha256=$sha", ("$sha", sha)), ReadMedia).SingleOrDefault();
        if (media is null) return null;
        var keys = PayloadKeys(job.Payload);
        var held = Rows(Sql(cmd, "SELECT key FROM renditions WHERE sha256=$sha AND job_id=$id", ("$sha", sha), ("$id", job.Id)), x => x.GetString(0));
        RenditionTarget[] outputs = [.. r.Desired(media).Where(s => keys.Contains(s.Key) && held.Contains(s.Key)).Select(s =>
        {
            var rel = RenditionPaths.RelativeDirectory(media.MediaUuid, s, token);
            return new RenditionTarget(s.Key, s.Kind, RenditionPaths.Full(r.OutputRoot, rel), rel, s.Video, s.SourceIndex, s.Channels);
        })];
        if (outputs.Length == 0) return null;
        // The preferred item's file while it still is this media, otherwise any file of this media, otherwise none.
        var source = Rows(Sql(cmd, $"SELECT {SourceColumns} FROM sources WHERE sha256=$sha ORDER BY item_id IS $item DESC,item_id LIMIT 1",
            ("$sha", sha), ("$item", job.ItemId is { } item ? Id(item) : null)), ReadSource).SingleOrDefault();
        return JsonSerializer.Serialize(new EncodePlan(sha, media.MediaUuid, media.Probe, source?.File, outputs));
    }

    // Queues the directories of every output the job submitted and forgets those outputs.
    private static void DiscardOutputs(SqliteCommand cmd, string id, DateTimeOffset now, string reason)
    {
        foreach (var dir in Rows(Sql(cmd, "SELECT rel_dir FROM job_outputs WHERE job_id=$id ORDER BY key", ("$id", id)), r => r.GetString(0)))
            QueueDeletion(cmd, dir, now, reason);
        Sql(cmd, "DELETE FROM job_outputs WHERE job_id=$id", ("$id", id)).ExecuteNonQuery();
    }

    // Cancels the job and releases its renditions. Lease fields are kept (see CancelEncodeJob).
    private static void Cancel(SqliteCommand cmd, string id, DateTimeOffset now) =>
        Sql(cmd, "UPDATE jobs SET status=5,updated_at=$now WHERE id=$id; UPDATE renditions SET job_id=NULL WHERE job_id=$id;", ("$id", id), ("$now", Ms(now))).ExecuteNonQuery();

    // Release and ReapExpiredLeases: the worker's directories are queued, a cancelled job only loses its lease, a running job fails.
    private static void EndLease(SqliteCommand cmd, LeasedJob job, string reason, DateTimeOffset notBefore, string error, int maxAttempts, DateTimeOffset now)
    {
        if (job.Kind == JobKind.Encode)
            foreach (var dir in PlannedDirectories(job.Payload)) QueueDeletion(cmd, dir, notBefore, reason);
        if (job.Status == JobStatus.Cancelled)
            Sql(cmd, "UPDATE jobs SET lease_token=NULL,lease_until=NULL,updated_at=$now WHERE id=$id", ("$id", job.Id), ("$now", Ms(now))).ExecuteNonQuery();
        else
            Fail(cmd, job.Id, job.Attempt, error, maxAttempts, now);
    }

    // Permanent failure once attempt reaches maxAttempts: the job becomes Failed, its missing renditions become Failed and are
    // released, its published renditions keep job_id so they are not scheduled again until a rebuild. Otherwise it is pending again.
    private static void Fail(SqliteCommand cmd, string id, int attempt, string error, int maxAttempts, DateTimeOffset now)
    {
        (string, object?)[] values = [("$id", id), ("$error", error), ("$now", Ms(now))];
        if (attempt >= maxAttempts)
            Sql(cmd, """
                UPDATE jobs SET status=4,error=$error,lease_token=NULL,lease_until=NULL,updated_at=$now WHERE id=$id;
                UPDATE renditions SET state=3,job_id=NULL WHERE job_id=$id AND state=0;
                """, values).ExecuteNonQuery();
        else
            Sql(cmd, "UPDATE jobs SET status=0,error=$error,lease_token=NULL,lease_until=NULL,updated_at=$now WHERE id=$id", values).ExecuteNonQuery();
    }

    private static LeasedJob ReadLeased(SqliteDataReader r) => new(r.GetString(0), (JobKind)r.GetInt32(1), (JobStatus)r.GetInt32(2), r.GetString(3), r.GetInt32(4));
}
