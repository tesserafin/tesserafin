using System.Collections.Generic;
using Tesserafin.Playback.Decision;
using Xunit;

namespace Tesserafin.Playback.Engine.Tests;

/// <summary>
/// Boundary unit tests for the phase-2 (PR97) engine additions: transcoding, resolution/level/
/// bit-depth/channel limits, HDR tonemapping, subtitle delivery/burn-in, and the
/// transcode-disallowed no-viable-plan path. Each test pins an edge (at the limit vs. one past it)
/// that the fixture-parity tests sample but don't isolate as precisely.
/// </summary>
public static class PlaybackEnginePhase2Tests
{
    [Fact]
    public static void Decide_VideoLevelAtMax_IsNotTripped()
    {
        var decision = DecideWithVideoLevel(level: 41);

        Assert.True(decision.IsViable);
        Assert.Equal(PlaybackMethod.DirectPlay, decision.Method);
        Assert.DoesNotContain(TransformKind.TranscodeVideo, decision.Transforms);
    }

    [Fact]
    public static void Decide_VideoLevelOneOverMax_TripsLevelCheck()
    {
        var decision = DecideWithVideoLevel(level: 42);

        Assert.True(decision.IsViable);
        Assert.Equal(PlaybackMethod.Transcode, decision.Method);
        Assert.Contains(TransformKind.TranscodeVideo, decision.Transforms);
        Assert.Contains(ReasonCode.VideoLevelNotSupported, FlattenReasonCodes(decision.Reasoning));
    }

    private static PlaybackDecision DecideWithVideoLevel(double level)
    {
        var capabilities = new ClientCapabilities(
            Decode: new DecodeCapabilities(
                DirectPlayProfiles: [new DecodeProfile(MediaKind.Video, ["mp4"], ["h264"], ["aac"])],
                VideoCodecs: [new VideoCodecCapability("h264", [], MaxLevel: 41, null, [], null, null)],
                AudioCodecs: [new AudioCodecCapability("aac", null, null, null, null)],
                SubtitleDelivery: [],
                SupportsHls: false,
                SupportsDash: false),
            OutputProfiles: []);

        var source = EngineTestFixtures.Source(
            "source-1",
            "mp4",
            videoStreams: [EngineTestFixtures.VideoStream(0, "h264", level: level)],
            audioStreams: [EngineTestFixtures.AudioStream(1, "aac", isDefault: true)]);

        var engine = new PlaybackEngine();
        return engine.Decide(EngineTestFixtures.Context(MediaKind.Video), capabilities, [source], EngineTestFixtures.Constraints());
    }

    [Fact]
    public static void Decide_AudioChannelsAtEffectiveMax_Copies()
    {
        var decision = DecideWithAudioChannels(channels: 2);

        Assert.True(decision.IsViable);
        Assert.Equal(PlaybackMethod.DirectPlay, decision.Method);
        Assert.DoesNotContain(TransformKind.Downmix, decision.Transforms);
    }

    [Fact]
    public static void Decide_AudioChannelsOneOverEffectiveMax_Downmixes()
    {
        var decision = DecideWithAudioChannels(channels: 3);

        Assert.True(decision.IsViable);
        Assert.Equal(PlaybackMethod.Transcode, decision.Method);
        Assert.Contains(TransformKind.TranscodeAudio, decision.Transforms);
        Assert.Contains(TransformKind.Downmix, decision.Transforms);
        Assert.Contains(ReasonCode.AudioChannelsNotSupported, FlattenReasonCodes(decision.Reasoning));
        Assert.Equal(2, decision.Output.AudioChannels);
    }

    private static PlaybackDecision DecideWithAudioChannels(int channels)
    {
        // effMaxChannels = min(cap.MaxChannels=6, constraints.MaxAudioChannels=2) = 2.
        var capabilities = new ClientCapabilities(
            Decode: new DecodeCapabilities(
                DirectPlayProfiles: [new DecodeProfile(MediaKind.Audio, ["mp4"], [], ["aac"])],
                VideoCodecs: [],
                AudioCodecs: [new AudioCodecCapability("aac", MaxChannels: 6, null, null, null)],
                SubtitleDelivery: [],
                SupportsHls: false,
                SupportsDash: false),
            OutputProfiles: []);

        var source = EngineTestFixtures.Source(
            "source-1",
            "mp4",
            audioStreams: [EngineTestFixtures.AudioStream(0, "aac", isDefault: true, channels: channels)]);

        var engine = new PlaybackEngine();
        return engine.Decide(
            EngineTestFixtures.Context(MediaKind.Audio),
            capabilities,
            [source],
            EngineTestFixtures.Constraints(maxAudioChannels: 2));
    }

