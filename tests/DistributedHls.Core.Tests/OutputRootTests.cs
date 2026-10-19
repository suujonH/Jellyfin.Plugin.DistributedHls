using Xunit;

namespace DistributedHls.Core.Tests;

public sealed class OutputRootTests : IDisposable
{
    private const string Published = "0123456789abcdef0123456789abcdef/video/1080/fedcba9876543210fedcba9876543210";
    private const string Other = "0123456789abcdef0123456789abcdef/audio/1/00112233445566778899aabbccddeeff";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "distributed-hls-tests", Guid.NewGuid().ToString("N"));

    public OutputRootTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    private string Root => Path.Combine(_directory, "hls");
    private string Marker => Path.Combine(Root, OutputRoot.MarkerName);

    [Fact]
    public void AMissingRootIsAProblem()
    {
        Assert.Equal($"输出目录不存在或无法访问：{Root}", OutputRoot.Check(Root, []));
        Assert.Equal($"输出目录不存在或无法访问：{Root}", OutputRoot.Check(Root, [Published]));
        Assert.False(Directory.Exists(Root));
    }

    [Fact]
    public void AnEmptyRootWithNothingPublishedIsUsableAndStaysEmpty()
    {
        Directory.CreateDirectory(Root);
        Assert.Null(OutputRoot.Check(Root, []));
        // It may be a mount point that has to stay empty until it is mounted.
        Assert.Empty(Directory.EnumerateFileSystemEntries(Root));
    }

    [Fact]
    public void ARootWithTheMarkerIsUsableWhateverIsPublished()
    {
        Directory.CreateDirectory(Root);
        File.WriteAllText(Marker, "kept");
        // The directories may be gone for other reasons (deleted by hand, never uploaded); that is for the verification to report.
        Assert.Null(OutputRoot.Check(Root, [Published, Other]));
        Assert.Equal("kept", File.ReadAllText(Marker));
    }

    [Fact]
    public void ARootThatPredatesTheMarkerGetsItWhenOnePublishedDirectoryIsThere()
    {
        Directory.CreateDirectory(RenditionPaths.Full(Root, Published));
        Assert.Null(OutputRoot.Check(Root, [Other, Published]));
        Assert.True(File.Exists(Marker));
    }

    [Fact]
    public void AnUnmarkedRootWithoutAnyPublishedDirectoryIsAProblemAndIsNotMarked()
    {
        // What an empty mount point looks like while the mount is missing.
        Directory.CreateDirectory(Path.Combine(Root, "lost+found"));
        Assert.Equal($"输出目录缺少标记文件 {OutputRoot.MarkerName}，已发布的成果也不在其中，可能尚未挂载：{Root}", OutputRoot.Check(Root, [Published, Other]));
        Assert.False(File.Exists(Marker));
    }

    [Fact]
    public void ARootThatCannotBeReadIsAProblem()
    {
        // EACCES: the S3 plugin protects a mount point whose s3fs exited with mode 000. Windows and root have no such failure.
        if (OperatingSystem.IsWindows()) return;
        if (Environment.UserName == "root") return;
        Directory.CreateDirectory(Root);
        File.SetUnixFileMode(Root, UnixFileMode.None);
        try
        {
            Assert.StartsWith("输出目录不可用：", OutputRoot.Check(Root, []));
        }
        finally
        {
            File.SetUnixFileMode(Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        Assert.False(File.Exists(Marker));
    }
}
