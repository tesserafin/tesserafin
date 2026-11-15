using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.MediaEncoding.Encoder;
using Tesserafin.MediaEncoding.Tests.Extraction;
using Tesserafin.Model.Dlna;
using Tesserafin.Model.Dto;
using Tesserafin.Model.Entities;
using Tesserafin.Model.MediaInfo;
using Xunit;

namespace Tesserafin.MediaEncoding.Tests.Probing;

public sealed class MediaEncoderProbeTests
{
    private static readonly TimeSpan _deadline = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task PublicProbe_FileAndLivePipe_ReturnSameUsefulStreams_AndCloseEachReader()
    {
        using var context = new ExtractionTestContext();
        var path = Path.Combine(context.Root, "tuner.ts");
        var generated = await context.Runner.RunProbeAsync(FfmpegCommand.FromArgumentLine("ffmpeg", $"-nostdin -v error -y -f lavfi -i color=size=64x64:rate=15:duration=4 -f lavfi -i sine=frequency=440:duration=4 -c:v libx264 -preset ultrafast -c:a aac -f mpegts \"{path}\""), _deadline, TestContext.Current.CancellationToken);
        Assert.True(generated.ExitCode == 0, generated.StandardError);
        using var encoder = MediaEncoderImageTests.Encoder(context, context.Runner);
        var file = await encoder.GetMediaInfo(Request(path, MediaProtocol.File), TestContext.Current.CancellationToken).WaitAsync(_deadline, TestContext.Current.CancellationToken);
        var provider = new Provider(path);
        var request = Request("http://127.0.0.1:1/LiveTv/LiveStreamFiles/must-not-fetch/stream.ts", MediaProtocol.Http, provider);
        request.MediaSource.RequiredHttpHeaders = new Dictionary<string, string> { ["User-Agent"] = "must-not-reach-pipe" };
        var watch = Stopwatch.StartNew();
        var live = await encoder.GetMediaInfo(request, TestContext.Current.CancellationToken).WaitAsync(_deadline, TestContext.Current.CancellationToken);
        var again = await encoder.GetMediaInfo(request, TestContext.Current.CancellationToken).WaitAsync(_deadline, TestContext.Current.CancellationToken);

        Assert.Equal(Signatures(file), Signatures(live));
        Assert.Equal(Signatures(live), Signatures(again));
        Assert.Contains(live.MediaStreams, stream => stream.Type == MediaStreamType.Video && stream.Codec == "h264" && stream.Width == 64);
        Assert.Contains(live.MediaStreams, stream => stream.Type == MediaStreamType.Audio && stream.Codec == "aac");
        Assert.Equal(request.MediaSource.Path, live.Path);
        Assert.Equal(MediaProtocol.Http, live.Protocol);
        Assert.Equal(2, provider.Readers.Count);
        Assert.NotSame(provider.Readers[0], provider.Readers[1]);
        Assert.All(provider.Readers, reader => Assert.True(reader.Closed));
        Assert.True(watch.Elapsed < _deadline, "A progressive reader must stop when ffprobe exits, without waiting its 30-second EOF poll");
    }

