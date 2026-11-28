#pragma warning disable CS0618 // tesserafin#129 step 7: the legacy route under test is deprecated, and this test goes with it.
using System;
using System.IO;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Tesserafin.Api.Auth.HlsJobOwnership;
using Tesserafin.Api.Constants;
using Tesserafin.Api.Controllers;
using Tesserafin.Common.Configuration;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Controller.Session;
using Tesserafin.Model.Configuration;
using Xunit;

namespace Tesserafin.Api.Tests.LiveTvSegmentOwnership;

/// <summary>
/// Every legacy HLS resource derived from a transcoding job must be reachable only by the job's
/// own owner, or by a capability that matches that job exactly (#153-LTV-R3, finding R2-1).
/// </summary>
/// <remarks>
/// WHAT LTV-R2 MEASURED. <c>HlsSegmentController</c> at c70fe557 contains zero occurrences of
/// <c>User</c>, <c>Claims</c>, <c>DeviceId</c> or <c>GetUserId</c>: the actions read no caller
/// identity at all. <c>Policies.MediaDelivery</c> succeeds for
/// <c>GetIsApiKey() || !GetUserId().IsEmpty()</c> — that is, for ANY authenticated principal — and
/// <c>RequiresPlaybackCapabilityAttribute</c> returns immediately when no capability is presented.
/// So a second authenticated user, holding nothing but their own durable token and the url, read
/// the first user's live segment bytes: 200 and a <see cref="PhysicalFileResult"/>.
///
/// These are the permanent reproductions of that boundary. They are built on the controller
/// directly rather than on a booted server because the property under test is decided before any
/// I/O: which caller the action compares against which job.
///
/// ANTI-VACUITY. <c>TheOwnersOwnDurableToken_StillReachesItsSegment</c> is the control for every
/// refusal below. LTV-R2 recorded honestly that at c70fe557 that control was NOT anti-vacuous —
/// the action read no principal, so all three invocations exercised one code path and the third
/// test asserted the same fact with the opposite sign. Once the authorizer exists the control
/// becomes real: it is then the only test here whose principal agrees with the job.
/// </remarks>
public sealed class HlsJobOwnershipTests : IDisposable
{
    private const string JobA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string JobB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string JobMediaSource = "6d5da76e3955fd1005f75c496c371521";
    private const string JobPlaySession = "306f71094f36456f9d0dc6e7b12b8a6b";
    private const string OwnerDevice = "owner-device";

    private static readonly Guid _itemA = new("11111111111111111111111111111111");
    private static readonly Guid _ownerUserId = new("aaaaaaaa11114444888800000000cccc");
    private static readonly Guid _strangerUserId = new("dddddddd2222555599990000eeeeffff");

    private readonly string _transcodePath;

