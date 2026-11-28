using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Api.Auth.HlsJobOwnership;
using Tesserafin.Api.Auth.PlaybackCapabilityPolicy;
using Tesserafin.Api.Constants;
using Tesserafin.Api.Controllers;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Controller.Net.PlaybackCredentials;
using Tesserafin.Controller.Session;
using Xunit;

namespace Tesserafin.Api.Tests.LiveTvSegmentOwnership;

/// <summary>
/// <c>DELETE Videos/ActiveEncodings</c> must end only the caller's OWN transcode (#153-LTV-R5,
/// finding F1).
/// </summary>
/// <remarks>
/// WHY THIS FILE EXISTS. R4 Phase 2 measured that <c>IHlsJobOwnershipAuthorizer.OwnsJob</c> — the
/// only member this route uses, and the only member no byte-serving route uses — could be neutered
/// to <c>return true</c>, or its call site rewritten to <c>if (job is null)</c>, and the entire
/// <c>LiveTvSegmentOwnership</c> suite stayed at 69/69 green. For one of the twelve job-backed
/// routes the authorizer was exactly what the mission forbids: mentioned in a registry and asserted
/// nowhere.
///
/// WHY THE STATUS CODE PROVES NOTHING HERE, AND WHAT IS ASSERTED INSTEAD. This route answers
/// <c>204 No Content</c> to every caller by design: a job the caller does not own answers exactly
/// as one that never existed, so that the route cannot be used as a probe for which play sessions
/// are live. A test that asserted the status code would therefore pass with the authorization
/// removed. What separates authorized from refused is whether the transcode was actually ENDED —
/// so every row below asserts the 204 AND the recorded effect: whether
/// <c>KillTranscodingJobs</c> was called, and with which arguments.
///
/// WHAT <c>OwnsJob</c> COMPARES, STATED PLAINLY. It compares identity only: the caller's user id
/// and the caller's device, resolved exactly as the job's own owner was recorded. It receives no
/// binding, because this route has no item, no media source and no play-session binding to compare
/// against — the job is selected from server state by the caller-named <c>playSessionId</c>, and
/// what the authorizer then decides is whether this caller is that job's owner. The capability rows
/// below are therefore contradictions of IDENTITY (another user, another device's session, a
/// capability nothing validated), which are the contradictions this route can see.
/// </remarks>
public sealed class ActiveEncodingsOwnershipTests : IDisposable
{
    private const string OwnerDevice = "owner-device";
    private const string OwnerSession = "owner-session";
    private const string ForeignSession = "a-session-on-another-device";
    private const string JobPlaySession = "306f71094f36456f9d0dc6e7b12b8a6b";
    private const string JobMediaSource = "6d5da76e3955fd1005f75c496c371521";
    private const string RequestedDeviceId = "the-device-named-in-the-query";

    private static readonly Guid _itemA = new("11111111111111111111111111111111");
    private static readonly Guid _itemB = new("22222222222222222222222222222222");
    private static readonly Guid _ownerUserId = new("aaaaaaaa11114444888800000000cccc");
    private static readonly Guid _strangerUserId = new("dddddddd2222555599990000eeeeffff");

    private readonly ServiceProvider _services;

    /// <summary>
    /// Initializes a new instance of the <see cref="ActiveEncodingsOwnershipTests"/> class.
    /// </summary>
    public ActiveEncodingsOwnershipTests()
    {
        // A real MVC service provider, so the 204 every row asserts is written by the framework's
        // own executor rather than by an imitation of it.
        _services = new ServiceCollection()
            .AddLogging()
            .AddMvcCore()
            .Services
            .BuildServiceProvider();
    }

    /// <summary>
    /// A caller shape asking to stop the owner's transcode.
    /// </summary>
    public enum Caller
    {
        /// <summary>The job's own user, on the job's own device, holding a durable token.</summary>
        DurableOwner,

        /// <summary>A different authenticated user, on a different device.</summary>
        AnotherUser,

        /// <summary>A different user whose token names the job's OWN device.</summary>
        AnotherUserOnTheOwnersDevice,

        /// <summary>The job's user, on a device the job was not started from.</summary>
        AnotherDevice,

