using System;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Serilog.Events;
using Tesserafin.Api.Constants;
using Tesserafin.Api.Controllers;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Controller.Session;
using Tesserafin.Model.Session;
using Xunit;

namespace Tesserafin.Server.Tests.LogForging
{
    /// <summary>
    /// <see cref="PlaystateController"/> writes the client-supplied <c>PlaySessionId</c> into a log
    /// record at two statements, before anything looks the value up: once from the
    /// <c>POST Sessions/Playing/Stopped</c> body, once from the <c>DELETE PlayingItems/{itemId}</c>
    /// query string. Nothing parses, encodes or validates the value first — the play session id is
    /// an opaque client-chosen string everywhere in this server — so the caller chooses the bytes
    /// the formatter writes.
    /// </summary>
    /// <remarks>
    /// The controller carries <see cref="AuthorizeAttribute"/>: the caller must hold a session. That
    /// is stated rather than glossed. An ordinary authenticated user is still an untrusted source
    /// for a log value, which is what puts these two statements in this tranche — the shape of the
    /// value, not the strength of the gate.
    /// </remarks>
    public sealed class PlaystateControllerLogTests
    {
        private const string Ordinary = "play-session-42";

        [Fact]
        public void Controller_StillRequiresAuthorization()
        {
            // The tranche changes logging arguments; it must not have relaxed the gate.
            Assert.NotEmpty(typeof(PlaystateController)
                .GetCustomAttributes<AuthorizeAttribute>(inherit: true)
                .ToArray());
        }

        [Theory]
        [InlineData("play\rsession")]
        [InlineData("play\nsession")]
        [InlineData("play\r\nsession")]
        [InlineData("play-session-42\r\n" + RealFormatterLogProbe.ForgedRecordPrefix)]
        public async Task ReportPlaybackStopped_HostileBodyPlaySessionId_WritesExactlyOnePhysicalRecord(string playSessionId)
        {
            using var probe = RealFormatterLogProbe.Text(LogEventLevel.Verbose);

            await ReportStoppedFromBody(probe, playSessionId).ConfigureAwait(true);

            Assert.Single(probe.Lines());
            Assert.Equal(1, probe.TextRecordCount());
            Assert.DoesNotContain('\r', probe.Raw);
        }

        [Theory]
        [InlineData("play\rsession")]
        [InlineData("play\nsession")]
        [InlineData("play\r\nsession")]
        [InlineData("play-session-42\r\n" + RealFormatterLogProbe.ForgedRecordPrefix)]
        public async Task OnPlaybackStopped_HostileQueryPlaySessionId_WritesExactlyOnePhysicalRecord(string playSessionId)
        {
            using var probe = RealFormatterLogProbe.Text(LogEventLevel.Verbose);

            await ReportStoppedFromQuery(probe, playSessionId).ConfigureAwait(true);

            Assert.Single(probe.Lines());
            Assert.Equal(1, probe.TextRecordCount());
            Assert.DoesNotContain('\r', probe.Raw);
        }

        [Fact]
        public async Task ReportPlaybackStopped_ForgedRecordPrefix_StaysInsideTheRealRecord()
        {
            using var probe = RealFormatterLogProbe.Text(LogEventLevel.Verbose);

            await ReportStoppedFromBody(probe, Ordinary + "\r\n" + RealFormatterLogProbe.ForgedRecordPrefix)
                .ConfigureAwait(true);

            var record = Assert.Single(probe.Lines());

            // One prefix, and it is the server's own: the forged "[12:00:00.000] [ERR]" is now text
            // inside the message rather than the head of a record of its own.
            Assert.Contains("[DBG] [1] Tesserafin.Api.Controllers.PlaystateController:", record, StringComparison.Ordinal);
            Assert.Contains(
                "ReportPlaybackStopped PlaySessionId: play-session-42\\r\\n[12:00:00.000] [ERR]",
                record,
                StringComparison.Ordinal);
            Assert.Contains("administrator account deleted by mallory", record, StringComparison.Ordinal);
        }

        [Fact]
        public async Task OnPlaybackStopped_ForgedRecordPrefix_StaysInsideTheRealRecord()
        {
            using var probe = RealFormatterLogProbe.Text(LogEventLevel.Verbose);

            await ReportStoppedFromQuery(probe, Ordinary + "\r\n" + RealFormatterLogProbe.ForgedRecordPrefix)
                .ConfigureAwait(true);

            var record = Assert.Single(probe.Lines());

            Assert.Contains("[DBG] [1] Tesserafin.Api.Controllers.PlaystateController:", record, StringComparison.Ordinal);
            Assert.Contains(
                "ReportPlaybackStopped PlaySessionId: play-session-42\\r\\n[12:00:00.000] [ERR]",
                record,
                StringComparison.Ordinal);
        }

        [Fact]
        public async Task ReportPlaybackStopped_OrdinaryPlaySessionId_LogsTheSameLineItLoggedBefore()
        {
            using var probe = RealFormatterLogProbe.Text(LogEventLevel.Verbose);

            await ReportStoppedFromBody(probe, Ordinary).ConfigureAwait(true);

            var record = Assert.Single(probe.Lines());
            Assert.EndsWith("ReportPlaybackStopped PlaySessionId: play-session-42", record.TrimEnd('\r'), StringComparison.Ordinal);
        }