    public HlsJobOwnershipTests()
    {
        _transcodePath = Path.Combine(Path.GetTempPath(), "ltvr3-job-ownership-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_transcodePath);

        // Two live jobs side by side in the one flat transcode folder, exactly as the server lays
        // them out: "<hash>.m3u8" for the playlist, "<hash><n>.ts"/".aac" for its segments.
        File.WriteAllText(Path.Combine(_transcodePath, JobA + ".m3u8"), "#EXTM3U\n");
        File.WriteAllText(Path.Combine(_transcodePath, JobB + ".m3u8"), "#EXTM3U\n");
        File.WriteAllBytes(Path.Combine(_transcodePath, JobA + "0.ts"), new byte[] { 0x47, 0x41 });
        File.WriteAllBytes(Path.Combine(_transcodePath, JobB + "0.ts"), new byte[] { 0x47, 0x42 });
        File.WriteAllBytes(Path.Combine(_transcodePath, JobA + "0.aac"), new byte[] { 0xFF, 0xF1, 0x0A });

        // #153-LTV-R5, repairing R4 finding F3. Job B's audio segment used to be MISSING, and
        // `AnAudioSegmentNameOwnedByNoJob_IsNeverServed` asserted that exactly this file was not
        // served. A file that does not exist cannot be served by any implementation, so that
        // assertion could not fail whatever the resolution did. It exists now, with bytes of its
        // own, so the refusal below is a decision rather than an absence.
        File.WriteAllBytes(Path.Combine(_transcodePath, JobB + "0.aac"), new byte[] { 0xFF, 0xF1, 0x0B });
    }

    // ---------------------------------------------------------------------------------------
    // Legacy HLS video segment: Videos/{itemId}/hls/{playlistId}/{segmentId}.{container}
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The job belongs to the owner. A DIFFERENT authenticated user, holding only a durable token
    /// and the url, must not reach its bytes.
    /// </summary>
    [Fact]
    public void ADurableTokenOfAnotherUser_NeverReachesTheJobsVideoSegment()
    {
        var result = InvokeVideo(callerUserId: _strangerUserId, callerDeviceId: "stranger-device");

        Assert.False(
            IsFileResultFor(result, JobA + "0.ts"),
            "a durable token belonging to another user reached the job's video segment bytes.");
    }

    /// <summary>
    /// The same user, on a device the job was not started from, is equally a stranger to the job.
    /// </summary>
    [Fact]
    public void ADurableTokenOfAnotherDevice_NeverReachesTheJobsVideoSegment()
    {
        var result = InvokeVideo(callerUserId: _ownerUserId, callerDeviceId: "some-other-device");

        Assert.False(
            IsFileResultFor(result, JobA + "0.ts"),
            "a durable token from another device reached the job's video segment bytes.");
    }

    /// <summary>
    /// An administrator who does not own the job is refused. Being an administrator is not a
    /// playback authority: the mission admits no role-based exemption.
    /// </summary>
    /// <remarks>
    /// The action must refuse on its own. This is the one boundary that cannot be delegated to a
    /// policy: <c>DefaultAuthorizationHandler</c> succeeds an administrator outright, and
    /// <c>UserPermissionRequirement</c> subclasses <c>DefaultAuthorizationRequirement</c>, so an
    /// administrator passes every permission policy in the server.
    /// </remarks>
    [Fact]
    public void AnAdministratorWhoDoesNotOwnTheJob_IsRefused()
    {
        var result = InvokeVideo(
            callerUserId: _strangerUserId,
            callerDeviceId: "admin-device",
            callerIsAdministrator: true);

        Assert.False(
            IsFileResultFor(result, JobA + "0.ts"),
            "an administrator who does not own the job reached its video segment bytes.");
    }

    /// <summary>
    /// An API key resolves to no user, so it can never be the owner of a job.
    /// </summary>
    [Fact]
    public void AnApiKeyWithNoResolvableUser_IsRefused()
    {
        var result = InvokeVideo(
            callerUserId: Guid.Empty,
            callerDeviceId: null,
            callerIsApiKey: true);

        Assert.False(
            IsFileResultFor(result, JobA + "0.ts"),
            "an api key with no resolvable user reached the job's video segment bytes.");
    }

    /// <summary>
    /// A caller with no credential at all reaches nothing. The route carries
    /// <c>Policies.MediaDelivery</c>, so the pipeline refuses first; the action must refuse too,
    /// so that the boundary does not depend on one attribute staying attached.
    /// </summary>
    [Fact]
    public void ACallerWithNoPrincipalAtAll_IsRefused()
    {
        var result = InvokeVideo(callerUserId: null, callerDeviceId: null);

        Assert.False(
            IsFileResultFor(result, JobA + "0.ts"),
            "an unauthenticated caller reached the job's video segment bytes.");
    }

    /// <summary>
    /// The owner's own durable token still reaches its own segment. This is the anti-vacuity
    /// control for every refusal above.
    /// </summary>
    [Fact]
    public void TheOwnersOwnDurableToken_StillReachesItsSegment()
    {
        var result = InvokeVideo(callerUserId: _ownerUserId, callerDeviceId: OwnerDevice);

        Assert.True(
            IsFileResultFor(result, JobA + "0.ts"),
            "the owner could not read its own segment; every refusal here would be vacuous.");
    }

    /// <summary>
    /// The owner of job A cannot reach job B's segment even naming their own playlist. This is
    /// already closed at c70fe557 by the segment-name prefix check; it is pinned here so the
    /// authorizer cannot be built in a way that reopens it.
    /// </summary>
    [Fact]
    public void TheOwnerOfOneJob_NeverReachesAnotherJobsSegment()
    {
        var result = InvokeVideo(
            callerUserId: _ownerUserId,
            callerDeviceId: OwnerDevice,
            segmentId: JobB + "0");

        Assert.False(
            IsFileResultFor(result, JobB + "0.ts"),
            "the owner of job A reached job B's segment bytes.");
    }

    /// <summary>
    /// With no server-side job the answer is a closed refusal, for the owner as much as anyone.
    /// </summary>
    [Fact]
    public void WhenTheJobIsGone_EvenItsOwnerIsRefused()
    {
        var result = InvokeVideo(
            callerUserId: _ownerUserId,
            callerDeviceId: OwnerDevice,
            jobIsGone: true);

        Assert.False(
            IsFileResultFor(result, JobA + "0.ts"),
            "a residual segment file of a job that no longer exists was served.");
    }

    // ---------------------------------------------------------------------------------------
    // Legacy HLS audio segment: Audio/{itemId}/hls/{segmentId}/stream.mp3|.aac  (finding R2-2)
    // ---------------------------------------------------------------------------------------

    // ---------------------------------------------------------------------------------------
    // Legacy HLS playlist: Videos/{itemId}/hls/{playlistId}/stream.m3u8  (finding R2-3)
    // ---------------------------------------------------------------------------------------

    // ---------------------------------------------------------------------------------------

    private static bool IsFileResultFor(ActionResult result, string fileName)
        => result is PhysicalFileResult physical
           && string.Equals(Path.GetFileName(physical.FileName), fileName, StringComparison.Ordinal);

    private static ClaimsPrincipal Principal(Guid? userId, string? deviceId, bool isApiKey, bool isAdministrator)
    {
        var identity = new ClaimsIdentity(authenticationType: userId is null && !isApiKey ? null : "CustomAuthentication");

        if (userId is { } id && !id.Equals(Guid.Empty))
        {
            identity.AddClaim(new Claim(InternalClaimTypes.UserId, id.ToString("N")));
            identity.AddClaim(new Claim(ClaimTypes.Name, id.ToString("N")));
        }

        if (deviceId is not null)
        {
            identity.AddClaim(new Claim(InternalClaimTypes.DeviceId, deviceId));
        }

        if (isApiKey)
        {
            identity.AddClaim(new Claim(InternalClaimTypes.IsApiKey, bool.TrueString));
        }

        if (isAdministrator)
        {
            identity.AddClaim(new Claim(ClaimTypes.Role, UserRoles.Administrator));
        }

        return new ClaimsPrincipal(identity);
    }

    private HlsSegmentController Controller(HttpContext httpContext, bool jobIsGone)
    {
        var configuration = new Mock<IServerConfigurationManager>(MockBehavior.Loose);
        configuration
            .Setup(c => c.GetConfiguration("encoding"))
            .Returns(new EncodingOptions { TranscodingTempPath = _transcodePath });
        configuration
            .SetupGet(c => c.CommonApplicationPaths)
            .Returns(Mock.Of<IApplicationPaths>());

        var binding = jobIsGone
            ? null
            : new HlsSegmentBinding(
                JobA,
                UserId: _ownerUserId,
                DeviceId: OwnerDevice,
                _itemA,
                MediaSourceId: JobMediaSource,
                PlaySessionId: JobPlaySession,
                CanonicalRoot: Path.GetFullPath(_transcodePath),
                CanonicalPlaylistPath: Path.GetFullPath(Path.Combine(_transcodePath, JobA + ".m3u8")),
                Generation: 1);

        // The registry stands in for the live job list: job A exists (unless the test says it is
        // gone) and nothing else does.
        var registry = new Mock<IHlsSegmentBindingRegistry>(MockBehavior.Loose);
        registry.Setup(r => r.ResolveByPlaylistId(JobA)).Returns(binding);

        var transcodeManager = new Mock<ITranscodeManager>(MockBehavior.Loose);
        transcodeManager
            .Setup(t => t.OnTranscodeBeginRequest(It.IsAny<string>(), It.IsAny<TranscodingJobType>()))
            .Returns((TranscodingJob?)null);

        // The REAL authorizer over a mocked registry: the boundary under test is its logic, so
        // substituting it would make every assertion here a statement about a mock.
        var sessionManager = new Mock<ISessionManager>(MockBehavior.Loose);
        sessionManager.SetupGet(m => m.Sessions).Returns(Array.Empty<SessionInfo>());
        var authorizer = new HlsJobOwnershipAuthorizer(registry.Object, sessionManager.Object);

        return new HlsSegmentController(transcodeManager.Object, authorizer)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };
    }

    private ActionResult InvokeVideo(
        Guid? callerUserId,
        string? callerDeviceId,
        string? segmentId = null,
        bool jobIsGone = false,
        bool callerIsApiKey = false,
        bool callerIsAdministrator = false)
    {
        segmentId ??= JobA + "0";

        var httpContext = new DefaultHttpContext
        {
            Request = { Path = new PathString($"/Videos/{_itemA:N}/hls/{JobA}/{segmentId}.ts") },
            User = Principal(callerUserId, callerDeviceId, callerIsApiKey, callerIsAdministrator)
        };

        return Controller(httpContext, jobIsGone)
            .ServeOwnedVideoSegment(_itemA.ToString("N"), JobA, segmentId, requiredPlaySessionId: null);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_transcodePath, true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not a test failure.
        }
    }
}
