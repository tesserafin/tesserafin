#pragma warning disable CS0618 // The endpoint is deprecated by tesserafin#129 step 7 and this test goes with it.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Tesserafin.Api.Controllers;
using Tesserafin.Api.Models.MediaInfoDtos;
using Tesserafin.Api.Models.PlaybackSessionDtos;
using Tesserafin.Controller.Devices;
using Tesserafin.Controller.Library;
using Xunit;

namespace Tesserafin.Server.Tests.LogForging
{
    /// <summary>
    /// <see cref="MediaInfoController.GetPostedPlaybackInfo"/> opens a logging scope carrying the
    /// caller's <c>PlaybackAttemptId</c> (CodeQL <c>cs/log-forging</c> #33).
    /// </summary>
    /// <remarks>
    /// Every id the validator accepts is free of CR/LF, so it is a fixed point of
    /// <c>ToSingleLogLine</c>: these tests lock the observable behaviour (same reference in the
    /// scope, hostile ids rejected before any scope opens), not the barrier itself. Closure of #33
    /// is the default-branch CodeQL analysis after landing.
    /// </remarks>
    public sealed class MediaInfoControllerAttemptScopeLogTests
    {
        [Fact]
        public void Controller_StillRequiresAuthorization()
        {
            Assert.NotEmpty(typeof(MediaInfoController)
                .GetCustomAttributes<AuthorizeAttribute>(inherit: true)
                .ToArray());
        }

        [Fact]
        public async Task LegalAttemptId_OpensOneScope_WithTheSameReference()
        {
            var logger = new Mock<ILogger<MediaInfoController>>();
            var dto = new PlaybackInfoDto { PlaybackAttemptId = "attempt-7" };

            var result = await Post(logger, dto);

            Assert.IsType<NotFoundResult>(result.Result);
            var state = Assert.Single(ScopeStates(logger));
            var entry = Assert.Single(Assert.IsAssignableFrom<IDictionary<string, object>>(state));
            Assert.Equal(PlaybackAttemptIdValidator.LogPropertyName, entry.Key);
            Assert.Same(dto.PlaybackAttemptId, entry.Value);
        }

        [Fact]
        public async Task NoAttemptId_OpensNoScope()
        {
            var logger = new Mock<ILogger<MediaInfoController>>();

            await Post(logger, new PlaybackInfoDto());
            await Post(logger, null);

            Assert.Empty(ScopeStates(logger));
        }

        [Theory]
        [InlineData("attempt\r\n7")]
        [InlineData("attempt-7\r\n" + RealFormatterLogProbe.ForgedRecordPrefix)]
        public async Task HostileAttemptId_IsRejectedBeforeAnyScope(string attemptId)
        {
            var logger = new Mock<ILogger<MediaInfoController>>();

            await Assert.ThrowsAnyAsync<ArgumentException>(
                () => Post(logger, new PlaybackInfoDto { PlaybackAttemptId = attemptId }));

            Assert.Empty(ScopeStates(logger));
        }

        private static IEnumerable<object?> ScopeStates(Mock<ILogger<MediaInfoController>> logger)
            => logger.Invocations
                .Where(i => i.Method.Name == nameof(ILogger.BeginScope))
                .Select(i => i.Arguments[0]);

        private static Task<ActionResult<Tesserafin.Model.MediaInfo.PlaybackInfoResponse>> Post(
            Mock<ILogger<MediaInfoController>> logger,
            PlaybackInfoDto? dto)
        {
            // No user and an item lookup that finds nothing: the action returns 404 before
            // MediaInfoHelper is ever reached.
            var controller = new MediaInfoController(
                Mock.Of<IMediaSourceManager>(),
                Mock.Of<IDeviceManager>(),
                Mock.Of<IItemLookupService>(),
                Mock.Of<IItemAccessService>(),
                logger.Object,
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
