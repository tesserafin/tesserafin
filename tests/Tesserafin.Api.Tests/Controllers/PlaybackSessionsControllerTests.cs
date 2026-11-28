using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Tesserafin.Api.Constants;
using Tesserafin.Api.Controllers;
using Tesserafin.Api.Models.PlaybackSessionDtos;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.Movies;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.LiveTv;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Controller.Net.PlaybackCredentials;
using Tesserafin.Data.Entities;
using Tesserafin.Data.Enums;
using Tesserafin.MediaEncoding.Playback;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.Dlna;
using Tesserafin.Model.Dto;
using Tesserafin.Model.Entities;
using Tesserafin.Model.MediaInfo;
using Tesserafin.Model.Session;
using Tesserafin.Playback.Decision;
using Tesserafin.Playback.Dlna;
using Tesserafin.Playback.Engine;
using Tesserafin.Playback.Execution;
using Xunit;

namespace Tesserafin.Api.Tests.Controllers;

public class PlaybackSessionsControllerTests
{
    private const string AttemptId = "attempt-7f3a";

    private readonly Mock<IPlaybackSessionManager> _playbackSessionManager = new();
    private readonly Mock<IItemAccessService> _itemLookupService = new();
    private readonly Mock<IUserManager> _userManager = new();
    private readonly Mock<IMediaSourceManager> _mediaSourceManager = new();
    private readonly Mock<IV2PlanStore> _v2PlanStore = new();
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _itemId = Guid.NewGuid();

    private PlaybackSessionsController CreateController(ILogger<PlaybackSessionsController>? logger = null)
    {
        var controller = new PlaybackSessionsController(
            _playbackSessionManager.Object,
            _itemLookupService.Object,
            _userManager.Object,
            _mediaSourceManager.Object,
            _v2PlanStore.Object,
            logger);

        SetIdentity(controller, _userId);

        return controller;
    }

