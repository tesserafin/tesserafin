using System;
using Microsoft.Extensions.Logging;
using Moq;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Controller.Session;
using Tesserafin.MediaEncoding.Playback;
using Tesserafin.Model.Dlna;
using Tesserafin.Model.Session;
using Xunit;

namespace Tesserafin.Server.Tests.LogForging
{
    /// <summary>
    /// <see cref="PlaybackSessionManager"/> writes two client-chosen opaque strings into log
    /// records: the legacy <c>PlaySessionId</c> a client picks for a playback attempt, and the
    /// <c>PlaybackAttemptId</c> it supplies on <c>POST/PUT Playback/Sessions</c>. Both reach the
    /// manager from an authenticated HTTP request, and the manager stores them verbatim — nothing
    /// in this class parses or encodes either value.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two statements are covered: the in-place replacement line (<c>StoreOrReplace</c>), reached
    /// when a second planning call arrives for a play session id that already has a session, and
    /// the single removal funnel (<c>RemoveNoLock</c>), reached by every teardown path.
    /// </para>
    /// <para>
    /// <c>PlaybackAttemptId</c> is separately rejected for control characters by
    /// <c>PlaybackAttemptIdValidator</c> on the HTTP edge, so the hostile shapes below are not a
    /// live route today. The boundary is applied anyway: the manager is also called from paths that
    /// do not go through that validator, and the logging statement must not depend on which caller
    /// reached it. That is defence in depth, and it is recorded as such rather than claimed as a
    /// closed CodeQL flow.
    /// </para>
    /// </remarks>
    public sealed class PlaybackSessionManagerLogTests
    {
        private const string OrdinaryPlaySession = "play-session-42";
        private const string OrdinaryAttempt = "attempt-7";

        [Theory]
        [InlineData("play\rsession")]
        [InlineData("play\nsession")]
        [InlineData("play\r\nsession")]
        [InlineData("play-session-42\r\n" + RealFormatterLogProbe.ForgedRecordPrefix)]
        public void ReplacedInPlace_HostilePlaySessionId_WritesExactlyOnePhysicalRecord(string playSessionId)
        {
            using var probe = RealFormatterLogProbe.Text();
            var fixture = new Fixture(probe);

            fixture.CreateTwice(playSessionId, OrdinaryAttempt);

            Assert.Equal(1, probe.TextRecordCount());
            Assert.Single(probe.Lines());
            Assert.DoesNotContain('\r', probe.Raw);
        }

        [Theory]
        [InlineData("attempt\r7")]
        [InlineData("attempt\n7")]
        [InlineData("attempt\r\n7")]
        [InlineData("attempt-7\r\n" + RealFormatterLogProbe.ForgedRecordPrefix)]
        public void ReplacedInPlace_HostileAttemptId_WritesExactlyOnePhysicalRecord(string attemptId)
        {
            using var probe = RealFormatterLogProbe.Text();
            var fixture = new Fixture(probe);

            fixture.CreateTwice(OrdinaryPlaySession, attemptId);

            Assert.Equal(1, probe.TextRecordCount());
            Assert.Single(probe.Lines());
            Assert.DoesNotContain('\r', probe.Raw);
        }

        [Fact]
        public void ReplacedInPlace_ForgedRecordPrefix_StaysInsideTheRealRecord()
        {
            using var probe = RealFormatterLogProbe.Text();
            var fixture = new Fixture(probe);

            fixture.CreateTwice(
                OrdinaryPlaySession + "\r\n" + RealFormatterLogProbe.ForgedRecordPrefix,
                OrdinaryAttempt);

            var record = Assert.Single(probe.Lines());
            Assert.Contains("[INF] [1] Tesserafin.MediaEncoding.Playback.PlaybackSessionManager:", record, StringComparison.Ordinal);
            Assert.Contains("play session play-session-42\\r\\n[12:00:00.000] [ERR]", record, StringComparison.Ordinal);
            Assert.Contains("administrator account deleted by mallory", record, StringComparison.Ordinal);
        }