    [Fact]
    public static void Decide_ResolutionAtMax_IsNotTripped()
    {
        var decision = DecideWithResolution(width: 1920, height: 1080);

        Assert.True(decision.IsViable);
        Assert.Equal(PlaybackMethod.DirectPlay, decision.Method);
        Assert.Null(decision.Output.Resolution);
    }

    [Fact]
    public static void Decide_WidthOneOverMax_Downscales()
    {
        var decision = DecideWithResolution(width: 1921, height: 1080);

        Assert.True(decision.IsViable);
        Assert.Equal(PlaybackMethod.Transcode, decision.Method);
        Assert.Contains(TransformKind.TranscodeVideo, decision.Transforms);
        Assert.Contains(ReasonCode.VideoResolutionNotSupported, FlattenReasonCodes(decision.Reasoning));
        Assert.Equal(new Resolution(1920, 1080), decision.Output.Resolution);
    }

    private static PlaybackDecision DecideWithResolution(int width, int height)
    {
        var capabilities = new ClientCapabilities(
            Decode: new DecodeCapabilities(
                DirectPlayProfiles: [new DecodeProfile(MediaKind.Video, ["mp4"], ["h264"], ["aac"])],
                VideoCodecs: [new VideoCodecCapability("h264", [], null, null, [], MaxResolution: new Resolution(1920, 1080), null)],
                AudioCodecs: [new AudioCodecCapability("aac", null, null, null, null)],
                SubtitleDelivery: [],
                SupportsHls: false,
                SupportsDash: false),
            OutputProfiles: []);

        var source = EngineTestFixtures.Source(
            "source-1",
            "mp4",
            videoStreams: [EngineTestFixtures.VideoStream(0, "h264", width: width, height: height)],
            audioStreams: [EngineTestFixtures.AudioStream(1, "aac", isDefault: true)]);

        var engine = new PlaybackEngine();
        return engine.Decide(EngineTestFixtures.Context(MediaKind.Video), capabilities, [source], EngineTestFixtures.Constraints());
    }

    [Fact]
    public static void Decide_SubtitleAlreadyExternal_DeliversExternalWithNoExtractAndStaysDirectPlay()
    {
        var capabilities = new ClientCapabilities(
            Decode: new DecodeCapabilities(
                DirectPlayProfiles: [new DecodeProfile(MediaKind.Video, ["mp4"], ["h264"], ["aac"])],
                VideoCodecs: [new VideoCodecCapability("h264", [], null, null, [], null, null)],
                AudioCodecs: [new AudioCodecCapability("aac", null, null, null, null)],
                SubtitleDelivery: [new SubtitleCapability("srt", SubtitleDeliveryMethod.External)],
                SupportsHls: false,
                SupportsDash: false),
            OutputProfiles: []);

        var source = EngineTestFixtures.Source(
            "source-1",
            "mp4",
            videoStreams: [EngineTestFixtures.VideoStream(0, "h264")],
            audioStreams: [EngineTestFixtures.AudioStream(1, "aac", isDefault: true)],
            subtitleStreams: [EngineTestFixtures.SubtitleStream(2, "srt", isExternal: true)]);

        var engine = new PlaybackEngine();
        var decision = engine.Decide(
            EngineTestFixtures.Context(MediaKind.Video),
            capabilities,
            [source],
            EngineTestFixtures.Constraints(preferredSubtitleStreamIndex: 2));

        Assert.True(decision.IsViable);
        Assert.Equal(PlaybackMethod.DirectPlay, decision.Method);
        Assert.Empty(decision.Transforms);
        Assert.Equal(SubtitleDeliveryMethod.External, decision.SelectedStreams.Subtitle?.Delivery);
        Assert.DoesNotContain(TransformKind.ExtractSubtitle, decision.Transforms);
    }

