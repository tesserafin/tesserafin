using System.Collections.Generic;
using Tesserafin.Playback.Decision;

namespace Tesserafin.Server.Integration.Tests.EndToEnd;

/// <summary>
/// PR119: builds the <see cref="ClientCapabilities"/>/<see cref="PlaybackConstraints"/> pairs that
/// deterministically steer the legacy <c>StreamBuilder</c> (via
/// <c>ReverseDlnaAdapter.ToDeviceProfile</c>/<c>ApplyConstraints</c>) to a specific
/// <c>PlayMethod</c> for the one real fixture <see cref="EndToEndMediaFixtures"/> produces
/// (H.264/AAC/MP4). Verified against <c>Tesserafin.Model/Dlna/StreamBuilder.cs</c>
/// (<c>BuildVideoItem</c>/<c>GetVideoDirectPlayProfile</c>):
/// <list type="bullet">
/// <item>DirectPlay: a declared direct-play combination whose container/codecs match the fixture
/// exactly, with <see cref="PlaybackConstraints.AllowDirectPlay"/> true.</item>
/// <item>Remux (DirectStream): a declared direct-play combination whose CODECS match but whose
/// CONTAINER deliberately does not (<c>"webm"</c> vs. the fixture's real <c>"mp4"</c>) - StreamBuilder's
/// <c>DirectStreamReasons</c> mask tolerates a container mismatch for DirectStream specifically
/// (remux = compatible codecs, different container), so this becomes DirectStream once
/// <see cref="PlaybackConstraints.AllowDirectPlay"/> is false and
/// <see cref="PlaybackConstraints.AllowDirectStream"/> is true.</item>
/// <item>Transcode (HLS): both direct methods forbidden by constraints, forcing
/// <c>GetVideoTranscodeProfile</c> to run against the declared HLS
/// <see cref="PlaybackOutputProfile"/>.</item>
/// </list>
/// </summary>
public static class EndToEndCapabilityPresets
{
    public const string FixtureContainer = "mp4";
    public const string FixtureVideoCodec = "h264";
    public const string FixtureAudioCodec = "aac";

    /// <summary>A container the fixture is deliberately NOT declared under, to force remux (codecs match, container does not).</summary>
    private const string MismatchedContainer = "webm";

    public static (ClientCapabilities Capabilities, PlaybackConstraints Constraints) DirectPlay()
    {
        var decode = new DecodeCapabilities(
            DirectPlayProfiles: [new DecodeProfile(MediaKind.Video, [FixtureContainer], [FixtureVideoCodec], [FixtureAudioCodec])],
            VideoCodecs: [new VideoCodecCapability(FixtureVideoCodec, [], null, null, [], null, null)],
            AudioCodecs: [new AudioCodecCapability(FixtureAudioCodec, null, null, null, null)],
            SubtitleDelivery: [],
            SupportsHls: true,
            SupportsDash: false);

        var capabilities = new ClientCapabilities(decode, OutputProfiles: []);
        var constraints = BaseConstraints(allowDirectPlay: true, allowDirectStream: true, allowTranscoding: true);

        return (capabilities, constraints);
    }

    public static (ClientCapabilities Capabilities, PlaybackConstraints Constraints) Remux()
    {
        var decode = new DecodeCapabilities(
            DirectPlayProfiles: [new DecodeProfile(MediaKind.Video, [MismatchedContainer], [FixtureVideoCodec], [FixtureAudioCodec])],
            VideoCodecs: [new VideoCodecCapability(FixtureVideoCodec, [], null, null, [], null, null)],
            AudioCodecs: [new AudioCodecCapability(FixtureAudioCodec, null, null, null, null)],
            SubtitleDelivery: [],
            SupportsHls: true,
            SupportsDash: false);

        // #374: a remux needs an output the client declared; without one this is a refusal.
        var capabilities = new ClientCapabilities(decode, OutputProfiles: [HlsOutputProfile()]);
        var constraints = BaseConstraints(allowDirectPlay: false, allowDirectStream: true, allowTranscoding: false);

        return (capabilities, constraints);
    }

