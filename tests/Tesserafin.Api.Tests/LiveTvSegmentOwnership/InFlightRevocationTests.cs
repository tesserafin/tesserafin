#pragma warning disable CS0618 // tesserafin#129 step 7: the legacy route under test is deprecated, and this test goes with it.
using System;
using System.IO;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Tesserafin.Api.Auth.HlsJobOwnership;
using Tesserafin.Api.Constants;
using Tesserafin.Api.Controllers;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Controller.Session;
using Xunit;

namespace Tesserafin.Api.Tests.LiveTvSegmentOwnership;

/// <summary>
/// What a stop does to a request that is already in progress, frozen as a contract
/// (#153-LTV-R5, documenting R4 finding F7).
/// </summary>
/// <remarks>
/// WHAT R4 MEASURED. Phase 4b killed a job 274 ms into an already-authorized read and the server
/// still answered 200 with 93 228 bytes at 330 ms. The mission accepts that "uniquement si cette
/// sémantique est explicitement documentée et qu'aucune requête commencée après la révocation ne
/// réussit". The documentation is in <see cref="HlsJobOwnershipAuthorizer"/>'s remarks; these tests
/// are the part of it that can fail.
///
/// THE CONTRACT, IN ONE SENTENCE. Revocation happens at the REQUEST boundary, not retroactively
/// inside a response that was already authorized.
///
/// THE BARRIERS ARE DETERMINISTIC, NOT TIMED. R4's evidence came from racing a real encoder and
/// only landed in one of four attempts. Here the stop is placed exactly where the race would have
/// to land to be interesting: the registry double flips itself to "stopped" ON THE WAY OUT of the
/// single resolve the decision makes — so the job is gone by the time the action builds a path and
/// opens a file, every run, on every host.
///
/// ONE DELIBERATE DIFFERENCE FROM THE REAL SERVER, STATED SO IT IS NOT MISTAKEN FOR A CLAIM. The
/// fixture leaves the segment file on disk after the stop. The real teardown deletes it — R4
/// Phase 4c measured 88 files before the stop and zero after — so on a live server the in-flight
/// completion is even narrower than this. Keeping the file is what makes the in-flight window
/// OBSERVABLE at all: if the file vanished, a 404 would prove nothing about authorization.
/// </remarks>
public sealed class InFlightRevocationTests : IDisposable
{
    private const string JobPlaylist = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string OtherPlaylist = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string OwnerDevice = "owner-device";
    private const string JobMediaSource = "6d5da76e3955fd1005f75c496c371521";
    private const string JobPlaySession = "306f71094f36456f9d0dc6e7b12b8a6b";

    private static readonly Guid _itemA = new("11111111111111111111111111111111");
    private static readonly Guid _ownerUserId = new("aaaaaaaa11114444888800000000cccc");
    private static readonly byte[] _segmentBytes = { 0x47, 0x41, 0x42, 0x43, 0x44, 0x45, 0x46, 0x47 };

    private readonly string _root;
    private readonly ServiceProvider _services;