    [Fact]
    public static void Decide_SubtitleUnsupportedFormat_BurnsInAndTranscodes()
    {
        var capabilities = new ClientCapabilities(
            Decode: new DecodeCapabilities(
                DirectPlayProfiles: [new DecodeProfile(MediaKind.Video, ["mp4"], ["h264"], ["aac"])],
                VideoCodecs: [new VideoCodecCapability("h264", [], null, null, [], null, null)],
                AudioCodecs: [new AudioCodecCapability("aac", null, null, null, null)],
                SubtitleDelivery: [new SubtitleCapability("srt", SubtitleDeliveryMethod.External)],
                SupportsHls: false,
                SupportsDash: false),
            OutputProfiles: []);

        var source = EngineTestFixtures.Source(
            "source-1",
            "mp4",
            videoStreams: [EngineTestFixtures.VideoStream(0, "h264")],
            audioStreams: [EngineTestFixtures.AudioStream(1, "aac", isDefault: true)],
            subtitleStreams: [EngineTestFixtures.SubtitleStream(2, "pgssub")]);

        var engine = new PlaybackEngine();
        var decision = engine.Decide(
            EngineTestFixtures.Context(MediaKind.Video),
            capabilities,
            [source],
            EngineTestFixtures.Constraints(preferredSubtitleStreamIndex: 2));

        Assert.True(decision.IsViable);
        Assert.Equal(PlaybackMethod.Transcode, decision.Method);
        Assert.Contains(TransformKind.BurnInSubtitle, decision.Transforms);
        Assert.Equal(SubtitleDeliveryMethod.Burn, decision.SelectedStreams.Subtitle?.Delivery);
        Assert.Contains(ReasonCode.SubtitleCodecNotSupported, FlattenReasonCodes(decision.Reasoning));
        Assert.Contains(ReasonCode.SubtitleBurnInRequired, FlattenReasonCodes(decision.Reasoning));
    }

    [Fact]
    public static void Decide_AlwaysBurnInWhenTranscoding_AddsBurnInForTextSubtitleOnUnrelatedVideoTranscode()
    {
        // Video already needs transcoding for an unrelated reason (codec not supported at all).
        // The subtitle itself would otherwise deliver cleanly as an External text format, but
        // AlwaysBurnInSubtitleWhenTranscoding overrides that once a video transcode is already
        // happening.
        var capabilities = new ClientCapabilities(
            Decode: new DecodeCapabilities(
                DirectPlayProfiles: [new DecodeProfile(MediaKind.Video, ["mp4"], ["h264"], ["aac"])],
                VideoCodecs: [new VideoCodecCapability("h264", [], null, null, [], null, null)],
                AudioCodecs: [new AudioCodecCapability("aac", null, null, null, null)],
                SubtitleDelivery: [new SubtitleCapability("srt", SubtitleDeliveryMethod.External)],
                SupportsHls: false,
                SupportsDash: false),
            OutputProfiles: []);

        var source = EngineTestFixtures.Source(
            "source-1",
            "mp4",
            videoStreams: [EngineTestFixtures.VideoStream(0, "av1")],
            audioStreams: [EngineTestFixtures.AudioStream(1, "aac", isDefault: true)],
            subtitleStreams: [EngineTestFixtures.SubtitleStream(2, "srt", isExternal: true)]);

        var engine = new PlaybackEngine();
        var decision = engine.Decide(
            EngineTestFixtures.Context(MediaKind.Video),
            capabilities,
            [source],
            EngineTestFixtures.Constraints(preferredSubtitleStreamIndex: 2, alwaysBurnInSubtitleWhenTranscoding: true));

        Assert.True(decision.IsViable);
        Assert.Equal(PlaybackMethod.Transcode, decision.Method);
        Assert.Contains(TransformKind.BurnInSubtitle, decision.Transforms);
        Assert.Equal(SubtitleDeliveryMethod.Burn, decision.SelectedStreams.Subtitle?.Delivery);
    }

    [Fact]
    public static void Decide_TranscodeDisallowedButNeeded_IsNotViable()
    {
        var capabilities = new ClientCapabilities(
            Decode: new DecodeCapabilities(
                DirectPlayProfiles: [new DecodeProfile(MediaKind.Video, ["mp4"], ["h264"], ["aac"])],
                VideoCodecs: [new VideoCodecCapability("h264", [], null, null, [], null, null)],
                AudioCodecs: [new AudioCodecCapability("aac", null, null, null, null)],
                SubtitleDelivery: [],
                SupportsHls: false,
                SupportsDash: false),
            OutputProfiles: []);

        // av1 is not decodable by the client at all, so a viable plan needs a video transcode -
        // but this request disallows transcoding outright.
        var source = EngineTestFixtures.Source(
            "source-1",
            "mp4",
            videoStreams: [EngineTestFixtures.VideoStream(0, "av1")],
            audioStreams: [EngineTestFixtures.AudioStream(1, "aac", isDefault: true)]);

        var engine = new PlaybackEngine();
        var decision = engine.Decide(
            EngineTestFixtures.Context(MediaKind.Video),
            capabilities,
            [source],
            EngineTestFixtures.Constraints(allowTranscoding: false));

        Assert.False(decision.IsViable);
        Assert.Equal(string.Empty, decision.SelectedSource);
        Assert.Contains(ReasonCode.VideoCodecNotSupported, FlattenReasonCodes(decision.Reasoning));
        Assert.Contains(ReasonCode.NoViablePlan, FlattenReasonCodes(decision.Reasoning));
    }