        /// <summary>An administrator who does not own the job.</summary>
        AdministratorWhoIsNotTheOwner,

        /// <summary>An api key, which resolves to no user at all.</summary>
        ApiKeyWithNoUser,

        /// <summary>No credential of any kind.</summary>
        NoCredentialAtAll,

        /// <summary>A capability of the job's user, minted on the job's own device session.</summary>
        ExactCapability,

        /// <summary>A validated capability belonging to a DIFFERENT user.</summary>
        CapabilityOfAnotherUser,

        /// <summary>A capability of the right user, minted for a session on another device.</summary>
        CapabilityFromAnotherDevicesSession,

        /// <summary>A capability presented in the query that nothing ever validated.</summary>
        CapabilityWithNoValidatedProvenance
    }

    /// <summary>
    /// The rows: who is asking, and whether the mission allows them to end this transcode.
    /// </summary>
    /// <returns>The rows.</returns>
    public static IEnumerable<object[]> Callers() => new[]
    {
        new object[] { Caller.DurableOwner, true },
        new object[] { Caller.AnotherUser, false },
        new object[] { Caller.AnotherUserOnTheOwnersDevice, false },
        new object[] { Caller.AnotherDevice, false },
        new object[] { Caller.AdministratorWhoIsNotTheOwner, false },
        new object[] { Caller.ApiKeyWithNoUser, false },
        new object[] { Caller.NoCredentialAtAll, false },
        new object[] { Caller.ExactCapability, true },
        new object[] { Caller.CapabilityOfAnotherUser, false },
        new object[] { Caller.CapabilityFromAnotherDevicesSession, false },
        new object[] { Caller.CapabilityWithNoValidatedProvenance, false }
    };

    /// <summary>
    /// The whole matrix, driven through the real route and asserted on the recorded effect rather
    /// than on the status code, which is 204 for everyone.
    /// </summary>
    /// <param name="caller">The caller shape.</param>
    /// <param name="mayEndTheJob">Whether the mission allows this caller to end the transcode.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    [Theory]
    [MemberData(nameof(Callers))]
    public async Task ActiveEncodingsFamily(Caller caller, bool mayEndTheJob)
    {
        var kills = new List<(string DeviceId, string? PlaySessionId)>();
        var httpContext = Context(caller);
        var controller = Controller(httpContext, kills, jobIsGone: false, out var asked);

        var result = await controller.StopEncodingProcess(RequestedDeviceId, JobPlaySession);

        // The real authorizer decided, and it was asked exactly once. Without this, every refusal
        // row would also be satisfied by an action that never consulted anything - which is the
        // shape of the vacuity R4 found in the audio tests.
        Assert.Equal(1, asked.Count);

        // Every caller gets the same answer. This assertion is here to make the point explicit:
        // the status code cannot be the evidence, because it does not vary.
        Assert.IsType<NoContentResult>(result);
        await Execute(result, httpContext).ConfigureAwait(true);
        Assert.Equal(StatusCodes.Status204NoContent, httpContext.Response.StatusCode);

        if (mayEndTheJob)
        {
            var kill = Assert.Single(kills);
            Assert.Equal(RequestedDeviceId, kill.DeviceId);
            Assert.Equal(JobPlaySession, kill.PlaySessionId);
        }
        else
        {
            Assert.Empty(kills);
        }
    }

    /// <summary>
    /// A play session no live job answers to is refused for everyone, its own owner included, and
    /// nothing is killed.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    [Fact]
    public async Task WhenTheJobIsGone_NothingIsKilledAndEvenItsOwnerIsRefused()
    {
        var kills = new List<(string DeviceId, string? PlaySessionId)>();
        var httpContext = Context(Caller.DurableOwner);
        var controller = Controller(httpContext, kills, jobIsGone: true);

        var result = await controller.StopEncodingProcess(RequestedDeviceId, JobPlaySession);

        Assert.IsType<NoContentResult>(result);
        await Execute(result, httpContext).ConfigureAwait(true);
        Assert.Equal(StatusCodes.Status204NoContent, httpContext.Response.StatusCode);
        Assert.Empty(kills);
    }

