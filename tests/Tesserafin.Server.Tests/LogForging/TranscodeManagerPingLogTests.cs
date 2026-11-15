using System;
using System.IO;
using Microsoft.Extensions.Configuration;
using Moq;
using Serilog.Events;
using Tesserafin.Common.Configuration;
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

namespace Tesserafin.Server.Tests.LogForging
{
    /// <summary>
    /// <see cref="TranscodeManager.PingTranscodingJob"/> logs the client-supplied
    /// <c>playSessionId</c> before it is used to look anything up. The value reaches it from
    /// <c>POST Sessions/Playing/Ping</c> — an authenticated endpoint whose query string the caller
    /// controls byte for byte, since a play session id is an opaque client-chosen string.
    /// </summary>
    /// <remarks>
    /// The statement carries a second argument, <c>RequestId</c>, which is server-generated
    /// (<c>IRequestCorrelationAccessor</c>) and therefore not part of this tranche. It is asserted
    /// here only to show that the surrounding line is unchanged.
    /// </remarks>
    public sealed class TranscodeManagerPingLogTests
    {
        private const string Ordinary = "play-session-42";

        [Theory]
        [InlineData("play\rsession")]
        [InlineData("play\nsession")]
        [InlineData("play\r\nsession")]
        [InlineData("play-session-42\r\n" + RealFormatterLogProbe.ForgedRecordPrefix)]
        public void PingTranscodingJob_HostilePlaySessionId_WritesExactlyOnePhysicalRecord(string playSessionId)
        {
            using var probe = RealFormatterLogProbe.Text(LogEventLevel.Verbose);

            CreateManager(probe).PingTranscodingJob(playSessionId, isUserPaused: null);

            Assert.Equal(1, probe.TextRecordCount());
            Assert.Single(probe.Lines());
            Assert.DoesNotContain('\r', probe.Raw);
        }

        [Fact]
        public void PingTranscodingJob_ForgedRecordPrefix_StaysInsideTheRealRecord()
        {
            using var probe = RealFormatterLogProbe.Text(LogEventLevel.Verbose);

            CreateManager(probe).PingTranscodingJob(
                Ordinary + "\r\n" + RealFormatterLogProbe.ForgedRecordPrefix,
                isUserPaused: null);

            var record = Assert.Single(probe.Lines());
            Assert.Contains("[DBG] [1] Tesserafin.MediaEncoding.Transcoding.TranscodeManager:", record, StringComparison.Ordinal);
            Assert.Contains("PlaySessionId=play-session-42\\r\\n[12:00:00.000] [ERR]", record, StringComparison.Ordinal);
            Assert.Contains("administrator account deleted by mallory", record, StringComparison.Ordinal);
        }

        [Fact]
        public void PingTranscodingJob_OrdinaryPlaySessionId_LogsTheSameLineItLoggedBefore()
        {
            using var probe = RealFormatterLogProbe.Text(LogEventLevel.Verbose);

            CreateManager(probe).PingTranscodingJob(Ordinary, isUserPaused: true);

            var record = Assert.Single(probe.Lines());
            Assert.EndsWith(
                "PingTranscodingJob RequestId=null PlaySessionId=play-session-42 isUsedPaused: true",
                record.TrimEnd('\r'),
                StringComparison.Ordinal);
        }

        private static TranscodeManager CreateManager(RealFormatterLogProbe probe)
        {
            var appPaths = Mock.Of<IServerApplicationPaths>();
            var configurationManager = new Mock<IServerConfigurationManager>();
            configurationManager
                .Setup(x => x.GetConfiguration("encoding"))
                .Returns(new EncodingOptions
                {
                    // A path that deliberately does not exist: the constructor's cache purge sees no
                    // directory and returns immediately, so nothing on disk is touched by this test.
                    TranscodingTempPath = Path.Combine(Path.GetTempPath(), "tesserafin-log-forging-" + Guid.NewGuid().ToString("N"))
                });
            configurationManager.Setup(x => x.CommonApplicationPaths).Returns(appPaths);

            return new TranscodeManager(
                probe.Factory,
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
                new Tesserafin.MediaEncoding.Encoder.FfmpegProcessRunner(Microsoft.Extensions.Logging.Abstractions.NullLogger<Tesserafin.MediaEncoding.Encoder.FfmpegProcessRunner>.Instance));
        }
    }
}
