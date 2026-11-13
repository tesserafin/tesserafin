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
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Controller.Net.PlaybackCredentials;
using Tesserafin.Data.Enums;
using Tesserafin.Database.Implementations.Entities;
using Tesserafin.MediaEncoding.Playback;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.Dlna;
using Tesserafin.Model.Dto;
using Tesserafin.Model.Entities;
using Tesserafin.Model.Session;
using Tesserafin.Playback.Decision;
using Tesserafin.Playback.Dlna;
using Tesserafin.Playback.Execution;
using Xunit;

namespace Tesserafin.Api.Tests.Controllers;

public class PlaybackSessionsControllerTests
{
    private const string AttemptId = "attempt-7f3a";

    /// <summary>Issue #70: the single media source id shared by every demotion fixture below.</summary>
    private const string DemotionSourceId = "source-1";

    private readonly Mock<IPlaybackSessionManager> _playbackSessionManager = new();
    private readonly Mock<IItemAccessService> _itemLookupService = new();
    private readonly Mock<IUserManager> _userManager = new();
    private readonly Mock<IMediaSourceManager> _mediaSourceManager = new();
    private readonly Mock<IV2PlanStore> _v2PlanStore = new();
    private readonly Mock<IPlaybackLiveStreamResolver> _liveStreamResolver = new();
    private readonly Mock<IPlaybackLiveWiringDiagnosticsStore> _liveWiringDiagnosticsStore = new();
    private readonly Mock<IMediaEncoder> _mediaEncoder = new();
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
            _liveStreamResolver.Object,
            _liveWiringDiagnosticsStore.Object,
            _mediaEncoder.Object,
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
        SubtitleMode: SubtitlePlaybackMode.Default,
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
        var streamInfo = new StreamInfo { DeviceProfile = new DeviceProfile(), PlayMethod = PlayMethod.DirectPlay, Container = "mp4" };
        var plan = new PlaybackPlan(PlayMethod.DirectPlay, default, streamInfo);
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
            _v2PlanStore.Object,
            _liveStreamResolver.Object,
            _liveWiringDiagnosticsStore.Object,
            _mediaEncoder.Object);
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

    [Fact]
    public async Task CreatePlaybackSession_ViablePlan_ReturnsMappedResponse()
    {
        var item = new Movie { Id = _itemId };
        SetUpItemAndUser(item);
        var streamInfo = new StreamInfo { DeviceProfile = new DeviceProfile(), PlayMethod = PlayMethod.DirectPlay, Container = "mp4" };
        var session = new PlaybackSession(PlaybackSessionId.NewId(), PlaybackMediaKind.Video, null, null, new PlaybackPlan(PlayMethod.DirectPlay, default, streamInfo), default, default);
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
            _liveStreamResolver.Object,
            _liveWiringDiagnosticsStore.Object,
            _mediaEncoder.Object,
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
        var streamInfo = new StreamInfo { DeviceProfile = new DeviceProfile(), PlayMethod = PlayMethod.DirectPlay, Container = "mp4" };
        var plan = new PlaybackPlan(PlayMethod.DirectPlay, default, streamInfo);
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
            _liveStreamResolver.Object,
            _liveWiringDiagnosticsStore.Object,
            _mediaEncoder.Object,
            logger);
        SetIdentity(controller, _userId);
        return (controller, manager, logger);
    }

    private PlaybackSession BuildStreamableSession(Guid ownerId, string? playSessionId, StreamInfo? streamInfo = null)
    {
        var mediaSource = new MediaSourceInfo { Id = "source-1", Container = "mkv", SupportsDirectPlay = true };
        streamInfo ??= new StreamInfo
        {
            ItemId = _itemId,
            MediaSource = mediaSource,
            DeviceProfile = new DeviceProfile(),
            PlayMethod = PlayMethod.DirectPlay,
            Container = "mkv",
            AudioStreamIndex = 1,
        };
        var options = new MediaOptions { ItemId = _itemId, UserId = ownerId, DeviceId = "device-1", Profile = new DeviceProfile() };
        return new PlaybackSession(
            PlaybackSessionId.NewId(),
            PlaybackMediaKind.Video,
            playSessionId,
            new PlaybackSessionRequest(PlaybackMediaKind.Video, options),
            new PlaybackPlan(PlayMethod.DirectPlay, default, streamInfo),
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
    public void GetPlaybackSessionStream_CallerWithoutToken_ReturnsForbidden()
    {
        var session = BuildStreamableSession(_userId, "play-session-1");
        _playbackSessionManager.Setup(m => m.Get(session.Id)).Returns(session);

        var controller = CreateController();
        SetIdentity(controller, _userId, token: null);

        var result = controller.GetPlaybackSessionStream(session.Id);

        Assert.IsType<ForbidResult>(result.Result);
        _liveStreamResolver.Verify(
            r => r.Resolve(It.IsAny<PlaybackSessionId>(), It.IsAny<StreamInfo>(), It.IsAny<MediaSourceInfo>(), It.IsAny<DeviceProfile>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<bool>()),
            Times.Never);
        _liveWiringDiagnosticsStore.Verify(
            s => s.TryGet(It.IsAny<PlaybackSessionId>(), out It.Ref<PlaybackLiveWiringOutcome?>.IsAny),
            Times.Never);
    }

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
    public void GetPlaybackSessionStream_Owner_ReturnsDescriptor()
    {
        var session = BuildStreamableSession(_userId, "play-session-1");
        _playbackSessionManager.Setup(m => m.Get(session.Id)).Returns(session);
        _liveStreamResolver
            .Setup(r => r.Resolve(session.Id, session.Plan.StreamInfo!, It.IsAny<MediaSourceInfo>(), It.IsAny<DeviceProfile>(), _itemId, "device-1", "play-session-1", 0, false))
            .Returns(session.Plan.StreamInfo!);
        PlaybackLiveWiringOutcome? outcome = PlaybackLiveWiringOutcome.Served(DateTimeOffset.UtcNow);
        _liveWiringDiagnosticsStore.Setup(s => s.TryGet(session.Id, out outcome)).Returns(true);

        var result = CreateController().GetPlaybackSessionStream(session.Id);

        var descriptor = Assert.IsType<PlaybackSessionStreamDescriptor>(Assert.IsAssignableFrom<OkObjectResult>(result.Result).Value);
        Assert.NotNull(descriptor.Url);
    }

    /// <summary>
    /// PR118 regression: the legacy fallback path returns the SAME <see cref="StreamInfo"/> instance
    /// retained in <c>session.Plan.StreamInfo</c> (not a fresh projection) - two concurrent calls
    /// with different <c>startTimeTicks</c> used to race on that shared instance's
    /// <see cref="StreamInfo.PlaySessionId"/>/<see cref="StreamInfo.StartPositionTicks"/> before the
    /// URL was built. Each response's own <c>startTimeTicks</c> must always show up in ITS OWN URL,
    /// never the other call's, and the instance the session itself retains must come out completely
    /// untouched by either call. Uses separate controller instances per call - exactly like ASP.NET's
    /// own per-request controller lifetime - racing on the single shared mocked session/StreamInfo,
    /// the actual source of the bug.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
    [Fact]
    public async Task GetPlaybackSessionStream_ConcurrentCallsWithDifferentTicks_DoNotMutateSharedStreamInfo()
    {
        var session = BuildStreamableSession(_userId, "play-session-1");
        var sharedStreamInfo = session.Plan.StreamInfo!;
        _playbackSessionManager.Setup(m => m.Get(session.Id)).Returns(session);
        _liveStreamResolver
            .Setup(r => r.Resolve(session.Id, sharedStreamInfo, It.IsAny<MediaSourceInfo>(), It.IsAny<DeviceProfile>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<bool>()))
            .Returns(sharedStreamInfo);
        PlaybackLiveWiringOutcome? outcome = PlaybackLiveWiringOutcome.Fallback(PlaybackLiveFallbackReason.KillSwitch, DateTimeOffset.UtcNow);
        _liveWiringDiagnosticsStore.Setup(s => s.TryGet(session.Id, out outcome)).Returns(true);

        // Sanity: nothing has touched the shared instance yet.
        Assert.Equal(0, sharedStreamInfo.StartPositionTicks);
        Assert.Null(sharedStreamInfo.PlaySessionId);

        const long ticksA = 1000;
        const long ticksB = 999999;
        PlaybackSessionStreamDescriptor? descriptorA = null;
        PlaybackSessionStreamDescriptor? descriptorB = null;
        var barrier = new Barrier(2);
        var cancellationToken = TestContext.Current.CancellationToken;

        var taskA = Task.Run(
            () =>
            {
                var controller = CreateController();
                barrier.SignalAndWait(cancellationToken);
                var result = controller.GetPlaybackSessionStream(session.Id, ticksA);
                descriptorA = Assert.IsType<PlaybackSessionStreamDescriptor>(Assert.IsAssignableFrom<OkObjectResult>(result.Result).Value);
            },
            cancellationToken);
        var taskB = Task.Run(
            () =>
            {
                var controller = CreateController();
                barrier.SignalAndWait(cancellationToken);
                var result = controller.GetPlaybackSessionStream(session.Id, ticksB);
                descriptorB = Assert.IsType<PlaybackSessionStreamDescriptor>(Assert.IsAssignableFrom<OkObjectResult>(result.Result).Value);
            },
            cancellationToken);

        await Task.WhenAll(taskA, taskB);

        Assert.NotNull(descriptorA);
        Assert.NotNull(descriptorB);
        Assert.Contains($"&StartTimeTicks={ticksA}", descriptorA!.Url, StringComparison.Ordinal);
        Assert.Contains($"&StartTimeTicks={ticksB}", descriptorB!.Url, StringComparison.Ordinal);
        Assert.DoesNotContain($"&StartTimeTicks={ticksB}", descriptorA!.Url, StringComparison.Ordinal);
        Assert.DoesNotContain($"&StartTimeTicks={ticksA}", descriptorB!.Url, StringComparison.Ordinal);

        // The instance the session itself still holds must never have been mutated by either call.
        Assert.Equal(0, sharedStreamInfo.StartPositionTicks);
        Assert.Null(sharedStreamInfo.PlaySessionId);
    }

    /// <summary>
    /// Design doc §4.2: an administrator may resolve a stream URL for a session it does not own -
    /// same elevated allowance <c>RequestHelpers.GetUserId</c> already grants elsewhere.
    /// </summary>
    [Fact]
    public void GetPlaybackSessionStream_Admin_ReturnsDescriptorForOtherUsersSession()
    {
        var session = BuildStreamableSession(Guid.NewGuid(), "play-session-1");
        _playbackSessionManager.Setup(m => m.Get(session.Id)).Returns(session);
        _liveStreamResolver
            .Setup(r => r.Resolve(session.Id, It.IsAny<StreamInfo>(), It.IsAny<MediaSourceInfo>(), It.IsAny<DeviceProfile>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<bool>()))
            .Returns(session.Plan.StreamInfo!);
        PlaybackLiveWiringOutcome? outcome = PlaybackLiveWiringOutcome.Served(DateTimeOffset.UtcNow);
        _liveWiringDiagnosticsStore.Setup(s => s.TryGet(session.Id, out outcome)).Returns(true);

        var controller = CreateController();
        SetIdentity(controller, Guid.NewGuid(), isAdmin: true);

        var result = controller.GetPlaybackSessionStream(session.Id);

        Assert.IsAssignableFrom<OkObjectResult>(result.Result);
    }

    /// <summary>
    /// Design doc §2.3 (mandatory): a session with no <c>PlaySessionId</c> cannot correlate a served
    /// URL to the transcoding job lifecycle - the endpoint must refuse with <c>409</c>, not emit one.
    /// </summary>
    [Fact]
    public void GetPlaybackSessionStream_NoPlaySessionId_ReturnsConflict()
    {
        var session = BuildStreamableSession(_userId, playSessionId: null);
        _playbackSessionManager.Setup(m => m.Get(session.Id)).Returns(session);

        var result = CreateController().GetPlaybackSessionStream(session.Id);

        Assert.IsType<ConflictObjectResult>(result.Result);
        _liveStreamResolver.Verify(
            r => r.Resolve(It.IsAny<PlaybackSessionId>(), It.IsAny<StreamInfo>(), It.IsAny<MediaSourceInfo>(), It.IsAny<DeviceProfile>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<bool>()),
            Times.Never);
    }

    /// <summary>
    /// PR #38 (amended, 2cf777d2): the endpoint used to answer <c>409</c> for BOTH "no PlaySessionId"
    /// and "no plannable stream". OpenAPI admits exactly one <c>responses</c> entry per status code
    /// per operation, so two 409s discriminated only by their body cannot exist in the contract and
    /// are invisible to every generated client. The conditions differ in kind - the 409 above is
    /// repairable by a <c>PUT</c> supplying a <c>PlaySessionId</c>, this one is structurally
    /// unservable - so this one is <c>422</c>. Asserted for each of the three shapes that reach it.
    /// </summary>
    /// <param name="noOptions">When true the session carries no request, so no playback options can be resolved.</param>
    /// <param name="noStreamInfo">When true the plan carries no <c>StreamInfo</c>.</param>
    /// <param name="noMediaSource">When true the plan's <c>StreamInfo</c> carries no media source.</param>
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void GetPlaybackSessionStream_NoPlannableStream_ReturnsUnprocessableEntity(bool noOptions, bool noStreamInfo, bool noMediaSource)
    {
        StreamInfo? streamInfo = noStreamInfo
            ? null
            : new StreamInfo
            {
                ItemId = _itemId,
                DeviceProfile = new DeviceProfile(),
                PlayMethod = PlayMethod.DirectPlay,
                Container = "mkv",
                MediaSource = noMediaSource ? null : new MediaSourceInfo { Id = "source-1" },
            };

        var request = noOptions
            ? null
            : new PlaybackSessionRequest(PlaybackMediaKind.Video, new MediaOptions { ItemId = _itemId, UserId = _userId, DeviceId = "device-1", Profile = new DeviceProfile() });

        var session = new PlaybackSession(
            PlaybackSessionId.NewId(),
            PlaybackMediaKind.Video,
            "play-session-1",
            request,
            new PlaybackPlan(PlayMethod.DirectPlay, default, streamInfo),
            default,
            default);
        _playbackSessionManager.Setup(m => m.Get(session.Id)).Returns(session);

        var controller = CreateController();
        // A null Request means EnsureCallerOwnsSessionOrIsAdmin has no owner to compare against, so
        // only an administrator gets far enough to observe the 422 in that shape.
        SetIdentity(controller, _userId, isAdmin: true);

        var result = controller.GetPlaybackSessionStream(session.Id);

        Assert.IsType<UnprocessableEntityObjectResult>(result.Result);
        _liveStreamResolver.Verify(
            r => r.Resolve(It.IsAny<PlaybackSessionId>(), It.IsAny<StreamInfo>(), It.IsAny<MediaSourceInfo>(), It.IsAny<DeviceProfile>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<bool>()),
            Times.Never);
    }

    /// <summary>
    /// PR #38 (amended, 2cf777d2): the session was already proven to exist by the ownership check, so
    /// a null from <c>Patch</c> can only mean re-planning produced no viable plan. That used to be a
    /// <c>404</c>, indistinguishable from an unknown id - precisely the distinction a client needs on
    /// a track change. <c>404</c> stays reserved for the unknown id
    /// (<see cref="ReplacePlaybackSession_UnknownSession_ReturnsNotFound"/>).
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
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
    /// Issue #44 §8 arbitrage A: the server picked a container and reported it nowhere, so the client
    /// had to borrow the legacy <c>TranscodingContainer</c> or fall back to legacy entirely. The
    /// descriptor now carries the effective output container and its content type, taken from the
    /// very <see cref="StreamInfo"/> that produced <c>Url</c>.
    /// </summary>
    [Fact]
    public void GetPlaybackSessionStream_Http_ReportsEffectiveContainerAndMimeType()
    {
        var session = BuildStreamableSession(_userId, "play-session-1");
        _playbackSessionManager.Setup(m => m.Get(session.Id)).Returns(session);
        _liveStreamResolver
            .Setup(r => r.Resolve(It.IsAny<PlaybackSessionId>(), It.IsAny<StreamInfo>(), It.IsAny<MediaSourceInfo>(), It.IsAny<DeviceProfile>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<bool>()))
            .Returns(session.Plan.StreamInfo!);
        PlaybackLiveWiringOutcome? outcome = PlaybackLiveWiringOutcome.Served(DateTimeOffset.UtcNow);
        _liveWiringDiagnosticsStore.Setup(s => s.TryGet(session.Id, out outcome)).Returns(true);

        var result = CreateController().GetPlaybackSessionStream(session.Id);

        var descriptor = Assert.IsType<PlaybackSessionStreamDescriptor>(Assert.IsAssignableFrom<OkObjectResult>(result.Result).Value);
        Assert.Equal("mkv", descriptor.Container);
        Assert.Equal("video/x-matroska", descriptor.MimeType);
        // The container reported is the one the URL itself carries - that is the whole point.
        Assert.Contains("/stream.mkv", descriptor.Url, StringComparison.Ordinal);
    }

    /// <summary>
    /// On HLS the URL addresses <c>master.m3u8</c> while <c>Container</c> is the SEGMENT container
    /// (<c>&amp;SegmentContainer=</c>), so the reported type must be the playlist's, never the
    /// segments'.
    /// </summary>
    [Fact]
    public void GetPlaybackSessionStream_Hls_ReportsSegmentContainerAndPlaylistMimeType()
    {
        var hlsStreamInfo = new StreamInfo
        {
            ItemId = _itemId,
            MediaSource = new MediaSourceInfo { Id = "source-1", Container = "mkv" },
            DeviceProfile = new DeviceProfile(),
            PlayMethod = PlayMethod.Transcode,
            Container = "ts",
            SubProtocol = MediaStreamProtocol.hls,
        };
        var session = BuildStreamableSession(_userId, "play-session-1", hlsStreamInfo);
        _playbackSessionManager.Setup(m => m.Get(session.Id)).Returns(session);
        _liveStreamResolver
            .Setup(r => r.Resolve(It.IsAny<PlaybackSessionId>(), It.IsAny<StreamInfo>(), It.IsAny<MediaSourceInfo>(), It.IsAny<DeviceProfile>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<bool>()))
            .Returns(hlsStreamInfo);
        PlaybackLiveWiringOutcome? outcome = PlaybackLiveWiringOutcome.Served(DateTimeOffset.UtcNow);
        _liveWiringDiagnosticsStore.Setup(s => s.TryGet(session.Id, out outcome)).Returns(true);

        var result = CreateController().GetPlaybackSessionStream(session.Id);

        var descriptor = Assert.IsType<PlaybackSessionStreamDescriptor>(Assert.IsAssignableFrom<OkObjectResult>(result.Result).Value);
        Assert.Equal(StreamingProtocol.Hls, descriptor.Protocol);
        Assert.Equal("ts", descriptor.Container);
        Assert.Equal("application/vnd.apple.mpegurl", descriptor.MimeType);
    }

    /// <summary>
    /// Design doc §3: <c>ServedBy</c> must be the real v2 engine version - read back from the same
    /// <see cref="IV2PlanStore"/> record whose <see cref="V2PlanRecord.ExecutionPlan"/> the resolver
    /// used - only when the live-wiring outcome the resolver itself just recorded says v2 served
    /// this request; <c>FallbackReason</c> must be <see langword="null"/> in that case.
    /// </summary>
    [Fact]
    public void GetPlaybackSessionStream_ServedByV2_ReflectsRealEngineVersionAndNullFallbackReason()
    {
        var session = BuildStreamableSession(_userId, "play-session-1");
        _playbackSessionManager.Setup(m => m.Get(session.Id)).Returns(session);
        _liveStreamResolver
            .Setup(r => r.Resolve(It.IsAny<PlaybackSessionId>(), It.IsAny<StreamInfo>(), It.IsAny<MediaSourceInfo>(), It.IsAny<DeviceProfile>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<bool>()))
            .Returns(session.Plan.StreamInfo!);
        PlaybackLiveWiringOutcome? outcome = PlaybackLiveWiringOutcome.Served(DateTimeOffset.UtcNow);
        _liveWiringDiagnosticsStore.Setup(s => s.TryGet(session.Id, out outcome)).Returns(true);

        var selectedStreams = new SelectedStreams(0, 1, null);
        var output = new OutputSpec("mkv", "h264", "aac", null, null, null, null, null, null, StreamingProtocol.Http, null);
        var reasoning = ReasonNode.Leaf(ReasonCode.MethodChosen, ReasonOutcome.Chosen, ReasonSubject.Method());
        var decision = PlaybackDecision.DirectPlay("source-1", selectedStreams, output, reasoning, engineVersion: 6);
        V2PlanRecord? v2Record = new V2PlanRecord(decision, ExecutionPlan: null, DateTimeOffset.UtcNow);
        _v2PlanStore.Setup(s => s.TryGet(session.Id, out v2Record)).Returns(true);

        var result = CreateController().GetPlaybackSessionStream(session.Id);

        var descriptor = Assert.IsType<PlaybackSessionStreamDescriptor>(Assert.IsAssignableFrom<OkObjectResult>(result.Result).Value);
        Assert.Equal(6, descriptor.ServedBy);
        Assert.Null(descriptor.FallbackReason);
    }

    /// <summary>
    /// Design doc §3: <c>FallbackReason</c> must reflect exactly the typed reason the resolver
    /// itself recorded for THIS call, and <c>ServedBy</c> must fall back to the legacy sentinel -
    /// never a stale engine version left over from an earlier <c>POST</c>/<c>PUT</c>.
    /// </summary>
    /// <param name="reason">Typed fallback reason the resolver recorded for this call.</param>
    [Theory]
    [InlineData(PlaybackLiveFallbackReason.KillSwitch)]
    [InlineData(PlaybackLiveFallbackReason.SourceIdMismatch)]
    [InlineData(PlaybackLiveFallbackReason.DolbyVisionExclusion)]
    [InlineData(PlaybackLiveFallbackReason.NoAuthoritativeRecord)]
    [InlineData(PlaybackLiveFallbackReason.PlanNotExecutable)]
    [InlineData(PlaybackLiveFallbackReason.AdapterError)]
    [InlineData(PlaybackLiveFallbackReason.StopThresholdTripped)]
    public void GetPlaybackSessionStream_Fallback_ReflectsTypedReasonAndLegacySentinel(PlaybackLiveFallbackReason reason)
    {
        var session = BuildStreamableSession(_userId, "play-session-1");
        _playbackSessionManager.Setup(m => m.Get(session.Id)).Returns(session);
        _liveStreamResolver
            .Setup(r => r.Resolve(It.IsAny<PlaybackSessionId>(), It.IsAny<StreamInfo>(), It.IsAny<MediaSourceInfo>(), It.IsAny<DeviceProfile>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<bool>()))
            .Returns(session.Plan.StreamInfo!);
        PlaybackLiveWiringOutcome? outcome = PlaybackLiveWiringOutcome.Fallback(reason, DateTimeOffset.UtcNow);
        _liveWiringDiagnosticsStore.Setup(s => s.TryGet(session.Id, out outcome)).Returns(true);

        var result = CreateController().GetPlaybackSessionStream(session.Id);

        var descriptor = Assert.IsType<PlaybackSessionStreamDescriptor>(Assert.IsAssignableFrom<OkObjectResult>(result.Result).Value);
        Assert.Equal(PlaybackSessionResponse.LegacyDecisionVersion, descriptor.ServedBy);
        Assert.Equal(reason, descriptor.FallbackReason);
        // Even when a v2 record happens to still be retained (a mismatch/kill-switch/etc. fallback
        // leaves it in place, per design doc §3.1), ServedBy must not report its engine version -
        // only a live-wiring outcome that actually says "served by v2" may do that.
        _v2PlanStore.Verify(s => s.TryGet(It.IsAny<PlaybackSessionId>(), out It.Ref<V2PlanRecord?>.IsAny), Times.Never);
    }

    /// <summary>
    /// Design doc §1.3/§3.2: the URL this endpoint returns for a legacy-fallback session must be
    /// exactly <c>StreamInfo.ToUrl(...)</c>'s own output for the session's planned <c>StreamInfo</c> -
    /// the same call <c>MediaInfoHelper.SetDeviceSpecificData</c> already makes to build
    /// <c>mediaSource.TranscodingUrl</c> for the identical <see cref="StreamInfo"/> shape, proving
    /// this endpoint reuses <c>ResolveServedStreamInfo</c>'s extracted logic verbatim rather than a
    /// new URL-serialization path. Uses the REAL <see cref="PlaybackLiveStreamResolver"/> (kill
    /// switch forced off), not a mock, so the fallback decision is genuinely exercised.
    /// </summary>
    [Fact]
    public void GetPlaybackSessionStream_LegacyFallback_UrlMatchesStreamInfoToUrl_Parity()
    {
        var mediaSource = new MediaSourceInfo { Id = "source-1", Container = "mkv", SupportsDirectPlay = true };
        var streamInfo = new StreamInfo
        {
            ItemId = _itemId,
            MediaSource = mediaSource,
            DeviceProfile = new DeviceProfile(),
            PlayMethod = PlayMethod.DirectPlay,
            Container = "mkv",
            AudioStreamIndex = 1,
        };
        var session = BuildStreamableSession(_userId, "play-session-1", streamInfo);
        _playbackSessionManager.Setup(m => m.Get(session.Id)).Returns(session);

        var configManager = new Mock<IServerConfigurationManager>();
        configManager.Setup(c => c.Configuration).Returns(new ServerConfiguration { PlaybackShadow = new PlaybackShadowOptions { Mode = PlaybackEngineMode.Legacy } });
        var liveWiringStore = new InMemoryPlaybackLiveWiringDiagnosticsStore();
        var realResolver = new PlaybackLiveStreamResolver(
            configManager.Object,
            Mock.Of<IPlaybackExecutionPlanResolver>(),
            liveWiringStore,
            new PlaybackOperationalMetrics(),
            new PlaybackStopThresholdGuard(() => new PlaybackShadowOptions(), new PlaybackOperationalMetrics(), Mock.Of<ILogger<PlaybackStopThresholdGuard>>()),
            Mock.Of<ILogger<PlaybackLiveStreamResolver>>());

        var controller = new PlaybackSessionsController(
            _playbackSessionManager.Object,
            _itemLookupService.Object,
            _userManager.Object,
            _mediaSourceManager.Object,
            _v2PlanStore.Object,
            realResolver,
            liveWiringStore,
            _mediaEncoder.Object);
        SetIdentity(controller, _userId, token: "caller-token");

        var result = controller.GetPlaybackSessionStream(session.Id, startTimeTicks: 500);

        var descriptor = Assert.IsType<PlaybackSessionStreamDescriptor>(Assert.IsAssignableFrom<OkObjectResult>(result.Result).Value);
        Assert.Equal(PlaybackSessionResponse.LegacyDecisionVersion, descriptor.ServedBy);
        Assert.Equal(PlaybackLiveFallbackReason.KillSwitch, descriptor.FallbackReason);

        // Independent oracle: the exact StreamInfo.ToUrl call the legacy path itself makes.
        streamInfo.PlaySessionId = "play-session-1";
        streamInfo.StartPositionTicks = 500;
        var expectedUrl = streamInfo.ToUrl(null, null, null);
        Assert.Equal(expectedUrl, descriptor.Url);
    }

    /// <summary>
    /// Design doc §1.3/§3.2, v2-served case: the URL must exactly match
    /// <c>PlaybackExecutionPlanAdapter.ToStreamInfo</c>'s own output for the retained v2 plan - the
    /// same adapter call <c>ResolveServedStreamInfo</c>'s extracted logic makes internally. Uses the
    /// REAL <see cref="PlaybackLiveStreamResolver"/>/<see cref="PlaybackExecutionPlanResolver"/>/
    /// <see cref="InMemoryV2PlanStore"/>, not mocks, so the v2-serving decision is genuinely
    /// exercised end to end.
    /// </summary>
    [Fact]
    public void GetPlaybackSessionStream_V2Served_UrlMatchesPlaybackExecutionPlanAdapterOutput_Parity()
    {
        var mediaSource = new MediaSourceInfo
        {
            Id = "source-1",
            Container = "mkv",
            SupportsDirectPlay = true,
            MediaStreams = new List<MediaStream>
            {
                new() { Type = MediaStreamType.Video, Index = 0, Codec = "h264" },
                new() { Type = MediaStreamType.Audio, Index = 1, Codec = "aac" },
            },
        };
        var legacyStreamInfo = new StreamInfo
        {
            ItemId = _itemId,
            MediaSource = mediaSource,
            DeviceProfile = new DeviceProfile(),
            PlayMethod = PlayMethod.DirectPlay,
            Container = "mkv",
            AudioStreamIndex = 5,
            VideoCodecs = ["h264"],
            AudioCodecs = ["aac"],
        };
        var profile = new DeviceProfile();
        var options = new MediaOptions { ItemId = _itemId, UserId = _userId, DeviceId = "device-1", Profile = profile };
        var session = new PlaybackSession(
            PlaybackSessionId.NewId(),
            PlaybackMediaKind.Video,
            "play-session-1",
            new PlaybackSessionRequest(PlaybackMediaKind.Video, options),
            new PlaybackPlan(PlayMethod.DirectPlay, default, legacyStreamInfo),
            default,
            default);
        _playbackSessionManager.Setup(m => m.Get(session.Id)).Returns(session);

        var selectedStreams = new SelectedStreams(Video: 0, Audio: 1, Subtitle: null);
        var output = new OutputSpec("mkv", "h264", "aac", null, null, null, null, null, null, StreamingProtocol.Http, null);
        var reasoning = ReasonNode.Leaf(ReasonCode.MethodChosen, ReasonOutcome.Chosen, ReasonSubject.Method());
        var decision = PlaybackDecision.DirectPlay("source-1", selectedStreams, output, reasoning, engineVersion: 6);
        var executionPlan = new PlaybackExecutionPlan(
            PlaybackMethod.DirectPlay,
            "source-1",
            output.Container!,
            output.Protocol,
            selectedStreams.Video,
            output.VideoCodec,
            output.VideoBitrate,
            output.Resolution,
            output.VideoRange,
            selectedStreams.Audio,
            output.AudioCodec,
            output.AudioBitrate,
            output.AudioChannels,
            output.TotalBitrate,
            selectedStreams.Subtitle?.Index,
            selectedStreams.Subtitle?.Delivery,
            output.SubtitleFormat,
            new List<TransformKind>());
        var v2PlanStore = new InMemoryV2PlanStore();
        v2PlanStore.Attach(session.Id, new V2PlanRecord(decision, executionPlan, DateTimeOffset.UtcNow));

        var configManager = new Mock<IServerConfigurationManager>();
        configManager.Setup(c => c.Configuration).Returns(new ServerConfiguration { PlaybackShadow = new PlaybackShadowOptions { Mode = PlaybackEngineMode.Canary, CanaryPercentage = 100 } });
        var liveWiringStore = new InMemoryPlaybackLiveWiringDiagnosticsStore();
        var executionPlanResolver = new PlaybackExecutionPlanResolver(v2PlanStore);
        var realResolver = new PlaybackLiveStreamResolver(
            configManager.Object,
            executionPlanResolver,
            liveWiringStore,
            new PlaybackOperationalMetrics(),
            new PlaybackStopThresholdGuard(() => new PlaybackShadowOptions(), new PlaybackOperationalMetrics(), Mock.Of<ILogger<PlaybackStopThresholdGuard>>()),
            Mock.Of<ILogger<PlaybackLiveStreamResolver>>());

        var controller = new PlaybackSessionsController(
            _playbackSessionManager.Object,
            _itemLookupService.Object,
            _userManager.Object,
            _mediaSourceManager.Object,
            v2PlanStore,
            realResolver,
            liveWiringStore,
            _mediaEncoder.Object);
        SetIdentity(controller, _userId, token: "caller-token");

        var result = controller.GetPlaybackSessionStream(session.Id, startTimeTicks: 250);

        var descriptor = Assert.IsType<PlaybackSessionStreamDescriptor>(Assert.IsAssignableFrom<OkObjectResult>(result.Result).Value);
        Assert.Equal(6, descriptor.ServedBy);
        Assert.Null(descriptor.FallbackReason);

        var context = new PlaybackExecutionContext(_itemId, "play-session-1", "device-1", profile.Id?.ToString("N"), 250, false);
        var expectedStreamInfo = PlaybackExecutionPlanAdapter.ToStreamInfo(executionPlan, context, mediaSource, profile);
        expectedStreamInfo.PlaySessionId = "play-session-1";
        expectedStreamInfo.StartPositionTicks = 250;
        Assert.Equal(expectedStreamInfo.ToUrl(null, null, null), descriptor.Url);
    }

    // ---------------------------------------------------------------------------------------
    // Issue #70: POST DirectPlay -> PUT AllowDirectPlay:false -> GET Stream.
    //
    // The retry ladder reefin-web sends when a Direct Play attempt fails re-plans the SAME,
    // still-directly-playable media with EnableDirectPlay:false (+ EnableDirectStream:false for a
    // local source). The v2 engine used to pick the method from the media alone, find Direct Play
    // forbidden by the constraints, and answer NotViable - so PlaybackExecutionPlanBuilder refused,
    // ShadowPlaybackSessionPlanner published a V2PlanRecord with a NULL ExecutionPlan, and every
    // subsequent GET /Stream fell back to legacy with PlaybackLiveFallbackReason.PlanNotExecutable.
    // Legacy StreamBuilder demotes the same input to Transcode and answers 200
    // (StreamBuilder.cs:729-730).
    //
    // These use the REAL PlaybackEngine, PlaybackExecutionPlanBuilder, InMemoryV2PlanStore,
    // PlaybackExecutionPlanResolver and PlaybackLiveStreamResolver - only the session manager (which
    // this PR must not touch) and the item/user lookups stay mocked.
    // ---------------------------------------------------------------------------------------

    private static Tesserafin.Playback.Decision.ClientCapabilities CreateDemotionCapabilities() => new(
        Decode: new DecodeCapabilities(
            DirectPlayProfiles: [new DecodeProfile(MediaKind.Video, ["mp4"], ["h264"], ["aac"])],
            VideoCodecs: [new VideoCodecCapability("h264", [], null, null, [], null, null)],
            AudioCodecs: [new AudioCodecCapability("aac", null, null, null, null)],
            SubtitleDelivery: [],
            SupportsHls: false,
            SupportsDash: false),
        OutputProfiles: [new PlaybackOutputProfile(MediaKind.Video, StreamingProtocol.Http, "mp4", ["h264"], ["aac"], null, null, null)]);

    private static PlaybackConstraints CreateDemotionConstraints(bool allowDirectPlay, bool allowDirectStream)
        => CreateConstraints() with { AllowDirectPlay = allowDirectPlay, AllowDirectStream = allowDirectStream };

    private PlaybackRequestContext CreateDemotionContext() => new(
        RequestId: Guid.NewGuid(),
        ItemId: _itemId,
        MediaSourceId: null,
        UserId: _userId,
        MediaKind: MediaKind.Video,
        RequestedAt: DateTimeOffset.UtcNow,
        EngineVersion: Tesserafin.Playback.Engine.PlaybackEngine.EngineVersion);

    /// <summary>The media the client CAN direct-play: mp4/h264/aac, exactly what the POST planned.</summary>
    private static MediaSourceSnapshot CreateDirectlyPlayableSnapshot() => new(
        MediaSourceId: DemotionSourceId,
        Container: "mp4",
        Protocol: "http",
        Bitrate: null,
        RunTimeTicks: null,
        VideoStreams: [new VideoStreamSnapshot(0, "h264", null, null, null, null, null, null, null, null, false, false)],
        AudioStreams: [new AudioStreamSnapshot(1, "aac", 2, null, null, null, null, true)],
        SubtitleStreams: [],
        SupportsDirectPlay: true,
        SupportsDirectStream: true,
        SupportsTranscoding: true);

    /// <summary>
    /// The same shape, but with codecs the client cannot decode at all - the engine reaches
    /// Transcode from the MEDIA here, with no demotion involved. The independent oracle for the
    /// plan-equality obligation below.
    /// </summary>
    private static MediaSourceSnapshot CreateUndecodableSnapshot() => new(
        MediaSourceId: DemotionSourceId,
        Container: "mp4",
        Protocol: "http",
        Bitrate: null,
        RunTimeTicks: null,
        VideoStreams: [new VideoStreamSnapshot(0, "hevc", null, null, null, null, null, null, null, null, false, false)],
        AudioStreams: [new AudioStreamSnapshot(1, "ac3", 2, null, null, null, null, true)],
        SubtitleStreams: [],
        SupportsDirectPlay: true,
        SupportsDirectStream: true,
        SupportsTranscoding: true);

    private static PlaybackExecutionPlan BuildPlanOrFail(PlaybackDecision decision, string label)
    {
        Assert.True(decision.IsViable, $"{label}: decision is not viable (method {decision.Method}).");
        Assert.True(
            PlaybackExecutionPlanBuilder.TryBuild(decision, out var plan, out var refusal),
            $"{label}: PlaybackExecutionPlanBuilder refused with {refusal}.");
        return plan!;
    }

    /// <summary>
    /// Field-for-field plan equality. The <c>with { Transforms = [] }</c> record comparison is
    /// deliberate belt-and-braces: it covers every field of
    /// <see cref="PlaybackExecutionPlan"/> - including any added later - while the explicit
    /// transform-sequence assertion covers the one member record equality compares by reference.
    /// </summary>
    private static void AssertExecutionPlansEqual(PlaybackExecutionPlan expected, PlaybackExecutionPlan actual)
    {
        Assert.Equal(expected.Method, actual.Method);
        Assert.Equal(expected.Container, actual.Container);
        Assert.Equal(expected.VideoCodec, actual.VideoCodec);
        Assert.Equal(expected.AudioCodec, actual.AudioCodec);
        Assert.Equal(expected.Transforms, actual.Transforms);
        Assert.Equal(expected with { Transforms = [] }, actual with { Transforms = [] });
    }

    /// <summary>
    /// Issue #70 PHASE 3, the equality obligation at the execution level: the Transcode plan the
    /// constraint-forbidden PUT produces must be the SAME plan the engine builds for a Transcode it
    /// reached without any demotion, and must adapt to the same legacy <see cref="StreamInfo"/> and
    /// the same served URL. Two independent references are compared, because "the plan a fresh POST
    /// produces for the same media" has two readings and both must hold:
    /// <list type="number">
    /// <item>a fresh POST carrying the retry constraints (same media, no prior session) - proves the
    /// PUT path is not special-cased relative to a first-planning of the same request;</item>
    /// <item>a POST whose media (hevc/ac3, undecodable by this client) forces Transcode with no
    /// demotion at all - the reference a plan synthesized from a NotViable decision, or a widened
    /// v2 transform capability, could not match.</item>
    /// </list>
    /// </summary>
    [Fact]
    public void ReplacePlaybackSession_DirectPlayForbidden_PlanEqualsUndemotedTranscodePlan()
    {
        var engine = new Tesserafin.Playback.Engine.PlaybackEngine();
        var capabilities = CreateDemotionCapabilities();

        // POST: the session as originally planned - Direct Play, executable.
        var postDecision = engine.Decide(
            CreateDemotionContext(),
            capabilities,
            [CreateDirectlyPlayableSnapshot()],
            CreateDemotionConstraints(allowDirectPlay: true, allowDirectStream: true));
        Assert.Equal(PlaybackMethod.DirectPlay, postDecision.Method);
        BuildPlanOrFail(postDecision, "POST");

        // PUT: the retry, same media, Direct Play and Direct Stream both forbidden.
        var putPlan = BuildPlanOrFail(
            engine.Decide(
                CreateDemotionContext(),
                capabilities,
                [CreateDirectlyPlayableSnapshot()],
                CreateDemotionConstraints(allowDirectPlay: false, allowDirectStream: false)),
            "PUT (AllowDirectPlay:false)");

        // Reference 1: a fresh POST already carrying the retry constraints.
        var freshPlan = BuildPlanOrFail(
            engine.Decide(
                CreateDemotionContext(),
                capabilities,
                [CreateDirectlyPlayableSnapshot()],
                CreateDemotionConstraints(allowDirectPlay: false, allowDirectStream: false)),
            "fresh POST (AllowDirectPlay:false)");

        // Reference 2: a POST the MEDIA forces to Transcode - no demotion anywhere in its path.
        var undemotedPlan = BuildPlanOrFail(
            engine.Decide(
                CreateDemotionContext(),
                capabilities,
                [CreateUndecodableSnapshot()],
                CreateDemotionConstraints(allowDirectPlay: true, allowDirectStream: true)),
            "POST (media-forced transcode)");

        Assert.Equal(PlaybackMethod.Transcode, undemotedPlan.Method);
        AssertExecutionPlansEqual(freshPlan, putPlan);
        AssertExecutionPlansEqual(undemotedPlan, putPlan);

        // ... and the same plan adapts to the same legacy StreamInfo and the same served URL.
        var mediaSource = CreateDemotionMediaSource();
        var profile = new DeviceProfile();
        var context = new PlaybackExecutionContext(_itemId, "play-session-1", "device-1", profile.Id?.ToString("N"), 0, false);
        var undemotedStreamInfo = PlaybackExecutionPlanAdapter.ToStreamInfo(undemotedPlan, context, mediaSource, profile);
        var putStreamInfo = PlaybackExecutionPlanAdapter.ToStreamInfo(putPlan, context, mediaSource, profile);

        Assert.Equal(undemotedStreamInfo.PlayMethod, putStreamInfo.PlayMethod);
        Assert.Equal(undemotedStreamInfo.Container, putStreamInfo.Container);
        Assert.Equal(undemotedStreamInfo.VideoCodecs, putStreamInfo.VideoCodecs);
        Assert.Equal(undemotedStreamInfo.AudioCodecs, putStreamInfo.AudioCodecs);
        Assert.Equal(undemotedStreamInfo.AudioStreamIndex, putStreamInfo.AudioStreamIndex);
        Assert.Equal(undemotedStreamInfo.MediaSourceId, putStreamInfo.MediaSourceId);
        Assert.Equal(
            undemotedStreamInfo.ToUrl(null, "caller-token", null),
            putStreamInfo.ToUrl(null, "caller-token", null));
    }

    private static MediaSourceInfo CreateDemotionMediaSource() => new()
    {
        Id = DemotionSourceId,
        Container = "mp4",
        SupportsDirectPlay = true,
        MediaStreams = new List<MediaStream>
        {
            new() { Type = MediaStreamType.Video, Index = 0, Codec = "h264" },
            new() { Type = MediaStreamType.Audio, Index = 1, Codec = "aac" },
        },
    };

    /// <summary>
    /// Issue #70, the API-level regression: after a PUT forbidding Direct Play on a still-directly-
    /// playable source replaces the session's authoritative v2 record, GET /Stream must still be
    /// served by v2 - not fall back to legacy with
    /// <see cref="PlaybackLiveFallbackReason.PlanNotExecutable"/>.
    /// </summary>
    [Fact]
    public void GetPlaybackSessionStream_AfterReplaceForbiddingDirectPlay_ServedByV2()
    {
        var engine = new Tesserafin.Playback.Engine.PlaybackEngine();
        var capabilities = CreateDemotionCapabilities();
        var mediaSource = CreateDemotionMediaSource();
        var profile = new DeviceProfile();
        var legacyStreamInfo = new StreamInfo
        {
            ItemId = _itemId,
            MediaSource = mediaSource,
            DeviceProfile = profile,
            // Legacy demoted the same input to Transcode and answered 200 - which is precisely why
            // the PUT succeeded while the v2 record it attached was unexecutable.
            PlayMethod = PlayMethod.Transcode,
            Container = "mp4",
            AudioStreamIndex = 1,
        };
        var options = new MediaOptions { ItemId = _itemId, UserId = _userId, DeviceId = "device-1", Profile = profile };
        var session = new PlaybackSession(
            PlaybackSessionId.NewId(),
            PlaybackMediaKind.Video,
            "play-session-1",
            new PlaybackSessionRequest(PlaybackMediaKind.Video, options),
            new PlaybackPlan(PlayMethod.Transcode, TranscodeReason.DirectPlayError, legacyStreamInfo),
            default,
            default);
        _playbackSessionManager.Setup(m => m.Get(session.Id)).Returns(session);

        var v2PlanStore = new InMemoryV2PlanStore();

        // POST attached an executable Direct Play record ...
        var postDecision = engine.Decide(
            CreateDemotionContext(),
            capabilities,
            [CreateDirectlyPlayableSnapshot()],
            CreateDemotionConstraints(allowDirectPlay: true, allowDirectStream: true));
        PlaybackExecutionPlanBuilder.TryBuild(postDecision, out var postPlan, out _);
        v2PlanStore.Attach(session.Id, new V2PlanRecord(postDecision, postPlan, DateTimeOffset.UtcNow));

        // ... and the PUT replaces it with whatever the retry constraints decide. This is the exact
        // publish shape ShadowPlaybackSessionPlanner uses: the record is attached whether or not the
        // builder produced a plan.
        var putDecision = engine.Decide(
            CreateDemotionContext(),
            capabilities,
            [CreateDirectlyPlayableSnapshot()],
            CreateDemotionConstraints(allowDirectPlay: false, allowDirectStream: false));
        PlaybackExecutionPlanBuilder.TryBuild(putDecision, out var putPlan, out _);
        v2PlanStore.Attach(session.Id, new V2PlanRecord(putDecision, putPlan, DateTimeOffset.UtcNow));

        var configManager = new Mock<IServerConfigurationManager>();
        configManager
            .Setup(c => c.Configuration)
            .Returns(new ServerConfiguration { PlaybackShadow = new PlaybackShadowOptions { Mode = PlaybackEngineMode.V2 } });
        var liveWiringStore = new InMemoryPlaybackLiveWiringDiagnosticsStore();
        var realResolver = new PlaybackLiveStreamResolver(
            configManager.Object,
            new PlaybackExecutionPlanResolver(v2PlanStore),
            liveWiringStore,
            new PlaybackOperationalMetrics(),
            new PlaybackStopThresholdGuard(() => new PlaybackShadowOptions(), new PlaybackOperationalMetrics(), Mock.Of<ILogger<PlaybackStopThresholdGuard>>()),
            Mock.Of<ILogger<PlaybackLiveStreamResolver>>());

        var controller = new PlaybackSessionsController(
            _playbackSessionManager.Object,
            _itemLookupService.Object,
            _userManager.Object,
            _mediaSourceManager.Object,
            v2PlanStore,
            realResolver,
            liveWiringStore,
            _mediaEncoder.Object);
        SetIdentity(controller, _userId, token: "caller-token");

        var result = controller.GetPlaybackSessionStream(session.Id);

        var descriptor = Assert.IsType<PlaybackSessionStreamDescriptor>(Assert.IsAssignableFrom<OkObjectResult>(result.Result).Value);
        Assert.Null(descriptor.FallbackReason);
        Assert.Equal(Tesserafin.Playback.Engine.PlaybackEngine.EngineVersion, descriptor.ServedBy);
        Assert.Equal("mp4", descriptor.Container);
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
    public void GetPlaybackSessionStream_ExistingSession_ResolvesUnderStoredAttemptScope()
    {
        var session = BuildStreamableSession(_userId, "play-session-1") with { PlaybackAttemptId = AttemptId };
        _playbackSessionManager.Setup(m => m.Get(session.Id)).Returns(session);
        var logger = new RecordingLogger();
        var scopeActiveDuringResolve = false;
        _liveStreamResolver
            .Setup(r => r.Resolve(It.IsAny<PlaybackSessionId>(), It.IsAny<StreamInfo>(), It.IsAny<MediaSourceInfo>(), It.IsAny<DeviceProfile>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<bool>()))
            .Callback(() => scopeActiveDuringResolve = logger.HasActiveScopeWithAttemptId(AttemptId))
            .Returns(session.Plan.StreamInfo!);
        PlaybackLiveWiringOutcome? outcome = PlaybackLiveWiringOutcome.Served(DateTimeOffset.UtcNow);
        _liveWiringDiagnosticsStore.Setup(s => s.TryGet(session.Id, out outcome)).Returns(true);

        var result = CreateController(logger).GetPlaybackSessionStream(session.Id);

        Assert.IsAssignableFrom<OkObjectResult>(result.Result);
        Assert.True(scopeActiveDuringResolve);
        Assert.Empty(logger.ActiveScopes);
    }

    /// <summary>
    /// Issue #43: a missing session has no stored attempt id to scope with - the 404 path must not
    /// invent one, so no attempt scope is ever opened.
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
