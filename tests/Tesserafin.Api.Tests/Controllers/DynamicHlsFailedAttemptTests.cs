#pragma warning disable CS0618 // tesserafin#129 step 7: the legacy route under test is deprecated, and this test goes with it.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
using Tesserafin.Controller.Entities.Movies;
using Tesserafin.Controller.IO;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Controller.Session;
using Tesserafin.Controller.Streaming;
using Tesserafin.Controller.Trickplay;
using Tesserafin.Data.Entities;
using Tesserafin.MediaEncoding.Hls.Playlist;
using Tesserafin.MediaEncoding.Playback;
using Tesserafin.MediaEncoding.Transcoding;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.Dto;
using Tesserafin.Model.Entities;
using Tesserafin.Model.IO;
using Tesserafin.Model.MediaInfo;
using Tesserafin.Model.Session;
using Tesserafin.Server.Core.IO;
using Xunit;

namespace Tesserafin.Api.Tests.Controllers;

/// <summary>
/// tesserafin#289. What a segment request does while the attempt that was writing its output is
/// failing: it serves, it says the attempt failed, or it starts another - and which of the three
/// must not depend on how the request and the failure happen to interleave.
/// </summary>
/// <remarks>
/// THE REAL PATH. The real <see cref="DynamicHlsController"/> action, the real
/// <see cref="TranscodeManager"/>, the real ownership authorizer and a real child process. The
/// process stands in for ffmpeg only in that it writes no media: it lives until it is told how to
/// end, and prints the line it is given on stderr, which the real classifier then reads. The output
/// files are written by the test, which is what makes "the segment exists" a fact the test states
/// rather than something it waits for.
///
/// NO DELAY DECIDES AN OUTCOME. Every interleaving is held open by a <see cref="Gate"/>: a named
/// place in the file system calls the controller and the manager make, where the calling thread
/// stops until the test opens it. The waits that remain are bounded waits for an event that must
/// happen (<see cref="Patience"/>), and a test that outlives one fails.
/// </remarks>
public sealed partial class DynamicHlsFailedAttemptTests : IDisposable
{
    private const string DeviceLost = "amdgpu: The CS has cancelled because the context is lost. This context is innocent.";
    private const string OwnerDevice = "owner-device";
    private const string SessionA = "11111111111111111111111111111111";
    private const string SessionB = "22222222222222222222222222222222";
    private const string MediaSourceId = "6d5da76e3955fd1005f75c496c371521";
    private const int Init = -1;

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FailureAnswerLifetime = TimeSpan.FromSeconds(30);
    private static readonly Guid _item = new("11111111111111111111111111111111");
    private static readonly Guid _owner = new("aaaaaaaa11114444888800000000cccc");
    private static readonly Guid _stranger = new("dddddddd2222555599990000eeeeffff");

    private readonly string _root;
    private readonly string _transcodePath;
    private readonly ServiceProvider _services;
    private readonly GatedFileSystem _fileSystem;
    private readonly Clock _clock = new();
    private readonly ConcurrentQueue<Start> _starts = new();
    private readonly Mock<ISessionManager> _sessionManager = new();
    private readonly object _sync = new();
    private Gate? _reportGate;
    private TaskCompletionSource<Start> _nextStart = NewSignal<Start>();
    private EncodingOptions? _options;
    private TranscodeManager? _manager;
    private Gate? _lockGate;
    private Gate? _verdictGate;
    private Gate? _stopGate;
    private Gate? _stopDecisionGate;
    private Gate? _beforeStartGate;
    private Func<DynamicHlsController>? _newController;
    private Func<HlsSegmentController>? _newLegacyController;
    private ITranscodeManager? _gated;

    public DynamicHlsFailedAttemptTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "p2r2-" + Guid.NewGuid().ToString("N"));
        _transcodePath = Path.Combine(_root, "transcodes");
        Directory.CreateDirectory(_transcodePath);
        Directory.CreateDirectory(Path.Combine(_root, "log"));

        _services = new ServiceCollection().AddLogging().AddMvcCore().Services.BuildServiceProvider();
        _fileSystem = new GatedFileSystem(Mock.Of<IServerApplicationPaths>(p => p.TempDirectory == _root));
    }

    // ---------------------------------------------------------------- the race of the issue

    [Fact]
    public async Task SegmentSeenOnDisk_ThenTheAttemptFailsAndIsRemoved_TheRequestIsToldSo()
    {
        var attempt = await StartPlayback(hardware: true, segments: 6);

        // The request has looked once and found its segment. Everything else happens before it looks again.
        var afterFirstLook = _fileSystem.PauseAfterLook("late", attempt.Segment(2));
        var late = Request(2, actor: "late");
        await afterFirstLook.Reached;

        await FailAndWait(attempt, 134, DeviceLost);
        afterFirstLook.Open();

        AssertFailed(await Settled(late), "software");
        Assert.Single(_starts);
        Assert.Empty(attempt.FilesOnDisk());
    }

    [Fact]
    public async Task NothingToSayBeforeTheLock_ThenTheAttemptFailsAndIsRemoved_TheRequestStartsNothing()
    {
        var attempt = await StartPlayback(hardware: true, segments: 6);

        // Segment 8 is not written yet. The request has looked, found a healthy attempt, and is
        // about to take the lock; the whole failure happens before it gets it.
        var beforeLock = new Gate("the lock");
        _lockGate = beforeLock;
        var request = Request(8);
        await beforeLock.Reached;

        await FailAndWait(attempt, 134, DeviceLost);
        Assert.Empty(attempt.FilesOnDisk());
        beforeLock.Open();

        AssertFailed(await Settled(request), "software");
        Assert.Single(_starts);
    }

    [Fact]
    public async Task RequestArrivingWhileTheFailedOutputIsBeingRemoved_IsToldSo()
    {
        var attempt = await StartPlayback(hardware: true, segments: 6);

        // The removal has taken the requested segment and stops there, the later ones still on disk.
        _fileSystem.RemoveFirst = attempt.Segment(2);
        var midRemoval = _fileSystem.PauseAfterRemoval(attempt.Segment(2));
        Fail(attempt, 134, DeviceLost);
        await midRemoval.Reached;

        var response = await Settled(Request(2));
        AssertFailed(response, "software");

        // The removal holds the lock a start on this output needs: nothing can begin there until it is done.
        using (var giveUp = new CancellationTokenSource())
        {
            var startersLock = _manager!.LockAsync(attempt.Job.Path!, giveUp.Token).AsTask();
            Assert.False(startersLock.IsCompleted, "the failed output is being removed outside the lock a start takes");
            await giveUp.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => startersLock);
        }

        midRemoval.Open();
        await attempt.Ended;

        Assert.Single(_starts);
        Assert.Empty(attempt.FilesOnDisk());
    }

    [Fact]
    public async Task FailurePublishedOnceTheRequestHoldsTheLock_IsAnsweredNotRestarted()
    {
        var attempt = await StartPlayback(hardware: true, segments: 6);

        // Segment 8 is not written yet and is near enough to be waited for. The request has found
        // nothing to say before the lock, has taken it, and has looked again.
        var secondLook = _fileSystem.PauseAfterLook("locked", attempt.Segment(8), occurrence: 2);
        var locked = Request(8, actor: "locked");
        await secondLook.Reached;

        var published = PauseAtNextTranscodingReport();
        Fail(attempt, 134, DeviceLost);
        await published.Reached;
        published.Open();
        secondLook.Open();

        AssertFailed(await Settled(locked), "software");
        await attempt.Ended;
        Assert.Single(_starts);
        Assert.Empty(attempt.FilesOnDisk());
    }

    [Fact]
    public async Task SeveralRequestsDuringTheTransition_NoneStartsAnything()
    {
        var attempt = await StartPlayback(hardware: true, segments: 6);

        var afterFirstLook = _fileSystem.PauseAfterLook("early", attempt.Segment(1));
        var early = Request(1, actor: "early");
        await afterFirstLook.Reached;

        _fileSystem.RemoveFirst = attempt.Segment(3);
        var midRemoval = _fileSystem.PauseAfterRemoval(attempt.Segment(3));
        Fail(attempt, 134, DeviceLost);
        await midRemoval.Reached;

        // One already removed, one never written, one still on disk, the initialisation segment,
        // and the one that looked before any of it happened.
        var during = new[] { Request(3), Request(9), Request(5), Request(Init) };
        afterFirstLook.Open();
        var responses = new List<Response>();
        foreach (var request in during.Append(early))
        {
            responses.Add(await Settled(request));
        }

        midRemoval.Open();
        await attempt.Ended;

        Assert.All(responses, r => AssertFailed(r, "software"));
        Assert.Single(_starts);
    }

    [Theory]
    [InlineData(true, "software")]
    [InlineData(false, "none")]
    public async Task AttemptFailsWhileARequestWaitsForTheNextSegment_TheRequestIsToldSo(bool hardware, string recovery)
    {
        // Segment 2 is on disk and 3 is not: 2 is the one being written, and a request for it waits.
        var attempt = await StartPlayback(hardware, segments: 3, last: "TRUNCATED");

        // More looks at the next segment than deciding takes: this one is made from the wait.
        var waiting = _fileSystem.PauseAfterLook("waiter", attempt.Segment(3), occurrence: 4);
        var waiter = Request(2, actor: "waiter");
        await waiting.Reached;

        await FailAndWait(attempt, hardware ? 134 : 1, hardware ? DeviceLost : "something nobody recognises");
        waiting.Open();

        var response = await Settled(waiter);
        AssertFailed(response, recovery);
        Assert.DoesNotContain("TRUNCATED", response.Text, StringComparison.Ordinal);
        Assert.Single(_starts);
    }

    // ---------------------------------------------------------------- the truncated last segment

    [Fact]
    public async Task LastSegmentOfAFailedAttempt_IsNotServed_AndTheOnesBeforeItAre()
    {
        // A failure no fallback is granted for removes nothing: what the attempt finished stays.
        var attempt = await StartPlayback(hardware: false, segments: 3, last: "TRUNCATED");
        await FailAndWait(attempt, 1, "something nobody recognises");

        var finished = await Settled(Request(1));
        var last = await Settled(Request(2));
        var never = await Settled(Request(3));

        Assert.Equal(StatusCodes.Status200OK, finished.Status);
        Assert.Equal("segment 1", finished.Text);
        AssertFailed(last, "none");
        Assert.DoesNotContain("TRUNCATED", last.Text, StringComparison.Ordinal);
        AssertFailed(never, "none");
        Assert.Single(_starts);
    }

    [Fact]
    public async Task AttemptThatEndedNormally_ServesItsLastSegment()
    {
        var attempt = await StartPlayback(hardware: true, segments: 3, last: "the real last one");
        await EndAndWait(attempt, "end 0");

        var last = await Settled(Request(2));

        Assert.Equal(StatusCodes.Status200OK, last.Status);
        Assert.Equal("the real last one", last.Text);
        Assert.Single(_starts);
    }

    // ---------------------------------------------------------------- expiry and release

    [Fact]
    public async Task UntilTheFailureAnswerExpires_NothingStarts_AndAfterItOneFreshAttemptDoes()
    {
        var attempt = await StartPlayback(hardware: true, segments: 6);
        await FailAndWait(attempt, 134, DeviceLost);

        _clock.Advance(FailureAnswerLifetime);
        AssertFailed(await Settled(Request(2)), "software");
        Assert.Single(_starts);

        _clock.Advance(TimeSpan.FromTicks(1));
        var next = NextStart();
        var request = Request(2);
        var successor = await next.WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.Equal(attempt.Job.Path, successor.Job.Path);
        Assert.Empty(successor.FilesWhenStarted);
        Assert.DoesNotContain("-init_hw_device", successor.Command, StringComparison.Ordinal);

        successor.Write(2, "software 2");
        successor.Write(3, "software 3");
        var response = await request.WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status200OK, response.Status);
        Assert.Equal("software 2", response.Text);
        Assert.Equal(2, _starts.Count);
    }

    [Fact]
    public async Task AfterExpiry_TheFailedAttemptsLastSegmentIsReplaced_NotServed()
    {
        var attempt = await StartPlayback(hardware: false, segments: 3, last: "TRUNCATED");
        await FailAndWait(attempt, 1, "something nobody recognises");

        _clock.Advance(FailureAnswerLifetime);
        AssertFailed(await Settled(Request(2)), "none");

        _clock.Advance(TimeSpan.FromTicks(1));
        var next = NextStart();
        var request = Request(2);
        var successor = await next.WaitAsync(Patience, TestContext.Current.CancellationToken);

        // The successor starts on an output that no longer holds the segment it is asked for.
        Assert.DoesNotContain(attempt.Segment(2), successor.FilesWhenStarted);

        successor.Write(2, "second attempt 2");
        successor.Write(3, "second attempt 3");
        var response = await request.WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status200OK, response.Status);
        Assert.Equal("second attempt 2", response.Text);
        Assert.Equal(2, _starts.Count);
    }

    [Fact]
    public async Task ReleasedWhileItsOutputIsStillThere_TheFailedAttemptLeavesItsSuccessorsOutputAlone()
    {
        var attempt = await StartPlayback(hardware: true, segments: 6);

        // Stopped at the earliest point where the attempt is ending and has removed nothing yet.
        var beforeRemoval = _fileSystem.PauseBeforeRemoval(attempt.Prefix);
        var afterExit = PauseAtNextTranscodingReport();
        Fail(attempt, 134, DeviceLost);
        await Task.WhenAny(beforeRemoval.Reached, afterExit.Reached).WaitAsync(Patience, TestContext.Current.CancellationToken);

        // The client lets go of the play session, and asks once more on it: a fresh attempt, by design.
        await _manager!.KillTranscodingJobs(OwnerDevice, SessionA, _ => false);
        var next = NextStart();
        var request = Request(2, actor: "successor");
        var successor = await next.WaitAsync(Patience, TestContext.Current.CancellationToken);

        // It does not start on what the failed attempt left: that would be served as its own.
        Assert.Empty(successor.FilesWhenStarted);
        successor.Write(Init, "successor init");
        successor.Write(2, "successor 2");
        successor.Write(3, "successor 3");
        var response = await request.WaitAsync(Patience, TestContext.Current.CancellationToken);

        beforeRemoval.Open();
        afterExit.Open();
        await attempt.Ended;

        Assert.Equal(StatusCodes.Status200OK, response.Status);
        Assert.Equal("successor 2", response.Text);
        Assert.True(File.Exists(successor.Segment(Init)), "the failed attempt removed its successor's initialisation segment");
        Assert.True(File.Exists(successor.Segment(2)), "the failed attempt removed its successor's segment");
        Assert.Equal(2, _starts.Count);
    }

    // ---------------------------------------------------------------- what must not change

    [Fact]
    public async Task AttemptFailingBeforeItsFirstOutput_AnswersTheRequestThatStartedIt()
    {
        Configure(hardware: true);
        var next = NextStart();
        var first = Request(0);
        var attempt = await next.WaitAsync(Patience, TestContext.Current.CancellationToken);

        await FailAndWait(attempt, 251, "Device creation failed: -5.");

        AssertFailed(await Settled(first), "software");
        AssertFailed(await Settled(Request(0)), "software");
        Assert.Single(_starts);
    }

    [Fact]
    public async Task SeekThatRestartsADyingAttempt_KeepsItsOutput_WhenThatAttemptIsThenFoundToHaveFailed()
    {
        var attempt = await StartPlayback(hardware: true, segments: 6);

        // The process is gone and judged, and nothing says so yet: no failure, no HasExited.
        var judged = new Gate("the verdict on the failed attempt");
        _verdictGate = judged;
        Fail(attempt, 134, DeviceLost);
        await judged.Reached;

        // To the server this is a healthy attempt and a seek far past it: stop it, start another.
        var next = NextStart();
        var request = Request(40);
        // If the request ends before anything starts, its own failure is the finding, not a wait that runs out.
        if (await Task.WhenAny(next, request).WaitAsync(Patience, TestContext.Current.CancellationToken) == request)
        {
            await request;
        }

        var restarted = await next.WaitAsync(Patience, TestContext.Current.CancellationToken);
        restarted.Write(Init, "restarted init");
        restarted.Write(40, "restarted 40");
        restarted.Write(41, "restarted 41");
        var response = await request.WaitAsync(Patience, TestContext.Current.CancellationToken);

        judged.Open();
        await attempt.Ended;

        Assert.Equal(StatusCodes.Status200OK, response.Status);
        Assert.Equal("restarted 40", response.Text);
        Assert.True(File.Exists(restarted.Segment(Init)), "the failed attempt removed the initialisation segment of the attempt that replaced it");
        Assert.True(File.Exists(restarted.Segment(40)), "the failed attempt removed a segment of the attempt that replaced it");
        Assert.Equal(2, _starts.Count);
    }

    [Fact]
    public async Task SeekDecidedOnAHealthyAttempt_ThatFailsBeforeItIsStopped_IsToldSo()
    {
        var attempt = await StartPlayback(hardware: true, segments: 6);

        // A seek far past the attempt. The request holds the lock, found the attempt healthy, and
        // is listing its output to decide; the failure is published before it has decided.
        var listing = _fileSystem.PauseAfterListing("seeker");
        var seek = Request(40, actor: "seeker");
        await listing.Reached;

        var published = PauseAtNextTranscodingReport();
        Fail(attempt, 134, DeviceLost);
        await published.Reached;
        published.Open();
        listing.Open();

        AssertFailed(await Settled(seek), "software");
        await attempt.Ended;
        Assert.Single(_starts);
        Assert.Empty(attempt.FilesOnDisk());
    }

    [Fact]
    public async Task InitialisationSegmentOfAnAttemptThatFailedWithoutFallback_IsStillServed()
    {
        // Restarted by a seek, so its first segment is not segment 0.
        var attempt = await StartPlayback(hardware: false, segments: 6);
        var next = NextStart();
        var seek = Request(40);
        var restarted = await next.WaitAsync(Patience, TestContext.Current.CancellationToken);
        restarted.Write(Init, "init after the seek");
        restarted.Write(40, "segment 40");
        restarted.Write(41, "segment 41");
        Assert.Equal(StatusCodes.Status200OK, (await seek.WaitAsync(Patience, TestContext.Current.CancellationToken)).Status);
        await attempt.Ended.WaitAsync(Patience, TestContext.Current.CancellationToken);

        // What the first run of the play session wrote from segment 0 is long gone in a real one.
        File.Delete(attempt.Segment(0));
        await FailAndWait(restarted, 1, "something nobody recognises");

        var init = await Settled(Request(Init));
        Assert.Equal(StatusCodes.Status200OK, init.Status);
        Assert.Equal("init after the seek", init.Text);
        AssertFailed(await Settled(Request(41)), "none");
        Assert.Equal(2, _starts.Count);
    }

    [Fact]
    public async Task SeekFarAhead_StillRestartsOnTheSamePlaySession()
    {
        var attempt = await StartPlayback(hardware: true, segments: 6);

        var next = NextStart();
        var request = Request(40);
        var restarted = await next.WaitAsync(Patience, TestContext.Current.CancellationToken);
        restarted.Write(40, "segment 40");
        restarted.Write(41, "segment 41");
        var response = await request.WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status200OK, response.Status);
        Assert.Equal("segment 40", response.Text);
        Assert.Equal(attempt.Job.Path, restarted.Job.Path);
        await attempt.Ended.WaitAsync(Patience, TestContext.Current.CancellationToken);
        Assert.Null(attempt.Job.Failure);
        Assert.Equal(2, _starts.Count);
    }

    [Fact]
    public async Task StoppedAttempt_IsNotAFailure_AndTheNextRequestStartsAfresh()
    {
        var attempt = await StartPlayback(hardware: true, segments: 6);
        await _manager!.KillTranscodingJobs(OwnerDevice, SessionA, _ => false);

        var next = NextStart();
        var request = Request(2);
        var restarted = await next.WaitAsync(Patience, TestContext.Current.CancellationToken);
        restarted.Write(2, "fresh 2");
        restarted.Write(3, "fresh 3");
        var response = await request.WaitAsync(Patience, TestContext.Current.CancellationToken);

        await attempt.Ended.WaitAsync(Patience, TestContext.Current.CancellationToken);
        Assert.Null(attempt.Job.Failure);
        Assert.Equal(StatusCodes.Status200OK, response.Status);
        Assert.Equal("fresh 2", response.Text);
    }

    [Fact]
    public async Task AnotherUserAskingForTheFailedOutput_LearnsNothingAndStartsNothing()
    {
        var attempt = await StartPlayback(hardware: true, segments: 6);
        await FailAndWait(attempt, 134, DeviceLost);

        var response = await Settled(Request(2, user: _stranger, device: "another-device"));

        Assert.Equal(StatusCodes.Status401Unauthorized, response.Status);
        Assert.Null(response.Recovery);
        Assert.Single(_starts);
    }

    [Fact]
    public async Task FailureOfOnePlaySession_LeavesAnotherOnesAttemptAlone()
    {
        var a = await StartPlayback(hardware: true, segments: 6);
        var b = await StartPlayback(hardware: true, segments: 6, playSession: SessionB);
        Assert.NotEqual(a.Job.Path, b.Job.Path);

        await FailAndWait(a, 134, DeviceLost);

        var fromB = await Settled(Request(1, playSession: SessionB));
        Assert.Equal(StatusCodes.Status200OK, fromB.Status);
        Assert.Equal("segment 1", fromB.Text);
        Assert.False(b.Job.HasExited);
        Assert.Null(b.Job.Failure);
        Assert.Equal(7, b.FilesOnDisk().Count);
        AssertFailed(await Settled(Request(1)), "software");
        Assert.Equal(2, _starts.Count);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _fileSystem.OpenEverything();
        _reportGate?.Open();
        _lockGate?.Open();
        _verdictGate?.Open();
        _stopGate?.Open();
        _stopDecisionGate?.Open();
        _beforeStartGate?.Open();

        foreach (var start in _starts)
        {
            try
            {
                start.Process.Kill(true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                // Already gone, which is the point.
            }
        }

        _manager?.Dispose();
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

    // ---------------------------------------------------------------- driving it

    private static TaskCompletionSource<T> NewSignal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void AssertFailed(Response response, string recovery)
    {
        Assert.Equal(StatusCodes.Status410Gone, response.Status);
        Assert.Equal(recovery, response.Recovery);
    }

    private static void Fail(Start attempt, int exitCode, string stderr)
        => attempt.Process.StandardInput.WriteLine(FormattableString.Invariant($"end {exitCode} {stderr}"));

    /// <summary>
    /// Starts a playback and puts <paramref name="segments"/> segments and the initialisation
    /// segment on disk; the highest one is the one "being written".
    /// </summary>
    private async Task<Start> StartPlayback(bool hardware, int segments, string? last = null, string? playSession = SessionA, Guid? user = null, string device = OwnerDevice, string? namedDevice = OwnerDevice, string? userAgent = null)
    {
        Configure(hardware);
        var next = NextStart();
        var first = Request(0, playSession: playSession, user: user, device: device, namedDevice: namedDevice, userAgent: userAgent);
        var attempt = await next.WaitAsync(Patience, TestContext.Current.CancellationToken);
        Assert.Equal(hardware, attempt.Command.Contains("-init_hw_device", StringComparison.Ordinal));

        attempt.Write(Init, "init");
        for (var i = 0; i < segments; i++)
        {
            attempt.Write(i, i == segments - 1 && last is not null ? last : "segment " + i.ToString(CultureInfo.InvariantCulture));
        }

        var response = await first.WaitAsync(Patience, TestContext.Current.CancellationToken);
        Assert.Equal(StatusCodes.Status200OK, response.Status);
        return attempt;
    }

    private Task<Start> NextStart()
    {
        lock (_sync)
        {
            _nextStart = NewSignal<Start>();
            return _nextStart.Task;
        }
    }

    private Task FailAndWait(Start attempt, int exitCode, string stderr)
        => EndAndWait(attempt, FormattableString.Invariant($"end {exitCode} {stderr}"));

    private async Task EndAndWait(Start attempt, string command)
    {
        await attempt.Process.StandardInput.WriteLineAsync(command);
        await attempt.Ended.WaitAsync(Patience, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// The request's answer - or a failure naming the transcode it started instead, which is the
    /// defect and would otherwise show as a request that never comes back.
    /// </summary>
    private async Task<Response> Settled(Task<Response> request)
    {
        Task<Start> started;
        lock (_sync)
        {
            started = _nextStart.Task.IsCompleted ? NextStartLocked() : _nextStart.Task;
        }

        var winner = await Task.WhenAny(request, started).WaitAsync(Patience, TestContext.Current.CancellationToken);
        if (winner == started && !request.IsCompleted)
        {
            var start = await started;
            Assert.Fail("A segment request started another transcode instead of being answered: " + start.Describe());
        }

        return await request;
    }

    private Task<Start> NextStartLocked()
    {
        _nextStart = NewSignal<Start>();
        return _nextStart.Task;
    }

    private Gate PauseAtNextTranscodingReport()
    {
        var gate = new Gate("the report that follows the attempt's exit");
        _reportGate = gate;
        return gate;
    }

    private void Configure(bool hardware)
    {
        if (_manager is not null)
        {
            Assert.Equal(hardware, _options!.HardwareAccelerationType != HardwareAccelerationType.none);
            return;
        }

        Assert.SkipWhen(OperatingSystem.IsWindows(), "The stand-in for ffmpeg is a POSIX shell script.");

        var ffmpeg = Path.Combine(_root, "ffmpeg");
        File.WriteAllText(
            ffmpeg,
            "#!/bin/sh\n"
            + "# Stands in for ffmpeg: writes no output, lives until told how to end. Asked to quit, it\n"
            + "# exits non-zero, so that only the server knowing it asked tells a stop from a failure.\n"
            + "while IFS= read -r line; do\n"
            + "  case \"$line\" in\n"
            + "    q) exit 1 ;;\n"
            + "    \"end \"*) rest=${line#end }; code=${rest%% *}; text=${rest#\"$code\"}; [ -n \"$text\" ] && printf '%s\\n' \"${text# }\" >&2; exit \"$code\" ;;\n"
            + "  esac\n"
            + "done\n"
            + "# Its input closed: it ends - unless the test put this marker down, for a process that does not.\n"
            + "[ -e \"$0.lingers\" ] && exec sleep 600\n"
            + "exit 0\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(ffmpeg, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        _options = new EncodingOptions
        {
            TranscodingTempPath = _transcodePath,
            HardwareAccelerationType = hardware ? HardwareAccelerationType.vaapi : HardwareAccelerationType.none,
            VaapiDevice = "/dev/dri/renderD128"
        };

        var appPaths = Mock.Of<IServerApplicationPaths>(p =>
            p.CachePath == _root && p.LogDirectoryPath == Path.Combine(_root, "log") && p.TempDirectory == _root);
        var configurationManager = new Mock<IServerConfigurationManager>();
        configurationManager.Setup(c => c.GetConfiguration("encoding")).Returns(_options);
        configurationManager.SetupGet(c => c.CommonApplicationPaths).Returns(appPaths);
        configurationManager.SetupGet(c => c.ApplicationPaths).Returns(appPaths);

        var item = new Movie { Id = _item, Path = Path.Combine(_root, "film.mkv") };
        var libraryManager = new Mock<ILibraryManager>();
        libraryManager.Setup(l => l.GetItemById<BaseItem>(It.IsAny<Guid>())).Returns(item);
        libraryManager.Setup(l => l.GetItemById<BaseItem>(It.IsAny<Guid>(), It.IsAny<User>())).Returns(item);
        libraryManager.Setup(l => l.GetItemById<BaseItem>(It.IsAny<Guid>(), It.IsAny<Guid>())).Returns(item);

        // Under five minutes: neither the throttler nor the segment cleaner is started, so nothing
        // but the test and the code under test touches the output.
        var mediaSource = new MediaSourceInfo
        {
            Id = MediaSourceId,
            Path = item.Path,
            Protocol = MediaProtocol.File,
            Container = "mkv",
            RunTimeTicks = TimeSpan.FromMinutes(4).Ticks,
            MediaStreams = new List<MediaStream>
            {
                new() { Type = MediaStreamType.Video, Index = 0, Codec = "hevc", Width = 1280, Height = 720, BitRate = 4_000_000, RealFrameRate = 25, AverageFrameRate = 25, PixelFormat = "yuv420p", BitDepth = 8 },
                new() { Type = MediaStreamType.Audio, Index = 1, Codec = "ac3", Channels = 2, BitRate = 192000, SampleRate = 48000 }
            }
        };
        var mediaSourceManager = new Mock<IMediaSourceManager>();
        mediaSourceManager
            .Setup(m => m.GetPlaybackMediaSources(It.IsAny<BaseItem>(), It.IsAny<Tesserafin.Data.Entities.User>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MediaSourceInfo> { mediaSource });

        // An encoder that can do everything it is asked about: which pipeline a command uses is
        // then decided by the options alone, as it is in production once the start-up probe passed.
        var mediaEncoder = new Mock<IMediaEncoder>();
        mediaEncoder.SetReturnsDefault(true);
        mediaEncoder.SetupGet(e => e.EncoderPath).Returns(ffmpeg);
        mediaEncoder.SetupGet(e => e.EncoderVersion).Returns(new Version(7, 1, 4));
        mediaEncoder.Setup(e => e.GetInputArgument(It.IsAny<string>(), It.IsAny<MediaSourceInfo>())).Returns("file:\"film.mkv\"");

        _sessionManager
            .Setup(s => s.ReportTranscodingInfo(It.IsAny<string>(), It.IsAny<TranscodingInfo>()))
            .Callback(() => Interlocked.Exchange(ref _reportGate, null)?.Hit());

        var encodingHelper = new EncodingHelper(
            appPaths,
            mediaEncoder.Object,
            Mock.Of<ISubtitleEncoder>(),
            Mock.Of<IConfiguration>(),
            configurationManager.Object,
            Mock.Of<IPathManager>());

        var manager = new TranscodeManager(
            new GatedLoggerFactory(message =>
            {
                if (message.StartsWith("Hardware transcode failed", StringComparison.Ordinal))
                {
                    Interlocked.Exchange(ref _verdictGate, null)?.Hit();
                }
                else if (message.StartsWith("Stopping ffmpeg process", StringComparison.Ordinal))
                {
                    // Said by a job that is being stopped, just before its process is told to end.
                    Interlocked.Exchange(ref _stopGate, null)?.Hit();
                }
                else if (message.StartsWith(ffmpeg, StringComparison.Ordinal))
                {
                    // The command, said once the job is registered and before its process is started.
                    Interlocked.Exchange(ref _beforeStartGate, null)?.Hit();
                }
            }),
            _fileSystem,
            appPaths,
            configurationManager.Object,
            TestHelpers.AnyUser(),
            _sessionManager.Object,
            encodingHelper,
            mediaEncoder.Object,
            mediaSourceManager.Object,
            Mock.Of<IAttachmentExtractor>(),
            new Tesserafin.MediaEncoding.Encoder.FfmpegProcessRunner(Microsoft.Extensions.Logging.Abstractions.NullLogger<Tesserafin.MediaEncoding.Encoder.FfmpegProcessRunner>.Instance))
        {
            TimeProvider = _clock
        };
        manager.TranscodingJobStarted += (_, job) =>
        {
            var start = new Start(job, _transcodePath);
            _starts.Enqueue(start);
            lock (_sync)
            {
                _nextStart.TrySetResult(start);
            }
        };
        _manager = manager;
        var gated = new GatedTranscodeManager(
            manager,
            () => Interlocked.Exchange(ref _lockGate, null)?.Hit(),
            () => Interlocked.Exchange(ref _stopDecisionGate, null)?.Hit());
        _gated = gated;

        var authorizer = new HlsJobOwnershipAuthorizer(manager, _sessionManager.Object);

        _newLegacyController = () => new HlsSegmentController(gated, authorizer);

        _newController = () => new DynamicHlsController(
            libraryManager.Object,
            TestHelpers.AnyUser(),
            mediaSourceManager.Object,
            configurationManager.Object,
            mediaEncoder.Object,
            _fileSystem,
            gated,
            NullLogger<DynamicHlsController>.Instance,
            encodingHelper,
            Mock.Of<IDynamicHlsPlaylistGenerator>(),
            authorizer);
    }

    /// <summary>
    /// One segment request through the real action, executed to the bytes the framework writes.
    /// </summary>
    /// <remarks>
    /// <paramref name="device"/> is the token's; <paramref name="namedDevice"/> and
    /// <paramref name="playSession"/> are what the client puts in the query, and may be nothing.
    /// </remarks>
    private Task<Response> Request(int segment, string? actor = null, string? playSession = SessionA, Guid? user = null, string device = OwnerDevice, string? namedDevice = OwnerDevice, string? userAgent = null)
    {
        var identity = new ClaimsIdentity("CustomAuthentication");
        identity.AddClaim(new Claim(InternalClaimTypes.UserId, (user ?? _owner).ToString("N")));
        identity.AddClaim(new Claim(InternalClaimTypes.DeviceId, device));

        var body = new MemoryStream();
        var httpContext = new DefaultHttpContext
        {
            Request = { Method = HttpMethods.Get, Path = new PathString(FormattableString.Invariant($"/Videos/{_item:N}/hls1/main/{segment}.mp4")) },
            Response = { Body = body },
            User = new ClaimsPrincipal(identity),
            RequestServices = _services
        };
        if (userAgent is not null)
        {
            // Part of what names an output; without it two nameless clients on one film share one.
            httpContext.Request.Headers.UserAgent = userAgent;
        }

        var controller = _newController!();
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        return Task.Run(async () =>
        {
            GatedFileSystem.Actor.Value = actor;
            var result = await controller.GetDynamicSegment(
                new Tesserafin.Controller.Streaming.VideoRequestDto
                {
                    // The plan the session retained: the source video and audio streams.
                    VideoStreamIndex = 0,
                    AudioStreamIndex = 1,
                    Id = _item,
                    CurrentRuntimeTicks = Math.Max(segment, 0) * TimeSpan.FromSeconds(3).Ticks,
                    ActualSegmentLengthTicks = TimeSpan.FromSeconds(3).Ticks,
                    Container = "mp4",
                    Static = false,
                    PlaySessionId = playSession,
                    SegmentContainer = "mp4",
                    SegmentLength = 3,
                    MinSegments = 1,
                    MediaSourceId = MediaSourceId,
                    DeviceId = namedDevice,
                    AudioCodec = "aac",
                    EnableAutoStreamCopy = false,
                    AllowAudioStreamCopy = false,
                    AllowVideoStreamCopy = false,
                    MaxAudioChannels = 2,
                    AudioBitRate = 128000,
                    CopyTimestamps = false,
                    VideoBitRate = 2_000_000,
                    SubtitleMethod = Tesserafin.Model.Dlna.SubtitleDeliveryMethod.External,
                    RequireAvc = false,
                    DeInterlace = false,
                    RequireNonAnamorphic = false,
                    EnableMpegtsM2TsMode = false,
                    VideoCodec = "h264",
                    Context = Tesserafin.Model.Dlna.EncodingContext.Streaming,
                    StreamOptions = new Dictionary<string, string>(),
                },
                segment).ConfigureAwait(false);

            await result.ExecuteResultAsync(new ActionContext(httpContext, new RouteData(), new ActionDescriptor())).ConfigureAwait(false);

            return new Response(
                httpContext.Response.StatusCode,
                httpContext.Response.Headers.TryGetValue("X-Tesserafin-Playback-Recovery", out var recovery) ? recovery.ToString() : null,
                Encoding.UTF8.GetString(body.ToArray()));
        });
    }

    private sealed record Response(int Status, string? Recovery, string Text);

    /// <summary>
    /// One started transcode: its job, the command it was started with, and its output as the test
    /// writes it.
    /// </summary>
    private sealed class Start
    {
        private static long _clock = DateTime.UtcNow.Ticks;
        private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly string _directory;

        public Start(TranscodingJob job, string directory)
        {
            Job = job;
            Process = job.Process!;
            Command = job.Process!.StartInfo.Arguments;
            _directory = directory;
            Prefix = Path.Combine(directory, Path.GetFileNameWithoutExtension(job.Path!));
            FilesWhenStarted = FilesOnDisk();

            // Subscribed after the manager, so raised after the manager's own handler has returned.
            Process.Exited += (_, _) => _ended.TrySetResult();
        }

        public TranscodingJob Job { get; }

        public System.Diagnostics.Process Process { get; }

        public string Command { get; }

        public string Prefix { get; }

        public IReadOnlyList<string> FilesWhenStarted { get; }

        /// <summary>Gets a task that completes when the manager has finished handling the process's exit, removal included.</summary>
        public Task Ended => _ended.Task;

        public string Segment(int index) => Prefix + index.ToString(CultureInfo.InvariantCulture) + ".mp4";

        public void Write(int index, string content)
        {
            File.WriteAllText(Segment(index), content);

            // "The last one written" is read off the modification time, so it is stated, not left to the clock's resolution.
            File.SetLastWriteTimeUtc(Segment(index), new DateTime(Interlocked.Add(ref _clock, TimeSpan.TicksPerSecond), DateTimeKind.Utc));
        }

        public IReadOnlyList<string> FilesOnDisk()
            => Directory.GetFiles(_directory).Where(f => f.StartsWith(Prefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList();

        public string Describe()
            => FormattableString.Invariant($"output {Path.GetFileName(Prefix)}, {(Command.Contains("-init_hw_device", StringComparison.Ordinal) ? "hardware" : "software")}, play session {Job.PlaySessionId}");
    }

    /// <summary>
    /// The real manager as the controller sees it, with a gate where a request takes the output's
    /// lock, and one between a job being found to be the caller's and that job being stopped.
    /// </summary>
    private sealed class GatedTranscodeManager : ITranscodeManager, IHardwareTranscodeFallback, ITranscodeOutputStop, ITranscodeOwnedStop
    {
        private readonly TranscodeManager _inner;
        private readonly Action _beforeLock;
        private readonly Action _afterStopDecision;

        public GatedTranscodeManager(TranscodeManager inner, Action beforeLock, Action afterStopDecision)
        {
            _inner = inner;
            _beforeLock = beforeLock;
            _afterStopDecision = afterStopDecision;
        }

        public event EventHandler<TranscodingJob>? TranscodingJobEnded
        {
            add => _inner.TranscodingJobEnded += value;
            remove => _inner.TranscodingJobEnded -= value;
        }

        public event EventHandler<TranscodingJob>? TranscodingJobStarted
        {
            add => _inner.TranscodingJobStarted += value;
            remove => _inner.TranscodingJobStarted -= value;
        }

        public ValueTask<IDisposable> LockAsync(string outputPath, CancellationToken cancellationToken)
        {
            _beforeLock();
            return _inner.LockAsync(outputPath, cancellationToken);
        }

        public TranscodingJob? GetTranscodingJob(string playSessionId) => _inner.GetTranscodingJob(playSessionId);

        public TranscodingJob? GetTranscodingJob(string path, TranscodingJobType type) => _inner.GetTranscodingJob(path, type);

        public void PingTranscodingJob(string playSessionId, bool? isUserPaused) => _inner.PingTranscodingJob(playSessionId, isUserPaused);

        public Task KillTranscodingJobs(string deviceId, string? playSessionId, Func<string, bool> deleteFiles) => _inner.KillTranscodingJobs(deviceId, playSessionId, deleteFiles);

        public void ReportTranscodingProgress(TranscodingJob job, StreamState state, TimeSpan? transcodingPosition, float? framerate, double? percentComplete, long? bytesTranscoded, int? bitRate)
            => _inner.ReportTranscodingProgress(job, state, transcodingPosition, framerate, percentComplete, bytesTranscoded, bitRate);

        public Task<TranscodingJob> StartFfMpeg(StreamState state, string outputPath, FfmpegCommand command, Guid userId, TranscodingJobType transcodingJobType, CancellationTokenSource cancellationTokenSource)
            => _inner.StartFfMpeg(state, outputPath, command, userId, transcodingJobType, cancellationTokenSource);

        public TranscodingJob? OnTranscodeBeginRequest(string path, TranscodingJobType type) => _inner.OnTranscodeBeginRequest(path, type);

        public void OnTranscodeEndRequest(TranscodingJob job) => _inner.OnTranscodeEndRequest(job);

        public EncodingOptions GetEffectiveEncodingOptions(string? mediaSourceId) => _inner.GetEffectiveEncodingOptions(mediaSourceId);

        public TranscodeFailure? GetTranscodeFailure(string path, TranscodingJobType type) => _inner.GetTranscodeFailure(path, type);

        public Task StopTranscodingJob(string path, TranscodingJobType type, long generation, Func<string, bool> deleteFiles) => _inner.StopTranscodingJob(path, type, generation, deleteFiles);

        public Task StopTranscodingJobs(string playSessionId, Func<TranscodingJob, bool> isCallers)
            => _inner.StopTranscodingJobs(playSessionId, job =>
            {
                var yes = isCallers(job);
                if (yes)
                {
                    _afterStopDecision();
                }

                return yes;
            });
    }

    /// <summary>
    /// Hands what the manager says to the test, which is how it stands between a failed attempt
    /// being judged and that judgement being published, and beside a job whose process is about to be stopped.
    /// </summary>
    private sealed class GatedLoggerFactory : ILoggerFactory, ILogger
    {
        private readonly Action<string> _onWarning;

        public GatedLoggerFactory(Action<string> onWarning) => _onWarning = onWarning;

        public ILogger CreateLogger(string categoryName) => this;

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel == LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel is LogLevel.Warning or LogLevel.Information)
            {
                _onWarning(formatter(state, exception));
            }
        }
    }

    private sealed class Clock : TimeProvider
    {
        private long _ticks = new DateTime(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc).Ticks;

        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

        public void Advance(TimeSpan amount) => Interlocked.Add(ref _ticks, amount.Ticks);
    }

    /// <summary>
    /// A place where one thread of the code under test stops until the test lets it go on.
    /// </summary>
    private sealed class Gate
    {
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly string _name;

        public Gate(string name) => _name = name;

        public Task Reached => _reached.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);

        public void Open() => _open.TrySetResult();

        public void Hit()
        {
            _reached.TrySetResult();
            if (!_open.Task.Wait(Patience))
            {
                throw new TimeoutException("Nobody opened the gate at " + _name);
            }
        }
    }

    /// <summary>
    /// The real file system, with gates on the three things the decision is made of: a look at a
    /// file, the listing of what to remove, and each removal.
    /// </summary>
    private sealed class GatedFileSystem : ManagedFileSystem
    {
        private readonly List<(Func<string, string?, string, bool> Matches, Gate Gate)> _gates = new();
        private readonly List<Gate> _all = new();

        public GatedFileSystem(IServerApplicationPaths paths)
            : base(NullLogger<ManagedFileSystem>.Instance, paths, Array.Empty<IShortcutHandler>())
        {
        }

        /// <summary>
        /// Gets the name a request gave itself. It follows the request's flow of control, the exit
        /// handling of a process that request started included.
        /// </summary>
        public static AsyncLocal<string?> Actor { get; } = new();

        /// <summary>Gets or sets the file a removal takes first. Directory order is the file system's to choose.</summary>
        public string? RemoveFirst { get; set; }

        /// <summary>Gets or sets a file that cannot be removed.</summary>
        public string? CannotRemove { get; set; }

        public Gate PauseAfterLook(string? actor, string path, int occurrence = 1)
        {
            var seen = 0;
            return Add("the look at " + Path.GetFileName(path), (op, who, p) => op == "looked" && who == actor && p == path && Interlocked.Increment(ref seen) == occurrence);
        }

        public Gate PauseAfterListing(string actor)
            => Add("the listing of the output", (op, who, _) => op == "listed" && who == actor);

        public Gate PauseAfterRemoval(string path)
            => Add("the removal of " + Path.GetFileName(path), (op, who, p) => op == "removed" && who is null && p == path);

        public Gate PauseBeforeRemoval(string prefix)
            => Add("the first removal", (op, who, p) => op == "removing" && who is null && p.StartsWith(prefix, StringComparison.Ordinal));

        public Gate PauseBeforeRemovalBy(string actor, string prefix)
            => Add("the first removal by " + actor, (op, who, p) => op == "removing" && who == actor && p.StartsWith(prefix, StringComparison.Ordinal));

        public void OpenEverything()
        {
            lock (_gates)
            {
                _all.ForEach(g => g.Open());
            }
        }

        public override bool FileExists(string path)
        {
            var exists = base.FileExists(path);
            Pass("looked", path);
            return exists;
        }

        public override void DeleteFile(string path)
        {
            Pass("removing", path);
            if (path == CannotRemove)
            {
                throw new IOException("The file is in use: " + path);
            }

            base.DeleteFile(path);
            Pass("removed", path);
        }

        public override IEnumerable<FileSystemMetadata> GetFiles(string path, IReadOnlyList<string>? extensions, bool enableCaseSensitiveExtensions, bool recursive)
        {
            var files = base.GetFiles(path, extensions, enableCaseSensitiveExtensions, recursive).ToList();
            Pass("listed", path);
            return files;
        }

        public override IEnumerable<string> GetFilePaths(string path, bool recursive = false)
            => base.GetFilePaths(path, recursive).OrderBy(p => p == RemoveFirst ? 0 : 1).ThenBy(p => p, StringComparer.Ordinal).ToList();

        private Gate Add(string name, Func<string, string?, string, bool> matches)
        {
            var gate = new Gate(name);
            lock (_gates)
            {
                _gates.Add((matches, gate));
                _all.Add(gate);
            }

            return gate;
        }

        private void Pass(string op, string path)
        {
            Gate? hit = null;
            lock (_gates)
            {
                var index = _gates.FindIndex(g => g.Matches(op, Actor.Value, path));
                if (index >= 0)
                {
                    hit = _gates[index].Gate;
                    _gates.RemoveAt(index);
                }
            }

            hit?.Hit();
        }
    }
}