    /// <summary>
    /// Issue #57's exact reported shape: the client declares it can direct-play H.264/AAC in
    /// <c>mp4</c> ONLY, against a real MATROSKA source (<c>EndToEndMediaFixtures.CreateH264AacMkvAsync</c>).
    /// The container is not supported (<c>ContainerNotSupported</c>) but both codecs are copyable
    /// (<c>StreamCopyable</c>), so the decision is <c>Remux</c> to <c>mp4</c> - and unlike
    /// <see cref="Remux()"/> (mp4 source, mp4 bytes either way) the announced container and the source
    /// container genuinely differ, which is what makes "did the server actually remux?" observable.
    /// </summary>
    /// <remarks>
    /// Every method stays allowed, deliberately: the reported decision carried
    /// <c>Reasons=[MethodChosen, ContainerNotSupported, StreamCopyable]</c>, i.e. Remux was CHOSEN on
    /// its merits, not forced by a constraint that forbade the alternatives.
    /// </remarks>
    /// <returns>Capabilities declaring mp4-only H.264/AAC direct play, with every delivery method allowed.</returns>
    public static (ClientCapabilities Capabilities, PlaybackConstraints Constraints) RemuxMatroskaToMp4()
    {
        var decode = new DecodeCapabilities(
            DirectPlayProfiles: [new DecodeProfile(MediaKind.Video, [FixtureContainer], [FixtureVideoCodec], [FixtureAudioCodec])],
            VideoCodecs: [new VideoCodecCapability(FixtureVideoCodec, [], null, null, [], null, null)],
            AudioCodecs: [new AudioCodecCapability(FixtureAudioCodec, null, null, null, null)],
            SubtitleDelivery: [],
            SupportsHls: true,
            SupportsDash: false);

        var capabilities = new ClientCapabilities(decode, OutputProfiles: []);
        var constraints = BaseConstraints(allowDirectPlay: true, allowDirectStream: true, allowTranscoding: true);

        return (capabilities, constraints);
    }

    /// <summary>
    /// Issue #59, matrix row 2: <see cref="RemuxMatroskaToMp4"/> with transcoding explicitly
    /// FORBIDDEN. Container is not supported but both codecs are copyable, so this is a genuine
    /// remux - which <see cref="PlaybackConstraints.AllowDirectStream"/>=true must keep permitting.
    /// Nothing here may be re-encoded, so <c>AllowTranscoding:false</c> must not block it.
    /// </summary>
    /// <returns>The <see cref="RemuxMatroskaToMp4"/> capabilities, with constraints that forbid transcoding.</returns>
    public static (ClientCapabilities Capabilities, PlaybackConstraints Constraints) RemuxMatroskaToMp4TranscodingForbidden()
    {
        var (capabilities, _) = RemuxMatroskaToMp4();
        var constraints = BaseConstraints(allowDirectPlay: true, allowDirectStream: true, allowTranscoding: false);

        return (capabilities, constraints);
    }

