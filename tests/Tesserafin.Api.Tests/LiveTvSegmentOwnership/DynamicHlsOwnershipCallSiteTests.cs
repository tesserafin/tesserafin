#pragma warning disable CS0618 // tesserafin#129 step 7: the legacy route under test is deprecated, and this test goes with it.
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Api.Auth.HlsJobOwnership;
using Tesserafin.Api.Constants;
using Tesserafin.Api.Controllers;
using Tesserafin.Api.Helpers;
using Tesserafin.Common.Net;
using Tesserafin.Controller;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.Audio;
using Tesserafin.Controller.IO;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Controller.Trickplay;
using Tesserafin.Data.Entities;
using Tesserafin.MediaEncoding.Hls.Playlist;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.Dto;
using Tesserafin.Model.Entities;
using Tesserafin.Model.IO;
using Tesserafin.Model.MediaInfo;
using Xunit;

namespace Tesserafin.Api.Tests.LiveTvSegmentOwnership;

/// <summary>
/// <c>DynamicHlsController</c>'s own authorizer call sites, driven through the real action
/// (#153-LTV-R5, closing R4 finding F2).
/// </summary>
/// <remarks>
/// WHAT F2 SAYS, AND WHAT IT DOES NOT. The R4 ledger states it three times and downgrades it once:
/// "the three dynamic data-plane routes enforce ownership correctly at runtime (R4 Phase 3 measured
/// it); what is missing is *automated coverage* — no test in the repository drives those controller
/// actions." It is a missing proof, never a missing behaviour, so nothing about the decision
/// changes here — only whether a regression in it would be noticed.
///
/// WHAT R4 MEASURED, AND WHY THIS FILE IS THE ANSWER TO IT. Phase 2 rows R5 and R6 applied one
/// mutation — the route's own call site is handed a blank <c>DefaultHttpContext</c> instead of
/// <c>HttpContext</c> — to <c>GetLiveHlsStream</c> and <c>GetDynamicSegment</c>. Both compiled and
/// both left all 69 tests green, because <c>HlsOwnershipMatrixTests.DynamicFamilyVerdict</c> calls
/// <c>authorizer.AuthorizeByOutputPath(...)</c> DIRECTLY and never instantiates the controller. The
/// verdict was covered; the wiring that carries the caller to it was not.
///
/// SO THE ASSERTION IS ABOUT THE WIRING. A recording authorizer answers the action, and the test
/// asserts that the object it was handed is the very <see cref="HttpContext"/> of the request —
/// <c>Assert.Same</c>, not an equivalence — and that it was asked about the path the action would
/// actually serve from. A blank context is a different object, so the mutation reds this directly
/// rather than through a behaviour that happens to correlate with it.
///
/// AND ABOUT THE CONSEQUENCE. Two further rows drive the same action to its outcome: a refused
/// decision must end the request with <see cref="UnauthorizedResult"/> and must never reach the
/// transcode manager, while an authorized one must carry on past the gate. The second is what makes
/// the first non-vacuous — without it, an action that refused everybody would satisfy the refusal
/// row perfectly.
/// </remarks>
public sealed class DynamicHlsOwnershipCallSiteTests : IDisposable
{
    private const string PlaySession = "306f71094f36456f9d0dc6e7b12b8a6b";
    private const string OwnerDevice = "owner-device";
    private const string MediaSourceId = "6d5da76e3955fd1005f75c496c371521";

    private static readonly Guid _itemA = new("11111111111111111111111111111111");
    private static readonly Guid _ownerUserId = new("aaaaaaaa11114444888800000000cccc");

    private readonly string _transcodePath;
    private readonly ServiceProvider _services;