        [Fact]
        public async Task OnPlaybackStopped_OrdinaryPlaySessionId_LogsTheSameLineItLoggedBefore()
        {
            using var probe = RealFormatterLogProbe.Text(LogEventLevel.Verbose);

            await ReportStoppedFromQuery(probe, Ordinary).ConfigureAwait(true);

            var record = Assert.Single(probe.Lines());
            Assert.EndsWith("ReportPlaybackStopped PlaySessionId: play-session-42", record.TrimEnd('\r'), StringComparison.Ordinal);
        }

        [Fact]
        public async Task ReportPlaybackStopped_MissingPlaySessionId_StillLogsTheEmptyValueItLoggedBefore()
        {
            using var probe = RealFormatterLogProbe.Text(LogEventLevel.Verbose);

            await ReportStoppedFromBody(probe, playSessionId: null).ConfigureAwait(true);

            var record = Assert.Single(probe.Lines());
            Assert.EndsWith("ReportPlaybackStopped PlaySessionId: ", record.TrimEnd('\r'), StringComparison.Ordinal);
        }

        [Fact]
        public async Task ReportPlaybackStopped_HostilePlaySessionId_IsStillPassedToKillTranscodingJobsVerbatim()
        {
            using var probe = RealFormatterLogProbe.Text(LogEventLevel.Verbose);
            var hostile = Ordinary + "\r\n" + RealFormatterLogProbe.ForgedRecordPrefix;
            var fixture = new Fixture(probe);

            await fixture.ReportStoppedFromBody(hostile).ConfigureAwait(true);

            // The boundary belongs to the logging pipeline only: business code keeps seeing the
            // caller's bytes exactly as they arrived.
            Assert.Equal(hostile, fixture.KilledPlaySessionId);
        }

        private static async Task ReportStoppedFromBody(RealFormatterLogProbe probe, string? playSessionId)
            => await new Fixture(probe).ReportStoppedFromBody(playSessionId).ConfigureAwait(true);

        private static async Task ReportStoppedFromQuery(RealFormatterLogProbe probe, string? playSessionId)
            => await new Fixture(probe).ReportStoppedFromQuery(playSessionId).ConfigureAwait(true);

        private sealed class Fixture
        {
            private readonly PlaystateController _controller;

            public Fixture(RealFormatterLogProbe probe)
            {
                var sessionManager = new Mock<ISessionManager>();
                sessionManager
                    .Setup(x => x.LogSessionActivity(
                        It.IsAny<string?>(),
                        It.IsAny<string?>(),
                        It.IsAny<string?>(),
                        It.IsAny<string?>(),
                        It.IsAny<string?>(),
                        It.IsAny<Tesserafin.Data.Entities.User?>()))
                    .ReturnsAsync(() => new SessionInfo(sessionManager.Object, NullLogger.Instance) { Id = "session-1" });

                var transcodeManager = new Mock<ITranscodeManager>();
                transcodeManager
                    .As<ITranscodeOwnedStop>()
                    .Setup(x => x.StopTranscodingJobs(It.IsAny<string>(), It.IsAny<Func<TranscodingJob, bool>>()))
                    .Callback<string, Func<TranscodingJob, bool>>((playSessionId, _) => KilledPlaySessionId = playSessionId)
                    .Returns(Task.CompletedTask);

                _controller = new PlaystateController(
                    Mock.Of<IUserManager>(),
                    Mock.Of<IUserDataManager>(),
                    Mock.Of<IItemAccessService>(),
                    Mock.Of<IItemPlayedStateService>(),
                    sessionManager.Object,
                    probe.Factory,
                    transcodeManager.Object,
                    Mock.Of<Tesserafin.Api.Auth.HlsJobOwnership.IHlsJobOwnershipAuthorizer>())
                {
                    ControllerContext = new ControllerContext
                    {
                        HttpContext = new DefaultHttpContext
                        {
                            User = new ClaimsPrincipal(new ClaimsIdentity(
                                new[]
                                {
                                    new Claim(InternalClaimTypes.UserId, Guid.NewGuid().ToString("N")),
                                    new Claim(InternalClaimTypes.DeviceId, "device-1")
                                },
                                authenticationType: "Test"))
                        }
                    }
                };
            }

            public string? KilledPlaySessionId { get; private set; }

            public async Task ReportStoppedFromBody(string? playSessionId)
            {
                var result = await _controller
                    .ReportPlaybackStopped(new PlaybackStopInfo { PlaySessionId = playSessionId })
                    .ConfigureAwait(true);

                Assert.IsType<NoContentResult>(result);
            }

#pragma warning disable CS0618 // the endpoint is obsolete but still routed, and still logs
            public async Task ReportStoppedFromQuery(string? playSessionId)
            {
                var result = await _controller
                    .OnPlaybackStopped(Guid.NewGuid(), null, null, null, null, playSessionId)
                    .ConfigureAwait(true);

                Assert.IsType<NoContentResult>(result);
            }
#pragma warning restore CS0618
        }
    }
}
