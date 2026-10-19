using Xunit;

namespace DistributedHls.Core.Tests;

public sealed class OutputVerifierTests : IDisposable
{
    private static readonly DateTime Stamp = new(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "distributed-hls-tests", Guid.NewGuid().ToString("N"));

    public OutputVerifierTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    // Creates the file with an explicit modification time and returns the manifest entry that describes it.
    private OutputFile Create(string relative, string content = "hello", DateTime? modified = null)
    {
        var path = Path.Combine(_directory, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, modified ?? Stamp);
        return new(relative, new FileInfo(path).Length, new DateTimeOffset(modified ?? Stamp));
    }

    [Fact]
    public void PassesWhenEveryFileMatches()
    {
        OutputFile[] files = [Create("index.m3u8"), Create("segments/segment-000001.m4s", "0123456789")];
        Assert.Null(OutputVerifier.Verify(_directory, files, true));
        Assert.Null(OutputVerifier.Verify(_directory, files, false));
    }

    [Fact]
    public void PassesForAnEmptyManifestInAnExistingDirectory() =>
        Assert.Null(OutputVerifier.Verify(_directory, [], true));

    [Fact]
    public void ReportsAMissingDirectory()
    {
        var missing = Path.Combine(_directory, "missing");
        Assert.Equal($"目录不存在：{missing}", OutputVerifier.Verify(missing, [new("index.m3u8", 5, new DateTimeOffset(Stamp))], true));
    }

    [Fact]
    public void ReportsAMissingFile() =>
        Assert.Equal("缺少文件 init.mp4", OutputVerifier.Verify(_directory, [Create("index.m3u8"), new("init.mp4", 5, new DateTimeOffset(Stamp))], true));

    [Fact]
    public void ReportsAFolderOrAnUnreachableEntryAsMissing()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "segments"));
        var file = Create("index.m3u8");
        Assert.Equal("缺少文件 segments", OutputVerifier.Verify(_directory, [new("segments", 0, new DateTimeOffset(Stamp))], true));
        Assert.Equal("缺少文件 index.m3u8/child", OutputVerifier.Verify(_directory, [file, new("index.m3u8/child", 0, new DateTimeOffset(Stamp))], true));
    }

    [Fact]
    public void ReportsADifferentLength()
    {
        var file = Create("segment.m4s", "12345");
        Assert.Equal("segment.m4s 长度为 5，期望 4", OutputVerifier.Verify(_directory, [file with { Length = 4 }], true));
        Assert.Equal("segment.m4s 长度为 5，期望 6", OutputVerifier.Verify(_directory, [file with { Length = 6 }], false));
    }

    [Fact]
    public void ReportsADifferentModifiedTimeBeyondTwoSeconds()
    {
        var file = Create("index.m3u8", modified: Stamp.AddSeconds(3));
        Assert.Equal("index.m3u8 修改时间不一致", OutputVerifier.Verify(_directory, [file with { LastModified = new DateTimeOffset(Stamp) }], true));
        var older = Create("older.m3u8", modified: Stamp.AddSeconds(-3));
        Assert.Equal("older.m3u8 修改时间不一致", OutputVerifier.Verify(_directory, [older with { LastModified = new DateTimeOffset(Stamp) }], true));
    }

    [Fact]
    public void ToleratesUpToTwoSecondsOfModifiedTimeDifference()
    {
        OutputFile[] files =
        [
            Create("a.bin", modified: Stamp.AddSeconds(1)) with { LastModified = new DateTimeOffset(Stamp) },
            Create("b.bin", modified: Stamp.AddSeconds(2)) with { LastModified = new DateTimeOffset(Stamp) },
            Create("c.bin", modified: Stamp.AddSeconds(-2)) with { LastModified = new DateTimeOffset(Stamp) }
        ];
        Assert.Null(OutputVerifier.Verify(_directory, files, true));
    }

    [Fact]
    public void ComparesInstantsRegardlessOfTheReportedOffset()
    {
        var file = Create("index.m3u8");
        Assert.Null(OutputVerifier.Verify(_directory, [file with { LastModified = file.LastModified.ToOffset(TimeSpan.FromHours(8)) }], true));
    }

    [Fact]
    public void IgnoresModifiedTimeWhenTheCheckIsOff()
    {
        var file = Create("index.m3u8", modified: Stamp.AddDays(1));
        OutputFile[] reported = [file with { LastModified = new DateTimeOffset(Stamp) }];
        Assert.Null(OutputVerifier.Verify(_directory, reported, false));
        Assert.NotNull(OutputVerifier.Verify(_directory, reported, true));
    }

    [Fact]
    public void ReportsTheFirstProblemInManifestOrder()
    {
        var wrong = Create("a.bin", "12345", Stamp.AddDays(1)) with { Length = 9 };
        var missing = new OutputFile("b.bin", 1, new DateTimeOffset(Stamp));
        // Length is reported before the modified time of the same file, and earlier entries before later ones.
        Assert.Equal("a.bin 长度为 5，期望 9", OutputVerifier.Verify(_directory, [wrong with { LastModified = new DateTimeOffset(Stamp) }, missing], true));
        Assert.Equal("缺少文件 b.bin", OutputVerifier.Verify(_directory, [missing, wrong], true));
    }

    [Fact]
    public void ReportsUnsafePathsInsteadOfThrowing()
    {
        var error = OutputVerifier.Verify(_directory, [new("../outside.bin", 1, new DateTimeOffset(Stamp))], true);
        Assert.NotNull(error);
        Assert.StartsWith("读取失败：", error);
    }
}