    [Fact]
    public static void Decide_VideoSourceWithNoStreamsAtAll_IsNotViable_DoesNotThrow()
    {
        // A MediaKind.Video source with empty VideoStreams and empty AudioStreams has nothing for
        // any method to select, copy, or transcode. Phase 1 fell through to NotViable here (the
        // videoOk/audioOk gates could never be satisfied); phase 2 must too, rather than crash
        // trying to build a StreamCopyable reason with no selected stream to reference.
        var capabilities = new ClientCapabilities(
            Decode: new DecodeCapabilities(
                DirectPlayProfiles: [new DecodeProfile(MediaKind.Video, ["mp4"], ["h264"], ["aac"])],
                VideoCodecs: [new VideoCodecCapability("h264", [], null, null, [], null, null)],
                AudioCodecs: [new AudioCodecCapability("aac", null, null, null, null)],
                SubtitleDelivery: [],
                SupportsHls: false,
                SupportsDash: false),
            OutputProfiles: []);

        var source = EngineTestFixtures.Source("source-1", "mkv");

        var engine = new PlaybackEngine();
        var decision = engine.Decide(EngineTestFixtures.Context(MediaKind.Video), capabilities, [source], EngineTestFixtures.Constraints());

        Assert.False(decision.IsViable);
        Assert.Equal(string.Empty, decision.SelectedSource);
    }

    [Fact]
    public static void Decide_ClientDeclaresAv1BeforeH264InOutputProfile_TranscodesToAv1()
    {
        // The PR98 oracle finding (RFC PR102): a client (Firefox) that lists AV1 ahead of H.264 in
        // its declared transcoding target order must be handed AV1 when the engine has to
        // transcode, not a hardcoded H.264 default. The source codec (vp9) is undecodable so a
        // video transcode is forced; the client's OutputProfile lists av1 before h264.
        var capabilities = new ClientCapabilities(
            Decode: new DecodeCapabilities(
                DirectPlayProfiles: [new DecodeProfile(MediaKind.Video, ["mp4"], ["h264"], ["aac"])],
                VideoCodecs: [new VideoCodecCapability("h264", [], null, null, [], null, null)],
                AudioCodecs: [new AudioCodecCapability("aac", null, null, null, null)],
                SubtitleDelivery: [],
                SupportsHls: true,
                SupportsDash: false),
            OutputProfiles:
            [
                new PlaybackOutputProfile(
                    Type: MediaKind.Video,
                    Protocol: StreamingProtocol.Hls,
                    Container: "mp4",
                    VideoCodecs: ["av1", "h264"],
                    AudioCodecs: ["aac"],
                    MaxVideoBitrate: null,
                    MaxAudioBitrate: null,
                    MaxAudioChannels: null),
            ]);

        var source = EngineTestFixtures.Source(
            "source-1",
            "mp4",
            videoStreams: [EngineTestFixtures.VideoStream(0, "vp9")],
            audioStreams: [EngineTestFixtures.AudioStream(1, "aac", isDefault: true)]);

        var engine = new PlaybackEngine();
        var decision = engine.Decide(EngineTestFixtures.Context(MediaKind.Video), capabilities, [source], EngineTestFixtures.Constraints());

        Assert.True(decision.IsViable);
        Assert.Equal(PlaybackMethod.Transcode, decision.Method);
        Assert.Equal("av1", decision.Output.VideoCodec);
        Assert.Equal("mp4", decision.Output.Container);
        Assert.DoesNotContain(ReasonCode.OutputProfileFallbackUsed, FlattenReasonCodes(decision.Reasoning));
    }

