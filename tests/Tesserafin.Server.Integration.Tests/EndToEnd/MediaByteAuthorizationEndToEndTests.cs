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
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Tesserafin.Api.Models.PlaybackSessionDtos;
using Tesserafin.Common.Configuration;
using Tesserafin.Controller.Configuration;
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
    private const string Transcode = "static=false&videoCodec=h264&audioCodec=aac&videoBitRate=1500000&audioBitRate=128000";
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
        { "GET", "Videos/{item}/stream?static=true&mediaSourceId={ms}", string.Empty },
        { "HEAD", "Videos/{item}/stream?static=true&mediaSourceId={ms}", string.Empty },
        { "GET", "Videos/{item}/stream?static=true&mediaSourceId={ms}", "bytes=0-15" },
        { "GET", "Videos/{item}/stream.mp4?static=true&mediaSourceId={ms}", string.Empty },
        { "GET", "Audio/{item}/stream?static=true&mediaSourceId={ms}", string.Empty },
        { "GET", "Videos/{item}/stream.ts?" + Transcode + "&mediaSourceId={ms}&deviceId={id}&playSessionId={id}", string.Empty },
        { "HEAD", "Videos/{item}/stream.ts?" + Transcode + "&mediaSourceId={ms}&deviceId={id}&playSessionId={id}", string.Empty },
        { "GET", "Audio/{item}/stream.mp3?static=false&audioCodec=mp3&audioBitRate=128000&mediaSourceId={ms}&deviceId={id}&playSessionId={id}", string.Empty },
        { "GET", "Audio/{item}/universal?container=mp3&audioCodec=mp3&transcodingContainer=mp3&transcodingProtocol=http&mediaSourceId={ms}&deviceId={id}", string.Empty },
        { "GET", "Videos/{item}/master.m3u8?" + Transcode + "&segmentContainer=ts&mediaSourceId={ms}&deviceId={id}&playSessionId={id}", string.Empty },
        { "HEAD", "Videos/{item}/master.m3u8?" + Transcode + "&segmentContainer=ts&mediaSourceId={ms}&deviceId={id}&playSessionId={id}", string.Empty },
        { "GET", "Videos/{item}/main.m3u8?" + Transcode + "&segmentContainer=ts&mediaSourceId={ms}&deviceId={id}&playSessionId={id}", string.Empty },
        { "GET", "Videos/{item}/hls1/main/0.ts?" + Transcode + "&segmentContainer=ts&runtimeTicks=0&actualSegmentLengthTicks=30000000&mediaSourceId={ms}&deviceId={id}&playSessionId={id}", string.Empty },
        { "GET", "Videos/{item}/live.m3u8?container=ts&" + Transcode + "&mediaSourceId={ms}&deviceId={id}&playSessionId={id}", string.Empty },
        { "GET", "Audio/{item}/main.m3u8?static=false&audioCodec=aac&audioBitRate=128000&segmentContainer=ts&mediaSourceId={ms}&deviceId={id}&playSessionId={id}", string.Empty },
        { "GET", "Audio/{item}/hls1/main/0.ts?static=false&audioCodec=aac&audioBitRate=128000&segmentContainer=ts&runtimeTicks=0&actualSegmentLengthTicks=30000000&mediaSourceId={ms}&deviceId={id}&playSessionId={id}", string.Empty },
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
        (_allowed, _allowedId) = await SignInAsync("smr1-allowed-" + _suffix, withheld: false);
        (_second, _secondId) = await SignInAsync("smr1-second-" + _suffix, withheld: false);
        (_withheld, _withheldId) = await SignInAsync("smr1-withheld-" + _suffix, withheld: true);

        _workDir = Directory.CreateTempSubdirectory("tesserafin-smr1-e2e-").FullName;
        var libraryManager = _factory.Services.GetRequiredService<ILibraryManager>();
        var streams = _factory.Services.GetRequiredService<IMediaStreamRepository>();

        var withheldFile = await EndToEndMediaFixtures.CreateH264AacMp4Async(_workDir, "withheld.mp4", Seconds);
        _itemBytes = await File.ReadAllBytesAsync(withheldFile);
        _item = LibraryItemSeeder.SeedVideo(
            libraryManager,
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
        _own = LibraryItemSeeder.SeedVideo(libraryManager, streams, ownFile, "mp4", Streams(), "SECURITY-MEDIA-R1 own " + _suffix, 3 * TimeSpan.TicksPerSecond);
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
        var served = await SendAsync(_allowed, method, Fill(template, _item), range);
        Assert.True((int)served.Status is >= 200 and < 300, FormattableString.Invariant($"the allowed user was answered {(int)served.Status}: this route does not serve the fixture"));

        var refused = await SendAsync(_withheld, method, Fill(template, _item), range);
        var unknown = await SendAsync(_withheld, method, Fill(template, Guid.NewGuid()), range);

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
    /// The allowed user's progressive transcode is registered; the withheld user replays its url.
    /// Then the transcode is released, its file stays, and the replay is sent again.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
    [Fact(Timeout = 120_000)]
    public async Task ProgressiveUrlOfAnAllowedUser_ReplayedByTheWithheldUser_IsNotAnswered_ActiveOrResidual()
    {
        var playSession = "smr1-replay-" + Guid.NewGuid().ToString("N");
        var url = FormattableString.Invariant($"Videos/{_item}/stream.ts?{Transcode}&mediaSourceId={_item:N}&deviceId={playSession}&playSessionId={playSession}");

        TranscodingJob job;
        int requests;
        using (var request = Request("GET", url))
        using (var playing = await _allowed.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, playing.StatusCode);
            job = Assert.Single(_started, j => j.PlaySessionId == playSession);
            Assert.Same(job, _transcodeManager.GetTranscodingJob(job.Path!, TranscodingJobType.Progressive));
            requests = job.ActiveRequestCount;

            // Active: the owner is still connected.
            var active = await SendAsync(_withheld, "GET", url, string.Empty);
            Assert.Equal(HttpStatusCode.NotFound, active.Status);
            Assert.Equal(requests, job.ActiveRequestCount);
        }

        // Released by its owner; the removal of its file fails, so the file stays with no job on it.
        _fileSystem.FailDeferredRemovalsUnder = _transcodes;
        using (var stopped = await _allowed.DeleteAsync(FormattableString.Invariant($"Videos/ActiveEncodings?deviceId={playSession}&playSessionId={playSession}"), TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, stopped.StatusCode);
        }

        await EventuallyAsync(() => _transcodeManager.GetTranscodingJob(job.Path!, TranscodingJobType.Progressive) is null, "the released transcode never left the registry");
        await File.WriteAllBytesAsync(job.Path!, _mark, TestContext.Current.CancellationToken);

        var residual = await SendAsync(_withheld, "GET", url, string.Empty);

        Assert.True(File.Exists(job.Path), "the released transcode's file is not on disk: this run shows nothing");
        Assert.Equal(HttpStatusCode.NotFound, residual.Status);
        Assert.False(Carries(residual.Body, _mark));
        Assert.DoesNotContain(_started, j => j.UserId.Equals(_withheldId));
    }

    /// <summary>
    /// The withheld user asks for an item of its own, naming the play session of somebody's
    /// transcode of the item it may not see.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
    [Fact(Timeout = 120_000)]
    public async Task PlaySessionOfAnotherItemsTranscode_NamedOnOnesOwnItem_DoesNotChangeWhatIsServed()
    {
        var playSession = "smr1-borrowed-" + Guid.NewGuid().ToString("N");
        var theirs = FormattableString.Invariant($"Videos/{_item}/stream.ts?{Transcode}&mediaSourceId={_item:N}&deviceId={playSession}&playSessionId={playSession}");
        using var request = Request("GET", theirs);
        using var playing = await _allowed.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, playing.StatusCode);
        Assert.NotNull(_transcodeManager.GetTranscodingJob(playSession));

        var mine = await SendAsync(_withheld, "GET", FormattableString.Invariant($"Videos/{_own}/stream?static=true&mediaSourceId={_own:N}&playSessionId={playSession}"), string.Empty);

        Assert.False(mine.Body.AsSpan().SequenceEqual(_itemBytes), "the withheld user was served the withheld item's file under its own item's id");
        Assert.Equal(HttpStatusCode.OK, mine.Status);
        Assert.True(mine.Body.AsSpan().SequenceEqual(_ownBytes));
    }

    /// <summary>
    /// The withheld item is merged, as another version, with an item the withheld user may see.
    /// A media source is then another item's file, named on the visible item's routes.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
    [Fact(Timeout = 120_000)]
    public async Task WithheldItemMergedAsAVersionOfAVisibleOne_IsNotServedThroughTheVisibleOne()
    {
        using (var merged = await _admin.PostAsync(FormattableString.Invariant($"Videos/MergeVersions?ids={_own},{_item}"), null, TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, merged.StatusCode);
        }

        string[] templates =
        [
            "Videos/{0}/stream?static=true&mediaSourceId={1}",
            "Videos/{0}/stream.ts?" + Transcode + "&mediaSourceId={1}&deviceId={2}&playSessionId={2}",
            "Videos/{0}/master.m3u8?" + Transcode + "&segmentContainer=ts&mediaSourceId={1}&deviceId={2}&playSessionId={2}",
            "Videos/{0}/hls1/main/0.ts?" + Transcode + "&segmentContainer=ts&runtimeTicks=0&actualSegmentLengthTicks=30000000&mediaSourceId={1}&deviceId={2}&playSessionId={2}",
            "Videos/{0}/{1}/Subtitles/2/Stream.vtt",
            "Videos/{0}/{1}/Subtitles/2/subtitles.m3u8?segmentLength=30",
        ];
        foreach (var template in templates)
        {
            string Url(Guid source) => string.Format(CultureInfo.InvariantCulture, template, _own, source.ToString("N", CultureInfo.InvariantCulture), "smr1-" + Guid.NewGuid().ToString("N"));

            // The version is reached through the visible item at all: by a user who may see both.
            var served = await SendAsync(_allowed, "GET", Url(_item), string.Empty);
            Assert.True((int)served.Status is >= 200 and < 300, FormattableString.Invariant($"{template}: the allowed user was answered {(int)served.Status}"));

            var refused = await SendAsync(_withheld, "GET", Url(_item), string.Empty);
            var unknown = await SendAsync(_withheld, "GET", Url(Guid.NewGuid()), string.Empty);

            Assert.False((int)refused.Status is >= 200 and < 300, FormattableString.Invariant($"{template}: the withheld user was answered {(int)refused.Status} with {refused.Body.Length} bytes"));
            Assert.Equal(unknown.Status, refused.Status);
            Assert.Equal(unknown.Body.Length, refused.Body.Length);
        }

        Assert.DoesNotContain(_started, j => j.UserId.Equals(_withheldId));

        // Its own version is still its own.
        var mine = await SendAsync(_withheld, "GET", FormattableString.Invariant($"Videos/{_own}/stream?static=true&mediaSourceId={_own:N}"), string.Empty);
        Assert.True(mine.Body.AsSpan().SequenceEqual(_ownBytes));
    }

    /// <summary>
    /// A capability is validated against the media source the query names; the legacy
    /// <c>params</c> blob names another one afterwards.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
    [Fact(Timeout = 120_000)]
    public async Task CapabilityForOneMediaSource_WithAParamsBlobNamingAnother_IsRefused()
    {
        using (var merged = await _admin.PostAsync(FormattableString.Invariant($"Videos/MergeVersions?ids={_own},{_item}"), null, TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, merged.StatusCode);
        }

        var playSession = "smr1-params-" + Guid.NewGuid().ToString("N");
        var capability = await MediaBoundaryFixture.MintWithAsync(_allowed, [PlaybackCapabilityScope.Media], _own, _own.ToString("N", CultureInfo.InvariantCulture), playSession);
        var bound = FormattableString.Invariant($"Videos/{_own}/stream?static=true&mediaSourceId={_own:N}&playSessionId={playSession}&playbackCapability={Uri.EscapeDataString(capability.Value)}");
        using var anonymous = _factory.CreateClient();

        Assert.True((await SendAsync(anonymous, "GET", bound, string.Empty)).Body.AsSpan().SequenceEqual(_ownBytes));

        var rewritten = await SendAsync(anonymous, "GET", bound + FormattableString.Invariant($"&params=;;{_item:N}"), string.Empty);

        Assert.False(rewritten.Body.AsSpan().SequenceEqual(_itemBytes), "the capability was served a media source it was not minted for");
        Assert.Equal(HttpStatusCode.Unauthorized, rewritten.Status);
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
        var capability = await MediaBoundaryFixture.MintWithAsync(_allowed, [PlaybackCapabilityScope.Media], _item, _item.ToString("N", CultureInfo.InvariantCulture), playSession);
        var url = FormattableString.Invariant($"Videos/{_item}/stream?static=true&mediaSourceId={_item:N}&playSessionId={playSession}&playbackCapability={Uri.EscapeDataString(capability.Value)}");
        using var anonymous = _factory.CreateClient();

        Assert.True((await SendAsync(anonymous, "GET", url, string.Empty)).Body.AsSpan().SequenceEqual(_itemBytes));
        Assert.True((await SendAsync(_allowed, "GET", url, string.Empty)).Body.AsSpan().SequenceEqual(_itemBytes));

        var contradicted = await SendAsync(_admin, "GET", url, string.Empty);

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
        var url = await CapabilityUrlAsync(_allowed, playSession);
        using var anonymous = _factory.CreateClient();

        var contradicted = await SendAsync(anonymous, "GET", url + "&ApiKey=" + key, string.Empty);

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
        var aliceSession = await CreatePlaybackSessionAsync(_allowed, _allowedId, playSession);
        var bobSession = await CreatePlaybackSessionAsync(_second, _secondId, playSession);
        Assert.NotEqual(aliceSession, bobSession);

        var aliceUrl = await CapabilityUrlAsync(_allowed, playSession);
        var bobUrl = await CapabilityUrlAsync(_second, playSession);
        using var anonymous = _factory.CreateClient();
        Assert.True((await SendAsync(anonymous, "GET", aliceUrl, string.Empty)).Body.AsSpan().SequenceEqual(_itemBytes));
        Assert.True((await SendAsync(anonymous, "GET", bobUrl, string.Empty)).Body.AsSpan().SequenceEqual(_itemBytes));

        await EndAsync(_allowed, aliceSession, playSession, byStopReport);

        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(anonymous, "GET", aliceUrl, string.Empty)).Status);
        Assert.True((await SendAsync(anonymous, "GET", bobUrl, string.Empty)).Body.AsSpan().SequenceEqual(_itemBytes));
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(_second, "GET", FormattableString.Invariant($"Playback/Sessions/{bobSession}/Stream"), string.Empty)).Status);
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
        using var otherDevice = await SignInAgainAsync("smr1-allowed-" + _suffix, "pc301-other-device");
        var firstUrl = await CapabilityUrlAsync(_allowed, playSession);
        var otherUrl = await CapabilityUrlAsync(otherDevice, playSession);
        using var anonymous = _factory.CreateClient();

        await EndAsync(otherDevice, Guid.Empty, playSession, byStopReport: true);

        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(anonymous, "GET", otherUrl, string.Empty)).Status);
        Assert.True((await SendAsync(anonymous, "GET", firstUrl, string.Empty)).Body.AsSpan().SequenceEqual(_itemBytes));

        var otherAgain = await CapabilityUrlAsync(otherDevice, playSession);
        var session = await CreatePlaybackSessionAsync(_allowed, _allowedId, playSession);
        await EndAsync(_allowed, session, playSession, byStopReport: false);

        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(anonymous, "GET", firstUrl, string.Empty)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(anonymous, "GET", otherAgain, string.Empty)).Status);
    }

    /// <summary>
    /// #301: the owner stamped on a job started under a capability is the capability's user and the
    /// device of its session, even beside a token of the same user on another device.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
    [Fact(Timeout = 120_000)]
    public async Task JobStartedUnderACapabilityBesideAnotherDevicesToken_IsStampedWithTheCapabilitysDevice()
    {
        var name = "smr1-allowed-" + _suffix;
        var playSession = "pc301-stamp-" + Guid.NewGuid().ToString("N");
        using var otherDevice = await SignInAgainAsync(name, "pc301-stamp-device");
        var capability = await MediaBoundaryFixture.MintWithAsync(_allowed, [PlaybackCapabilityScope.Media], _item, _item.ToString("N", CultureInfo.InvariantCulture), playSession);
        var url = FormattableString.Invariant($"Videos/{_item}/stream.ts?{Transcode}&mediaSourceId={_item:N}&deviceId={playSession}&playSessionId={playSession}&playbackCapability={Uri.EscapeDataString(capability.Value)}");

        using var request = Request("GET", url);
        using var playing = await otherDevice.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, playing.StatusCode);
        var job = Assert.Single(_started, j => j.PlaySessionId == playSession);
        Assert.Equal(_allowedId, job.UserId);
        Assert.Equal(name + "-device", job.OwnerDeviceId);
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
        var bound = await SendAsync(anonymous, "GET", string.Format(CultureInfo.InvariantCulture, template, _item) + "?" + credential, string.Empty);
        Assert.Equal(HttpStatusCode.Unauthorized, bound.Status);

        foreach (var rewrite in new[] { FormattableString.Invariant($"itemId={_item}&mediaSourceId={_item:N}"), FormattableString.Invariant($"itemId={_item}"), FormattableString.Invariant($"mediaSourceId={_item:N}") })
        {
            var rewritten = await SendAsync(anonymous, "GET", own + "?" + rewrite + "&" + credential, string.Empty);

            Assert.False(Carries(rewritten.Body, Encoding.UTF8.GetBytes(SubtitleText)), rewrite + ": the capability was served the subtitles of an item it was not minted for");
            Assert.Equal(HttpStatusCode.Unauthorized, rewritten.Status);
        }

        // Naming what the route already names changes nothing, with a capability or with a token.
        var same = FormattableString.Invariant($"itemId={_item}&mediaSourceId={_item:N}");
        var item = string.Format(CultureInfo.InvariantCulture, template, _item);
        var itemCapability = await MediaBoundaryFixture.MintWithAsync(_allowed, [PlaybackCapabilityScope.Subtitles], _item, _item.ToString("N", CultureInfo.InvariantCulture), playSession);
        Assert.True(Carries((await SendAsync(anonymous, "GET", item + "?" + same + "&playbackCapability=" + Uri.EscapeDataString(itemCapability.Value), string.Empty)).Body, Encoding.UTF8.GetBytes(SubtitleText)));
        Assert.True(Carries((await SendAsync(_allowed, "GET", own + "?" + same, string.Empty)).Body, Encoding.UTF8.GetBytes(SubtitleText)));
    }

    /// <summary>
    /// A user who may see the item plays it; the right is withdrawn; the same requests are sent again.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
    [Fact(Timeout = 120_000)]
    public async Task RightWithdrawnBetweenTwoRequests_TheSecondIsRefused_ForATokenACapabilityAndARunningTranscode()
    {
        var playSession = "smr1-withdrawn-" + Guid.NewGuid().ToString("N");
        var direct = FormattableString.Invariant($"Videos/{_item}/stream?static=true&mediaSourceId={_item:N}&playSessionId={playSession}");
        var progressive = FormattableString.Invariant($"Videos/{_item}/stream.ts?{Transcode}&mediaSourceId={_item:N}&deviceId={playSession}&playSessionId={playSession}");
        var capability = await MediaBoundaryFixture.MintWithAsync(_second, [PlaybackCapabilityScope.Media], _item, _item.ToString("N", CultureInfo.InvariantCulture), playSession);
        var byCapability = direct + "&playbackCapability=" + Uri.EscapeDataString(capability.Value);
        using var anonymous = _factory.CreateClient();

        var before = await SendAsync(_second, "GET", direct, string.Empty);
        Assert.True(before.Body.AsSpan().SequenceEqual(_itemBytes));
        Assert.True((await SendAsync(anonymous, "GET", byCapability, string.Empty)).Body.AsSpan().SequenceEqual(_itemBytes));

        using var request = Request("GET", progressive);
        using var playing = await _second.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, playing.StatusCode);
        var job = Assert.Single(_started, j => j.PlaySessionId == playSession);
        var requests = job.ActiveRequestCount;

        // A transcode behind the legacy segment route, which resolves a job and never the item.
        var live = FormattableString.Invariant($"Videos/{_item}/live.m3u8?container=ts&{Transcode}&mediaSourceId={_item:N}&deviceId={playSession}-live&playSessionId={playSession}-live");
        var playlist = await SendAsync(_second, "GET", live, string.Empty);
        Assert.Equal(HttpStatusCode.OK, playlist.Status);
        var segment = Encoding.UTF8.GetString(playlist.Body).Split('\n').Select(l => l.Trim()).First(l => l.Length > 0 && !l.StartsWith('#'));
        segment = segment.StartsWith('/') ? segment[1..] : FormattableString.Invariant($"Videos/{_item}/{segment}");
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(_second, "GET", segment, string.Empty)).Status);

        await WithholdAsync(_secondId);

        var legacy = await SendAsync(_second, "GET", segment, string.Empty);
        Assert.False((int)legacy.Status is >= 200 and < 300, FormattableString.Invariant($"legacy segment route answered {(int)legacy.Status} with {legacy.Body.Length} bytes to {segment}"));
        Assert.Equal(HttpStatusCode.NotFound, legacy.Status);
        Assert.NotNull(_transcodeManager.GetTranscodingJob(playSession + "-live"));

        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(_second, "GET", direct, string.Empty)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(_second, "HEAD", direct, string.Empty)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(_second, "GET", direct, "bytes=0-15")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(anonymous, "GET", byCapability, string.Empty)).Status);

        // Its own transcode, still registered, on an output that is still there.
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(_second, "GET", progressive, string.Empty)).Status);
        Assert.Equal(requests, job.ActiveRequestCount);
        Assert.Equal(2, _started.Count(j => j.UserId.Equals(_secondId)));
    }

    /// <summary>
    /// The three routes the fix changed that a bare film does not reach: an attachment, the
    /// trickplay playlist and the legacy audio segment. Each is served to a user who may see the
    /// item, refused to one who may not as an item that does not exist, and refused to the first
    /// once its right is withdrawn.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
    [Fact(Timeout = 120_000)]
    public async Task AttachmentTrickplayPlaylistAndLegacyAudioSegment_AreServedToWhoMaySeeTheItem_AndToNobodyElse()
    {
        await SeedAttachmentAndTrickplayAsync();

        // A transcode of the allowed user's, and a file under one of its segments' names with the
        // extension the audio route asks for: that route selects a job by the segment's name.
        var playSession = "smr1-audio-" + Guid.NewGuid().ToString("N");
        var live = FormattableString.Invariant($"Videos/{_item}/live.m3u8?container=ts&{Transcode}&mediaSourceId={_item:N}&deviceId={playSession}&playSessionId={playSession}");
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(_second, "GET", live, string.Empty)).Status);
        var job = Assert.Single(_started, j => j.PlaySessionId == playSession);
        var segment = Path.GetFileNameWithoutExtension(job.Path!) + "0";
        await File.WriteAllBytesAsync(Path.Combine(Path.GetDirectoryName(job.Path!)!, segment + ".aac"), _mark, TestContext.Current.CancellationToken);

        (string Template, byte[] Content)[] routes =
        [
            ("Videos/{0}/{0:N}/Attachments/" + AttachmentIndex, _mark),
            ("Videos/{0}/Trickplay/" + EndToEndMediaFixtures.Width + "/tiles.m3u8", Encoding.ASCII.GetBytes("#EXT-X-TILES")),
            ("Audio/{0}/hls/" + segment + "/stream.aac", _mark),
        ];
        string Url(string template, Guid item) => string.Format(CultureInfo.InvariantCulture, template, item);

        foreach (var (template, content) in routes)
        {
            var served = await SendAsync(_second, "GET", Url(template, _item), string.Empty);
            Assert.True(served.Status == HttpStatusCode.OK && Carries(served.Body, content), FormattableString.Invariant($"{template}: the allowed user was answered {(int)served.Status}"));

            var refused = await SendAsync(_withheld, "GET", Url(template, _item), string.Empty);
            var unknown = await SendAsync(_withheld, "GET", Url(template, Guid.NewGuid()), string.Empty);
            Assert.Equal(HttpStatusCode.NotFound, refused.Status);
            Assert.Equal(unknown.Status, refused.Status);
            Assert.Equal(unknown.Body.Length, refused.Body.Length);
            Assert.False(Carries(refused.Body, content));
        }

        await WithholdAsync(_secondId);

        foreach (var (template, content) in routes)
        {
            var withdrawn = await SendAsync(_second, "GET", Url(template, _item), string.Empty);
            Assert.True(withdrawn.Status == HttpStatusCode.NotFound, FormattableString.Invariant($"{template}: answered {(int)withdrawn.Status} after the right was withdrawn"));
            Assert.False(Carries(withdrawn.Body, content));
        }

        Assert.NotNull(_transcodeManager.GetTranscodingJob(playSession));
    }

    /// <summary>
    /// SECURITY-MEDIA-R3. The legacy audio segment route alone, and the withdrawal alone: one
    /// user, one url, one transcode that stays registered and one file that stays on disk. Served
    /// before the right is withdrawn, refused after as for an item that does not exist, and served
    /// again once the right is given back - so that the right is the only thing the refusal can
    /// be an answer to.
    /// </summary>
    /// <param name="extension">The extension the route is asked with.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
    [Theory(Timeout = 120_000)]
    [InlineData("aac")]
    [InlineData("mp3")]
    public async Task LegacyAudioSegment_IsRefusedWhileTheRightIsWithdrawn_AndOnlyThen(string extension)
    {
        var playSession = "smr3-audio-" + Guid.NewGuid().ToString("N");
        var live = FormattableString.Invariant($"Videos/{_item}/live.m3u8?container=ts&{Transcode}&mediaSourceId={_item:N}&deviceId={playSession}&playSessionId={playSession}");
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(_second, "GET", live, string.Empty)).Status);
        var job = Assert.Single(_started, j => j.PlaySessionId == playSession);

        // The route selects a job by the segment's name and serves the file of that name.
        var segment = Path.GetFileNameWithoutExtension(job.Path!) + "0";
        var file = Path.Combine(Path.GetDirectoryName(job.Path!)!, segment + "." + extension);
        await File.WriteAllBytesAsync(file, _mark, TestContext.Current.CancellationToken);
        var url = FormattableString.Invariant($"Audio/{_item}/hls/{segment}/stream.{extension}");
        var unknownUrl = FormattableString.Invariant($"Audio/{Guid.NewGuid()}/hls/{segment}/stream.{extension}");

        var before = await SendAsync(_second, "GET", url, string.Empty);
        Assert.Equal(HttpStatusCode.OK, before.Status);
        Assert.True(Carries(before.Body, _mark));

        await WithholdAsync(_secondId);

        var withdrawn = await SendAsync(_second, "GET", url, string.Empty);
        var unknown = await SendAsync(_second, "GET", unknownUrl, string.Empty);
        Assert.Equal(HttpStatusCode.NotFound, withdrawn.Status);
        Assert.False(Carries(withdrawn.Body, _mark));
        Assert.Equal(unknown.Status, withdrawn.Status);
        Assert.Equal(unknown.Body, withdrawn.Body);

        // Nothing else changed: the transcode is the same registered job, its file is still there.
        Assert.Same(job, _transcodeManager.GetTranscodingJob(playSession));
        Assert.True(File.Exists(file));

        await WithholdAsync(_secondId, withheld: false);

        var restored = await SendAsync(_second, "GET", url, string.Empty);
        Assert.Equal(HttpStatusCode.OK, restored.Status);
        Assert.True(Carries(restored.Body, _mark));
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
        await SeedAttachmentAndTrickplayAsync();
        using (var merged = await _admin.PostAsync(FormattableString.Invariant($"Videos/MergeVersions?ids={_own},{_item}"), null, TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, merged.StatusCode);
        }

        string[] templates =
        [
            "Videos/{0}/{1:N}/Attachments/" + AttachmentIndex,
            "Videos/{0}/Trickplay/" + EndToEndMediaFixtures.Width + "/tiles.m3u8?mediaSourceId={1}",
        ];
        foreach (var template in templates)
        {
            string Url(Guid source) => string.Format(CultureInfo.InvariantCulture, template, _own, source);

            var served = await SendAsync(_allowed, "GET", Url(_item), string.Empty);
            Assert.True(served.Status == HttpStatusCode.OK, FormattableString.Invariant($"{template}: the allowed user was answered {(int)served.Status}"));

            var asked = Guid.NewGuid();
            var refused = await SendAsync(_withheld, "GET", Url(_item), string.Empty);
            var unknown = await SendAsync(_withheld, "GET", Url(asked), string.Empty);

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
            var playSession = "smr1-both-" + Guid.NewGuid().ToString("N");
            var direct = await SendAsync(client, "GET", FormattableString.Invariant($"Videos/{_item}/stream?static=true&mediaSourceId={_item:N}"), string.Empty);
            Assert.True(direct.Body.AsSpan().SequenceEqual(_itemBytes));

            var ranged = await SendAsync(client, "GET", FormattableString.Invariant($"Videos/{_item}/stream?static=true&mediaSourceId={_item:N}"), "bytes=16-31");
            Assert.Equal(HttpStatusCode.PartialContent, ranged.Status);
            Assert.True(ranged.Body.AsSpan().SequenceEqual(_itemBytes.AsSpan(16, 16)));

            var transcoded = await SendAsync(client, "GET", FormattableString.Invariant($"Videos/{_item}/stream.ts?{Transcode}&mediaSourceId={_item:N}&deviceId={playSession}&playSessionId={playSession}"), string.Empty);
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

        var served = await SendAsync(anonymous, "GET", FormattableString.Invariant($"Videos/{_item}/stream?static=true&mediaSourceId={_item:N}&ApiKey={key}"), string.Empty);

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

    private static async Task<(HttpStatusCode Status, byte[] Body)> SendAsync(HttpClient client, string method, string url, string range)
    {
        using var request = Request(method, url);
        if (range.Length > 0)
        {
            request.Headers.Range = RangeHeaderValue.Parse(range);
        }

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        return (response.StatusCode, await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
    }

    private static async Task EventuallyAsync(Func<bool> condition, string otherwise)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, otherwise);
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
    }

    private void OnStarted(object? sender, TranscodingJob job) => _started.Enqueue(job);

    /// <summary>
    /// Gives the withheld item an attachment, already extracted, and a trickplay resolution.
    /// </summary>
    private async Task SeedAttachmentAndTrickplayAsync()
    {
        const string FileName = "smr1.ttf";
        _factory.Services.GetRequiredService<IMediaAttachmentRepository>().SaveMediaAttachments(
            _item,
            [new MediaAttachment { Index = AttachmentIndex, Codec = "ttf", FileName = FileName, MimeType = "font/ttf" }],
            TestContext.Current.CancellationToken);
        var extracted = _factory.Services.GetRequiredService<IPathManager>().GetAttachmentPath(_item.ToString("N", CultureInfo.InvariantCulture), FileName)!;
        Directory.CreateDirectory(Path.GetDirectoryName(extracted)!);
        await File.WriteAllBytesAsync(extracted, _mark, TestContext.Current.CancellationToken);

        await _factory.Services.GetRequiredService<ITrickplayManager>().SaveTrickplayInfo(new TrickplayInfo
        {
            ItemId = _item,
            Width = EndToEndMediaFixtures.Width,
            Height = EndToEndMediaFixtures.Height,
            TileWidth = 1,
            TileHeight = 1,
            ThumbnailCount = 1,
            Interval = 1000,
            Bandwidth = 1,
        });
    }

    /// <summary>
    /// Blocks the withheld tag for a user, or stops blocking it: its policy as it is, with that
    /// one field changed.
    /// </summary>
    private async Task WithholdAsync(Guid userId, bool withheld = true)
    {
        var user = (await _admin.GetFromJsonAsync<UserDto>(FormattableString.Invariant($"Users/{userId}"), JsonDefaults.Options, TestContext.Current.CancellationToken))!;
        user.Policy!.BlockedTags = withheld ? [WithheldTag] : [];
        using var applied = await _admin.PostAsJsonAsync(FormattableString.Invariant($"Users/{userId}/Policy"), user.Policy, JsonDefaults.Options, TestContext.Current.CancellationToken);
        applied.EnsureSuccessStatusCode();
    }

    private async Task<string> CapabilityUrlAsync(HttpClient client, string playSession)
    {
        var capability = await MediaBoundaryFixture.MintWithAsync(client, [PlaybackCapabilityScope.Media], _item, _item.ToString("N", CultureInfo.InvariantCulture), playSession);
        return FormattableString.Invariant($"Videos/{_item}/stream?static=true&mediaSourceId={_item:N}&playSessionId={playSession}&playbackCapability={Uri.EscapeDataString(capability.Value)}");
    }

    private async Task<Guid> CreatePlaybackSessionAsync(HttpClient client, Guid userId, string playSession)
    {
        var (capabilities, constraints) = EndToEndCapabilityPresets.Remux();
        var body = new CreatePlaybackSessionRequest(_item, userId, capabilities, constraints, MediaSourceId: null, PlaySessionId: playSession);
        using var response = await client.PostAsJsonAsync("Playback/Sessions", body, JsonDefaults.Options, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PlaybackSessionResponse>(JsonDefaults.Options, TestContext.Current.CancellationToken))!.Id;
    }

    private async Task EndAsync(HttpClient client, Guid session, string playSession, bool byStopReport)
    {
        using var response = byStopReport
            ? await client.PostAsJsonAsync("Sessions/Playing/Stopped", new { ItemId = _item, PlaySessionId = playSession, PositionTicks = 0L }, JsonDefaults.Options, TestContext.Current.CancellationToken)
            : await client.DeleteAsync(FormattableString.Invariant($"Playback/Sessions/{session}"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private async Task<HttpClient> SignInAgainAsync(string name, string device)
    {
        using var anonymous = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/Users/AuthenticateByName");
        request.Headers.TryAddWithoutValidation(AuthHelper.AuthHeaderName, MediaBoundaryFixture.AuthorizationHeader(device, null));
        request.Content = JsonContent.Create(new { Username = name, Pw = "smr1-password" }, options: JsonDefaults.Options);
        using var authenticated = await anonymous.SendAsync(request, TestContext.Current.CancellationToken);
        authenticated.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await authenticated.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            AuthHelper.AuthHeaderName,
            MediaBoundaryFixture.AuthorizationHeader(device, document.RootElement.GetProperty("AccessToken").GetString()));
        return client;
    }

    private async Task<(HttpClient Client, Guid UserId)> SignInAsync(string name, bool withheld)
    {
        const string Password = "smr1-password";
        UserDto created;
        using (var response = await _admin.PostAsJsonAsync("/Users/New", new { Name = name, Password }, JsonDefaults.Options, TestContext.Current.CancellationToken))
        {
            response.EnsureSuccessStatusCode();
            created = (await response.Content.ReadFromJsonAsync<UserDto>(JsonDefaults.Options, TestContext.Current.CancellationToken))!;
        }

        if (withheld)
        {
            await WithholdAsync(created.Id);
        }

        using var anonymous = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/Users/AuthenticateByName");
        request.Headers.TryAddWithoutValidation(AuthHelper.AuthHeaderName, MediaBoundaryFixture.AuthorizationHeader(name + "-device", null));
        request.Content = JsonContent.Create(new { Username = name, Pw = Password }, options: JsonDefaults.Options);
        using var authenticated = await anonymous.SendAsync(request, TestContext.Current.CancellationToken);
        authenticated.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await authenticated.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        var client = _factory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(90);
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            AuthHelper.AuthHeaderName,
            MediaBoundaryFixture.AuthorizationHeader(name + "-device", document.RootElement.GetProperty("AccessToken").GetString()));
        return (client, created.Id);
    }
}
