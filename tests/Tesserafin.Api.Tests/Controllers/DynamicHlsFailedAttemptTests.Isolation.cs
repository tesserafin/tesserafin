using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Api.Auth.HlsJobOwnership;
using Tesserafin.Api.Controllers;
using Tesserafin.Common;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Controller.Session;
using Tesserafin.Controller.Streaming;
using Tesserafin.Model.Dto;
using Tesserafin.Model.MediaInfo;
using Tesserafin.Model.Session;
using Xunit;

namespace Tesserafin.Api.Tests.Controllers;

/// <summary>
/// POLISH-2-R4. Two users, one output or one play session id: what a released job leaves behind,
/// and whose transcodes a stop reaches.
/// </summary>
/// <remarks>
/// An output's path and a play session id are both made of what the client sends. A second user
/// who sends what the first one sent arrives at the first user's output, and names the first
/// user's jobs. While a job is registered the caller is compared with its owner; these are the
/// places where there was nobody to compare with, or where one job answered for several.
///
/// THE WINDOW IS HELD OPEN BY A RELEASE THAT REMOVES NOTHING. In production the files of a
/// released job stay for a second and a half, or for good if their removal fails. Here the
/// release is asked for no removal at all - what a seek does - so the output stays as it was for
/// as long as the test needs, and no test waits on a real delay.
/// </remarks>
public sealed partial class DynamicHlsFailedAttemptTests
{
    // ---------------------------------------------------------------- what a released job leaves on its output

