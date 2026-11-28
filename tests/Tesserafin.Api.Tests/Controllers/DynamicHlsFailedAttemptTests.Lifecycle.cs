#pragma warning disable CS0618 // tesserafin#129 step 7: the legacy route under test is deprecated, and this test goes with it.
using System;
using System.Collections.Generic;
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
using Tesserafin.Api.Constants;
using Tesserafin.Controller.MediaEncoding;
using Xunit;

namespace Tesserafin.Api.Tests.Controllers;

/// <summary>
/// POLISH-2-R3. What #290 changed is shared: the output's lock, and who removes an output when.
/// These are the neighbours of the route it was proven on - the live playlist that holds that
/// lock while it waits, the legacy segment routes that serve a live output's files, and the
/// removal a release schedules for later.
/// </summary>
/// <remarks>
/// None of these reproduces tesserafin#289. Each says which regression it would catch.
/// </remarks>
public sealed partial class DynamicHlsFailedAttemptTests
{
    private bool _liveWithoutIds;

    // ---------------------------------------------------------------- the live playlist holds the lock while it waits

    [Fact]
    public async Task LiveRequestWaitingForSegments_IsReleasedWhenTheAttemptFails_AndDoesNotHoldItsEndBack()
    {
        Configure(hardware: true);
        var next = NextStart();
        var live = LiveRequest(minSegments: 1);
        var attempt = await next.WaitAsync(Patience, TestContext.Current.CancellationToken);

        // A playlist with no segment in it yet: the request has its transcode and waits for the
        // first segment, holding the output's lock.
        await File.WriteAllTextAsync(attempt.Job.Path!, "#EXTM3U\n", TestContext.Current.CancellationToken);
        Assert.True(SpinWait.SpinUntil(() => attempt.Job.IsLiveOutput, Patience), "the live request never got its transcode");

        var reportedEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _manager!.TranscodingJobEnded += (_, job) =>
        {
            if (ReferenceEquals(job, attempt.Job))
            {
                reportedEnded.TrySetResult();
            }
        };

        Fail(attempt, 134, DeviceLost);

        // The end of the process is published and reported whoever holds the lock.
        await reportedEnded.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);
        Assert.True(attempt.Job.HasExited);
        Assert.NotNull(attempt.Job.Failure);

        // And the request that was waiting on that process is let go, with the reason.
        AssertFailed(await live.WaitAsync(Patience, TestContext.Current.CancellationToken), "software");

