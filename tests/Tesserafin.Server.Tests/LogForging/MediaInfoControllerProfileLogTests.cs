#pragma warning disable CS0618 // The endpoint is deprecated by tesserafin#129 step 7 and this test goes with it.
using System;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Serilog.Events;
using Tesserafin.Api.Controllers;
using Tesserafin.Api.Models.MediaInfoDtos;
using Tesserafin.Controller.Devices;
using Tesserafin.Controller.Library;
using Tesserafin.Model.Dlna;
using Xunit;

namespace Tesserafin.Server.Tests.LogForging
{
    /// <summary>
    /// <see cref="MediaInfoController.GetPostedPlaybackInfo"/> logs the caller-supplied
    /// <see cref="DeviceProfile"/> at Debug (CodeQL <c>cs/log-forging</c> #6). It is now logged as
    /// a JSON string instead of a destructured object.
    /// </summary>
    /// <remarks>
    /// Measured, not assumed: the shipped <c>{Message:lj}</c> template already JSON-escapes a
    /// destructured value, so the old <c>{@Profile}</c> did not split a record on this probe either.
    /// What the change buys is a single line under an operator template without <c>j</c>, and the
    /// barrier CodeQL needs; these tests lock the one-record behaviour, and closure of #6 is the
    /// default-branch CodeQL analysis after landing. The probe runs at Debug: at the shipped
    /// Information level the statement writes nothing at all.
    /// </remarks>
    public sealed class MediaInfoControllerProfileLogTests
    {
        private const string Hostile = "x\r\n" + RealFormatterLogProbe.ForgedRecordPrefix;

        [Fact]
        public async Task HostileNestedProfile_WritesExactlyOneTextRecord()
        {
            using var probe = RealFormatterLogProbe.Text(LogEventLevel.Debug);
            var profile = HostileProfile();

            await Post(probe, new PlaybackInfoDto { DeviceProfile = profile });

            Assert.Equal(1, probe.TextRecordCount());
            Assert.Single(probe.Lines());
            Assert.DoesNotContain('\r', probe.Raw);
            Assert.Contains("MediaInfoController", probe.Raw, StringComparison.Ordinal);
            Assert.Contains(RealFormatterLogProbe.ForgedRecordPrefix, probe.Raw, StringComparison.Ordinal);

            // The log got a rendering, not the caller's object: playback still sees the raw value.
            Assert.Equal(Hostile, profile.CodecProfiles[0].Conditions[0].Value);
        }

        [Fact]
        public async Task HostileNestedProfile_WritesExactlyOneJsonRecord()
        {
            using var probe = RealFormatterLogProbe.Json(LogEventLevel.Debug);

            await Post(probe, new PlaybackInfoDto { DeviceProfile = HostileProfile() });

            Assert.Single(probe.Lines());
        }

        [Fact]
        public async Task NoProfile_StillLogsOneRecord()
        {
            using var probe = RealFormatterLogProbe.Text(LogEventLevel.Debug);

            await Post(probe, null);
            await Post(probe, new PlaybackInfoDto());

            Assert.Equal(2, probe.TextRecordCount());
            Assert.Equal(2, probe.Lines().Length);
        }

        private static DeviceProfile HostileProfile() => new()
        {
            Name = Hostile,
            DirectPlayProfiles = [new DirectPlayProfile { Container = Hostile, AudioCodec = Hostile, VideoCodec = Hostile }],
            TranscodingProfiles = [new TranscodingProfile { Container = Hostile, Conditions = [new ProfileCondition { Value = Hostile }] }],
            ContainerProfiles = [new ContainerProfile { Container = Hostile, Conditions = [new ProfileCondition { Value = Hostile }] }],
            CodecProfiles = [new CodecProfile { Codec = Hostile, Conditions = [new ProfileCondition { Value = Hostile }] }],
            SubtitleProfiles = [new SubtitleProfile { Format = Hostile, Language = Hostile }],
        };

        private static Task<ActionResult<Tesserafin.Model.MediaInfo.PlaybackInfoResponse>> Post(
            RealFormatterLogProbe probe,
            PlaybackInfoDto? dto)
        {
            // No user and an item lookup that finds nothing: the action returns 404 before
            // MediaInfoHelper is ever reached.
            var controller = new MediaInfoController(
                Mock.Of<IMediaSourceManager>(),
                Mock.Of<IDeviceManager>(),
                Mock.Of<IItemLookupService>(),
                Mock.Of<IItemAccessService>(),
                probe.LoggerFor<MediaInfoController>(),
                null!,
                Mock.Of<IUserManager>())
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity()) }
                }
            };

            return controller.GetPostedPlaybackInfo(
                itemId: Guid.NewGuid(),
                userId: null,
                maxStreamingBitrate: null,
                startTimeTicks: null,
                audioStreamIndex: null,
                subtitleStreamIndex: null,
                maxAudioChannels: null,
                mediaSourceId: null,
                liveStreamId: null,
                autoOpenLiveStream: null,
                enableDirectPlay: null,
                enableDirectStream: null,
                enableTranscoding: null,
                allowVideoStreamCopy: null,
                allowAudioStreamCopy: null,
                playbackInfoDto: dto);
        }
    }
}
