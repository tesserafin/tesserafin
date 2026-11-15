using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Claims;
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

namespace Tesserafin.Api.Tests.LiveTvSegmentOwnership;

/// <summary>
/// Two live audio jobs at once: the segment name must select the RIGHT one, and the caller must be
/// compared with THAT one (#153-LTV-R5, finding F3).
/// </summary>
/// <remarks>
/// WHY THIS FILE EXISTS. R4 Phase 2 inverted the predicate in
/// <c>TranscodeManager.ResolveBySegmentName</c> — so that a segment name selects the WRONG job —
/// and all 69 tests stayed green, twice, the second time with a confirmed 23-project rebuild. The
/// two tests that were supposed to cover the two-job case could not fail: the fixture wrote
/// <c>JobA + "0.aac"</c> and never <c>JobB + "0.aac"</c>, which is the exact file
/// <c>AnAudioSegmentNameOwnedByNoJob_IsNeverServed</c> asserted was not served, and the registry
/// double resolved job A's binding or nothing and never modelled a second live job at all. Nothing
/// in the repository could distinguish "the right job was selected" from "the only job was
/// selected".
///
/// WHAT IS REAL HERE, AND WHY. The registry is the REAL <see cref="TranscodeManager"/>, holding two
/// REAL <see cref="TranscodingJob"/> entries, so <c>ResolveBySegmentName</c>'s own predicate and
/// <c>SelectHlsJob</c>'s own scan are what decides. The two jobs differ in every dimension the
/// selection could confuse: owner, device, item, media source, play session, playlist identifier,
/// segment name, file on disk and the BYTES in that file. Both files exist and their contents
/// differ, which is asserted before anything else, so a missing fixture cannot produce a green.
///
/// The jobs are placed in the manager's private list by reflection. There is no public way to add
/// one — <c>StartFfMpeg</c> launches a real encoder — and inventing a production seam to make a
/// test easier would be changing the thing under test. The reflection is asserted, not assumed: if
/// the field is renamed, <see cref="Jobs"/> throws rather than silently testing nothing.
///
/// NO WEB CLAIM IS MADE. R4 Phase 5 found no producer of this route on six searched surfaces, so
/// there is no runtime web assertion to make; the proof is controller-level with real bytes.
/// </remarks>
public sealed class AudioSegmentJobSelectionTests : IDisposable
{
    private const string JobAPlaylist = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string JobBPlaylist = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string DeviceA = "device-of-owner-a";
    private const string DeviceB = "device-of-owner-b";
    private const string MediaSourceA = "6d5da76e3955fd1005f75c496c371521";
    private const string MediaSourceB = "0e1e0dfd1d7bcbc4cd9d3d3a9d0b1c2d";
    private const string PlaySessionA = "306f71094f36456f9d0dc6e7b12b8a6b";
    private const string PlaySessionB = "9b1c4e2a77db4f0e9d2f5a6b8c0d1e2f";

    private static readonly Guid _itemA = new("11111111111111111111111111111111");
    private static readonly Guid _itemB = new("22222222222222222222222222222222");
    private static readonly Guid _ownerA = new("aaaaaaaa11114444888800000000cccc");
    private static readonly Guid _ownerB = new("dddddddd2222555599990000eeeeffff");

    private static readonly byte[] _bytesA = { 0xFF, 0xF1, 0x50, 0x80, 0x0A, 0x0A, 0x0A, 0x0A };
    private static readonly byte[] _bytesB = { 0xFF, 0xF1, 0x50, 0x80, 0x0B, 0x0B, 0x0B, 0x0B };