    /// <summary>
    /// Issue #59, matrix row 3: the client cannot decode the fixture's codecs at all (it declares
    /// vp9/opus against an h264/aac source), so no stream is copyable and the ONLY plan that could
    /// ever serve this session is a real re-encode. With <c>AllowTranscoding:false</c> that plan is
    /// forbidden, so the correct outcome is "no viable plan" - never a silent transcode.
    /// </summary>
    /// <remarks>
    /// <see cref="PlaybackConstraints.AllowDirectStream"/> stays true on purpose: that is the exact
    /// combination that used to let legacy's <c>StreamBuilder.GetVideoTranscodeProfile</c> through on
    /// the strength of <c>SupportsDirectStream</c> alone and re-encode anyway. DirectPlayProfiles is
    /// declared but deliberately unsatisfiable (vp9/opus), which also satisfies the validator's
    /// "declared nothing decodable" rule - so this request is VALID, and its rejection is a 422
    /// ("no viable plan"), formally distinct from a 400 ("contradictory request").
    /// </remarks>
    /// <returns>Capabilities declaring only undecodable vp9/opus plus a reachable HLS output profile, with constraints that forbid transcoding.</returns>
    public static (ClientCapabilities Capabilities, PlaybackConstraints Constraints) IncompatibleCodecsTranscodingForbidden()
    {
        const string UndecodableVideoCodec = "vp9";
        const string UndecodableAudioCodec = "opus";

        var decode = new DecodeCapabilities(
            DirectPlayProfiles: [new DecodeProfile(MediaKind.Video, [FixtureContainer], [UndecodableVideoCodec], [UndecodableAudioCodec])],
            VideoCodecs: [new VideoCodecCapability(UndecodableVideoCodec, [], null, null, [], null, null)],
            AudioCodecs: [new AudioCodecCapability(UndecodableAudioCodec, null, null, null, null)],
            SubtitleDelivery: [],
            SupportsHls: true,
            SupportsDash: false);

        // An HLS output profile is declared so that a transcode is genuinely REACHABLE here: without
        // it the request would be unservable for want of an output target rather than because
        // AllowTranscoding:false forbade it, and the test would prove nothing about the constraint.
        var outputProfiles = new List<PlaybackOutputProfile>
        {
            new(MediaKind.Video, StreamingProtocol.Hls, "ts", [UndecodableVideoCodec], [UndecodableAudioCodec], null, null, null),
        };

        var capabilities = new ClientCapabilities(decode, outputProfiles);
        var constraints = BaseConstraints(allowDirectPlay: true, allowDirectStream: true, allowTranscoding: false);

        return (capabilities, constraints);
    }

    /// <summary>
    /// Issue #59, matrix row 5: every delivery method forbidden. Unlike
    /// <see cref="IncompatibleCodecsTranscodingForbidden"/> this request is self-contradictory
    /// (it permits no method at all), which the validator rejects with 400 before any planning runs.
    /// </summary>
    /// <returns>The <see cref="DirectPlay"/> capabilities, with constraints that forbid direct play, direct streaming and transcoding.</returns>
    public static (ClientCapabilities Capabilities, PlaybackConstraints Constraints) AllMethodsForbidden()
    {
        var (capabilities, _) = DirectPlay();
        var constraints = BaseConstraints(allowDirectPlay: false, allowDirectStream: false, allowTranscoding: false);

        return (capabilities, constraints);
    }

    public static (ClientCapabilities Capabilities, PlaybackConstraints Constraints) TranscodeHls()
    {
        // DirectPlayProfiles deliberately empty (no direct-play combination declared at all, so
        // DirectPlay is unreachable regardless of constraints) - VideoCodecs/AudioCodecs still
        // declared so PlaybackSessionRequestValidator's "declared nothing decodable" rule is
        // satisfied; they play no role in ruling DirectPlay in since DirectPlayProfiles stays empty.
        var decode = new DecodeCapabilities(
            DirectPlayProfiles: [],
            VideoCodecs: [new VideoCodecCapability(FixtureVideoCodec, [], null, null, [], null, null)],
            AudioCodecs: [new AudioCodecCapability(FixtureAudioCodec, null, null, null, null)],
            SubtitleDelivery: [],
            SupportsHls: true,
            SupportsDash: false);

        var outputProfiles = new List<PlaybackOutputProfile>
        {
            new(MediaKind.Video, StreamingProtocol.Hls, "ts", [FixtureVideoCodec], [FixtureAudioCodec], null, null, null),
        };

        var capabilities = new ClientCapabilities(decode, outputProfiles);

        // AllowVideoStreamCopy=false: forces a REAL re-encode (not just a remux-through-HLS) so this
        // scenario actually exercises TranscodeManager/ffmpeg, not merely a container change.
        var constraints = new PlaybackConstraints(
            AllowDirectPlay: false,
            AllowDirectStream: false,
            AllowTranscoding: true,
            AllowVideoStreamCopy: false,
            AllowAudioStreamCopy: true,
            MaxBitrate: null,
            MaxAudioChannels: null,
            PreferredAudioStreamIndex: null,
            PreferredSubtitleStreamIndex: null,
            SubtitleMode: SubtitlePlaybackMode.None,
            PreferredSubtitleLanguages: [],
            AlwaysBurnInSubtitleWhenTranscoding: false,
            StartTimeTicks: 0);

        return (capabilities, constraints);
    }

