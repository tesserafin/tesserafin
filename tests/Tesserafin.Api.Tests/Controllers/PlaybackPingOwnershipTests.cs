using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Api.Auth.HlsJobOwnership;
using Tesserafin.Api.Controllers;
using Tesserafin.Controller;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.IO;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Controller.Session;
using Tesserafin.MediaEncoding.Transcoding;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.IO;
using Xunit;

namespace Tesserafin.Api.Tests.Controllers;

/// <summary>
/// A play session id is the client's to send, so two users' transcodes can carry the same one.
/// Pinging it - by the ping route or by a progress report - keeps alive, and marks paused, only the
/// jobs of the caller, never another user's job under the same id.
/// </summary>
public sealed class PlaybackPingOwnershipTests
{
    private const string SharedPlaySessionId = "shared-play-session";
    private const string OwnerDevice = "owner-device";
    private const string StrangerDevice = "stranger-device";

    private static readonly Guid _owner = new("aaaaaaaa11114444888800000000cccc");
    private static readonly Guid _stranger = new("dddddddd2222555599990000eeeeffff");
    private static readonly DateTime _neverPinged = DateTime.UnixEpoch;

    [Fact]
    public void ThePingRoute_KeepsAliveOnlyTheCallersJob()
    {
        var (manager, _) = CreateManager();
        var ownerJob = AddJob(manager, _owner, OwnerDevice);
        var strangerJob = AddJob(manager, _stranger, StrangerDevice);

        var result = PingAs(manager, _stranger, StrangerDevice);

        Assert.IsType<NoContentResult>(result);
        Assert.NotEqual(_neverPinged, strangerJob.LastPingDate);
        Assert.Equal(_neverPinged, ownerJob.LastPingDate);
    }

    [Fact]
    public void ThePingRoute_AsksTheAuthorizerAboutEachJobsOwner()
    {
        var (manager, _) = CreateManager();
        AddJob(manager, _owner, OwnerDevice);
        AddJob(manager, _stranger, StrangerDevice);
        var asked = new List<(Guid UserId, string? DeviceId)>();
        var authorizer = new Mock<IHlsJobOwnershipAuthorizer>();
        authorizer
            .Setup(a => a.OwnsJob(It.IsAny<HttpContext>(), It.IsAny<Guid>(), It.IsAny<string?>()))
            .Callback<HttpContext, Guid, string?>((_, user, device) => asked.Add((user, device)))
            .Returns(false);

        Controller(manager, authorizer.Object).PingPlaybackSession(SharedPlaySessionId);

        Assert.Equal([(_owner, OwnerDevice), (_stranger, StrangerDevice)], asked);
    }

    [Fact]
    public void AProgressReport_KeepsAliveAndPausesOnlyTheReportersJob()
    {
        var (manager, sessionManager) = CreateManager();
        var ownerJob = AddJob(manager, _owner, OwnerDevice);
        var strangerJob = AddJob(manager, _stranger, StrangerDevice);

        sessionManager.Raise(
            m => m.PlaybackProgress += null,
            new PlaybackProgressEventArgs
            {
                PlaySessionId = SharedPlaySessionId,
                IsPaused = true,
                Session = new SessionInfo(Mock.Of<ISessionManager>(), NullLogger.Instance) { UserId = _stranger, DeviceId = StrangerDevice }
            });

        Assert.NotEqual(_neverPinged, strangerJob.LastPingDate);
        Assert.True(strangerJob.IsUserPaused);
        Assert.Equal(_neverPinged, ownerJob.LastPingDate);
        Assert.False(ownerJob.IsUserPaused);
    }

