using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Tesserafin.Api.Models.PlaybackSessionDtos;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.Movies;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Data.Entities;
using Tesserafin.Extensions.Json;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.Dlna;
using Tesserafin.Model.Dto;
using Tesserafin.Model.Entities;
using Tesserafin.Model.Session;
using Tesserafin.Playback.Decision;
using Xunit;

namespace Tesserafin.Server.Integration.Tests.Controllers
{
    /// <summary>
    /// The Playback v2 session protocol over REAL HTTP - routing, authentication, model binding,
    /// status codes and JSON serialization all included, none of them stubbed. Only the two
    /// library-facing lookups are replaced (<see cref="IItemLookupService"/>,
    /// <see cref="IMediaSourceManager"/>), because seeding a genuinely playable media file into the
    /// test server's library would drag ffmpeg probing into a contract test. Everything downstream
    /// of them - <c>PlaybackSessionManager</c>, the real <c>StreamBuilder</c> planner, the live
    /// stream resolver, the descriptor mapper - is the production wiring.
    ///
    /// Covers the three statuses PR #38 decided (409 / 422 on <c>GET .../Stream</c>, 422 on
    /// <c>PUT</c>) and the two fields issue #44 §8 arbitrage A adds to the descriptor, asserted on
    /// the wire, under their exact JSON names, since that is what LANE W consumes.
    /// </summary>
    public sealed class PlaybackSessionsControllerTests : IClassFixture<PlaybackSessionsControllerTests.PlaybackFactory>
    {
        /// <summary>An item whose (stubbed) media source is genuinely playable - the planner produces a real plan for it.</summary>
        public static readonly Guid PlayableItemId = new("11111111-1111-1111-1111-111111111111");

        /// <summary>An item with NO media source at all - the planner can produce no plan for it, whatever the constraints.</summary>
        public static readonly Guid UnplannableItemId = new("22222222-2222-2222-2222-222222222222");

        private readonly PlaybackFactory _factory;
        private static string? _accessToken;

        public PlaybackSessionsControllerTests(PlaybackFactory factory)
        {
            _factory = factory;
        }

        private async Task<HttpClient> CreateAuthenticatedClientAsync()
        {
            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.AddAuthHeader(_accessToken ??= await AuthHelper.CompleteStartupAsync(client));
            return client;
        }

        private static Tesserafin.Playback.Decision.ClientCapabilities Capabilities() => new(
            Decode: new DecodeCapabilities(
                DirectPlayProfiles: [new DecodeProfile(MediaKind.Video, ["mp4"], ["h264"], ["aac"])],
                VideoCodecs: [],
                AudioCodecs: [],
                SubtitleDelivery: [],
                SupportsHls: true,
                SupportsDash: false),
            OutputProfiles: []);

        private static PlaybackConstraints Constraints() => new(
            AllowDirectPlay: true,
            AllowDirectStream: true,
            AllowTranscoding: true,
            AllowVideoStreamCopy: true,
            AllowAudioStreamCopy: true,
            MaxBitrate: null,
            MaxAudioChannels: null,
            PreferredAudioStreamIndex: null,
            PreferredSubtitleStreamIndex: null,
            SubtitleMode: SubtitlePlaybackMode.Default,
            PreferredSubtitleLanguages: [],
            AlwaysBurnInSubtitleWhenTranscoding: false,
            StartTimeTicks: 0);

