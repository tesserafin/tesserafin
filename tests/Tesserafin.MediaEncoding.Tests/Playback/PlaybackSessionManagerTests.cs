using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Controller.Session;
using Tesserafin.MediaEncoding.Playback;
using Tesserafin.Model.Dlna;
using Tesserafin.Model.Session;
using Xunit;

namespace Tesserafin.MediaEncoding.Tests.Playback;

/// <summary>
/// Lifecycle tests for <see cref="PlaybackSessionManager"/>: create/patch/delete bookkeeping
/// on top of an <see cref="IPlaybackSessionPlanner"/> stub.
/// </summary>
public class PlaybackSessionManagerTests
{
    [Fact]
    public void Create_ViablePlan_StoresAndReturnsSession()
    {
        var options = CreateOptions();
        var plan = CreatePlan();
        var manager = GetManager(planner => planner.Setup(p => p.PlanVideo(options)).Returns(plan));

        var session = manager.Create(new PlaybackSessionRequest(PlaybackMediaKind.Video, options));

        Assert.NotNull(session);
        Assert.Same(plan, session.Plan);
        Assert.Equal(session, manager.Get(session.Id));
    }

    [Fact]
    public void Create_NoViablePlan_ReturnsNullAndDoesNotStore()
    {
        var options = CreateOptions();
        var manager = GetManager(planner => planner.Setup(p => p.PlanAudio(options)).Returns((PlaybackPlan?)null));

        var session = manager.Create(new PlaybackSessionRequest(PlaybackMediaKind.Audio, options));

        Assert.Null(session);
    }

    [Fact]
    public void Patch_ExistingSession_ReplacesPlanAndKeepsId()
    {
        var initialOptions = CreateOptions();
        var patchedOptions = CreateOptions();
        var initialPlan = CreatePlan();
        var patchedPlan = new PlaybackPlan(PlayMethod.Transcode, TranscodeReason.AudioCodecNotSupported);
        var mockPlanner = new Mock<IPlaybackSessionPlanner>();
        mockPlanner.Setup(p => p.PlanVideo(initialOptions)).Returns(initialPlan);
        mockPlanner.Setup(p => p.PlanVideo(patchedOptions)).Returns(patchedPlan);
        var manager = new PlaybackSessionManager(mockPlanner.Object, new Mock<ITranscodeManager>().Object, new Mock<ISessionManager>().Object);
        var session = manager.Create(new PlaybackSessionRequest(PlaybackMediaKind.Video, initialOptions));
        Assert.NotNull(session);

        var patched = manager.Patch(session.Id, new PlaybackSessionRequest(PlaybackMediaKind.Video, patchedOptions));

        Assert.NotNull(patched);
        Assert.Equal(session.Id, patched.Id);
        Assert.Same(patchedPlan, patched.Plan);
        Assert.Same(patchedPlan, manager.Get(session.Id)?.Plan);
    }

    [Fact]
    public void Patch_UnknownSession_ReturnsNull()
    {
        var manager = GetManager(_ => { });

        var patched = manager.Patch(PlaybackSessionId.NewId(), new PlaybackSessionRequest(PlaybackMediaKind.Video, CreateOptions()));

        Assert.Null(patched);
    }

    [Fact]
    public void Delete_ExistingSession_RemovesIt()
    {
        var options = CreateOptions();
        var plan = CreatePlan();
        var manager = GetManager(planner => planner.Setup(p => p.PlanVideo(options)).Returns(plan));
        var session = manager.Create(new PlaybackSessionRequest(PlaybackMediaKind.Video, options));
        Assert.NotNull(session);

        var deleted = manager.Delete(session.Id);

        Assert.True(deleted);
        Assert.Null(manager.Get(session.Id));
    }

    [Fact]
    public void Delete_UnknownSession_ReturnsFalse()
    {
        var manager = GetManager(_ => { });

        Assert.False(manager.Delete(PlaybackSessionId.NewId()));
    }