    [Fact]
    public void AProgressReport_WithoutAUser_PingsNoJob()
    {
        var (manager, sessionManager) = CreateManager();
        var ownerJob = AddJob(manager, _owner, OwnerDevice);

        sessionManager.Raise(
            m => m.PlaybackProgress += null,
            new PlaybackProgressEventArgs
            {
                PlaySessionId = SharedPlaySessionId,
                IsPaused = true,
                Session = new SessionInfo(Mock.Of<ISessionManager>(), NullLogger.Instance) { DeviceId = StrangerDevice }
            });

        Assert.Equal(_neverPinged, ownerJob.LastPingDate);
        Assert.False(ownerJob.IsUserPaused);
    }

    private static ActionResult PingAs(TranscodeManager manager, Guid userId, string deviceId)
    {
        var authorizer = new Mock<IHlsJobOwnershipAuthorizer>();
        authorizer
            .Setup(a => a.OwnsJob(It.IsAny<HttpContext>(), It.IsAny<Guid>(), It.IsAny<string?>()))
            .Returns<HttpContext, Guid, string?>((_, user, device) => user.Equals(userId) && string.Equals(device, deviceId, StringComparison.Ordinal));

        return Controller(manager, authorizer.Object).PingPlaybackSession(SharedPlaySessionId);
    }

    private static PlaystateController Controller(ITranscodeManager manager, IHlsJobOwnershipAuthorizer authorizer)
        => new(
            Mock.Of<IUserManager>(),
            Mock.Of<IUserDataManager>(),
            Mock.Of<IItemAccessService>(),
            Mock.Of<ISessionManager>(),
            NullLoggerFactory.Instance,
            manager,
            authorizer)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

    private static TranscodingJob AddJob(TranscodeManager manager, Guid ownerUserId, string ownerDeviceId)
    {
        var job = new TranscodingJob(NullLogger<TranscodingJob>.Instance)
        {
            Type = TranscodingJobType.Hls,
            Path = Path.Combine(Path.GetTempPath(), "ping-never-created-" + Guid.NewGuid().ToString("N") + ".m3u8"),
            UserId = ownerUserId,
            OwnerDeviceId = ownerDeviceId,
            PlaySessionId = SharedPlaySessionId,
            LastPingDate = _neverPinged
        };

        Jobs(manager).Add(job);
        return job;
    }

    private static List<TranscodingJob> Jobs(TranscodeManager manager)
    {
        var field = typeof(TranscodeManager).GetField("_activeTranscodingJobs", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "TranscodeManager no longer has a private _activeTranscodingJobs field. There is no public way to add "
                + "a job without launching ffmpeg; re-point this fixture rather than let it test nothing.");

        return (List<TranscodingJob>)(field.GetValue(manager)
            ?? throw new InvalidOperationException("TranscodeManager's job list was null."));
    }

    private static (TranscodeManager Manager, Mock<ISessionManager> SessionManager) CreateManager()
    {
        var appPaths = Mock.Of<IServerApplicationPaths>();
        var configurationManager = new Mock<IServerConfigurationManager>();
        configurationManager
            .Setup(x => x.GetConfiguration("encoding"))
            .Returns(new EncodingOptions
            {
                // A path that does not exist: the constructor's cache purge returns at once.
                TranscodingTempPath = Path.Combine(Path.GetTempPath(), "ping-never-created-" + Guid.NewGuid().ToString("N"))
            });
        configurationManager.Setup(x => x.CommonApplicationPaths).Returns(appPaths);
        var sessionManager = new Mock<ISessionManager>();

        var manager = new TranscodeManager(
            NullLoggerFactory.Instance,
            Mock.Of<IFileSystem>(),
            appPaths,
            configurationManager.Object,
            Mock.Of<IUserManager>(),
            sessionManager.Object,
            new EncodingHelper(
                appPaths,
                Mock.Of<IMediaEncoder>(),
                Mock.Of<ISubtitleEncoder>(),
                Mock.Of<IConfiguration>(),
                configurationManager.Object,
                Mock.Of<IPathManager>()),
            Mock.Of<IMediaEncoder>(),
            Mock.Of<IMediaSourceManager>(),
            Mock.Of<IAttachmentExtractor>());

        return (manager, sessionManager);
    }
}
