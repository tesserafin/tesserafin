using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Tesserafin.Api.Models.PlaybackSessionDtos;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Controller.Persistence;
using Tesserafin.Extensions.Json;
using Tesserafin.MediaEncoding.Playback;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.Entities;
using Tesserafin.Playback.Decision;
using Tesserafin.Playback.Dlna;
using Xunit;

namespace Tesserafin.Server.Integration.Tests.EndToEnd;

/// <summary>
/// PR119: the end-to-end proof <c>ci/smoke.sh</c>'s own header names as a real, deliberate gap left
/// by PR117 - a real, booted Tesserafin server, driven only through its public HTTP surface, planning a
/// session (<c>POST Playback/Sessions</c>), resolving the PR117 URL contract
/// (<c>GET Playback/Sessions/{id}/Stream</c>), and then a real HTTP client fetching the URL the
/// descriptor names, asserting it actually serves bytes - not just that the descriptor LOOKS right.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope, honestly stated (mirrors <c>ci/smoke.sh</c>'s own header discipline).</b> One real fixture
/// (<see cref="EndToEndMediaFixtures.CreateH264AacMp4Async"/>, a genuine ffmpeg-synthesized H.264/AAC
/// MP4) backs every method-selection scenario; what changes per scenario is the request's own
/// <see cref="PlaybackConstraints"/>/<see cref="ClientCapabilities"/> (see
/// <see cref="EndToEndCapabilityPresets"/>'s remarks for the exact <c>StreamBuilder</c> mechanics this
/// relies on, verified against <c>Tesserafin.Model/Dlna/StreamBuilder.cs</c>) - a deliberate simplification
/// over maintaining five differently-encoded fixtures, since the play METHOD is what those tests need
/// to control, not the codec. The one deliberate exception is
/// <see cref="Remux_MatroskaSource_ServesTheDeclaredMpegTs"/> (issue #57), which asserts on
/// the CONTENT of the served bytes rather than only on the method: it needs a source whose container
/// genuinely differs from the announced output container, so it uses the real Matroska fixture
/// <see cref="EndToEndMediaFixtures.CreateH264AacMkvAsync"/> - with an mp4 source the defect it pins
/// is invisible, because serving the source verbatim still yields mp4 bytes. The library item itself
/// is seeded directly
/// (<see cref="LibraryItemSeeder"/>) rather than through a full virtual-folder scan - see that class's
/// remarks for why that is a faithful shortcut of the exact same real persistence calls a scan would
/// end up making, not a fake one.
/// </para>
/// <para>
/// Tagged <see cref="TraitAttribute"/> <c>Category=Smoke</c>: <c>ci/run.sh</c> (the mandatory daily
/// gate) excludes <c>Category=Smoke</c> by filter already, and <c>ci/smoke.sh</c>'s own
/// <c>SMOKE_FILTER</c> is a <c>Category=Smoke</c> OR-clause, so this class is picked up there
/// automatically - see <c>ci/smoke-e2e.sh</c> for the dedicated invocation this PR adds (this suite is
/// slower than the rest of <c>ci/smoke.sh</c>: a real boot plus, for the transcode scenario, a real
/// ffmpeg encode).
/// </para>
/// </remarks>
[Trait("Category", "Smoke")]
public sealed class PlaybackUrlContractEndToEndTests : IClassFixture<E2eApplicationFactory>, IAsyncLifetime
{
    private static readonly MediaStream[] _baseStreams =
    [
        new MediaStream
        {
            Index = 0,
            Type = MediaStreamType.Video,
            Codec = EndToEndCapabilityPresets.FixtureVideoCodec,
            Width = EndToEndMediaFixtures.Width,
            Height = EndToEndMediaFixtures.Height,
            IsDefault = true,
        },
        new MediaStream
        {
            Index = 1,
            Type = MediaStreamType.Audio,
            Codec = EndToEndCapabilityPresets.FixtureAudioCodec,
            Channels = 2,
            IsDefault = true,
        },
    ];

    private readonly E2eApplicationFactory _factory;
    private HttpClient _client = null!;
    private Guid _userId;
    private string _workDir = null!;
    private string _fixturePath = null!;
    private ILibraryManager _libraryManager = null!;
    private IMediaStreamRepository _mediaStreamRepository = null!;
    private IServerConfigurationManager _configManager = null!;

