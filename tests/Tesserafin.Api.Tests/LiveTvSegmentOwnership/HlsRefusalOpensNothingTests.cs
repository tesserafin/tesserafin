#pragma warning disable CS0618 // tesserafin#129 step 7: the legacy route under test is deprecated, and this test goes with it.
using System;
using System.IO;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Api.Auth.HlsJobOwnership;
using Tesserafin.Api.Constants;
using Tesserafin.Api.Controllers;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Controller.Session;
using Xunit;

namespace Tesserafin.Api.Tests.LiveTvSegmentOwnership;

/// <summary>
/// A refused caller must receive zero bytes, and the server must not have opened the file to tell
/// them so (#153-LTV-R3, Phase 2 "un test instrumenté doit prouver zéro ouverture de fichier sur
/// refus").
/// </summary>
/// <remarks>
/// HOW THE OPEN IS DETECTED, AND WHY THIS IS A MEASUREMENT AND NOT AN ASSERTION. The segment file
/// is held for the whole invocation with <see cref="FileShare.None"/>. Any attempt to open it —
/// by the action, or by the result executor afterwards — throws <see cref="IOException"/> in the
/// same process. So "nothing opened it" is observed as "no exception was raised and no byte
/// reached the response body", not claimed.
///
/// THE ANTI-VACUITY CONTROL IS THE POINT OF THE LOCK. <c>TheLockIsLoadBearing</c> takes the
/// owner's own successful result and executes it against the same locked file: it throws. Without
/// that control, "the refusal did not throw" would be equally consistent with a lock that does
/// nothing on this platform.
/// </remarks>
public sealed class HlsRefusalOpensNothingTests : IDisposable
{
    private const string JobA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string OwnerDevice = "owner-device";

    private static readonly Guid _itemA = new("11111111111111111111111111111111");
    private static readonly Guid _ownerUserId = new("aaaaaaaa11114444888800000000cccc");
    private static readonly Guid _strangerUserId = new("dddddddd2222555599990000eeeeffff");

    private readonly string _transcodePath;
    private readonly string _segmentPath;

    public HlsRefusalOpensNothingTests()
    {
        _transcodePath = Path.Combine(Path.GetTempPath(), "ltvr3-zero-open-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_transcodePath);
        File.WriteAllText(Path.Combine(_transcodePath, JobA + ".m3u8"), "#EXTM3U\n");
        _segmentPath = Path.Combine(_transcodePath, JobA + "0.ts");
        File.WriteAllBytes(_segmentPath, new byte[] { 0x47, 0x41, 0x42, 0x43 });
    }

    /// <summary>
    /// A stranger's request, made while the file cannot be opened at all: the answer is a refusal,
    /// no exception is raised, no file result is produced and the response body stays empty.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    [Fact]
    public async Task ARefusedCaller_GetsZeroBytesAndTheFileIsNeverOpened()
    {
        using var exclusive = new FileStream(_segmentPath, FileMode.Open, FileAccess.Read, FileShare.None);

        var (result, httpContext) = Invoke(_strangerUserId, "stranger-device");

        Assert.IsNotType<PhysicalFileResult>(result);
        Assert.IsNotType<FileStreamResult>(result);

        var body = new MemoryStream();
        httpContext.Response.Body = body;
        await ExecuteAsync(result, httpContext).ConfigureAwait(true);

        Assert.Equal(StatusCodes.Status401Unauthorized, httpContext.Response.StatusCode);
        Assert.Equal(0, body.Length);
    }

    /// <summary>
    /// The control that makes the test above mean something: the owner IS given a file result, and
    /// executing that result against the locked file throws. The lock is therefore load-bearing,
    /// and the refusal's silence is the absence of an open rather than the absence of a check.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    [Fact]
    public async Task TheLockIsLoadBearing()
    {
        using var exclusive = new FileStream(_segmentPath, FileMode.Open, FileAccess.Read, FileShare.None);

        var (result, httpContext) = Invoke(_ownerUserId, OwnerDevice);

        var physical = Assert.IsType<PhysicalFileResult>(result);
        Assert.Equal(_segmentPath, physical.FileName);

        httpContext.Response.Body = new MemoryStream();
        await Assert.ThrowsAsync<IOException>(() => ExecuteAsync(result, httpContext)).ConfigureAwait(true);
    }

    private static Task ExecuteAsync(ActionResult result, HttpContext httpContext)
    {
        if (result is PhysicalFileResult physical)
        {
            // The real framework executor, so that "executing a file result opens the file" is the
            // framework's behaviour rather than this test's imitation of it.
            var executor = new PhysicalFileResultExecutor(NullLoggerFactory.Instance);
            return executor.ExecuteAsync(new ActionContext { HttpContext = httpContext }, physical);
        }

        return result.ExecuteResultAsync(new ActionContext { HttpContext = httpContext });
    }

    private (ActionResult Result, HttpContext Context) Invoke(Guid callerUserId, string callerDeviceId)
    {
        var binding = new HlsSegmentBinding(
            JobA,
            UserId: _ownerUserId,
            DeviceId: OwnerDevice,
            _itemA,
            MediaSourceId: null,
            PlaySessionId: null,
            CanonicalRoot: Path.GetFullPath(_transcodePath),
            CanonicalPlaylistPath: Path.GetFullPath(Path.Combine(_transcodePath, JobA + ".m3u8")),
            Generation: 1);

        var registry = new Mock<IHlsSegmentBindingRegistry>(MockBehavior.Loose);
        registry.Setup(r => r.ResolveByPlaylistId(JobA)).Returns(binding);

        var transcodeManager = new Mock<ITranscodeManager>(MockBehavior.Loose);
        transcodeManager
            .Setup(t => t.OnTranscodeBeginRequest(It.IsAny<string>(), It.IsAny<TranscodingJobType>()))
            .Returns((TranscodingJob?)null);

        var sessionManager = new Mock<ISessionManager>(MockBehavior.Loose);
        sessionManager.SetupGet(m => m.Sessions).Returns(Array.Empty<SessionInfo>());

        var identity = new ClaimsIdentity(
            new[]
            {
                new Claim(InternalClaimTypes.UserId, callerUserId.ToString("N")),
                new Claim(InternalClaimTypes.DeviceId, callerDeviceId)
            },
            "CustomAuthentication");

        var httpContext = new DefaultHttpContext
        {
            Request = { Path = new PathString($"/Videos/{_itemA:N}/hls/{JobA}/{JobA}0.ts") },
            User = new ClaimsPrincipal(identity),

            // StatusCodeResult.ExecuteResult resolves an ILoggerFactory off the request. Nothing
            // else is resolved, so the smallest possible provider is the honest one here.
            RequestServices = new LoggerFactoryOnlyServices()
        };

        var controller = new HlsSegmentController(
            transcodeManager.Object,
            new HlsJobOwnershipAuthorizer(registry.Object, sessionManager.Object))
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };

        return (controller.GetHlsVideoSegmentLegacy(_itemA.ToString("N"), JobA, JobA + "0", "ts"), httpContext);
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

    private sealed class LoggerFactoryOnlyServices : IServiceProvider
    {
        public object? GetService(Type serviceType)
            => serviceType == typeof(ILoggerFactory) ? NullLoggerFactory.Instance : null;
    }
}
