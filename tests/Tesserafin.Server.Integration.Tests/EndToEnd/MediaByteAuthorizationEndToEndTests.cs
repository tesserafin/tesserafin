using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Tesserafin.Api.Models.PlaybackSessionDtos;
using Tesserafin.Common.Configuration;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.IO;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Controller.Net.PlaybackCredentials;
using Tesserafin.Controller.Persistence;
using Tesserafin.Controller.Trickplay;
using Tesserafin.Data.Entities;
using Tesserafin.Extensions.Json;
using Tesserafin.Model.Dto;
using Tesserafin.Model.Entities;
using Tesserafin.Model.IO;
using Tesserafin.Server.Integration.Tests.PlaybackCredentials;
using Xunit;

namespace Tesserafin.Server.Integration.Tests.EndToEnd;

/// <summary>
/// SECURITY-MEDIA-R1, over HTTP: whether the routes that serve an item's bytes ask if the caller may
/// see the item. Real accounts with their own tokens, a local file, a real ffmpeg.
/// </summary>
/// <remarks>
/// The withheld user is restricted by a blocked tag: the same predicate
/// (<c>BaseItem.IsVisibleStandalone</c>) that decides a library restriction, and one this harness
/// can express - its seeded items have no collection folder for a library restriction to name.
///
/// A refusal is compared with the answer for an item that does not exist, sent by the same user:
/// the two have to be the same answer, or the route tells a user which hidden items exist.
/// </remarks>
public sealed class MediaByteAuthorizationEndToEndTests : IClassFixture<HlsIsolationApplicationFactory>, IAsyncLifetime
{
    private const string WithheldTag = "smr1-withheld";
    private const string Player = "smr1-player/1.0";
    private const string SubtitleText = "PR119 end-to-end external subtitle fixture";
    private const int Seconds = 20;
    private const int AttachmentIndex = 3;

    private static readonly byte[] _mark = Encoding.ASCII.GetBytes("\n-- SECURITY-MEDIA-R1: on disk before the withheld user asked --\n");

    private readonly HlsIsolationApplicationFactory _factory;
    private readonly ConcurrentQueue<TranscodingJob> _started = new();
    private HttpClient _admin = null!;
    private HttpClient _allowed = null!;
    private HttpClient _second = null!;
    private HttpClient _withheld = null!;
    private Guid _allowedId;
    private Guid _secondId;
    private Guid _withheldId;
    private Guid _item;
    private Guid _own;
    private byte[] _itemBytes = null!;
    private byte[] _ownBytes = null!;
    private string _suffix = null!;
    private string _workDir = null!;
    private string _transcodes = null!;
    private ITranscodeManager _transcodeManager = null!;
    private FailingRemovals _fileSystem = null!;

    public MediaByteAuthorizationEndToEndTests(HlsIsolationApplicationFactory factory)
    {
        _factory = factory;
    }

    public static TheoryData<string, string, string> ItemRoutes => new()
    {
        { "GET", "Videos/{item}/{ms}/Subtitles/2/Stream.vtt", string.Empty },
        { "GET", "Videos/{item}/{ms}/Subtitles/2/Stream.srt", string.Empty },
        { "GET", "Videos/{item}/{ms}/Subtitles/2/0/Stream.vtt", string.Empty },
        { "GET", "Videos/{item}/{ms}/Subtitles/2/subtitles.m3u8?segmentLength=30", string.Empty },
        { "GET", "Items/{item}/Download", string.Empty },
        { "GET", "Items/{item}/File", string.Empty },
    };