    [Fact]
    public static void Decide_NoOutputProfileDeclared_FallsBackToNamedLegacyDefault()
    {
        // A client declaring no PlaybackOutputProfile at all for the requested media kind must not
        // crash or silently transcode to nothing: the engine falls back to its named legacy
        // default (h264/aac) and records that fact with a dedicated ReasonCode, rather than an
        // inline magic-value substitution.
        var capabilities = new ClientCapabilities(
            Decode: new DecodeCapabilities(
                DirectPlayProfiles: [],
                VideoCodecs: [],
                AudioCodecs: [],
                SubtitleDelivery: [],
                SupportsHls: false,
                SupportsDash: false),
            OutputProfiles: []);

        var source = EngineTestFixtures.Source(
            "source-1",
            "mp4",
            videoStreams: [EngineTestFixtures.VideoStream(0, "vp9")],
            audioStreams: [EngineTestFixtures.AudioStream(1, "flac", isDefault: true)]);

        var engine = new PlaybackEngine();
        var decision = engine.Decide(EngineTestFixtures.Context(MediaKind.Video), capabilities, [source], EngineTestFixtures.Constraints());

        Assert.True(decision.IsViable);
        Assert.Equal(PlaybackMethod.Transcode, decision.Method);
        Assert.Equal("h264", decision.Output.VideoCodec);
        Assert.Equal("aac", decision.Output.AudioCodec);
        Assert.Contains(ReasonCode.OutputProfileFallbackUsed, FlattenReasonCodes(decision.Reasoning));
    }

    // --- PR102b acceptance tests ---

    [Fact]
    public static void Decide_UndeclaredContainerCodecCombo_IsNotDirectPlay()
    {
        // RFC PR102b problem #1: the client declares MP4/H.264 and, separately, WebM/VP9 as two
        // distinct DirectPlayProfiles. Both containers are accepted somewhere, and both codecs are
        // individually decodable (they appear in VideoCodecs), but the client never declared
        // MP4+VP9 together - the old flattened model would wrongly accept it as direct play.
        var capabilities = new ClientCapabilities(
            Decode: new DecodeCapabilities(
                DirectPlayProfiles:
                [
                    new DecodeProfile(MediaKind.Video, ["mp4"], ["h264"], ["aac"]),
                    new DecodeProfile(MediaKind.Video, ["webm"], ["vp9"], ["aac"]),
                ],
                VideoCodecs:
                [
                    new VideoCodecCapability("h264", [], null, null, [], null, null),
                    new VideoCodecCapability("vp9", [], null, null, [], null, null),
                ],
                AudioCodecs: [new AudioCodecCapability("aac", null, null, null, null)],
                SubtitleDelivery: [],
                SupportsHls: false,
                SupportsDash: false),
            OutputProfiles: []);

        var source = EngineTestFixtures.Source(
            "source-1",
            "mp4",
            videoStreams: [EngineTestFixtures.VideoStream(0, "vp9")],
            audioStreams: [EngineTestFixtures.AudioStream(1, "aac", isDefault: true)]);

        var engine = new PlaybackEngine();
        var decision = engine.Decide(EngineTestFixtures.Context(MediaKind.Video), capabilities, [source], EngineTestFixtures.Constraints());

        Assert.NotEqual(PlaybackMethod.DirectPlay, decision.Method);
    }

    [Fact]
    public static void Decide_PerCodecResolutionLimit_DoesNotLeakBetweenCodecs()
    {
        // RFC PR102b problem #2: H.264 is limited to 1080p, HEVC separately to 2160p. A 4K HEVC
        // stream must be judged against HEVC's own limit, not an artificial global minimum derived
        // from H.264's tighter one.
        var capabilities = new ClientCapabilities(
            Decode: new DecodeCapabilities(
                DirectPlayProfiles: [new DecodeProfile(MediaKind.Video, ["mp4"], ["h264", "hevc"], ["aac"])],
                VideoCodecs:
                [
                    new VideoCodecCapability("h264", [], null, null, [], MaxResolution: new Resolution(1920, 1080), null),
                    new VideoCodecCapability("hevc", [], null, null, [], MaxResolution: new Resolution(3840, 2160), null),
                ],
                AudioCodecs: [new AudioCodecCapability("aac", null, null, null, null)],
                SubtitleDelivery: [],
                SupportsHls: false,
                SupportsDash: false),
            OutputProfiles: []);

        var source = EngineTestFixtures.Source(
            "source-1",
            "mp4",
            videoStreams: [EngineTestFixtures.VideoStream(0, "hevc", width: 3840, height: 2160)],
            audioStreams: [EngineTestFixtures.AudioStream(1, "aac", isDefault: true)]);

        var engine = new PlaybackEngine();
        var decision = engine.Decide(EngineTestFixtures.Context(MediaKind.Video), capabilities, [source], EngineTestFixtures.Constraints());

        Assert.True(decision.IsViable);
        Assert.Equal(PlaybackMethod.DirectPlay, decision.Method);
        Assert.DoesNotContain(ReasonCode.VideoResolutionNotSupported, FlattenReasonCodes(decision.Reasoning));
    }

