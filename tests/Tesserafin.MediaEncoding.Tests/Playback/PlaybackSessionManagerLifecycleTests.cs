using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
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
/// Issue #71: liveness of a v2 playback session — who is allowed to end one, and who is not.
/// <para>
/// The defect: a session established through the v2 HTTP API (<c>POST /Playback/Sessions</c> →
/// <see cref="PlaybackSessionManager.Create"/>) was reaped by
/// <c>ITranscodeManager.TranscodingJobEnded</c>, i.e. by the ffmpeg job ending — which is NOT the
/// same event as playback ending. ffmpeg routinely finishes encoding minutes before the user stops
/// watching, and a seek kills and restarts the job outright
/// (<c>DynamicHlsController.GetDynamicSegment</c> → <c>KillTranscodingJobs</c>). Both left the
/// <c>PlaybackSessionId</c> the client still held permanently dead, so its later <c>DELETE</c>
/// could only 404.
/// </para>
/// <para>
/// Every clock here is injected. <see cref="PlaybackSessionManager.SweepExpired"/> already takes
/// the instant to judge against, and a <see cref="TimeProvider"/> supplies the creation/re-plan
/// stamps, so nothing in this file sleeps or waits on the 30-minute sweep timer.
/// </para>
/// </summary>
public class PlaybackSessionManagerLifecycleTests
{
    private const string PlaySessionA = "play-session-a";
    private const string PlaySessionB = "play-session-b";
    private const string AttemptId = "attempt-1";