        await attempt.Ended.WaitAsync(Patience, TestContext.Current.CancellationToken);
        Assert.Empty(attempt.FilesOnDisk());
        Assert.Single(_starts);
    }

    [Fact]
    public async Task LivePlaylistOfAnAttemptSoftwareTakesOverFrom_IsNeitherServedNorRestartedInPlace()
    {
        Configure(hardware: true);
        var next = NextStart();
        var first = LiveRequest(minSegments: 1);
        var attempt = await next.WaitAsync(Patience, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(attempt.Job.Path!, "#EXTM3U\n#EXTINF:3.0,\nx0.ts\n", TestContext.Current.CancellationToken);
        Assert.Equal(StatusCodes.Status200OK, (await first.WaitAsync(Patience, TestContext.Current.CancellationToken)).Status);

        // Published, and its playlist still on disk.
        var beforeRemoval = _fileSystem.PauseBeforeRemoval(attempt.Prefix);
        Fail(attempt, 134, DeviceLost);
        await beforeRemoval.Reached;
        var whileThere = await Settled(LiveRequest(minSegments: 1));

        beforeRemoval.Open();
        await attempt.Ended.WaitAsync(Patience, TestContext.Current.CancellationToken);
        Assert.False(File.Exists(attempt.Job.Path));
        var onceRemoved = await Settled(LiveRequest(minSegments: 1));

        AssertFailed(whileThere, "software");
        AssertFailed(onceRemoved, "software");
        Assert.Single(_starts);
    }

    [Fact]
    public async Task LiveRequestQueuedBehindAStartThatFails_IsToldSo_AndStartsNothing()
    {
        Configure(hardware: true);
        var next = NextStart();
        var first = LiveRequest(minSegments: 1);
        var attempt = await next.WaitAsync(Patience, TestContext.Current.CancellationToken);

        // A second request for the same playlist: nothing on disk yet, nothing failed yet, and it
        // is about to wait for the lock the first one holds while its transcode starts.
        var beforeLock = new Gate("the lock");
        _lockGate = beforeLock;
        var second = LiveRequest(minSegments: 1);
        await beforeLock.Reached;

        // The transcode fails before it writes anything.
        await FailAndWait(attempt, 251, "Device creation failed: -5.");
        AssertFailed(await Settled(first), "software");
        beforeLock.Open();

        AssertFailed(await Settled(second), "software");
        Assert.Single(_starts);
    }

    [Fact]
    public async Task LiveRequestDecidedBeforeTheJobExisted_IsStillToldSo_WhenThatJobFails()
    {
        Configure(hardware: true);

        // This request arrives first, finds no job and no playlist, and stops before the lock.
        var beforeLock = new Gate("the lock");
        _lockGate = beforeLock;
        var early = LiveRequest(minSegments: 1);
        await beforeLock.Reached;

        // Another one for the same playlist overtakes it, starts the transcode, and that fails.
        var next = NextStart();
        var starter = LiveRequest(minSegments: 1);
        var attempt = await next.WaitAsync(Patience, TestContext.Current.CancellationToken);
        await FailAndWait(attempt, 251, "Device creation failed: -5.");
        AssertFailed(await Settled(starter), "software");
        beforeLock.Open();

        AssertFailed(await Settled(early), "software");
        Assert.Single(_starts);
    }

    [Fact]
    public async Task LegacyRoutes_KeepRefusingAFailedAttemptsFiles_PastTheThirtySeconds()
    {
        var attempt = await StartPlayback(hardware: true, segments: 6);
        var beforeRemoval = _fileSystem.PauseBeforeRemoval(attempt.Prefix);
        Fail(attempt, 134, DeviceLost);
        await beforeRemoval.Reached;

        // The files are still there, and the answer the segment route keeps has run out.
        _clock.Advance(FailureAnswerLifetime + TimeSpan.FromTicks(1));
        var video = await LegacySegmentRequest(attempt, 1);
        var audio = await LegacyAudioRequest(attempt, 1);

        beforeRemoval.Open();
        await attempt.Ended.WaitAsync(Patience, TestContext.Current.CancellationToken);

        AssertFailed(video, "software");
        AssertFailed(audio, "software");
    }

    [Fact]
    public async Task LiveRequestOfAnotherUserDecidedBeforeTheJobExisted_DoesNotGetItsPlaylist()
    {
        Configure(hardware: true);

        // Another user, replaying the owner's url, arrives when there is neither a job nor a
        // playlist - nothing to refuse yet - and stops before the lock.
        var beforeLock = new Gate("the lock");
        _lockGate = beforeLock;
        var stranger = LiveRequest(minSegments: 1, user: _stranger, device: "another-device");
        await beforeLock.Reached;

        var next = NextStart();
        var owner = LiveRequest(minSegments: 1);
        var attempt = await next.WaitAsync(Patience, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(attempt.Job.Path!, "#EXTM3U\n#EXTINF:3.0,\nx0.ts\n", TestContext.Current.CancellationToken);
        Assert.Equal(StatusCodes.Status200OK, (await owner.WaitAsync(Patience, TestContext.Current.CancellationToken)).Status);
        beforeLock.Open();

        var response = await Settled(stranger);
        Assert.Equal(StatusCodes.Status401Unauthorized, response.Status);
        Assert.DoesNotContain("x0.ts", response.Text, StringComparison.Ordinal);
        Assert.Single(_starts);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LivePlaylistAfterTheFailureAnswerExpired_StartsOneAttempt_AndTheFailedOneIsGone(bool clientNamesItself)
    {
        Configure(hardware: true);
        _liveWithoutIds = !clientNamesItself;
        var next = NextStart();
        var first = LiveRequest(minSegments: 1);
        var attempt = await next.WaitAsync(Patience, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(attempt.Job.Path!, "#EXTM3U\n#EXTINF:3.0,\nx0.ts\n", TestContext.Current.CancellationToken);
        Assert.Equal(StatusCodes.Status200OK, (await first.WaitAsync(Patience, TestContext.Current.CancellationToken)).Status);
        await FailAndWait(attempt, 134, DeviceLost);

        _clock.Advance(FailureAnswerLifetime + TimeSpan.FromTicks(1));
        next = NextStart();
        var again = LiveRequest(minSegments: 1);
        var successor = await next.WaitAsync(Patience, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(successor.Job.Path!, "#EXTM3U\n#EXTINF:3.0,\ny0.ts\n", TestContext.Current.CancellationToken);
        var response = await again.WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status200OK, response.Status);
        Assert.Contains("y0.ts", response.Text, StringComparison.Ordinal);
        Assert.Same(successor.Job, _manager!.GetTranscodingJob(successor.Job.Path!, TranscodingJobType.Hls));
        Assert.Equal(2, _starts.Count);
    }

    [Fact]
    public async Task LiveRestartByAClientThatNamesNothing_StopsNobodyElsesTranscode()
    {
        // Somebody else's healthy transcode, started without a device id or a play session id.
        Configure(hardware: true);
        _liveWithoutIds = true;
        var next = NextStart();
        var theirs = LiveRequest(minSegments: 1, user: _stranger, device: "another-device", userAgent: "another client");
        var bystander = await next.WaitAsync(Patience, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(bystander.Job.Path!, "#EXTM3U\n#EXTINF:3.0,\nz0.ts\n", TestContext.Current.CancellationToken);
        Assert.Equal(StatusCodes.Status200OK, (await theirs.WaitAsync(Patience, TestContext.Current.CancellationToken)).Status);

        // The owner's, as nameless, fails; the answer runs out; the owner asks again.
        next = NextStart();
        var first = LiveRequest(minSegments: 1);
        var attempt = await next.WaitAsync(Patience, TestContext.Current.CancellationToken);
        Assert.NotEqual(bystander.Job.Path, attempt.Job.Path);
        await File.WriteAllTextAsync(attempt.Job.Path!, "#EXTM3U\n#EXTINF:3.0,\nx0.ts\n", TestContext.Current.CancellationToken);
        Assert.Equal(StatusCodes.Status200OK, (await first.WaitAsync(Patience, TestContext.Current.CancellationToken)).Status);
        await FailAndWait(attempt, 134, DeviceLost);
        _clock.Advance(FailureAnswerLifetime + TimeSpan.FromTicks(1));
        next = NextStart();
        var again = LiveRequest(minSegments: 1);
        var successor = await next.WaitAsync(Patience, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(successor.Job.Path!, "#EXTM3U\n#EXTINF:3.0,\ny0.ts\n", TestContext.Current.CancellationToken);
        Assert.Equal(StatusCodes.Status200OK, (await again.WaitAsync(Patience, TestContext.Current.CancellationToken)).Status);

        Assert.False(bystander.Job.HasExited, "a live restart stopped another user's transcode");
        Assert.Same(bystander.Job, _manager!.GetTranscodingJob(bystander.Job.Path!, TranscodingJobType.Hls));
        Assert.Same(successor.Job, _manager.GetTranscodingJob(successor.Job.Path!, TranscodingJobType.Hls));
    }

    [Fact]
    public async Task InitialisationSegmentRequestThatStartedAnAttemptWhichFailsWithoutFallback_GetsTheFile()
    {
        Configure(hardware: false);
        var next = NextStart();
        var init = Request(Init);
        var attempt = await next.WaitAsync(Patience, TestContext.Current.CancellationToken);

        // The request is waiting on the attempt it started; the attempt writes its initialisation
        // segment and one segment, and ends there.
        var waiting = _fileSystem.PauseAfterLook(null, attempt.Segment(1), occurrence: 3);
        attempt.Write(Init, "init");
        attempt.Write(0, "segment 0");
        await waiting.Reached;
        await FailAndWait(attempt, 1, "something nobody recognises");
        waiting.Open();

        var response = await Settled(init);
        Assert.Equal(StatusCodes.Status200OK, response.Status);
        Assert.Equal("init", response.Text);
        Assert.Single(_starts);
    }

    // ---------------------------------------------------------------- the legacy routes serve a live output's files

    [Fact]
    public async Task LegacySegmentRoute_ServesAHealthyAttemptsSegment()
    {
        var attempt = await StartPlayback(hardware: true, segments: 6);

        var response = await LegacySegmentRequest(attempt, 1);

        Assert.Equal(StatusCodes.Status200OK, response.Status);
        Assert.Equal("segment 1", response.Text);
    }

    [Fact]
    public async Task LegacySegmentRoute_DoesNotServeWhatAFailedAttemptWrote_WhenSoftwareTakesOver()
    {
        var attempt = await StartPlayback(hardware: true, segments: 6);

        // The failure is published and nothing has been removed yet: the file is there to be served.
        var beforeRemoval = _fileSystem.PauseBeforeRemoval(attempt.Prefix);
        Fail(attempt, 134, DeviceLost);
        await beforeRemoval.Reached;

        var whileThere = await LegacySegmentRequest(attempt, 1);
        beforeRemoval.Open();
        await attempt.Ended.WaitAsync(Patience, TestContext.Current.CancellationToken);
        var onceRemoved = await LegacySegmentRequest(attempt, 1);

        AssertFailed(whileThere, "software");
        Assert.DoesNotContain("segment 1", whileThere.Text, StringComparison.Ordinal);
        AssertFailed(onceRemoved, "software");
    }

    [Fact]
    public async Task LegacySegmentRoute_StillRefusesAnotherUser_AfterAFailure()
    {
        var attempt = await StartPlayback(hardware: true, segments: 6);
        await FailAndWait(attempt, 134, DeviceLost);

        var response = await LegacySegmentRequest(attempt, 1, user: _stranger, device: "another-device");

        Assert.Equal(StatusCodes.Status401Unauthorized, response.Status);
        Assert.Null(response.Recovery);
    }

    // ---------------------------------------------------------------- the removal a release schedules for later

    [Fact]
    public async Task ReleaseThenAStartOnTheSameOutput_TheLateRemovalLeavesTheSuccessorAlone()
    {
        var attempt = await StartPlayback(hardware: true, segments: 6);

        // What DELETE /Videos/ActiveEncodings does, and does not wait for: the job is gone at once,
        // its files a second and a half later.
        var release = _manager!.KillTranscodingJobs(OwnerDevice, SessionA, _ => true);

        // A request still on its way for that play session, for a segment the released attempt
        // never wrote: a fresh attempt, by design.
        var next = NextStart();
        var request = Request(8, actor: "successor");
        var successor = await next.WaitAsync(Patience, TestContext.Current.CancellationToken);

        // The late removal has not run yet: this run is the one the guard exists for. What the
        // released attempt left is gone all the same - the start emptied the output (POLISH-2-R4).
        Assert.False(release.IsCompleted, "the removal ran before the successor started; this run shows nothing");
        Assert.Empty(successor.FilesWhenStarted);
        successor.Write(Init, "successor init");
        successor.Write(8, "successor 8");
        successor.Write(9, "successor 9");
        var response = await request.WaitAsync(Patience, TestContext.Current.CancellationToken);

        await release.WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status200OK, response.Status);
        Assert.Equal("successor 8", response.Text);
        Assert.Equal(attempt.Job.Path, successor.Job.Path);
        Assert.True(File.Exists(successor.Segment(Init)), "the released attempt's late removal took its successor's initialisation segment");
        Assert.True(File.Exists(successor.Segment(8)), "the released attempt's late removal took its successor's segment");
    }

    [Fact]
    public async Task LateRemovalAfterARelease_HoldsTheLockAStartNeeds()
    {
        var attempt = await StartPlayback(hardware: true, segments: 6);

        _fileSystem.RemoveFirst = attempt.Segment(0);
        var midRemoval = _fileSystem.PauseAfterRemoval(attempt.Segment(0));
        var release = _manager!.KillTranscodingJobs(OwnerDevice, SessionA, _ => true);
        await midRemoval.Reached;

        using (var giveUp = new CancellationTokenSource())
        {
            var startersLock = _manager.LockAsync(attempt.Job.Path!, giveUp.Token).AsTask();
            Assert.False(startersLock.IsCompleted, "a released output is being removed outside the lock a start takes");
            await giveUp.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => startersLock);
        }

        midRemoval.Open();
        await release.WaitAsync(Patience, TestContext.Current.CancellationToken);
        Assert.Empty(attempt.FilesOnDisk());
    }

    [Fact]
    public async Task StopReleaseAndSeekMeetingADyingAttempt_NothingThrows_NothingBlocks_TheSuccessorKeepsItsOutput()
    {
        var attempt = await StartPlayback(hardware: true, segments: 6);

        // The process is gone and judged, and nothing says so yet.
        var judged = new Gate("the verdict on the failed attempt");
        _verdictGate = judged;
        Fail(attempt, 134, DeviceLost);
        await judged.Reached;

        // The viewer stops, the client releases, and a seek is still on its way. The first of the
        // two takes the job; the second finds none, which is what a second one does.
        var stop = _manager!.KillTranscodingJobs(OwnerDevice, SessionA, _ => true);
        var release = _manager.KillTranscodingJobs(OwnerDevice, SessionA, _ => true);
        var next = NextStart();
        var seek = Request(40, actor: "successor");
        if (await Task.WhenAny(next, seek).WaitAsync(Patience, TestContext.Current.CancellationToken) == seek)
        {
            await seek;
        }

        var successor = await next.WaitAsync(Patience, TestContext.Current.CancellationToken);
        Assert.False(stop.IsCompleted, "the removal ran before the successor started; this run shows nothing");
        Assert.Empty(successor.FilesWhenStarted);
        successor.Write(Init, "successor init");
        successor.Write(40, "successor 40");
        successor.Write(41, "successor 41");
        var response = await seek.WaitAsync(Patience, TestContext.Current.CancellationToken);

        judged.Open();
        await attempt.Ended.WaitAsync(Patience, TestContext.Current.CancellationToken);
        await Task.WhenAll(stop, release).WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status200OK, response.Status);
        Assert.Equal("successor 40", response.Text);
        Assert.True(File.Exists(successor.Segment(Init)), "the successor lost its initialisation segment");
        Assert.True(File.Exists(successor.Segment(40)), "the successor lost a segment");
        Assert.Equal(2, _starts.Count);
    }

    // ---------------------------------------------------------------- driving the neighbours

    private DefaultHttpContext Context(string path, Guid? user, string device, out MemoryStream body)
    {
        var identity = new ClaimsIdentity("CustomAuthentication");
        identity.AddClaim(new Claim(InternalClaimTypes.UserId, (user ?? _owner).ToString("N")));
        identity.AddClaim(new Claim(InternalClaimTypes.DeviceId, device));
        body = new MemoryStream();
        return new DefaultHttpContext
        {
            Request = { Method = HttpMethods.Get, Path = new PathString(path) },
            Response = { Body = body },
            User = new ClaimsPrincipal(identity),
            RequestServices = _services
        };
    }

    private static async Task<Response> Executed(ActionResult result, HttpContext httpContext, MemoryStream body)
    {
        await result.ExecuteResultAsync(new ActionContext(httpContext, new RouteData(), new ActionDescriptor())).ConfigureAwait(false);
        return new Response(
            httpContext.Response.StatusCode,
            httpContext.Response.Headers.TryGetValue("X-Tesserafin-Playback-Recovery", out var recovery) ? recovery.ToString() : null,
            Encoding.UTF8.GetString(body.ToArray()));
    }

    /// <summary>
    /// <c>Videos/{itemId}/live.m3u8</c> through the real action.
    /// </summary>
    private Task<Response> LiveRequest(int minSegments, Guid? user = null, string device = OwnerDevice, string? userAgent = null)
    {
        var httpContext = Context(FormattableString.Invariant($"/Videos/{_item:N}/live.m3u8"), user, device, out var body);
        if (userAgent is not null)
        {
            // Part of what names an output; without it two nameless clients on one film share one.
            httpContext.Request.Headers.UserAgent = userAgent;
        }

        var named = !_liveWithoutIds;
        var controller = _newController!();
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        return Task.Run(async () =>
        {
            var result = await controller.GetLiveHlsStream(
                itemId: _item,
                container: "ts",
                @static: null,
                @params: null,
                tag: null,
                deviceProfileId: null,
                playSessionId: named ? SessionA : null,
                segmentContainer: "ts",
                segmentLength: 3,
                minSegments: minSegments,
                mediaSourceId: MediaSourceId,
                deviceId: named ? OwnerDevice : null,
                audioCodec: "aac",
                enableAutoStreamCopy: false,
                allowVideoStreamCopy: false,
                allowAudioStreamCopy: false,
                audioSampleRate: null,
                maxAudioBitDepth: null,
                audioBitRate: 128000,
                audioChannels: null,
                maxAudioChannels: 2,
                profile: null,
                level: null,
                framerate: null,
                maxFramerate: null,
                copyTimestamps: null,
                startTimeTicks: null,
                width: null,
                height: null,
                videoBitRate: 2_000_000,
                subtitleStreamIndex: null,
                subtitleMethod: null,
                maxRefFrames: null,
                maxVideoBitDepth: null,
                requireAvc: null,
                deInterlace: null,
                requireNonAnamorphic: null,
                transcodingMaxAudioChannels: null,
                cpuCoreLimit: null,
                liveStreamId: null,
                enableMpegtsM2TsMode: null,
                videoCodec: "h264",
                subtitleCodec: null,
                transcodeReasons: null,
                audioStreamIndex: null,
                videoStreamIndex: null,
                context: null,
                streamOptions: new Dictionary<string, string>(),
                maxWidth: null,
                maxHeight: null,
                enableSubtitlesInManifest: null).ConfigureAwait(false);

            return await Executed(result, httpContext, body).ConfigureAwait(false);
        });
    }

    /// <summary>
    /// <c>Videos/{itemId}/hls/{playlistId}/{segmentId}.{container}</c>, the route a live playlist's
    /// segment uris point at, for one of this attempt's files.
    /// </summary>
    private Task<Response> LegacySegmentRequest(Start attempt, int segment, Guid? user = null, string device = OwnerDevice)
    {
        var playlistId = Path.GetFileName(attempt.Prefix);
        var segmentId = Path.GetFileNameWithoutExtension(attempt.Segment(segment));
        var httpContext = Context(FormattableString.Invariant($"/Videos/{_item:N}/hls/{playlistId}/{segmentId}.mp4"), user, device, out var body);
        var controller = _newLegacyController!();
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        return Task.Run(() => Executed(controller.GetHlsVideoSegmentLegacy(_item.ToString("N"), playlistId, segmentId, "mp4"), httpContext, body));
    }

    /// <summary>
    /// <c>Audio/{itemId}/hls/{segmentId}/stream.aac</c>, the audio sibling, which selects its job by the segment's name.
    /// </summary>
    private Task<Response> LegacyAudioRequest(Start attempt, int segment)
    {
        var segmentId = Path.GetFileNameWithoutExtension(attempt.Segment(segment));
        var httpContext = Context(FormattableString.Invariant($"/Audio/{_item:N}/hls/{segmentId}/stream.mp4"), null, OwnerDevice, out var body);
        var controller = _newLegacyController!();
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        return Task.Run(() => Executed(controller.GetHlsAudioSegmentLegacy(_item.ToString("N"), segmentId), httpContext, body));
    }
}