    [Fact]
    public static void Decide_AudioOnlyDecodeProfile_DoesNotAuthorizeVideoCombo()
    {
        // RFC PR102b problem #1/#3: a DecodeProfile of MediaKind.Audio must never authorize a video
        // combination even when the container and video codec each independently match one of its
        // fields - MediaKind.Type is a hard gate, not just another wildcard-able axis. The video
        // codec itself is kept decodable (present in VideoCodecs, no limit tripped) so a failure
        // here can only come from the profile-type mismatch, not from codec absence.
        var capabilities = new ClientCapabilities(
            Decode: new DecodeCapabilities(
                DirectPlayProfiles: [new DecodeProfile(MediaKind.Audio, ["mp4"], [], ["aac"])],
                VideoCodecs: [new VideoCodecCapability("h264", [], null, null, [], null, null)],
                AudioCodecs: [new AudioCodecCapability("aac", null, null, null, null)],
                SubtitleDelivery: [],
                SupportsHls: false,
                SupportsDash: false),
            OutputProfiles: []);

        var source = EngineTestFixtures.Source(
            "source-1",
            "mp4",
            videoStreams: [EngineTestFixtures.VideoStream(0, "h264")],
            audioStreams: [EngineTestFixtures.AudioStream(1, "aac", isDefault: true)]);

        var engine = new PlaybackEngine();
        var decision = engine.Decide(EngineTestFixtures.Context(MediaKind.Video), capabilities, [source], EngineTestFixtures.Constraints());

        Assert.NotEqual(PlaybackMethod.DirectPlay, decision.Method);
    }

    [Fact]
    public static void EngineVersion_IsEight()
    {
        // PR111e bumped 5->6: CSV-aware container comparisons, Direct Play container resolution,
        // and the HDR10-or-SDR tonemap-target policy are all decision-affecting changes to Decide.
        // Issue #70 bumped 6->7: a constraint-forbidden method now demotes to the next heavier
        // allowed method instead of vetoing the source.
        // Consolidation step 1 bumped 7->8: a remux is delivered through the declared output profile
        // that can carry the copied streams (PlaybackEngineRemuxDeliveryTests).
        Assert.Equal(8, PlaybackEngine.EngineVersion);
    }

    // ---------------------------------------------------------------------------------------
    // Issue #70: constraint-forbidden method DEMOTION.
    //
    // EngineTestFixtures.Constraints defaults allowDirectPlay to true and, before this suite, no
    // test in this project ever passed false - which is exactly why the defect below shipped
    // untested. A retry PUT carrying AllowDirectPlay:false over a still-directly-playable source
    // used to pick DirectPlay from the MEDIA alone, find it forbidden by the CONSTRAINTS, and treat
    // that as a hard veto (SourceCandidate.ForNotViable with an EMPTY blocking-reason list - nothing
    // is actually wrong with the media). Legacy StreamBuilder demotes the same input to Transcode
    // (StreamBuilder.cs:729-730) and answers 200; v2 answered NotViable, producing a V2PlanRecord
    // with a null ExecutionPlan and a PlanNotExecutable fallback to legacy at
    // PlaybackExecutionPlanResolver.cs:50-51.
    //
    // The rule these tests pin: a forbidden method DEMOTES to the next heavier ALLOWED method
    // (DirectPlay -> Remux -> Transcode). Never the reverse - AllowTranscoding/SupportsTranscoding
    // keep an absolute veto, because there is nothing heavier than Transcode to fall through to.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Issue #70, the defect itself: a source the client can direct-play, with Direct Play and
    /// Direct Stream both forbidden by the request's constraints (exactly the retry ladder
    /// reefin-web sends: EnableDirectPlay:false + EnableDirectStream:false for a local source),
    /// must demote to a viable Transcode - not answer NotViable.
    /// </summary>
    [Fact]
    public static void Decide_DirectPlayForbiddenByConstraints_DemotesToTranscode()
    {
        var decision = new PlaybackEngine().Decide(
            EngineTestFixtures.Context(MediaKind.Video),
            DemotionCapabilities(),
            [DirectlyPlayableSource()],
            EngineTestFixtures.Constraints(allowDirectPlay: false, allowDirectStream: false));

        Assert.True(decision.IsViable);
        Assert.Equal(PlaybackMethod.Transcode, decision.Method);
        Assert.Equal("shared-source", decision.SelectedSource);
        Assert.Contains(TransformKind.TranscodeVideo, decision.Transforms);
        Assert.Contains(TransformKind.TranscodeAudio, decision.Transforms);
        Assert.Equal("h264", decision.Output.VideoCodec);
        Assert.Equal("aac", decision.Output.AudioCodec);
        Assert.Equal("mp4", decision.Output.Container);
    }

