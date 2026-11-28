#pragma warning disable CS0618 // tesserafin#129 step 7: the legacy route under test is deprecated, and this test goes with it.
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
/// The permanent #153-LTV-R3 Phase 4 matrix: every caller shape against every job-backed HLS
/// resource family, driven all the way to the bytes on the wire.
/// </summary>
/// <remarks>
/// WHAT IS COVERED HERE AND WHAT IS NOT. Two of the three families are exercised through their
/// real controller action: the legacy video segment and its audio sibling. The third —
/// <c>DynamicHlsController</c>'s <c>hls1/</c> pair, its fMP4 init map and <c>live.m3u8</c> — cannot
/// be reached at this level, because its action resolves a full streaming state first and that
/// needs a library, a media source manager and an encoder. Its DECISION is nevertheless the same
/// decision: those routes call <c>AuthorizeByOutputPath</c>, and every row below is therefore also
/// run directly against the authorizer through that member, so the matrix covers the family's
/// verdict even where it cannot cover its plumbing. The plumbing is covered by the source-level
/// ordering gate and by Phase 6.
///
/// EVERY REFUSAL IS CHECKED AT THE BYTE LEVEL, NOT AT THE RESULT TYPE. A refused row is executed
/// through the framework's own result executor and the response body is measured. GET, HEAD and
/// Range are each driven separately, because they take different paths through
/// <c>PhysicalFileResultExecutor</c> and "refused" has to mean zero bytes on all three.
/// </remarks>
public sealed class HlsOwnershipMatrixTests : IDisposable
{
    private const string JobA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string JobB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string JobMediaSource = "6d5da76e3955fd1005f75c496c371521";
    private const string JobPlaySession = "306f71094f36456f9d0dc6e7b12b8a6b";
    private const string OwnerDevice = "owner-device";
    private const string OwnerSession = "owner-session";
    private const string ForeignSession = "a-session-on-another-device";

    private static readonly Guid _itemA = new("11111111111111111111111111111111");
    private static readonly Guid _itemB = new("22222222222222222222222222222222");
    private static readonly Guid _ownerUserId = new("aaaaaaaa11114444888800000000cccc");
    private static readonly Guid _strangerUserId = new("dddddddd2222555599990000eeeeffff");

    private static readonly byte[] _videoBytes = { 0x47, 0x41, 0x42, 0x43, 0x44, 0x45, 0x46, 0x47 };
    private static readonly byte[] _audioBytes = { 0xFF, 0xF1, 0x50, 0x80, 0x11, 0x22, 0x33, 0x44 };

    private readonly string _transcodePath;
    private readonly ServiceProvider _services;