    /// <summary>
    /// Initializes a new instance of the <see cref="DynamicHlsOwnershipCallSiteTests"/> class.
    /// </summary>
    public DynamicHlsOwnershipCallSiteTests()
    {
        _services = new ServiceCollection().AddLogging().AddMvcCore().Services.BuildServiceProvider();
        _transcodePath = Path.Combine(Path.GetTempPath(), "ltvr5-dynamic-callsite-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_transcodePath);
    }

    /// <summary>
    /// The <c>Audio/{itemId}/hls1/{playlistId}/{segmentId}.{container}</c> action asks the
    /// authorizer about THIS request, using the request's own <see cref="HttpContext"/>.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    [Fact]
    public async Task TheDynamicSegmentAction_AsksTheAuthorizerAboutThisRequestsOwnHttpContext()
    {
        var authorizer = new RecordingAuthorizer(HlsJobOwnershipOutcome.Refused);
        var (controller, httpContext) = Controller(authorizer);

        var result = await InvokeAudioSegment(controller).ConfigureAwait(true);

        var asked = Assert.Single(authorizer.Questions);
        Assert.Same(httpContext, asked.Context);
        Assert.False(string.IsNullOrEmpty(asked.OutputPath));
        Assert.Equal(".m3u8", Path.GetExtension(asked.OutputPath));
        Assert.IsType<UnauthorizedResult>(result);
    }

    /// <summary>
    /// A refused decision ends the request, and nothing downstream of the gate is reached.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    [Fact]
    public async Task ARefusedDecision_EndsTheRequestBeforeAnythingIsOpened()
    {
        var authorizer = new RecordingAuthorizer(HlsJobOwnershipOutcome.Refused);
        var (controller, _) = Controller(authorizer, out var beyondTheGate);

        var result = await InvokeAudioSegment(controller).ConfigureAwait(true);

        // The gate was CONSULTED and then nothing downstream happened. Without this line the
        // assertion below would also be satisfied by an action that threw before it ever reached
        // the gate - which is R4 finding F3's vacuity in a new costume.
        Assert.Single(authorizer.Questions);
        Assert.IsType<UnauthorizedResult>(result);
        Assert.False(beyondTheGate.Reached, "a refused caller reached the transcode manager.");
    }

    /// <summary>
    /// Anti-vacuity for the row above: an authorized decision carries on past the gate. Without
    /// this, an action that refused every caller would satisfy the refusal row perfectly.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    [Fact]
    public async Task AnAuthorizedDecision_CarriesOnPastTheGate()
    {
        var authorizer = new RecordingAuthorizer(HlsJobOwnershipOutcome.Authorized);
        var (controller, _) = Controller(authorizer, out var beyondTheGate);

        await Assert.ThrowsAsync<BeyondTheGate.ReachedException>(
            () => InvokeAudioSegment(controller)).ConfigureAwait(true);

        Assert.True(beyondTheGate.Reached);
        Assert.Single(authorizer.Questions);
    }

    /// <summary>
    /// The <c>Videos/{itemId}/live.m3u8</c> action — the OTHER of the two call sites R4 row R5
    /// blinded — asks the authorizer about this request's own <see cref="HttpContext"/>.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    [Fact]
    public async Task TheLivePlaylistAction_AsksTheAuthorizerAboutThisRequestsOwnHttpContext()
    {
        var authorizer = new RecordingAuthorizer(HlsJobOwnershipOutcome.Refused);
        var (controller, httpContext) = Controller(authorizer);

        var result = await InvokeLivePlaylist(controller).ConfigureAwait(true);

        var asked = Assert.Single(authorizer.Questions);
        Assert.Same(httpContext, asked.Context);
        Assert.Equal(".m3u8", Path.GetExtension(asked.OutputPath));
        Assert.IsType<UnauthorizedResult>(result);
    }

    /// <summary>
    /// Anti-vacuity for the live playlist row: an authorized caller carries on past the gate.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    [Fact]
    public async Task TheLivePlaylistAction_CarriesOnPastTheGateWhenAuthorized()
    {
        var authorizer = new RecordingAuthorizer(HlsJobOwnershipOutcome.Authorized);
        var (controller, _) = Controller(authorizer, out var beyondTheGate);

        await Assert.ThrowsAsync<BeyondTheGate.ReachedException>(
            () => InvokeLivePlaylist(controller)).ConfigureAwait(true);

        Assert.True(beyondTheGate.Reached);
        Assert.Single(authorizer.Questions);
    }

    private static ClaimsPrincipal Owner()
    {
        var identity = new ClaimsIdentity("CustomAuthentication");
        identity.AddClaim(new Claim(InternalClaimTypes.UserId, _ownerUserId.ToString("N")));
        identity.AddClaim(new Claim(ClaimTypes.Name, _ownerUserId.ToString("N")));
        identity.AddClaim(new Claim(InternalClaimTypes.DeviceId, OwnerDevice));
        return new ClaimsPrincipal(identity);
    }

    private static Task<ActionResult> InvokeAudioSegment(DynamicHlsController controller)
        => controller.GetHlsAudioSegment(
            _itemA,
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            0,
            "aac",
            TimeSpan.FromMinutes(1).Ticks,
            TimeSpan.FromSeconds(6).Ticks,
            null,
            null,
            null,
            null,
            PlaySession,
            "aac",
            6,
            1,
            MediaSourceId,
            OwnerDevice,
            "aac",
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            new Dictionary<string, string>());

    private static Task<ActionResult> InvokeLivePlaylist(DynamicHlsController controller)
        => controller.GetLiveHlsStream(
            _itemA,
            "ts",
            null,
            null,
            null,
            null,
            PlaySession,
            "ts",
            6,
            1,
            MediaSourceId,
            OwnerDevice,
            "aac",
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            "live-stream-id",
            null,
            "h264",
            null,
            null,
            null,
            null,
            null,
            new Dictionary<string, string>(),
            null,
            null,
            null);

    private (DynamicHlsController Controller, HttpContext HttpContext) Controller(RecordingAuthorizer authorizer)
        => Controller(authorizer, out _);

    private (DynamicHlsController Controller, HttpContext HttpContext) Controller(RecordingAuthorizer authorizer, out BeyondTheGate beyondTheGate)
    {
        var appPaths = Mock.Of<IServerApplicationPaths>(p => p.CachePath == _transcodePath);
        var configurationManager = new Mock<IServerConfigurationManager>();
        configurationManager
            .Setup(c => c.GetConfiguration("encoding"))
            .Returns(new EncodingOptions { TranscodingTempPath = _transcodePath });
        configurationManager.SetupGet(c => c.CommonApplicationPaths).Returns(appPaths);

        var item = new Audio { Id = _itemA, Path = Path.Combine(_transcodePath, "source.mp3") };
        var libraryManager = new Mock<ILibraryManager>();
        libraryManager.Setup(l => l.GetItemById<BaseItem>(It.IsAny<Guid>())).Returns(item);
        libraryManager.Setup(l => l.GetItemById<BaseItem>(It.IsAny<Guid>(), It.IsAny<User>())).Returns(item);
        libraryManager.Setup(l => l.GetItemById<BaseItem>(It.IsAny<Guid>(), It.IsAny<Guid>())).Returns(item);

        var mediaSource = new MediaSourceInfo
        {
            Id = MediaSourceId,
            Path = item.Path,
            Protocol = MediaProtocol.File,
            Container = "mp3",
            RunTimeTicks = TimeSpan.FromMinutes(1).Ticks,
            MediaStreams = new List<MediaStream>
            {
                new() { Type = MediaStreamType.Audio, Index = 0, Codec = "mp3", Channels = 2, BitRate = 128000 }
            }
        };
        var mediaSourceManager = new Mock<IMediaSourceManager>();
        mediaSourceManager
            .Setup(m => m.GetPlaybackMediaSources(It.IsAny<BaseItem>(), It.IsAny<Tesserafin.Data.Entities.User>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MediaSourceInfo> { mediaSource });

        // `Videos/{itemId}/live.m3u8` names a live stream, which is resolved through this member
        // rather than through the playback media sources above.
        mediaSourceManager
            .Setup(m => m.GetLiveStreamWithDirectStreamProvider(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Tuple<MediaSourceInfo, IDirectStreamProvider>(mediaSource, Mock.Of<IDirectStreamProvider>()));

        var mediaEncoder = new Mock<IMediaEncoder>();
        mediaEncoder.Setup(e => e.CanEncodeToAudioCodec(It.IsAny<string>())).Returns(true);
        mediaEncoder.SetupGet(e => e.EncoderVersion).Returns(new Version(6, 0));

        var gate = new BeyondTheGate();
        beyondTheGate = gate;

        var transcodeManager = new Mock<ITranscodeManager>();
        transcodeManager
            .Setup(t => t.LockAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>((_, _) => throw gate.Reach());
        transcodeManager
            .Setup(t => t.OnTranscodeBeginRequest(It.IsAny<string>(), It.IsAny<TranscodingJobType>()))
            .Returns((TranscodingJob?)null);

        var encodingHelper = new EncodingHelper(
            appPaths,
            mediaEncoder.Object,
            Mock.Of<ISubtitleEncoder>(),
            Mock.Of<IConfiguration>(),
            configurationManager.Object,
            Mock.Of<IPathManager>());

        var httpContext = new DefaultHttpContext
        {
            Request = { Path = new PathString($"/Audio/{_itemA:N}/hls1/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/0.aac") },
            User = Owner(),
            RequestServices = _services
        };

        var helper = new DynamicHlsHelper(
            libraryManager.Object,
            TestHelpers.AnyUser(),
            mediaSourceManager.Object,
            configurationManager.Object,
            mediaEncoder.Object,
            transcodeManager.Object,
            Mock.Of<INetworkManager>(),
            NullLogger<DynamicHlsHelper>.Instance,
            Mock.Of<IHttpContextAccessor>(),
            encodingHelper,
            Mock.Of<ITrickplayManager>());

        var controller = new DynamicHlsController(
            libraryManager.Object,
            TestHelpers.AnyUser(),
            mediaSourceManager.Object,
            configurationManager.Object,
            mediaEncoder.Object,
            Mock.Of<IFileSystem>(),
            transcodeManager.Object,
            NullLogger<DynamicHlsController>.Instance,
            helper,
            encodingHelper,
            Mock.Of<IDynamicHlsPlaylistGenerator>(),
            Mock.Of<IPlaybackSessionManager>(),
            authorizer)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };

        return (controller, httpContext);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _services.Dispose();

        try
        {
            Directory.Delete(_transcodePath, true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not a test failure.
        }
    }

    /// <summary>
    /// A marker for "the action got past the ownership gate". The first thing
    /// <c>GetDynamicSegment</c> does after the gate that touches an injected dependency is take the
    /// transcode manager's lock, so reaching that is the observable consequence of not being
    /// refused.
    /// </summary>
    private sealed class BeyondTheGate
    {
        public bool Reached { get; private set; }

        public ReachedException Reach()
        {
            Reached = true;
            return new ReachedException();
        }

        public sealed class ReachedException : Exception
        {
        }
    }

    private sealed class RecordingAuthorizer : IHlsJobOwnershipAuthorizer
    {
        private readonly HlsJobOwnershipOutcome _outcome;

        public RecordingAuthorizer(HlsJobOwnershipOutcome outcome) => _outcome = outcome;

        public List<(HttpContext Context, string OutputPath)> Questions { get; } = new();

        public HlsJobOwnershipDecision AuthorizeByPlaylistId(HttpContext context, string playlistId)
            => HlsJobOwnershipDecision.NoSuchJob();

        public HlsJobOwnershipDecision AuthorizeBySegmentName(HttpContext context, string segmentName)
            => HlsJobOwnershipDecision.NoSuchJob();

        public HlsJobOwnershipDecision AuthorizeProgressiveByOutputPath(HttpContext context, string outputPath)
            => throw new NotSupportedException("The HLS routes never ask about a progressive output.");

        public HlsJobOwnershipDecision AuthorizeByOutputPath(HttpContext context, string outputPath)
        {
            Questions.Add((context, outputPath));
            return _outcome switch
            {
                HlsJobOwnershipOutcome.Refused => HlsJobOwnershipDecision.Refused(),
                HlsJobOwnershipOutcome.Authorized => HlsJobOwnershipDecision.Authorized(
                    new HlsSegmentBinding(
                        Path.GetFileNameWithoutExtension(outputPath),
                        _ownerUserId,
                        OwnerDevice,
                        _itemA,
                        MediaSourceId,
                        PlaySession,
                        Path.GetDirectoryName(Path.GetFullPath(outputPath))!,
                        Path.GetFullPath(outputPath),
                        1)),
                _ => HlsJobOwnershipDecision.NoSuchJob()
            };
        }

        public bool OwnsJob(HttpContext context, Guid ownerUserId, string? ownerDeviceId) => false;
    }
}