    private static void SetIdentity(PlaybackSessionsController controller, Guid userId, bool isAdmin = false, string? token = "caller-token")
    {
        var claims = new List<Claim> { new(InternalClaimTypes.UserId, userId.ToString()) };
        if (isAdmin)
        {
            claims.Add(new Claim(ClaimTypes.Role, UserRoles.Administrator));
        }

        if (token is not null)
        {
            claims.Add(new Claim(InternalClaimTypes.Token, token));
        }

        var identity = new ClaimsIdentity(claims, "test");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) },
        };
    }

    private static Tesserafin.Playback.Decision.ClientCapabilities CreateCapabilities() => new(
        Decode: new DecodeCapabilities(
            DirectPlayProfiles: [new DecodeProfile(MediaKind.Video, ["mp4"], ["h264"], ["aac"])],
            VideoCodecs: [],
            AudioCodecs: [],
            SubtitleDelivery: [],
            SupportsHls: false,
            SupportsDash: false),
        OutputProfiles: []);

    private static PlaybackConstraints CreateConstraints() => new(
        AllowDirectPlay: true,
        AllowDirectStream: true,
        AllowTranscoding: true,
        AllowVideoStreamCopy: true,
        AllowAudioStreamCopy: true,
        MaxBitrate: null,
        MaxAudioChannels: null,
        PreferredAudioStreamIndex: null,
        PreferredSubtitleStreamIndex: null,
        SubtitleMode: Tesserafin.Playback.Decision.SubtitlePlaybackMode.Default,
        PreferredSubtitleLanguages: [],
        AlwaysBurnInSubtitleWhenTranscoding: false,
        StartTimeTicks: 0);

    private CreatePlaybackSessionRequest CreateRequest(string? playSessionId = null)
        => new(_itemId, _userId, CreateCapabilities(), CreateConstraints(), PlaySessionId: playSessionId);

    private ReplacePlaybackSessionRequest CreateReplaceRequest()
        => new(_itemId, _userId, CreateCapabilities(), CreateConstraints());

    private void SetUpItemAndUser(Video item)
    {
        _userManager.Setup(m => m.GetUserById(_userId)).Returns(new User("test", "auth", "reset"));
        _itemLookupService.Setup(m => m.GetVisibleItemById<BaseItem>(_itemId, It.IsAny<User>())).Returns(item);
        _mediaSourceManager
            .Setup(m => m.GetPlaybackMediaSources(item, It.IsAny<User>(), true, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new MediaSourceInfo { Id = _itemId.ToString("N") }]);
    }

    /// <summary>
    /// Every property of <see cref="PlaybackSessionResponse"/> must be a primitive/BCL value or
    /// drawn from the <see cref="Tesserafin.Playback.Decision"/> vocabulary (PR91) - never
    /// <c>Tesserafin.Model.Dlna</c> or the internal <c>Tesserafin.Controller.MediaEncoding.PlaybackSession</c>
    /// record. Asserted structurally so a future change can't silently reintroduce a leak.
    /// </summary>
    [Fact]
    public void PlaybackSessionResponse_Properties_ReferenceOnlyDecisionVocabOrPrimitives()
    {
        var allowedPrimitiveTypes = new[] { typeof(Guid), typeof(int), typeof(string), typeof(bool), typeof(DateTimeOffset) };

        foreach (var property in typeof(PlaybackSessionResponse).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var type = property.PropertyType;
            var elementType = GetEnumerableElementTypeOrSelf(type);

            var isAllowed = allowedPrimitiveTypes.Contains(elementType)
                || elementType.Namespace == "Tesserafin.Playback.Decision";

            Assert.True(isAllowed, $"{property.Name} ({type}) must be a primitive or Tesserafin.Playback.Decision type.");
        }
    }

    private static Type GetEnumerableElementTypeOrSelf(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;

        if (underlying.IsGenericType)
        {
            var definition = underlying.GetGenericTypeDefinition();
            if (definition == typeof(System.Collections.Generic.IReadOnlyList<>))
            {
                return underlying.GetGenericArguments()[0];
            }
        }

        return underlying;
    }

    /// <summary>
    /// Issue #43, end-to-end over a REAL <see cref="PlaybackSessionManager"/> (every other test in
    /// this file mocks it, which is exactly why the defect below survived): the ordinary client
    /// sequence - POST Playback/Sessions, then an HLS segment fetch, which reaches
    /// <see cref="IPlaybackSessionManager.Track"/> with the same PlaySessionId and no request of its
    /// own - must leave the session's OWNER still able to end their own session.
    ///
    /// Before the StoreOrReplace fix, Track nulled the stored request, and
    /// <c>EnsureCallerOwnsSessionOrIsAdmin</c> reads ownership off <c>session.Request?.Options
    /// .UserId</c>, forbidding on null. So DELETE answered 403 to the very user who created the
    /// session, and the client-side teardown of #43 could never complete. An administrator was
    /// unaffected, which is what made the endpoint read as "admin only".
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
    [Fact]
    public async Task DeletePlaybackSession_OwnerAfterSegmentFetchTrackedSession_StillSucceeds()
    {
        var item = new Movie { Id = _itemId };
        SetUpItemAndUser(item);
        var plan = new PlaybackPlan(PlayMethod.DirectPlay, default);
        var planner = new Mock<IPlaybackSessionPlanner>();
        planner.Setup(p => p.PlanVideo(It.IsAny<MediaOptions>())).Returns(plan);
        using var realManager = new PlaybackSessionManager(
            planner.Object,
            new Mock<ITranscodeManager>().Object,
            new Mock<Tesserafin.Controller.Session.ISessionManager>().Object);

        var controller = new PlaybackSessionsController(
            realManager,
            _itemLookupService.Object,
            _userManager.Object,
            _mediaSourceManager.Object,
            _v2PlanStore.Object);
        SetIdentity(controller, _userId);

        var created = await controller.CreatePlaybackSession(CreateRequest(playSessionId: "play-session-1"));
        var response = Assert.IsType<PlaybackSessionResponse>(
            Assert.IsType<Tesserafin.Api.Results.OkResult<PlaybackSessionResponse>>(created.Result).Value);

        // The HLS segment path (DynamicHlsController -> PlaybackSessionManager.Track): same play
        // session id, no request to contribute.
        realManager.Track(PlaybackMediaKind.Video, plan, "play-session-1");

        var deleted = await controller.DeletePlaybackSession(new PlaybackSessionId(response.Id));

        Assert.IsType<NoContentResult>(deleted);
    }

    /// <summary>
    /// Library and channel-plugin videos reach the session planner. A Live TV recording still
    /// being written goes to the existing chain.
    /// </summary>
    /// <param name="kind">Which video the session is for.</param>
    /// <param name="offered">Whether the engine must be asked.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
    [Theory]
    [InlineData("library", true)]
    [InlineData("channel", true)]
    [InlineData("recording", false)]
    public async Task CreatePlaybackSession_OffersSupportedVideosToTheLocalMediaPlanner(string kind, bool offered)
    {
        const string MediaPath = "/media/home.mkv";
        var item = new Video { Id = _itemId, Path = MediaPath, ChannelId = kind == "channel" ? Guid.NewGuid() : Guid.Empty };
        SetUpItemAndUser(item);
        _mediaSourceManager
            .Setup(m => m.GetPlaybackMediaSources(item, It.IsAny<User>(), true, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new MediaSourceInfo { Id = _itemId.ToString("N"), Protocol = MediaProtocol.File, Path = MediaPath }]);
        var recordings = new Mock<IRecordingsManager>();
        if (kind == "recording")
        {
            recordings.Setup(r => r.GetActiveRecordingInfo(MediaPath)).Returns(new ActiveRecordingInfo { Path = MediaPath });
        }

        var engine = new Mock<IPlaybackEngine>();
        engine
            .Setup(e => e.Decide(It.IsAny<PlaybackRequestContext>(), It.IsAny<Tesserafin.Playback.Decision.ClientCapabilities>(), It.IsAny<IReadOnlyList<MediaSourceSnapshot>>(), It.IsAny<PlaybackConstraints>()))
            .Throws(new InvalidOperationException("offered to the engine"));
        using var manager = new PlaybackSessionManager(
            new LocalMediaPlanner(engine.Object, _v2PlanStore.Object, Microsoft.Extensions.Logging.Abstractions.NullLogger<LocalMediaPlanner>.Instance),
            new Mock<ITranscodeManager>().Object,
            new Mock<Tesserafin.Controller.Session.ISessionManager>().Object);
        var controller = new PlaybackSessionsController(
            manager,
            _itemLookupService.Object,
            _userManager.Object,
            _mediaSourceManager.Object,
            _v2PlanStore.Object,
            recordingsManager: recordings.Object);
        SetIdentity(controller, _userId);

        await Record.ExceptionAsync(() => controller.CreatePlaybackSession(CreateRequest()));

        engine.Verify(
            e => e.Decide(It.IsAny<PlaybackRequestContext>(), It.IsAny<Tesserafin.Playback.Decision.ClientCapabilities>(), It.IsAny<IReadOnlyList<MediaSourceSnapshot>>(), It.IsAny<PlaybackConstraints>()),
            offered ? Times.Once() : Times.Never());
    }

    [Fact]
    public async Task CreatePlaybackSession_ViablePlan_ReturnsMappedResponse()
    {
        var item = new Movie { Id = _itemId };
        SetUpItemAndUser(item);
        var session = new PlaybackSession(PlaybackSessionId.NewId(), PlaybackMediaKind.Video, null, null, new PlaybackPlan(PlayMethod.DirectPlay, default), default, default);
        _playbackSessionManager
            .Setup(m => m.Create(It.IsAny<PlaybackSessionRequest>(), null))
            .Returns(session);

        var result = await CreateController().CreatePlaybackSession(CreateRequest());

        var response = Assert.IsType<PlaybackSessionResponse>(Assert.IsAssignableFrom<OkObjectResult>(result.Result).Value);
        Assert.Equal(session.Id.Value, response.Id);
        Assert.Equal(MediaKind.Video, response.Kind);
        Assert.Equal(PlaybackSessionResponse.LegacyDecisionVersion, response.DecisionVersion);
        Assert.Equal(PlaybackMethod.DirectPlay, response.Method);
    }

    /// <summary>
    /// PR113b: <c>ResolveOptions</c> must carry the real requesting user (already resolved via
    /// <c>RequestHelpers.GetUserId</c>) onto <c>MediaOptions.UserId</c>, the only vector that
    /// reaches <c>ShadowPlaybackSessionPlanner</c> - previously left at its default
    /// <see cref="Guid.Empty"/>.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
    [Fact]
    public async Task CreatePlaybackSession_ResolvesOptions_CarriesRequestingUserId()
    {
        var item = new Movie { Id = _itemId };
        SetUpItemAndUser(item);
        var session = new PlaybackSession(PlaybackSessionId.NewId(), PlaybackMediaKind.Video, null, null, new PlaybackPlan(PlayMethod.DirectPlay, default), default, default);
        PlaybackSessionRequest? captured = null;
        _playbackSessionManager
            .Setup(m => m.Create(It.IsAny<PlaybackSessionRequest>(), null, null))
            .Callback<PlaybackSessionRequest, string?, string?>((request, _, _) => captured = request)
            .Returns(session);

        await CreateController().CreatePlaybackSession(CreateRequest());

        Assert.NotNull(captured);
        Assert.Equal(_userId, captured!.Options.UserId);
    }

    [Fact]
    public async Task CreatePlaybackSession_NoViablePlan_ReturnsUnprocessableEntity()
    {
        var item = new Movie { Id = _itemId };
        SetUpItemAndUser(item);
        _playbackSessionManager
            .Setup(m => m.Create(It.IsAny<PlaybackSessionRequest>(), null))
            .Returns((PlaybackSession?)null);

        var result = await CreateController().CreatePlaybackSession(CreateRequest());

        Assert.IsType<UnprocessableEntityObjectResult>(result.Result);
    }

    [Fact]
    public async Task CreatePlaybackSession_ItemNotFound_Throws()
    {
        _userManager.Setup(m => m.GetUserById(_userId)).Returns(new User("test", "auth", "reset"));
        _itemLookupService.Setup(m => m.GetVisibleItemById<BaseItem>(_itemId, It.IsAny<User>())).Returns((BaseItem?)null);

        await Assert.ThrowsAsync<Tesserafin.Common.Extensions.ResourceNotFoundException>(
            () => CreateController().CreatePlaybackSession(CreateRequest()));
    }

    /// <summary>
    /// A minimal existing session shaped only for the owner-or-admin check
    /// (<c>EnsureCallerOwnsSessionOrIsAdmin</c>) that PUT/DELETE now perform against whatever
    /// <see cref="IPlaybackSessionManager.Get"/> returns for the target id - independent of whatever
    /// the verb's own mocked outcome (<c>Patch</c>/<c>Delete</c>) later returns.
    /// </summary>
    private PlaybackSession BuildExistingSessionForAuth(PlaybackSessionId id, Guid ownerId)
    {
        var options = new MediaOptions { ItemId = _itemId, UserId = ownerId, Profile = new DeviceProfile() };
        return new PlaybackSession(id, PlaybackMediaKind.Video, null, new PlaybackSessionRequest(PlaybackMediaKind.Video, options), new PlaybackPlan(PlayMethod.DirectPlay, default), default, default);
    }

    [Fact]
    public async Task ReplacePlaybackSession_ViablePlan_ReturnsMappedResponse()
    {
        var item = new Movie { Id = _itemId };
        SetUpItemAndUser(item);
        var session = new PlaybackSession(PlaybackSessionId.NewId(), PlaybackMediaKind.Video, null, null, new PlaybackPlan(PlayMethod.Transcode, TranscodeReason.VideoCodecNotSupported), default, default);
        _playbackSessionManager.Setup(m => m.Get(session.Id)).Returns(BuildExistingSessionForAuth(session.Id, _userId));
        _playbackSessionManager
            .Setup(m => m.Patch(It.IsAny<PlaybackSessionId>(), It.IsAny<PlaybackSessionRequest>()))
            .Returns(session);

        var result = await CreateController().ReplacePlaybackSession(session.Id, CreateReplaceRequest());

        var response = Assert.IsType<PlaybackSessionResponse>(Assert.IsAssignableFrom<OkObjectResult>(result.Result).Value);
        Assert.Equal(PlaybackMethod.Transcode, response.Method);
    }

    [Fact]
    public async Task ReplacePlaybackSession_UnknownSession_ReturnsNotFound()
    {
        var item = new Movie { Id = _itemId };
        SetUpItemAndUser(item);
        _playbackSessionManager.Setup(m => m.Get(It.IsAny<PlaybackSessionId>())).Returns((PlaybackSession?)null);

        var result = await CreateController().ReplacePlaybackSession(PlaybackSessionId.NewId(), CreateReplaceRequest());

        Assert.IsType<NotFoundResult>(result.Result);
        _playbackSessionManager.Verify(m => m.Patch(It.IsAny<PlaybackSessionId>(), It.IsAny<PlaybackSessionRequest>()), Times.Never);
    }

    /// <summary>
    /// PR118: PUT used to have no ownership check at all - any authenticated caller who knew the id
    /// could re-plan someone else's session. Same owner-or-admin semantics as
    /// <c>GetPlaybackSessionStream</c> (§4.2 of the PR117 design doc flagged this as a pre-existing,
    /// separately tracked gap).
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
    [Fact]
    public async Task ReplacePlaybackSession_OtherUser_ReturnsForbidden()
    {
        var item = new Movie { Id = _itemId };
        SetUpItemAndUser(item);
        var id = PlaybackSessionId.NewId();
        _playbackSessionManager.Setup(m => m.Get(id)).Returns(BuildExistingSessionForAuth(id, Guid.NewGuid()));

        var result = await CreateController().ReplacePlaybackSession(id, CreateReplaceRequest());

        Assert.IsType<ForbidResult>(result.Result);
        _playbackSessionManager.Verify(m => m.Patch(It.IsAny<PlaybackSessionId>(), It.IsAny<PlaybackSessionRequest>()), Times.Never);
    }

    /// <summary>
    /// PR118: an administrator may replace a session it does not own - same elevated allowance
    /// <c>GetPlaybackSessionStream</c> already grants.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
    [Fact]
    public async Task ReplacePlaybackSession_Admin_ReturnsMappedResponseForOtherUsersSession()
    {
        var item = new Movie { Id = _itemId };
        SetUpItemAndUser(item);
        var id = PlaybackSessionId.NewId();
        _playbackSessionManager.Setup(m => m.Get(id)).Returns(BuildExistingSessionForAuth(id, Guid.NewGuid()));
        var patchedSession = new PlaybackSession(id, PlaybackMediaKind.Video, null, null, new PlaybackPlan(PlayMethod.Transcode, TranscodeReason.VideoCodecNotSupported), default, default);
        _playbackSessionManager.Setup(m => m.Patch(id, It.IsAny<PlaybackSessionRequest>())).Returns(patchedSession);

        var controller = CreateController();
        SetIdentity(controller, Guid.NewGuid(), isAdmin: true);

        var result = await controller.ReplacePlaybackSession(id, CreateReplaceRequest());

        Assert.IsAssignableFrom<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task DeletePlaybackSession_ExistingSession_ReturnsNoContent()
    {
        var id = PlaybackSessionId.NewId();
        _playbackSessionManager.Setup(m => m.Get(id)).Returns(BuildExistingSessionForAuth(id, _userId));
        _playbackSessionManager.Setup(m => m.Delete(id)).Returns(true);

        var result = await CreateController().DeletePlaybackSession(id);

        Assert.IsType<NoContentResult>(result);
    }

    [Fact]
    public async Task DeletePlaybackSession_EndsOnlyTheOwnersJobs_AndRevokesTheCapabilities_Issue301()
    {
        var id = PlaybackSessionId.NewId();
        var options = new MediaOptions { ItemId = _itemId, UserId = _userId, Profile = new DeviceProfile() };
        var session = new PlaybackSession(id, PlaybackMediaKind.Video, "play-301", new PlaybackSessionRequest(PlaybackMediaKind.Video, options), new PlaybackPlan(PlayMethod.Transcode, default), default, default);
        _playbackSessionManager.Setup(m => m.Get(id)).Returns(session);
        _playbackSessionManager.Setup(m => m.Delete(id)).Returns(true);

        Func<TranscodingJob, bool>? selected = null;
        var transcodeManager = new Mock<ITranscodeManager>();
        transcodeManager.As<ITranscodeOwnedStop>()
            .Setup(m => m.StopTranscodingJobs("play-301", It.IsAny<Func<TranscodingJob, bool>>()))
            .Callback<string, Func<TranscodingJob, bool>>((_, isOwn) => selected = isOwn)
            .Returns(Task.CompletedTask);
        var credentials = new Mock<IPlaybackCredentialService>();
        var revocation = credentials.As<IOwnedPlaySessionRevocation>();

        var controller = new PlaybackSessionsController(
            _playbackSessionManager.Object,
            _itemLookupService.Object,
            _userManager.Object,
            _mediaSourceManager.Object,
            _v2PlanStore.Object,
            transcodeManager: transcodeManager.Object,
            credentialService: credentials.Object);
        SetIdentity(controller, _userId);

        Assert.IsType<NoContentResult>(await controller.DeletePlaybackSession(id));

        revocation.Verify(c => c.RevokePlaySession("play-301", _userId, null), Times.Once);
        credentials.Verify(c => c.RevokePlaySession(It.IsAny<string>()), Times.Never);
        Assert.NotNull(selected);
        Assert.True(selected(JobOf(_userId)));
        Assert.False(selected(JobOf(Guid.NewGuid())));

        static TranscodingJob JobOf(Guid user) => new(Mock.Of<ILogger<TranscodingJob>>()) { UserId = user };
    }

    [Fact]
    public async Task DeletePlaybackSession_UnknownSession_ReturnsNotFound()
    {
        var id = PlaybackSessionId.NewId();
        _playbackSessionManager.Setup(m => m.Get(id)).Returns((PlaybackSession?)null);

        var result = await CreateController().DeletePlaybackSession(id);

        Assert.IsType<NotFoundResult>(result);
        _playbackSessionManager.Verify(m => m.Delete(It.IsAny<PlaybackSessionId>()), Times.Never);
    }

    /// <summary>
    /// PR118: DELETE used to have no ownership check at all - any authenticated caller who knew the
    /// id could end someone else's session. Same owner-or-admin semantics as
    /// <c>GetPlaybackSessionStream</c>.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
    [Fact]
    public async Task DeletePlaybackSession_OtherUser_ReturnsForbidden()
    {
        var id = PlaybackSessionId.NewId();
        _playbackSessionManager.Setup(m => m.Get(id)).Returns(BuildExistingSessionForAuth(id, Guid.NewGuid()));

        var result = await CreateController().DeletePlaybackSession(id);

        Assert.IsType<ForbidResult>(result);
        _playbackSessionManager.Verify(m => m.Delete(It.IsAny<PlaybackSessionId>()), Times.Never);
    }

    /// <summary>
    /// PR118: an administrator may delete a session it does not own - same elevated allowance
    /// <c>GetPlaybackSessionStream</c> already grants.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
    [Fact]
    public async Task DeletePlaybackSession_Admin_ReturnsNoContentForOtherUsersSession()
    {
        var id = PlaybackSessionId.NewId();
        _playbackSessionManager.Setup(m => m.Get(id)).Returns(BuildExistingSessionForAuth(id, Guid.NewGuid()));
        _playbackSessionManager.Setup(m => m.Delete(id)).Returns(true);

        var controller = CreateController();
        SetIdentity(controller, Guid.NewGuid(), isAdmin: true);

        var result = await controller.DeletePlaybackSession(id);

        Assert.IsType<NoContentResult>(result);
    }

    /// <summary>
    /// Issue #71, against a REAL <see cref="PlaybackSessionManager"/> rather than a mock: the
    /// session the POST established is still there the instant before the DELETE, the DELETE
    /// answers 204, and the <c>deleted</c> line PR #69 added actually gets emitted — carrying the
    /// session id and the attempt id that join it to the <c>created</c> line. The whole point of
    /// #71 is that this sequence was NOT observable: an ffmpeg job ending in between reaped the
    /// session, so the happy path answered 404 and the <c>deleted</c> line never appeared.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
    [Fact]
    public async Task DeletePlaybackSession_RealManagerAfterCreate_IsStillPresentAndLogsDeleted()
    {
        var (controller, manager, logger) = CreateRealManagerFixture();
        using var owned = manager;

        var created = await controller.CreatePlaybackSession(
            CreateRequest(playSessionId: "play-session-1") with { PlaybackAttemptId = AttemptId });
        var response = Assert.IsType<PlaybackSessionResponse>(
            Assert.IsType<Tesserafin.Api.Results.OkResult<PlaybackSessionResponse>>(created.Result).Value);
        var id = new PlaybackSessionId(response.Id);

        Assert.NotNull(manager.Get(id));
        logger.Entries.Clear();

        var deleted = await controller.DeletePlaybackSession(id);

        Assert.IsType<NoContentResult>(deleted);
        var entry = Assert.Single(logger.Entries, e => e.Message.Contains("deleted", StringComparison.Ordinal));
        // #203: this statement now projects the id at the logging boundary, so the state
        // dictionary carries the rendered string rather than the boxed PlaybackSessionId. The
        // bytes both shipped formatters write are unchanged - see PlaybackSessionIdProjectionTests.
        Assert.Equal(id.Value.ToString("N"), Assert.IsType<string>(entry.Properties["SessionId"]));
        Assert.Equal(AttemptId, entry.Properties["PlaybackAttemptId"]);
        Assert.Null(manager.Get(id));
    }

    /// <summary>
    /// Issue #71, the other half of the contract: when the server's own reaping genuinely got there
    /// first — <c>DeleteByPlaySessionId</c>, the primitive both lifecycle handlers and the TTL
    /// backstop funnel into — the DELETE answers 404 and says so in the log. This is the case
    /// <c>docs/issue43-design-playback-session-lifecycle.md</c> §4 calls a client-side success; what
    /// #71 changes is only that an ffmpeg job ending no longer produces it.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
    [Fact]
    public async Task DeletePlaybackSession_AfterServerSideReap_ReturnsNotFoundAndLogsAlreadyGone()
    {
        var (controller, manager, logger) = CreateRealManagerFixture();
        using var owned = manager;

        var created = await controller.CreatePlaybackSession(
            CreateRequest(playSessionId: "play-session-1") with { PlaybackAttemptId = AttemptId });
        var response = Assert.IsType<PlaybackSessionResponse>(
            Assert.IsType<Tesserafin.Api.Results.OkResult<PlaybackSessionResponse>>(created.Result).Value);
        var id = new PlaybackSessionId(response.Id);

        // The server-side reap primitive both lifecycle handlers (and the TTL sweep) funnel into.
        Assert.True(manager.DeleteByPlaySessionId("play-session-1"));
        logger.Entries.Clear();

        var deleted = await controller.DeletePlaybackSession(id);

        Assert.IsType<NotFoundResult>(deleted);
        var entry = Assert.Single(logger.Entries, e => e.Message.Contains("already gone", StringComparison.Ordinal));
        // #203: projected at the logging boundary - the state dictionary carries the rendered
        // string; both shipped formatters write the same bytes (PlaybackSessionIdProjectionTests).
        Assert.Equal(id.Value.ToString("N"), Assert.IsType<string>(entry.Properties["SessionId"]));
    }

    /// <summary>
    /// Issue #71: builds the controller over a real <see cref="PlaybackSessionManager"/>, so the
    /// DELETE tests above exercise genuine session bookkeeping instead of a mock that can be made
    /// to say anything.
    /// </summary>
    private (PlaybackSessionsController Controller, PlaybackSessionManager Manager, RecordingLogger Logger) CreateRealManagerFixture()
    {
        SetUpItemAndUser(new Movie { Id = _itemId });
        var plan = new PlaybackPlan(PlayMethod.DirectPlay, default);
        var planner = new Mock<IPlaybackSessionPlanner>();
        planner.Setup(p => p.PlanVideo(It.IsAny<MediaOptions>())).Returns(plan);
        var manager = new PlaybackSessionManager(
            planner.Object,
            new Mock<ITranscodeManager>().Object,
            new Mock<Tesserafin.Controller.Session.ISessionManager>().Object);
        var logger = new RecordingLogger();
        var controller = new PlaybackSessionsController(
            manager,
            _itemLookupService.Object,
            _userManager.Object,
            _mediaSourceManager.Object,
            _v2PlanStore.Object,
            logger);
        SetIdentity(controller, _userId);
        return (controller, manager, logger);
    }

    private PlaybackSession BuildStreamableSession(Guid ownerId, string? playSessionId)
    {
        var options = new MediaOptions { ItemId = _itemId, UserId = ownerId, DeviceId = "device-1", Profile = new DeviceProfile() };
        return new PlaybackSession(
            PlaybackSessionId.NewId(),
            PlaybackMediaKind.Video,
            playSessionId,
            new PlaybackSessionRequest(PlaybackMediaKind.Video, options),
            new PlaybackPlan(PlayMethod.DirectPlay, default),
            default,
            default);
    }

    [Fact]
    public void GetPlaybackSessionStream_NegativeStartTimeTicks_ReturnsBadRequest()
    {
        var session = BuildStreamableSession(_userId, "play-session-1");
        _playbackSessionManager.Setup(m => m.Get(session.Id)).Returns(session);

        var result = CreateController().GetPlaybackSessionStream(session.Id, startTimeTicks: -1);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    /// <summary>
    /// A caller authenticated without a bearer token must not receive a descriptor: the URL would
    /// lack its <c>&amp;ApiKey=</c> and 401 at fetch time, and a token-less principal has no
    /// business receiving a tokenized URL at all. PR118: the check now runs BEFORE resolution -
    /// verified here by asserting the resolver is never invoked and diagnostics are never read, not
    /// just that the response is a 403.
    /// </summary>
    [Fact]
    public void GetPlaybackSessionStream_UnknownSession_ReturnsNotFound()
    {
        var id = PlaybackSessionId.NewId();
        _playbackSessionManager.Setup(m => m.Get(id)).Returns((PlaybackSession?)null);

        var result = CreateController().GetPlaybackSessionStream(id);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    /// <summary>
    /// Design doc §4.2 (mandatory, new on this endpoint): an authenticated user who is neither the
    /// session's owner nor an administrator must never receive the descriptor - it carries the
    /// caller's own access token in <see cref="PlaybackSessionStreamDescriptor.Url"/>.
    /// </summary>
    [Fact]
    public void GetPlaybackSessionStream_OtherUser_ReturnsForbidden()
    {
        var session = BuildStreamableSession(Guid.NewGuid(), "play-session-1");
        _playbackSessionManager.Setup(m => m.Get(session.Id)).Returns(session);

        var result = CreateController().GetPlaybackSessionStream(session.Id);

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task ReplacePlaybackSession_ExistingSessionNoViablePlan_ReturnsUnprocessableEntity()
    {
        var item = new Movie { Id = _itemId };
        SetUpItemAndUser(item);
        var id = PlaybackSessionId.NewId();
        _playbackSessionManager.Setup(m => m.Get(id)).Returns(BuildExistingSessionForAuth(id, _userId));
        _playbackSessionManager
            .Setup(m => m.Patch(It.IsAny<PlaybackSessionId>(), It.IsAny<PlaybackSessionRequest>()))
            .Returns((PlaybackSession?)null);

        var result = await CreateController().ReplacePlaybackSession(id, CreateReplaceRequest());

        Assert.IsType<UnprocessableEntityObjectResult>(result.Result);
    }

    /// <summary>
    /// Issue #43: the POST transition emits exactly one lifecycle line carrying the named
    /// properties a log query correlates on - the session id, the attempt id, and the decided
    /// method - and emits it inside the attempt scope the action opened.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
    [Fact]
    public async Task CreatePlaybackSession_ViablePlan_LogsCreatedWithAttemptIdAndMethod()
    {
        var item = new Movie { Id = _itemId };
        SetUpItemAndUser(item);
        var session = new PlaybackSession(PlaybackSessionId.NewId(), PlaybackMediaKind.Video, null, null, new PlaybackPlan(PlayMethod.DirectPlay, default), default, default, AttemptId);
        _playbackSessionManager
            .Setup(m => m.Create(It.IsAny<PlaybackSessionRequest>(), null, AttemptId))
            .Returns(session);
        var logger = new RecordingLogger();

        await CreateController(logger).CreatePlaybackSession(CreateRequest() with { PlaybackAttemptId = AttemptId });

        var entry = Assert.Single(logger.Entries);
        Assert.Contains("created", entry.Message, StringComparison.Ordinal);
        Assert.Equal(session.Id, entry.Properties["SessionId"]);
        Assert.Equal(PlayMethod.DirectPlay, entry.Properties["PlayMethod"]);
        Assert.Equal(AttemptId, entry.Properties["PlaybackAttemptId"]);
        Assert.Contains(entry.ScopesAtLog, s => RecordingLogger.ScopeCarriesAttemptId(s, AttemptId));
    }

    /// <summary>
    /// Issue #43: the PUT transition logs the bits that actually change on a re-plan (old and new
    /// method) - and the attempt id it carries is the one the PATCHED session retains, so a PUT
    /// that omitted the field still logs the attempt recorded at creation ("not sent" is not
    /// "forget it").
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
    [Fact]
    public async Task ReplacePlaybackSession_ViablePlan_LogsReplacedWithOldAndNewMethod()
    {
        var item = new Movie { Id = _itemId };
        SetUpItemAndUser(item);
        var id = PlaybackSessionId.NewId();
        _playbackSessionManager.Setup(m => m.Get(id)).Returns(BuildExistingSessionForAuth(id, _userId) with { PlaybackAttemptId = AttemptId });
        var patched = new PlaybackSession(id, PlaybackMediaKind.Video, null, null, new PlaybackPlan(PlayMethod.Transcode, TranscodeReason.VideoCodecNotSupported), default, default, AttemptId);
        _playbackSessionManager
            .Setup(m => m.Patch(id, It.IsAny<PlaybackSessionRequest>()))
            .Returns(patched);
        var logger = new RecordingLogger();

        await CreateController(logger).ReplacePlaybackSession(id, CreateReplaceRequest());

        var entry = Assert.Single(logger.Entries);
        Assert.Contains("replaced", entry.Message, StringComparison.Ordinal);
        Assert.Equal(id, entry.Properties["SessionId"]);
        Assert.Equal(PlayMethod.DirectPlay, entry.Properties["OldPlayMethod"]);
        Assert.Equal(PlayMethod.Transcode, entry.Properties["NewPlayMethod"]);
        Assert.Equal(AttemptId, entry.Properties["PlaybackAttemptId"]);
    }

    /// <summary>
    /// Issue #43: GET Stream carries no attempt id of its own, so the handler recovers the one
    /// stored on the session and runs its work inside the same attempt scope POST/PUT open -
    /// proven by observing the scope genuinely active at the moment the resolver is invoked, and
    /// closed again by the time the action returns.
    /// </summary>
    [Fact]
    public void GetPlaybackSessionStream_UnknownSession_OpensNoAttemptScope()
    {
        var id = PlaybackSessionId.NewId();
        _playbackSessionManager.Setup(m => m.Get(id)).Returns((PlaybackSession?)null);
        var logger = new RecordingLogger();

        var result = CreateController(logger).GetPlaybackSessionStream(id);

        Assert.IsType<NotFoundResult>(result.Result);
        Assert.Empty(logger.Scopes);
    }

    /// <summary>
    /// Issue #43: the DELETE transition recovers the stored attempt id (a DELETE has no body to
    /// carry one) and logs the deletion inside that attempt scope, with the named properties a log
    /// query correlates on.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
    [Fact]
    public async Task DeletePlaybackSession_ExistingSession_LogsDeletedUnderStoredAttemptScope()
    {
        var id = PlaybackSessionId.NewId();
        _playbackSessionManager.Setup(m => m.Get(id)).Returns(BuildExistingSessionForAuth(id, _userId) with { PlaybackAttemptId = AttemptId });
        _playbackSessionManager.Setup(m => m.Delete(id)).Returns(true);
        var logger = new RecordingLogger();

        var result = await CreateController(logger).DeletePlaybackSession(id);

        Assert.IsType<NoContentResult>(result);
        var entry = Assert.Single(logger.Entries);
        Assert.Contains("deleted", entry.Message, StringComparison.Ordinal);
        // #203: this statement now projects the id at the logging boundary, so the state
        // dictionary carries the rendered string rather than the boxed PlaybackSessionId. The
        // bytes both shipped formatters write are unchanged - see PlaybackSessionIdProjectionTests.
        Assert.Equal(id.Value.ToString("N"), Assert.IsType<string>(entry.Properties["SessionId"]));
        Assert.Equal(AttemptId, entry.Properties["PlaybackAttemptId"]);
        Assert.Contains(entry.ScopesAtLog, s => RecordingLogger.ScopeCarriesAttemptId(s, AttemptId));
    }

    /// <summary>
    /// Issue #43: deleting a session that never existed (or already ended) still answers 404 - and
    /// now says so in the log, tied to the request via the ambient RequestId scope (#42), so an e2e
    /// can tell "teardown raced the TTL sweep" from "teardown never reached the server". No attempt
    /// scope: a missing session has no stored attempt id, and inventing one would be worse.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
    [Fact]
    public async Task DeletePlaybackSession_UnknownSession_LogsAlreadyGoneAndReturnsNotFound()
    {
        var id = PlaybackSessionId.NewId();
        _playbackSessionManager.Setup(m => m.Get(id)).Returns((PlaybackSession?)null);
        var logger = new RecordingLogger();

        var result = await CreateController(logger).DeletePlaybackSession(id);

        Assert.IsType<NotFoundResult>(result);
        _playbackSessionManager.Verify(m => m.Delete(It.IsAny<PlaybackSessionId>()), Times.Never);
        var entry = Assert.Single(logger.Entries);
        Assert.Contains("already gone", entry.Message, StringComparison.Ordinal);
        // #203: projected at the logging boundary - the state dictionary carries the rendered
        // string; both shipped formatters write the same bytes (PlaybackSessionIdProjectionTests).
        Assert.Equal(id.Value.ToString("N"), Assert.IsType<string>(entry.Properties["SessionId"]));
        Assert.Empty(logger.Scopes);
    }

    /// <summary>
    /// Issue #43: the session can vanish between the existence check and the delete (PlaybackStopped,
    /// TTL sweep, a concurrent DELETE) - same observable outcome as the not-found path: 404, logged
    /// as already gone.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
    [Fact]
    public async Task DeletePlaybackSession_RacedAwayBetweenGetAndDelete_LogsAlreadyGoneAndReturnsNotFound()
    {
        var id = PlaybackSessionId.NewId();
        _playbackSessionManager.Setup(m => m.Get(id)).Returns(BuildExistingSessionForAuth(id, _userId) with { PlaybackAttemptId = AttemptId });
        _playbackSessionManager.Setup(m => m.Delete(id)).Returns(false);
        var logger = new RecordingLogger();

        var result = await CreateController(logger).DeletePlaybackSession(id);

        Assert.IsType<NotFoundResult>(result);
        var entry = Assert.Single(logger.Entries);
        Assert.Contains("already gone", entry.Message, StringComparison.Ordinal);
        // #203: projected at the logging boundary - the state dictionary carries the rendered
        // string; both shipped formatters write the same bytes (PlaybackSessionIdProjectionTests).
        Assert.Equal(id.Value.ToString("N"), Assert.IsType<string>(entry.Properties["SessionId"]));
    }

    /// <summary>
    /// Issue #43: captures scopes and structured entries so tests can assert the attempt scope is
    /// genuinely open while handler work runs and that lifecycle lines carry the named properties a
    /// log query correlates on - same capturing approach as
    /// <c>RequestCorrelationMiddlewareTests.ScopeCapturingLogger</c>.
    /// </summary>
    private sealed class RecordingLogger : ILogger<PlaybackSessionsController>
    {
        private readonly List<object> _activeScopes = new();

        public List<object> Scopes { get; } = new();

        public List<RecordedEntry> Entries { get; } = new();

        public IReadOnlyList<object> ActiveScopes => _activeScopes;

        public static bool ScopeCarriesAttemptId(object scope, string attemptId) =>
            scope is IEnumerable<KeyValuePair<string, object>> pairs
            && pairs.Any(p => p.Key == "PlaybackAttemptId" && Equals(p.Value, attemptId));

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull
        {
            Scopes.Add(state);
            _activeScopes.Add(state);
            return new ScopeHandle(_activeScopes, state);
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var properties = state as IEnumerable<KeyValuePair<string, object?>> ?? Array.Empty<KeyValuePair<string, object?>>();
            Entries.Add(new RecordedEntry(
                formatter(state, exception),
                properties.ToDictionary(p => p.Key, p => p.Value),
                _activeScopes.ToList()));
        }

        public bool HasActiveScopeWithAttemptId(string attemptId) =>
            _activeScopes.Any(s => ScopeCarriesAttemptId(s, attemptId));

        public sealed record RecordedEntry(string Message, Dictionary<string, object?> Properties, List<object> ScopesAtLog);

        private sealed class ScopeHandle : IDisposable
        {
            private readonly List<object> _activeScopes;
            private readonly object _state;

            public ScopeHandle(List<object> activeScopes, object state)
            {
                _activeScopes = activeScopes;
                _state = state;
            }

            public void Dispose() => _activeScopes.Remove(_state);
        }
    }
}