    public async ValueTask InitializeAsync()
    {
        var (adminToken, _) = await _factory.EnsureAuthenticatedAsync();
        _admin = _factory.CreateClient();
        _admin.DefaultRequestHeaders.AddAuthHeader(adminToken);

        _suffix = Guid.NewGuid().ToString("N")[..8];
        (_allowed, _allowedId) = await SignInAsync("smr1-allowed-" + _suffix, withheld: false, cancellationToken: TestContext.Current.CancellationToken);
        (_second, _secondId) = await SignInAsync("smr1-second-" + _suffix, withheld: false, cancellationToken: TestContext.Current.CancellationToken);
        (_withheld, _withheldId) = await SignInAsync("smr1-withheld-" + _suffix, withheld: true, cancellationToken: TestContext.Current.CancellationToken);

        _workDir = Directory.CreateTempSubdirectory("tesserafin-smr1-e2e-").FullName;
        var libraryManager = _factory.Services.GetRequiredService<ILibraryManager>();
        var libraryManagerWrites = _factory.Services.GetRequiredService<IItemWriteCommands>();
        var streams = _factory.Services.GetRequiredService<IMediaStreamRepository>();

        var withheldFile = await EndToEndMediaFixtures.CreateH264AacMp4Async(_workDir, "withheld.mp4", Seconds);
        _itemBytes = await File.ReadAllBytesAsync(withheldFile);
        _item = LibraryItemSeeder.SeedVideo(
            libraryManagerWrites,
            streams,
            withheldFile,
            "mp4",
            [.. Streams(), new MediaStream { Index = 2, Type = MediaStreamType.Subtitle, Codec = "srt", IsExternal = true, Path = EndToEndMediaFixtures.CreateExternalSrt(_workDir), Language = "eng" }],
            "SECURITY-MEDIA-R1 withheld " + _suffix,
            Seconds * TimeSpan.TicksPerSecond,
            [WithheldTag]);

        // An item of its own for the withheld user: shorter, so its bytes are not the other's.
        var ownFile = await EndToEndMediaFixtures.CreateH264AacMp4Async(_workDir, "own.mp4", 3);
        _ownBytes = await File.ReadAllBytesAsync(ownFile);
        _own = LibraryItemSeeder.SeedVideo(libraryManagerWrites, streams, ownFile, "mp4", Streams(), "SECURITY-MEDIA-R1 own " + _suffix, 3 * TimeSpan.TicksPerSecond);
        Assert.False(_ownBytes.AsSpan().SequenceEqual(_itemBytes));

        _transcodes = _factory.Services.GetRequiredService<IServerConfigurationManager>().GetTranscodePath();
        _fileSystem = (FailingRemovals)_factory.Services.GetRequiredService<IFileSystem>();
        _transcodeManager = _factory.Services.GetRequiredService<ITranscodeManager>();
        _transcodeManager.TranscodingJobStarted += OnStarted;
    }