    private static readonly DateTimeOffset T0 = new(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// THE red test for issue #71. A transcoding job ending is the ffmpeg process exiting; the user
    /// may still be watching everything ffmpeg already wrote. Nothing about that event says the
    /// client is done with its session, so it must not destroy one the client still holds.
    /// </summary>
    [Fact]
    public void TranscodingJobEnded_ForLiveClientOwnedSession_DoesNotRemoveIt()
    {
        var fixture = new Fixture();
        var session = fixture.CreateClientSession(PlaySessionA);

        fixture.RaiseTranscodingJobEnded(PlaySessionA);

        Assert.NotNull(fixture.Manager.Get(session.Id));
    }

    /// <summary>
    /// Issue #71, second reap path. <c>PlaybackStopped</c> is a genuine end-of-playback signal, but
    /// it is keyed on <c>PlaySessionId</c> — the LEGACY transcode pipeline's key — and
    /// <c>PlaystateController.ReportPlaybackStopped</c> raises it before the client's own
    /// <c>DELETE</c> can land. Leaving this path destructive would make the v2 teardown contract
    /// (issue #43) unobservable: the happy path would answer 404 by construction. The client owns
    /// the session id, so the client ends the session; the TTL sweep remains the backstop.
    /// </summary>
    [Fact]
    public void PlaybackStopped_ForLiveClientOwnedSession_DoesNotRemoveIt()
    {
        var fixture = new Fixture();
        var session = fixture.CreateClientSession(PlaySessionA);

        fixture.RaisePlaybackStopped(PlaySessionA);

        Assert.NotNull(fixture.Manager.Get(session.Id));
    }

    /// <summary>
    /// Issue #71: the explicit client teardown is the removal path that must keep working — it is
    /// the only one left for a client-owned session besides the TTL backstop.
    /// </summary>
    [Fact]
    public void Delete_ClientOwnedSession_StillRemovesIt()
    {
        var fixture = new Fixture();
        var session = fixture.CreateClientSession(PlaySessionA);

        Assert.True(fixture.Manager.Delete(session.Id));
        Assert.Null(fixture.Manager.Get(session.Id));
    }

    /// <summary>
    /// Issue #71, scope boundary. A session only ever tracked
    /// (<see cref="IPlaybackSessionManager.Track"/>), never created by a client, has no client holding an id and no client that will ever <c>DELETE</c> it, so its transcode
    /// job ending remains the right moment to drop it. The fix must not turn those into a leak.
    /// </summary>
    [Fact]
    public void TranscodingJobEnded_ForLegacyTrackedSession_StillRemovesIt()
    {
        var fixture = new Fixture();
        var session = fixture.Manager.Track(PlaybackMediaKind.Video, new PlaybackPlan(PlayMethod.Transcode, default), PlaySessionA);

        fixture.RaiseTranscodingJobEnded(PlaySessionA);

        Assert.Null(fixture.Manager.Get(session.Id));
    }

    /// <summary>
    /// Issue #71 / design §5: a teardown owed by attempt A must never address attempt B's session.
    /// A's play session id is unbound the moment A is deleted, so replaying A's stale
    /// <c>TranscodingJobEnded</c>/<c>PlaybackStopped</c> afterwards can correlate to nothing.
    /// </summary>
    [Fact]
    public void StaleTeardownOfOldAttempt_DoesNotRemoveNewAttemptSession()
    {
        var fixture = new Fixture();
        var sessionA = fixture.CreateClientSession(PlaySessionA);
        Assert.True(fixture.Manager.Delete(sessionA.Id));

        var sessionB = fixture.CreateClientSession(PlaySessionB);

        fixture.RaiseTranscodingJobEnded(PlaySessionA);
        fixture.RaisePlaybackStopped(PlaySessionA);

        Assert.NotNull(fixture.Manager.Get(sessionB.Id));
        Assert.NotEqual(sessionA.Id, sessionB.Id);
    }

    /// <summary>
    /// Issue #71: a <c>PUT</c> can only ever LENGTHEN a session's life. It refreshes
    /// <c>UpdatedAt</c> — which is the only stamp the sweeper reads — and never touches
    /// <c>CreatedAt</c>. Driven off an injected clock: created at T0, re-planned an hour later, then
    /// swept at T0 + TTL + ε, the instant the session would have died had the <c>PUT</c> not
    /// happened.
    /// </summary>
    [Fact]
    public void Patch_AfterCreation_DoesNotShortenLifetime()
    {
        var fixture = new Fixture();
        var session = fixture.CreateClientSession(PlaySessionA);
        Assert.Equal(T0, session.CreatedAt);

        fixture.Clock.Now = T0.AddHours(1);
        var patched = fixture.Manager.Patch(session.Id, fixture.Request);
        Assert.NotNull(patched);

        var removed = fixture.Manager.SweepExpired(T0 + TimeSpan.FromHours(6) + TimeSpan.FromSeconds(1));

        Assert.Equal(0, removed);
        Assert.NotNull(fixture.Manager.Get(session.Id));
        Assert.Equal(T0, patched.CreatedAt);
        Assert.Equal(T0.AddHours(1), patched.UpdatedAt);
    }

    /// <summary>
    /// Issue #71: the TTL backstop still reaps. It is the ONLY automatic removal left for a
    /// client-owned session, so it had better work — and it must judge <c>UpdatedAt</c>, meaning the
    /// re-planned session above survives the same sweep that takes an untouched one.
    /// </summary>
    [Fact]
    public void SweepExpired_PastTtl_StillReapsClientOwnedSession()
    {
        var fixture = new Fixture();
        var stale = fixture.CreateClientSession(PlaySessionA);

        fixture.Clock.Now = T0.AddHours(5);
        var fresh = fixture.CreateClientSession(PlaySessionB);

        var removed = fixture.Manager.SweepExpired(T0 + TimeSpan.FromHours(6) + TimeSpan.FromSeconds(1));

        Assert.Equal(1, removed);
        Assert.Null(fixture.Manager.Get(stale.Id));
        Assert.NotNull(fixture.Manager.Get(fresh.Id));
    }

    /// <summary>
    /// Issue #71: the single line the removal funnel emits — the one that decides the whole
    /// investigation. It must carry the reason, both ids, the attempt id that joins it to PR #69's
    /// created/replaced lines, and the timestamps.
    /// </summary>
    [Fact]
    public void Delete_EmitsRemovalLineWithReasonAndTelemetry()
    {
        var fixture = new Fixture();
        var session = fixture.CreateClientSession(PlaySessionA, AttemptId);
        fixture.Logger.Entries.Clear();

        Assert.True(fixture.Manager.Delete(session.Id));

        var entry = Assert.Single(fixture.Logger.Entries, e => e.Message.Contains("removed", StringComparison.Ordinal));
        // #203: projected at the logging boundary - the state dictionary carries the rendered
        // string; both shipped formatters write the same bytes (PlaybackSessionIdProjectionTests).
        Assert.Equal(session.Id.Value.ToString("N"), Assert.IsType<string>(entry.Properties["SessionId"]));
        Assert.Equal(PlaySessionA, entry.Properties["PlaySessionId"]);
        Assert.Equal(AttemptId, entry.Properties["PlaybackAttemptId"]);
        Assert.Equal(PlaybackSessionRemovalReason.HttpDelete, entry.Properties["RemovalReason"]);
        Assert.Equal(T0, entry.Properties["CreatedAt"]);
        Assert.Equal(T0, entry.Properties["UpdatedAt"]);
        Assert.Equal(0d, entry.Properties["AgeSeconds"]);
    }

    /// <summary>
    /// Issue #71: the TTL reap must be attributable as such — an incident timeline that cannot tell
    /// a 6-hour expiry from an ffmpeg-driven reap proves nothing.
    /// </summary>
    [Fact]
    public void SweepExpired_EmitsExpiryLineAndTtlSweepRemovalReason()
    {
        var fixture = new Fixture();
        var session = fixture.CreateClientSession(PlaySessionA);
        fixture.Logger.Entries.Clear();

        fixture.Manager.SweepExpired(T0 + TimeSpan.FromHours(6) + TimeSpan.FromSeconds(1));

        var expiry = Assert.Single(fixture.Logger.Entries, e => e.Message.Contains("expired", StringComparison.Ordinal));
        Assert.Equal(T0, expiry.Properties["UpdatedAt"]);
        Assert.Equal(T0.AddHours(6), expiry.Properties["ExpiresAt"]);

        var removal = Assert.Single(fixture.Logger.Entries, e => e.Message.Contains("removed", StringComparison.Ordinal));
        Assert.Equal(PlaybackSessionRemovalReason.TtlSweep, removal.Properties["RemovalReason"]);
        // #203: projected at the logging boundary - the state dictionary carries the rendered
        // string; both shipped formatters write the same bytes (PlaybackSessionIdProjectionTests).
        Assert.Equal(session.Id.Value.ToString("N"), Assert.IsType<string>(removal.Properties["SessionId"]));
    }

    /// <summary>
    /// Issue #71: the transcoding-job line is emitted whether or not the reap that used to follow it
    /// happens, and reports whether the job's play session id addresses a tracked session at all —
    /// that <c>Correlated</c> bit is what proves, in production logs, that ffmpeg's exit landed on a
    /// session the client was still using.
    /// </summary>
    [Fact]
    public void TranscodingJobEnded_ForLiveClientOwnedSession_LogsTheCorrelatedJob()
    {
        var fixture = new Fixture();
        var session = fixture.CreateClientSession(PlaySessionA);
        fixture.Logger.Entries.Clear();

        fixture.RaiseTranscodingJobEnded(PlaySessionA, jobId: "job-1");

        var entry = Assert.Single(fixture.Logger.Entries, e => e.Message.Contains("Transcoding job ended", StringComparison.Ordinal));
        Assert.Equal(PlaySessionA, entry.Properties["PlaySessionId"]);
        Assert.Equal("job-1", entry.Properties["JobId"]);
        Assert.Equal(true, entry.Properties["Correlated"]);
        Assert.Equal(session.Id, Assert.IsType<PlaybackSessionId>(entry.Properties["SessionId"]));
    }

    /// <summary>
    /// The other side of the same discrimination: a re-<c>Create</c> on a live play session id does
    /// carry a request, so the field must report false.
    /// </summary>
    [Fact]
    public void Create_TwiceOnSamePlaySessionId_LogsIncomingRequestWasNullFalse()
    {
        var fixture = new Fixture();
        fixture.CreateClientSession(PlaySessionA);
        fixture.Logger.Entries.Clear();

        fixture.CreateClientSession(PlaySessionA);

        var entry = Assert.Single(fixture.Logger.Entries, e => e.Message.Contains("replaced in place", StringComparison.Ordinal));
        Assert.Equal(false, entry.Properties["IncomingRequestWasNull"]);
        Assert.Equal(false, entry.Properties["PlanPreserved"]);
    }

    private sealed class Fixture
    {
        public Fixture()
        {
            var options = new MediaOptions();
            Request = new PlaybackSessionRequest(PlaybackMediaKind.Video, options);
            var planner = new Mock<IPlaybackSessionPlanner>();
            planner.Setup(p => p.PlanVideo(options)).Returns(new PlaybackPlan(PlayMethod.DirectPlay, default));
            TranscodeManager = new Mock<ITranscodeManager>();
            SessionManager = new Mock<ISessionManager>();
            Manager = new PlaybackSessionManager(
                planner.Object,
                TranscodeManager.Object,
                SessionManager.Object,
                v2PlanStore: null,
                logger: Logger,
                timeProvider: Clock);
        }

        public MutableTimeProvider Clock { get; } = new() { Now = T0 };

        public RecordingLogger Logger { get; } = new();

        public Mock<ITranscodeManager> TranscodeManager { get; }

        public Mock<ISessionManager> SessionManager { get; }

        public PlaybackSessionManager Manager { get; }

        public PlaybackSessionRequest Request { get; }

        public PlaybackSession CreateClientSession(string playSessionId, string? attemptId = null)
        {
            var session = Manager.Create(Request, playSessionId, attemptId);
            Assert.NotNull(session);
            return session;
        }

        public void RaiseTranscodingJobEnded(string playSessionId, string? jobId = null)
        {
            var job = new TranscodingJob(NullLogger<TranscodingJob>.Instance) { PlaySessionId = playSessionId, Id = jobId };
            TranscodeManager.Raise(m => m.TranscodingJobEnded += null, TranscodeManager.Object, job);
        }

        public void RaiseTranscodingJobStarted(string playSessionId)
        {
            var job = new TranscodingJob(NullLogger<TranscodingJob>.Instance) { PlaySessionId = playSessionId };
            TranscodeManager.Raise(m => m.TranscodingJobStarted += null, TranscodeManager.Object, job);
        }

        public void RaisePlaybackStopped(string playSessionId) =>
            SessionManager.Raise(m => m.PlaybackStopped += null, SessionManager.Object, new PlaybackStopEventArgs { PlaySessionId = playSessionId });
    }

    private sealed class MutableTimeProvider : TimeProvider
    {
        public DateTimeOffset Now { get; set; }

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class RecordingLogger : ILogger<PlaybackSessionManager>
    {
        public List<RecordedEntry> Entries { get; } = new();

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var properties = state as IEnumerable<KeyValuePair<string, object?>> ?? Array.Empty<KeyValuePair<string, object?>>();
            Entries.Add(new RecordedEntry(formatter(state, exception), properties.ToDictionary(p => p.Key, p => p.Value)));
        }

        public sealed record RecordedEntry(string Message, Dictionary<string, object?> Properties);

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
