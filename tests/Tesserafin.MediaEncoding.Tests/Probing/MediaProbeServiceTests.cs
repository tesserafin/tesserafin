using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Common;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.MediaEncoding.Encoder;
using Tesserafin.Model.Globalization;
using Tesserafin.Model.MediaInfo;
using Xunit;

namespace Tesserafin.MediaEncoding.Tests.Probing;

public sealed class MediaProbeServiceTests
{
    private const string Json = """
        {"streams":[{"index":0,"codec_type":"video","codec_name":"h264","width":1920,"height":1080,"display_aspect_ratio":"0:1","sample_aspect_ratio":"0:1","disposition":{}}],"format":{"format_name":"matroska","duration":"5"}}
        """;

    private static readonly FfmpegCommand _command = FfmpegCommand.FromArgumentLine("selected-ffprobe", "captured-arguments");

    [Fact]
    public async Task SuccessfulProbe_NormalizesMetadataAndForwardsCommandAndDeadline()
    {
        var runner = Runner(new FfmpegProcessResult(0, Json, string.Empty, false));
        using var service = Service(runner.Object);
        var result = await Probe(service, TestContext.Current.CancellationToken);
        var stream = Assert.Single(result.MediaStreams);
        Assert.Equal("h264", stream.Codec);
        Assert.Equal("16:9", stream.AspectRatio);
        Assert.Equal("movie.mkv", result.Path);
        Assert.Equal(MediaProtocol.File, result.Protocol);
        runner.Verify(r => r.RunMediaProbeAsync(_command, MediaProbeService.DefaultTimeout, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(7, false)]
    [InlineData(null, true)]
    public async Task FailedProcess_RejectsEvenValidMetadata(int? exitCode, bool timedOut)
    {
        var runner = Runner(new FfmpegProcessResult(exitCode, Json, "diagnostic tail", timedOut));
        using var service = Service(runner.Object);
        await Assert.ThrowsAsync<FfmpegException>(() => Probe(service, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    public async Task EmptyResult_IsRejected(string output)
    {
        var runner = Runner(new FfmpegProcessResult(0, output, string.Empty, false));
        using var service = Service(runner.Object);
        await Assert.ThrowsAsync<FfmpegException>(() => Probe(service, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MalformedJson_IsRejected()
    {
        var runner = Runner(new FfmpegProcessResult(0, "{truncated", string.Empty, false));
        using var service = Service(runner.Object);
        await Assert.ThrowsAsync<JsonException>(() => Probe(service, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PreCancelled_DoesNotReachRunner()
    {
        var runner = new Mock<IFfmpegProcessRunner>(MockBehavior.Strict);
        using var service = Service(runner.Object);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Probe(service, cancellation.Token));
        Assert.Empty(runner.Invocations);
    }

    [Fact]
    public async Task Shutdown_ClosesAdmissionAndWaitsForRunnerCleanup_WithIdempotentStop()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleaned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new Mock<IFfmpegProcessRunner>(MockBehavior.Strict);
        runner.Setup(r => r.RunMediaProbeAsync(It.IsAny<FfmpegCommand>(), It.IsAny<TimeSpan>(), null, It.IsAny<CancellationToken>()))
            .Returns(async (FfmpegCommand _, TimeSpan _, Func<Stream>? _, CancellationToken token) =>
            {
                using var registration = token.Register(() => cancelled.TrySetResult());
                entered.TrySetResult();
                await cancelled.Task;
                await cleaned.Task;
                token.ThrowIfCancellationRequested();
                return new FfmpegProcessResult(0, Json, string.Empty, false);
            });
        using var service = Service(runner.Object);
        try
        {
            var work = Probe(service, TestContext.Current.CancellationToken);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            var stop = service.StopAsync();
            Assert.Same(stop, service.StopAsync());
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.False(stop.IsCompleted);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => Probe(service, TestContext.Current.CancellationToken));
            cleaned.SetResult();
            await stop.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work);
        }
        finally
        {
            cleaned.TrySetResult();
        }
    }

    [Fact]
    public async Task CallerCancellation_DoesNotCancelConcurrentProbe()
    {
        using var cancelled = new CancellationTokenSource();
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new Mock<IFfmpegProcessRunner>();
        var calls = 0;
        runner.Setup(r => r.RunMediaProbeAsync(It.IsAny<FfmpegCommand>(), It.IsAny<TimeSpan>(), null, It.IsAny<CancellationToken>()))
            .Returns(async (FfmpegCommand _, TimeSpan _, Func<Stream>? _, CancellationToken token) =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    firstEntered.SetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }

                secondEntered.SetResult();
                await releaseSecond.Task.WaitAsync(token);
                return new FfmpegProcessResult(0, Json, string.Empty, false);
            });
        using var service = Service(runner.Object);
        try
        {
            var first = Probe(service, cancelled.Token);
            await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            var second = Probe(service, TestContext.Current.CancellationToken);
            await secondEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            await cancelled.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
            Assert.False(second.IsCompleted);
            releaseSecond.SetResult();
            Assert.Single((await second).MediaStreams);
        }
        finally
        {
            releaseSecond.TrySetResult();
        }
    }

    private static Mock<IFfmpegProcessRunner> Runner(FfmpegProcessResult result)
    {
        var runner = new Mock<IFfmpegProcessRunner>(MockBehavior.Strict);
        runner.Setup(r => r.RunMediaProbeAsync(It.IsAny<FfmpegCommand>(), It.IsAny<TimeSpan>(), null, It.IsAny<CancellationToken>())).ReturnsAsync(result);
        return runner;
    }

    private static MediaProbeService Service(IFfmpegProcessRunner runner) => new(runner, NullLogger.Instance, Mock.Of<ILocalizationManager>());

    private static Task<MediaInfo> Probe(MediaProbeService service, CancellationToken token)
        => service.ProbeAsync(_command, "movie.mkv", MediaProtocol.File, false, null, null, token);
}