    [Theory]
    [InlineData(2)]
    [InlineData(Init)]
    public async Task ReleasedOutputReplayedByAnotherUser_IsNotAnsweredWithWhatTheFirstUserLeft(int segment)
    {
        var theirs = await StartPlayback(hardware: false, segments: 6);
        await _manager!.KillTranscodingJobs(OwnerDevice, SessionA, _ => false);
        Assert.Equal(7, theirs.FilesOnDisk().Count);

        // The same url, to the letter: User-Agent, device id and play session id are the first
        // user's. Only the credential is the second user's own.
        var next = NextStart();
        var replay = Request(segment, actor: "replay", user: _stranger, device: OtherDevice);
        var mine = await next.WaitAsync(Patience, TestContext.Current.CancellationToken);
        Assert.Equal(theirs.Job.Path, mine.Job.Path);

        // The second transcode has written nothing yet. Started beside the first one's files, the
        // request finds the one it waits for already there, and its answer says whose it is.
        if (mine.FilesWhenStarted.Count != 0)
        {
            var leaked = await replay.WaitAsync(Patience, TestContext.Current.CancellationToken);
            Assert.Fail(FormattableString.Invariant($"another user's replay was answered {leaked.Status} \"{leaked.Text}\": what the first user's transcode left on the output"));
        }

        mine.Write(Init, "mine, init");
        mine.Write(Math.Max(segment, 0), "mine, first");
        mine.Write(Math.Max(segment, 0) + 1, "mine, next");
        var response = await replay.WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status200OK, response.Status);
        Assert.Equal(segment == Init ? "mine, init" : "mine, first", response.Text);
        Assert.Equal(3, mine.FilesOnDisk().Count);
        Assert.Equal(2, _starts.Count);
    }

    [Fact]
    public async Task ReleasedOutputAskedForAgainByItsOwner_IsTranscodedAfresh_NotServedFromWhatWasLeft()
    {
        // Its former owner is not known to be its owner either: nothing says so but the url.
        var attempt = await StartPlayback(hardware: false, segments: 6);
        await _manager!.KillTranscodingJobs(OwnerDevice, SessionA, _ => false);

        var next = NextStart();
        var request = Request(2);
        var again = await next.WaitAsync(Patience, TestContext.Current.CancellationToken);
        Assert.Empty(again.FilesWhenStarted);
        again.Write(2, "afresh 2");
        again.Write(3, "afresh 3");
        var response = await request.WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status200OK, response.Status);
        Assert.Equal("afresh 2", response.Text);
        Assert.Equal(attempt.Job.Path, again.Job.Path);
    }

    [Fact]
    public async Task ReleasedOutputThatCannotBeEmptied_IsNeitherStartedOnNorServed()
    {
        var theirs = await StartPlayback(hardware: false, segments: 6);
        await _manager!.KillTranscodingJobs(OwnerDevice, SessionA, _ => false);
        _fileSystem.CannotRemove = theirs.Segment(2);

        var replay = Request(2, actor: "replay", user: _stranger, device: OtherDevice);

        await Assert.ThrowsAsync<IOException>(() => Settled(replay));
        Assert.Single(_starts);
        Assert.True(File.Exists(theirs.Segment(2)));
    }

    [Fact]
    public async Task WhileAReleasedAttemptIsStillBeingStopped_AnotherUserIsRefused_AndStartsNothing()
    {
        // Between "released" and "its process has ended" the output is still being written to.
        // It has an owner for exactly that long.
        var theirs = await StartPlayback(hardware: false, segments: 6);
        var stopping = new Gate("the stop of the process");
        _stopGate = stopping;
        var release = Task.Run(() => _manager!.KillTranscodingJobs(OwnerDevice, SessionA, _ => false), TestContext.Current.CancellationToken);
        await stopping.Reached;
        Assert.False(theirs.Job.HasExited);

        var response = await Settled(Request(2, actor: "replay", user: _stranger, device: OtherDevice));

        Assert.Equal(StatusCodes.Status401Unauthorized, response.Status);
        Assert.DoesNotContain("segment", response.Text, StringComparison.Ordinal);
        Assert.Single(_starts);

        stopping.Open();
        await release.WaitAsync(Patience, TestContext.Current.CancellationToken);
        Assert.Null(_manager!.GetTranscodingJob(theirs.Job.Path!, TranscodingJobType.Hls));
    }

    [Fact]
    public async Task StopThatDoesNotEndTheProcess_IsAnError_AndTheOutputKeepsItsOwner()
    {
        // THE PROBE: the job is handed a second handle on its own process, one with no standard
        // input to be asked through. The stop runs to its end and the process - the one that
        // writes this output - is still there. It stands for a process that outlives its kill;
        // what is under test is what the manager concludes from a stop that ended nothing.
        var theirs = await StartPlayback(hardware: false, segments: 6);
        theirs.Job.Process = System.Diagnostics.Process.GetProcessById(theirs.Process.Id);

        var error = await Assert.ThrowsAsync<AggregateException>(() => StopEncodings(SessionA));

        Assert.Contains(error.Flatten().InnerExceptions, e => e is TimeoutException);
        Assert.False(Stopped(theirs));
        Assert.Same(theirs.Job, _manager!.GetTranscodingJob(theirs.Job.Path!, TranscodingJobType.Hls));
        Assert.Equal(7, theirs.FilesOnDisk().Count);

        // Still written to, so still somebody's: the second user is compared with its owner.
        var response = await Settled(Request(2, actor: "replay", user: _stranger, device: OtherDevice));
        Assert.Equal(StatusCodes.Status401Unauthorized, response.Status);
        Assert.DoesNotContain("segment", response.Text, StringComparison.Ordinal);
        Assert.Single(_starts);

        // Once the process has ended, the same stop releases the output.
        await EndAndWait(theirs, "q");
        await StopEncodings(SessionA).WaitAsync(Patience, TestContext.Current.CancellationToken);
        Assert.Null(_manager.GetTranscodingJob(theirs.Job.Path!, TranscodingJobType.Hls));
    }

    [Fact]
    public async Task StartThatFailsWithItsProcessRunning_DoesNotLeaveThatProcessOnAnOutputWithNoJob()
    {
        // A live stream that fails before ffmpeg's first output fails the start - after the
        // process was started. This stand-in outlives the closing of its input, as an ffmpeg
        // that is finishing what it has does.
        Configure(hardware: false);
        await File.WriteAllTextAsync(Path.Combine(_root, "ffmpeg.lingers"), string.Empty, TestContext.Current.CancellationToken);
        var output = Path.Combine(_transcodePath, "feed.m3u8");
        using var state = new StreamState(Mock.Of<IMediaSourceManager>(), TranscodingJobType.Hls, _manager!)
        {
            Request = new StreamingRequestDto { Id = _item, PlaySessionId = SessionA, DeviceId = OwnerDevice, MediaSourceId = MediaSourceId },
            MediaSource = new MediaSourceInfo { Id = MediaSourceId, Path = "pipe:0", Protocol = MediaProtocol.File },
            DirectStreamProvider = Mock.Of<IDirectStreamProvider>(p => p.GetStream() == new LostTuner()),
            OwnerDeviceId = OwnerDevice
        };
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAsync<FfmpegException>(() => _manager!.StartFfMpeg(state, output, "-i pipe:0", _owner, TranscodingJobType.Hls, cancellation));

        var start = Assert.Single(_starts);
        Assert.True(
            Stopped(start) || _manager!.GetTranscodingJob(output, TranscodingJobType.Hls) is not null,
            "the start failed, its job left the registry, and its process is still running on the output");
    }

    [Fact]
    public async Task StopBetweenAJobsRegistrationAndItsProcesssStart_CancelsTheStart_AndNoProcessIsLeftOnTheOutput()
    {
        // A job is registered before its process is started. Stopped in between, there is no
        // process to stop yet: the job leaves the registry, and the start must not go on.
        Configure(hardware: false);
        var output = Path.Combine(_transcodePath, "early.m3u8");
        using var state = new StreamState(Mock.Of<IMediaSourceManager>(), TranscodingJobType.Hls, _manager!)
        {
            Request = new StreamingRequestDto { Id = _item, PlaySessionId = SessionA, DeviceId = OwnerDevice, MediaSourceId = MediaSourceId },
            MediaSource = new MediaSourceInfo { Id = MediaSourceId, Path = "film.mkv", Protocol = MediaProtocol.File },
            OwnerDeviceId = OwnerDevice
        };
        using var cancellation = new CancellationTokenSource();
        var registered = new Gate("the job registered, its process not started");
        _beforeStartGate = registered;

        var start = Task.Run(() => _manager!.StartFfMpeg(state, output, "-i film.mkv", _owner, TranscodingJobType.Hls, cancellation), TestContext.Current.CancellationToken);
        await registered.Reached;
        var job = _manager!.GetTranscodingJob(output, TranscodingJobType.Hls);
        Assert.NotNull(job);

        // THE BARRIER STANDS BEFORE TWO WRITES TO THE LOG THAT OBSERVE THE CANCELLATION, and the
        // window is after them: a few lines nothing can be made to stop in. So the stop is given
        // another source to cancel, and the start goes through those writes not knowing - as it
        // does when the stop arrives once they have begun.
        using var unseen = new CancellationTokenSource();
        job.CancellationTokenSource = unseen;

        await _manager.StopTranscodingJobs(SessionA, _ => true).WaitAsync(Patience, TestContext.Current.CancellationToken);
        registered.Open();

        // Either outcome keeps the output somebody's while something writes to it: the start is
        // cancelled, or the process it started is still registered.
        var outcome = await Record.ExceptionAsync(() => start.WaitAsync(Patience, TestContext.Current.CancellationToken));
        var stillRegistered = ReferenceEquals(job, _manager.GetTranscodingJob(output, TranscodingJobType.Hls));
        Assert.True(
            _starts.IsEmpty || stillRegistered,
            "a process was started for a job that had already left the registry: it writes to an output with no owner");
        if (_starts.IsEmpty)
        {
            Assert.IsAssignableFrom<OperationCanceledException>(outcome);
            Assert.Throws<InvalidOperationException>(() => job.Process!.Id);
        }
    }

    [Fact]
    public async Task LivePlaylistGoneButItsSegmentsLeft_AnotherUsersStartDoesNotInheritThem()
    {
        Configure(hardware: false);
        var next = NextStart();
        var first = LiveRequest(minSegments: 1);
        var theirs = await next.WaitAsync(Patience, TestContext.Current.CancellationToken);
        var playlistId = Path.GetFileName(theirs.Prefix);
        var leftover = theirs.Prefix + "0.ts";
        await File.WriteAllTextAsync(leftover, "their live segment", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(theirs.Job.Path!, "#EXTM3U\n#EXTINF:3.0,\n" + playlistId + "0.ts\n", TestContext.Current.CancellationToken);
        Assert.Equal(StatusCodes.Status200OK, (await first.WaitAsync(Patience, TestContext.Current.CancellationToken)).Status);

        // Released, and a removal that took the playlist and stopped there. While the playlist was
        // still on disk the live route refused everybody; without it, it starts a transcode.
        await _manager!.KillTranscodingJobs(OwnerDevice, SessionA, _ => false);
        File.Delete(theirs.Job.Path!);

        next = NextStart();
        var replay = LiveRequest(minSegments: 1, user: _stranger, device: OtherDevice);
        var mine = await next.WaitAsync(Patience, TestContext.Current.CancellationToken);
        Assert.Equal(theirs.Job.Path, mine.Job.Path);
        await File.WriteAllTextAsync(mine.Job.Path!, "#EXTM3U\n#EXTINF:3.0,\n" + playlistId + "7.ts\n", TestContext.Current.CancellationToken);
        Assert.Equal(StatusCodes.Status200OK, (await replay.WaitAsync(Patience, TestContext.Current.CancellationToken)).Status);

        // The second user owns the job on this output now, and the legacy route serves a job's
        // owner any file that carries the job's prefix.
        if (File.Exists(leftover))
        {
            var httpContext = Context(FormattableString.Invariant($"/Videos/{_item:N}/hls/{playlistId}/{playlistId}0.ts"), _stranger, OtherDevice, out var body);
            var controller = _newLegacyController!();
            controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
            var leaked = await Executed(controller.GetHlsVideoSegmentLegacy(_item.ToString("N"), playlistId, playlistId + "0", "ts"), httpContext, body);
            Assert.Fail(FormattableString.Invariant($"the legacy segment route answered another user {leaked.Status} \"{leaked.Text}\": a segment the first user's live transcode left"));
        }

        Assert.Empty(mine.FilesWhenStarted);
    }

    [Fact]
    public async Task LegacySegmentRoute_ServesNobodyAReleasedAttemptsSegment()
    {
        // Looked at, not repaired: this route starts nothing, so with no job it has no answer.
        var attempt = await StartPlayback(hardware: false, segments: 6);
        await _manager!.KillTranscodingJobs(OwnerDevice, SessionA, _ => false);

        var owner = await LegacySegmentRequest(attempt, 1);
        var other = await LegacySegmentRequest(attempt, 1, user: _stranger, device: OtherDevice);

        Assert.Equal(StatusCodes.Status404NotFound, owner.Status);
        Assert.Equal(StatusCodes.Status404NotFound, other.Status);
        Assert.DoesNotContain("segment 1", owner.Text + other.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LegacyRoutes_DoNotServeTheProgressiveFileThatSharesTheOutputsName()
    {
        // The job's owner is served any file that carries the job's prefix - and "{name}.{ext}",
        // the prefix and nothing after it, is a progressive transcode's, possibly somebody else's.
        var attempt = await StartPlayback(hardware: false, segments: 6);
        var playlistId = Path.GetFileName(attempt.Prefix);
        await File.WriteAllTextAsync(attempt.Prefix + ".mp4", "a progressive transcode", TestContext.Current.CancellationToken);

        var video = Context(FormattableString.Invariant($"/Videos/{_item:N}/hls/{playlistId}/{playlistId}.mp4"), null, OwnerDevice, out var videoBody);
        var controller = _newLegacyController!();
        controller.ControllerContext = new ControllerContext { HttpContext = video };
        var fromVideo = await Executed(controller.GetHlsVideoSegmentLegacy(_item.ToString("N"), playlistId, playlistId, "mp4"), video, videoBody);

        var audio = Context(FormattableString.Invariant($"/Audio/{_item:N}/hls/{playlistId}/stream.mp4"), null, OwnerDevice, out var audioBody);
        controller = _newLegacyController!();
        controller.ControllerContext = new ControllerContext { HttpContext = audio };
        var fromAudio = await Executed(controller.GetHlsAudioSegmentLegacy(_item.ToString("N"), playlistId), audio, audioBody);

        Assert.Equal(StatusCodes.Status404NotFound, fromVideo.Status);
        Assert.Equal(StatusCodes.Status404NotFound, fromAudio.Status);
        Assert.DoesNotContain("progressive", fromVideo.Text + fromAudio.Text, StringComparison.Ordinal);

        // The name that is opened is the segment id AND an extension the url supplies separately.
        // An url ending in a dot supplies none, and the whole file name can then be the id; so
        // can the playlist's, or its temporary file's.
        await File.WriteAllTextAsync(attempt.Prefix + ".m3u8", "the playlist", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(attempt.Prefix + ".m3u8.tmp", "the playlist, being written", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(attempt.Prefix + "1x.mp4", "not a segment either", TestContext.Current.CancellationToken);
        foreach (var (segmentId, container, tail) in new[] { (playlistId + ".mp4", ".", ".mp4.."), (playlistId + ".m3u8", ".", ".m3u8.."), (playlistId + ".m3u8", "tmp", ".m3u8.tmp"), (playlistId + "1x", "mp4", "1x.mp4") })
        {
            var spelled = Context(FormattableString.Invariant($"/Videos/{_item:N}/hls/{playlistId}/{playlistId}{tail}"), null, OwnerDevice, out var spelledBody);
            controller = _newLegacyController!();
            controller.ControllerContext = new ControllerContext { HttpContext = spelled };
            var answer = await Executed(controller.GetHlsVideoSegmentLegacy(_item.ToString("N"), playlistId, segmentId, container), spelled, spelledBody);
            Assert.True(answer.Status == StatusCodes.Status404NotFound, FormattableString.Invariant($"{tail}: {answer.Status} \"{answer.Text}\""));
        }

        // The audio route's extension is the url's too, and an url ending in a slash has none.
        var slashed = Context(FormattableString.Invariant($"/Audio/{_item:N}/hls/{playlistId}.mp4/stream.mp3/"), null, OwnerDevice, out var slashedBody);
        controller = _newLegacyController!();
        controller.ControllerContext = new ControllerContext { HttpContext = slashed };
        var fromSlashed = await Executed(controller.GetHlsAudioSegmentLegacy(_item.ToString("N"), playlistId + ".mp4"), slashed, slashedBody);
        Assert.True(fromSlashed.Status == StatusCodes.Status404NotFound, FormattableString.Invariant($"trailing slash: {fromSlashed.Status} \"{fromSlashed.Text}\""));

        // And it is not that these routes serve nothing: a segment of the job's is still the owner's.
        Assert.Equal("segment 1", (await LegacySegmentRequest(attempt, 1)).Text);
        Assert.Equal("segment 1", (await LegacyAudioRequest(attempt, 1)).Text);
        Assert.Equal("init", (await LegacySegmentRequest(attempt, Init)).Text);
    }

    // ---------------------------------------------------------------- whose transcodes a stop reaches

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StopOnAPlaySessionTwoUsersNamed_StopsTheCallersJobAndOnlyThat_WhicheverIsRegisteredFirst(bool callersFirst)
    {
        // One play session id, two users: the second one borrowed it for a playback of its own.
        // Two outputs - the User-Agent tells them apart - and two jobs under one id.
        Start mine, theirs;
        if (callersFirst)
        {
            mine = await StartPlayback(hardware: false, segments: 6, userAgent: "one client");
            theirs = await StartPlayback(hardware: false, segments: 6, user: _stranger, device: OtherDevice, namedDevice: OtherDevice, userAgent: "another client");
        }
        else
        {
            theirs = await StartPlayback(hardware: false, segments: 6, user: _stranger, device: OtherDevice, namedDevice: OtherDevice, userAgent: "another client");
            mine = await StartPlayback(hardware: false, segments: 6, userAgent: "one client");
        }

        Assert.NotEqual(mine.Job.Path, theirs.Job.Path);
        Assert.Equal(mine.Job.PlaySessionId, theirs.Job.PlaySessionId);

        await StopEncodings(SessionA);

        Assert.False(theirs.Job.HasExited, "one user's DELETE Videos/ActiveEncodings stopped another user's transcode");
        Assert.Same(theirs.Job, _manager!.GetTranscodingJob(theirs.Job.Path!, TranscodingJobType.Hls));

        // And it is the 204 of a stop that has happened: the caller's own process has ended, and
        // its job is gone, by the time the route answers.
        Assert.True(Stopped(mine), "DELETE Videos/ActiveEncodings answered before the caller's own transcode had stopped");
        Assert.Null(_manager.GetTranscodingJob(mine.Job.Path!, TranscodingJobType.Hls));
        Assert.Null(mine.Job.Failure);
    }

    [Fact]
    public async Task StopNamingWhatIsNotTheCallers_StopsNothingOfAnybodyElses()
    {
        var mine = await StartPlayback(hardware: false, segments: 6, userAgent: "one client");
        var theirs = await StartPlayback(hardware: false, segments: 6, playSession: SessionB, user: _stranger, device: OtherDevice, namedDevice: OtherDevice, userAgent: "another client");

        // Somebody else's play session id; no play session id at all; one that nothing carries.
        await StopEncodings(SessionB);
        await StopEncodings(" ");
        await StopEncodings("33333333333333333333333333333333");
        Assert.False(theirs.Job.HasExited);
        Assert.False(mine.Job.HasExited);

        // The caller's own play session with somebody else's device id: the device id selects
        // nothing, so this is the caller's own job and no other.
        await StopEncodings(SessionA, namedDevice: OtherDevice);
        Assert.True(Stopped(mine));
        Assert.False(theirs.Job.HasExited, "a borrowed device id stopped that device's transcode");
        Assert.Same(theirs.Job, _manager!.GetTranscodingJob(theirs.Job.Path!, TranscodingJobType.Hls));

        // Again: nothing left to stop, and the same answer.
        await StopEncodings(SessionA);
        Assert.False(theirs.Job.HasExited);
    }

    [Fact]
    public async Task JobReplacedBetweenTheDecisionAndTheStop_TheOneThatReplacedItIsNotStopped()
    {
        var mine = await StartPlayback(hardware: false, segments: 6, userAgent: "one client");

        // The caller has been found to own the job its play session names, and nothing is stopped yet.
        var decided = new Gate("the stop, once decided");
        _stopDecisionGate = decided;
        var stop = StopEncodings(SessionA);
        await decided.Reached;

        // Meanwhile that job is released, and another user starts one under the same play session id.
        await _manager!.KillTranscodingJobs(OwnerDevice, SessionA, _ => false);
        var theirs = await StartPlayback(hardware: false, segments: 6, user: _stranger, device: OtherDevice, namedDevice: OtherDevice, userAgent: "another client");

        decided.Open();
        await stop.WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.True(Stopped(mine));
        Assert.False(theirs.Job.HasExited, "a stop decided on one job stopped the one that took its place under the same play session id");
        Assert.Same(theirs.Job, _manager.GetTranscodingJob(theirs.Job.Path!, TranscodingJobType.Hls));
    }

    [Fact]
    public async Task StopThatFails_IsAnError_NotA204()
    {
        await StartPlayback(hardware: false, segments: 6);

        // Only on the request's own flow of control: the same event is raised when a process exits.
        _manager!.TranscodingJobEnded += (_, _) =>
        {
            if (GatedFileSystem.Actor.Value == "stop")
            {
                throw new InvalidOperationException("the stop could not be completed");
            }
        };

        // The manager's own error, carrying what went wrong - not any exception at all.
        var error = await Assert.ThrowsAsync<AggregateException>(() => StopEncodings(SessionA));
        Assert.Contains(error.Flatten().InnerExceptions, e => e.Message == "the stop could not be completed");
    }

    [Fact]
    public async Task EmptyingAReleasedOutput_LeavesAProgressiveTranscodesFileOfTheSameNameAlone()
    {
        // The same request as a progressive transcode writes "{name}.{ext}" in the same folder,
        // under a job that is not an HLS job and may well be running.
        var theirs = await StartPlayback(hardware: false, segments: 6);
        await _manager!.KillTranscodingJobs(OwnerDevice, SessionA, _ => false);
        var progressive = theirs.Prefix + ".mp4";
        await File.WriteAllTextAsync(progressive, "a progressive transcode", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(theirs.Prefix + ".m3u8", "#EXTM3U", TestContext.Current.CancellationToken);

        var next = NextStart();
        var replay = Request(2, actor: "replay", user: _stranger, device: OtherDevice);
        var mine = await next.WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.Equal([progressive], mine.FilesWhenStarted);
        mine.Write(2, "mine 2");
        mine.Write(3, "mine 3");
        Assert.Equal("mine 2", (await replay.WaitAsync(Patience, TestContext.Current.CancellationToken)).Text);
    }

    [Fact]
    public async Task StopAnswersOnceTheProcessHasEnded_WithoutWaitingForItsFilesToBeRemoved()
    {
        var mine = await StartPlayback(hardware: false, segments: 6);
        var beforeRemoval = _fileSystem.PauseBeforeRemovalBy("stop", mine.Prefix);

        await StopEncodings(SessionA).WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        // Answered, and the removal has not begun: it is the process the route speaks for.
        Assert.True(Stopped(mine));
        Assert.Equal(7, mine.FilesOnDisk().Count);

        // The removal still happens.
        await beforeRemoval.Reached;
        beforeRemoval.Open();
        await Eventually(() => mine.FilesOnDisk().Count == 0);
    }

    [Theory]
    [InlineData("owner", false)]
    [InlineData("owner", true)]
    [InlineData("stranger", false)]
    [InlineData("stranger", true)]
    [InlineData("owner's other device", false)]
    [InlineData("owner's other device", true)]
    public async Task PlaybackStopReport_StopsTheReportersOwnTranscode_NotTheOneItsPlaySessionIdNames(string reporter, bool legacyRoute)
    {
        // Both routes a stop is reported on: POST Sessions/Playing/Stopped, and the obsolete
        // DELETE PlayingItems/{itemId} that older clients still send.
        var reportedByItsOwner = reporter == "owner";
        var path = legacyRoute ? FormattableString.Invariant($"/PlayingItems/{_item:N}") : "/Sessions/Playing/Stopped";
        var theirs = await StartPlayback(hardware: false, segments: 6);
        _sessionManager
            .Setup(s => s.LogSessionActivity(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<Tesserafin.Data.Entities.User?>()))
            .ReturnsAsync(() => new SessionInfo(_sessionManager.Object, NullLogger.Instance) { Id = "a-session" });

        var httpContext = reporter == "stranger"
            ? Context(path, _stranger, OtherDevice, out _)
            : Context(path, null, reportedByItsOwner ? OwnerDevice : OtherDevice, out _);
        var controller = new PlaystateController(
            Mock.Of<IUserManager>(),
            Mock.Of<IUserDataManager>(),
            Mock.Of<IItemAccessService>(),
            Mock.Of<IItemPlayedStateService>(),
            _sessionManager.Object,
            NullLoggerFactory.Instance,
            _gated!,
            new HlsJobOwnershipAuthorizer(_manager!, _sessionManager.Object))
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };

#pragma warning disable CS0618 // The obsolete route is one of the two under test.
        var result = await Task.Run(
            () => legacyRoute
                ? controller.OnPlaybackStopped(_item, null, null, null, null, SessionA)
                : controller.ReportPlaybackStopped(new PlaybackStopInfo { PlaySessionId = SessionA }),
            TestContext.Current.CancellationToken);
#pragma warning restore CS0618

        Assert.IsType<NoContentResult>(result);
        Assert.Equal(reportedByItsOwner, Stopped(theirs));
        Assert.Equal(reportedByItsOwner, _manager!.GetTranscodingJob(theirs.Job.Path!, TranscodingJobType.Hls) is null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlaybackStopReport_IsRecorded_EvenWhenTheTranscodeCouldNotBeStopped(bool legacyRoute)
    {
        // The same probe as StopThatDoesNotEndTheProcess: a stop that ends nothing.
        var mine = await StartPlayback(hardware: false, segments: 6);
        mine.Job.Process = System.Diagnostics.Process.GetProcessById(mine.Process.Id);
        _sessionManager
            .Setup(s => s.LogSessionActivity(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<Tesserafin.Data.Entities.User?>()))
            .ReturnsAsync(() => new SessionInfo(_sessionManager.Object, NullLogger.Instance) { Id = "a-session" });
        var controller = new PlaystateController(
            Mock.Of<IUserManager>(),
            Mock.Of<IUserDataManager>(),
            Mock.Of<IItemAccessService>(),
            Mock.Of<IItemPlayedStateService>(),
            _sessionManager.Object,
            NullLoggerFactory.Instance,
            _gated!,
            new HlsJobOwnershipAuthorizer(_manager!, _sessionManager.Object))
        {
            ControllerContext = new ControllerContext { HttpContext = Context("/Sessions/Playing/Stopped", null, OwnerDevice, out _) }
        };

#pragma warning disable CS0618 // The obsolete route is one of the two under test.
        var error = await Assert.ThrowsAsync<AggregateException>(() => Task.Run(
            () => legacyRoute
                ? controller.OnPlaybackStopped(_item, null, null, null, null, SessionA)
                : controller.ReportPlaybackStopped(new PlaybackStopInfo { PlaySessionId = SessionA }),
            TestContext.Current.CancellationToken));
#pragma warning restore CS0618

        Assert.Contains(error.Flatten().InnerExceptions, e => e is TimeoutException);
        _sessionManager.Verify(s => s.OnPlaybackStopped(It.Is<PlaybackStopInfo>(i => i.PlaySessionId == SessionA && i.SessionId == "a-session")), Times.Once);
    }

    /// <summary>
    /// <c>DELETE Videos/ActiveEncodings</c> through the real action, as the owner unless said otherwise.
    /// </summary>
    private Task StopEncodings(string playSession, string namedDevice = OwnerDevice)
    {
        var httpContext = Context("/Videos/ActiveEncodings", null, OwnerDevice, out _);
        var controller = _newLegacyController!();
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        return Task.Run(async () =>
        {
            GatedFileSystem.Actor.Value = "stop";
            Assert.IsType<NoContentResult>(await controller.StopEncodingProcess(namedDevice, playSession).ConfigureAwait(false));
        });
    }

    /// <summary>
    /// Whether the process is gone, asked of the system: the job only says so once the exit has
    /// been handled, and a stop answers as soon as the process has ended.
    /// </summary>
    private static bool Stopped(Start start)
    {
        try
        {
            return start.Process.HasExited;
        }
        catch (InvalidOperationException)
        {
            // The job has been disposed of, which happens once its process's exit has been handled.
            return true;
        }
    }

    private static async Task Eventually(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "It never happened.");
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }

    private sealed class LostTuner : MemoryStream
    {
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("the tuner is gone");

        public override int Read(Span<byte> buffer) => throw new IOException("the tuner is gone");

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => throw new IOException("the tuner is gone");

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => throw new IOException("the tuner is gone");
    }
}
