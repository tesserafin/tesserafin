#pragma warning disable CS0618 // tesserafin#129 step 7: the legacy route under test is deprecated, and this test goes with it.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Api.Attributes;
using Tesserafin.Api.Auth.HlsJobOwnership;
using Tesserafin.Api.Auth.PlaybackCapabilityPolicy;
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
using Tesserafin.Controller.Net.PlaybackCredentials;
using Tesserafin.Controller.Session;
using Tesserafin.Controller.Trickplay;
using Tesserafin.Data.Entities;
using Tesserafin.MediaEncoding.Hls.Playlist;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.Dto;
using Tesserafin.Model.Entities;
using Tesserafin.Model.IO;
using Tesserafin.Model.MediaInfo;
using Tesserafin.Model.Session;
using Xunit;

namespace Tesserafin.Api.Tests.LiveTvSegmentOwnership;

/// <summary>
/// The <c>startTimeTicks</c> boundary on the two dynamic HLS segment routes, and the ownership
/// decision that has to stay unconditional behind it.
/// </summary>
/// <remarks>
/// WHAT THIS FILE EXISTS FOR. <c>GetDynamicSegment</c> used to refuse a caller-supplied
/// <c>StartTimeTicks</c> with a guard of its own, standing in front of
/// <c>IHlsJobOwnershipAuthorizer.AuthorizeByOutputPath</c>. The refusal was correct and the guard
/// threw, but the SHAPE was a query parameter deciding whether an authorization call happens —
/// which is what CodeQL <c>cs/user-controlled-bypass</c> reports and, more to the point, is a
/// property nobody should have to re-derive by reading the method. The refusal now lives on
/// <see cref="RejectsStartTimeTicksAttribute"/> at the MVC boundary and the authorizer call is the
/// action's first decision, unconditionally.
///
/// SO THERE ARE THREE CLAIMS, AND EACH IS MEASURED SEPARATELY.
/// <list type="number">
/// <item>The boundary is WHERE IT IS CLAIMED TO BE — a property of the route table, asserted by
/// reading the attributes the framework dispatches on. A request-level test can only sample it.</item>
/// <item>The boundary REFUSES, deterministically and with zero bytes, and a refused request never
/// reaches the action at all — so no streaming state is resolved, no binding is looked up, no
/// output path is named and no transcoding job is attached, created or killed. The anti-vacuity
/// control is the allowed value, which does all of those things.</item>
/// <item>The ownership matrix is UNCHANGED by the new boundary. Every row is driven through the
/// real action with an allowed <c>startTimeTicks</c> actually present in the request, against the
/// real <see cref="HlsJobOwnershipAuthorizer"/>, so "the refusal moved" cannot quietly mean "the
/// ownership decision moved with it".</item>
/// </list>
///
/// WHY A 400 AND NOT AN EXCEPTION. The guard threw <c>ArgumentException</c>, which
/// <c>ExceptionMiddleware</c> maps to 400 with a body. The filter answers 400 with no body. The
/// status a caller observes is the same; the body is now empty, which is what
/// <c>AForbiddenValue_IsRefusedWithZeroBytes</c> pins.
/// </remarks>
public sealed class DynamicHlsStartTimeTicksBoundaryTests : IDisposable
{
    private const string PlaylistId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string PlaySession = "306f71094f36456f9d0dc6e7b12b8a6b";
    private const string OwnerDevice = "owner-device";
    private const string MediaSourceId = "6d5da76e3955fd1005f75c496c371521";
    private const string ParameterName = "startTimeTicks";

    private static readonly Guid _itemA = new("11111111111111111111111111111111");
    private static readonly Guid _itemB = new("22222222222222222222222222222222");
    private static readonly Guid _ownerUserId = new("aaaaaaaa11114444888800000000cccc");
    private static readonly Guid _strangerUserId = new("dddddddd2222555599990000eeeeffff");

    private readonly string _transcodePath;
    private readonly ServiceProvider _services;

