using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.Movies;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.MediaEncoding.Attachments;
using Tesserafin.MediaEncoding.Subtitles;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.Dto;
using Tesserafin.Model.Entities;
using Tesserafin.Model.MediaInfo;
using Xunit;

namespace Tesserafin.MediaEncoding.Tests.Extraction;

public sealed class ExtractionConsumerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealMatroska_AttachmentsUseConfinedUniquePaths_AndCacheSurvivesAnotherAttempt(bool attachmentOnly)
    {
        using var context = new ExtractionTestContext();
        var source = await Fixture(context, attachmentOnly);
        using var extractor = Attachments(context, source, context.Runner);
        await extractor.ExtractAllAttachments(source.Path, source, TestContext.Current.CancellationToken);
        var folder = context.Paths.Object.GetAttachmentFolderPath(source.Id)!;
        var output = Assert.Single(Directory.GetFiles(folder));
        Assert.Equal(source.MediaAttachments[0].Index + "-font.bin", Path.GetFileName(output));
        Assert.Equal("attachment payload", await File.ReadAllTextAsync(output, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(Path.Combine(context.Root, "font.bin")));

        // A later missing attachment must not invalidate a file from a successful attempt.
        var cached = await File.ReadAllBytesAsync(output, TestContext.Current.CancellationToken);
        source.MediaAttachments = [.. source.MediaAttachments, new MediaAttachment { Index = 99, FileName = "missing.bin", Codec = "bin" }];
        await Assert.ThrowsAsync<Tesserafin.Common.FfmpegException>(() => extractor.ExtractAllAttachments(source.Path, source, TestContext.Current.CancellationToken));
        Assert.Equal(cached, await File.ReadAllBytesAsync(output, TestContext.Current.CancellationToken));
        Assert.Single(Directory.GetFiles(folder));

        File.Delete(source.Path);
        var (_, stream) = await extractor.GetAttachment(new Movie(), source.Id, source.MediaAttachments[0].Index, TestContext.Current.CancellationToken);
        await using (stream)
        {
            using var reader = new StreamReader(stream);
            Assert.Equal("attachment payload", await reader.ReadToEndAsync(TestContext.Current.CancellationToken));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealSubtitle_EmbeddedExtractionOrExternalConversion_ProducesFreshReusableCache(bool external)
    {
        using var context = new ExtractionTestContext();
        var source = await Fixture(context, attachmentOnly: false);
        var subtitle = source.MediaStreams[1];
        if (external)
        {
            subtitle.IsExternal = true;
            subtitle.Path = Path.Combine(context.Root, "source.srt");
        }

        using var encoder = Subtitles(context, context.Runner);
        var info = await encoder.GetReadableFile(source, subtitle, TestContext.Current.CancellationToken);
        var text = await File.ReadAllTextAsync(info.Path, TestContext.Current.CancellationToken);
        Assert.Contains("extraction-proof", text, StringComparison.Ordinal);
        Assert.True(File.Exists(info.Path + ".meta"));
        var timestamp = File.GetLastWriteTimeUtc(info.Path);
        var again = await encoder.GetReadableFile(source, subtitle, TestContext.Current.CancellationToken);
        Assert.Equal(info.Path, again.Path);
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(info.Path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledAttempt_CleansUpBeforeWaitingSuccessorGetsCacheLock(bool attachments)
    {
        using var context = new ExtractionTestContext();
        var source = Source(context, attachmentOnly: false);
        var path = attachments
            ? context.Paths.Object.GetAttachmentPath(source.Id, "2-font.bin")!
            : context.Paths.Object.GetSubtitlePath(source.Id, 1, ".srt")!;
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var runner = new Mock<IFfmpegProcessRunner>();
        runner.Setup(r => r.RunFileAsync(It.IsAny<FfmpegCommand>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Returns(async (FfmpegCommand command, TimeSpan _, CancellationToken token) =>
            {
                var staged = Assert.Single(ExtractionTestContext.CommandOutputs(command));
                Assert.NotEqual(path, staged);
                var call = Interlocked.Increment(ref calls);
                if (call == 1)
                {
                    await File.WriteAllTextAsync(staged, "partial", token);
                    Assert.False(File.Exists(path));
                    reached.SetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }

                Assert.False(File.Exists(path), "the failed attempt's output must be gone before its successor runs");
                await File.WriteAllTextAsync(staged, "complete", token);
                return new FfmpegProcessResult(0, string.Empty, string.Empty, false);
            });
        using var extractor = Attachments(context, source, runner.Object);
        using var subtitles = Subtitles(context, runner.Object);
        Task Run(CancellationToken token) => attachments
            ? extractor.ExtractAllAttachments(source.Path, source, token)
            : subtitles.ExtractAllExtractableSubtitles(source, token);

        using var cancellation = new CancellationTokenSource();
        var first = Run(cancellation.Token);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var successor = Run(TestContext.Current.CancellationToken);
        Assert.Equal(1, Volatile.Read(ref calls));
        Assert.False(successor.IsCompleted);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await successor.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(2, calls);
        Assert.Equal("complete", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SubtitleFailure_PropagatesWithoutPublishingCacheMetadata_AndCanRetry()
    {
        using var context = new ExtractionTestContext();
        var source = Source(context, attachmentOnly: false);
        var output = context.Paths.Object.GetSubtitlePath(source.Id, 1, ".srt")!;
        await File.WriteAllTextAsync(source.Path, "input", TestContext.Current.CancellationToken);
        var attempts = 0;
        var runner = new Mock<IFfmpegProcessRunner>();
        runner.Setup(r => r.RunFileAsync(It.IsAny<FfmpegCommand>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Returns(async (FfmpegCommand command, TimeSpan _, CancellationToken token) =>
            {
                var staged = Assert.Single(ExtractionTestContext.CommandOutputs(command));
                Assert.NotEqual(output, staged);
                await File.WriteAllTextAsync(staged, "content", token);
                return new FfmpegProcessResult(++attempts == 1 ? 7 : 0, string.Empty, "failure", false);
            });
        using var encoder = Subtitles(context, runner.Object);
        await Assert.ThrowsAsync<Tesserafin.Common.FfmpegException>(() => encoder.GetReadableFile(source, source.MediaStreams[1], TestContext.Current.CancellationToken));
        Assert.False(File.Exists(output));
        Assert.False(File.Exists(output + ".meta"));

        var info = await encoder.GetReadableFile(source, source.MediaStreams[1], TestContext.Current.CancellationToken);
        Assert.Equal(output, info.Path);
        Assert.True(File.Exists(output + ".meta"));
        Assert.Equal(2, attempts);
    }

    private static AttachmentExtractor Attachments(ExtractionTestContext context, MediaSourceInfo source, IFfmpegProcessRunner runner)
    {
        var manager = new Mock<IMediaSourceManager>();
        manager.Setup(m => m.GetPlaybackMediaSources(It.IsAny<BaseItem>(), null, true, false, It.IsAny<CancellationToken>())).ReturnsAsync(new List<MediaSourceInfo> { source });
        return new AttachmentExtractor(NullLogger<AttachmentExtractor>.Instance, context.FileSystem.Object, context.Encoder.Object, manager.Object, context.Paths.Object, runner);
    }

    [Fact]
    public async Task SourceChangedDuringExtraction_PreservesPreviousCacheAndStamp_UntilSuccessfulRetry()
    {
        using var context = new ExtractionTestContext();
        var source = Source(context, attachmentOnly: false);
        await File.WriteAllTextAsync(source.Path, "input", TestContext.Current.CancellationToken);
        var output = context.Paths.Object.GetSubtitlePath(source.Id, 1, ".srt")!;
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await File.WriteAllTextAsync(output, "old cached subtitle", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(output + ".meta", "old stamp", TestContext.Current.CancellationToken);
        var calls = 0;
        var runner = new Mock<IFfmpegProcessRunner>();
        runner.Setup(r => r.RunFileAsync(It.IsAny<FfmpegCommand>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Returns(async (FfmpegCommand command, TimeSpan _, CancellationToken token) =>
            {
                var staged = Assert.Single(ExtractionTestContext.CommandOutputs(command));
                await File.WriteAllTextAsync(staged, "new cached subtitle", token);
                if (++calls == 1)
                {
                    await File.WriteAllTextAsync(source.Path, "different source revision", token);
                }

                return new FfmpegProcessResult(0, string.Empty, string.Empty, false);
            });
        using var encoder = Subtitles(context, runner.Object);
        await Assert.ThrowsAsync<IOException>(() => encoder.GetReadableFile(source, source.MediaStreams[1], TestContext.Current.CancellationToken));
        Assert.Equal("old cached subtitle", await File.ReadAllTextAsync(output, TestContext.Current.CancellationToken));
        Assert.Equal("old stamp", await File.ReadAllTextAsync(output + ".meta", TestContext.Current.CancellationToken));
        await encoder.GetReadableFile(source, source.MediaStreams[1], TestContext.Current.CancellationToken);
        Assert.Equal("new cached subtitle", await File.ReadAllTextAsync(output, TestContext.Current.CancellationToken));
        Assert.NotEqual("old stamp", await File.ReadAllTextAsync(output + ".meta", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RealAssSubtitle_IsPostprocessedBeforeCachePublication()
    {
        using var context = new ExtractionTestContext();
        var source = await Fixture(context, attachmentOnly: false, ass: true);
        source.MediaStreams[1].Codec = "ass";
        using var encoder = Subtitles(context, context.Runner);
        var info = await encoder.GetReadableFile(source, source.MediaStreams[1], TestContext.Current.CancellationToken);
        var text = await File.ReadAllTextAsync(info.Path, TestContext.Current.CancellationToken);
        Assert.Contains(",Arial Unicode MS,", text, StringComparison.Ordinal);
        Assert.DoesNotContain(",Arial,", text, StringComparison.Ordinal);
        Assert.Contains("extraction-proof", text, StringComparison.Ordinal);
        Assert.True(File.Exists(info.Path + ".meta"));
    }

    private static SubtitleEncoder Subtitles(ExtractionTestContext context, IFfmpegProcessRunner runner)
    {
        var configuration = new Mock<IServerConfigurationManager>();
        configuration.Setup(c => c.GetConfiguration("encoding")).Returns(new EncodingOptions { SubtitleExtractionTimeoutMinutes = 1 });
        return new SubtitleEncoder(NullLogger<SubtitleEncoder>.Instance, context.FileSystem.Object, context.Encoder.Object, Mock.Of<IHttpClientFactory>(), Mock.Of<IMediaSourceManager>(), Mock.Of<ISubtitleParser>(), context.Paths.Object, configuration.Object, runner);
    }

    private static MediaSourceInfo Source(ExtractionTestContext context, bool attachmentOnly)
    {
        var subtitles = new MediaStream { Index = attachmentOnly ? 0 : 1, Type = MediaStreamType.Subtitle, Codec = "subrip", SupportsExternalStream = true };
        return new MediaSourceInfo
        {
            Id = Guid.NewGuid().ToString("N"),
            Path = Path.Combine(context.Root, attachmentOnly ? "source.mks" : "source.mkv"),
            Protocol = MediaProtocol.File,
            MediaAttachments = [new MediaAttachment { Index = attachmentOnly ? 1 : 2, FileName = "..\\../font.bin", Codec = "bin" }],
            MediaStreams = attachmentOnly ? [subtitles] : [new MediaStream { Index = 0, Type = MediaStreamType.Video, Codec = "mpeg4" }, subtitles]
        };
    }

    private static async Task<MediaSourceInfo> Fixture(ExtractionTestContext context, bool attachmentOnly, bool ass = false)
    {
        var source = Source(context, attachmentOnly);
        await File.WriteAllTextAsync(Path.Combine(context.Root, "source.srt"), "1\n00:00:00,000 --> 00:00:00,500\nextraction-proof\n", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(context.Root, "font.bin"), "attachment payload", TestContext.Current.CancellationToken);
        var input = attachmentOnly ? "-i source.srt -map 0:s -c:s copy" : "-f lavfi -i color=size=32x32:duration=1 -i source.srt -map 0:v -map 1:s -c:v mpeg4 -c:s " + (ass ? "ass" : "srt");
        var command = FfmpegCommand.FromArgumentLine("ffmpeg", $"-nostdin -y {input} -attach font.bin -metadata:s:t mimetype=application/octet-stream -metadata:s:t filename=../font.bin -f matroska \"{source.Path}\"", workingDirectory: context.Root);
        var result = await context.Runner.RunProbeAsync(command, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.True(result.ExitCode == 0, result.StandardError);
        File.Delete(Path.Combine(context.Root, "font.bin"));
        return source;
    }
}