    [Fact]
    public void Track_StoresSessionWithoutCallingPlanner()
    {
        var manager = GetManager(_ => { });
        var plan = new PlaybackPlan(PlayMethod.DirectStream, default);

        var session = manager.Track(PlaybackMediaKind.Video, plan);

        Assert.Equal(PlaybackMediaKind.Video, session.Kind);
        Assert.Null(session.Request);
        Assert.Same(plan, session.Plan);
        Assert.Equal(session, manager.Get(session.Id));
    }

    [Fact]
    public void Track_SamePlaySessionIdTwice_ReplacesInsteadOfAdding()
    {
        var manager = GetManager(_ => { });
        var initialPlan = new PlaybackPlan(PlayMethod.DirectStream, default);
        var updatedPlan = new PlaybackPlan(PlayMethod.Transcode, TranscodeReason.AudioCodecNotSupported);

        var first = manager.Track(PlaybackMediaKind.Video, initialPlan, "play-session-1");
        var second = manager.Track(PlaybackMediaKind.Video, updatedPlan, "play-session-1");

        Assert.Equal(first.Id, second.Id);
        Assert.Same(updatedPlan, manager.Get(first.Id)?.Plan);
    }

    [Fact]
    public void DeleteByPlaySessionId_RemovesTrackedSession()
    {
        var manager = GetManager(_ => { });
        var session = manager.Track(PlaybackMediaKind.Video, new PlaybackPlan(PlayMethod.Transcode, default), "play-session-1");

        Assert.True(manager.DeleteByPlaySessionId("play-session-1"));
        Assert.Null(manager.Get(session.Id));
        Assert.False(manager.DeleteByPlaySessionId("play-session-1"));
    }

    [Fact]
    public void TranscodingJobEnded_RemovesSessionWithMatchingPlaySessionId()
    {
        var mockTranscodeManager = new Mock<ITranscodeManager>();
        var manager = new PlaybackSessionManager(new Mock<IPlaybackSessionPlanner>().Object, mockTranscodeManager.Object, new Mock<ISessionManager>().Object);
        var session = manager.Track(PlaybackMediaKind.Video, new PlaybackPlan(PlayMethod.Transcode, default), "play-session-1");

        var job = new TranscodingJob(NullLogger<TranscodingJob>.Instance) { PlaySessionId = "play-session-1" };
        mockTranscodeManager.Raise(m => m.TranscodingJobEnded += null, mockTranscodeManager.Object, job);

        Assert.Null(manager.Get(session.Id));
    }