    /// <summary>
    /// Anti-vacuity, stated as one assertion rather than left implicit across the matrix: the SAME
    /// fixture, the SAME job and the SAME arguments produce a kill for the owner and no kill for a
    /// stranger.
    /// </summary>
    /// <remarks>
    /// Without this the eleven rows above could all be green because the harness never kills
    /// anything — a fixture defect that reads exactly like a boundary. It is the F3 vacuity in a
    /// different costume, so it is pinned here explicitly.
    /// </remarks>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    [Fact]
    public async Task TheOwnerIsKilledAndAStrangerIsNot_FromTheSameFixture()
    {
        var ownerKills = new List<(string DeviceId, string? PlaySessionId)>();
        var strangerKills = new List<(string DeviceId, string? PlaySessionId)>();

        await Controller(Context(Caller.DurableOwner), ownerKills, jobIsGone: false)
            .StopEncodingProcess(RequestedDeviceId, JobPlaySession);
        await Controller(Context(Caller.AnotherUser), strangerKills, jobIsGone: false)
            .StopEncodingProcess(RequestedDeviceId, JobPlaySession);

        Assert.Single(ownerKills);
        Assert.Empty(strangerKills);
    }

    /// <summary>
    /// The route consults the authorizer at all. A recording authorizer proves the call happens
    /// with the request's OWN <see cref="HttpContext"/> and with the JOB's owner — not with the
    /// caller-supplied query parameters, which is what the route used to act on.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    [Fact]
    public async Task TheRouteAsksTheAuthorizerAboutTheJobsOwner_NotAboutTheQuery()
    {
        var httpContext = Context(Caller.DurableOwner);
        var authorizer = new RecordingAuthorizer(answer: true);
        var controller = new HlsSegmentController(TranscodeManager(new List<(string, string?)>(), jobIsGone: false).Object, authorizer)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };

        await controller.StopEncodingProcess(RequestedDeviceId, JobPlaySession);

