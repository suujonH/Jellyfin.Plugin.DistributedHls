using System.Text.Json;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DistributedHls.Core.Tests;

public sealed class JobStoreTests : IDisposable
{
    private const string Profile = EncodingProfile.Version;
    private const string NextProfile = "next-profile";
    private const int MaxAttempts = 3;
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);
    // What the verification service passes to Publish: the lifetime of a playback grant.
    private static readonly TimeSpan ReplacedGrace = TimeSpan.FromHours(24);
    private static readonly int[] Qualities = [1080, 720];
    private static readonly Guid Library = Guid.Parse("0123456789abcdef0123456789abcdef");
    private static readonly DateTime Modified = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
    // One audio track (index 1), one text subtitle (index 2) and one graphic subtitle (index 3, never planned).
    private static readonly ProbeInfo Probe = new(1920, 1080, 25, 60, "h264", "yuv420p", "bt709", "progressive", 0,
        [new(1, "aac", 6, 48000, "eng", "", true)], [new(2, "subrip", "eng", "", false), new(3, "hdmv_pgs_subtitle", "eng", "", false)]);
    // RenditionPlanner.Desired(Probe, Qualities), in planner order.
    private static readonly string[] PlannedKeys = ["v1080", "v720", "a1", "s2", "preview"];
    private static readonly string[] Version2Tables = ["deletions", "job_outputs", "jobs", "legacy_items", "media", "renditions", "sources", "version_items"];

    // The last schema written by version 1 (JobStore.cs before the redesign).
    private const string Version1Schema = """
        PRAGMA journal_mode=WAL;
        CREATE TABLE IF NOT EXISTS jobs (
            id TEXT PRIMARY KEY, work_key TEXT NOT NULL UNIQUE, kind INTEGER NOT NULL, item_id TEXT NOT NULL,
            payload TEXT NOT NULL, status INTEGER NOT NULL DEFAULT 0, worker TEXT,
            lease_token TEXT, lease_until INTEGER, attempt INTEGER NOT NULL DEFAULT 0, error TEXT,
            created_at INTEGER NOT NULL);
        CREATE INDEX IF NOT EXISTS ix_jobs_pending ON jobs(status, created_at);
        CREATE TABLE IF NOT EXISTS probes (sha256 TEXT PRIMARY KEY, probe TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS sources (item_id TEXT PRIMARY KEY, library_id TEXT NOT NULL, path TEXT NOT NULL,
            length INTEGER NOT NULL, modified_ticks INTEGER NOT NULL, sha256 TEXT);
        CREATE TABLE IF NOT EXISTS assets (sha256 TEXT PRIMARY KEY, root_path TEXT NOT NULL, metadata TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS published_assets (item_id TEXT PRIMARY KEY, generation TEXT NOT NULL, root_path TEXT NOT NULL, metadata TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS retired_assets (root_path TEXT PRIMARY KEY, metadata TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS retired_directories (root_path TEXT PRIMARY KEY);
        """;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "distributed-hls-tests", Guid.NewGuid().ToString("N"));
    private readonly TestClock _clock = new();

    private string DatabasePath => Path.Combine(_directory, "jobs.db");
    private string OutputRoot => Path.Combine(_directory, "output");
    private DateTimeOffset Now => _clock.Now;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    // ---- Schema

    [Fact]
    public void CreatesTheVersion2SchemaAndReopensWithoutChangingIt()
    {
        var store = Create();
        Assert.Equal(2L, Scalar("PRAGMA user_version"));
        Assert.Equal("wal", Scalar("PRAGMA journal_mode"));
        Assert.Equal(Version2Tables, Tables());
        var item = AddMedia(store, Sha('a'));
        var reopened = Create();
        Assert.Equal(Sha('a'), reopened.GetSource(item)!.Sha256);
        Assert.Equal(2L, Scalar("PRAGMA user_version"));
        Assert.Empty(reopened.LegacyItems());
    }

    [Fact]
    public void MigratesAVersion1DatabaseKeepingOnlyItsItemIds()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        Directory.CreateDirectory(_directory);
        Execute(Version1Schema);
        Execute("""
            INSERT INTO sources(item_id,library_id,path,length,modified_ticks,sha256) VALUES($first,$library,'/media/a.mkv',1,1,NULL),($second,$library,'/media/b.mkv',2,2,$sha);
            INSERT INTO published_assets(item_id,generation,root_path,metadata) VALUES($second,'generation','/cache/hls/b','{}');
            INSERT INTO jobs(id,work_key,kind,item_id,payload,created_at) VALUES('job','encode:b',1,$second,'{}',0);
            """, ("$first", first.ToString("N")), ("$second", second.ToString("N")), ("$library", Library.ToString("N")), ("$sha", Sha('b')));
        var store = Create();
        Assert.Equal(ByText(first, second), store.LegacyItems());
        Assert.Equal(2L, Scalar("PRAGMA user_version"));
        // probes, assets, published_assets and retired_* are gone; jobs and sources exist again in the version 2 shape, empty.
        Assert.Equal(Version2Tables, Tables());
        Assert.Empty(store.Sources());
        Assert.Empty(store.List(10));
        store.RemoveLegacyItem(first);
        Assert.Equal([second], Create().LegacyItems());   // opening again does not migrate again
    }

    [Fact]
    public void AFileThatIsNotADatabaseLeavesTheStoreUnusableInsteadOfThrowing()
    {
        Directory.CreateDirectory(_directory);
        var garbage = Enumerable.Range(0, 4096).Select(i => (byte)(i * 7 + 3)).ToArray();
        File.WriteAllBytes(DatabasePath, garbage);
        var store = Create();
        Assert.StartsWith($"无法打开 HLS 数据库 {DatabasePath}：", store.InitError);
        // Every method fails with the reason, reads and writes alike.
        Assert.Equal(store.InitError, Assert.Throws<InvalidOperationException>(() => store.Sources()).Message);
        Assert.Equal(store.InitError, Assert.Throws<InvalidOperationException>(() => store.Counts()).Message);
        Assert.Throws<InvalidOperationException>(() => store.UpsertSources([LibraryFile(Guid.NewGuid())]));
        Assert.Throws<InvalidOperationException>(() => store.AssociateProbe(null, Sha('a'), Probe, Library, Now));
        Assert.Throws<InvalidOperationException>(() => store.CancelUnreferencedEncodeJob(Sha('a'), Now));
        SqliteConnection.ClearAllPools();   // the pooled connection of the constructor keeps the file open for writing
        Assert.Equal(garbage, File.ReadAllBytes(DatabasePath));   // nothing was written over the file
    }

    [Fact]
    public void ADatabaseOfANewerSchemaIsReportedAndLeftUntouched()
    {
        Directory.CreateDirectory(_directory);
        // A table named like one of version 2: creating the version 2 schema over it would fail, and drop nothing.
        Execute("CREATE TABLE sources(item_id TEXT PRIMARY KEY, future TEXT); INSERT INTO sources VALUES('x','y'); PRAGMA user_version=3;");
        var store = Create();
        Assert.Equal("HLS 数据库版本 3 高于本插件支持的 2，可能装回了旧版本插件", store.InitError);
        Assert.Equal(store.InitError, Assert.Throws<InvalidOperationException>(() => store.Sources()).Message);
        Assert.Equal(3L, Scalar("PRAGMA user_version"));
        Assert.Equal(["sources"], Tables());
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM sources WHERE item_id='x' AND future='y'"));
    }

    // ---- Sources

    [Fact]
    public void UpsertSourcesReportsWhatChangedForEachFileInInputOrder()
    {
        var store = Create();
        Guid[] items = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
        Assert.Equal(items.Select(item => new UpsertResult(item, SourceChange.Inserted, null)), store.UpsertSources([.. items.Select(LibraryFile)]));
        for (var i = 0; i < items.Length; i++) store.AssociateProbe(items[i], Sha((char)('a' + i)), Probe, Library, Now);
        var otherLibrary = Guid.NewGuid();
        var results = store.UpsertSources(
        [
            LibraryFile(items[0]),
            LibraryFile(items[1]) with { LibraryId = otherLibrary },
            LibraryFile(items[2]) with { Length = 2000 },
            LibraryFile(items[3]) with { Path = "/media/renamed.mkv" },
            LibraryFile(items[4]) with { LastWriteTimeUtc = Modified.AddSeconds(1) }
        ]);
        Assert.Equal(new UpsertResult[]
        {
            new(items[0], SourceChange.Unchanged, Sha('a')),
            new(items[1], SourceChange.Unchanged, Sha('b')),
            new(items[2], SourceChange.ContentChanged, Sha('c')),
            new(items[3], SourceChange.ContentChanged, Sha('d')),
            new(items[4], SourceChange.ContentChanged, Sha('e'))
        }, results);
        // Moving to another library keeps the hash; new content loses it until it is probed again.
        Assert.Equal(new StoredSource(LibraryFile(items[1]) with { LibraryId = otherLibrary }, Sha('b')), store.GetSource(items[1]));
        Assert.Equal(new StoredSource(LibraryFile(items[2]) with { Length = 2000 }, null), store.GetSource(items[2]));
        Assert.Equal(new StoredSource(LibraryFile(items[3]) with { Path = "/media/renamed.mkv" }, null), store.GetSource(items[3]));
        Assert.Equal(ByText(items), store.Sources().Select(s => s.File.ItemId));
        Assert.Equal([items[0]], store.SourcesBySha(Sha('a')).Select(s => s.File.ItemId));
        Assert.Empty(store.SourcesBySha(Sha('c')));
        Assert.Null(store.GetSource(Guid.NewGuid()));
    }

    [Fact]
    public void RemoveSourceReturnsTheHashTheFileHad()
    {
        var store = Create();
        var item = AddMedia(store, Sha('a'));
        var unprobed = Guid.NewGuid();
        store.UpsertSources([LibraryFile(unprobed)]);
        Assert.Equal(Sha('a'), store.RemoveSource(item));
        Assert.Null(store.RemoveSource(unprobed));
        Assert.Null(store.RemoveSource(Guid.NewGuid()));
        Assert.Empty(store.Sources());
        Assert.NotNull(store.GetMedia(Sha('a')));   // the media stays until it expires as an orphan
    }

    // ---- Media

    [Fact]
    public void AssociateProbeNormalizesTheHashAndLinksTheItem()
    {
        var store = Create();
        var item = Guid.NewGuid();
        store.UpsertSources([LibraryFile(item)]);
        var sha = "0123456789ABCDEF" + new string('E', 48);
        var media = store.AssociateProbe(item, sha, Probe, Library, Now);
        var normalized = sha.ToLowerInvariant();
        Assert.Equal((normalized, RenditionPaths.MediaUuid(normalized), Library), (media.Sha256, media.MediaUuid, media.LibraryId));
        Assert.Equal(normalized[..32], media.MediaUuid.ToString("N"));
        Assert.Equal(Json(Probe), Json(media.Probe));
        Assert.Null(media.OrphanedAt);
        var stored = store.GetMedia(normalized)!;
        Assert.Equal((media.Sha256, media.MediaUuid, media.LibraryId, media.OrphanedAt), (stored.Sha256, stored.MediaUuid, stored.LibraryId, stored.OrphanedAt));
        Assert.Equal(Json(Probe), Json(stored.Probe));
        Assert.Equal(normalized, store.GetSource(item)!.Sha256);
        Assert.Equal([normalized], store.AllMediaShas());
        Assert.Null(store.GetMedia(Sha('b')));
        Assert.Throws<ArgumentException>(() => store.AssociateProbe(item, "not a hash", Probe, Library, Now));
    }

    [Fact]
    public void MediaSubmittedWithoutAnItemIsAnOrphanUntilAnItemIsAssociated()
    {
        var store = Create();
        var submitted = Now;
        Assert.Equal(submitted, store.AssociateProbe(null, Sha('a'), Probe, Library, Now).OrphanedAt);
        _clock.Now += TimeSpan.FromMinutes(1);
        // Submitting the same file again changes nothing, not even the library it was first submitted for.
        var again = store.AssociateProbe(null, Sha('a'), Probe, Guid.NewGuid(), Now);
        Assert.Equal((submitted, Library), (again.OrphanedAt, again.LibraryId));
        var item = Guid.NewGuid();
        store.UpsertSources([LibraryFile(item)]);
        Assert.Null(store.AssociateProbe(item, Sha('a'), Probe, Library, Now).OrphanedAt);
        Assert.Equal(Sha('a'), store.GetSource(item)!.Sha256);
    }

    [Fact]
    public void AssociateProbeRejectsAnUnknownItemOrAnotherProbeWithoutWritingAnything()
    {
        var store = Create();
        Assert.Equal("素材尚未登记", Assert.Throws<KeyNotFoundException>(() => store.AssociateProbe(Guid.NewGuid(), Sha('a'), Probe, Library, Now)).Message);
        Assert.Null(store.GetMedia(Sha('a')));   // the media row was rolled back too
        var item = AddMedia(store, Sha('a'));
        var other = Guid.NewGuid();
        store.UpsertSources([LibraryFile(other)]);
        var mismatch = Assert.Throws<ProbeMismatchException>(() => store.AssociateProbe(other, Sha('a'), Probe with { Width = 1280 }, Library, Now));
        Assert.Equal("同一 SHA256 的媒体信息与已保存的信息不一致", mismatch.Message);
        Assert.ThrowsAny<ArgumentException>(() => store.AssociateProbe(null, Sha('a'), Probe with { DurationSeconds = 61 }, Library, Now));
        Assert.Null(store.GetSource(other)!.Sha256);
        // Probes are compared as JSON, so an equal probe built from other arrays is the same probe.
        store.AssociateProbe(other, Sha('a'), Probe with { Audio = [.. Probe.Audio] }, Library, Now);
        Assert.Equal(ByText(item, other), store.SourcesBySha(Sha('a')).Select(s => s.File.ItemId));
    }

    [Fact]
    public void AProbeThatKeepsTheStoredOneAssociatesTheItemDespiteADifferentProbe()
    {
        var store = Create();
        var item = AddMedia(store, Sha('a'));
        var other = Guid.NewGuid();
        store.UpsertSources([LibraryFile(other)]);
        // Another FFprobe build describes the same file differently.
        var different = Probe with { FramesPerSecond = 25.000025, FieldOrder = "unknown" };
        Assert.Throws<ProbeMismatchException>(() => store.AssociateProbe(other, Sha('a'), different, Library, Now));   // the default still refuses it
        Assert.Null(store.GetSource(other)!.Sha256);
        var media = store.AssociateProbe(other, Sha('a'), different, Library, Now, keepExistingProbe: true);
        Assert.Equal(Json(Probe), Json(media.Probe));
        Assert.Equal(Json(Probe), Json(store.GetMedia(Sha('a'))!.Probe));
        Assert.Equal(Sha('a'), store.GetSource(other)!.Sha256);
        Assert.Equal(ByText(item, other), store.SourcesBySha(Sha('a')).Select(s => s.File.ItemId));
        // It clears the orphan mark as any association does.
        store.RemoveSource(item);
        store.RemoveSource(other);
        store.RefreshOrphans(Now);
        store.UpsertSources([LibraryFile(other)]);
        Assert.Null(store.AssociateProbe(other, Sha('a'), different, Library, Now, keepExistingProbe: true).OrphanedAt);
    }

    [Fact]
    public void OrphanMediaIsOnlyDeletedAfterTheRetention()
    {
        var store = Create();
        AddMedia(store, Sha('a'));
        var item = AddMedia(store, Sha('b'));
        var published = EncodeAndPublish(store, Sha('b'));
        store.RemoveSource(item);
        var orphaned = Now;
        store.RefreshOrphans(Now);
        Assert.Null(store.GetMedia(Sha('a'))!.OrphanedAt);
        Assert.Equal(orphaned, store.GetMedia(Sha('b'))!.OrphanedAt);
        var retention = TimeSpan.FromHours(72);
        _clock.Now += retention - TimeSpan.FromMilliseconds(1);
        store.RefreshOrphans(Now);   // the time it was first seen orphaned counts
        Assert.Equal(orphaned, store.GetMedia(Sha('b'))!.OrphanedAt);
        Assert.Empty(store.DeleteExpiredOrphans(Now - retention, Now));
        _clock.Now += TimeSpan.FromMilliseconds(1);
        Assert.Equal([Sha('b')], store.DeleteExpiredOrphans(Now - retention, Now));
        Assert.Null(store.GetMedia(Sha('b')));
        Assert.Empty(store.Renditions(Sha('b')));
        Assert.Equal([Sha('a')], store.AllMediaShas());
        Assert.DoesNotContain(store.List(10), job => job.Sha256 == Sha('b'));
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM job_outputs"));
        Assert.Equal(Directories(published), Deletions().Select(d => d.RelDir));
        Assert.All(Deletions(), d => Assert.Equal(("孤儿媒体", Now), (d.Reason, d.NotBefore)));
    }

    [Fact]
    public void OrphansStillInUseAreKept()
    {
        var store = Create();
        AddSubmittedMedia(store, Sha('a'));
        store.EnsureEncodeJob(Sha('a'), null, Profile, Now);
        Assert.NotNull(store.Claim(Request(Sha('a'))));          // running
        AddSubmittedMedia(store, Sha('b'));
        store.EnsureEncodeJob(Sha('b'), null, Profile, Now);
        var verifying = store.Claim(Request(Sha('b')))!;
        Assert.True(store.SubmitOutputs(verifying.Id, verifying.LeaseToken, Plan(verifying), Completion(verifying), Now));
        AddSubmittedMedia(store, Sha('c'));
        store.EnsureEncodeJob(Sha('c'), null, Profile, Now);
        var cancelled = store.Claim(Request(Sha('c')))!;
        store.CancelEncodeJob(Sha('c'), Now);                    // cancelled, but its worker still holds the lease
        AddMedia(store, Sha('d'));
        Execute("UPDATE media SET orphaned_at=0 WHERE sha256=$sha", ("$sha", Sha('d')));   // a stale mark from an earlier run
        _clock.Now += TimeSpan.FromMinutes(1);
        store.RefreshOrphans(Now);
        Assert.Null(store.GetMedia(Sha('d'))!.OrphanedAt);
        Assert.Empty(store.DeleteExpiredOrphans(Now, Now));
        // Once the worker has released the cancelled job, nothing uses that media any more.
        Assert.True(store.Release(cancelled.Id, cancelled.LeaseToken, "cancelled", MaxAttempts, Now));
        Assert.Equal([Sha('c')], store.DeleteExpiredOrphans(Now, Now));
        Assert.Equal([Sha('a'), Sha('b'), Sha('d')], store.AllMediaShas());
    }

    [Fact]
    public void AnExpiredOrphanWhoseOnlyJobIsPendingIsDeletedWithIt()
    {
        var store = Create();
        var item = AddMedia(store, Sha('a'), [1080]);
        var published = EncodeAndPublish(store, Sha('a'));
        // A quality added later still waits for a worker when the file leaves the library.
        Assert.True(store.SyncRenditions(Sha('a'), RenditionPlanner.Desired(Probe, Qualities), Now));
        var pending = store.EnsureEncodeJob(Sha('a'), item, Profile, Now)!.Value;
        store.RemoveSource(item);
        store.RefreshOrphans(Now);
        Assert.Null(store.Claim(Request()));                     // without a library file a general claim never takes it
        var retention = TimeSpan.FromHours(72);
        _clock.Now += retention - TimeSpan.FromMilliseconds(1);
        Assert.Empty(store.DeleteExpiredOrphans(Now - retention, Now));
        Assert.Equal(JobStatus.Pending, Job(store, pending).Status);
        _clock.Now += TimeSpan.FromMilliseconds(1);
        Assert.Equal([Sha('a')], store.DeleteExpiredOrphans(Now - retention, Now));
        Assert.Null(store.GetMedia(Sha('a')));
        Assert.Empty(store.Renditions(Sha('a')));
        Assert.Empty(store.List(10));
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM job_outputs"));
        Assert.Equal(Directories(published), Deletions().Select(d => d.RelDir));
        Assert.All(Deletions(), d => Assert.Equal(("孤儿媒体", Now), (d.Reason, d.NotBefore)));
    }

    // ---- Renditions

    [Fact]
    public void SyncRenditionsAddsNewKeysAndRetiresCancelledOnes()
    {
        var store = Create();
        AddMedia(store, Sha('a'));
        Assert.Equal(Ordinal(PlannedKeys), store.Renditions(Sha('a')).Select(r => r.Key));
        Assert.All(store.Renditions(Sha('a')), r => Assert.Equal(new RenditionRow(Sha('a'), r.Key, RenditionState.Missing, null, null, null, null, null), r));
        Assert.False(store.SyncRenditions(Sha('a'), RenditionPlanner.Desired(Probe, Qualities), Now));
        var published = EncodeAndPublish(store, Sha('a'));
        var v720 = store.Renditions(Sha('a')).Single(r => r.Key == "v720");
        _clock.Now += TimeSpan.FromMinutes(1);
        Assert.True(store.SyncRenditions(Sha('a'), RenditionPlanner.Desired(Probe, [1080, 480]), Now));
        Assert.Equal(["a1", "preview", "s2", "v1080", "v480"], store.Renditions(Sha('a')).Select(r => r.Key));
        Assert.Equal(RenditionState.Missing, store.Renditions(Sha('a')).Single(r => r.Key == "v480").State);
        Assert.Equal(new Deletion(v720.RelDir!, Now, "档位已取消", 0, null), Assert.Single(Deletions()));
        Assert.Equal(["a1", "preview", "s2", "v1080"], store.PublishedManifests(Sha('a')).Select(m => m.Key));
        Assert.Equal(4L, Scalar("SELECT COUNT(*) FROM job_outputs WHERE job_id=$id", ("$id", published.Id.ToString("N"))));
        Assert.DoesNotContain(store.AllPublished(), r => r.Key == "v720");
    }

    [Fact]
    public void ShasNeedingJobsFollowsThePlanDefinition()
    {
        var store = Create();
        AddMedia(store, Sha('a'));   // missing renditions
        AddMedia(store, Sha('b'));
        EncodeAndPublish(store, Sha('b'));   // published under the current profile
        AddMedia(store, Sha('c'));
        store.EnsureEncodeJob(Sha('c'), null, Profile, Now);   // held by a job
        Assert.Equal([Sha('a')], store.ShasNeedingJobs(Profile));
        Assert.Equal([Sha('a'), Sha('b')], store.ShasNeedingJobs(NextProfile));   // published under another profile
    }

    // ---- Probe jobs

    [Fact]
    public void EnsureProbeJobQueuesACancelledOrCompletedProbeAgainButNothingElse()
    {
        var store = Create();
        var item = Guid.NewGuid();
        store.UpsertSources([LibraryFile(item)]);
        var source = store.GetSource(item)!;
        store.EnsureProbeJob(source, Now);
        store.EnsureProbeJob(source, Now);   // pending: left alone
        var job = Assert.Single(store.List(10));
        Assert.Equal((JobKind.Probe, (Guid?)item, JobStatus.Pending), (job.Kind, job.ItemId, job.Status));
        Assert.Null(job.Sha256);
        var lease = store.Claim(Request())!;
        Assert.Equal((job.Id, $"probe:{item:N}:1000:{Modified.Ticks}"), (lease.Id, lease.WorkKey));
        Assert.Equal(source.File, JsonSerializer.Deserialize<SourceFile>(lease.Payload));
        store.EnsureProbeJob(source, Now);   // running: left alone
        Assert.Equal(lease, store.GetLease(lease.Id, lease.LeaseToken, Now));
        store.CancelJob(lease.Id, Now);
        _clock.Now += TimeSpan.FromMinutes(1);
        store.EnsureProbeJob(source, Now);   // cancelled: queued again
        job = Assert.Single(store.List(10));
        Assert.Equal((JobStatus.Pending, 0, Now), (job.Status, job.Attempt, job.UpdatedAt));
        lease = store.Claim(Request())!;
        Assert.True(store.CompleteProbe(lease.Id, lease.LeaseToken, Now));
        _clock.Now += TimeSpan.FromMinutes(1);
        // Completed: queued again too, since it is only asked for a file without a hash (failed probes: see the rebuild test).
        store.EnsureProbeJob(source, Now);
        job = Assert.Single(store.List(10));
        Assert.Equal((lease.Id, JobStatus.Pending, 0, Now), (job.Id, job.Status, job.Attempt, job.UpdatedAt));
        // Other content of the same item is another probe.
        store.EnsureProbeJob(source with { File = source.File with { Length = 2000 } }, Now);
        Assert.Equal(2, store.List(10).Count);
    }

    [Fact]
    public void AFileWhosePathChangedIsProbedAgainWithItsNewPath()
    {
        var store = Create();
        var item = Guid.NewGuid();
        store.UpsertSources([LibraryFile(item)]);
        store.EnsureProbeJob(store.GetSource(item)!, Now);
        var probe = store.Claim(Request())!;
        store.AssociateProbe(item, Sha('a'), Probe, Library, Now);
        Assert.True(store.CompleteProbe(probe.Id, probe.LeaseToken, Now));
        // Same item, length and modification time, hence the same work key; only the path differs.
        var moved = LibraryFile(item) with { Path = "/media/moved/a.mkv" };
        Assert.Equal([new UpsertResult(item, SourceChange.ContentChanged, Sha('a'))], store.UpsertSources([moved]));
        // What the coordinator does for ContentChanged: cancel pending probes, then ensure one. The completed probe is queued again.
        store.CancelProbeJobs(item, Now);
        store.EnsureProbeJob(store.GetSource(item)!, Now);
        // It moves again before a worker comes: the cancelled probe is queued again, with the newest path.
        var movedAgain = moved with { Path = "/media/moved/b.mkv" };
        Assert.Equal([new UpsertResult(item, SourceChange.ContentChanged, null)], store.UpsertSources([movedAgain]));
        store.CancelProbeJobs(item, Now);
        Assert.Equal(JobStatus.Cancelled, Job(store, probe.Id).Status);
        store.EnsureProbeJob(store.GetSource(item)!, Now);
        // Claim's check against the source row passes, so the probe is handed out instead of being cancelled.
        var again = store.Claim(Request())!;
        Assert.Equal((probe.Id, 1), (again.Id, again.Attempt));
        Assert.Equal(movedAgain, JsonSerializer.Deserialize<SourceFile>(again.Payload));
        Assert.Equal(JobStatus.Running, Job(store, probe.Id).Status);
    }

    [Fact]
    public async Task ParallelWorkersNeverReceiveTheSameJob()
    {
        var store = Create();
        var files = Enumerable.Range(0, 12).Select(_ => LibraryFile(Guid.NewGuid())).ToList();
        store.UpsertSources(files);
        foreach (var file in files) store.EnsureProbeJob(new StoredSource(file, null), Now);
        var leases = await Task.WhenAll(Enumerable.Range(0, 16).Select(i => Task.Run(() => Create().Claim(Request(worker: $"worker-{i}")))));
        var claimed = leases.OfType<JobLease>().ToList();
        Assert.Equal(12, claimed.Count);
        Assert.Equal(12, claimed.Select(lease => lease.Id).Distinct().Count());
        Assert.Equal(12, store.Counts().Running);
    }

    [Fact]
    public void ClaimCancelsProbeJobsWhoseFileChangedOrDisappeared()
    {
        var store = Create();
        Guid[] items = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
        store.UpsertSources([.. items.Select(LibraryFile)]);
        foreach (var item in items)
        {
            store.EnsureProbeJob(store.GetSource(item)!, Now);
            _clock.Now += TimeSpan.FromSeconds(1);
        }
        store.UpsertSources([LibraryFile(items[0]) with { Length = 5 }]);
        store.RemoveSource(items[1]);
        Assert.Equal(items[2], store.Claim(Request())!.ItemId);
        Assert.Equal([JobStatus.Cancelled, JobStatus.Cancelled], store.List(10).Where(job => job.ItemId != items[2]).Select(job => job.Status));
    }

    [Fact]
    public void ALeaseWorksOnlyWithItsTokenUntilItExpires()
    {
        var store = Create();
        var item = Guid.NewGuid();
        store.UpsertSources([LibraryFile(item)]);
        store.EnsureProbeJob(store.GetSource(item)!, Now);
        var lease = store.Claim(Request(worker: "prober"))!;
        Assert.Equal((JobKind.Probe, (Guid?)item, 1, Now + Lease), (lease.Kind, lease.ItemId, lease.Attempt, lease.LeaseExpiresAt));
        Assert.Equal(lease, store.GetLease(lease.Id, lease.LeaseToken, Now));
        Assert.Null(store.GetLease(lease.Id, "wrong", Now));
        Assert.False(store.Heartbeat(lease.Id, "wrong", Lease, Now));
        _clock.Now += TimeSpan.FromMinutes(4);
        Assert.True(store.Heartbeat(lease.Id, lease.LeaseToken, Lease, Now));
        Assert.Equal(Now + Lease, store.GetLease(lease.Id, lease.LeaseToken, Now)!.LeaseExpiresAt);
        _clock.Now += Lease;   // at the expiry the lease is no longer valid
        Assert.Null(store.GetLease(lease.Id, lease.LeaseToken, Now));
        Assert.False(store.Heartbeat(lease.Id, lease.LeaseToken, Lease, Now));
        Assert.False(store.CompleteProbe(lease.Id, lease.LeaseToken, Now));
        _clock.Now -= TimeSpan.FromMilliseconds(1);
        Assert.True(store.CompleteProbe(lease.Id, lease.LeaseToken, Now));
        Assert.False(store.CompleteProbe(lease.Id, lease.LeaseToken, Now));
        var job = Assert.Single(store.List(10));
        Assert.Equal((JobStatus.Complete, "prober", 1), (job.Status, job.Worker, job.Attempt));
    }

    // ---- Encode jobs

    [Fact]
    public void EnsureEncodeJobMergesKeysIntoThePendingJob()
    {
        var store = Create();
        var item = AddMedia(store, Sha('a'), [1080]);
        var id = store.EnsureEncodeJob(Sha('a'), item, Profile, Now)!.Value;
        Assert.Equal("""{"Keys":["a1","preview","s2","v1080"]}""", Payload(id));
        Assert.True(store.SyncRenditions(Sha('a'), RenditionPlanner.Desired(Probe, Qualities), Now));
        Assert.Equal(id, store.EnsureEncodeJob(Sha('a'), null, Profile, Now));
        Assert.Equal("""{"Keys":["a1","preview","s2","v1080","v720"]}""", Payload(id));
        Assert.Null(store.EnsureEncodeJob(Sha('a'), null, Profile, Now));   // nothing left without a job
        Assert.All(store.Renditions(Sha('a')), r => Assert.Equal(id, r.JobId));
        var job = Assert.Single(store.List(10));
        Assert.Equal((JobKind.Encode, (Guid?)item, Sha('a'), JobStatus.Pending, 0), (job.Kind, job.ItemId, job.Sha256, job.Status, job.Attempt));
        Assert.Equal(PlannedKeys, Plan(store.Claim(Request())!).Outputs.Select(o => o.Key));
    }

    [Fact]
    public void EnsureEncodeJobAlsoMergesIntoARetriedJobThatStillCarriesItsPlan()
    {
        var store = Create();
        AddMedia(store, Sha('a'), [1080]);
        var id = store.EnsureEncodeJob(Sha('a'), null, Profile, Now)!.Value;
        var lease = store.Claim(Request(desired: m => RenditionPlanner.Desired(m.Probe, [1080])))!;
        Assert.True(store.Release(lease.Id, lease.LeaseToken, "failed", MaxAttempts, Now));
        Assert.Equal(lease.Payload, Payload(id));
        Assert.True(store.SyncRenditions(Sha('a'), RenditionPlanner.Desired(Probe, Qualities), Now));
        Assert.Equal(id, store.EnsureEncodeJob(Sha('a'), null, Profile, Now));
        Assert.Equal("""{"Keys":["v1080","a1","s2","preview","v720"]}""", Payload(id));
        Assert.Equal(PlannedKeys, Plan(store.Claim(Request())!).Outputs.Select(o => o.Key));
    }

    [Fact]
    public void EnsureEncodeJobLeavesAJobInProgressAloneAndStartsTheNextRunWithANewId()
    {
        var store = Create();
        AddMedia(store, Sha('a'), [1080]);
        var id = store.EnsureEncodeJob(Sha('a'), null, Profile, Now)!.Value;
        var lease = store.Claim(Request(desired: m => RenditionPlanner.Desired(m.Probe, [1080])))!;
        Assert.True(store.SyncRenditions(Sha('a'), RenditionPlanner.Desired(Probe, Qualities), Now));   // v720 added while running
        Assert.Null(store.EnsureEncodeJob(Sha('a'), null, Profile, Now));
        Assert.True(store.SubmitOutputs(lease.Id, lease.LeaseToken, Plan(lease), Completion(lease), Now));
        Assert.Null(store.EnsureEncodeJob(Sha('a'), null, Profile, Now));
        Assert.Equal(["a1", "preview", "s2", "v1080"], store.Publish(lease.Id, Profile, Now, ReplacedGrace)!.PublishedKeys);
        var next = store.EnsureEncodeJob(Sha('a'), null, Profile, Now)!.Value;
        Assert.NotEqual(id, next);
        Assert.Equal("""{"Keys":["v720"]}""", Payload(next));
        // Same work key: the completed run is replaced, while its manifests stay under its id.
        var job = Assert.Single(store.List(10));
        Assert.Equal((next, JobStatus.Pending, 0), (job.Id, job.Status, job.Attempt));
        Assert.Equal(4, store.PublishedManifests(Sha('a')).Count);
        Assert.All(store.AllPublished(), r => Assert.Equal(id, r.PublishedJobId));
    }

    // ---- Claims

    [Fact]
    public void DirectedClaimOnlyReturnsTheEncodeJobOfThatSha()
    {
        var store = Create();
        var probeItem = Guid.NewGuid();
        store.UpsertSources([LibraryFile(probeItem)]);
        store.EnsureProbeJob(store.GetSource(probeItem)!, Now);   // the oldest job
        _clock.Now += TimeSpan.FromSeconds(1);
        AddMedia(store, Sha('a'));
        store.EnsureEncodeJob(Sha('a'), null, Profile, Now);
        _clock.Now += TimeSpan.FromSeconds(1);
        AddMedia(store, Sha('b'));
        var b = store.EnsureEncodeJob(Sha('b'), null, Profile, Now);
        var lease = store.Claim(Request(Sha('b')))!;
        Assert.Equal((b, JobKind.Encode, $"encode:{Sha('b')}", Sha('b')), (lease.Id, lease.Kind, lease.WorkKey, Plan(lease).Sha256));
        Assert.Null(store.Claim(Request(Sha('b'))));   // its only job is taken
        Assert.Null(store.Claim(Request(Sha('c'))));   // nothing else is handed out instead
        Assert.Equal(2, store.Counts().Pending);
    }

    [Fact]
    public void GeneralClaimSkipsEncodeJobsWithoutALibraryFile()
    {
        var store = Create();
        AddSubmittedMedia(store, Sha('a'));
        var id = store.EnsureEncodeJob(Sha('a'), null, Profile, Now)!.Value;
        Assert.Null(store.Claim(Request()));
        Assert.Equal(JobStatus.Pending, Job(store, id).Status);   // skipped, not cancelled
        // The worker that submitted the file takes the job with a directed claim and provides the input itself.
        var lease = store.Claim(Request(Sha('a')))!;
        Assert.Equal(id, lease.Id);
        Assert.Null(lease.ItemId);
        Assert.Null(Plan(lease).Source);
    }

    [Fact]
    public void ClaimTurnsThePayloadIntoTheFullPlan()
    {
        var store = Create();
        // The preferred file sorts last, so it is only chosen because it is preferred.
        var preferred = Guid.Parse("ffffffffffffffffffffffffffffffff");
        foreach (var item in new[] { Guid.Parse("00000000000000000000000000000001"), preferred })
        {
            store.UpsertSources([LibraryFile(item)]);
            store.AssociateProbe(item, Sha('a'), Probe, Library, Now);
        }
        store.SyncRenditions(Sha('a'), RenditionPlanner.Desired(Probe, Qualities), Now);
        var id = store.EnsureEncodeJob(Sha('a'), preferred, Profile, Now);
        var lease = store.Claim(Request(worker: "encoder"))!;
        Assert.Equal((id, (Guid?)preferred, JobKind.Encode, 1, Now + Lease), (lease.Id, lease.ItemId, lease.Kind, lease.Attempt, lease.LeaseExpiresAt));
        Assert.Equal(lease.Payload, Payload(lease.Id));
        Assert.Equal(lease, store.GetLease(lease.Id, lease.LeaseToken, Now));
        var plan = Plan(lease);
        Assert.Equal((Sha('a'), RenditionPaths.MediaUuid(Sha('a')), LibraryFile(preferred)), (plan.Sha256, plan.MediaUuid, plan.Source));
        Assert.Equal(Json(Probe), Json(plan.Probe));
        var specs = RenditionPlanner.Desired(Probe, Qualities);
        Assert.Equal(PlannedKeys, plan.Outputs.Select(o => o.Key));
        foreach (var (target, spec) in plan.Outputs.Zip(specs))
        {
            Assert.Equal(RenditionPaths.RelativeDirectory(plan.MediaUuid, spec, lease.LeaseToken), target.RelativeDirectory);
            Assert.True(RenditionPaths.IsValidRelativeDirectory(target.RelativeDirectory));
            Assert.Equal(RenditionPaths.Full(OutputRoot, target.RelativeDirectory), target.Directory);
            Assert.Equal((spec.Kind, spec.Video, spec.SourceIndex, spec.Channels), (target.Kind, target.Video, target.SourceIndex, target.Channels));
        }
        Assert.Equal((JobStatus.Running, "encoder"), (Job(store, lease.Id).Status, Job(store, lease.Id).Worker));
    }

    [Fact]
    public void ClaimUsesAnotherFileOfTheMediaWhenThePreferredOneChanged()
    {
        var store = Create();
        var first = AddMedia(store, Sha('a'));
        var preferred = Guid.NewGuid();
        store.UpsertSources([LibraryFile(preferred)]);
        store.AssociateProbe(preferred, Sha('a'), Probe, Library, Now);
        store.EnsureEncodeJob(Sha('a'), preferred, Profile, Now);
        store.UpsertSources([LibraryFile(preferred) with { Length = 1 }]);   // the preferred item now holds other content
        Assert.Equal(LibraryFile(first), Plan(store.Claim(Request())!).Source);
    }

    [Fact]
    public void ClaimOnlyPlansRenditionsThatAreStillWantedAndHeldByTheJob()
    {
        var store = Create();
        AddMedia(store, Sha('a'));
        store.EnsureEncodeJob(Sha('a'), null, Profile, Now);
        // v720 is switched off and on again before a worker comes: its new row is not held by this job.
        store.SyncRenditions(Sha('a'), RenditionPlanner.Desired(Probe, [1080]), Now);
        store.SyncRenditions(Sha('a'), RenditionPlanner.Desired(Probe, Qualities), Now);
        // s2 is held, but the configuration the claim sees no longer wants it.
        var lease = store.Claim(Request(desired: m => [.. RenditionPlanner.Desired(m.Probe, Qualities).Where(s => s.Key != "s2")]))!;
        Assert.Equal(["v1080", "a1", "preview"], Plan(lease).Outputs.Select(o => o.Key));
        Assert.Null(store.Renditions(Sha('a')).Single(r => r.Key == "v720").JobId);
        Assert.Equal([Sha('a')], store.ShasNeedingJobs(Profile));
    }

    [Fact]
    public void ClaimCancelsJobsThatAreNoLongerEligibleOrHaveNothingLeftToEncode()
    {
        var store = Create();
        var ids = new List<Guid>();
        foreach (var sha in new[] { Sha('a'), Sha('b'), Sha('c') })
        {
            AddMedia(store, sha);
            ids.Add(store.EnsureEncodeJob(sha, null, Profile, Now)!.Value);
            _clock.Now += TimeSpan.FromSeconds(1);
        }
        var lease = store.Claim(Request(eligible: sha => sha != Sha('a'), desired: m => m.Sha256 == Sha('b') ? [] : RenditionPlanner.Desired(m.Probe, Qualities)))!;
        Assert.Equal(ids[2], lease.Id);
        Assert.Equal([JobStatus.Cancelled, JobStatus.Cancelled], ids.Take(2).Select(id => Job(store, id).Status));
        // Their renditions are free again for the next scheduling sweep.
        Assert.Equal([Sha('a'), Sha('b')], store.ShasNeedingJobs(Profile));
    }

    // ---- Verification and publication

    [Fact]
    public void SubmitOutputsStoresTheDirectoriesOfThePlan()
    {
        var store = Create();
        AddMedia(store, Sha('a'));
        store.EnsureEncodeJob(Sha('a'), null, Profile, Now);
        var lease = store.Claim(Request())!;
        var plan = Plan(lease);
        var completion = Completion(lease);
        // Only RelativeDirectory counts: absolute directories are for the worker and are never stored.
        var elsewhere = plan with { Outputs = [.. plan.Outputs.Select(o => o with { Directory = "/elsewhere" })] };
        _clock.Now += TimeSpan.FromMinutes(1);
        Assert.True(store.SubmitOutputs(lease.Id, lease.LeaseToken, elsewhere, completion, Now));
        Assert.Null(store.GetLease(lease.Id, lease.LeaseToken, Now));
        Assert.False(store.SubmitOutputs(lease.Id, lease.LeaseToken, plan, completion, Now));   // already verifying
        var job = Assert.Single(store.VerifyingJobs(10));
        Assert.Equal((lease.Id, Sha('a'), 1), (job.Id, job.Sha256, job.Attempt));
        Assert.Equal(plan.Outputs.Select(o => (o.Key, o.RelativeDirectory)).OrderBy(o => o.Key, StringComparer.Ordinal), job.Outputs.Select(o => (o.Key, o.RelDir)));
        Assert.All(job.Outputs, output => Assert.Equal(completion.Renditions.Single(r => r.Key == output.Key).Files, output.Files));
        Assert.Equal(JobStatus.Verifying, Job(store, lease.Id).Status);
    }

    [Fact]
    public void SubmitOutputsRequiresARunningEncodeJobWithAValidLease()
    {
        var store = Create();
        var probeItem = Guid.NewGuid();
        store.UpsertSources([LibraryFile(probeItem)]);
        store.EnsureProbeJob(store.GetSource(probeItem)!, Now);
        var probe = store.Claim(Request())!;
        AddMedia(store, Sha('a'));
        store.EnsureEncodeJob(Sha('a'), null, Profile, Now);
        var lease = store.Claim(Request())!;
        var plan = Plan(lease);
        var completion = Completion(lease);
        Assert.False(store.SubmitOutputs(lease.Id, "wrong", plan, completion, Now));
        Assert.False(store.SubmitOutputs(lease.Id, lease.LeaseToken, plan, completion, lease.LeaseExpiresAt));
        Assert.False(store.SubmitOutputs(probe.Id, probe.LeaseToken, plan, completion, Now));
        Assert.False(store.CompleteProbe(lease.Id, lease.LeaseToken, Now));
        // A rendition the plan does not contain is rejected as a whole.
        var foreign = completion with { Renditions = [.. completion.Renditions, new RenditionOutput("v480", new RenditionSummary(), [new OutputFile("index.m3u8", 1, Now)])] };
        Assert.Throws<ArgumentException>(() => store.SubmitOutputs(lease.Id, lease.LeaseToken, plan, foreign, Now));
        Assert.Empty(store.VerifyingJobs(10));
        Assert.NotNull(store.GetLease(lease.Id, lease.LeaseToken, Now));
    }

    [Fact]
    public void PublishMakesOutputsVisibleAndQueuesTheDirectoriesTheyReplace()
    {
        var store = Create();
        AddMedia(store, Sha('a'));
        var first = EncodeAndPublish(store, Sha('a'));
        var rows = store.Renditions(Sha('a'));
        Assert.All(rows, r => Assert.Equal((RenditionState.Published, (Guid?)null, (Guid?)first.Id, Profile), (r.State, r.JobId, r.PublishedJobId, r.Profile)));
        Assert.Equal(Directories(first), Ordinal(rows.Select(r => r.RelDir!)));
        Assert.Equal(new RenditionSummary(1080, 1920, 1080, 25, 5000000, 4000000, "avc1.640028"), rows.Single(r => r.Key == "v1080").Summary);
        Assert.Equal(rows, store.AllPublished());
        var manifest = Assert.Single(store.PublishedManifests(Sha('a')), m => m.Key == "v1080");
        Assert.Equal(Plan(first).Outputs[0].RelativeDirectory, manifest.RelDir);
        Assert.Equal(Completion(first).Renditions[0].Files, manifest.Files);
        Assert.Empty(Deletions());
        // A new encoding profile encodes everything again in a new run; the directories it replaces are queued, after the grace.
        _clock.Now += TimeSpan.FromMinutes(1);
        Assert.Equal([Sha('a')], store.ShasNeedingJobs(NextProfile));
        var second = EncodeAndPublish(store, Sha('a'), NextProfile);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(Directories(first), Deletions().Select(d => d.RelDir));
        Assert.All(Deletions(), d => Assert.Equal(("已被新成果替换", Now + ReplacedGrace), (d.Reason, d.NotBefore)));
        Assert.Equal(Directories(second), Ordinal(store.PublishedManifests(Sha('a')).Select(m => m.RelDir)));
        Assert.All(store.AllPublished(), r => Assert.Equal(((Guid?)second.Id, NextProfile), (r.PublishedJobId, r.Profile)));
        Assert.Equal(5L, Scalar("SELECT COUNT(*) FROM job_outputs"));   // the replaced manifests are gone
        Assert.Null(store.Publish(second.Id, NextProfile, Now, ReplacedGrace));   // only a verifying job is published
    }

    [Fact]
    public void OnlyAReplacedDirectoryWaitsForTheGraceAnOutputNoLongerWantedGoesAtOnce()
    {
        var store = Create();
        AddMedia(store, Sha('a'));
        var first = EncodeAndPublish(store, Sha('a'));
        // A new encoding profile encodes everything again; the 720 quality is switched off while that run is being verified.
        store.EnsureEncodeJob(Sha('a'), null, NextProfile, Now);
        var second = store.Claim(Request(Sha('a')))!;
        Assert.True(store.SubmitOutputs(second.Id, second.LeaseToken, Plan(second), Completion(second), Now));
        Assert.True(store.SyncRenditions(Sha('a'), RenditionPlanner.Desired(Probe, [1080]), Now));
        var withdrawn = Plan(first).Outputs.Single(o => o.Key == "v720").RelativeDirectory;
        _clock.Now += TimeSpan.FromMinutes(1);
        var grace = TimeSpan.FromHours(6);
        Assert.Equal(["a1", "preview", "s2", "v1080"], store.Publish(second.Id, NextProfile, Now, grace)!.PublishedKeys);
        var replaced = Ordinal(Plan(first).Outputs.Where(o => o.Key != "v720").Select(o => o.RelativeDirectory)).ToList();
        var unwanted = Plan(second).Outputs.Single(o => o.Key == "v720").RelativeDirectory;
        Assert.Equal(replaced, Deletions().Where(d => d.Reason == "已被新成果替换").Select(d => d.RelDir));
        Assert.All(Deletions().Where(d => d.Reason == "已被新成果替换"), d => Assert.Equal(Now + grace, d.NotBefore));
        Assert.Equal(new Deletion(unwanted, Now, "档位已不再需要", 0, null), Deletions().Single(d => d.RelDir == unwanted));
        // Due now: the withdrawn quality of both runs. The replaced directories, which playback may still be reading, once the grace is over.
        Assert.Equal(Ordinal([withdrawn, unwanted]), Ordinal(store.DueDeletions(Now, 100).Select(d => d.RelDir)));
        Assert.Equal(2, store.DueDeletions(Now + grace - TimeSpan.FromMilliseconds(1), 100).Count);
        Assert.Equal(Ordinal([withdrawn, unwanted, .. replaced]), Ordinal(store.DueDeletions(Now + grace, 100).Select(d => d.RelDir)));
    }

    [Fact]
    public void PublishDiscardsTheOutputOfARemovedRenditionAndQueuesItsDirectory()
    {
        var store = Create();
        AddMedia(store, Sha('a'));
        store.EnsureEncodeJob(Sha('a'), null, Profile, Now);
        var lease = store.Claim(Request())!;
        Assert.True(store.SubmitOutputs(lease.Id, lease.LeaseToken, Plan(lease), Completion(lease), Now));
        // The 720 quality is switched off while the job is being verified.
        Assert.True(store.SyncRenditions(Sha('a'), RenditionPlanner.Desired(Probe, [1080]), Now));
        _clock.Now += TimeSpan.FromMinutes(1);
        var result = store.Publish(lease.Id, Profile, Now, ReplacedGrace)!;
        Assert.Equal(Sha('a'), result.Sha256);
        Assert.Equal(["a1", "preview", "s2", "v1080"], result.PublishedKeys);
        Assert.DoesNotContain(store.Renditions(Sha('a')), r => r.Key == "v720");
        var deletion = Assert.Single(Deletions());
        Assert.Equal(new Deletion(Plan(lease).Outputs.Single(o => o.Key == "v720").RelativeDirectory, Now, "档位已不再需要", 0, null), deletion);
        Assert.Equal(["a1", "preview", "s2", "v1080"], store.PublishedManifests(Sha('a')).Select(m => m.Key));
        Assert.Equal(4L, Scalar("SELECT COUNT(*) FROM job_outputs"));
        Assert.Equal(JobStatus.Complete, Job(store, lease.Id).Status);
    }

    // ---- Failures

    [Fact]
    public void AFailedJobDoesNotStopOtherJobsFromBeingClaimed()
    {
        var store = Create();
        AddMedia(store, Sha('a'));
        var a = store.EnsureEncodeJob(Sha('a'), null, Profile, Now)!.Value;
        _clock.Now += TimeSpan.FromSeconds(1);
        AddMedia(store, Sha('b'));
        var b = store.EnsureEncodeJob(Sha('b'), null, Profile, Now)!.Value;
        var failed = store.Claim(Request(worker: "first"))!;
        Assert.Equal(a, failed.Id);
        Assert.True(store.Release(failed.Id, failed.LeaseToken, "ffmpeg exited with 1", MaxAttempts, Now));
        Assert.Equal((JobStatus.Pending, "ffmpeg exited with 1"), (Job(store, a).Status, Job(store, a).Error));
        // Its directories wait in the deletion queue; claiming never waits for them.
        Assert.Equal(Directories(failed), store.DueDeletions(Now, 100).Select(d => d.RelDir));
        Assert.All(Deletions(), d => Assert.Equal(("Worker 报告失败", Now), (d.Reason, d.NotBefore)));
        var retried = store.Claim(Request(worker: "second"))!;
        var other = store.Claim(Request(worker: "third"))!;
        Assert.Equal((a, 2), (retried.Id, retried.Attempt));
        Assert.Equal(b, other.Id);
        Assert.Empty(Directories(retried).Intersect(Directories(failed)));   // every attempt writes to new directories
        Assert.Null(Job(store, a).Error);
    }

    [Fact]
    public void ReachingTheAttemptLimitFailsPermanentlyUntilARebuild()
    {
        var store = Create();
        AddMedia(store, Sha('a'));
        var id = store.EnsureEncodeJob(Sha('a'), null, Profile, Now)!.Value;
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var lease = store.Claim(Request())!;
            Assert.Equal((id, attempt), (lease.Id, lease.Attempt));
            Assert.True(store.SubmitOutputs(lease.Id, lease.LeaseToken, Plan(lease), Completion(lease), Now));
            Assert.True(store.FailVerification(lease.Id, "校验失败：缺少文件 init.mp4", MaxAttempts, Now));
            Assert.False(store.FailVerification(lease.Id, "again", MaxAttempts, Now));   // only a verifying job
        }
        Assert.Equal((JobStatus.Failed, "校验失败：缺少文件 init.mp4"), (Job(store, id).Status, Job(store, id).Error));
        Assert.All(store.Renditions(Sha('a')), r => Assert.Equal((RenditionState.Failed, (Guid?)null), (r.State, r.JobId)));
        Assert.Equal(MaxAttempts * PlannedKeys.Length, Deletions().Count);
        Assert.All(Deletions(), d => Assert.Equal("校验失败", d.Reason));
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM job_outputs"));
        Assert.Empty(store.ShasNeedingJobs(Profile));
        Assert.Null(store.EnsureEncodeJob(Sha('a'), null, Profile, Now));
        Assert.Null(store.Claim(Request()));
        store.ResetForRebuild(Now);
        Assert.All(store.Renditions(Sha('a')), r => Assert.Equal((RenditionState.Missing, (Guid?)null), (r.State, r.JobId)));
        Assert.Equal([Sha('a')], store.ShasNeedingJobs(Profile));
        var again = store.EnsureEncodeJob(Sha('a'), null, Profile, Now);
        var restarted = store.Claim(Request())!;
        Assert.Equal((again, 1), (restarted.Id, restarted.Attempt));
    }

    [Fact]
    public void PublishedRenditionsWhoseReencodeFailedStayPlayableButUnscheduledUntilARebuild()
    {
        var store = Create();
        AddMedia(store, Sha('a'));
        var published = EncodeAndPublish(store, Sha('a'));
        var reencode = store.EnsureEncodeJob(Sha('a'), null, NextProfile, Now)!.Value;
        var lease = store.Claim(Request())!;
        Assert.True(store.Release(lease.Id, lease.LeaseToken, "encoder failed", 1, Now));
        Assert.Equal((JobStatus.Failed, "encoder failed"), (Job(store, reencode).Status, Job(store, reencode).Error));
        Assert.All(store.Renditions(Sha('a')), r => Assert.Equal((RenditionState.Published, (Guid?)reencode, (Guid?)published.Id, Profile), (r.State, r.JobId, r.PublishedJobId, r.Profile)));
        Assert.Equal(PlannedKeys.Length, store.PublishedManifests(Sha('a')).Count);
        Assert.Empty(store.ShasNeedingJobs(NextProfile));
        // A quality added later is encoded on its own; the blocked renditions stay blocked although the job is created again.
        Assert.True(store.SyncRenditions(Sha('a'), RenditionPlanner.Desired(Probe, [1080, 720, 480]), Now));
        var added = store.EnsureEncodeJob(Sha('a'), null, NextProfile, Now)!.Value;
        Assert.NotEqual(reencode, added);
        Assert.Equal("""{"Keys":["v480"]}""", Payload(added));
        store.ResetForRebuild(Now);
        Assert.All(store.Renditions(Sha('a')), r => Assert.Null(r.JobId));
        Assert.Equal([Sha('a')], store.ShasNeedingJobs(NextProfile));
    }

    [Fact]
    public void ReapingAnExpiredLeaseDelaysTheDeletionByTheGrace()
    {
        var store = Create();
        AddMedia(store, Sha('a'));
        var id = store.EnsureEncodeJob(Sha('a'), null, Profile, Now)!.Value;
        var lease = store.Claim(Request())!;
        var grace = TimeSpan.FromMinutes(10);
        Assert.Equal(0, store.ReapExpiredLeases(MaxAttempts, grace, Now));
        _clock.Now = lease.LeaseExpiresAt;
        Assert.Equal(1, store.ReapExpiredLeases(MaxAttempts, grace, Now));
        Assert.Equal((JobStatus.Pending, "租约过期"), (Job(store, id).Status, Job(store, id).Error));
        Assert.Equal(Directories(lease), Deletions().Select(d => d.RelDir));
        Assert.All(Deletions(), d => Assert.Equal(("租约过期", Now + grace), (d.Reason, d.NotBefore)));
        Assert.Empty(store.DueDeletions(Now + grace - TimeSpan.FromMilliseconds(1), 100));
        Assert.Equal(PlannedKeys.Length, store.DueDeletions(Now + grace, 100).Count);
        Assert.False(store.Release(lease.Id, lease.LeaseToken, "late", MaxAttempts, Now));   // the old worker no longer has a lease
        Assert.Equal(0, store.ReapExpiredLeases(MaxAttempts, grace, Now));
    }

    // ---- Cancellation and rebuild

    [Fact]
    public void ACancelledRunningJobIsLeftToItsWorkerUntilItReleasesTheLease()
    {
        var store = Create();
        AddMedia(store, Sha('a'));
        var pending = store.EnsureEncodeJob(Sha('a'), null, Profile, Now)!.Value;
        store.CancelEncodeJob(Sha('a'), Now);
        Assert.Equal(JobStatus.Cancelled, Job(store, pending).Status);
        Assert.All(store.Renditions(Sha('a')), r => Assert.Null(r.JobId));
        var id = store.EnsureEncodeJob(Sha('a'), null, Profile, Now)!.Value;
        var lease = store.Claim(Request())!;
        store.CancelEncodeJob(Sha('a'), Now);
        Assert.Equal(JobStatus.Cancelled, Job(store, id).Status);
        Assert.All(store.Renditions(Sha('a')), r => Assert.Null(r.JobId));
        Assert.False(store.Heartbeat(lease.Id, lease.LeaseToken, Lease, Now));   // the worker gets 409 and stops
        Assert.Null(store.GetLease(lease.Id, lease.LeaseToken, Now));
        // No new run is created over a worker that may still be uploading.
        Assert.Null(store.EnsureEncodeJob(Sha('a'), null, Profile, Now));
        Assert.True(store.Release(lease.Id, lease.LeaseToken, "cancelled", MaxAttempts, Now));
        Assert.Equal(JobStatus.Cancelled, Job(store, id).Status);
        Assert.Equal(Directories(lease), Deletions().Select(d => d.RelDir));
        Assert.All(Deletions(), d => Assert.Equal(("Worker 报告失败", Now), (d.Reason, d.NotBefore)));
        Assert.False(store.Release(lease.Id, lease.LeaseToken, "cancelled", MaxAttempts, Now));
        Assert.NotEqual(id, store.EnsureEncodeJob(Sha('a'), null, Profile, Now));
    }

    [Fact]
    public void ReapingACancelledJobOnlyClearsItsLease()
    {
        var store = Create();
        AddMedia(store, Sha('a'));
        var id = store.EnsureEncodeJob(Sha('a'), null, Profile, Now)!.Value;
        var lease = store.Claim(Request())!;
        store.CancelAllJobs(Now);
        _clock.Now = lease.LeaseExpiresAt;
        var grace = TimeSpan.FromMinutes(2);
        Assert.Equal(1, store.ReapExpiredLeases(MaxAttempts, grace, Now));
        var job = Job(store, id);
        Assert.Equal((JobStatus.Cancelled, 1), (job.Status, job.Attempt));
        Assert.Null(job.Error);
        Assert.Equal(Directories(lease), Deletions().Select(d => d.RelDir));
        Assert.All(Deletions(), d => Assert.Equal(("租约过期", Now + grace), (d.Reason, d.NotBefore)));
        Assert.Equal(0, store.ReapExpiredLeases(MaxAttempts, grace, Now));
        Assert.NotNull(store.EnsureEncodeJob(Sha('a'), null, Profile, Now));
    }

    [Fact]
    public void CancelAllJobsCancelsPendingAndRunningJobsButNotVerifyingOnes()
    {
        var store = Create();
        AddMedia(store, Sha('a'));
        AddMedia(store, Sha('b'));
        var verifying = store.EnsureEncodeJob(Sha('a'), null, Profile, Now);
        var lease = store.Claim(Request(Sha('a')))!;
        Assert.True(store.SubmitOutputs(lease.Id, lease.LeaseToken, Plan(lease), Completion(lease), Now));
        store.EnsureEncodeJob(Sha('b'), null, Profile, Now);
        var running = store.Claim(Request(Sha('b')))!;
        var probeItem = Guid.NewGuid();
        store.UpsertSources([LibraryFile(probeItem)]);
        store.EnsureProbeJob(store.GetSource(probeItem)!, Now);
        store.CancelAllJobs(Now);
        Assert.Equal(new JobCounts(0, 0, 1, 0, 0, 2, 0, 2, 0), store.Counts());
        Assert.All(store.Renditions(Sha('a')), r => Assert.Equal(verifying, r.JobId));
        Assert.All(store.Renditions(Sha('b')), r => Assert.Null(r.JobId));
        // The running job keeps its lease, so its worker's directories are still queued when it gives up.
        Assert.True(store.Release(running.Id, running.LeaseToken, "cancelled", MaxAttempts, Now));
        Assert.Equal(Directories(running), Deletions().Select(d => d.RelDir));
    }

    [Fact]
    public void CancelJobOnlyCancelsAJobThatHasNotFinished()
    {
        var store = Create();
        var item = Guid.NewGuid();
        store.UpsertSources([LibraryFile(item)]);
        store.EnsureProbeJob(store.GetSource(item)!, Now);
        var probe = store.Claim(Request())!;
        store.CancelJob(probe.Id, Now);
        Assert.Equal(JobStatus.Cancelled, Job(store, probe.Id).Status);
        Assert.False(store.CompleteProbe(probe.Id, probe.LeaseToken, Now));
        AddMedia(store, Sha('a'));
        var pending = store.EnsureEncodeJob(Sha('a'), null, Profile, Now)!.Value;
        store.CancelJob(pending, Now);
        Assert.Equal(JobStatus.Cancelled, Job(store, pending).Status);
        Assert.All(store.Renditions(Sha('a')), r => Assert.Null(r.JobId));
        var published = EncodeAndPublish(store, Sha('a'));
        store.CancelJob(published.Id, Now);
        store.CancelJob(Guid.NewGuid(), Now);
        Assert.Equal(JobStatus.Complete, Job(store, published.Id).Status);
    }

    [Fact]
    public void CancelJobDiscardsTheOutputsOfAVerifyingJob()
    {
        var store = Create();
        AddMedia(store, Sha('a'));
        AddMedia(store, Sha('b'));
        var verifying = store.EnsureEncodeJob(Sha('a'), null, Profile, Now)!.Value;
        var submitted = store.Claim(Request(Sha('a')))!;
        Assert.True(store.SubmitOutputs(submitted.Id, submitted.LeaseToken, Plan(submitted), Completion(submitted), Now));
        var running = store.EnsureEncodeJob(Sha('b'), null, Profile, Now)!.Value;
        var lease = store.Claim(Request(Sha('b')))!;
        _clock.Now += TimeSpan.FromMinutes(1);
        store.CancelJob(verifying, Now);
        store.CancelJob(running, Now);
        Assert.Equal([JobStatus.Cancelled, JobStatus.Cancelled], new[] { verifying, running }.Select(id => Job(store, id).Status));
        Assert.All(store.Renditions(Sha('a')).Concat(store.Renditions(Sha('b'))), r => Assert.Null(r.JobId));
        // Nothing would ever publish or delete what the verifying job submitted, so it is discarded now.
        Assert.Empty(store.VerifyingJobs(10));
        Assert.Null(store.Publish(verifying, Profile, Now, ReplacedGrace));
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM job_outputs"));
        Assert.Equal(Directories(submitted), Deletions().Select(d => d.RelDir));
        Assert.All(Deletions(), d => Assert.Equal(("任务已取消", Now), (d.Reason, d.NotBefore)));
        // The running job's directories are left to its worker, which still holds the lease, as before.
        Assert.True(store.Release(lease.Id, lease.LeaseToken, "cancelled", MaxAttempts, Now));
        Assert.Equal(Ordinal(Directories(submitted).Concat(Directories(lease))), Deletions().Select(d => d.RelDir));
    }

    [Fact]
    public void CancelUnreferencedEncodeJobCancelsThePendingJobOnceNoFileReferencesTheHash()
    {
        var store = Create();
        var item = AddMedia(store, Sha('a'));
        var other = Guid.NewGuid();
        store.UpsertSources([LibraryFile(other)]);
        store.AssociateProbe(other, Sha('a'), Probe, Library, Now);
        var pending = store.EnsureEncodeJob(Sha('a'), item, Profile, Now)!.Value;
        // While a source still references the hash nothing changes.
        store.RemoveSource(item);
        Assert.False(store.CancelUnreferencedEncodeJob(Sha('a'), Now));
        Assert.Equal(JobStatus.Pending, Job(store, pending).Status);
        Assert.All(store.Renditions(Sha('a')), r => Assert.Equal(pending, r.JobId));
        store.RemoveSource(other);
        _clock.Now += TimeSpan.FromMinutes(1);
        // The hash is normalized first.
        Assert.True(store.CancelUnreferencedEncodeJob(Sha('a').ToUpperInvariant(), Now));
        Assert.Equal((JobStatus.Cancelled, Now), (Job(store, pending).Status, Job(store, pending).UpdatedAt));
        Assert.All(store.Renditions(Sha('a')), r => Assert.Null(r.JobId));
        Assert.False(store.CancelUnreferencedEncodeJob(Sha('a'), Now));   // nothing is pending any more
        Assert.NotNull(store.GetMedia(Sha('a')));                          // the media itself waits for the orphan retention
    }

    [Fact]
    public void CancelUnreferencedEncodeJobLeavesARunningJobAlone()
    {
        var store = Create();
        AddSubmittedMedia(store, Sha('a'));
        store.EnsureEncodeJob(Sha('a'), null, Profile, Now);
        var lease = store.Claim(Request(Sha('a')))!;
        Assert.False(store.CancelUnreferencedEncodeJob(Sha('a'), Now));
        Assert.Equal(JobStatus.Running, Job(store, lease.Id).Status);
        Assert.All(store.Renditions(Sha('a')), r => Assert.Equal(lease.Id, r.JobId));
        Assert.True(store.Heartbeat(lease.Id, lease.LeaseToken, Lease, Now));
    }

    [Fact]
    public void ACancelledUnreferencedEncodeJobIsCreatedAgainForAWorkerThatAsksForTheHash()
    {
        var store = Create();
        AddSubmittedMedia(store, Sha('a'));
        var cancelled = store.EnsureEncodeJob(Sha('a'), null, Profile, Now)!.Value;
        Assert.True(store.CancelUnreferencedEncodeJob(Sha('a'), Now));
        Assert.Equal([Sha('a')], store.ShasNeedingJobs(Profile));
        var revived = store.EnsureEncodeJob(Sha('a'), null, Profile, Now)!.Value;
        Assert.NotEqual(cancelled, revived);
        var job = Assert.Single(store.List(10));
        Assert.Equal((revived, JobStatus.Pending, 0), (job.Id, job.Status, job.Attempt));
        Assert.All(store.Renditions(Sha('a')), r => Assert.Equal(revived, r.JobId));
        Assert.Equal(revived, store.Claim(Request(Sha('a')))!.Id);
    }

    [Fact]
    public void RebuildCancelsPendingJobsAndLetsFailedProbesRunAgain()
    {
        var store = Create();
        var item = Guid.NewGuid();
        store.UpsertSources([LibraryFile(item)]);
        var source = store.GetSource(item)!;
        store.EnsureProbeJob(source, Now);
        var probe = store.Claim(Request())!;
        Assert.True(store.Release(probe.Id, probe.LeaseToken, "ffprobe failed", 1, Now));
        store.EnsureProbeJob(source, Now);   // scanning does not retry a failed probe
        Assert.Equal(JobStatus.Failed, Job(store, probe.Id).Status);
        Assert.Empty(Deletions());           // probes have no directories
        AddMedia(store, Sha('a'));
        var pending = store.EnsureEncodeJob(Sha('a'), null, Profile, Now)!.Value;
        store.ResetForRebuild(Now);
        Assert.Equal([JobStatus.Cancelled, JobStatus.Cancelled], new[] { probe.Id, pending }.Select(id => Job(store, id).Status));
        Assert.All(store.Renditions(Sha('a')), r => Assert.Null(r.JobId));
        store.EnsureProbeJob(source, Now);
        Assert.Equal((JobStatus.Pending, 0), (Job(store, probe.Id).Status, Job(store, probe.Id).Attempt));
        Assert.NotNull(store.EnsureEncodeJob(Sha('a'), null, Profile, Now));
    }

    // ---- Deletion queue

    [Fact]
    public void DeletionsAreHandedOutWhenDueAndCanBePostponed()
    {
        var store = Create();
        AddMedia(store, Sha('a'));
        store.EnsureEncodeJob(Sha('a'), null, Profile, Now);
        var lease = store.Claim(Request())!;
        Assert.True(store.Release(lease.Id, lease.LeaseToken, "failed", MaxAttempts, Now));
        var directories = Directories(lease).ToList();
        Assert.Empty(store.DueDeletions(Now - TimeSpan.FromMilliseconds(1), 10));
        Assert.Equal(directories.Take(2).Select(d => new DeletionRow(d, 0)), store.DueDeletions(Now, 2));
        var next = Now + TimeSpan.FromMinutes(2);
        store.PostponeDeletion(directories[0], "access denied", next);
        Assert.Equal(new Deletion(directories[0], next, "Worker 报告失败", 1, "access denied"), Deletions()[0]);
        Assert.Equal(directories.Skip(1), store.DueDeletions(Now, 10).Select(d => d.RelDir));
        Assert.Equal(new DeletionRow(directories[0], 1), store.DueDeletions(next, 10)[^1]);
        Assert.Equal(PlannedKeys.Length, store.Counts().PendingDeletions);
        foreach (var directory in directories) store.CompleteDeletion(directory);
        Assert.Empty(store.DueDeletions(next, 10));
        Assert.Equal(0, store.Counts().PendingDeletions);
    }

    [Fact]
    public void OnlyDirectoriesOfTheGeneratedLayoutCanBeQueuedForDeletion()
    {
        var store = Create();
        AddMedia(store, Sha('a'));
        store.EnsureEncodeJob(Sha('a'), null, Profile, Now);
        var lease = store.Claim(Request())!;
        var plan = Plan(lease);
        var tampered = plan with { Outputs = [plan.Outputs[0] with { RelativeDirectory = "../../etc" }, .. plan.Outputs[1..]] };
        Assert.True(store.SubmitOutputs(lease.Id, lease.LeaseToken, tampered, Completion(lease), Now));
        Assert.Throws<ArgumentException>(() => store.FailVerification(lease.Id, "校验失败", MaxAttempts, Now));
        // Rolled back as a whole: still verifying with every output, nothing queued.
        Assert.Equal(PlannedKeys.Length, Assert.Single(store.VerifyingJobs(10)).Outputs.Count);
        Assert.Empty(Deletions());
    }

    // ---- Version items

    [Fact]
    public void VersionItemsAreKeptPerItemAndQuality()
    {
        var store = Create();
        var item = Guid.NewGuid();
        var other = Guid.NewGuid();
        var auto = Guid.NewGuid();
        var full = Guid.NewGuid();
        store.SetVersionItem(item, 0, auto, "signature-0");
        store.SetVersionItem(item, 1080, full, "signature-1080");
        store.SetVersionItem(other, 720, Guid.NewGuid(), "signature-720");
        store.SetVersionItem(item, 1080, full, "signature-1080-b");
        var versions = store.VersionItems(item);
        Assert.Equal(2, versions.Count);
        Assert.Equal((auto, "signature-0"), versions[0]);
        Assert.Equal((full, "signature-1080-b"), versions[1080]);
        Assert.Equal(ByText(item, other), store.ItemsWithVersions());
        store.RemoveVersionItem(item, 0);
        Assert.Equal([1080], store.VersionItems(item).Keys);
        store.RemoveVersionItem(other, 720);
        Assert.Equal([item], store.ItemsWithVersions());
        Assert.Empty(store.VersionItems(Guid.NewGuid()));
    }

    // ---- Re-verification

    [Fact]
    public void RetireRenditionOnlyRetiresTheDirectoryThatWasChecked()
    {
        var store = Create();
        AddMedia(store, Sha('a'));
        AddMedia(store, Sha('b'));
        var lease = EncodeAndPublish(store, Sha('a'));
        EncodeAndPublish(store, Sha('b'));
        Assert.Equal(2 * PlannedKeys.Length, store.PublishedManifests(null).Count);
        var manifests = store.PublishedManifests(Sha('a'));
        Assert.Equal(Ordinal(PlannedKeys), manifests.Select(m => m.Key));
        var video = manifests.Single(m => m.Key == "v1080");
        Assert.Equal((Sha('a'), Plan(lease).Outputs[0].RelativeDirectory), (video.Sha256, video.RelDir));
        Assert.Equal(Completion(lease).Renditions[0].Files, video.Files);
        // A directory that is no longer the published one (published again meanwhile) is left alone.
        var stale = RenditionPaths.RelativeDirectory(RenditionPaths.MediaUuid(Sha('a')), RenditionPlanner.Desired(Probe, [1080])[0], new string('0', 32));
        Assert.False(store.RetireRendition(Sha('a'), "v1080", stale, "重新校验失败", Now));
        Assert.True(store.RetireRendition(Sha('a'), "v1080", video.RelDir, "重新校验失败：缺少文件 init.mp4", Now));
        Assert.Equal(new RenditionRow(Sha('a'), "v1080", RenditionState.Missing, null, null, null, null, Profile), store.Renditions(Sha('a')).Single(r => r.Key == "v1080"));
        Assert.Equal(new Deletion(video.RelDir, Now, "重新校验失败：缺少文件 init.mp4", 0, null), Assert.Single(Deletions()));
        Assert.DoesNotContain(store.PublishedManifests(Sha('a')), m => m.Key == "v1080");
        Assert.Equal(2 * PlannedKeys.Length - 1, store.PublishedManifests(null).Count);
        Assert.Equal([Sha('a')], store.ShasNeedingJobs(Profile));
        Assert.False(store.RetireRendition(Sha('a'), "v1080", video.RelDir, "again", Now));
    }

    [Fact]
    public void PublishedShasListsEveryMediaWithAPublishedRendition()
    {
        var store = Create();
        Assert.Empty(store.PublishedShas());
        AddMedia(store, Sha('b'));
        EncodeAndPublish(store, Sha('b'));
        AddMedia(store, Sha('a'));
        EncodeAndPublish(store, Sha('a'));
        AddMedia(store, Sha('c'));   // nothing published yet
        Assert.Equal([Sha('a'), Sha('b')], store.PublishedShas());
        var manifests = store.PublishedManifests(Sha('a'));
        foreach (var manifest in manifests.Skip(1)) Assert.True(store.RetireRendition(Sha('a'), manifest.Key, manifest.RelDir, "重新校验失败", Now));
        Assert.Equal([Sha('a'), Sha('b')], store.PublishedShas());   // one published rendition is enough
        Assert.True(store.RetireRendition(Sha('a'), manifests[0].Key, manifests[0].RelDir, "重新校验失败", Now));
        Assert.Equal([Sha('b')], store.PublishedShas());
    }

    [Fact]
    public void PublishedSampleListsTheMostRecentlyPublishedDirectories()
    {
        var store = Create();
        Assert.Empty(store.PublishedSample(5));
        AddMedia(store, Sha('a'));
        var older = EncodeAndPublish(store, Sha('a'));
        _clock.Now += TimeSpan.FromMinutes(1);
        AddMedia(store, Sha('b'));
        var newer = EncodeAndPublish(store, Sha('b'));
        AddMedia(store, Sha('c'));   // nothing published
        Assert.Equal(Directories(newer), Ordinal(store.PublishedSample(PlannedKeys.Length)));
        var all = store.PublishedSample(100);
        Assert.Equal(Directories(newer), Ordinal(all.Take(PlannedKeys.Length)));
        Assert.Equal(Directories(older), Ordinal(all.Skip(PlannedKeys.Length)));
        // A retired rendition has no directory any more.
        var retired = store.PublishedManifests(Sha('b'))[0];
        Assert.True(store.RetireRendition(Sha('b'), retired.Key, retired.RelDir, "重新校验失败", Now));
        Assert.Equal(Ordinal(Directories(newer).Concat(Directories(older)).Where(d => d != retired.RelDir)), Ordinal(store.PublishedSample(100)));
    }

    // ---- Administration

    [Fact]
    public void ListShowsTheMostRecentlyUpdatedJobsFirstAndCountsSummarizeTheStore()
    {
        var store = Create();
        AddMedia(store, Sha('a'));
        var published = EncodeAndPublish(store, Sha('a'));
        _clock.Now += TimeSpan.FromMinutes(1);
        var item = Guid.NewGuid();
        store.UpsertSources([LibraryFile(item)]);
        store.EnsureProbeJob(store.GetSource(item)!, Now);
        var probe = store.Claim(Request(worker: "prober"))!;
        _clock.Now += TimeSpan.FromMinutes(1);
        AddMedia(store, Sha('b'));
        var pending = store.EnsureEncodeJob(Sha('b'), null, Profile, Now)!.Value;
        var jobs = store.List(10);
        Assert.Equal([pending, probe.Id, published.Id], jobs.Select(job => job.Id));
        Assert.Equal(new JobSummary(probe.Id, JobKind.Probe, item, null, JobStatus.Running, 1, "prober", null, Now - TimeSpan.FromMinutes(1)), jobs[1]);
        Assert.Equal(new JobSummary(published.Id, JobKind.Encode, null, Sha('a'), JobStatus.Complete, 1, "worker", null, Now - TimeSpan.FromMinutes(2)), jobs[2]);
        Assert.Equal([pending], store.List(1).Select(job => job.Id));
        Assert.Equal(new JobCounts(Pending: 1, Running: 1, Verifying: 0, Complete: 1, Failed: 0, Cancelled: 0, PendingDeletions: 0, Media: 2, PublishedRenditions: PlannedKeys.Length), store.Counts());
    }

    // ---- Helpers

    private JobStore Create() => new(DatabasePath, _clock);

    private static string Sha(char c) => new(c, 64);

    private static SourceFile LibraryFile(Guid item) => new(item, Library, $"/media/{item:N}.mkv", 1000, Modified);

    // A library file probed as sha, with its renditions planned: the state right after its probe job.
    private Guid AddMedia(JobStore store, string sha, int[]? qualities = null)
    {
        var item = Guid.NewGuid();
        store.UpsertSources([LibraryFile(item)]);
        store.AssociateProbe(item, sha, Probe, Library, Now);
        Assert.True(store.SyncRenditions(sha, RenditionPlanner.Desired(Probe, qualities ?? Qualities), Now));
        return item;
    }

    // A file a worker submitted without a library item.
    private void AddSubmittedMedia(JobStore store, string sha)
    {
        store.AssociateProbe(null, sha, Probe, Library, Now);
        Assert.True(store.SyncRenditions(sha, RenditionPlanner.Desired(Probe, Qualities), Now));
    }

    private ClaimRequest Request(string? sha = null, string worker = "worker", Func<string, bool>? eligible = null, Func<MediaRecord, IReadOnlyList<RenditionSpec>>? desired = null) =>
        new(worker, Lease, sha, OutputRoot, desired ?? (m => RenditionPlanner.Desired(m.Probe, Qualities)), eligible ?? (_ => true), Now);

    private static EncodePlan Plan(JobLease lease) => JsonSerializer.Deserialize<EncodePlan>(lease.Payload)!;

    private static IEnumerable<string> Directories(JobLease lease) => Ordinal(Plan(lease).Outputs.Select(o => o.RelativeDirectory));

    // What a worker reports: every target of the plan with its files.
    private static EncodeCompletion Completion(JobLease lease) => new(lease.LeaseToken, [.. Plan(lease).Outputs.Select(o => new RenditionOutput(o.Key,
        new RenditionSummary(o.Video?.Name ?? 0, o.Video?.Width ?? 0, o.Video?.Height ?? 0, o.Video?.FramesPerSecond ?? 0, 5000000, 4000000, "avc1.640028", o.SourceIndex, Channels: o.Channels),
        [new OutputFile("index.m3u8", 120, new DateTimeOffset(Modified)), new OutputFile("init.mp4", 900, new DateTimeOffset(Modified))]))]);

    // Ensures, claims (directed), submits and publishes the encode job of sha.
    private JobLease EncodeAndPublish(JobStore store, string sha, string profile = Profile)
    {
        var id = store.EnsureEncodeJob(sha, null, profile, Now);
        var lease = store.Claim(Request(sha))!;
        Assert.Equal(id, lease.Id);
        Assert.True(store.SubmitOutputs(lease.Id, lease.LeaseToken, Plan(lease), Completion(lease), Now));
        Assert.Equal(sha, store.Publish(lease.Id, profile, Now, ReplacedGrace)!.Sha256);
        return lease;
    }

    private static JobSummary Job(JobStore store, Guid? id) => store.List(1000).Single(job => job.Id == id);

    private static string Json(object value) => JsonSerializer.Serialize(value);

    private static IEnumerable<string> Ordinal(IEnumerable<string> values) => values.Order(StringComparer.Ordinal);

    // Guids in the order the store lists them: by their "N" text.
    private static IEnumerable<Guid> ByText(params Guid[] ids) => ids.OrderBy(id => id.ToString("N"), StringComparer.Ordinal);

    private sealed record Deletion(string RelDir, DateTimeOffset NotBefore, string Reason, int Attempts, string? LastError);

    private List<Deletion> Deletions() => Query("SELECT rel_dir,not_before,reason,attempts,last_error FROM deletions ORDER BY rel_dir",
        r => new Deletion(r.GetString(0), DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(1)), r.GetString(2), r.GetInt32(3), r.IsDBNull(4) ? null : r.GetString(4)));

    private List<string> Tables() => Query("SELECT name FROM sqlite_master WHERE type='table' ORDER BY name", r => r.GetString(0));

    private string Payload(Guid id) => (string)Scalar("SELECT payload FROM jobs WHERE id=$id", ("$id", id.ToString("N")))!;

    private static SqliteCommand Command(SqliteConnection db, string sql, (string Name, object Value)[] parameters)
    {
        var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        return cmd;
    }

    private SqliteConnection Connect()
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath }.ToString());
        db.Open();
        return db;
    }

    private void Execute(string sql, params (string Name, object Value)[] parameters)
    {
        using var db = Connect();
        using var cmd = Command(db, sql, parameters);
        cmd.ExecuteNonQuery();
    }

    private object? Scalar(string sql, params (string Name, object Value)[] parameters)
    {
        using var db = Connect();
        using var cmd = Command(db, sql, parameters);
        return cmd.ExecuteScalar();
    }

    private List<T> Query<T>(string sql, Func<SqliteDataReader, T> map)
    {
        using var db = Connect();
        using var cmd = Command(db, sql, []);
        using var reader = cmd.ExecuteReader();
        var rows = new List<T>();
        while (reader.Read()) rows.Add(map(reader));
        return rows;
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 17, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
