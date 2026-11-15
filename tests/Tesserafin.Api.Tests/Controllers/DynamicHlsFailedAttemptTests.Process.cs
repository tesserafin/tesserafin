using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Controller.Streaming;
using Tesserafin.Model.Dto;
using Tesserafin.Model.MediaInfo;
using Xunit;

namespace Tesserafin.Api.Tests.Controllers;

public sealed partial class DynamicHlsFailedAttemptTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RegisteredButUnstartedProcess_CancellationOrLaunchFailureReleasesOwnershipAndLog(bool failToLaunch)
    {
        Configure(hardware: false);
        var output = Path.Combine(_transcodePath, "process-start.m3u8");
        using var state = new StreamState(Mock.Of<IMediaSourceManager>(), TranscodingJobType.Hls, _manager!)
        {
            Request = new StreamingRequestDto { Id = _item, PlaySessionId = SessionA, DeviceId = OwnerDevice, MediaSourceId = MediaSourceId },
            MediaSource = new MediaSourceInfo { Id = MediaSourceId, Path = "film.mkv", Protocol = MediaProtocol.File },
            OwnerDeviceId = OwnerDevice
        };
        using var cancellation = new CancellationTokenSource();
        var gate = new Gate("registered before process launch");
        _beforeStartGate = gate;
        var executable = Path.Combine(_root, "ffmpeg");
        // The logger gates on the executable prefix; a nonexistent suffix preserves that barrier.
        var command = FfmpegCommand.FromArgumentLine(failToLaunch ? executable + ".missing" : executable, "-i film.mkv");
        var start = Task.Run(() => _manager!.StartFfMpeg(state, output, command, _owner, TranscodingJobType.Hls, cancellation), TestContext.Current.CancellationToken);
        await gate.Reached;
        var job = _manager!.GetTranscodingJob(output, TranscodingJobType.Hls);
        Assert.NotNull(job);
        Assert.Equal(_owner, job.UserId);
        Assert.Equal(OwnerDevice, job.OwnerDeviceId);
        Assert.Throws<InvalidOperationException>(() => job.Process!.Id);
        if (!failToLaunch)
        {
            await cancellation.CancelAsync();
        }

        gate.Open();
        var error = await Record.ExceptionAsync(() => start.WaitAsync(Patience, TestContext.Current.CancellationToken));
        if (failToLaunch)
        {
            Assert.IsType<System.ComponentModel.Win32Exception>(error);
        }
        else
        {
            Assert.IsAssignableFrom<OperationCanceledException>(error);
        }

        Assert.Empty(_starts);
        Assert.Null(_manager.GetTranscodingJob(output, TranscodingJobType.Hls));
        Assert.Throws<InvalidOperationException>(() => job.Process!.Id);
        Assert.False(File.Exists(output));
        foreach (var log in Directory.GetFiles(_root, "FFmpeg.*.log", SearchOption.AllDirectories))
        {
            // A pending startup write must not keep the failed attempt's log open.
            using var exclusive = File.Open(log, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
    }
}