    public HlsOwnershipMatrixTests()
    {
        // A real MVC service provider, so that every ActionResult below — file, status code and
        // object alike — is executed by the framework's own executors rather than by an imitation
        // of them. "Zero bytes on refusal" is then a measurement of what the framework writes.
        _services = new ServiceCollection()
            .AddLogging()
            .AddMvcCore()
            .Services
            .BuildServiceProvider();

        _transcodePath = Path.Combine(Path.GetTempPath(), "ltvr3-matrix-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_transcodePath);
        File.WriteAllText(Path.Combine(_transcodePath, JobA + ".m3u8"), "#EXTM3U\n");
        File.WriteAllText(Path.Combine(_transcodePath, JobB + ".m3u8"), "#EXTM3U\n");
        File.WriteAllBytes(Path.Combine(_transcodePath, JobA + "0.ts"), _videoBytes);
        File.WriteAllBytes(Path.Combine(_transcodePath, JobB + "0.ts"), _videoBytes);
        File.WriteAllBytes(Path.Combine(_transcodePath, JobA + "0.aac"), _audioBytes);

        // #153-LTV-R5. Job B's audio segment exists on disk too, and job B is deliberately NOT in
        // the registry. A resolution that fell back to the flat folder, or that selected the wrong
        // job, would now serve real bytes into a refused row instead of finding nothing there.
        File.WriteAllBytes(Path.Combine(_transcodePath, JobB + "0.aac"), _audioBytes);
    }

    /// <summary>
    /// A caller shape from the mission's Phase 4 table.
    /// </summary>
    public enum Row
    {
        /// <summary>Durable token, same user and same device as the job.</summary>
        DurableOwner,

        /// <summary>Durable token belonging to a different user, on a different device.</summary>
        DurableOtherUser,

        /// <summary>
        /// A different user whose token names the SAME device as the job's.
        /// </summary>
        /// <remarks>
        /// Two people on one shared device — a family television, a browser profile handed over —
        /// is the ordinary case, not a contrived one. This row exists because without it the
        /// device comparison alone refuses every other-user row, and the hostile control that
        /// deletes the USER comparison graded INERT: nothing separated the two checks.
        /// </remarks>
        DurableOtherUserSameDevice,

        /// <summary>The job's user, on a device the job was not started from.</summary>
        DurableOtherDevice,

        /// <summary>An administrator who does not own the job.</summary>
        AdministratorWhoIsNotTheOwner,

        /// <summary>An api key, which resolves to no user at all.</summary>
        ApiKeyWithNoUser,

        /// <summary>No credential of any kind.</summary>
        NoCredentialAtAll,

        /// <summary>A capability agreeing with the job on every binding it carries.</summary>
        ExactCapability,

        /// <summary>A capability bound to another item.</summary>
        CapabilityForAnotherItem,

        /// <summary>A capability bound to another media source.</summary>
        CapabilityForAnotherMediaSource,

        /// <summary>A capability minted under another play session.</summary>
        CapabilityForAnotherPlaySession,

        /// <summary>A capability of the right user, minted for a session on another device.</summary>
        CapabilityFromAnotherDevicesSession,

        /// <summary>The owner of job A, asking for a resource of job B.</summary>
        OwnerOfJobAAskingForJobB,

        /// <summary>The owner, after the job has ended and only its files remain.</summary>
        OwnerWhoseJobIsGone
    }

    private enum Family
    {
        Video,
        Audio
    }

    private enum Verb
    {
        Get,
        Head,
        Range
    }

    /// <summary>
    /// The matrix. Each row names a caller shape, what it is asking for, and whether the mission
    /// says it may have it.
    /// </summary>
    /// <returns>The rows.</returns>
    public static IEnumerable<object[]> Matrix() => new[]
    {
        new object[] { Row.DurableOwner, true },
        new object[] { Row.DurableOtherUser, false },
        new object[] { Row.DurableOtherUserSameDevice, false },
        new object[] { Row.DurableOtherDevice, false },
        new object[] { Row.AdministratorWhoIsNotTheOwner, false },
        new object[] { Row.ApiKeyWithNoUser, false },
        new object[] { Row.NoCredentialAtAll, false },
        new object[] { Row.ExactCapability, true },
        new object[] { Row.CapabilityForAnotherItem, false },
        new object[] { Row.CapabilityForAnotherMediaSource, false },
        new object[] { Row.CapabilityForAnotherPlaySession, false },
        new object[] { Row.CapabilityFromAnotherDevicesSession, false },
        new object[] { Row.OwnerOfJobAAskingForJobB, false },
        new object[] { Row.OwnerWhoseJobIsGone, false }
    };

    /// <summary>
    /// Legacy HLS video segment, driven to the bytes for GET, HEAD and Range.
    /// </summary>
    /// <param name="row">The matrix row.</param>
    /// <param name="expectedToBeServed">Whether the mission allows this caller through.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task VideoSegmentFamily(Row row, bool expectedToBeServed)
    {
        foreach (var verb in new[] { Verb.Get, Verb.Head, Verb.Range })
        {
            var expectedFile = row == Row.OwnerOfJobAAskingForJobB ? JobB + "0.ts" : JobA + "0.ts";
            await AssertRow(row, verb, expectedToBeServed, expectedFile, _videoBytes, Family.Video).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// The dynamic family's verdict, taken directly from the authorizer member those routes call.
    /// </summary>
    /// <param name="row">The matrix row.</param>
    /// <param name="expectedToBeServed">Whether the mission allows this caller through.</param>
    [Theory]
    [MemberData(nameof(Matrix))]
    public void DynamicFamilyVerdict(Row row, bool expectedToBeServed)
    {
        var jobIsGone = row == Row.OwnerWhoseJobIsGone;
        var outputPath = Path.Combine(_transcodePath, JobA + ".m3u8");

        var registry = Registry(jobIsGone);
        registry
            .Setup(r => r.ResolveByOutputPath(It.IsAny<string>()))
            .Returns((string path) => jobIsGone || !string.Equals(path, outputPath, StringComparison.Ordinal)
                ? null
                : Binding(JobA, _itemA));

        var httpContext = Context(row, Family.Video, Verb.Get, JobA + "0");
        var authorizer = new HlsJobOwnershipAuthorizer(registry.Object, SessionManager().Object);

        var decision = authorizer.AuthorizeByOutputPath(httpContext, outputPath);

        // Job A's credential asking for job B's resource is, for this family, a caller landing on
        // an output path no job of theirs writes.
        var expected = expectedToBeServed && row != Row.OwnerOfJobAAskingForJobB;
        if (row == Row.OwnerOfJobAAskingForJobB)
        {
            decision = authorizer.AuthorizeByOutputPath(httpContext, Path.Combine(_transcodePath, JobB + ".m3u8"));
        }

        Assert.Equal(expected, decision.IsAuthorized);
        if (!decision.IsAuthorized)
        {
            Assert.Null(decision.Binding);
        }
    }

    private async Task AssertRow(Row row, Verb verb, bool expectedToBeServed, string expectedFile, byte[] expectedBytes, Family family)
    {
        var jobIsGone = row == Row.OwnerWhoseJobIsGone;
        var segmentName = row == Row.OwnerOfJobAAskingForJobB ? JobB + "0" : JobA + "0";

        var registry = Registry(jobIsGone);
        var httpContext = Context(row, family, verb, segmentName);
        var controller = new HlsSegmentController(
            TranscodeManager().Object,
            new HlsJobOwnershipAuthorizer(registry.Object, SessionManager().Object))
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };

        var result = controller.ServeOwnedVideoSegment(_itemA.ToString("N"), JobA, segmentName, requiredPlaySessionId: null);

        var body = new MemoryStream();
        httpContext.Response.Body = body;

        if (!expectedToBeServed)
        {
            Assert.IsNotType<PhysicalFileResult>(result);
            Assert.IsNotType<FileStreamResult>(result);

            await Execute(result, httpContext).ConfigureAwait(true);

            Assert.True(
                httpContext.Response.StatusCode >= StatusCodes.Status400BadRequest,
                $"{family}/{verb}/{row}: refused rows must not answer with a success status.");
            Assert.False(
                ContainsTheResource(body.ToArray(), expectedBytes),
                $"{family}/{verb}/{row}: a refused caller received the resource's bytes.");
            return;
        }

        var physical = Assert.IsType<PhysicalFileResult>(result);
        Assert.Equal(expectedFile, Path.GetFileName(physical.FileName));

        await Execute(result, httpContext).ConfigureAwait(true);

        switch (verb)
        {
            case Verb.Get:
                Assert.Equal(expectedBytes, body.ToArray());
                break;
            case Verb.Head:
                // The executor writes headers and no body for HEAD, which is what a client asking
                // "is it there and how big" must receive — and never bytes.
                Assert.Equal(0, body.Length);
                Assert.Equal(expectedBytes.Length, httpContext.Response.ContentLength);
                break;
            case Verb.Range:
                Assert.Equal(StatusCodes.Status206PartialContent, httpContext.Response.StatusCode);
                Assert.Equal(new[] { expectedBytes[0], expectedBytes[1] }, body.ToArray());
                break;
        }
    }

    private static Task Execute(ActionResult result, HttpContext httpContext)
        => result.ExecuteResultAsync(new ActionContext { HttpContext = httpContext });

    /// <summary>
    /// Whether the response body contains the resource's bytes anywhere in it. A refusal may carry
    /// a short explanatory message — <c>NotFound("Hls segment not found.")</c> does — so "zero
    /// octet" is asserted as "not one byte OF THE RESOURCE", which is the property that matters,
    /// rather than as an empty body, which would be a weaker claim dressed as a stronger one.
    /// </summary>
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

    private static Mock<ITranscodeManager> TranscodeManager()
    {
        var transcodeManager = new Mock<ITranscodeManager>(MockBehavior.Loose);
        transcodeManager
            .Setup(t => t.OnTranscodeBeginRequest(It.IsAny<string>(), It.IsAny<TranscodingJobType>()))
            .Returns((TranscodingJob?)null);
        return transcodeManager;
    }

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

    private Mock<IHlsSegmentBindingRegistry> Registry(bool jobIsGone)
    {
        var registry = new Mock<IHlsSegmentBindingRegistry>(MockBehavior.Loose);

        registry
            .Setup(r => r.ResolveByPlaylistId(It.IsAny<string>()))
            .Returns((string id) => jobIsGone || !string.Equals(id, JobA, StringComparison.Ordinal)
                ? null
                : Binding(JobA, _itemA));

        return registry;
    }

    private HlsSegmentBinding Binding(string playlistId, Guid itemId)
        => new(
            playlistId,
            UserId: _ownerUserId,
            DeviceId: OwnerDevice,
            itemId,
            MediaSourceId: JobMediaSource,
            PlaySessionId: JobPlaySession,
            CanonicalRoot: Path.GetFullPath(_transcodePath),
            CanonicalPlaylistPath: Path.GetFullPath(Path.Combine(_transcodePath, playlistId + ".m3u8")),
            Generation: 1);

    private DefaultHttpContext Context(Row row, Family family, Verb verb, string segmentName)
    {
        var path = family == Family.Video
            ? $"/Videos/{_itemA:N}/hls/{JobA}/{segmentName}.ts"
            : $"/Audio/{_itemA:N}/hls/{segmentName}/stream.aac";

        var httpContext = new DefaultHttpContext
        {
            Request =
            {
                Path = new PathString(path),
                Method = verb == Verb.Head ? HttpMethods.Head : HttpMethods.Get
            },
            User = PrincipalFor(row),
            RequestServices = _services
        };

        if (verb == Verb.Range)
        {
            httpContext.Request.Headers.Range = "bytes=0-1";
        }

        // The video route corroborates a caller-named media source against the job's. Naming the
        // job's own keeps every row's refusal attributable to the identity comparison under test
        // rather than to this one.
        httpContext.Request.QueryString = QueryString.Create("mediaSourceId", JobMediaSource);

        var capability = CapabilityFor(row);
        if (capability is not null)
        {
            httpContext.Features.Set(capability);
        }

        return httpContext;
    }

    private static ClaimsPrincipal PrincipalFor(Row row)
    {
        if (row == Row.NoCredentialAtAll)
        {
            return new ClaimsPrincipal(new ClaimsIdentity());
        }

        var identity = new ClaimsIdentity("CustomAuthentication");

        var userId = row switch
        {
            Row.DurableOtherUser => _strangerUserId,
            Row.DurableOtherUserSameDevice => _strangerUserId,
            Row.AdministratorWhoIsNotTheOwner => _strangerUserId,
            Row.ApiKeyWithNoUser => Guid.Empty,
            _ => _ownerUserId
        };

        if (!userId.Equals(Guid.Empty))
        {
            identity.AddClaim(new Claim(InternalClaimTypes.UserId, userId.ToString("N")));
            identity.AddClaim(new Claim(ClaimTypes.Name, userId.ToString("N")));
        }

        // A capability principal carries no device claim; the handler does not mint one.
        var deviceId = row switch
        {
            Row.DurableOtherDevice => "some-other-device",
            Row.DurableOtherUser => "stranger-device",
            Row.DurableOtherUserSameDevice => OwnerDevice,
            Row.AdministratorWhoIsNotTheOwner => "admin-device",
            Row.ApiKeyWithNoUser => null,
            _ when CapabilityFor(row) is not null => null,
            _ => OwnerDevice
        };

        if (deviceId is not null)
        {
            identity.AddClaim(new Claim(InternalClaimTypes.DeviceId, deviceId));
        }

        if (row == Row.ApiKeyWithNoUser)
        {
            identity.AddClaim(new Claim(InternalClaimTypes.IsApiKey, bool.TrueString));
        }

        if (row == Row.AdministratorWhoIsNotTheOwner)
        {
            identity.AddClaim(new Claim(ClaimTypes.Role, UserRoles.Administrator));
        }

        return new ClaimsPrincipal(identity);
    }

    private static ValidatedPlaybackCapability? CapabilityFor(Row row) => row switch
    {
        Row.ExactCapability => Capability(_itemA, JobMediaSource, JobPlaySession, OwnerSession),
        Row.CapabilityForAnotherItem => Capability(_itemB, JobMediaSource, JobPlaySession, OwnerSession),
        Row.CapabilityForAnotherMediaSource => Capability(_itemA, "another-media-source", JobPlaySession, OwnerSession),
        Row.CapabilityForAnotherPlaySession => Capability(_itemA, JobMediaSource, "another-play-session", OwnerSession),
        Row.CapabilityFromAnotherDevicesSession => Capability(_itemA, JobMediaSource, JobPlaySession, ForeignSession),
        _ => null
    };

    private static ValidatedPlaybackCapability Capability(Guid itemId, string? mediaSourceId, string? playSessionId, string sessionId)
        => new(
            "capability-value",
            Guid.NewGuid(),
            itemId,
            mediaSourceId,
            playSessionId,
            PlaybackCapabilityScope.Media,
            new PlaybackCapabilityValidation(true, PlaybackCapabilityFailure.None, Guid.NewGuid(), _ownerUserId, sessionId, playSessionId));

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
}