        private static async Task<Guid> CreateSessionAsync(HttpClient client, Guid itemId, string? playSessionId)
        {
            var userId = (await AuthHelper.GetUserDtoAsync(client)).Id;
            using var response = await client.PostAsync(
                "Playback/Sessions",
                JsonContent.Create(
                    new CreatePlaybackSessionRequest(itemId, userId, Capabilities(), Constraints(), PlaySessionId: playSessionId),
                    options: JsonDefaults.Options),
                TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var session = await response.Content.ReadFromJsonAsync<PlaybackSessionResponse>(JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(session);
            return session.Id;
        }

        /// <summary>
        /// Issue #44 §8 arbitrage A: the descriptor must carry the effective output container and its
        /// content type, under exactly these two JSON names - <c>Container</c> and <c>MimeType</c> -
        /// because that is the whole point of the field: reefin-web could not name what the server had
        /// decided to serve, so it fell remux and non-HLS transcode back to legacy. Asserted on the
        /// raw JSON, not on a deserialized record, so a rename cannot pass silently.
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
        [Fact]
        public async Task GetStream_Planned_DescriptorCarriesContainerAndMimeType()
        {
            var client = await CreateAuthenticatedClientAsync();
            var sessionId = await CreateSessionAsync(client, PlayableItemId, "integration-play-session-1");

            using var response = await client.GetAsync($"Playback/Sessions/{sessionId}/Stream", TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            var json = JsonDocument.Parse(body);
            var root = json.RootElement;

            Assert.True(root.TryGetProperty("Container", out var container), "descriptor must expose 'Container': " + body);
            Assert.True(root.TryGetProperty("MimeType", out var mimeType), "descriptor must expose 'MimeType': " + body);

            var containerValue = container.GetString();
            Assert.False(string.IsNullOrEmpty(containerValue));

            // The container is EFFECTIVE, not advisory: the URL the same response carries addresses
            // it. On http that is /stream.{container}; on hls it is &SegmentContainer={container}.
            var url = root.GetProperty("Url").GetString();
            Assert.NotNull(url);
            var isHls = string.Equals(root.GetProperty("Protocol").GetString(), "Hls", StringComparison.OrdinalIgnoreCase);
            if (isHls)
            {
                Assert.Contains("SegmentContainer=" + containerValue, url, StringComparison.OrdinalIgnoreCase);
                Assert.Equal("application/vnd.apple.mpegurl", mimeType.GetString());
            }
            else
            {
                Assert.Contains("/stream." + containerValue, url, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(
                    Tesserafin.Model.Net.MimeTypes.GetMimeType("." + containerValue, null),
                    mimeType.GetString());
            }
        }

        /// <summary>
        /// PR #38 §2.3: a session with no <c>PlaySessionId</c> cannot correlate a served URL to the
        /// transcoding job lifecycle. 409 - and it stays 409, because the client can REPAIR it by
        /// re-requesting with a <c>PlaySessionId</c>.
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
        [Fact]
        public async Task GetStream_NoPlaySessionId_Conflict()
        {
            var client = await CreateAuthenticatedClientAsync();
            var sessionId = await CreateSessionAsync(client, PlayableItemId, playSessionId: null);

            using var response = await client.GetAsync($"Playback/Sessions/{sessionId}/Stream", TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        }

        /// <summary>
        /// PR #38 (amended): this used to be a SECOND 409 on the same operation, which OpenAPI cannot
        /// express - one <c>responses</c> entry per status code per operation - so it was invisible to
        /// every generated client. It is structurally unservable, not repairable, hence 422. Driven
        /// through <c>Track</c>, the production entry point that records a plan decided elsewhere and
        /// therefore stores no request options.
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
        [Fact]
        public async Task GetStream_NoPlannableStream_UnprocessableEntity()
        {
            var client = await CreateAuthenticatedClientAsync();
            var sessions = _factory.Services.GetRequiredService<IPlaybackSessionManager>();
            var tracked = sessions.Track(
                PlaybackMediaKind.Video,
                new PlaybackPlan(PlayMethod.DirectPlay, default),
                "integration-tracked-" + Guid.NewGuid().ToString("N"));

            using var response = await client.GetAsync($"Playback/Sessions/{tracked.Id.Value}/Stream", TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        }

        /// <summary>
        /// 404 stays what it always was - an id nobody has ever seen - so the 422 above is a genuinely
        /// new signal rather than a relabelling.
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
        [Fact]
        public async Task GetStream_UnknownSession_NotFound()
        {
            var client = await CreateAuthenticatedClientAsync();

            using var response = await client.GetAsync($"Playback/Sessions/{Guid.NewGuid()}/Stream", TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        /// <summary>
        /// PR #38: the session exists (proved by the 200 that created it), so a failure to re-plan is
        /// not "unknown id" - it is "these options are unsatisfiable". That distinction is exactly
        /// what a client needs on a track change, and a 404 destroyed it.
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
        [Fact]
        public async Task Put_ExistingSessionNoViablePlan_UnprocessableEntity()
        {
            var client = await CreateAuthenticatedClientAsync();
            var sessionId = await CreateSessionAsync(client, PlayableItemId, "integration-play-session-put");
            var userId = (await AuthHelper.GetUserDtoAsync(client)).Id;

            using var response = await client.PutAsync(
                $"Playback/Sessions/{sessionId}",
                JsonContent.Create(
                    new ReplacePlaybackSessionRequest(UnplannableItemId, userId, Capabilities(), Constraints()),
                    options: JsonDefaults.Options),
                TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        }

        /// <summary>
        /// The companion to the test above: 404 is still reserved for the unknown session id, and is
        /// still decided BEFORE any planning happens.
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
        [Fact]
        public async Task Put_UnknownSession_NotFound()
        {
            var client = await CreateAuthenticatedClientAsync();
            var userId = (await AuthHelper.GetUserDtoAsync(client)).Id;

            using var response = await client.PutAsync(
                $"Playback/Sessions/{Guid.NewGuid()}",
                JsonContent.Create(
                    new ReplacePlaybackSessionRequest(PlayableItemId, userId, Capabilities(), Constraints()),
                    options: JsonDefaults.Options),
                TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        /// <summary>
        /// The production host, with only the two library-facing lookups the planner needs replaced.
        /// </summary>
        /// <remarks>
        /// Nested deliberately: it is the class fixture of exactly one test class, and it reads that
        /// class's item-id constants. Hoisting it to a top-level type would force those constants
        /// out too, and SA1402 forbids a second type in this file — so the alternative is a separate
        /// file for a helper with a single consumer. xUnit constructs an <c>IClassFixture</c> by
        /// reflection and needs it public, so the accessibility escape hatch CA1034 also offers is
        /// closed. Suppressed the same way the rule is already suppressed elsewhere in the tree
        /// (<c>SubtitleEncoder.cs</c>, <c>SplitStringExtensions.cs</c>, the OMDb/AudioDb providers).
        /// </remarks>
#pragma warning disable CA1034 // Nested types should not be visible
        public sealed class PlaybackFactory : TesserafinApplicationFactory
        {
            protected override void ConfigureWebHost(IWebHostBuilder builder)
            {
                base.ConfigureWebHost(builder);

                builder.ConfigureTestServices(services =>
                {
                    var itemLookup = new Mock<IItemLookupService>();
                    itemLookup.Setup(m => m.GetItemById<BaseItem>(PlayableItemId)).Returns(new Movie { Id = PlayableItemId, Name = "Playable" });
                    itemLookup.Setup(m => m.GetItemById<BaseItem>(UnplannableItemId)).Returns(new Movie { Id = UnplannableItemId, Name = "Unplannable" });
                    services.AddSingleton(itemLookup.Object);

                    var mediaSources = new Mock<IMediaSourceManager>();
                    mediaSources
                        .Setup(m => m.GetPlaybackMediaSources(It.Is<BaseItem>(i => i.Id.Equals(PlayableItemId)), It.IsAny<User>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                        .ReturnsAsync(() => new[] { PlayableMediaSource() });
                    mediaSources
                        .Setup(m => m.GetPlaybackMediaSources(It.Is<BaseItem>(i => i.Id.Equals(UnplannableItemId)), It.IsAny<User>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                        .ReturnsAsync(Array.Empty<MediaSourceInfo>());
                    services.AddSingleton(mediaSources.Object);
                });
            }

            /// <summary>
            /// A fresh instance per call: <c>StreamBuilder</c> mutates the media source it plans
            /// against, so sharing one between tests would let an earlier plan leak into a later one.
            /// </summary>
            private static MediaSourceInfo PlayableMediaSource() => new()
            {
                Id = PlayableItemId.ToString("N"),
                Path = "/media/playable.mp4",
                Protocol = Tesserafin.Model.MediaInfo.MediaProtocol.File,
                Container = "mp4",
                SupportsDirectPlay = true,
                SupportsDirectStream = true,
                SupportsTranscoding = true,
                RunTimeTicks = TimeSpan.FromMinutes(1).Ticks,
                MediaStreams = new List<MediaStream>
                {
                    new() { Type = MediaStreamType.Video, Index = 0, Codec = "h264", IsInterlaced = false, Width = 1920, Height = 1080 },
                    new() { Type = MediaStreamType.Audio, Index = 1, Codec = "aac", Channels = 2 },
                },
            };
        }
#pragma warning restore CA1034
    }
}
