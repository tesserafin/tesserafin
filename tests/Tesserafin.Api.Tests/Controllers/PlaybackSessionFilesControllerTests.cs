using System;
using System.Globalization;
using System.IO;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Tesserafin.Api.Constants;
using Tesserafin.Api.Controllers;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.Movies;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Database.Implementations.Entities;
using Tesserafin.MediaEncoding.Playback;
using Tesserafin.Model.Dlna;
using Tesserafin.Model.Dto;
using Tesserafin.Model.Entities;
using Tesserafin.Model.MediaInfo;
using Tesserafin.Model.Session;
using Tesserafin.Playback.Decision;
using Tesserafin.Playback.Engine;
using Tesserafin.Playback.Execution;
using Xunit;

namespace Tesserafin.Api.Tests.Controllers;

/// <summary>
/// Consolidation step 1: the session file route re-checks, on every request, that the owner still
/// sees the media it serves - including an alternate version, which is its own item with its own
/// access rules (#404 review P1).
/// </summary>
public sealed class PlaybackSessionFilesControllerTests : IDisposable
{
    private const string PlaySession = "ps-version";
    private readonly Guid _ownerId = Guid.NewGuid();
    private readonly Guid _filmId = Guid.NewGuid();
    private readonly Guid _versionId = Guid.NewGuid();
    private readonly string _versionPath = Path.GetTempFileName();
    private readonly PlaybackSessionId _sessionId = new(Guid.NewGuid());
    private readonly Mock<IItemAccessService> _access = new();
    private readonly User _owner;

    public PlaybackSessionFilesControllerTests()
    {
        File.WriteAllBytes(_versionPath, new byte[64]);
        _owner = new User("owner", "Default", "Default") { Id = _ownerId };
        _access.Setup(a => a.GetVisibleItemById<BaseItem>(_filmId, It.IsAny<User>())).Returns(new Movie { Id = _filmId });
    }

    private string VersionSourceId => _versionId.ToString("N", CultureInfo.InvariantCulture);

    public void Dispose() => File.Delete(_versionPath);

    [Fact]
    public void A_visible_alternate_version_is_served()
    {
        _access.Setup(a => a.GetVisibleItemById<BaseItem>(_versionId, It.IsAny<User>())).Returns(new Movie { Id = _versionId });

        var result = Controller().GetPlaybackSessionFile(_sessionId, _filmId, VersionSourceId, PlaySession);

        Assert.IsType<PhysicalFileResult>(result);
    }

    [Fact]
    public void An_alternate_version_no_longer_visible_is_refused_although_the_film_still_is()
    {
        _access.Setup(a => a.GetVisibleItemById<BaseItem>(_versionId, It.IsAny<User>())).Returns((BaseItem?)null);

        var result = Controller().GetPlaybackSessionFile(_sessionId, _filmId, VersionSourceId, PlaySession);

        Assert.IsType<NotFoundResult>(result);
    }

    private PlaybackSessionFilesController Controller()
    {
        var source = new MediaSourceInfo
        {
            Id = VersionSourceId,
            Path = _versionPath,
            Protocol = MediaProtocol.File,
            VideoType = VideoType.VideoFile,
            Container = "mp4",
        };
        var options = new MediaOptions { ItemId = _filmId, UserId = _ownerId, MediaSources = [source], Profile = new DeviceProfile() };
        var session = new PlaybackSession(
            _sessionId,
            PlaybackMediaKind.Video,
            PlaySession,
            new PlaybackSessionRequest(PlaybackMediaKind.Video, options),
            new PlaybackPlan(PlayMethod.DirectPlay, 0),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

        var manager = new Mock<IPlaybackSessionManager>();
        manager.Setup(m => m.Get(_sessionId)).Returns(session);

        var plan = new PlaybackExecutionPlan(
            PlaybackMethod.DirectPlay,
            VersionSourceId,
            "mp4",
            StreamingProtocol.Http,
            0,
            "h264",
            null,
            null,
            null,
            1,
            "aac",
            null,
            2,
            null,
            null,
            null,
            null,
            []);
        var store = new InMemoryV2PlanStore();
        var decision = PlaybackDecision.NotViable(
            PlaybackMethod.DirectPlay,
            new ReasonNode(ReasonCode.NoViablePlan, ReasonOutcome.Rejected, ReasonSubject.Method(), null, []),
            engineVersion: PlaybackEngine.EngineVersion);
        store.Attach(_sessionId, new V2PlanRecord(decision, plan, DateTimeOffset.UtcNow));

        var users = new Mock<IUserManager>();
        users.Setup(u => u.GetUserById(_ownerId)).Returns(_owner);

        var identity = new ClaimsIdentity("test");
        identity.AddClaim(new Claim(InternalClaimTypes.UserId, _ownerId.ToString("N", CultureInfo.InvariantCulture)));
        identity.AddClaim(new Claim(ClaimTypes.Role, UserRoles.User));

        return new PlaybackSessionFilesController(manager.Object, store, users.Object, _access.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) } },
        };
    }
}
