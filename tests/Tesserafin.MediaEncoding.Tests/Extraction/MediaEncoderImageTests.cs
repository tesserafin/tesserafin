using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Common.Configuration;
using Tesserafin.Controller;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.IO;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.MediaEncoding.Encoder;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.Dto;
using Tesserafin.Model.Entities;
using Tesserafin.Model.Globalization;
using Tesserafin.Model.MediaInfo;
using Xunit;

namespace Tesserafin.MediaEncoding.Tests.Extraction;

public sealed class MediaEncoderImageTests
{
    [Fact]
    public async Task VideoImage_UsesRealFfmpeg_AndReturnsNonemptyJpeg()
    {
        using var context = FfmpegImageSchedulerTests.Context();
        var (source, video) = await Fixture(context);
        using var encoder = Encoder(context, context.Runner);

        var output = await encoder.ExtractVideoImage(source.Path, "matroska", source, video, null, TimeSpan.Zero, TestContext.Current.CancellationToken);
        var bytes = await File.ReadAllBytesAsync(output, TestContext.Current.CancellationToken);
        Assert.True(bytes.Length > 2);
        Assert.Equal(0xff, bytes[0]);
        Assert.Equal(0xd8, bytes[1]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IntervalImages_UseRealSoftwareFfmpeg_AndReturnCompletedSequence(bool keyframes)
    {
        using var context = FfmpegImageSchedulerTests.Context();
        var (source, video) = await Fixture(context);
        using var encoder = Encoder(context, context.Runner);
        var helper = new EncodingHelper(Mock.Of<IApplicationPaths>(), encoder, Mock.Of<ISubtitleEncoder>(), new ConfigurationBuilder().Build(), Mock.Of<Tesserafin.Common.Configuration.IConfigurationManager>(), context.Paths.Object);
        var directory = await encoder.ExtractVideoImagesOnIntervalAccelerated(source.Path, "matroska", source, video, 32, TimeSpan.FromSeconds(1), false, false, 1, 4, null, keyframes, helper, TestContext.Current.CancellationToken);

        var images = Directory.GetFiles(directory, "*.jpg");
        Assert.True(images.Length >= 2);
        foreach (var image in images)
        {
            var bytes = await File.ReadAllBytesAsync(image, TestContext.Current.CancellationToken);
            Assert.True(bytes.Length > 2);
            Assert.Equal(0xff, bytes[0]);
            Assert.Equal(0xd8, bytes[1]);
        }
    }

    [Fact]
    public async Task Cancellation_DoesNotRetryIFrameAttempt()
    {
        using var context = FfmpegImageSchedulerTests.Context();
        using var cancellation = new CancellationTokenSource();
        var runner = new Mock<IFfmpegProcessRunner>();
        runner.Setup(r => r.RunImageAsync(It.IsAny<FfmpegCommand>(), It.IsAny<TimeSpan>(), It.IsAny<Func<bool>?>(), It.IsAny<System.Diagnostics.ProcessPriorityClass>(), It.IsAny<CancellationToken>()))
            .Returns(async (FfmpegCommand _, TimeSpan _, Func<bool>? _, System.Diagnostics.ProcessPriorityClass _, CancellationToken token) =>
            {
                await cancellation.CancelAsync();
                token.ThrowIfCancellationRequested();
                throw new InvalidOperationException("cancellation did not reach the runner");
            });
        var logger = new Mock<ILogger<MediaEncoder>>();
        using var encoder = Encoder(context, runner.Object, logger.Object);
        var video = new MediaStream { Codec = "mpeg4", Type = MediaStreamType.Video, Width = 64, Height = 64 };
        var source = new MediaSourceInfo { Path = "movie.mkv", Protocol = MediaProtocol.File };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => encoder.ExtractVideoImage(source.Path, "matroska", source, video, null, TimeSpan.Zero, cancellation.Token));
        Assert.Single(runner.Invocations);
        Assert.DoesNotContain(logger.Invocations, invocation => invocation.Method.Name == "Log" && invocation.Arguments[0] is LogLevel.Warning);
        Assert.Empty(Directory.GetFiles(context.Root));
    }

    [Fact]
    public async Task DisposedEncoder_RejectsImageWorkWithoutRetry()
    {
        using var context = FfmpegImageSchedulerTests.Context();
        var runner = new Mock<IFfmpegProcessRunner>();
        var logger = new Mock<ILogger<MediaEncoder>>();
        var encoder = Encoder(context, runner.Object, logger.Object);
        encoder.Dispose();
        var video = new MediaStream { Codec = "mpeg4", Type = MediaStreamType.Video, Width = 64, Height = 64 };
        var source = new MediaSourceInfo { Path = "movie.mkv", Protocol = MediaProtocol.File };
        await Assert.ThrowsAsync<ObjectDisposedException>(() => encoder.ExtractVideoImage(source.Path, "matroska", source, video, null, TimeSpan.Zero, TestContext.Current.CancellationToken));
        Assert.Empty(runner.Invocations);
        Assert.DoesNotContain(logger.Invocations, invocation => invocation.Method.Name == "Log" && invocation.Arguments[0] is LogLevel.Warning);
    }

    internal static MediaEncoder Encoder(ExtractionTestContext context, IFfmpegProcessRunner runner, ILogger<MediaEncoder>? logger = null)
    {
        var configuration = new Mock<IServerConfigurationManager>();
        configuration.Setup(c => c.Configuration).Returns(new ServerConfiguration { ParallelImageEncodingLimit = 1, ImageExtractionTimeoutMs = 5_000 });
        configuration.Setup(c => c.GetConfiguration("encoding")).Returns(new EncodingOptions());
        configuration.Setup(c => c.ApplicationPaths).Returns(Mock.Of<IServerApplicationPaths>(p => p.TempDirectory == context.Root));
        var encoder = new MediaEncoder(logger ?? NullLogger<MediaEncoder>.Instance, configuration.Object, context.FileSystem.Object, Mock.Of<IBlurayExaminer>(), Mock.Of<ILocalizationManager>(), new ConfigurationBuilder().Build(), configuration.Object, runner);
        // Startup probing is outside these tests: supply the already-selected binary/version.
        typeof(MediaEncoder).GetField("_ffmpegPath", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(encoder, "ffmpeg");
        typeof(MediaEncoder).GetField("_ffmpegVersion", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(encoder, new Version(7, 1));
        typeof(MediaEncoder).GetField("_ffprobePath", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(encoder, "ffprobe");
        return encoder;
    }

    private static async Task<(MediaSourceInfo Source, MediaStream Video)> Fixture(ExtractionTestContext context)
    {
        var path = Path.Combine(context.Root, "movie.mkv");
        var result = await context.Runner.RunProbeAsync(FfmpegCommand.FromArgumentLine("ffmpeg", $"-nostdin -y -f lavfi -i color=size=64x64:rate=2:duration=2 -c:v mpeg4 -g 1 \"{path}\""), TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.True(result.ExitCode == 0, result.StandardError);
        var video = new MediaStream { Index = 0, Codec = "mpeg4", Type = MediaStreamType.Video, Width = 64, Height = 64, RealFrameRate = 2, AverageFrameRate = 2, PixelFormat = "yuv420p", BitDepth = 8 };
        return (new MediaSourceInfo { Path = path, Protocol = MediaProtocol.File, Container = "mkv", MediaStreams = [video] }, video);
    }
}
