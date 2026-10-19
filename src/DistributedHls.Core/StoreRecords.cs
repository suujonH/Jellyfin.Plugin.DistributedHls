namespace DistributedHls.Core;

public enum SourceChange { Unchanged, Inserted, ContentChanged }
// PreviousSha is the hash the row had before the call: null for Inserted, the kept hash for Unchanged, the cleared hash for ContentChanged.
public sealed record UpsertResult(Guid ItemId, SourceChange Change, string? PreviousSha);
public sealed record StoredSource(SourceFile File, string? Sha256);
public sealed record MediaRecord(string Sha256, Guid MediaUuid, Guid LibraryId, ProbeInfo Probe, DateTimeOffset? OrphanedAt);
public enum RenditionState { Missing = 0, Published = 2, Failed = 3 }
public sealed record RenditionRow(string Sha256, string Key, RenditionState State, Guid? JobId, Guid? PublishedJobId, string? RelDir, RenditionSummary? Summary, string? Profile);
public sealed record VerifyingOutput(string Key, string RelDir, OutputFile[] Files);
public sealed record VerifyingJob(Guid Id, string Sha256, int Attempt, IReadOnlyList<VerifyingOutput> Outputs);
public sealed record PublishedManifest(string Sha256, string Key, string RelDir, OutputFile[] Files);
public sealed record DeletionRow(string RelDir, int Attempts);
public sealed record JobCounts(int Pending, int Running, int Verifying, int Complete, int Failed, int Cancelled, int PendingDeletions, int Media, int PublishedRenditions);
public sealed record PublishResult(string Sha256, IReadOnlyList<string> PublishedKeys);
public sealed record ClaimRequest(string Worker, TimeSpan Lease, string? Sha256, string OutputRoot,
    Func<MediaRecord, IReadOnlyList<RenditionSpec>> Desired, Func<string, bool> ShaEligible, DateTimeOffset Now);
public sealed class ProbeMismatchException(string message) : ArgumentException(message);