    /// <summary>
    /// What a browser declares for the consolidation remux journey: H.264/AAC decodable in
    /// <c>mp4</c> only, HLS supported, and two video output profiles in its own preference order -
    /// fMP4 segments first, MPEG-TS second. Against a Matroska source the engine's answer is a
    /// remux delivered through the first of them. Every method stays allowed.
    /// </summary>
    /// <returns>Browser-like capabilities with every delivery method allowed.</returns>
    public static (ClientCapabilities Capabilities, PlaybackConstraints Constraints) BrowserRemuxOverHls()
    {
        var decode = new DecodeCapabilities(
            DirectPlayProfiles: [new DecodeProfile(MediaKind.Video, [FixtureContainer], [FixtureVideoCodec], [FixtureAudioCodec])],
            VideoCodecs: [new VideoCodecCapability(FixtureVideoCodec, [], null, null, [], null, null)],
            AudioCodecs: [new AudioCodecCapability(FixtureAudioCodec, null, null, null, null)],
            SubtitleDelivery: [new SubtitleCapability("vtt", SubtitleDeliveryMethod.External)],
            SupportsHls: true,
            SupportsDash: false);

        var capabilities = new ClientCapabilities(
            decode,
            OutputProfiles:
            [
                new PlaybackOutputProfile(MediaKind.Video, StreamingProtocol.Hls, "mp4", [FixtureVideoCodec], [FixtureAudioCodec], null, null, null),
                HlsOutputProfile(),
            ]);

        return (capabilities, BaseConstraints(allowDirectPlay: true, allowDirectStream: true, allowTranscoding: true));
    }

    /// <summary>
    /// DirectPlay preset augmented with an external subtitle declaration - the client declares it can
    /// render <c>srt</c> via external delivery, and explicitly prefers the given stream index (mirrors
    /// a real client that already knows which track it wants, rather than relying on
    /// auto-selection/scoring).
    /// </summary>
    /// <param name="subtitleStreamIndex">Index of the subtitle stream the client explicitly prefers.</param>
    /// <returns>The <see cref="DirectPlay"/> preset augmented with external <c>srt</c> delivery and that preferred subtitle index.</returns>
    public static (ClientCapabilities Capabilities, PlaybackConstraints Constraints) DirectPlayWithExternalSubtitle(int subtitleStreamIndex)
    {
        var (baseCapabilities, baseConstraints) = DirectPlay();
        var decode = baseCapabilities.Decode with
        {
            SubtitleDelivery = [new SubtitleCapability("srt", SubtitleDeliveryMethod.External)],
        };
        var capabilities = baseCapabilities with { Decode = decode };
        var constraints = baseConstraints with { PreferredSubtitleStreamIndex = subtitleStreamIndex };

        return (capabilities, constraints);
    }

    private static PlaybackOutputProfile HlsOutputProfile() =>
        new(MediaKind.Video, StreamingProtocol.Hls, "ts", [FixtureVideoCodec], [FixtureAudioCodec], null, null, null);

    private static PlaybackConstraints BaseConstraints(bool allowDirectPlay, bool allowDirectStream, bool allowTranscoding) => new(
        AllowDirectPlay: allowDirectPlay,
        AllowDirectStream: allowDirectStream,
        AllowTranscoding: allowTranscoding,
        AllowVideoStreamCopy: true,
        AllowAudioStreamCopy: true,
        MaxBitrate: null,
        MaxAudioChannels: null,
        PreferredAudioStreamIndex: null,
        PreferredSubtitleStreamIndex: null,
        SubtitleMode: SubtitlePlaybackMode.None,
        PreferredSubtitleLanguages: [],
        AlwaysBurnInSubtitleWhenTranscoding: false,
        StartTimeTicks: 0);
}
