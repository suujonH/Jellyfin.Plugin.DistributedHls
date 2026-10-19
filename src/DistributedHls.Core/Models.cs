namespace DistributedHls.Core;

public sealed record ProbeInfo(
    int Width, int Height, double FramesPerSecond, double DurationSeconds, string VideoCodec,
    string PixelFormat, string ColorTransfer, string FieldOrder, int Rotation,
    AudioTrack[] Audio, SubtitleTrack[] Subtitles,
    int VideoIndex = 0, int SarNumerator = 1, int SarDenominator = 1);
public sealed record AudioTrack(int Index, string Codec, int Channels, int SampleRate, string Language, string Title, bool Default);
public sealed record SubtitleTrack(int Index, string Codec, string Language, string Title, bool Default);
public sealed record Quality(int Name, int Height, int Width, double FramesPerSecond);
public sealed record SourceFile(Guid ItemId, Guid LibraryId, string Path, long Length, DateTime LastWriteTimeUtc);
public enum JobKind { Probe = 0, Encode = 1 }
public enum JobStatus { Pending = 0, Running = 1, Verifying = 2, Complete = 3, Failed = 4, Cancelled = 5 }
public enum RenditionKind { Video = 0, Audio = 1, Subtitle = 2, Preview = 3 }
public sealed record RenditionTarget(
    string Key, RenditionKind Kind, string Directory, string RelativeDirectory,
    Quality? Video = null, int SourceIndex = -1, int Channels = 0);   // Directory=服务器绝对路径；RelativeDirectory 仅服务端使用
public sealed record EncodePlan(string Sha256, Guid MediaUuid, ProbeInfo Probe, SourceFile? Source, RenditionTarget[] Outputs);
public sealed record JobLease(Guid Id, string WorkKey, JobKind Kind, Guid? ItemId, string Payload, string LeaseToken, DateTimeOffset LeaseExpiresAt, int Attempt);
public sealed record WorkAssignment(JobLease Lease, string? DownloadUrl);   // 相对路径；null 表示由 Worker 本地提供输入
public sealed record PollRequest(string Worker, string? Sha256 = null, string? ProbeJson = null, Guid? ItemId = null, Guid? LibraryId = null);
public sealed record ProbeCompletion(string LeaseToken, string Sha256, string ProbeJson);
public sealed record OutputFile(string Path, long Length, DateTimeOffset LastModified);
public sealed record RenditionSummary(
    int Quality = 0, int Width = 0, int Height = 0, double FramesPerSecond = 0, int Bandwidth = 0,
    int AverageBandwidth = 0, string Codec = "", int SourceIndex = -1, string Language = "", string Title = "", int Channels = 0, bool Default = false);
public sealed record RenditionOutput(string Key, RenditionSummary Summary, OutputFile[] Files);   // Files 相对该档位目录
public sealed record EncodeCompletion(string LeaseToken, RenditionOutput[] Renditions);
public sealed record LeaseRequest(string LeaseToken);
public sealed record FailureRequest(string LeaseToken, string Error);
public sealed record JobSummary(Guid Id, JobKind Kind, Guid? ItemId, string? Sha256, JobStatus Status, int Attempt, string? Worker, string? Error, DateTimeOffset UpdatedAt);