    public ValueTask DisposeAsync()
    {
        _transcodeManager.TranscodingJobStarted -= OnStarted;
        _fileSystem.FailDeferredRemovalsUnder = null;
        _admin.Dispose();
        _allowed.Dispose();
        _second.Dispose();
        _withheld.Dispose();
        try
        {
            Directory.Delete(_workDir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// The withheld user does not see the item, and every route that would serve it answers that
    /// user as it answers for an item that does not exist - with no transcode started for it.
    /// </summary>
    /// <param name="method">The HTTP method.</param>
    /// <param name="template">The route, with the item, its media source and a fresh id to fill in.</param>
    /// <param name="range">A Range header, or empty.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
    [Theory(Timeout = 120_000)]
    [MemberData(nameof(ItemRoutes))]
    public async Task WithheldItem_IsAnsweredAsAnItemThatDoesNotExist(string method, string template, string range)
    {
        using (var hidden = await _withheld.GetAsync(FormattableString.Invariant($"Items/{_item}?userId={_withheldId}"), TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        }

        // The route serves the item at all: without this a refusal below shows nothing.
        var served = await SendAsync(_allowed, method, Fill(template, _item), range, TestContext.Current.CancellationToken);
        Assert.True((int)served.Status is >= 200 and < 300, FormattableString.Invariant($"the allowed user was answered {(int)served.Status}: this route does not serve the fixture"));

        var refused = await SendAsync(_withheld, method, Fill(template, _item), range, TestContext.Current.CancellationToken);
        var unknown = await SendAsync(_withheld, method, Fill(template, Guid.NewGuid()), range, TestContext.Current.CancellationToken);

        Assert.False(
            (int)refused.Status is >= 200 and < 300,
            FormattableString.Invariant($"the withheld user was answered {(int)refused.Status} with {refused.Body.Length} bytes"));
        Assert.Equal(HttpStatusCode.NotFound, refused.Status);
        Assert.Equal(unknown.Status, refused.Status);
        Assert.Equal(unknown.Body.Length, refused.Body.Length);
        Assert.False(Carries(refused.Body, _itemBytes.AsSpan(0, 64)) || Carries(refused.Body, Encoding.UTF8.GetBytes(SubtitleText)));
        Assert.DoesNotContain(_started, j => j.UserId.Equals(_withheldId));
    }

    /// <summary>
    /// #301: a capability beside another user's token is a contradiction, whoever that user is. Beside
    /// its own user's token, or alone, it is served.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
    [Fact(Timeout = 120_000)]
    public async Task CapabilityBesideADurableToken_IsServedOnlyForItsOwnUser()
    {
        var playSession = "pc301-" + Guid.NewGuid().ToString("N");
        var url = await CapabilityUrlAsync(_allowed, _allowedId, playSession);
        using var anonymous = _factory.CreateClient();

        Assert.True((await SendAsync(anonymous, "GET", url, string.Empty, TestContext.Current.CancellationToken)).Body.AsSpan().SequenceEqual(_itemBytes));
        Assert.True((await SendAsync(_allowed, "GET", url, string.Empty, TestContext.Current.CancellationToken)).Body.AsSpan().SequenceEqual(_itemBytes));

        var contradicted = await SendAsync(_admin, "GET", url, string.Empty, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, contradicted.Status);
        Assert.False(contradicted.Body.AsSpan().SequenceEqual(_itemBytes));
    }

    /// <summary>
    /// #301: an API key names no user, so beside a capability it contradicts it like any other
    /// durable credential, though alone it is served.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
    [Fact(Timeout = 120_000)]
    public async Task CapabilityBesideAnApiKey_IsRefused()
    {
        var app = "pc301-key-" + _suffix;
        using (var created = await _admin.PostAsync("Auth/Keys?app=" + app, null, TestContext.Current.CancellationToken))
        {
            created.EnsureSuccessStatusCode();
        }

        using var keys = JsonDocument.Parse(await _admin.GetStringAsync("Auth/Keys", TestContext.Current.CancellationToken));
        var key = keys.RootElement.GetProperty("Items").EnumerateArray().Single(k => k.GetProperty("AppName").GetString() == app).GetProperty("AccessToken").GetString();
        var playSession = "pc301-key-" + Guid.NewGuid().ToString("N");
        var url = await CapabilityUrlAsync(_allowed, _allowedId, playSession);
        using var anonymous = _factory.CreateClient();

        var contradicted = await SendAsync(anonymous, "GET", url + "&ApiKey=" + key, string.Empty, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, contradicted.Status);
    }

    /// <summary>
    /// #301: two users on one client-chosen play session id each hold a session and a capability.
    /// Ending one ends only that user's, by deleting the session or by the stop report.
    /// </summary>
    /// <param name="byStopReport">Whether Alice ends with the stop report rather than the delete.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
    [Theory(Timeout = 120_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TwoUsersOnOnePlaySessionId_EndingOneLeavesTheOthersCapabilityServing(bool byStopReport)
    {
        var playSession = "pc301-shared-" + Guid.NewGuid().ToString("N");
        var aliceSession = await CreatePlaybackSessionAsync(_allowed, _allowedId, playSession, TestContext.Current.CancellationToken);
        var bobSession = await CreatePlaybackSessionAsync(_second, _secondId, playSession, TestContext.Current.CancellationToken);
        Assert.NotEqual(aliceSession, bobSession);

        var aliceUrl = await CapabilityUrlAsync(_allowed, _allowedId, playSession);
        var bobUrl = await CapabilityUrlAsync(_second, _secondId, playSession);
        using var anonymous = _factory.CreateClient();
        Assert.True((await SendAsync(anonymous, "GET", aliceUrl, string.Empty, TestContext.Current.CancellationToken)).Body.AsSpan().SequenceEqual(_itemBytes));
        Assert.True((await SendAsync(anonymous, "GET", bobUrl, string.Empty, TestContext.Current.CancellationToken)).Body.AsSpan().SequenceEqual(_itemBytes));

        await EndAsync(_allowed, aliceSession, playSession, byStopReport, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(anonymous, "GET", aliceUrl, string.Empty, TestContext.Current.CancellationToken)).Status);
        Assert.True((await SendAsync(anonymous, "GET", bobUrl, string.Empty, TestContext.Current.CancellationToken)).Body.AsSpan().SequenceEqual(_itemBytes));
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(_second, "GET", FormattableString.Invariant($"Playback/Sessions/{bobSession}/Stream"), string.Empty, TestContext.Current.CancellationToken)).Status);
    }

    /// <summary>
    /// #301, device half: the stop report ends what its own device was issued; a delete names the
    /// user's session and ends what that user holds under the id, on any device.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
    [Fact(Timeout = 120_000)]
    public async Task TwoDevicesOfOneUser_OnePlaySessionId_StopReportEndsOnlyItsDevice_DeleteEndsBoth()
    {
        var playSession = "pc301-devices-" + Guid.NewGuid().ToString("N");
        using var otherDevice = await SignInAgainAsync("smr1-allowed-" + _suffix, "pc301-other-device", TestContext.Current.CancellationToken);
        var firstUrl = await CapabilityUrlAsync(_allowed, _allowedId, playSession);
        var otherUrl = await CapabilityUrlAsync(otherDevice, _allowedId, playSession);
        using var anonymous = _factory.CreateClient();

        await EndAsync(otherDevice, Guid.Empty, playSession, byStopReport: true, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(anonymous, "GET", otherUrl, string.Empty, TestContext.Current.CancellationToken)).Status);
        Assert.True((await SendAsync(anonymous, "GET", firstUrl, string.Empty, TestContext.Current.CancellationToken)).Body.AsSpan().SequenceEqual(_itemBytes));

        var otherAgain = await CapabilityUrlAsync(otherDevice, _allowedId, playSession);
        var session = await CreatePlaybackSessionAsync(_allowed, _allowedId, playSession, TestContext.Current.CancellationToken);
        await EndAsync(_allowed, session, playSession, byStopReport: false, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(anonymous, "GET", firstUrl, string.Empty, TestContext.Current.CancellationToken)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(anonymous, "GET", otherAgain, string.Empty, TestContext.Current.CancellationToken)).Status);
    }

    /// <summary>
    /// A subtitle capability is validated against the item and media source in the route; the
    /// obsolete query parameters name another item afterwards.
    /// </summary>
    /// <param name="template">The subtitle route, with the item and its media source to fill in.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
    [Theory(Timeout = 120_000)]
    [InlineData("Videos/{0}/{0:N}/Subtitles/2/Stream.vtt")]
    [InlineData("Videos/{0}/{0:N}/Subtitles/2/0/Stream.vtt")]
    public async Task SubtitleCapabilityForOneItem_WithObsoleteParametersNamingAnother_IsRefused(string template)
    {
        var playSession = "smr1-subtitles-" + Guid.NewGuid().ToString("N");
        var capability = await MediaBoundaryFixture.MintWithAsync(_allowed, [PlaybackCapabilityScope.Subtitles], _own, _own.ToString("N", CultureInfo.InvariantCulture), playSession);
        var credential = "playbackCapability=" + Uri.EscapeDataString(capability.Value);
        using var anonymous = _factory.CreateClient();

        // What the capability was minted for is asked for, and answered, by the route's own values.
        var own = string.Format(CultureInfo.InvariantCulture, template, _own);
        var bound = await SendAsync(anonymous, "GET", string.Format(CultureInfo.InvariantCulture, template, _item) + "?" + credential, string.Empty, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, bound.Status);

        foreach (var rewrite in new[] { FormattableString.Invariant($"itemId={_item}&mediaSourceId={_item:N}"), FormattableString.Invariant($"itemId={_item}"), FormattableString.Invariant($"mediaSourceId={_item:N}") })
        {
            var rewritten = await SendAsync(anonymous, "GET", own + "?" + rewrite + "&" + credential, string.Empty, TestContext.Current.CancellationToken);

            Assert.False(Carries(rewritten.Body, Encoding.UTF8.GetBytes(SubtitleText)), rewrite + ": the capability was served the subtitles of an item it was not minted for");
            Assert.Equal(HttpStatusCode.Unauthorized, rewritten.Status);
        }

        // Naming what the route already names changes nothing, with a capability or with a token.
        var same = FormattableString.Invariant($"itemId={_item}&mediaSourceId={_item:N}");
        var item = string.Format(CultureInfo.InvariantCulture, template, _item);
        var itemCapability = await MediaBoundaryFixture.MintWithAsync(_allowed, [PlaybackCapabilityScope.Subtitles], _item, _item.ToString("N", CultureInfo.InvariantCulture), playSession);
        Assert.True(Carries((await SendAsync(anonymous, "GET", item + "?" + same + "&playbackCapability=" + Uri.EscapeDataString(itemCapability.Value), string.Empty, TestContext.Current.CancellationToken)).Body, Encoding.UTF8.GetBytes(SubtitleText)));
        Assert.True(Carries((await SendAsync(_allowed, "GET", own + "?" + same, string.Empty, TestContext.Current.CancellationToken)).Body, Encoding.UTF8.GetBytes(SubtitleText)));
    }

    /// <summary>
    /// A user who may see the item plays it; the right is withdrawn; the same requests are sent again.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
    [Fact(Timeout = 120_000)]
    public async Task RightWithdrawnBetweenTwoRequests_TheSecondIsRefused_ForATokenACapabilityAndARunningTranscode()
    {
        var playSession = "smr1-withdrawn-" + Guid.NewGuid().ToString("N");
        var byCapability = await CapabilityUrlAsync(_second, _secondId, playSession);
        var direct = byCapability[..byCapability.IndexOf("&playbackCapability=", StringComparison.Ordinal)];
        var progressiveSession = playSession + "-progressive";
        var progressive = await ProgressiveUrlAsync(_second, _secondId, progressiveSession);
        using var anonymous = _factory.CreateClient();

        var before = await SendAsync(_second, "GET", direct, string.Empty, TestContext.Current.CancellationToken);
        Assert.True(before.Body.AsSpan().SequenceEqual(_itemBytes));
        Assert.True((await SendAsync(anonymous, "GET", byCapability, string.Empty, TestContext.Current.CancellationToken)).Body.AsSpan().SequenceEqual(_itemBytes));

        using var request = Request("GET", progressive);
        using var playing = await _second.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, playing.StatusCode);
        var job = Assert.Single(_started, j => j.PlaySessionId == progressiveSession);
        var requests = job.ActiveRequestCount;

        await WithholdAsync(_secondId, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(_second, "GET", direct, string.Empty, TestContext.Current.CancellationToken)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(_second, "HEAD", direct, string.Empty, TestContext.Current.CancellationToken)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(_second, "GET", direct, "bytes=0-15", TestContext.Current.CancellationToken)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(anonymous, "GET", byCapability, string.Empty, TestContext.Current.CancellationToken)).Status);