    /// <summary>
    /// Initializes a new instance of the <see cref="InFlightRevocationTests"/> class.
    /// </summary>
    public InFlightRevocationTests()
    {
        _services = new ServiceCollection().AddLogging().AddMvcCore().Services.BuildServiceProvider();

        _root = Path.Combine(Path.GetTempPath(), "ltvr5-inflight-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, JobPlaylist + ".m3u8"), "#EXTM3U\n");
        File.WriteAllBytes(Path.Combine(_root, JobPlaylist + "0.ts"), _segmentBytes);
        File.WriteAllBytes(Path.Combine(_root, OtherPlaylist + "0.ts"), _segmentBytes);
    }

    /// <summary>
    /// INTERLOCK 1 — the stop lands BEFORE the authorization decision. The owner is refused, the
    /// residual file is not served, and the refusal carries no binding for anything to open.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    [Fact]
    public async Task StopBeforeAuthorization_RefusesTheOwnerAndServesNoResidualBytes()
    {
        var registry = new StoppableRegistry(Binding()) { Stopped = true };

        var (result, body, status) = await Invoke(registry).ConfigureAwait(true);

        Assert.IsNotType<PhysicalFileResult>(result);
        Assert.Equal(StatusCodes.Status404NotFound, status);
        Assert.False(ContainsTheResource(body, _segmentBytes));
        Assert.True(File.Exists(Path.Combine(_root, JobPlaylist + "0.ts")), "the residual file is still there, so the refusal is a decision and not an absence.");
    }

    /// <summary>
    /// INTERLOCK 2 — the stop lands AFTER a successful authorization and BEFORE the open. The
    /// request completes. This is the measured semantics, and it is the one this file exists to
    /// freeze.
    /// </summary>
    /// <remarks>
    /// The linearization point is the successful decision taken while the binding existed. The
    /// decision carries the binding, so nothing below re-reads the registry — which is exactly why
    /// the response can complete, and exactly why nothing else can.
    /// </remarks>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    [Fact]
    public async Task AuthorizedThenStoppedBeforeTheOpen_CompletesTheRequestItHadAlreadyAuthorized()
    {
        var registry = new StoppableRegistry(Binding()) { StopOnTheWayOutOfTheFirstResolve = true };

        var (result, body, status) = await Invoke(registry).ConfigureAwait(true);

        Assert.IsType<PhysicalFileResult>(result);
        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.Equal(_segmentBytes, body);

        // The stop really did land before the open: the registry has been empty since the single
        // resolve returned, and the action never consulted it again.
        Assert.True(registry.Stopped);
        Assert.Equal(1, registry.Resolves);
        Assert.Null(registry.ResolveByPlaylistId(JobPlaylist));
    }

    /// <summary>
    /// INTERLOCK 3 — a request BEGUN after the stop is refused, from the same fixture in which the
    /// previous request had just succeeded.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    [Fact]
    public async Task ARequestBegunAfterTheStop_IsRefusedEvenThoughTheOneBeforeItSucceeded()
    {
        var registry = new StoppableRegistry(Binding()) { StopOnTheWayOutOfTheFirstResolve = true };

        var (first, firstBody, firstStatus) = await Invoke(registry).ConfigureAwait(true);
        Assert.IsType<PhysicalFileResult>(first);
        Assert.Equal(StatusCodes.Status200OK, firstStatus);
        Assert.Equal(_segmentBytes, firstBody);

        var (second, secondBody, secondStatus) = await Invoke(registry).ConfigureAwait(true);

        Assert.IsNotType<PhysicalFileResult>(second);
        Assert.Equal(StatusCodes.Status404NotFound, secondStatus);
        Assert.False(ContainsTheResource(secondBody, _segmentBytes));
    }

    /// <summary>
    /// INTERLOCK 4 — the job's disappearance recreates no binding and falls back to nothing. Not
    /// for its own segment, not for a residual file of another job, and not on any later attempt.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    [Fact]
    public async Task AfterTheStop_NoBindingIsRecreatedAndNothingFallsBack()
    {
        var registry = new StoppableRegistry(Binding()) { Stopped = true };

        // No resurrection: repeated resolution stays empty, on every member the routes use.
        Assert.Null(registry.ResolveByPlaylistId(JobPlaylist));
        Assert.Null(registry.ResolveBySegmentName(JobPlaylist + "0"));
        Assert.Null(registry.ResolveByOutputPath(Path.Combine(_root, JobPlaylist + ".m3u8")));
        Assert.Null(registry.ResolveByPlaylistId(JobPlaylist));

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var (result, body, status) = await Invoke(registry).ConfigureAwait(true);
            Assert.IsNotType<PhysicalFileResult>(result);
            Assert.Equal(StatusCodes.Status404NotFound, status);
            Assert.False(ContainsTheResource(body, _segmentBytes));
        }

        // And no fallback to a file the dead job did not write either: naming another job's
        // residual segment resolves nothing rather than reaching the folder.
        var (other, otherBody, otherStatus) = await Invoke(registry, OtherPlaylist).ConfigureAwait(true);
        Assert.IsNotType<PhysicalFileResult>(other);
        Assert.Equal(StatusCodes.Status404NotFound, otherStatus);
        Assert.False(ContainsTheResource(otherBody, _segmentBytes));
    }

    /// <summary>
    /// Anti-vacuity: with no stop at all, the same fixture and the same owner are served. Without
    /// this, every refusal above could be a broken harness.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    [Fact]
    public async Task WithNoStopAtAll_TheOwnerIsServed()
    {
        var (result, body, status) = await Invoke(new StoppableRegistry(Binding())).ConfigureAwait(true);

        Assert.IsType<PhysicalFileResult>(result);
        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.Equal(_segmentBytes, body);
    }