    /// <summary>
    /// Demotion goes to the NEXT heavier allowed method, never straight to the heaviest: with
    /// Direct Play forbidden but Direct Stream still allowed, the streams are still copyable, so
    /// the answer is Remux, not Transcode. The client here declares both mkv and mp4 with these
    /// codecs, so the mkv source really can be remuxed into a different container.
    /// </summary>
    [Fact]
    public static void Decide_DirectPlayForbiddenButDirectStreamAllowed_DemotesToRemuxNotTranscode()
    {
        var capabilities = new ClientCapabilities(
            Decode: new DecodeCapabilities(
                DirectPlayProfiles: [new DecodeProfile(MediaKind.Video, ["mkv", "mp4"], ["h264"], ["aac"])],
                VideoCodecs: [new VideoCodecCapability("h264", [], null, null, [], null, null)],
                AudioCodecs: [new AudioCodecCapability("aac", null, null, null, null)],
                SubtitleDelivery: [],
                SupportsHls: false,
                SupportsDash: false),
            OutputProfiles: [new PlaybackOutputProfile(MediaKind.Video, StreamingProtocol.Http, "mp4", ["h264"], ["aac"], null, null, null)]);

        var decision = new PlaybackEngine().Decide(
            EngineTestFixtures.Context(MediaKind.Video),
            capabilities,
            [
                EngineTestFixtures.Source(
                    "shared-source",
                    "mkv",
                    videoStreams: [EngineTestFixtures.VideoStream(0, "h264")],
                    audioStreams: [EngineTestFixtures.AudioStream(1, "aac", isDefault: true, channels: 2)]),
            ],
            EngineTestFixtures.Constraints(allowDirectPlay: false));

        Assert.True(decision.IsViable);
        Assert.Equal(PlaybackMethod.Remux, decision.Method);
        Assert.Contains(TransformKind.RemuxContainer, decision.Transforms);
        Assert.Contains(TransformKind.CopyVideo, decision.Transforms);
        Assert.Contains(TransformKind.CopyAudio, decision.Transforms);
        Assert.DoesNotContain(TransformKind.TranscodeVideo, decision.Transforms);
    }

    /// <summary>
    /// The named limit of the Remux rung, pinned deliberately rather than discovered later: a Remux
    /// decision is defined by its container change (<c>PlaybackDecision.Validate</c> rejects a Remux
    /// carrying no <see cref="TransformKind.RemuxContainer"/>). When the client declares only the
    /// container the source already has, there is no different container to land in, so "remux"
    /// would be a no-op v2 cannot even express - demotion skips that rung and goes to Transcode,
    /// even though Direct Stream is nominally allowed.
    /// </summary>
    [Fact]
    public static void Decide_DirectPlayForbidden_NoOtherContainerAvailable_SkipsRemuxRung()
    {
        var decision = new PlaybackEngine().Decide(
            EngineTestFixtures.Context(MediaKind.Video),
            DemotionCapabilities(),
            [DirectlyPlayableSource()],
            EngineTestFixtures.Constraints(allowDirectPlay: false));

        Assert.True(decision.IsViable);
        Assert.Equal(PlaybackMethod.Transcode, decision.Method);
    }

    /// <summary>
    /// The absolute veto is preserved in both of its forms: with every method forbidden by the
    /// constraints there is nothing heavier to demote into, and a source that cannot be transcoded
    /// at all (<c>SupportsTranscoding:false</c>) cannot be rescued by demotion either. Demotion is
    /// never promotion, and never a licence to ignore a transcode veto.
    /// </summary>
    /// <param name="allowTranscoding">Whether the constraints permit transcoding.</param>
    /// <param name="supportsTranscoding">Whether the source itself can be transcoded.</param>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public static void Decide_TranscodeVetoed_DirectPlayForbidden_StaysNotViable(bool allowTranscoding, bool supportsTranscoding)
    {
        var decision = new PlaybackEngine().Decide(
            EngineTestFixtures.Context(MediaKind.Video),
            DemotionCapabilities(),
            [DirectlyPlayableSource(supportsTranscoding: supportsTranscoding)],
            EngineTestFixtures.Constraints(allowDirectPlay: false, allowDirectStream: false, allowTranscoding: allowTranscoding));

        Assert.False(decision.IsViable);
        Assert.Contains(ReasonCode.NoViablePlan, FlattenReasonCodes(decision.Reasoning));
    }