        // Its own transcode, still registered, on an output that is still there.
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(_second, "GET", progressive, string.Empty, TestContext.Current.CancellationToken)).Status);
        Assert.Equal(requests, job.ActiveRequestCount);
        Assert.Equal(1, _started.Count(j => j.UserId.Equals(_secondId)));
    }

    /// <summary>
    /// The two routes the fix changed that a bare film does not reach: an attachment and the
    /// trickplay playlist. Each is served to a user who may see the
    /// item, refused to one who may not as an item that does not exist, and refused to the first
    /// once its right is withdrawn.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
    [Fact(Timeout = 120_000)]
    public async Task AttachmentAndTrickplayPlaylist_AreServedToWhoMaySeeTheItem_AndToNobodyElse()
    {
        await SeedAttachmentAndTrickplayAsync(TestContext.Current.CancellationToken);

        (string Template, byte[] Content)[] routes =
        [
            ("Videos/{0}/{0:N}/Attachments/" + AttachmentIndex, _mark),
            ("Videos/{0}/Trickplay/" + EndToEndMediaFixtures.Width + "/tiles.m3u8", Encoding.ASCII.GetBytes("#EXT-X-TILES")),
        ];
        string Url(string template, Guid item) => string.Format(CultureInfo.InvariantCulture, template, item);

        foreach (var (template, content) in routes)
        {
            var served = await SendAsync(_second, "GET", Url(template, _item), string.Empty, TestContext.Current.CancellationToken);
            Assert.True(served.Status == HttpStatusCode.OK && Carries(served.Body, content), FormattableString.Invariant($"{template}: the allowed user was answered {(int)served.Status}"));

            var refused = await SendAsync(_withheld, "GET", Url(template, _item), string.Empty, TestContext.Current.CancellationToken);
            var unknown = await SendAsync(_withheld, "GET", Url(template, Guid.NewGuid()), string.Empty, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, refused.Status);
            Assert.Equal(unknown.Status, refused.Status);
            Assert.Equal(unknown.Body.Length, refused.Body.Length);
            Assert.False(Carries(refused.Body, content));
        }

        await WithholdAsync(_secondId, cancellationToken: TestContext.Current.CancellationToken);

        foreach (var (template, content) in routes)
        {
            var withdrawn = await SendAsync(_second, "GET", Url(template, _item), string.Empty, TestContext.Current.CancellationToken);
            Assert.True(withdrawn.Status == HttpStatusCode.NotFound, FormattableString.Invariant($"{template}: answered {(int)withdrawn.Status} after the right was withdrawn"));
            Assert.False(Carries(withdrawn.Body, content));
        }
    }

    /// <summary>
    /// The withheld item is another version of a visible one: its attachment and its trickplay
    /// playlist, asked of the visible item, are answered as for a version that does not exist -
    /// and the attachment's refusal carries no message naming the version asked for.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
    [Fact(Timeout = 120_000)]
    public async Task AttachmentAndTrickplayOfAWithheldVersion_AreAnsweredAsAVersionThatDoesNotExist()
    {
        var token = TestContext.Current.CancellationToken;
        await SeedAttachmentAndTrickplayAsync(token);
        var trickplay = _factory.Services.GetRequiredService<ITrickplayManager>();
        await SaveTrickplayResolutionAsync(trickplay, _own);

        using (var merged = await _admin.PostAsync(FormattableString.Invariant($"Videos/MergeVersions?ids={_own},{_item}"), null, token))
        {
            Assert.Equal(HttpStatusCode.NoContent, merged.StatusCode);
        }

        var video = Assert.IsAssignableFrom<Video>(_factory.Services.GetRequiredService<IItemLookupService>().GetItemById(_own));
        var manifest = await trickplay.GetTrickplayManifest(video);
        Assert.Equal(_own, Assert.Single(manifest[_own.ToString("N", CultureInfo.InvariantCulture)]).Value.ItemId);
        Assert.Equal(_item, Assert.Single(manifest[_item.ToString("N", CultureInfo.InvariantCulture)]).Value.ItemId);

        string[] templates =
        [
            "Videos/{0}/{1:N}/Attachments/" + AttachmentIndex,
            "Videos/{0}/Trickplay/" + EndToEndMediaFixtures.Width + "/tiles.m3u8?mediaSourceId={1}",
        ];
        foreach (var template in templates)
        {
            string Url(Guid source) => string.Format(CultureInfo.InvariantCulture, template, _own, source);

            var served = await SendAsync(_allowed, "GET", Url(_item), string.Empty, TestContext.Current.CancellationToken);
            Assert.True(served.Status == HttpStatusCode.OK, FormattableString.Invariant($"{template}: the allowed user was answered {(int)served.Status}"));

            var asked = Guid.NewGuid();
            var refused = await SendAsync(_withheld, "GET", Url(_item), string.Empty, TestContext.Current.CancellationToken);
            var unknown = await SendAsync(_withheld, "GET", Url(asked), string.Empty, TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.NotFound, refused.Status);
            Assert.Equal(unknown.Status, refused.Status);
            Assert.Equal(unknown.Body.Length, refused.Body.Length);
            Assert.False(Carries(refused.Body, _mark));
            Assert.False(Carries(unknown.Body, Encoding.ASCII.GetBytes(asked.ToString("N", CultureInfo.InvariantCulture))), template + ": the refusal names the media source asked for");
        }
    }

    /// <summary>
    /// Two users who may both see the item are each served it, and each transcode is its user's own.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
    [Fact(Timeout = 120_000)]
    public async Task TwoUsersWhoMaySeeTheItem_AreEachServedIt_FromATranscodeOfTheirOwn()
    {
        foreach (var (client, userId) in new[] { (_allowed, _allowedId), (_second, _secondId) })
        {
            var (_, file) = await SessionFileRoute.OpenAsync(client, userId, _item, SessionFileRoute.NewPlaySession(), TestContext.Current.CancellationToken);
            var direct = await SendAsync(client, "GET", file!, string.Empty, TestContext.Current.CancellationToken);
            Assert.True(direct.Body.AsSpan().SequenceEqual(_itemBytes));

            var ranged = await SendAsync(client, "GET", file!, "bytes=16-31", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.PartialContent, ranged.Status);
            Assert.True(ranged.Body.AsSpan().SequenceEqual(_itemBytes.AsSpan(16, 16)));

            var playSession = "smr1-both-" + Guid.NewGuid().ToString("N");
            var transcoded = await SendAsync(client, "GET", await ProgressiveUrlAsync(client, userId, playSession), string.Empty, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, transcoded.Status);
            Assert.Equal(0x47, transcoded.Body[0]);
            Assert.Equal(userId, Assert.Single(_started, j => j.PlaySessionId == playSession).UserId);
        }
    }

    /// <summary>
    /// An API key is no user: it is not restricted by any user's policy, as on every other route
    /// (<c>DefaultAuthorizationHandler</c>: "Api keys are unrestricted").
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
    [Fact(Timeout = 120_000)]
    public async Task ApiKey_IsServedTheItem()
    {
        var app = "smr1-key-" + _suffix;
        using (var created = await _admin.PostAsync("Auth/Keys?app=" + app, null, TestContext.Current.CancellationToken))
        {
            created.EnsureSuccessStatusCode();
        }

        using var keys = JsonDocument.Parse(await _admin.GetStringAsync("Auth/Keys", TestContext.Current.CancellationToken));
        var key = keys.RootElement.GetProperty("Items").EnumerateArray().Single(k => k.GetProperty("AppName").GetString() == app).GetProperty("AccessToken").GetString();
        using var anonymous = _factory.CreateClient();
        var (_, file) = await SessionFileRoute.OpenAsync(_allowed, _allowedId, _item, SessionFileRoute.NewPlaySession(), TestContext.Current.CancellationToken);

        var served = await SendAsync(anonymous, "GET", file + "&ApiKey=" + key, string.Empty, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, served.Status);
        Assert.True(served.Body.AsSpan().SequenceEqual(_itemBytes));
    }

    /// <summary>
    /// Planning a playback of the withheld item is refused as for an item that does not exist.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
    [Fact(Timeout = 120_000)]
    public async Task PlanningAPlaybackOfTheWithheldItem_IsAnsweredAsAnItemThatDoesNotExist()
    {
        var (capabilities, constraints) = EndToEndCapabilityPresets.TranscodeHls();
        async Task<HttpStatusCode> PlanAsync(HttpClient client, Guid userId, Guid item)
        {
            using var planned = await client.PostAsJsonAsync(
                "Playback/Sessions",
                new CreatePlaybackSessionRequest(item, userId, capabilities, constraints, MediaSourceId: null, PlaySessionId: "smr1-plan-" + Guid.NewGuid().ToString("N")),
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            return planned.StatusCode;
        }

        Assert.True((int)await PlanAsync(_allowed, _allowedId, _item) is >= 200 and < 300);
        var refused = await PlanAsync(_withheld, _withheldId, _item);
        Assert.Equal(HttpStatusCode.NotFound, refused);
        Assert.Equal(await PlanAsync(_withheld, _withheldId, Guid.NewGuid()), refused);
    }

    private static MediaStream[] Streams() =>
    [
        new MediaStream { Index = 0, Type = MediaStreamType.Video, Codec = EndToEndCapabilityPresets.FixtureVideoCodec, Width = EndToEndMediaFixtures.Width, Height = EndToEndMediaFixtures.Height, IsDefault = true },
        new MediaStream { Index = 1, Type = MediaStreamType.Audio, Codec = EndToEndCapabilityPresets.FixtureAudioCodec, Channels = 2, IsDefault = true },
    ];

    private static string Fill(string template, Guid item)
        => template
            .Replace("{item}", item.ToString(), StringComparison.Ordinal)
            .Replace("{ms}", item.ToString("N", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{id}", "smr1-" + Guid.NewGuid().ToString("N"), StringComparison.Ordinal);

    private static bool Carries(byte[] body, ReadOnlySpan<byte> content) => body.AsSpan().IndexOf(content) >= 0;

    private static HttpRequestMessage Request(string method, string url)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), url);
        request.Headers.TryAddWithoutValidation("User-Agent", Player);
        return request;
    }

    private static async Task<(HttpStatusCode Status, byte[] Body)> SendAsync(HttpClient client, string method, string url, string range, CancellationToken cancellationToken)
    {
        using var request = Request(method, url);
        if (range.Length > 0)
        {
            request.Headers.Range = RangeHeaderValue.Parse(range);
        }

        using var response = await client.SendAsync(request, cancellationToken);
        return (response.StatusCode, await response.Content.ReadAsByteArrayAsync(cancellationToken));
    }

    private static async Task EventuallyAsync(Func<bool> condition, string otherwise, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, otherwise);
            await Task.Delay(100, cancellationToken);
        }
    }

    private void OnStarted(object? sender, TranscodingJob job) => _started.Enqueue(job);

    /// <summary>
    /// Gives the withheld item an attachment, already extracted, and a trickplay resolution.
    /// </summary>
    private async Task SeedAttachmentAndTrickplayAsync(CancellationToken cancellationToken)
    {
        const string FileName = "smr1.ttf";
        _factory.Services.GetRequiredService<IMediaAttachmentRepository>().SaveMediaAttachments(
            _item,
            [new MediaAttachment { Index = AttachmentIndex, Codec = "ttf", FileName = FileName, MimeType = "font/ttf" }],
            cancellationToken);
        var cacheName = AttachmentIndex.ToString(CultureInfo.InvariantCulture) + "-" + FileName;
        var extracted = _factory.Services.GetRequiredService<IPathManager>().GetAttachmentPath(_item.ToString("N", CultureInfo.InvariantCulture), cacheName)!;
        Directory.CreateDirectory(Path.GetDirectoryName(extracted)!);
        await File.WriteAllBytesAsync(extracted, _mark, cancellationToken);

        await SaveTrickplayResolutionAsync(_factory.Services.GetRequiredService<ITrickplayManager>(), _item);
    }

    private static Task SaveTrickplayResolutionAsync(ITrickplayManager trickplay, Guid itemId)
        => trickplay.SaveTrickplayInfo(new TrickplayInfo
        {
            ItemId = itemId,
            Width = EndToEndMediaFixtures.Width,
            Height = EndToEndMediaFixtures.Height,
            TileWidth = 1,
            TileHeight = 1,
            ThumbnailCount = 1,
            Interval = 1000,
            Bandwidth = 1,
        });

    /// <summary>
    /// Blocks the withheld tag for a user, or stops blocking it: its policy as it is, with that
    /// one field changed.
    /// </summary>
    private async Task WithholdAsync(Guid userId, bool withheld = true, CancellationToken cancellationToken = default)
    {
        var user = (await _admin.GetFromJsonAsync<UserDto>(FormattableString.Invariant($"Users/{userId}"), JsonDefaults.Options, cancellationToken))!;
        user.Policy!.BlockedTags = withheld ? [WithheldTag] : [];
        using var applied = await _admin.PostAsJsonAsync(FormattableString.Invariant($"Users/{userId}/Policy"), user.Policy, JsonDefaults.Options, cancellationToken);
        applied.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Opens a direct-play session for the caller under <paramref name="playSession"/> and names
    /// its <c>File</c> route with a capability minted for that play session.
    /// </summary>
    private async Task<string> CapabilityUrlAsync(HttpClient client, Guid userId, string playSession)
    {
        var (created, url) = await SessionFileRoute.OpenAsync(client, userId, _item, playSession, TestContext.Current.CancellationToken);
        Assert.True(url is not null, FormattableString.Invariant($"POST Playback/Sessions answered {(int)created}"));
        var capability = await MediaBoundaryFixture.MintWithAsync(client, [PlaybackCapabilityScope.Media], _item, _item.ToString("N", CultureInfo.InvariantCulture), playSession);
        return url + "&playbackCapability=" + Uri.EscapeDataString(capability.Value);
    }

    /// <summary>
    /// Opens a session for the caller whose plan is one progressive MPEG-TS stream, and names it.
    /// </summary>
    private async Task<string> ProgressiveUrlAsync(HttpClient client, Guid userId, string playSession)
    {
        var (capabilities, constraints) = EndToEndCapabilityPresets.RemuxMatroskaToMpegTs();
        capabilities = capabilities with { Decode = capabilities.Decode with { SubtitleDelivery = [new Tesserafin.Playback.Decision.SubtitleCapability("srt", Tesserafin.Playback.Decision.SubtitleDeliveryMethod.External)] } };
        var body = new CreatePlaybackSessionRequest(_item, userId, capabilities, constraints with { AllowDirectPlay = false }, MediaSourceId: null, PlaySessionId: playSession);
        using var response = await client.PostAsJsonAsync("Playback/Sessions", body, JsonDefaults.Options, TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, $"POST Playback/Sessions answered {(int)response.StatusCode}");
        var session = (await response.Content.ReadFromJsonAsync<PlaybackSessionResponse>(JsonDefaults.Options, TestContext.Current.CancellationToken))!;
        var descriptor = await client.GetFromJsonAsync<PlaybackSessionStreamDescriptor>(FormattableString.Invariant($"Playback/Sessions/{session.Id}/Stream"), JsonDefaults.Options, TestContext.Current.CancellationToken);
        return descriptor!.Url.TrimStart('/');
    }

    private async Task<Guid> CreatePlaybackSessionAsync(HttpClient client, Guid userId, string playSession, CancellationToken cancellationToken)
    {
        var (capabilities, constraints) = EndToEndCapabilityPresets.Remux();

        // The item carries an external SRT track: the client says it renders it beside the stream.
        // Nothing here may be transcoded, so a subtitle only burnt in would leave no playable output.
        capabilities = capabilities with { Decode = capabilities.Decode with { SubtitleDelivery = [new Tesserafin.Playback.Decision.SubtitleCapability("srt", Tesserafin.Playback.Decision.SubtitleDeliveryMethod.External)] } };
        var body = new CreatePlaybackSessionRequest(_item, userId, capabilities, constraints, MediaSourceId: null, PlaySessionId: playSession);
        using var response = await client.PostAsJsonAsync("Playback/Sessions", body, JsonDefaults.Options, cancellationToken);
        Assert.True(
            response.IsSuccessStatusCode,
            $"POST Playback/Sessions answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(cancellationToken)}");
        return (await response.Content.ReadFromJsonAsync<PlaybackSessionResponse>(JsonDefaults.Options, cancellationToken))!.Id;
    }

    private async Task EndAsync(HttpClient client, Guid session, string playSession, bool byStopReport, CancellationToken cancellationToken)
    {
        using var response = byStopReport
            ? await client.PostAsJsonAsync("Sessions/Playing/Stopped", new { ItemId = _item, PlaySessionId = playSession, PositionTicks = 0L }, JsonDefaults.Options, cancellationToken)
            : await client.DeleteAsync(FormattableString.Invariant($"Playback/Sessions/{session}"), cancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private async Task<HttpClient> SignInAgainAsync(string name, string device, CancellationToken cancellationToken)
    {
        using var anonymous = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/Users/AuthenticateByName");
        request.Headers.TryAddWithoutValidation(AuthHelper.AuthHeaderName, MediaBoundaryFixture.AuthorizationHeader(device, null));
        request.Content = JsonContent.Create(new { Username = name, Pw = "smr1-password" }, options: JsonDefaults.Options);
        using var authenticated = await anonymous.SendAsync(request, cancellationToken);
        authenticated.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await authenticated.Content.ReadAsStringAsync(cancellationToken));

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            AuthHelper.AuthHeaderName,
            MediaBoundaryFixture.AuthorizationHeader(device, document.RootElement.GetProperty("AccessToken").GetString()));
        return client;
    }

    private async Task<(HttpClient Client, Guid UserId)> SignInAsync(string name, bool withheld, CancellationToken cancellationToken)
    {
        const string Password = "smr1-password";
        UserDto created;
        using (var response = await _admin.PostAsJsonAsync("/Users/New", new { Name = name, Password }, JsonDefaults.Options, cancellationToken))
        {
            response.EnsureSuccessStatusCode();
            created = (await response.Content.ReadFromJsonAsync<UserDto>(JsonDefaults.Options, cancellationToken))!;
        }

        if (withheld)
        {
            await WithholdAsync(created.Id, cancellationToken: cancellationToken);
        }

        using var anonymous = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/Users/AuthenticateByName");
        request.Headers.TryAddWithoutValidation(AuthHelper.AuthHeaderName, MediaBoundaryFixture.AuthorizationHeader(name + "-device", null));
        request.Content = JsonContent.Create(new { Username = name, Pw = Password }, options: JsonDefaults.Options);
        using var authenticated = await anonymous.SendAsync(request, cancellationToken);
        authenticated.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await authenticated.Content.ReadAsStringAsync(cancellationToken));

        var client = _factory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(90);
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            AuthHelper.AuthHeaderName,
            MediaBoundaryFixture.AuthorizationHeader(name + "-device", document.RootElement.GetProperty("AccessToken").GetString()));
        return (client, created.Id);
    }
}