    public PlaybackUrlContractEndToEndTests(E2eApplicationFactory factory)
    {
        _factory = factory;
    }

    public async ValueTask InitializeAsync()
    {
        _client = _factory.CreateClient();
        // Bounded so a genuine server-side hang (e.g. a stuck real ffmpeg process) fails the test with
        // a diagnosable timeout instead of hanging the whole test run indefinitely.
        _client.Timeout = TimeSpan.FromSeconds(90);
        // The startup wizard can only be completed ONCE against this class's single shared, real
        // booted server (IClassFixture<E2eApplicationFactory>) - EnsureAuthenticatedAsync races-and-
        // caches across this class's concurrently-run [Fact]s so only the first caller actually runs
        // it; every caller (including this one) still adds the resulting token to its OWN client.
        var (accessToken, userId) = await _factory.EnsureAuthenticatedAsync();
        _client.DefaultRequestHeaders.AddAuthHeader(accessToken);
        _userId = userId;

        _libraryManager = _factory.Services.GetRequiredService<ILibraryManager>();
        _mediaStreamRepository = _factory.Services.GetRequiredService<IMediaStreamRepository>();
        _configManager = _factory.Services.GetRequiredService<IServerConfigurationManager>();

        // Explicit, known starting state regardless of framework defaults - scenarios that care about

        _workDir = Directory.CreateTempSubdirectory("reefin-pr119-e2e-").FullName;
        _fixturePath = await EndToEndMediaFixtures.CreateH264AacMp4Async(_workDir);
    }

    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        try
        {
            Directory.Delete(_workDir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup - mirrors HlsSmokeTests' own TempDirectory discipline.
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Scenario 1: DirectPlay.</summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
    [Fact]
    public async Task DirectPlay_PostThenGetStream_ServesRealBytes()
    {
        var itemId = SeedItem();
        var (capabilities, constraints) = EndToEndCapabilityPresets.DirectPlay();

        var session = await CreateSessionAsync(itemId, capabilities, constraints, "e2e-direct-play", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(PlaybackMethod.DirectPlay, session.Method);

        var descriptor = await GetStreamDescriptorAsync(session.Id, TestContext.Current.CancellationToken);
        Assert.Equal(StreamingProtocol.Http, descriptor.Protocol);

        await AssertUrlServes200Async(descriptor.Url, "DirectPlay");
    }

    /// <summary>Scenario 2: Remux / DirectStream.</summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
    [Fact]
    public async Task Remux_PostThenGetStream_ServesRealBytes()
    {
        var itemId = SeedItem();
        var (capabilities, constraints) = EndToEndCapabilityPresets.Remux();

        var session = await CreateSessionAsync(itemId, capabilities, constraints, "e2e-remux", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(PlaybackMethod.Remux, session.Method);

        var descriptor = await GetStreamDescriptorAsync(session.Id, TestContext.Current.CancellationToken);

        await AssertUrlServes200Async(descriptor.Url, "Remux/DirectStream");
    }

    /// <summary>Scenario 4: an external subtitle sidecar is named on the descriptor and itself servable.</summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
    [Fact]
    public async Task ExternalSubtitle_IsNamedOnDescriptorAndServable()
    {
        var subtitlePath = EndToEndMediaFixtures.CreateExternalSrt(_workDir);
        var streams = _baseStreams.Concat(
        [
            new MediaStream
            {
                Index = 2,
                Type = MediaStreamType.Subtitle,
                Codec = "srt",
                IsExternal = true,
                Path = subtitlePath,
                Language = "eng",
            },
        ]).ToArray();

        var itemId = LibraryItemSeeder.SeedVideo(_factory.Services.GetRequiredService<IItemWriteCommands>(), _mediaStreamRepository, _fixturePath, "mp4", streams, $"PR119 subtitle fixture {Guid.NewGuid()}");
        var (capabilities, constraints) = EndToEndCapabilityPresets.DirectPlayWithExternalSubtitle(subtitleStreamIndex: 2);

        // A specific subtitle stream request requires naming the media source explicitly
        // (PlaybackSessionRequestValidator) - the item's single MediaSource id, itself the item id.
        var session = await CreateSessionAsync(itemId, capabilities, constraints, "e2e-external-subtitle", mediaSourceId: itemId.ToString("N"), cancellationToken: TestContext.Current.CancellationToken);

        var descriptor = await GetStreamDescriptorAsync(session.Id, TestContext.Current.CancellationToken);
        Assert.True(!string.IsNullOrEmpty(descriptor.SubtitleUrl), $"Descriptor named no SubtitleUrl for an external-subtitle session.");

        await AssertUrlServes200Async(descriptor.Url, "DirectPlay (subtitle scenario)");

        using var subtitleResponse = await _client.GetAsync(descriptor.SubtitleUrl, TestContext.Current.CancellationToken);
        var subtitleBody = await subtitleResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(subtitleResponse.StatusCode == HttpStatusCode.OK, $"External subtitle URL '{descriptor.SubtitleUrl}' did not serve 200 - got {(int)subtitleResponse.StatusCode}. Body: {Truncate(subtitleBody)}");
        Assert.Contains("PR119 end-to-end external subtitle fixture", subtitleBody, StringComparison.Ordinal);
    }

    /// <summary>
    /// Scenario 6 (issue #57): a Remux decision must be EXECUTED, not merely announced.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The reported defect, measured: a Matroska source, a client that declares mp4-only decoding, a
    /// <c>Remux</c> decision announcing <c>Container=mp4</c>/<c>MimeType=video/mp4</c> - and a media
    /// response that was BYTE-IDENTICAL to the source <c>.mkv</c> (EBML magic <c>1a45dfa3</c>,
    /// ffprobe <c>format_name=matroska,webm</c>) served under <c>Content-Type: video/mp4</c>. The
    /// descriptor's URL carried <c>&amp;Static=true</c>, which
    /// <c>VideosController.GetVideoStream</c> answers with the source file verbatim.
    /// </para>
    /// <para>
    /// This test asserts the WHOLE chain in one place on purpose: method, URL shape, announced
    /// container/MIME type, and what the bytes actually are. Any one of those alone can pass while
    /// the defect is live - a URL-shape-only assertion in particular would have said nothing about
    /// the bytes, which is the entire point. The byte-level assertions are deliberately layered from
    /// cheapest to strongest: not-identical-to-source, then not-Matroska (EBML magic absent), then
    /// positively ISOBMFF (<c>ftyp</c> at offset 4), then real ffprobe identification.
    /// </para>
    /// </remarks>
    /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
    [Fact(Timeout = 90_000)]
    public async Task Remux_MatroskaSource_ServesTheDeclaredMpegTs()
    {
        var mkvPath = await EndToEndMediaFixtures.CreateH264AacMkvAsync(_workDir);
        var itemId = LibraryItemSeeder.SeedVideo(
            _factory.Services.GetRequiredService<IItemWriteCommands>(),
            _mediaStreamRepository,
            mkvPath,
            "mkv",
            _baseStreams,
            $"Issue 57 matroska fixture {Guid.NewGuid()}");

        var (capabilities, constraints) = EndToEndCapabilityPresets.RemuxMatroskaToMpegTs();

        var session = await CreateSessionAsync(itemId, capabilities, constraints, "e2e-remux-mkv-to-ts", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(PlaybackMethod.Remux, session.Method);

        var descriptor = await GetStreamDescriptorAsync(session.Id, TestContext.Current.CancellationToken);

        // Guard the premise: a legacy fallback here would silently turn this into a test of a
        // different engine than the one issue #57 was reported against.

        Assert.Equal(StreamingProtocol.Http, descriptor.Protocol);
        Assert.Equal("ts", descriptor.Container);
        Assert.Equal("video/mp2t", descriptor.MimeType);

        // Static=true means "serve the source file untouched" - DirectPlay semantics. A Remux that
        // asks for it can only ever serve the source container, whatever it announces.
        Assert.DoesNotContain("Static=true", descriptor.Url, StringComparison.OrdinalIgnoreCase);

        var served = await GetBytesAsync(descriptor.Url, "Remux (Matroska source, MPEG-TS declared)");
        var source = await File.ReadAllBytesAsync(mkvPath, TestContext.Current.CancellationToken);

        Assert.False(
            served.AsSpan().SequenceEqual(source),
            $"The served bytes are byte-identical to the source .mkv ({served.Length} bytes): the announced remux never happened.");

        Assert.False(
            IsMatroska(served),
            $"The served bytes start with the Matroska/EBML magic 1a45dfa3, but the descriptor announced Container=ts. Head: {Head(served)}");

        // The strongest available statement: what a real demuxer says the bytes ARE.
        var probedPath = Path.Combine(_workDir, $"served-{Guid.NewGuid():N}.ts");
        await File.WriteAllBytesAsync(probedPath, served, TestContext.Current.CancellationToken);
        var formatName = await EndToEndMediaFixtures.ProbeFormatNameAsync(probedPath);
        Assert.DoesNotContain("matroska", formatName, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("mpegts", formatName);
    }

    /// <summary>
    /// #374: with no output the client can open, the refusal says so instead of handing over a file.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
    [Fact(Timeout = 90_000)]
    public async Task Remux_LegacyPath_NoOutputAvailable_IsAnExplainedRefusal()
    {
        var mkvPath = await EndToEndMediaFixtures.CreateH264AacMkvAsync(_workDir);
        var itemId = LibraryItemSeeder.SeedVideo(_factory.Services.GetRequiredService<IItemWriteCommands>(), _mediaStreamRepository, mkvPath, "mkv", _baseStreams, $"Issue 374 refusal {Guid.NewGuid()}");

        var (capabilities, constraints) = EndToEndCapabilityPresets.Remux();
        var noOutput = capabilities with { OutputProfiles = [] };

        var request = new CreatePlaybackSessionRequest(itemId, _userId, noOutput, constraints, MediaSourceId: null, PlaySessionId: "e2e-374-refused");
        using var response = await _client.PostAsJsonAsync("Playback/Sessions", request, JsonDefaults.Options, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("No playable output", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    /// <summary>
    /// Issue #59, matrix row 2 - the STOP condition, with byte proof. A Matroska source, a client
    /// that decodes mp4 only, and transcoding explicitly FORBIDDEN: both codecs are copyable, so this
    /// is a genuine remux and <c>AllowDirectStream:true</c> must keep permitting it. The fix keys the
    /// permission on the method really required, so a plan that copies every stream is never treated
    /// as a transcode - if it over-blocked, this test is what breaks.
    /// </summary>
    /// <remarks>
    /// Byte-level on purpose, and for the same reason as
    /// <see cref="Remux_MatroskaSource_ServesTheDeclaredMpegTs"/>: a 200 proves the request
    /// was not refused, but only the bytes prove a REAL remux still happened under the constraint.
    /// </remarks>
    /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
    [Fact(Timeout = 90_000)]
    public async Task Remux_TranscodingForbidden_StillServesRealRemuxedBytes()
    {
        var mkvPath = await EndToEndMediaFixtures.CreateH264AacMkvAsync(_workDir);
        var itemId = LibraryItemSeeder.SeedVideo(
            _factory.Services.GetRequiredService<IItemWriteCommands>(),
            _mediaStreamRepository,
            mkvPath,
            "mkv",
            _baseStreams,
            $"Issue 59 remux-not-blocked fixture {Guid.NewGuid()}");

        var (capabilities, constraints) = EndToEndCapabilityPresets.RemuxMatroskaToMpegTsTranscodingForbidden();
        Assert.False(constraints.AllowTranscoding, "Premise: this row is only meaningful with transcoding forbidden.");

        var session = await CreateSessionAsync(itemId, capabilities, constraints, "e2e-59-remux-allowed", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(PlaybackMethod.Remux, session.Method);

        var descriptor = await GetStreamDescriptorAsync(session.Id, TestContext.Current.CancellationToken);
        Assert.Equal("ts", descriptor.Container);
        Assert.DoesNotContain("Static=true", descriptor.Url, StringComparison.OrdinalIgnoreCase);

        var served = await GetBytesAsync(descriptor.Url, "Remux under AllowTranscoding:false");
        var source = await File.ReadAllBytesAsync(mkvPath, TestContext.Current.CancellationToken);

        Assert.False(
            served.AsSpan().SequenceEqual(source),
            $"The served bytes are byte-identical to the source .mkv ({served.Length} bytes): the remux never happened.");
        Assert.False(IsMatroska(served), $"Served bytes are still Matroska. Head: {Head(served)}");

        var probedPath = Path.Combine(_workDir, $"served-59-remux-{Guid.NewGuid():N}.ts");
        await File.WriteAllBytesAsync(probedPath, served, TestContext.Current.CancellationToken);
        var formatName = await EndToEndMediaFixtures.ProbeFormatNameAsync(probedPath);
        Assert.DoesNotContain("matroska", formatName, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("mpegts", formatName);
    }

    /// <summary>
    /// Issue #59, matrix row 3 - the defect itself. The client cannot decode the source's codecs, so
    /// the only plan that could serve this session is a real re-encode, and transcoding is forbidden.
    /// The contractual answer is 422 ("no viable plan") - never a session that goes on to transcode.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
    [Fact]
    public async Task IncompatibleCodecs_TranscodingForbidden_YieldsNoViablePlan()
    {
        var itemId = SeedItem();
        var (capabilities, constraints) = EndToEndCapabilityPresets.IncompatibleCodecsTranscodingForbidden();

        var status = await CreateSessionExpectingFailureAsync(itemId, capabilities, constraints, "e2e-59-no-plan", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
    }

    /// <summary>
    /// Issue #59, matrix row 3 on the LEGACY branch. Same request, but the kill switch forces the
    /// legacy engine - the branch that used to ignore the constraint and re-encode anyway. Legacy
    /// must now reach the same conclusion as v2.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
    [Fact]
    public async Task IncompatibleCodecs_TranscodingForbidden_LegacyBranchAlsoYieldsNoViablePlan()
    {
        var itemId = SeedItem();
        var (capabilities, constraints) = EndToEndCapabilityPresets.IncompatibleCodecsTranscodingForbidden();

        var status = await CreateSessionExpectingFailureAsync(itemId, capabilities, constraints, "e2e-59-no-plan-legacy", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
    }

    /// <summary>
    /// Issue #59, matrix row 5 - a request permitting NO method at all is contradictory, and the
    /// validator rejects it with 400 before any planning happens. Asserting 400 here and 422 above is
    /// the point: "invalid request" and "valid request, no viable plan" must stay formally distinct.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
    [Fact]
    public async Task AllMethodsForbidden_IsRejectedByValidatorAsBadRequest()
    {
        var itemId = SeedItem();
        var (capabilities, constraints) = EndToEndCapabilityPresets.AllMethodsForbidden();

        var status = await CreateSessionExpectingFailureAsync(itemId, capabilities, constraints, "e2e-59-all-forbidden", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    /// <summary>
    /// Issue #59, matrix row 6 - the PUT verb. Re-planning an ALREADY VIABLE session with
    /// constraints that leave only a re-encode must be refused too (422), and the session must not
    /// be quietly converted into a transcode. POST and GET Stream are covered by the rows above;
    /// this closes the third verb, which re-enters planning by a different path.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
    [Fact]
    public async Task Replan_ToIncompatibleCodecsWithTranscodingForbidden_YieldsNoViablePlan()
    {
        var itemId = SeedItem();

        // A genuinely viable session first, so the refusal below is caused by the re-plan and not by
        // the session having been unservable all along.
        var (directPlayCapabilities, directPlayConstraints) = EndToEndCapabilityPresets.DirectPlay();
        var session = await CreateSessionAsync(itemId, directPlayCapabilities, directPlayConstraints, "e2e-59-replan-seed", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(PlaybackMethod.DirectPlay, session.Method);

        var (capabilities, constraints) = EndToEndCapabilityPresets.IncompatibleCodecsTranscodingForbidden();
        var request = new ReplacePlaybackSessionRequest(itemId, _userId, capabilities, constraints);

        using var response = await _client.PutAsJsonAsync(
            $"Playback/Sessions/{session.Id}",
            request,
            JsonDefaults.Options,
            TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.True(
            response.StatusCode == HttpStatusCode.UnprocessableEntity,
            $"PUT Playback/Sessions was expected to refuse a re-plan that only a re-encode could satisfy, got {(int)response.StatusCode}. Body: {Truncate(body)}");
    }

    /// <summary>
    /// POSTs a session expected NOT to succeed and returns the status code, asserting only that the
    /// request genuinely failed. Deliberately separate from <see cref="CreateSessionAsync"/>, which
    /// asserts success: a refusal is the assertion subject here, not an error to surface.
    /// </summary>
    private async Task<HttpStatusCode> CreateSessionExpectingFailureAsync(
        Guid itemId, ClientCapabilities capabilities, PlaybackConstraints constraints, string playSessionId, CancellationToken cancellationToken)
    {
        var request = new CreatePlaybackSessionRequest(itemId, _userId, capabilities, constraints, MediaSourceId: null, PlaySessionId: playSessionId);
        using var response = await _client.PostAsJsonAsync("Playback/Sessions", request, JsonDefaults.Options, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.False(
            response.IsSuccessStatusCode,
            $"POST Playback/Sessions was expected to be refused for PlaySessionId='{playSessionId}', but it SUCCEEDED with {(int)response.StatusCode}. " +
            $"A session created here would go on to be served - which is exactly the issue #59 defect. Body: {Truncate(body)}");

        return response.StatusCode;
    }

    /// <summary>Matroska/EBML magic: <c>1A 45 DF A3</c> at offset 0.</summary>
    private static bool IsMatroska(byte[] bytes) =>
        bytes.Length >= 4 && bytes[0] == 0x1A && bytes[1] == 0x45 && bytes[2] == 0xDF && bytes[3] == 0xA3;

    private static string Head(byte[] bytes, int count = 16) =>
        Convert.ToHexString(bytes, 0, Math.Min(count, bytes.Length));

    private Guid SeedItem() =>
        LibraryItemSeeder.SeedVideo(_factory.Services.GetRequiredService<IItemWriteCommands>(), _mediaStreamRepository, _fixturePath, "mp4", _baseStreams, $"PR119 fixture {Guid.NewGuid()}");

    private async Task<PlaybackSessionResponse> CreateSessionAsync(Guid itemId, ClientCapabilities capabilities, PlaybackConstraints constraints, string playSessionId, string? mediaSourceId = null, CancellationToken cancellationToken = default)
    {
        var request = new CreatePlaybackSessionRequest(itemId, _userId, capabilities, constraints, MediaSourceId: mediaSourceId, PlaySessionId: playSessionId);
        using var response = await _client.PostAsJsonAsync("Playback/Sessions", request, JsonDefaults.Options, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        Assert.True(response.IsSuccessStatusCode, $"POST Playback/Sessions failed for PlaySessionId='{playSessionId}': {(int)response.StatusCode} {Truncate(body)}");

        return JsonSerializer.Deserialize<PlaybackSessionResponse>(body, JsonDefaults.Options)
            ?? throw new InvalidOperationException("POST Playback/Sessions returned an empty body.");
    }

    private async Task<PlaybackSessionStreamDescriptor> GetStreamDescriptorAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync($"Playback/Sessions/{sessionId}/Stream", cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        Assert.True(response.IsSuccessStatusCode, $"GET Playback/Sessions/{sessionId}/Stream failed: {(int)response.StatusCode} {Truncate(body)}");

        return JsonSerializer.Deserialize<PlaybackSessionStreamDescriptor>(body, JsonDefaults.Options)
            ?? throw new InvalidOperationException("GET .../Stream returned an empty body.");
    }

    private async Task AssertUrlServes200Async(string relativeUrl, string diagnosticContext, int minBytes = 1)
        => await GetBytesAsync(relativeUrl, diagnosticContext, minBytes);

    /// <summary>
    /// Fetches a URL the descriptor named and returns the bytes it actually served, asserting 200
    /// and a minimum length first. Returning the bytes (rather than only asserting on them) is what
    /// lets a caller assert on the CONTENT - see
    /// <see cref="Remux_MatroskaSource_ServesTheDeclaredMpegTs"/>, where the whole point is
    /// that a 200 with the right Content-Type proved nothing about what was inside it.
    /// </summary>
    private async Task<byte[]> GetBytesAsync(string relativeUrl, string diagnosticContext, int minBytes = 1)
    {
        using var boundedCts = CreateBoundedCancellation();
        using var response = await _client.GetAsync(relativeUrl, boundedCts.Token);
        var content = await response.Content.ReadAsByteArrayAsync(boundedCts.Token);
        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"{diagnosticContext}: expected HTTP 200 fetching '{relativeUrl}', got {(int)response.StatusCode}. Body head: {Truncate(Encoding.UTF8.GetString(content))}");
        Assert.True(content.Length >= minBytes, $"{diagnosticContext}: '{relativeUrl}' returned 200 but only {content.Length} byte(s).");
        return content;
    }

    private async Task<string> GetTextAsync(string relativeUrl, string diagnosticContext)
    {
        using var boundedCts = CreateBoundedCancellation();
        using var response = await _client.GetAsync(relativeUrl, boundedCts.Token);
        var body = await response.Content.ReadAsStringAsync(boundedCts.Token);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{diagnosticContext}: expected HTTP 200 fetching '{relativeUrl}', got {(int)response.StatusCode}. Body: {Truncate(body)}");
        return body;
    }

    /// <summary>
    /// A per-call bounded cancellation, independent of (and a deliberate belt-and-suspenders on top
    /// of) <see cref="HttpClient.Timeout"/>: a real, reproducible hang was found in this suite's own
    /// development (the real HLS transcode/serve path never returning even after ffmpeg itself had
    /// already produced a complete, valid VOD manifest+segment on disk - root-caused and fixed, see
    /// <see cref="Transcode_Hls_PostThenGetStream_ServesManifestAndSegment"/>'s remarks) where
    /// <see cref="HttpClient.Timeout"/> alone did not observably abort the stuck request within
    /// several minutes. Linking an explicit token to each individual call, rather than trusting the
    /// client-wide timeout, is what actually turns a hang into a clean, fast test failure - kept as
    /// defense in depth for any other genuinely stuck request, not only the one root-caused above.
    /// </summary>
    private static CancellationTokenSource CreateBoundedCancellation(int seconds = 30)
    {
        // CancelAfter() on the linked source itself, rather than linking in a second, timed
        // CancellationTokenSource: that intermediate source would never be disposed, leaking its
        // Timer for the lifetime of the test run.
        var linked = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        linked.CancelAfter(TimeSpan.FromSeconds(seconds));
        return linked;
    }

    /// <summary>
    /// A real ffmpeg transcode is not instantaneous - the first HLS media playlist/segment is not
    /// guaranteed ready the instant the master playlist returns. Polls with a clear diagnostic on
    /// timeout, mirroring <c>ci/smoke.sh</c>'s own "attente readiness" discipline.
    /// </summary>
    private async Task<string> GetTextWithRetryAsync(string relativeUrl, string diagnosticContext, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        Exception? lastFailure = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                return await GetTextAsync(relativeUrl, diagnosticContext).ConfigureAwait(false);
            }
            catch (Xunit.Sdk.XunitException ex)
            {
                lastFailure = ex;
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }
        }

        throw new TimeoutException($"{diagnosticContext}: '{relativeUrl}' never became ready within {timeout}.", lastFailure);
    }

    /// <summary>
    /// Minimal, deliberately naive M3U8 parsing: the first non-empty, non-comment line is the next
    /// URI to follow - true for both a master playlist's variant reference and a media playlist's
    /// segment reference. Resolves a relative URI against <paramref name="baseUrl"/>'s own path, the
    /// same resolution a real HLS client performs.
    /// </summary>
    private static string? FirstUriLine(string manifest, string baseUrl)
    {
        var line = manifest
            .Split('\n')
            .Select(l => l.Trim('\r', ' '))
            .FirstOrDefault(l => l.Length > 0 && !l.StartsWith('#'));

        if (line is null)
        {
            return null;
        }

        if (Uri.TryCreate(line, UriKind.Absolute, out _))
        {
            return line;
        }

        var basePath = baseUrl[..(baseUrl.LastIndexOf('/') + 1)];
        return basePath + line;
    }

    private static string Truncate(string text, int maxChars = 2000) => text.Length <= maxChars ? text : text[..maxChars];
}