    /// <summary>
    /// Initializes a new instance of the <see cref="DynamicHlsStartTimeTicksBoundaryTests"/> class.
    /// </summary>
    public DynamicHlsStartTimeTicksBoundaryTests()
    {
        _services = new ServiceCollection().AddLogging().AddMvcCore().Services.BuildServiceProvider();
        _transcodePath = Path.Combine(Path.GetTempPath(), "ltvcql-starttime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_transcodePath);
    }

    /// <summary>The identities the ownership matrix is driven with, through the new boundary.</summary>
    public enum Caller
    {
        /// <summary>The durable-token principal that started the job.</summary>
        Owner,

        /// <summary>A different authenticated user, guessing the url.</summary>
        OtherUser,

        /// <summary>The same user on a different device.</summary>
        OtherDevice,

        /// <summary>A capability that is valid, but bound to a different item.</summary>
        ForeignCapability,

        /// <summary>No credential at all.</summary>
        Anonymous
    }

    /// <summary>Gets the two actions the boundary is declared on, and the route family of each.</summary>
    public static TheoryData<string> DecoratedActions { get; } = new()
    {
        nameof(DynamicHlsController.GetHlsVideoSegment),
        nameof(DynamicHlsController.GetHlsAudioSegment)
    };

    /// <summary>Gets the values this route cannot honour and must refuse.</summary>
    public static TheoryData<long> ForbiddenValues { get; } = new()
    {
        1L,
        TimeSpan.FromSeconds(30).Ticks,
        -1L,
        long.MaxValue,
        long.MinValue
    };

    /// <summary>Gets the ownership rows and whether each is the job's owner.</summary>
    public static TheoryData<Caller, bool> OwnershipRows { get; } = new()
    {
        { Caller.Owner, true },
        { Caller.OtherUser, false },
        { Caller.OtherDevice, false },
        { Caller.ForeignCapability, false },
        { Caller.Anonymous, false }
    };

    /// <summary>
    /// CLAIM 1. Exactly the two segment actions carry the boundary, and no other action on the
    /// controller does — the master and variant playlist routes still accept a start offset,
    /// which is the behaviour this repair must not have widened.
    /// </summary>
    [Fact]
    public void ExactlyTheTwoSegmentActions_CarryTheBoundary()
    {
        var decorated = typeof(DynamicHlsController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttribute<RejectsStartTimeTicksAttribute>() is not null)
            .Select(m => m.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[] { nameof(DynamicHlsController.GetHlsAudioSegment), nameof(DynamicHlsController.GetHlsVideoSegment) },
            decorated);
    }

    /// <summary>
    /// CLAIM 1, anti-rename. The attribute addresses the parameter by name, the way MVC itself
    /// addresses a bound argument. A rename that left the attribute behind would make it examine a
    /// parameter that no longer exists and refuse nothing, so the name is asserted to resolve — to
    /// a parameter that is actually a nullable <see cref="long"/>, because a differently typed one
    /// would never match the filter's unboxing either.
    /// </summary>
    /// <param name="actionName">The decorated action.</param>
    [Theory]
    [MemberData(nameof(DecoratedActions))]
    public void TheDeclaredParameterName_ResolvesToANullableLongOnTheAction(string actionName)
    {
        var method = typeof(DynamicHlsController).GetMethod(actionName)!;
        var declared = method.GetCustomAttribute<RejectsStartTimeTicksAttribute>()!.ParameterName;

        var parameter = Assert.Single(
            method.GetParameters(),
            p => string.Equals(p.Name, declared, StringComparison.Ordinal));
        Assert.Equal(typeof(long?), parameter.ParameterType);
    }

    /// <summary>
    /// CLAIM 1, fail-closed. The filter throws rather than passing when the parameter it was told
    /// to examine is not on the action. Without this it would be silently inert, and inert is
    /// indistinguishable from green.
    /// </summary>
    [Fact]
    public void TheFilter_ThrowsWhenTheNamedParameterIsNotOnTheAction()
    {
        var filter = new RejectsStartTimeTicksAttribute(ParameterName);
        var context = FilterContext(
            typeof(DynamicHlsStartTimeTicksBoundaryTests).GetMethod(
                nameof(AnActionThatTakesNoStartOffset),
                BindingFlags.NonPublic | BindingFlags.Static)!,
            new DefaultHttpContext { RequestServices = _services },
            arguments: new Dictionary<string, object?>(StringComparer.Ordinal));

        var thrown = Assert.Throws<InvalidOperationException>(() => filter.OnActionExecuting(context));
        Assert.Contains(ParameterName, thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// CLAIM 2. A forbidden value is refused at the boundary with a deterministic 400 and an empty
    /// body — not a 404, not an exception escaping to the middleware, not a file that happens not
    /// to exist.
    /// </summary>
    /// <param name="ticks">The forbidden value.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    [Theory]
    [MemberData(nameof(ForbiddenValues))]
    public async Task AForbiddenValue_IsRefusedWithZeroBytes(long ticks)
    {
        var httpContext = new DefaultHttpContext { RequestServices = _services };
        var context = SegmentFilterContext(httpContext, ticks);

        new RejectsStartTimeTicksAttribute(ParameterName).OnActionExecuting(context);

        var result = Assert.IsType<ContentResult>(context.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, result.StatusCode);
        Assert.Null(result.Content);
        var body = new MemoryStream();
        httpContext.Response.Body = body;
        await result.ExecuteResultAsync(new ActionContext { HttpContext = httpContext }).ConfigureAwait(true);

        Assert.Equal(StatusCodes.Status400BadRequest, httpContext.Response.StatusCode);
        Assert.Equal(0, body.Length);
    }

    /// <summary>
    /// CLAIM 2, anti-vacuity. An absent or zero value is NOT short-circuited, so the rows above
    /// are the boundary refusing rather than the boundary refusing everything.
    /// </summary>
    /// <param name="ticks">The allowed value, or null for absent.</param>
    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    public void AnAllowedValue_IsNotShortCircuited(long? ticks)
    {
        var context = SegmentFilterContext(new DefaultHttpContext { RequestServices = _services }, ticks);

        new RejectsStartTimeTicksAttribute(ParameterName).OnActionExecuting(context);

        Assert.Null(context.Result);
    }

    /// <summary>
    /// CLAIM 2. A refused request reaches no collaborator: nothing resolves a streaming state,
    /// nothing looks a binding up, nothing names an output path and nothing touches the transcode
    /// manager.
    /// </summary>
    /// <remarks>
    /// HOW THIS IS MEASURED, AND WHY IT IS THE PIPELINE AND NOT THE FILTER. The helper below does
    /// what MVC does: it runs the filter, and it invokes the action only if the filter did not
    /// short-circuit. So a repair that refused later — inside the action, after the streaming
    /// state was resolved or after the output path was named — would still satisfy "the caller got
    /// a refusal" and would still fail here, on the collaborator that was reached to produce it.
    /// A test that only asserted the filter's return value could not tell those apart.
    ///
    /// The anti-vacuity control is the allowed-value row below, which reaches every one of these
    /// collaborators.
    /// </remarks>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    [Fact]
    public async Task AForbiddenValue_ResolvesNoStreamNamesNoPathAndTouchesNoJob()
    {
        var authorizer = new RecordingAuthorizer(HlsJobOwnershipOutcome.Authorized);
        var fixture = Fixture(authorizer);

        var (status, bytes) = await RunPipelineAsync(fixture, TimeSpan.FromSeconds(30).Ticks).ConfigureAwait(true);

        Assert.Equal(StatusCodes.Status400BadRequest, status);
        Assert.Equal(0, bytes);
        Assert.Empty(authorizer.Questions);
        Assert.False(fixture.BeyondTheGate.Reached);
        fixture.LibraryManager.Verify(l => l.GetItemById<BaseItem>(It.IsAny<Guid>(), It.IsAny<User>()), Times.Never);
        fixture.TranscodeManager.Verify(
            t => t.OnTranscodeBeginRequest(It.IsAny<string>(), It.IsAny<TranscodingJobType>()),
            Times.Never);
        fixture.TranscodeManager.Verify(
            t => t.LockAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// CLAIM 2, anti-vacuity for the row above. The same pipeline with an allowed value reaches
    /// every collaborator the row above asserts was never reached — so that row is the boundary
    /// refusing early, not a fixture that could never have reached them.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    [Fact]
    public async Task AnAllowedValue_ResolvesTheStreamNamesThePathAndReachesTheJob()
    {
        var authorizer = new RecordingAuthorizer(HlsJobOwnershipOutcome.Authorized);
        var fixture = Fixture(authorizer);

        await Assert.ThrowsAsync<BeyondTheGate.ReachedException>(
            () => RunPipelineAsync(fixture, 0L)).ConfigureAwait(true);

        Assert.Single(authorizer.Questions);
        Assert.True(fixture.BeyondTheGate.Reached);
        fixture.LibraryManager.Verify(l => l.GetItemById<BaseItem>(It.IsAny<Guid>(), It.IsAny<User>()), Times.AtLeastOnce);
        fixture.TranscodeManager.Verify(
            t => t.LockAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// CLAIM 2, the control that makes the row above mean something, and CLAIM 3's premise: with
    /// an allowed value the boundary passes, the action runs, and the ownership authorizer is
    /// asked exactly once — about THIS request's own <see cref="HttpContext"/>, the same instance
    /// the filter was handed. Validation and ownership are decisions about one request or they are
    /// not a boundary at all.
    /// </summary>
    /// <param name="segmentId">The segment, including the fMP4 init map at -1.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task AnAllowedValue_ReachesTheAuthorizerExactlyOnceOnTheSameHttpContext(int segmentId)
    {
        var authorizer = new RecordingAuthorizer(HlsJobOwnershipOutcome.Refused);
        var fixture = Fixture(authorizer);

        var context = SegmentFilterContext(fixture.HttpContext, 0L);
        new RejectsStartTimeTicksAttribute(ParameterName).OnActionExecuting(context);
        Assert.Null(context.Result);

        var result = await InvokeAudioSegment(fixture.Controller, segmentId, 0L).ConfigureAwait(true);

        var asked = Assert.Single(authorizer.Questions);
        Assert.Same(fixture.HttpContext, asked.Context);
        Assert.Same(context.HttpContext, asked.Context);
        Assert.Equal(".m3u8", Path.GetExtension(asked.OutputPath));
        Assert.IsType<UnauthorizedResult>(result);
    }

    /// <summary>
    /// CLAIM 2, and the fMP4 init map specifically. The init map is <c>segmentId = -1</c> on this
    /// same route, so it is covered by the same boundary — refused for a forbidden value and
    /// carried past it for an allowed one (the row above).
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    [Fact]
    public async Task TheInitMapSegment_IsCoveredByTheSameBoundary()
    {
        var authorizer = new RecordingAuthorizer(HlsJobOwnershipOutcome.Authorized);
        var fixture = Fixture(authorizer);

        var context = SegmentFilterContext(fixture.HttpContext, 1L, segmentId: -1);
        new RejectsStartTimeTicksAttribute(ParameterName).OnActionExecuting(context);

        Assert.IsType<ContentResult>(context.Result);
        Assert.Empty(authorizer.Questions);
        Assert.False(fixture.BeyondTheGate.Reached);

        // And the same route at -1 with an allowed value does reach the gate, so the row above is
        // the boundary refusing rather than the init map being unreachable for another reason.
        await Assert.ThrowsAsync<BeyondTheGate.ReachedException>(
            () => InvokeAudioSegment(fixture.Controller, -1, 0L)).ConfigureAwait(true);
        Assert.Single(authorizer.Questions);
    }

    /// <summary>
    /// CLAIM 3. The ownership matrix, driven through the real action against the real
    /// <see cref="HlsJobOwnershipAuthorizer"/>, with an allowed <c>startTimeTicks</c> present in
    /// the request. Only the job's owner is served; every other caller gets 401 and zero bytes,
    /// and a foreign capability does not fall back to the durable path.
    /// </summary>
    /// <param name="caller">The identity making the request.</param>
    /// <param name="isOwner">Whether that identity owns the job.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    [Theory]
    [MemberData(nameof(OwnershipRows))]
    public async Task TheOwnershipMatrix_IsUnchangedByTheBoundary(Caller caller, bool isOwner)
    {
        var fixture = Fixture(RealAuthorizer(), caller);

        var context = SegmentFilterContext(fixture.HttpContext, 0L);
        new RejectsStartTimeTicksAttribute(ParameterName).OnActionExecuting(context);
        Assert.Null(context.Result);

        if (isOwner)
        {
            // Authorized: the action carries on past the gate and takes the transcode manager's
            // lock, which is the first thing it does with an injected dependency after the gate.
            await Assert.ThrowsAsync<BeyondTheGate.ReachedException>(
                () => InvokeAudioSegment(fixture.Controller, 0, 0L)).ConfigureAwait(true);
            Assert.True(fixture.BeyondTheGate.Reached);
            return;
        }

        var result = await InvokeAudioSegment(fixture.Controller, 0, 0L).ConfigureAwait(true);

        Assert.IsType<UnauthorizedResult>(result);
        Assert.False(fixture.BeyondTheGate.Reached);

        var body = new MemoryStream();
        fixture.HttpContext.Response.Body = body;
        await result.ExecuteResultAsync(
            new ActionContext { HttpContext = fixture.HttpContext }).ConfigureAwait(true);

        Assert.Equal(StatusCodes.Status401Unauthorized, fixture.HttpContext.Response.StatusCode);
        Assert.Equal(0, body.Length);
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

    /// <summary>A stand-in for an action the boundary must not be able to sit on silently.</summary>
    private static void AnActionThatTakesNoStartOffset()
    {
    }

    /// <summary>
    /// What MVC does: run the action filters, then invoke the action only if none short-circuited,
    /// then execute whatever result came back. Returns the status the caller observes and the
    /// number of bytes they receive.
    /// </summary>
    private static async Task<(int Status, long Bytes)> RunPipelineAsync(
        ControllerFixture fixture,
        long? ticks,
        int segmentId = 0)
    {
        var context = SegmentFilterContext(fixture.HttpContext, ticks, segmentId);
        new RejectsStartTimeTicksAttribute(ParameterName).OnActionExecuting(context);

        var result = context.Result is null
            ? await InvokeAudioSegment(fixture.Controller, segmentId, ticks).ConfigureAwait(false)
            : (ActionResult)context.Result;

        var body = new MemoryStream();
        fixture.HttpContext.Response.Body = body;
        await result.ExecuteResultAsync(
            new ActionContext { HttpContext = fixture.HttpContext }).ConfigureAwait(false);

        return (fixture.HttpContext.Response.StatusCode, body.Length);
    }

    private static ActionExecutingContext FilterContext(
        MethodInfo method,
        HttpContext httpContext,
        IDictionary<string, object?> arguments)
    {
        var descriptor = new ControllerActionDescriptor
        {
            ActionName = method.Name,
            MethodInfo = method,
            ControllerTypeInfo = method.DeclaringType!.GetTypeInfo(),

            // Read off the real method, so a rename of the action's parameter changes what this
            // descriptor says without anyone having to remember to update it here.
            Parameters = method.GetParameters()
                .Select(p => (ParameterDescriptor)new ControllerParameterDescriptor
                {
                    Name = p.Name!,
                    ParameterType = p.ParameterType,
                    ParameterInfo = p
                })
                .ToList()
        };

        return new ActionExecutingContext(
            new ActionContext(httpContext, new RouteData(), descriptor),
            new List<IFilterMetadata>(),
            arguments,
            controller: null!);
    }

    private static ActionExecutingContext SegmentFilterContext(
        HttpContext httpContext,
        long? ticks,
        int segmentId = 0)
    {
        var method = typeof(DynamicHlsController).GetMethod(nameof(DynamicHlsController.GetHlsAudioSegment))!;
        var arguments = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["itemId"] = _itemA,
            ["playlistId"] = PlaylistId,
            ["segmentId"] = segmentId,
            ["container"] = "aac"
        };

        // An absent query key leaves the argument unbound, and MVC omits an unbound argument from
        // this dictionary entirely. Modelling "absent" as a present null would test a case the
        // framework never produces.
        if (ticks.HasValue)
        {
            arguments[ParameterName] = ticks;
        }

        return FilterContext(method, httpContext, arguments);
    }

    private static Task<ActionResult> InvokeAudioSegment(DynamicHlsController controller, int segmentId, long? startTimeTicks)
        => controller.GetHlsAudioSegment(
            _itemA,
            PlaylistId,
            segmentId,
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
            startTimeTicks,
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

    private static HlsSegmentBinding OwnerBinding(string transcodePath)
        => new(
            PlaylistId,
            UserId: _ownerUserId,
            DeviceId: OwnerDevice,
            _itemA,
            MediaSourceId: MediaSourceId,
            PlaySessionId: PlaySession,
            CanonicalRoot: Path.GetFullPath(transcodePath),
            CanonicalPlaylistPath: Path.GetFullPath(Path.Combine(transcodePath, PlaylistId + ".m3u8")),
            Generation: 1);

    private static ClaimsPrincipal PrincipalFor(Caller caller)
    {
        if (caller == Caller.Anonymous)
        {
            return new ClaimsPrincipal(new ClaimsIdentity());
        }

        var identity = new ClaimsIdentity("CustomAuthentication");
        var userId = caller == Caller.OtherUser ? _strangerUserId : _ownerUserId;
        identity.AddClaim(new Claim(InternalClaimTypes.UserId, userId.ToString("N")));
        identity.AddClaim(new Claim(ClaimTypes.Name, userId.ToString("N")));

        // A capability principal carries no device claim; the handler does not mint one.
        var deviceId = caller switch
        {
            Caller.OtherUser => "stranger-device",
            Caller.OtherDevice => "some-other-device",
            Caller.ForeignCapability => null,
            _ => OwnerDevice
        };

        if (deviceId is not null)
        {
            identity.AddClaim(new Claim(InternalClaimTypes.DeviceId, deviceId));
        }

        return new ClaimsPrincipal(identity);
    }

    private IHlsJobOwnershipAuthorizer RealAuthorizer()
    {
        var registry = new Mock<IHlsSegmentBindingRegistry>(MockBehavior.Loose);
        registry.Setup(r => r.ResolveByOutputPath(It.IsAny<string>())).Returns(OwnerBinding(_transcodePath));
        registry.Setup(r => r.ResolveByPlaylistId(It.IsAny<string>())).Returns(OwnerBinding(_transcodePath));

        var sessionManager = new Mock<ISessionManager>(MockBehavior.Loose);
        sessionManager.SetupGet(m => m.Sessions).Returns(Array.Empty<SessionInfo>());

        return new HlsJobOwnershipAuthorizer(registry.Object, sessionManager.Object);
    }

    private ControllerFixture Fixture(IHlsJobOwnershipAuthorizer authorizer, Caller caller = Caller.Owner)
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

        var mediaEncoder = new Mock<IMediaEncoder>();
        mediaEncoder.Setup(e => e.CanEncodeToAudioCodec(It.IsAny<string>())).Returns(true);
        mediaEncoder.SetupGet(e => e.EncoderVersion).Returns(new Version(6, 0));

        var gate = new BeyondTheGate();
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
            Request = { Path = new PathString($"/Audio/{_itemA:N}/hls1/{PlaylistId}/0.aac") },
            User = PrincipalFor(caller),
            RequestServices = _services
        };

        if (caller == Caller.ForeignCapability)
        {
            // Valid, and bound to a DIFFERENT item. A presented capability is the whole decision:
            // there is no fallback to the durable path, which is what this row pins.
            httpContext.Features.Set(new ValidatedPlaybackCapability(
                "capability-value",
                Guid.NewGuid(),
                _itemB,
                MediaSourceId,
                PlaySession,
                PlaybackCapabilityScope.Media,
                new PlaybackCapabilityValidation(
                    true,
                    PlaybackCapabilityFailure.None,
                    Guid.NewGuid(),
                    _ownerUserId,
                    "owner-session",
                    PlaySession)));
        }

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

        return new ControllerFixture(controller, httpContext, gate, libraryManager, transcodeManager);
    }

    private sealed record ControllerFixture(
        DynamicHlsController Controller,
        HttpContext HttpContext,
        BeyondTheGate BeyondTheGate,
        Mock<ILibraryManager> LibraryManager,
        Mock<ITranscodeManager> TranscodeManager);

    /// <summary>
    /// A marker for "the action got past the ownership gate". The first thing
    /// <c>GetDynamicSegment</c> does after the gate that touches an injected dependency is take
    /// the transcode manager's lock.
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
