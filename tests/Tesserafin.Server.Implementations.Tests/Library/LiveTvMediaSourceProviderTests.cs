using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Controller;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.LiveTv;
using Tesserafin.LiveTv;
using Tesserafin.Model.Dto;
using Tesserafin.Model.LiveTv;
using Xunit;

namespace Tesserafin.Server.Implementations.Tests.Library;

public class LiveTvMediaSourceProviderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetMediaSources_VideoUsesLiveStreamOnlyWhileRecording(bool fromChannel)
    {
        var item = new Video
        {
            Id = Guid.NewGuid(),
            ChannelId = fromChannel ? Guid.NewGuid() : Guid.Empty,
            Path = "/recordings/video.ts"
        };
        var token = TestContext.Current.CancellationToken;
        var recordings = new ActiveRecordings();
        using var recordingCancellation = new CancellationTokenSource();
        var recording = new ActiveRecordingInfo
        {
            Path = item.Path,
            Timer = new TimerInfo { Status = RecordingStatus.InProgress },
            CancellationTokenSource = recordingCancellation
        };
        var source = new MediaSourceInfo { Id = "recording-stream" };
        var mediaSources = new Mock<IMediaSourceManager>(MockBehavior.Strict);
        mediaSources.Setup(x => x.GetRecordingStreamMediaSources(recording, token)).ReturnsAsync([source]);
        var provider = new LiveTvMediaSourceProvider(
            NullLogger<LiveTvMediaSourceProvider>.Instance,
            Mock.Of<IServerApplicationHost>(MockBehavior.Strict),
            recordings,
            mediaSources.Object,
            Mock.Of<IItemLookupService>(MockBehavior.Strict),
            []);

        Assert.Empty(await provider.GetMediaSources(item, token));

        recordings["timer"] = recording;
        Assert.Same(source, Assert.Single(await provider.GetMediaSources(item, token)));

        await recordingCancellation.CancelAsync();
        Assert.Empty(await provider.GetMediaSources(item, token));
    }
}
