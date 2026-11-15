using System;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Common;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.MediaEncoding.Encoder;
using Xunit;

namespace Tesserafin.MediaEncoding.Tests.Extraction;

public sealed class AtomicCachePublicationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrCancelledWriter_NeverTouchesExistingCache(bool cancel)
    {
        using var context = new ExtractionTestContext();
        var output = Path.Combine(context.Root, "old.srt");
        await File.WriteAllTextAsync(output, "old complete", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(output + ".meta", "old stamp", TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        string? temporary = null;
        var runner = Runner(async token =>
        {
            await File.WriteAllTextAsync(temporary!, "partial", token);
            Assert.Equal("old complete", await File.ReadAllTextAsync(output, token));
            Assert.Equal("old stamp", await File.ReadAllTextAsync(output + ".meta", token));
            if (cancel)
            {
                await cancellation.CancelAsync();
                token.ThrowIfCancellationRequested();
            }

            return new FfmpegProcessResult(7, string.Empty, "failure", false);
        });
        var executor = new FfmpegFileExecutor(runner.Object, context.FileSystem.Object, NullLogger.Instance);
        var error = await Record.ExceptionAsync(() => executor.ExecuteAsync(
            staged =>
            {
                temporary = staged[0];
                Assert.NotEqual(output, temporary);
                Assert.NotEqual(Path.GetDirectoryName(output), Path.GetDirectoryName(temporary));
                return Command();
            },
            [output],
            TimeSpan.FromSeconds(5),
            cancellation.Token));
        if (cancel)
        {
            Assert.IsAssignableFrom<OperationCanceledException>(error);
        }
        else
        {
            Assert.IsType<FfmpegException>(error);
        }

        Assert.Equal("old complete", await File.ReadAllTextAsync(output, TestContext.Current.CancellationToken));
        Assert.Equal("old stamp", await File.ReadAllTextAsync(output + ".meta", TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(Path.GetDirectoryName(temporary)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrEmptyBatchMember_PublishesNothing(bool empty)
    {
        using var context = new ExtractionTestContext();
        var first = Path.Combine(context.Root, "first.srt");
        var second = Path.Combine(context.Root, "second.srt");
        await File.WriteAllTextAsync(first, "old first", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(second, "old second", TestContext.Current.CancellationToken);
        string[] staged = [];
        var runner = Runner(async token =>
        {
            await File.WriteAllTextAsync(staged[0], "new first", token);
            if (empty)
            {
                await File.WriteAllTextAsync(staged[1], string.Empty, token);
            }

            return Success();
        });
        var executor = new FfmpegFileExecutor(runner.Object, context.FileSystem.Object, NullLogger.Instance);
        await Assert.ThrowsAsync<FfmpegException>(() => executor.ExecuteAsync(
            paths =>
            {
                staged = [.. paths];
                return Command();
            },
            [first, second],
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken));
        Assert.Equal("old first", await File.ReadAllTextAsync(first, TestContext.Current.CancellationToken));
        Assert.Equal("old second", await File.ReadAllTextAsync(second, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("error")]
    [InlineData("cancel")]
    [InlineData("empty")]
    public async Task PostprocessingMustFinishAndValidateBeforeAnyReplacement(string failure)
    {
        using var context = new ExtractionTestContext();
        var output = Path.Combine(context.Root, "subtitle.ass");
        await File.WriteAllTextAsync(output, "old content", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(output + ".meta", "old stamp", TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        string? stagedPath = null;
        var runner = Runner(async token =>
        {
            await File.WriteAllTextAsync(stagedPath!, "fresh", token);
            return Success();
        });
        var executor = new FfmpegFileExecutor(runner.Object, context.FileSystem.Object, NullLogger.Instance);
        var expected = new IOException("postprocessing failed");
        var error = await Record.ExceptionAsync(() => executor.ExecuteAsync(
            staged =>
            {
                stagedPath = staged[0];
                return Command();
            },
            [output],
            TimeSpan.FromSeconds(5),
            cancellation.Token,
            prepare: async (publication, token) =>
            {
                await publication.WriteMetadataAsync(0, "new stamp", token);
                if (failure == "error")
                {
                    throw expected;
                }

                if (failure == "cancel")
                {
                    await cancellation.CancelAsync();
                    return;
                }

                await File.WriteAllTextAsync(stagedPath!, string.Empty, token);
            }));
        if (failure == "error")
        {
            Assert.Same(expected, error);
        }
        else if (failure == "cancel")
        {
            Assert.IsAssignableFrom<OperationCanceledException>(error);
        }
        else
        {
            Assert.IsType<FfmpegException>(error);
        }

        Assert.Equal("old content", await File.ReadAllTextAsync(output, TestContext.Current.CancellationToken));
        Assert.Equal("old stamp", await File.ReadAllTextAsync(output + ".meta", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Replacement_PreservesOldOpenReader_AndPublishesPreparedContentWithMetadata()
    {
        using var context = new ExtractionTestContext();
        var output = Path.Combine(context.Root, "subtitle.ass");
        await File.WriteAllTextAsync(output, "old complete", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(output + ".meta", "old stamp", TestContext.Current.CancellationToken);
        await using var oldStream = new FileStream(output, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var publication = new AtomicCachePublication([output], context.FileSystem.Object, NullLogger.Instance);
        await File.WriteAllTextAsync(publication.StagedPaths[0], "new postprocessed content", TestContext.Current.CancellationToken);
        await publication.WriteMetadataAsync(0, "new stamp", TestContext.Current.CancellationToken);
        publication.Validate();
        Assert.Equal("old complete", await File.ReadAllTextAsync(output, TestContext.Current.CancellationToken));
        publication.Publish(TestContext.Current.CancellationToken);
        using var oldReader = new StreamReader(oldStream, leaveOpen: true);
        Assert.Equal("old complete", await oldReader.ReadToEndAsync(TestContext.Current.CancellationToken));
        Assert.Equal("new postprocessed content", await File.ReadAllTextAsync(output, TestContext.Current.CancellationToken));
        Assert.Equal("new stamp", await File.ReadAllTextAsync(output + ".meta", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MetadataRenameFailure_DoesNotBlessOldDataWithNewStamp()
    {
        using var context = new ExtractionTestContext();
        var output = Path.Combine(context.Root, "subtitle.srt");
        await File.WriteAllTextAsync(output, "old content", TestContext.Current.CancellationToken);
        Directory.CreateDirectory(output + ".meta");
        using var publication = new AtomicCachePublication([output], context.FileSystem.Object, NullLogger.Instance);
        await File.WriteAllTextAsync(publication.StagedPaths[0], "complete new content", TestContext.Current.CancellationToken);
        await publication.WriteMetadataAsync(0, "new stamp", TestContext.Current.CancellationToken);
        publication.Validate();
        Assert.Throws<IOException>(() => publication.Publish(TestContext.Current.CancellationToken));
        Assert.Equal("complete new content", await File.ReadAllTextAsync(output, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(output + ".meta"));
        Assert.True(Directory.Exists(output + ".meta"));
    }

    [Fact]
    public async Task DataDisappearsAfterValidation_DoesNotPublishItsNewMetadata()
    {
        using var context = new ExtractionTestContext();
        var output = Path.Combine(context.Root, "subtitle.srt");
        await File.WriteAllTextAsync(output, "old complete", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(output + ".meta", "old stamp", TestContext.Current.CancellationToken);
        using var publication = new AtomicCachePublication([output], context.FileSystem.Object, NullLogger.Instance);
        var staged = publication.StagedPaths[0];
        await File.WriteAllTextAsync(staged, "new complete", TestContext.Current.CancellationToken);
        await publication.WriteMetadataAsync(0, "new stamp", TestContext.Current.CancellationToken);
        context.FileSystem.Setup(fs => fs.GetFileInfo(staged)).Returns(() =>
        {
            // Simulate the filesystem changing immediately after a successful stat.
            File.Delete(staged);
            return new Tesserafin.Model.IO.FileSystemMetadata { Exists = true, Length = 12 };
        });
        Assert.Throws<IOException>(() => publication.Publish(TestContext.Current.CancellationToken));
        Assert.Equal("old complete", await File.ReadAllTextAsync(output, TestContext.Current.CancellationToken));
        Assert.Equal("old stamp", await File.ReadAllTextAsync(output + ".meta", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task BatchRenameFailure_LeavesOnlyCompletePublishedPrefix(int failingIndex)
    {
        using var context = new ExtractionTestContext();
        var paths = new[] { Path.Combine(context.Root, "first.srt"), Path.Combine(context.Root, "second.srt") };
        for (var index = 0; index < paths.Length; index++)
        {
            if (index == failingIndex)
            {
                Directory.CreateDirectory(paths[index]);
            }
            else
            {
                await File.WriteAllTextAsync(paths[index], "old complete", TestContext.Current.CancellationToken);
            }
        }

        using var publication = new AtomicCachePublication(paths, context.FileSystem.Object, NullLogger.Instance);
        foreach (var staged in publication.StagedPaths)
        {
            await File.WriteAllTextAsync(staged, "new complete", TestContext.Current.CancellationToken);
        }

        publication.Validate();
        Assert.Throws<IOException>(() => publication.Publish(TestContext.Current.CancellationToken));
        var surviving = failingIndex == 0 ? 1 : 0;
        Assert.Equal(failingIndex == 0 ? "old complete" : "new complete", await File.ReadAllTextAsync(paths[surviving], TestContext.Current.CancellationToken));
        Assert.True(Directory.Exists(paths[failingIndex]));
    }

    [Fact]
    public async Task CrossDevicePublication_FailsWithoutCopyingOrDeletingEitherFile()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() && Directory.Exists("/dev/shm"), "Requires Linux tmpfs");
        using var context = new ExtractionTestContext();
        var source = Path.Combine(context.Root, "new.srt");
        var target = Path.Combine("/dev/shm", "tesserafin-atomic-" + Guid.NewGuid().ToString("N"));
        try
        {
            await File.WriteAllTextAsync(source, "complete new", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(target, "complete old", TestContext.Current.CancellationToken);
            var error = Assert.Throws<IOException>(() => AtomicCachePublication.Rename(source, target));
            Assert.Equal(18, Assert.IsType<Win32Exception>(error.InnerException).NativeErrorCode);
            Assert.Equal("complete old", await File.ReadAllTextAsync(target, TestContext.Current.CancellationToken));
            Assert.Equal("complete new", await File.ReadAllTextAsync(source, TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(target);
        }
    }

    [Fact]
    public async Task UndeletableFailedStaging_IsNotACacheHit_AndDoesNotPreventRetry()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Requires Unix directory permissions");
        using var context = new ExtractionTestContext();
        var probe = Directory.CreateDirectory(Path.Combine(context.Root, "permission-probe")).FullName;
        var probeFile = Path.Combine(probe, "file");
        await File.WriteAllTextAsync(probeFile, "probe", TestContext.Current.CancellationToken);
        Exception? denied;
        try
        {
            SetPermissions(probe, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            denied = Record.Exception(() => File.Delete(probeFile));
        }
        finally
        {
            SetPermissions(probe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Assert.SkipUnless(denied is IOException or UnauthorizedAccessException, "Filesystem bypasses directory modes");
        var output = Path.Combine(context.Root, "cached.srt");
        await File.WriteAllTextAsync(output, "old complete", TestContext.Current.CancellationToken);
        string? current = null;
        string? failedDirectory = null;
        var calls = 0;
        var runner = Runner(async token =>
        {
            await File.WriteAllTextAsync(current!, ++calls == 1 ? "partial" : "new complete", token);
            if (calls == 1)
            {
                failedDirectory = Path.GetDirectoryName(current)!;
                SetPermissions(failedDirectory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            }

            return new FfmpegProcessResult(calls == 1 ? 7 : 0, string.Empty, "failure", false);
        });
        var executor = new FfmpegFileExecutor(runner.Object, context.FileSystem.Object, NullLogger.Instance);
        Task Run() => executor.ExecuteAsync(
            staged =>
            {
                current = staged[0];
                return Command();
            },
            [output],
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        try
        {
            await Assert.ThrowsAsync<FfmpegException>(Run);
            Assert.True(Directory.Exists(failedDirectory));
            Assert.Equal("old complete", await File.ReadAllTextAsync(output, TestContext.Current.CancellationToken));
            await Run();
            Assert.Equal("new complete", await File.ReadAllTextAsync(output, TestContext.Current.CancellationToken));
            Assert.True(Directory.Exists(failedDirectory));
            Assert.NotEqual(Path.GetDirectoryName(output), failedDirectory);
        }
        finally
        {
            if (failedDirectory is not null && Directory.Exists(failedDirectory))
            {
                SetPermissions(failedDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                Directory.Delete(failedDirectory, recursive: true);
            }
        }
    }

    private static void SetPermissions(string path, UnixFileMode mode)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, mode);
        }
    }

    private static FfmpegCommand Command() => FfmpegCommand.FromArgumentLine("ffmpeg", "staged command");

    private static FfmpegProcessResult Success() => new(0, string.Empty, string.Empty, false);

    private static Mock<IFfmpegProcessRunner> Runner(Func<CancellationToken, Task<FfmpegProcessResult>> callback)
    {
        var runner = new Mock<IFfmpegProcessRunner>();
        runner.Setup(r => r.RunFileAsync(It.IsAny<FfmpegCommand>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Returns((FfmpegCommand _, TimeSpan _, CancellationToken token) => callback(token));
        return runner;
    }
}