    private static bool ContainsTheResource(byte[] body, byte[] resource)
    {
        for (var i = 0; i + resource.Length <= body.Length; i++)
        {
            var match = true;
            for (var j = 0; j < resource.Length; j++)
            {
                if (body[i + j] != resource[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return true;
            }
        }

        return false;
    }

    private static ClaimsPrincipal Owner()
    {
        var identity = new ClaimsIdentity("CustomAuthentication");
        identity.AddClaim(new Claim(InternalClaimTypes.UserId, _ownerUserId.ToString("N")));
        identity.AddClaim(new Claim(ClaimTypes.Name, _ownerUserId.ToString("N")));
        identity.AddClaim(new Claim(InternalClaimTypes.DeviceId, OwnerDevice));
        return new ClaimsPrincipal(identity);
    }

    private HlsSegmentBinding Binding()
        => new(
            JobPlaylist,
            UserId: _ownerUserId,
            DeviceId: OwnerDevice,
            _itemA,
            MediaSourceId: JobMediaSource,
            PlaySessionId: JobPlaySession,
            CanonicalRoot: Path.GetFullPath(_root),
            CanonicalPlaylistPath: Path.GetFullPath(Path.Combine(_root, JobPlaylist + ".m3u8")),
            Generation: 1);

    private async Task<(ActionResult Result, byte[] Body, int Status)> Invoke(StoppableRegistry registry, string? playlistId = null)
    {
        playlistId ??= JobPlaylist;
        var segmentId = playlistId + "0";

        var httpContext = new DefaultHttpContext
        {
            Request = { Path = new PathString($"/Videos/{_itemA:N}/hls/{playlistId}/{segmentId}.ts") },
            User = Owner(),
            RequestServices = _services
        };
        httpContext.Request.QueryString = QueryString.Create("mediaSourceId", JobMediaSource);

        var transcodeManager = new Mock<ITranscodeManager>(MockBehavior.Loose);
        transcodeManager
            .Setup(t => t.OnTranscodeBeginRequest(It.IsAny<string>(), It.IsAny<TranscodingJobType>()))
            .Returns((TranscodingJob?)null);

        var sessionManager = new Mock<ISessionManager>(MockBehavior.Loose);
        sessionManager.SetupGet(m => m.Sessions).Returns(Array.Empty<SessionInfo>());

        var controller = new HlsSegmentController(
            transcodeManager.Object,
            new HlsJobOwnershipAuthorizer(registry, sessionManager.Object))
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };

        var result = controller.GetHlsVideoSegmentLegacy(_itemA.ToString("N"), playlistId, segmentId, "ts");

        var body = new MemoryStream();
        httpContext.Response.Body = body;
        await result.ExecuteResultAsync(new ActionContext { HttpContext = httpContext }).ConfigureAwait(true);

        return (result, body.ToArray(), httpContext.Response.StatusCode);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _services.Dispose();

        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not a test failure.
        }
    }

    /// <summary>
    /// A registry that can be stopped, either before the request arrives or on the way out of the
    /// one resolve the decision makes — which is the only instant at which "authorized, then
    /// stopped, then opened" is reachable at all.
    /// </summary>
    private sealed class StoppableRegistry : IHlsSegmentBindingRegistry
    {
        private readonly HlsSegmentBinding _binding;

        public StoppableRegistry(HlsSegmentBinding binding) => _binding = binding;

        public bool Stopped { get; set; }

        public bool StopOnTheWayOutOfTheFirstResolve { get; set; }

        public int Resolves { get; private set; }

        public HlsSegmentBinding? ResolveByPlaylistId(string playlistId)
            => Resolve(string.Equals(playlistId, _binding.PlaylistId, StringComparison.Ordinal));

        public HlsSegmentBinding? ResolveBySegmentName(string segmentName)
            => Resolve(segmentName.StartsWith(_binding.PlaylistId, StringComparison.Ordinal));

        public HlsSegmentBinding? ResolveByOutputPath(string outputPath)
            => Resolve(string.Equals(outputPath, _binding.CanonicalPlaylistPath, StringComparison.Ordinal));

        private HlsSegmentBinding? Resolve(bool namesThisJob)
        {
            Resolves++;
            if (Stopped || !namesThisJob)
            {
                return null;
            }

            if (StopOnTheWayOutOfTheFirstResolve)
            {
                // The stop lands here: after the decision has its binding, before the action has a
                // path. Everything the action does from now on reads the snapshot it was handed.
                Stopped = true;
            }

            return _binding;
        }
    }
}