    private readonly string _root;
    private readonly ServiceProvider _services;
    private readonly TranscodeManager _transcodeManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="AudioSegmentJobSelectionTests"/> class.
    /// </summary>
    public AudioSegmentJobSelectionTests()
    {
        _services = new ServiceCollection()
            .AddLogging()
            .AddMvcCore()
            .Services
            .BuildServiceProvider();

        _root = Path.Combine(Path.GetTempPath(), "ltvr5-audio-selection-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        File.WriteAllText(Path.Combine(_root, JobAPlaylist + ".m3u8"), "#EXTM3U\n");
        File.WriteAllText(Path.Combine(_root, JobBPlaylist + ".m3u8"), "#EXTM3U\n");
        File.WriteAllBytes(Path.Combine(_root, JobAPlaylist + "0.aac"), _bytesA);
        File.WriteAllBytes(Path.Combine(_root, JobBPlaylist + "0.aac"), _bytesB);

        _transcodeManager = CreateManager();
        Jobs(_transcodeManager).Add(Job(JobAPlaylist, _ownerA, DeviceA, _itemA, MediaSourceA, PlaySessionA, 1));
        Jobs(_transcodeManager).Add(Job(JobBPlaylist, _ownerB, DeviceB, _itemB, MediaSourceB, PlaySessionB, 2));
    }

    /// <summary>
    /// The fixture itself, asserted before any boundary claim is made on top of it: two jobs, two
    /// files, two different byte sequences.
    /// </summary>
    /// <remarks>
    /// This is the assertion whose absence made the previous two-job tests vacuous. If either file
    /// is missing, or the two carry the same bytes, every "the right file was served" assertion
    /// below would be satisfiable by the wrong file.
    /// </remarks>
    [Fact]
    public void TheFixtureReallyHasTwoJobsAndTwoDifferentFiles()
    {
        Assert.Equal(2, Jobs(_transcodeManager).Count);
        Assert.True(File.Exists(Path.Combine(_root, JobAPlaylist + "0.aac")));
        Assert.True(File.Exists(Path.Combine(_root, JobBPlaylist + "0.aac")));
        Assert.NotEqual(_bytesA, _bytesB);
        Assert.Equal(_bytesA, File.ReadAllBytes(Path.Combine(_root, JobAPlaylist + "0.aac")));
        Assert.Equal(_bytesB, File.ReadAllBytes(Path.Combine(_root, JobBPlaylist + "0.aac")));
    }

    /// <summary>
    /// The registry selects the job whose playlist identifier prefixes the segment name, and
    /// projects THAT job's binding — not the first job in the list.
    /// </summary>
    [Fact]
    public void ASegmentNameSelectsTheJobThatWritesIt_NotTheFirstJobInTheRegistry()
    {
        var forA = _transcodeManager.ResolveBySegmentName(JobAPlaylist + "0");
        var forB = _transcodeManager.ResolveBySegmentName(JobBPlaylist + "0");

        Assert.NotNull(forA);
        Assert.NotNull(forB);
        Assert.Equal(JobAPlaylist, forA!.PlaylistId);
        Assert.Equal(_ownerA, forA.UserId);
        Assert.Equal(DeviceA, forA.DeviceId);
        Assert.Equal(_itemA, forA.ItemId);
        Assert.Equal(JobBPlaylist, forB!.PlaylistId);
        Assert.Equal(_ownerB, forB.UserId);
        Assert.Equal(DeviceB, forB.DeviceId);
        Assert.Equal(_itemB, forB.ItemId);
    }

    /// <summary>
    /// Each owner reaches their own job's audio segment, and receives that job's own bytes.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    [Fact]
    public async Task EachOwnerReachesTheirOwnJobsAudioSegment_WithThatJobsOwnBytes()
    {
        await AssertServed(_ownerA, DeviceA, _itemA, JobAPlaylist + "0", _bytesA).ConfigureAwait(true);
        await AssertServed(_ownerB, DeviceB, _itemB, JobBPlaylist + "0", _bytesB).ConfigureAwait(true);
    }

    /// <summary>
    /// The owner of one job, naming the OTHER job's segment, is refused and receives none of that
    /// job's bytes — even though the file is on disk and the caller is an authenticated owner of a
    /// live job of their own.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    [Fact]
    public async Task TheWrongOwnerIsRefusedTheOtherJobsAudioSegment()
    {
        await AssertRefused(_ownerA, DeviceA, _itemB, JobBPlaylist + "0", _bytesB).ConfigureAwait(true);
        await AssertRefused(_ownerB, DeviceB, _itemA, JobAPlaylist + "0", _bytesA).ConfigureAwait(true);
    }

    /// <summary>
    /// The order the two jobs sit in the registry decides nothing. The same four questions are
    /// asked of a manager whose job list was built the other way round.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    [Fact]
    public async Task TheRegistryOrderChangesNothing()
    {
        using var reversed = CreateManager();
        Jobs(reversed).Add(Job(JobBPlaylist, _ownerB, DeviceB, _itemB, MediaSourceB, PlaySessionB, 1));
        Jobs(reversed).Add(Job(JobAPlaylist, _ownerA, DeviceA, _itemA, MediaSourceA, PlaySessionA, 2));

        Assert.Equal(JobAPlaylist, reversed.ResolveBySegmentName(JobAPlaylist + "0")!.PlaylistId);
        Assert.Equal(JobBPlaylist, reversed.ResolveBySegmentName(JobBPlaylist + "0")!.PlaylistId);

        await AssertServed(_ownerA, DeviceA, _itemA, JobAPlaylist + "0", _bytesA, reversed).ConfigureAwait(true);
        await AssertServed(_ownerB, DeviceB, _itemB, JobBPlaylist + "0", _bytesB, reversed).ConfigureAwait(true);
        await AssertRefused(_ownerA, DeviceA, _itemB, JobBPlaylist + "0", _bytesB, reversed).ConfigureAwait(true);
        await AssertRefused(_ownerB, DeviceB, _itemA, JobAPlaylist + "0", _bytesA, reversed).ConfigureAwait(true);
    }

    /// <summary>
    /// A segment name no live job writes resolves nothing, and the file on disk beside it is not a
    /// fallback.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    [Fact]
    public async Task ASegmentNameNoLiveJobWrites_ResolvesNothingAndServesNothing()
    {
        const string Orphan = "cccccccccccccccccccccccccccccccc";
        var orphanBytes = new byte[] { 0xFF, 0xF1, 0x50, 0x80, 0x0C, 0x0C, 0x0C, 0x0C };
        var orphanFile = Path.Combine(_root, Orphan + "0.aac");
        await File.WriteAllBytesAsync(orphanFile, orphanBytes, TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Null(_transcodeManager.ResolveBySegmentName(Orphan + "0"));

        // The file really is there, so the refusal is a decision and not an absence.
        Assert.True(File.Exists(orphanFile));
        Assert.Equal(orphanBytes, await File.ReadAllBytesAsync(orphanFile, TestContext.Current.CancellationToken).ConfigureAwait(true));
        await AssertRefused(_ownerA, DeviceA, _itemA, Orphan + "0", orphanBytes).ConfigureAwait(true);
    }

    private static List<TranscodingJob> Jobs(TranscodeManager manager)
    {
        var field = typeof(TranscodeManager).GetField("_activeTranscodingJobs", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "TranscodeManager no longer has a private _activeTranscodingJobs field. This fixture seeds the "
                + "live job list directly because there is no public way to add a job without launching ffmpeg; "
                + "if the field has moved, this test must be re-pointed rather than silently testing nothing.");

        return (List<TranscodingJob>)(field.GetValue(manager)
            ?? throw new InvalidOperationException("TranscodeManager's job list was null."));
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

    private static ClaimsPrincipal Principal(Guid userId, string deviceId)
    {
        var identity = new ClaimsIdentity("CustomAuthentication");
        identity.AddClaim(new Claim(InternalClaimTypes.UserId, userId.ToString("N")));
        identity.AddClaim(new Claim(ClaimTypes.Name, userId.ToString("N")));
        identity.AddClaim(new Claim(InternalClaimTypes.DeviceId, deviceId));
        return new ClaimsPrincipal(identity);
    }

    private static TranscodeManager CreateManager()
    {
        var appPaths = Mock.Of<IServerApplicationPaths>();
        var configurationManager = new Mock<IServerConfigurationManager>();
        configurationManager
            .Setup(x => x.GetConfiguration("encoding"))
            .Returns(new EncodingOptions
            {
                // Deliberately a path that does not exist. The constructor's cache purge then sees
                // no directory and returns immediately, so the fixture's own files - which live
                // elsewhere and are named by each job's absolute Path - are never touched.
                TranscodingTempPath = Path.Combine(Path.GetTempPath(), "ltvr5-never-created-" + Guid.NewGuid().ToString("N"))
            });
        configurationManager.Setup(x => x.CommonApplicationPaths).Returns(appPaths);

        return new TranscodeManager(
            NullLoggerFactory.Instance,
            Mock.Of<IFileSystem>(),
            appPaths,
            configurationManager.Object,
            Mock.Of<IUserManager>(),
            Mock.Of<ISessionManager>(),
            new EncodingHelper(
                appPaths,
                Mock.Of<IMediaEncoder>(),
                Mock.Of<ISubtitleEncoder>(),
                Mock.Of<IConfiguration>(),
                configurationManager.Object,
                Mock.Of<IPathManager>()),
            Mock.Of<IMediaEncoder>(),
            Mock.Of<IMediaSourceManager>(),
            Mock.Of<IAttachmentExtractor>(),
            new Tesserafin.MediaEncoding.Encoder.FfmpegProcessRunner());
    }

    private TranscodingJob Job(string playlistId, Guid ownerUserId, string ownerDeviceId, Guid itemId, string mediaSourceId, string playSessionId, long generation)
        => new(NullLogger<TranscodingJob>.Instance)
        {
            Type = TranscodingJobType.Hls,
            Path = Path.Combine(_root, playlistId + ".m3u8"),
            UserId = ownerUserId,
            OwnerDeviceId = ownerDeviceId,
            ItemId = itemId,
            MediaSourceId = mediaSourceId,
            PlaySessionId = playSessionId,
            Generation = generation
        };

    private DefaultHttpContext Context(Guid userId, string deviceId, Guid itemId, string segmentName)
        => new()
        {
            Request = { Path = new PathString($"/Audio/{itemId:N}/hls/{segmentName}/stream.aac") },
            User = Principal(userId, deviceId),
            RequestServices = _services
        };

    private HlsSegmentController Controller(HttpContext httpContext, TranscodeManager manager)
    {
        var sessionManager = new Mock<ISessionManager>(MockBehavior.Loose);
        sessionManager.SetupGet(m => m.Sessions).Returns(Array.Empty<SessionInfo>());

        // The REAL authorizer over the REAL registry. Both halves of the property under test -
        // which job the name selects, and whether this caller is that job's owner - are production
        // code here.
        return new HlsSegmentController(manager, new HlsJobOwnershipAuthorizer(manager, sessionManager.Object))
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };
    }

    private async Task AssertServed(Guid userId, string deviceId, Guid itemId, string segmentName, byte[] expectedBytes, TranscodeManager? manager = null)
    {
        var httpContext = Context(userId, deviceId, itemId, segmentName);
        var result = Controller(httpContext, manager ?? _transcodeManager)
            .GetHlsAudioSegmentLegacy(itemId.ToString("N"), segmentName);

        var physical = Assert.IsType<PhysicalFileResult>(result);
        Assert.Equal(segmentName + ".aac", Path.GetFileName(physical.FileName));

        var body = new MemoryStream();
        httpContext.Response.Body = body;
        await result.ExecuteResultAsync(new ActionContext { HttpContext = httpContext }).ConfigureAwait(true);

        Assert.Equal(expectedBytes, body.ToArray());
    }

    private async Task AssertRefused(Guid userId, string deviceId, Guid itemId, string segmentName, byte[] forbiddenBytes, TranscodeManager? manager = null)
    {
        var httpContext = Context(userId, deviceId, itemId, segmentName);
        var result = Controller(httpContext, manager ?? _transcodeManager)
            .GetHlsAudioSegmentLegacy(itemId.ToString("N"), segmentName);

        Assert.IsNotType<PhysicalFileResult>(result);
        Assert.IsNotType<FileStreamResult>(result);

        var body = new MemoryStream();
        httpContext.Response.Body = body;
        await result.ExecuteResultAsync(new ActionContext { HttpContext = httpContext }).ConfigureAwait(true);

        Assert.True(
            httpContext.Response.StatusCode >= StatusCodes.Status400BadRequest,
            $"a refused caller for {segmentName} received a success status.");
        Assert.False(
            ContainsTheResource(body.ToArray(), forbiddenBytes),
            $"a refused caller for {segmentName} received the resource's bytes.");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _transcodeManager.Dispose();
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
}