    /// <summary>
    /// Issue #70 PHASE 3 - the equality obligation. Demotion is only legitimate if the plan it
    /// produces is the SAME plan the engine already builds for a transcode it arrived at without
    /// any demotion at all. Both cases below run against the same client (same decode profile, same
    /// declared <see cref="PlaybackOutputProfile"/>) and the same source id, differing only in HOW
    /// the engine got to Transcode:
    /// <list type="bullet">
    /// <item>A: the MEDIA forces it - neither the video (hevc) nor the audio (ac3) codec is decodable.</item>
    /// <item>B: the CONSTRAINTS force it - the media (h264/aac/mp4) is directly playable, but the
    /// request forbids Direct Play and Direct Stream, so the engine demotes.</item>
    /// </list>
    /// Method, transforms, selected streams and the whole <see cref="OutputSpec"/> (target codecs,
    /// container, protocol, channels, every bitrate ceiling) must be identical. Reasoning is
    /// deliberately NOT compared: A legitimately reports VideoCodecNotSupported/AudioCodecNotSupported
    /// and B has nothing wrong with its media to report. The ExecutionPlan/StreamInfo half of this
    /// obligation is asserted in Tesserafin.Api.Tests (PlaybackSessionsControllerTests), the only test
    /// project that can see Tesserafin.Playback.Execution.
    /// </summary>
    [Fact]
    public static void Decide_ConstraintDemotedTranscode_EqualsMediaForcedTranscode()
    {
        var engine = new PlaybackEngine();
        var capabilities = DemotionCapabilities();

        var mediaForced = engine.Decide(
            EngineTestFixtures.Context(MediaKind.Video),
            capabilities,
            [
                EngineTestFixtures.Source(
                    "shared-source",
                    "mp4",
                    videoStreams: [EngineTestFixtures.VideoStream(0, "hevc")],
                    audioStreams: [EngineTestFixtures.AudioStream(1, "ac3", isDefault: true, channels: 2)]),
            ],
            EngineTestFixtures.Constraints());

        var constraintDemoted = engine.Decide(
            EngineTestFixtures.Context(MediaKind.Video),
            capabilities,
            [DirectlyPlayableSource()],
            EngineTestFixtures.Constraints(allowDirectPlay: false, allowDirectStream: false));

        Assert.True(mediaForced.IsViable);
        Assert.True(constraintDemoted.IsViable);
        Assert.Equal(PlaybackMethod.Transcode, mediaForced.Method);

        Assert.Equal(mediaForced.Method, constraintDemoted.Method);
        Assert.Equal(mediaForced.SelectedSource, constraintDemoted.SelectedSource);
        Assert.Equal(mediaForced.SelectedStreams, constraintDemoted.SelectedStreams);
        Assert.Equal(mediaForced.Transforms, constraintDemoted.Transforms);

        // OutputSpec is a record: this single assertion pins target video codec, target audio codec,
        // container, protocol, resolution, video range, audio channels and all three bitrate
        // ceilings field for field.
        Assert.Equal(mediaForced.Output, constraintDemoted.Output);
    }

    private static ClientCapabilities DemotionCapabilities() => new(
        Decode: new DecodeCapabilities(
            DirectPlayProfiles: [new DecodeProfile(MediaKind.Video, ["mp4"], ["h264"], ["aac"])],
            VideoCodecs: [new VideoCodecCapability("h264", [], null, null, [], null, null)],
            AudioCodecs: [new AudioCodecCapability("aac", null, null, null, null)],
            SubtitleDelivery: [],
            SupportsHls: false,
            SupportsDash: false),
        OutputProfiles: [new PlaybackOutputProfile(MediaKind.Video, StreamingProtocol.Http, "mp4", ["h264"], ["aac"], null, null, null)]);

    private static MediaSourceSnapshot DirectlyPlayableSource(bool supportsTranscoding = true) => EngineTestFixtures.Source(
        "shared-source",
        "mp4",
        videoStreams: [EngineTestFixtures.VideoStream(0, "h264")],
        audioStreams: [EngineTestFixtures.AudioStream(1, "aac", isDefault: true, channels: 2)],
        supportsTranscoding: supportsTranscoding);

    private static IEnumerable<ReasonCode> FlattenReasonCodes(ReasonNode node)
    {
        yield return node.Code;

        foreach (var child in node.Children)
        {
            foreach (var code in FlattenReasonCodes(child))
            {
                yield return code;
            }
        }
    }
}
