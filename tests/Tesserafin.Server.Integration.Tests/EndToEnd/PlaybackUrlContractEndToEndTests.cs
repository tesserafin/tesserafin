using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Tesserafin.Api.Models.PlaybackSessionDtos;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Persistence;
using Tesserafin.Extensions.Json;
using Tesserafin.MediaEncoding.Playback;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.Entities;
using Tesserafin.Playback.Decision;
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
/// <see cref="Remux_MatroskaSourceAnnouncedAsMp4_ServesRealMp4Bytes"/> (issue #57), which asserts on
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
        // the kill switch set this themselves.
        _configManager.Configuration.PlaybackShadow.Mode = PlaybackEngineMode.Legacy;

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

        var session = await CreateSessionAsync(itemId, capabilities, constraints, "e2e-direct-play");
        Assert.Equal(PlaybackMethod.DirectPlay, session.Method);

        var descriptor = await GetStreamDescriptorAsync(session.Id);
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

        var session = await CreateSessionAsync(itemId, capabilities, constraints, "e2e-remux");
        Assert.Equal(PlaybackMethod.Remux, session.Method);

        var descriptor = await GetStreamDescriptorAsync(session.Id);

        await AssertUrlServes200Async(descriptor.Url, "Remux/DirectStream");
    }

    /// <summary>
    /// Scenario 3: Transcode to HLS - manifest AND at least one real segment.
    /// </summary>
    /// <remarks>
    /// <b>Root-caused and fixed, not papered over.</b> This scenario used to hang past both its own
    /// <see cref="FactAttribute.Timeout"/> and <see cref="CreateBoundedCancellation"/>'s per-call
    /// bound: the real ffmpeg process completes in well under a second and writes a complete, valid
    /// VOD <c>.m3u8</c> (<c>#EXT-X-ENDLIST</c>) plus its <c>.ts</c> segment to disk, but the segment
    /// HTTP request never returned. Root cause, confirmed by instrumenting
    /// <c>DynamicHlsController.GetSegmentResult</c>'s readiness-wait loop directly: <c>Process.Exited</c>
    /// fires <c>TranscodeManager.OnFfMpegProcessExited</c>, which sets <c>job.HasExited = true</c> and
    /// then immediately calls <c>job.Dispose()</c> - and <see cref="TranscodingJob.Dispose"/> used to
    /// null out <see cref="TranscodingJob.CurrentAttempt"/> as part of that cleanup, which silently
    /// reverted <c>job.HasExited</c>'s getter (<c>CurrentAttempt?.HasExited ?? false</c>) back to
    /// <c>false</c> the instant <c>Dispose()</c> ran - even though the process had genuinely already
    /// exited. <c>GetSegmentResult</c>'s <c>while (!transcodingJob.HasExited)</c> readiness loop can
    /// then never exit on its own condition again, and - for this fixture, which produces exactly one
    /// segment - its "or the next segment appeared" alternative never becomes true either, so the
    /// request hung forever even though the file it needed to serve was already complete and correct
    /// on disk. Fixed at the source in <see cref="TranscodingJob.Dispose"/> (stopped nulling
    /// <c>CurrentAttempt</c> - nothing else in the codebase reads it, so the seam for a future
    /// multi-attempt fallback wasn't lost). <see cref="CreateBoundedCancellation"/>'s per-call bound is
    /// kept as defense in depth (a genuinely stuck request - e.g. a real transcode failure - should
    /// still fail this test fast rather than hang the run), not because it is still needed to survive
    /// this particular bug.
    /// </remarks>
    /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
    [Fact(Timeout = 90_000)]
    public async Task Transcode_Hls_PostThenGetStream_ServesManifestAndSegment()
    {
        var itemId = SeedItem();
        var (capabilities, constraints) = EndToEndCapabilityPresets.TranscodeHls();

        var session = await CreateSessionAsync(itemId, capabilities, constraints, "e2e-transcode-hls");
        Assert.Equal(PlaybackMethod.Transcode, session.Method);

        var descriptor = await GetStreamDescriptorAsync(session.Id);
        Assert.Equal(StreamingProtocol.Hls, descriptor.Protocol);

        var masterManifest = await GetTextAsync(descriptor.Url, "HLS master playlist");
        Assert.Contains("#EXTM3U", masterManifest, StringComparison.Ordinal);

        var variantUrl = FirstUriLine(masterManifest, descriptor.Url)
            ?? throw new InvalidOperationException($"HLS master playlist named no variant. Content:\n{masterManifest}");

        var mediaPlaylist = await GetTextWithRetryAsync(variantUrl, "HLS media playlist", TimeSpan.FromSeconds(60));
        Assert.Contains("#EXTINF", mediaPlaylist, StringComparison.Ordinal);

        var segmentUrl = FirstUriLine(mediaPlaylist, variantUrl)
            ?? throw new InvalidOperationException($"HLS media playlist named no segment. Content:\n{mediaPlaylist}");

        await AssertUrlServes200Async(segmentUrl, "HLS segment", minBytes: 1);
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

        var itemId = LibraryItemSeeder.SeedVideo(_libraryManager, _mediaStreamRepository, _fixturePath, "mp4", streams, $"PR119 subtitle fixture {Guid.NewGuid()}");
        var (capabilities, constraints) = EndToEndCapabilityPresets.DirectPlayWithExternalSubtitle(subtitleStreamIndex: 2);

        // A specific subtitle stream request requires naming the media source explicitly
        // (PlaybackSessionRequestValidator) - the item's single MediaSource id, itself the item id.
        var session = await CreateSessionAsync(itemId, capabilities, constraints, "e2e-external-subtitle", mediaSourceId: itemId.ToString("N"));

        var descriptor = await GetStreamDescriptorAsync(session.Id);
        Assert.True(!string.IsNullOrEmpty(descriptor.SubtitleUrl), $"Descriptor named no SubtitleUrl for an external-subtitle session (FallbackReason={descriptor.FallbackReason}).");

        await AssertUrlServes200Async(descriptor.Url, "DirectPlay (subtitle scenario)");

        using var subtitleResponse = await _client.GetAsync(descriptor.SubtitleUrl, TestContext.Current.CancellationToken);
        var subtitleBody = await subtitleResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(subtitleResponse.StatusCode == HttpStatusCode.OK, $"External subtitle URL '{descriptor.SubtitleUrl}' did not serve 200 - got {(int)subtitleResponse.StatusCode}. Body: {Truncate(subtitleBody)}");
        Assert.Contains("PR119 end-to-end external subtitle fixture", subtitleBody, StringComparison.Ordinal);
    }

    /// <summary>
    /// Scenario 5: the PR115c kill switch (<see cref="PlaybackShadowOptions.Mode"/> flipped away from
    /// <see cref="PlaybackEngineMode.V2"/>/<see cref="PlaybackEngineMode.Canary"/>) forces legacy for
    /// the very next request, with no restart - and the URL that request resolves to must still be
    /// servable.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
    [Fact]
    public async Task KillSwitch_ForcesLegacyOnNextRequest_UrlStillServable()
    {
        // Not a film: since consolidation step 1 a film's direct play is decided by the engine alone
        // and has no legacy fallback for the kill switch to restore (docs/playback-path-inventory.md §4).
        // The kill switch still governs every journey that has not migrated.
        var itemId = LibraryItemSeeder.SeedVideo(_libraryManager, _mediaStreamRepository, _fixturePath, "mp4", _baseStreams, $"PR119 non-film fixture {Guid.NewGuid()}", film: false);
        var (capabilities, constraints) = EndToEndCapabilityPresets.DirectPlay();

        // V2 mode (not merely Canary): every session is v2-authoritative, no cohort-hash dependency -
        // the simplest way to get a REAL "before" state where v2 actually served this session, so the
        // "after" transition below is a genuine transition and not just re-observing the default.
        _configManager.Configuration.PlaybackShadow.Mode = PlaybackEngineMode.V2;

        var session = await CreateSessionAsync(itemId, capabilities, constraints, "e2e-kill-switch");

        var beforeDescriptor = await GetStreamDescriptorAsync(session.Id);
        await AssertUrlServes200Async(beforeDescriptor.Url, "Before kill switch");
        Assert.True(
            beforeDescriptor.FallbackReason is null && beforeDescriptor.ServedBy != PlaybackSessionResponse.LegacyDecisionVersion,
            $"Expected the session to be genuinely v2-served before the kill switch - got ServedBy={beforeDescriptor.ServedBy}, FallbackReason={beforeDescriptor.FallbackReason}. " +
            "The kill-switch transition below is only meaningful against a real v2-served 'before' state.");

        // The kill switch itself: an operator flips PlaybackShadow.Mode back to Legacy/Shadow. Per
        // PlaybackLiveStreamResolver's own remarks, this takes effect on the very next request - no
        // restart, no session recreation.
        _configManager.Configuration.PlaybackShadow.Mode = PlaybackEngineMode.Legacy;

        var afterDescriptor = await GetStreamDescriptorAsync(session.Id);
        Assert.Equal(PlaybackLiveFallbackReason.KillSwitch, afterDescriptor.FallbackReason);
        Assert.Equal(PlaybackSessionResponse.LegacyDecisionVersion, afterDescriptor.ServedBy);

        await AssertUrlServes200Async(afterDescriptor.Url, "After kill switch (legacy fallback)");
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
    public async Task Remux_MatroskaSourceAnnouncedAsMp4_ServesRealMp4Bytes()
    {
        var mkvPath = await EndToEndMediaFixtures.CreateH264AacMkvAsync(_workDir);
        var itemId = LibraryItemSeeder.SeedVideo(
            _libraryManager,
            _mediaStreamRepository,
            mkvPath,
            "mkv",
            _baseStreams,
            $"Issue 57 matroska fixture {Guid.NewGuid()}");

        var (capabilities, constraints) = EndToEndCapabilityPresets.RemuxMatroskaToMp4();

        // The reported descriptor carried ServedBy=6 and NO FallbackReason - the v2 engine, not a
        // legacy fallback. V2 mode (not merely Canary) makes every session v2-authoritative with no
        // cohort-hash dependency; InitializeAsync resets this to Legacy before every test.
        _configManager.Configuration.PlaybackShadow.Mode = PlaybackEngineMode.V2;

        var session = await CreateSessionAsync(itemId, capabilities, constraints, "e2e-remux-mkv-to-mp4");
        Assert.Equal(PlaybackMethod.Remux, session.Method);

        var descriptor = await GetStreamDescriptorAsync(session.Id);

        // Guard the premise: a legacy fallback here would silently turn this into a test of a
        // different engine than the one issue #57 was reported against.
        Assert.Null(descriptor.FallbackReason);
        Assert.NotEqual(PlaybackSessionResponse.LegacyDecisionVersion, descriptor.ServedBy);

        Assert.Equal(StreamingProtocol.Http, descriptor.Protocol);
        Assert.Equal("mp4", descriptor.Container);
        Assert.Equal("video/mp4", descriptor.MimeType);

        // Static=true means "serve the source file untouched" - DirectPlay semantics. A Remux that
        // asks for it can only ever serve the source container, whatever it announces.
        Assert.DoesNotContain("Static=true", descriptor.Url, StringComparison.OrdinalIgnoreCase);

        var served = await GetBytesAsync(descriptor.Url, "Remux (Matroska source announced as mp4)");
        var source = await File.ReadAllBytesAsync(mkvPath, TestContext.Current.CancellationToken);

        Assert.False(
            served.AsSpan().SequenceEqual(source),
            $"The served bytes are byte-identical to the source .mkv ({served.Length} bytes): the announced remux to mp4 never happened.");

        Assert.False(
            IsMatroska(served),
            $"The served bytes start with the Matroska/EBML magic 1a45dfa3, but the descriptor announced Container=mp4/MimeType=video/mp4. Head: {Head(served)}");

        Assert.True(
            IsIsoBaseMediaFile(served),
            $"The served bytes are not ISOBMFF: expected the ASCII box type 'ftyp' at offset 4. Head: {Head(served)}");

        // The strongest available statement: what a real demuxer says the bytes ARE.
        var probedPath = Path.Combine(_workDir, $"served-{Guid.NewGuid():N}.mp4");
        await File.WriteAllBytesAsync(probedPath, served, TestContext.Current.CancellationToken);
        var formatName = await EndToEndMediaFixtures.ProbeFormatNameAsync(probedPath);
        Assert.DoesNotContain("matroska", formatName, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("mp4", formatName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// #374: the default (legacy) path must not answer a Remux with the source container the client
    /// declared unplayable. Asserted down to what a demuxer says the served segment is.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
    [Fact(Timeout = 90_000)]
    public async Task Remux_LegacyPath_UndeclaredSourceContainer_ServesDeclaredOutputNotSourceFile()
    {
        var mkvPath = await EndToEndMediaFixtures.CreateH264AacMkvAsync(_workDir);
        var itemId = LibraryItemSeeder.SeedVideo(_libraryManager, _mediaStreamRepository, mkvPath, "mkv", _baseStreams, $"Issue 374 fixture {Guid.NewGuid()}");

        var (capabilities, constraints) = EndToEndCapabilityPresets.Remux();
        var session = await CreateSessionAsync(itemId, capabilities, constraints, "e2e-374-remux");
        Assert.Equal(PlaybackMethod.Remux, session.Method);

        var descriptor = await GetStreamDescriptorAsync(session.Id);
        Assert.Equal(PlaybackLiveFallbackReason.KillSwitch, descriptor.FallbackReason);
        Assert.Equal(StreamingProtocol.Hls, descriptor.Protocol);
        Assert.Equal("ts", descriptor.Container);
        Assert.DoesNotContain("Static=true", descriptor.Url, StringComparison.OrdinalIgnoreCase);

        var master = await GetTextAsync(descriptor.Url, "#374 master");
        var variantUrl = FirstUriLine(master, descriptor.Url);
        Assert.NotNull(variantUrl);
        var variant = await GetTextWithRetryAsync(variantUrl, "#374 variant", TimeSpan.FromSeconds(60));
        var segmentUrl = FirstUriLine(variant, variantUrl);
        Assert.NotNull(segmentUrl);

        var served = await GetBytesAsync(segmentUrl, "#374 segment");
        Assert.False(IsMatroska(served), $"Served bytes are still the source Matroska. Head: {Head(served)}");
        var probedPath = Path.Combine(_workDir, $"served-374-{Guid.NewGuid():N}.ts");
        await File.WriteAllBytesAsync(probedPath, served, TestContext.Current.CancellationToken);
        Assert.Equal("mpegts", await EndToEndMediaFixtures.ProbeFormatNameAsync(probedPath));
    }

    /// <summary>
    /// #374: a session v2 decided, falling back to legacy for real. With a compatible legacy output
    /// the fallback serves it; without one it refuses, and never the undeclared source file.
    /// </summary>
    /// <param name="mode">The engine mode the session is planned under.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
    [Theory(Timeout = 90_000)]
    [InlineData(PlaybackEngineMode.V2)]
    [InlineData(PlaybackEngineMode.Canary)]
    public async Task RealFallbackFromV2_NeverServesTheUndeclaredSourceFile(PlaybackEngineMode mode)
    {
        var mkvPath = await EndToEndMediaFixtures.CreateH264AacMkvAsync(_workDir);
        var itemId = LibraryItemSeeder.SeedVideo(_libraryManager, _mediaStreamRepository, mkvPath, "mkv", _baseStreams, $"Issue 374 fallback {Guid.NewGuid()}");
        var shadow = _configManager.Configuration.PlaybackShadow;
        shadow.Mode = mode;
        shadow.CanaryPercentage = 100;

        var (mp4Declared, constraints) = EndToEndCapabilityPresets.RemuxMatroskaToMp4();
        var withOutput = await CreateSessionAsync(itemId, mp4Declared, constraints, $"e2e-374-fallback-{mode}-ok");
        var noOutput = mp4Declared with
        {
            Decode = mp4Declared.Decode with { DirectPlayProfiles = [new DecodeProfile(MediaKind.Video, ["webm"], [EndToEndCapabilityPresets.FixtureVideoCodec], [EndToEndCapabilityPresets.FixtureAudioCodec])] },
        };
        var withoutOutput = await CreateSessionAsync(itemId, noOutput, constraints, $"e2e-374-fallback-{mode}-none");

        // The kill switch is the operator's real, live path back to legacy.
        shadow.Mode = PlaybackEngineMode.Legacy;

        var descriptor = await GetStreamDescriptorAsync(withOutput.Id);
        Assert.Equal(PlaybackLiveFallbackReason.KillSwitch, descriptor.FallbackReason);
        Assert.Equal("mp4", descriptor.Container);
        Assert.DoesNotContain("Static=true", descriptor.Url, StringComparison.OrdinalIgnoreCase);
        var served = await GetBytesAsync(descriptor.Url, "#374 fallback");
        Assert.False(IsMatroska(served), $"The fallback served the source Matroska. Head: {Head(served)}");
        Assert.True(IsIsoBaseMediaFile(served), $"The fallback did not serve mp4. Head: {Head(served)}");

        using var refused = await _client.GetAsync($"Playback/Sessions/{withoutOutput.Id}/Stream", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
    }

    /// <summary>
    /// #374: with no output the client can open, the refusal says so instead of handing over a file.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
    [Fact(Timeout = 90_000)]
    public async Task Remux_LegacyPath_NoOutputAvailable_IsAnExplainedRefusal()
    {
        var mkvPath = await EndToEndMediaFixtures.CreateH264AacMkvAsync(_workDir);
        var itemId = LibraryItemSeeder.SeedVideo(_libraryManager, _mediaStreamRepository, mkvPath, "mkv", _baseStreams, $"Issue 374 refusal {Guid.NewGuid()}");

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
    /// <see cref="Remux_MatroskaSourceAnnouncedAsMp4_ServesRealMp4Bytes"/>: a 200 proves the request
    /// was not refused, but only the bytes prove a REAL remux still happened under the constraint.
    /// </remarks>
    /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
    [Fact(Timeout = 90_000)]
    public async Task Remux_TranscodingForbidden_StillServesRealRemuxedMp4Bytes()
    {
        var mkvPath = await EndToEndMediaFixtures.CreateH264AacMkvAsync(_workDir);
        var itemId = LibraryItemSeeder.SeedVideo(
            _libraryManager,
            _mediaStreamRepository,
            mkvPath,
            "mkv",
            _baseStreams,
            $"Issue 59 remux-not-blocked fixture {Guid.NewGuid()}");

        var (capabilities, constraints) = EndToEndCapabilityPresets.RemuxMatroskaToMp4TranscodingForbidden();
        Assert.False(constraints.AllowTranscoding, "Premise: this row is only meaningful with transcoding forbidden.");

        _configManager.Configuration.PlaybackShadow.Mode = PlaybackEngineMode.V2;

        var session = await CreateSessionAsync(itemId, capabilities, constraints, "e2e-59-remux-allowed");
        Assert.Equal(PlaybackMethod.Remux, session.Method);

        var descriptor = await GetStreamDescriptorAsync(session.Id);
        Assert.Equal("mp4", descriptor.Container);
        Assert.DoesNotContain("Static=true", descriptor.Url, StringComparison.OrdinalIgnoreCase);

        var served = await GetBytesAsync(descriptor.Url, "Remux under AllowTranscoding:false");
        var source = await File.ReadAllBytesAsync(mkvPath, TestContext.Current.CancellationToken);

        Assert.False(
            served.AsSpan().SequenceEqual(source),
            $"The served bytes are byte-identical to the source .mkv ({served.Length} bytes): the remux never happened.");
        Assert.False(IsMatroska(served), $"Served bytes are still Matroska. Head: {Head(served)}");
        Assert.True(IsIsoBaseMediaFile(served), $"Served bytes are not ISOBMFF ('ftyp' at offset 4). Head: {Head(served)}");

        var probedPath = Path.Combine(_workDir, $"served-59-remux-{Guid.NewGuid():N}.mp4");
        await File.WriteAllBytesAsync(probedPath, served, TestContext.Current.CancellationToken);
        var formatName = await EndToEndMediaFixtures.ProbeFormatNameAsync(probedPath);
        Assert.DoesNotContain("matroska", formatName, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("mp4", formatName, StringComparison.OrdinalIgnoreCase);
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

        _configManager.Configuration.PlaybackShadow.Mode = PlaybackEngineMode.V2;

        var status = await CreateSessionExpectingFailureAsync(itemId, capabilities, constraints, "e2e-59-no-plan");

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

        // The legacy engine, not v2: this is the branch issue #59 was reported against.
        _configManager.Configuration.PlaybackShadow.Mode = PlaybackEngineMode.Legacy;

        var status = await CreateSessionExpectingFailureAsync(itemId, capabilities, constraints, "e2e-59-no-plan-legacy");

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

        var status = await CreateSessionExpectingFailureAsync(itemId, capabilities, constraints, "e2e-59-all-forbidden");

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
        var session = await CreateSessionAsync(itemId, directPlayCapabilities, directPlayConstraints, "e2e-59-replan-seed");
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
        Guid itemId,
        ClientCapabilities capabilities,
        PlaybackConstraints constraints,
        string playSessionId)
    {
        var request = new CreatePlaybackSessionRequest(itemId, _userId, capabilities, constraints, MediaSourceId: null, PlaySessionId: playSessionId);
        using var response = await _client.PostAsJsonAsync("Playback/Sessions", request, JsonDefaults.Options, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.False(
            response.IsSuccessStatusCode,
            $"POST Playback/Sessions was expected to be refused for PlaySessionId='{playSessionId}', but it SUCCEEDED with {(int)response.StatusCode}. " +
            $"A session created here would go on to be served - which is exactly the issue #59 defect. Body: {Truncate(body)}");

        return response.StatusCode;
    }

    /// <summary>Matroska/EBML magic: <c>1A 45 DF A3</c> at offset 0.</summary>
    private static bool IsMatroska(byte[] bytes) =>
        bytes.Length >= 4 && bytes[0] == 0x1A && bytes[1] == 0x45 && bytes[2] == 0xDF && bytes[3] == 0xA3;

    /// <summary>ISO base media file format: the ASCII box type <c>ftyp</c> at offset 4 (ISO/IEC 14496-12).</summary>
    private static bool IsIsoBaseMediaFile(byte[] bytes) =>
        bytes.Length >= 8 && bytes[4] == (byte)'f' && bytes[5] == (byte)'t' && bytes[6] == (byte)'y' && bytes[7] == (byte)'p';

    private static string Head(byte[] bytes, int count = 16) =>
        Convert.ToHexString(bytes, 0, Math.Min(count, bytes.Length));

    private Guid SeedItem() =>
        LibraryItemSeeder.SeedVideo(_libraryManager, _mediaStreamRepository, _fixturePath, "mp4", _baseStreams, $"PR119 fixture {Guid.NewGuid()}");

    private async Task<PlaybackSessionResponse> CreateSessionAsync(Guid itemId, ClientCapabilities capabilities, PlaybackConstraints constraints, string playSessionId, string? mediaSourceId = null)
    {
        var request = new CreatePlaybackSessionRequest(itemId, _userId, capabilities, constraints, MediaSourceId: mediaSourceId, PlaySessionId: playSessionId);
        using var response = await _client.PostAsJsonAsync("Playback/Sessions", request, JsonDefaults.Options, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, $"POST Playback/Sessions failed for PlaySessionId='{playSessionId}': {(int)response.StatusCode} {Truncate(body)}");

        return JsonSerializer.Deserialize<PlaybackSessionResponse>(body, JsonDefaults.Options)
            ?? throw new InvalidOperationException("POST Playback/Sessions returned an empty body.");
    }

    private async Task<PlaybackSessionStreamDescriptor> GetStreamDescriptorAsync(Guid sessionId)
    {
        using var response = await _client.GetAsync($"Playback/Sessions/{sessionId}/Stream", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
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
    /// <see cref="Remux_MatroskaSourceAnnouncedAsMp4_ServesRealMp4Bytes"/>, where the whole point is
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
    private async Task<string> GetTextWithRetryAsync(string relativeUrl, string diagnosticContext, TimeSpan timeout)
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
                await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken).ConfigureAwait(false);
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