    [Fact]
    public void Create_SamePlaySessionIdTwice_ReplacesInsteadOfAdding()
    {
        var initialOptions = CreateOptions();
        var patchedOptions = CreateOptions();
        var initialPlan = CreatePlan();
        var patchedPlan = new PlaybackPlan(PlayMethod.Transcode, TranscodeReason.AudioCodecNotSupported);
        var mockPlanner = new Mock<IPlaybackSessionPlanner>();
        mockPlanner.Setup(p => p.PlanVideo(initialOptions)).Returns(initialPlan);
        mockPlanner.Setup(p => p.PlanVideo(patchedOptions)).Returns(patchedPlan);
        var manager = new PlaybackSessionManager(mockPlanner.Object, new Mock<ITranscodeManager>().Object, new Mock<ISessionManager>().Object);

        var first = manager.Create(new PlaybackSessionRequest(PlaybackMediaKind.Video, initialOptions), "play-session-1");
        var second = manager.Create(new PlaybackSessionRequest(PlaybackMediaKind.Video, patchedOptions), "play-session-1");

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first.Id, second.Id);
        Assert.Same(patchedPlan, manager.Get(first.Id)?.Plan);
    }

    [Fact]
    public void PlaybackStopped_RemovesSessionWithMatchingPlaySessionId()
    {
        var mockSessionManager = new Mock<ISessionManager>();
        var manager = new PlaybackSessionManager(new Mock<IPlaybackSessionPlanner>().Object, new Mock<ITranscodeManager>().Object, mockSessionManager.Object);
        var session = manager.Track(PlaybackMediaKind.Video, new PlaybackPlan(PlayMethod.DirectPlay, default), "play-session-1");

        var args = new PlaybackStopEventArgs { PlaySessionId = "play-session-1" };
        mockSessionManager.Raise(m => m.PlaybackStopped += null, mockSessionManager.Object, args);

        Assert.Null(manager.Get(session.Id));
    }

    [Fact]
    public void SweepExpired_PastTtl_RemovesSession()
    {
        var manager = new PlaybackSessionManager(new Mock<IPlaybackSessionPlanner>().Object, new Mock<ITranscodeManager>().Object, new Mock<ISessionManager>().Object);
        var session = manager.Track(PlaybackMediaKind.Video, new PlaybackPlan(PlayMethod.DirectPlay, default), "play-session-1");

        var removed = manager.SweepExpired(DateTimeOffset.UtcNow.AddHours(7));

        Assert.Equal(1, removed);
        Assert.Null(manager.Get(session.Id));
    }

    [Fact]
    public void SweepExpired_WithinTtl_KeepsSession()
    {
        var manager = new PlaybackSessionManager(new Mock<IPlaybackSessionPlanner>().Object, new Mock<ITranscodeManager>().Object, new Mock<ISessionManager>().Object);
        var session = manager.Track(PlaybackMediaKind.Video, new PlaybackPlan(PlayMethod.DirectPlay, default), "play-session-1");

        var removed = manager.SweepExpired(DateTimeOffset.UtcNow.AddHours(1));

        Assert.Equal(0, removed);
        Assert.NotNull(manager.Get(session.Id));
    }

    /// <summary>
    /// Issue #43: <c>StoreOrReplace</c>'s own comment claims the same "null does not erase" rule
    /// <c>Patch</c> applies to <c>PlaybackAttemptId</c>, but <c>Request</c> was assigned
    /// unconditionally - so a <see cref="PlaybackSessionManager.Track"/> call (which always passes
    /// <c>request: null</c>) landing on a session a previous <see cref="PlaybackSessionManager.Create"/>
    /// established for the SAME play session id silently nulled the stored request.
    ///
    /// That is not a cosmetic loss. <c>PlaybackSessionsController.EnsureCallerOwnsSessionOrIsAdmin</c>
    /// reads ownership off <c>session.Request?.Options.UserId</c> and forbids when it is null, and
    /// <c>GetPlaybackSessionStream</c> 422s on the same null. The real client sequence - POST
    /// Playback/Sessions, then fetch an HLS segment (DynamicHlsController -> Track, same
    /// PlaySessionId) - therefore locked the session's own OWNER out of their own session: PUT and
    /// DELETE answered 403, GET Stream 422, while an administrator still saw everything. The
    /// client-side teardown of issue #43 cannot work at all while this holds, since DELETE is
    /// exactly the verb the owner is refused.
    /// </summary>
    [Fact]
    public void Track_AfterCreateOnSamePlaySessionId_DoesNotEraseStoredRequest()
    {
        var options = CreateOptions();
        var plan = CreatePlan();
        var mockPlanner = new Mock<IPlaybackSessionPlanner>();
        mockPlanner.Setup(p => p.PlanVideo(options)).Returns(plan);
        var manager = new PlaybackSessionManager(mockPlanner.Object, new Mock<ITranscodeManager>().Object, new Mock<ISessionManager>().Object);
        var request = new PlaybackSessionRequest(PlaybackMediaKind.Video, options);

        var created = manager.Create(request, "play-session-1");
        Assert.NotNull(created);
        Assert.NotNull(created.Request);

        // The HLS segment path: same play session id, no request of its own to contribute.
        var tracked = manager.Track(PlaybackMediaKind.Video, new PlaybackPlan(PlayMethod.Transcode, default), "play-session-1");

        Assert.Equal(created.Id, tracked.Id);
        Assert.Same(request, tracked.Request);
        Assert.Same(request, manager.Get(created.Id)?.Request);
    }

    /// <summary>
    /// Issue #43: the counterpart to
    /// <see cref="Track_AfterCreateOnSamePlaySessionId_DoesNotEraseStoredRequest"/> - "null does not
    /// erase" must not degrade into "null is ignored". A <see cref="PlaybackSessionManager.Create"/>
    /// landing on an existing play session id carries a real, non-null request and MUST replace the
    /// stored one, or a re-plan would keep answering ownership and stream questions from the
    /// superseded request.
    /// </summary>
    [Fact]
    public void Create_AfterTrackOnSamePlaySessionId_ReplacesStoredRequest()
    {
        var options = CreateOptions();
        var plan = CreatePlan();
        var mockPlanner = new Mock<IPlaybackSessionPlanner>();
        mockPlanner.Setup(p => p.PlanVideo(options)).Returns(plan);
        var manager = new PlaybackSessionManager(mockPlanner.Object, new Mock<ITranscodeManager>().Object, new Mock<ISessionManager>().Object);

        var tracked = manager.Track(PlaybackMediaKind.Video, new PlaybackPlan(PlayMethod.DirectPlay, default), "play-session-1");
        Assert.Null(tracked.Request);

        var request = new PlaybackSessionRequest(PlaybackMediaKind.Video, options);
        var created = manager.Create(request, "play-session-1");

        Assert.NotNull(created);
        Assert.Equal(tracked.Id, created.Id);
        Assert.Same(request, created.Request);
    }

    /// <summary>
    /// Issue #43: a session that was only ever <see cref="PlaybackSessionManager.Track"/>ed has no
    /// request to preserve, and the guard must not invent one. Pins that the fix is "keep what is
    /// there", not "always non-null".
    /// </summary>
    [Fact]
    public void Track_TwiceOnSamePlaySessionId_LeavesRequestNull()
    {
        var manager = new PlaybackSessionManager(new Mock<IPlaybackSessionPlanner>().Object, new Mock<ITranscodeManager>().Object, new Mock<ISessionManager>().Object);

        manager.Track(PlaybackMediaKind.Video, new PlaybackPlan(PlayMethod.DirectPlay, default), "play-session-1");
        var second = manager.Track(PlaybackMediaKind.Video, new PlaybackPlan(PlayMethod.Transcode, default), "play-session-1");

        Assert.Null(second.Request);
    }

    [Fact]
    public void GetAll_ReturnsSnapshotOfAllTrackedSessions()
    {
        var manager = new PlaybackSessionManager(new Mock<IPlaybackSessionPlanner>().Object, new Mock<ITranscodeManager>().Object, new Mock<ISessionManager>().Object);
        var first = manager.Track(PlaybackMediaKind.Video, new PlaybackPlan(PlayMethod.DirectPlay, default), "play-session-1");
        var second = manager.Track(PlaybackMediaKind.Audio, new PlaybackPlan(PlayMethod.Transcode, default), "play-session-2");

        var all = manager.GetAll();

        Assert.Equal(2, all.Count);
        Assert.Contains(all, s => s.Id == first.Id);
        Assert.Contains(all, s => s.Id == second.Id);
    }

    private static PlaybackSessionManager GetManager(System.Action<Mock<IPlaybackSessionPlanner>> setup)
    {
        var mockPlanner = new Mock<IPlaybackSessionPlanner>();
        setup(mockPlanner);
        return new PlaybackSessionManager(mockPlanner.Object, new Mock<ITranscodeManager>().Object, new Mock<ISessionManager>().Object);
    }

    private static MediaOptions CreateOptions() => new();

    private static PlaybackPlan CreatePlan() => new(PlayMethod.DirectPlay, default);
}