        [Fact]
        public void ReplacedInPlace_OrdinaryValues_LogTheSameLineTheyLoggedBefore()
        {
            using var probe = RealFormatterLogProbe.Text();
            var fixture = new Fixture(probe);

            fixture.CreateTwice(OrdinaryPlaySession, OrdinaryAttempt);

            var record = Assert.Single(probe.Lines());
            Assert.Contains("replaced in place (play session play-session-42, ", record, StringComparison.Ordinal);
            Assert.Contains("attempt attempt-7)", record, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("play\rsession")]
        [InlineData("play\nsession")]
        [InlineData("play\r\nsession")]
        [InlineData("play-session-42\r\n" + RealFormatterLogProbe.ForgedRecordPrefix)]
        public void Removed_HostilePlaySessionId_WritesExactlyOnePhysicalRecord(string playSessionId)
        {
            using var probe = RealFormatterLogProbe.Text();
            var fixture = new Fixture(probe);
            var session = fixture.Create(playSessionId, OrdinaryAttempt);

            Assert.True(fixture.Manager.Delete(session.Id));

            Assert.Equal(1, probe.TextRecordCount());
            Assert.Single(probe.Lines());
            Assert.DoesNotContain('\r', probe.Raw);
        }

        [Theory]
        [InlineData("attempt\r7")]
        [InlineData("attempt\n7")]
        [InlineData("attempt\r\n7")]
        [InlineData("attempt-7\r\n" + RealFormatterLogProbe.ForgedRecordPrefix)]
        public void Removed_HostileAttemptId_WritesExactlyOnePhysicalRecord(string attemptId)
        {
            using var probe = RealFormatterLogProbe.Text();
            var fixture = new Fixture(probe);
            var session = fixture.Create(OrdinaryPlaySession, attemptId);

            Assert.True(fixture.Manager.Delete(session.Id));

            Assert.Equal(1, probe.TextRecordCount());
            Assert.Single(probe.Lines());
            Assert.DoesNotContain('\r', probe.Raw);
        }

        [Fact]
        public void Removed_ForgedRecordPrefix_StaysInsideTheRealRecord()
        {
            using var probe = RealFormatterLogProbe.Text();
            var fixture = new Fixture(probe);
            var session = fixture.Create(
                OrdinaryPlaySession,
                OrdinaryAttempt + "\r\n" + RealFormatterLogProbe.ForgedRecordPrefix);

            Assert.True(fixture.Manager.Delete(session.Id));

            var record = Assert.Single(probe.Lines());
            Assert.Contains("attempt attempt-7\\r\\n[12:00:00.000] [ERR]", record, StringComparison.Ordinal);
            Assert.Contains("administrator account deleted by mallory", record, StringComparison.Ordinal);
        }

        [Fact]
        public void Removed_OrdinaryValues_LogTheSameLineTheyLoggedBefore()
        {
            using var probe = RealFormatterLogProbe.Text();
            var fixture = new Fixture(probe);
            var session = fixture.Create(OrdinaryPlaySession, OrdinaryAttempt);

            Assert.True(fixture.Manager.Delete(session.Id));

            var record = Assert.Single(probe.Lines());
            Assert.Contains("removed (play session play-session-42, attempt attempt-7, reason ", record, StringComparison.Ordinal);
        }

        [Fact]
        public void HostileValues_AreStillStoredOnTheSessionVerbatim()
        {
            using var probe = RealFormatterLogProbe.Text();
            var fixture = new Fixture(probe);
            var hostilePlaySession = OrdinaryPlaySession + "\r\n" + RealFormatterLogProbe.ForgedRecordPrefix;
            var hostileAttempt = OrdinaryAttempt + "\r\n" + RealFormatterLogProbe.ForgedRecordPrefix;

            var session = fixture.Create(hostilePlaySession, hostileAttempt);

            // The boundary is the logging pipeline only: stored state keeps the caller's bytes.
            Assert.Equal(hostilePlaySession, session.PlaySessionId);
            Assert.Equal(hostileAttempt, session.PlaybackAttemptId);
            Assert.NotNull(fixture.Manager.Get(session.Id));
        }

        private sealed class Fixture
        {
            private readonly PlaybackSessionRequest _request;

            public Fixture(RealFormatterLogProbe probe)
            {
                var options = new MediaOptions();
                _request = new PlaybackSessionRequest(PlaybackMediaKind.Video, options);
                var planner = new Mock<IPlaybackSessionPlanner>();
                planner.Setup(p => p.PlanVideo(options)).Returns(new PlaybackPlan(PlayMethod.DirectPlay, default));
                Manager = new PlaybackSessionManager(
                    planner.Object,
                    Mock.Of<ITranscodeManager>(),
                    Mock.Of<ISessionManager>(),
                    logger: probe.LoggerFor<PlaybackSessionManager>());
            }

            public PlaybackSessionManager Manager { get; }

            public PlaybackSession Create(string playSessionId, string? attemptId)
            {
                var session = Manager.Create(_request, playSessionId, attemptId);
                Assert.NotNull(session);
                return session;
            }

            public void CreateTwice(string playSessionId, string? attemptId)
            {
                // Only the SECOND planning call for the same play session id reaches the in-place
                // replacement statement; the first one takes the silent fresh-session branch, so
                // the probe still holds exactly one record afterwards.
                Create(playSessionId, attemptId);
                Create(playSessionId, attemptId);
            }
        }
    }
}