    [Fact]
    public async Task PublicProbe_CapturesChaptersAndFileFrameOptions_WithoutOpeningLiveSourceEarly()
    {
        using var context = new ExtractionTestContext();
        var runner = new Mock<IFfmpegProcessRunner>(MockBehavior.Strict);
        FfmpegCommand? captured = null;
        Func<Stream>? input = null;
        runner.Setup(r => r.RunMediaProbeAsync(It.IsAny<FfmpegCommand>(), It.IsAny<TimeSpan>(), It.IsAny<Func<Stream>?>(), It.IsAny<CancellationToken>()))
            .Callback((FfmpegCommand command, TimeSpan _, Func<Stream>? reader, CancellationToken _) =>
            {
                captured = command;
                input = reader;
            })
            .ReturnsAsync(new FfmpegProcessResult(0, "{\"format\":{\"format_name\":\"matroska\"}}", string.Empty, false));
        using var encoder = MediaEncoderImageTests.Encoder(context, runner.Object);
        typeof(MediaEncoder).GetField("_proberSupportsFirstVideoFrame", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(encoder, true);
        var request = Request("movie.mkv", MediaProtocol.File);
        request.ExtractChapters = true;
        await encoder.GetMediaInfo(request, TestContext.Current.CancellationToken);
        Assert.NotNull(captured);
        Assert.Equal("ffprobe", captured.Executable);
        Assert.Contains("-show_chapters", captured.ArgumentLine, StringComparison.Ordinal);
        Assert.Contains("-show_frames -only_first_vframe", captured.ArgumentLine, StringComparison.Ordinal);
        Assert.Null(input);

        var provider = new Mock<IDirectStreamProvider>(MockBehavior.Strict);
        await encoder.GetMediaInfo(Request("http://must-not-fetch", MediaProtocol.Http, provider.Object), TestContext.Current.CancellationToken);
        Assert.NotNull(captured);
        Assert.Contains("-i pipe:0", captured.ArgumentLine, StringComparison.Ordinal);
        Assert.DoesNotContain("must-not-fetch", captured.ArgumentLine, StringComparison.Ordinal);
        Assert.DoesNotContain("-show_frames", captured.ArgumentLine, StringComparison.Ordinal);
        Assert.NotNull(input);
        Assert.Empty(provider.Invocations);
    }

    [Fact]
    public async Task EncoderShutdown_CancelsImagesAndProbesBeforeWaitingForEitherCleanup()
    {
        using var context = new ExtractionTestContext();
        var probeEntered = Signal();
        var imageEntered = Signal();
        var probeCancelled = Signal();
        var imageCancelled = Signal();
        var releaseCleanup = Signal();
        var runner = new Mock<IFfmpegProcessRunner>(MockBehavior.Strict);
        async Task<FfmpegProcessResult> Block(TaskCompletionSource entered, TaskCompletionSource cancelled, CancellationToken token)
        {
            using var registration = token.Register(() => cancelled.TrySetResult());
            entered.TrySetResult();
            await cancelled.Task;
            await releaseCleanup.Task;
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException("runner was not cancelled");
        }

        runner.Setup(r => r.RunMediaProbeAsync(It.IsAny<FfmpegCommand>(), It.IsAny<TimeSpan>(), null, It.IsAny<CancellationToken>()))
            .Returns((FfmpegCommand _, TimeSpan _, Func<Stream>? _, CancellationToken token) => Block(probeEntered, probeCancelled, token));
        runner.Setup(r => r.RunImageAsync(It.IsAny<FfmpegCommand>(), It.IsAny<TimeSpan>(), It.IsAny<Func<bool>?>(), It.IsAny<ProcessPriorityClass>(), It.IsAny<CancellationToken>()))
            .Returns((FfmpegCommand _, TimeSpan _, Func<bool>? _, ProcessPriorityClass _, CancellationToken token) => Block(imageEntered, imageCancelled, token));
        using var encoder = MediaEncoderImageTests.Encoder(context, runner.Object);
        Task? stopping = null;
        try
        {
            var request = Request("movie.mkv", MediaProtocol.File);
            var probe = encoder.GetMediaInfo(request, TestContext.Current.CancellationToken);
            var video = new MediaStream { Codec = "mpeg4", Type = MediaStreamType.Video, Width = 64, Height = 64 };
            var image = encoder.ExtractVideoImage(request.MediaSource.Path, "matroska", request.MediaSource, video, null, TimeSpan.Zero, TestContext.Current.CancellationToken);
            await Task.WhenAll(probeEntered.Task, imageEntered.Task).WaitAsync(_deadline, TestContext.Current.CancellationToken);
            stopping = Task.Run(encoder.Dispose, TestContext.Current.CancellationToken);
            await Task.WhenAll(probeCancelled.Task, imageCancelled.Task).WaitAsync(_deadline, TestContext.Current.CancellationToken);
            Assert.False(stopping.IsCompleted);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => encoder.GetMediaInfo(request, TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => encoder.ExtractVideoImage(request.MediaSource.Path, "matroska", request.MediaSource, video, null, TimeSpan.Zero, TestContext.Current.CancellationToken));
            releaseCleanup.SetResult();
            await stopping.WaitAsync(_deadline, TestContext.Current.CancellationToken);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => image);
        }
        finally
        {
            releaseCleanup.TrySetResult();
            if (stopping is not null)
            {
                await stopping;
            }
        }
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static string[] Signatures(MediaInfo info) => info.MediaStreams.Select(stream => $"{stream.Index}:{stream.Type}:{stream.Codec}").ToArray();

    private static MediaInfoRequest Request(string path, MediaProtocol protocol, IDirectStreamProvider? provider = null) => new()
    {
        MediaSource = new MediaSourceInfo { Path = path, Protocol = protocol, AnalyzeDurationMs = 500, VideoType = VideoType.VideoFile },
        MediaType = DlnaProfileType.Video,
        DirectStreamReader = provider
    };

    private sealed class Provider(string path) : IDirectStreamProvider
    {
        public List<Reader> Readers { get; } = [];

        public Stream GetStream()
        {
            var reader = new Reader(path);
            Readers.Add(reader);
            return reader;
        }
    }

    private sealed class Reader(string path) : FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
    {
        public bool Closed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Closed = true;
            base.Dispose(disposing);
        }
    }
}