        var asked = Assert.Single(authorizer.Questions);
        Assert.Same(httpContext, asked.Context);
        Assert.Equal(_ownerUserId, asked.OwnerUserId);
        Assert.Equal(OwnerDevice, asked.OwnerDeviceId);
        Assert.NotEqual(RequestedDeviceId, asked.OwnerDeviceId);
    }

    private static Task Execute(ActionResult result, HttpContext httpContext)
        => result.ExecuteResultAsync(new ActionContext { HttpContext = httpContext });

    private static Mock<ISessionManager> SessionManager()
    {
        var sessionManager = new Mock<ISessionManager>(MockBehavior.Loose);
        var owner = new SessionInfo(sessionManager.Object, NullLogger.Instance)
        {
            Id = OwnerSession,
            DeviceId = OwnerDevice,
            UserId = _ownerUserId
        };
        var foreign = new SessionInfo(sessionManager.Object, NullLogger.Instance)
        {
            Id = ForeignSession,
            DeviceId = "a-device-that-is-not-the-owners",
            UserId = _ownerUserId
        };
        sessionManager.SetupGet(m => m.Sessions).Returns(new[] { owner, foreign });
        return sessionManager;
    }

    private static Mock<ITranscodeManager> TranscodeManager(List<(string DeviceId, string? PlaySessionId)> kills, bool jobIsGone)
    {
        var job = new TranscodingJob(NullLogger<TranscodingJob>.Instance)
        {
            Type = TranscodingJobType.Hls,
            Path = Path.Combine(Path.GetTempPath(), "ltvr5-active-encodings", "aaaa.m3u8"),
            PlaySessionId = JobPlaySession,
            ItemId = _itemA,
            MediaSourceId = JobMediaSource,

            // The two values the route's authorization is decided against. Neither is a query
            // parameter: `UserId` is the validated principal of the request that started the
            // transcode, `OwnerDeviceId` is that token's own device claim. `DeviceId` below IS the
            // query parameter, and it is deliberately a DIFFERENT string, so a test that passed by
            // comparing the query against itself could not.
            UserId = _ownerUserId,
            OwnerDeviceId = OwnerDevice,
            DeviceId = RequestedDeviceId,
            Generation = 1
        };

        // POLISH-2-R4. The route no longer looks one job up and then names a play session to
        // kill: it hands the manager the question to put to EACH job. This stand-in puts it to the
        // one job there is, and records a kill only when the answer was yes.
        var transcodeManager = new Mock<ITranscodeManager>(MockBehavior.Loose);
        transcodeManager
            .As<ITranscodeOwnedStop>()
            .Setup(t => t.StopTranscodingJobs(It.IsAny<string>(), It.IsAny<Func<TranscodingJob, bool>>()))
            .Callback((string playSessionId, Func<TranscodingJob, bool> isCallers) =>
            {
                if (!jobIsGone && string.Equals(playSessionId, JobPlaySession, StringComparison.Ordinal) && isCallers(job))
                {
                    kills.Add((job.DeviceId!, playSessionId));
                }
            })
            .Returns(Task.CompletedTask);
        return transcodeManager;
    }

    private static ClaimsPrincipal PrincipalFor(Caller caller)
    {
        if (caller == Caller.NoCredentialAtAll)
        {
            return new ClaimsPrincipal(new ClaimsIdentity());
        }

        var identity = new ClaimsIdentity("CustomAuthentication");

        var userId = caller switch
        {
            Caller.AnotherUser => _strangerUserId,
            Caller.AnotherUserOnTheOwnersDevice => _strangerUserId,
            Caller.AdministratorWhoIsNotTheOwner => _strangerUserId,
            Caller.ApiKeyWithNoUser => Guid.Empty,
            _ => _ownerUserId
        };

        if (!userId.Equals(Guid.Empty))
        {
            identity.AddClaim(new Claim(InternalClaimTypes.UserId, userId.ToString("N")));
            identity.AddClaim(new Claim(ClaimTypes.Name, userId.ToString("N")));
        }

        // A capability principal carries no device claim; the handler does not mint one, and the
        // device is resolved through the capability's session instead.
        var deviceId = caller switch
        {
            Caller.AnotherUser => "stranger-device",
            Caller.AnotherUserOnTheOwnersDevice => OwnerDevice,
            Caller.AnotherDevice => "some-other-device",
            Caller.AdministratorWhoIsNotTheOwner => "admin-device",
            Caller.ApiKeyWithNoUser => null,
            _ when CapabilityFor(caller) is not null => null,
            _ => OwnerDevice
        };

        if (deviceId is not null)
        {
            identity.AddClaim(new Claim(InternalClaimTypes.DeviceId, deviceId));
        }

        if (caller == Caller.ApiKeyWithNoUser)
        {
            identity.AddClaim(new Claim(InternalClaimTypes.IsApiKey, bool.TrueString));
        }

        if (caller == Caller.AdministratorWhoIsNotTheOwner)
        {
            identity.AddClaim(new Claim(ClaimTypes.Role, UserRoles.Administrator));
        }

        return new ClaimsPrincipal(identity);
    }

    private static ValidatedPlaybackCapability? CapabilityFor(Caller caller) => caller switch
    {
        Caller.ExactCapability => Capability(_itemA, _ownerUserId, OwnerSession),
        Caller.CapabilityOfAnotherUser => Capability(_itemB, _strangerUserId, ForeignSession),
        Caller.CapabilityFromAnotherDevicesSession => Capability(_itemA, _ownerUserId, ForeignSession),
        _ => null
    };

    private static ValidatedPlaybackCapability Capability(Guid itemId, Guid userId, string sessionId)
        => new(
            "capability-value",
            Guid.NewGuid(),
            itemId,
            JobMediaSource,
            JobPlaySession,
            PlaybackCapabilityScope.Media,
            new PlaybackCapabilityValidation(true, PlaybackCapabilityFailure.None, Guid.NewGuid(), userId, sessionId, JobPlaySession));

    private DefaultHttpContext Context(Caller caller)
    {
        var httpContext = new DefaultHttpContext
        {
            Request =
            {
                Path = new PathString("/Videos/ActiveEncodings"),
                Method = HttpMethods.Delete
            },
            User = PrincipalFor(caller),
            RequestServices = _services
        };

        httpContext.Request.QueryString = QueryString.Create(new Dictionary<string, string?>
        {
            ["deviceId"] = RequestedDeviceId,
            ["playSessionId"] = JobPlaySession
        });

        if (caller == Caller.CapabilityWithNoValidatedProvenance)
        {
            // Presented and never validated. `PlaybackCapabilityProvenance.Resolve` refuses this
            // outright, which is the one contradiction visible to a route that carries no
            // capability demand of its own.
            httpContext.Request.QueryString = QueryString.Create(new Dictionary<string, string?>
            {
                ["deviceId"] = RequestedDeviceId,
                ["playSessionId"] = JobPlaySession,
                [PlaybackCapabilityAuthenticationHandler.QueryKey] = "a-capability-nothing-validated"
            });
        }

        var capability = CapabilityFor(caller);
        if (capability is not null)
        {
            httpContext.Features.Set(capability);
        }

        return httpContext;
    }

    private HlsSegmentController Controller(HttpContext httpContext, List<(string DeviceId, string? PlaySessionId)> kills, bool jobIsGone)
        => Controller(httpContext, kills, jobIsGone, out _);

    private HlsSegmentController Controller(
        HttpContext httpContext,
        List<(string DeviceId, string? PlaySessionId)> kills,
        bool jobIsGone,
        out CountingAuthorizer asked)
    {
        // The REAL authorizer, wrapped only to COUNT the questions put to it. Substituting the
        // decision would make every assertion here a statement about a mock rather than about the
        // boundary; counting around it changes no answer.
        asked = new CountingAuthorizer(new HlsJobOwnershipAuthorizer(
            Mock.Of<IHlsSegmentBindingRegistry>(MockBehavior.Loose),
            SessionManager().Object));

        return new HlsSegmentController(TranscodeManager(kills, jobIsGone).Object, asked)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };
    }

    /// <inheritdoc />
    public void Dispose() => _services.Dispose();

    /// <summary>
    /// The real authorizer, with a counter around <c>OwnsJob</c>. It answers exactly what the real
    /// one answers; all it adds is the ability to assert that the route asked at all.
    /// </summary>
    private sealed class CountingAuthorizer : IHlsJobOwnershipAuthorizer
    {
        private readonly IHlsJobOwnershipAuthorizer _inner;

        public CountingAuthorizer(IHlsJobOwnershipAuthorizer inner) => _inner = inner;

        public int Count { get; private set; }

        public HlsJobOwnershipDecision AuthorizeByPlaylistId(HttpContext context, string playlistId)
            => _inner.AuthorizeByPlaylistId(context, playlistId);

        public HlsJobOwnershipDecision AuthorizeByOutputPath(HttpContext context, string outputPath)
            => _inner.AuthorizeByOutputPath(context, outputPath);

        public HlsJobOwnershipDecision AuthorizeProgressiveByOutputPath(HttpContext context, string outputPath)
            => _inner.AuthorizeProgressiveByOutputPath(context, outputPath);

        public bool OwnsJob(HttpContext context, Guid ownerUserId, string? ownerDeviceId)
        {
            Count++;
            return _inner.OwnsJob(context, ownerUserId, ownerDeviceId);
        }
    }

    private sealed class RecordingAuthorizer : IHlsJobOwnershipAuthorizer
    {
        private readonly bool _answer;

        public RecordingAuthorizer(bool answer) => _answer = answer;

        public List<(HttpContext Context, Guid OwnerUserId, string? OwnerDeviceId)> Questions { get; } = new();

        public HlsJobOwnershipDecision AuthorizeByPlaylistId(HttpContext context, string playlistId)
            => HlsJobOwnershipDecision.NoSuchJob();

        public HlsJobOwnershipDecision AuthorizeByOutputPath(HttpContext context, string outputPath)
            => HlsJobOwnershipDecision.NoSuchJob();

        public HlsJobOwnershipDecision AuthorizeProgressiveByOutputPath(HttpContext context, string outputPath)
            => HlsJobOwnershipDecision.NoSuchJob();

        public bool OwnsJob(HttpContext context, Guid ownerUserId, string? ownerDeviceId)
        {
            Questions.Add((context, ownerUserId, ownerDeviceId));
            return _answer;
        }
    }
}
