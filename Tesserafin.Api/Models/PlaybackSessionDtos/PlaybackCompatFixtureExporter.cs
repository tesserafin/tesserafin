using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.MediaEncoding.Playback;
using Tesserafin.Playback.Decision;
using Tesserafin.Playback.Shadow;

namespace Tesserafin.Api.Models.PlaybackSessionDtos;

/// <summary>
/// Builds a <see cref="PlaybackCompatFixtureExport"/> from a retained <see cref="ShadowDiagnosticRecord"/>
/// (PR113b), for the admin export endpoint (docs/pr92-design-playback-api-and-diagnostics.md §5,
/// "Exporter le cas de test"): "produit une fixture au format PR93 (source + capacités + contraintes
/// + décision attendue)". <c>expected</c> is therefore the v2 engine's own observed decision - the
/// same decision <see cref="PlaybackDiagnosticDetail.Reasoning"/>/<see cref="DiagnosticComparison"/>
/// already expose - never the legacy plan, which the fixture schema has no vocabulary for at all
/// (fixtures describe what the v2 engine should decide, compared against the engine at fixture-run
/// time, not against legacy).
/// </summary>
/// <remarks>
/// No secret-leak risk beyond what PR113 already closed for <see cref="PlaybackDiagnosticDetail"/>:
/// every field this pulls from <see cref="ShadowDiagnosticRecord.Sources"/>/<see cref="ShadowDiagnosticRecord.Capabilities"/>/
/// <see cref="ShadowDiagnosticRecord.Constraints"/>/<see cref="ShadowDiagnosticRecord.Decision"/> is
/// drawn from the <c>Tesserafin.Playback.Decision</c> domain vocabulary, which by construction carries no
/// file path, transcoding URL, session token, or API key (see <c>MediaSourceSnapshot</c>'s own
/// remarks: "the domain performs no I/O") - there is nothing here to mask that PR113's existing
/// <see cref="PlaybackDiagnosticDetailMapper"/> did not already keep out of the retained record in
/// the first place.
/// </remarks>
public static class PlaybackCompatFixtureExporter
{
    /// <summary>
    /// The current fixture schema version this exporter targets
    /// (tests/PlaybackCompat/schema/fixture.schema.json).
    /// </summary>
    public const int FixtureVersion = 5;

    /// <summary>
    /// Gets the serializer options that produce schema-conformant JSON: camelCase property names
    /// (the schema's convention for object keys) with PascalCase enum member names as string values
    /// (the schema's convention for enum values, for example <c>"method": "DirectPlay"</c>) - the
    /// opposite pairing from this API's own default response casing
    /// (<c>Tesserafin.Server.Extensions.ApiServiceCollectionExtensions</c>'s <c>JsonDefaults.PascalCaseOptions</c>),
    /// which is why the export endpoint serializes with this explicit options instance instead of
    /// going through the normal MVC content-negotiated output formatters.
    /// </summary>
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    /// <summary>
    /// Builds the fixture export for a session's retained diagnostic.
    /// </summary>
    /// <param name="id">The session the diagnostic was retained for, used to derive <see cref="PlaybackCompatFixtureExport.Id"/>.</param>
    /// <param name="diagnostic">The retained shadow diagnostic to export.</param>
    /// <returns>A fixture conforming to tests/PlaybackCompat/schema/fixture.schema.json.</returns>
    public static PlaybackCompatFixtureExport Export(PlaybackSessionId id, ShadowDiagnosticRecord diagnostic)
    {
        var decision = diagnostic.Decision;

        return new PlaybackCompatFixtureExport(
            FixtureVersion,
            $"exported-{id.Value:N}",
            DetermineCategory(decision),
            decision.EngineVersion,
            $"Exported from a retained production shadow diagnostic (session {id.Value:N}, captured {diagnostic.CapturedAt:O}). "
                + "'category' is a heuristic guess (see PlaybackCompatFixtureExporter remarks) - verify before committing to the curated fixture catalog.",
            new PlaybackCompatFixtureInput(
                new PlaybackCompatFixtureContext(diagnostic.Context.MediaKind),
                diagnostic.Capabilities,
                diagnostic.Sources,
                diagnostic.Context.MediaSourceId,
                MapConstraints(diagnostic.Constraints)),
            MapExpected(decision));
    }

    /// <summary>
    /// Serializes a fixture export to schema-conformant JSON text using <see cref="Options"/>.
    /// </summary>
    /// <param name="export">The export to serialize.</param>
    /// <returns>The serialized JSON.</returns>
    public static string ToJson(PlaybackCompatFixtureExport export) => JsonSerializer.Serialize(export, Options);

    /// <summary>
    /// PR93's curated fixture categories describe what compatibility problem a hand-authored
    /// fixture demonstrates; a real production case carries no such label. This maps the
    /// unambiguous cases (no viable plan, direct play, remux) directly, and falls back to
    /// <c>"audio-transcode"</c> for every transcode - a placeholder, not a claim that the export
    /// is actually an audio-transcode case. <see cref="PlaybackCompatFixtureExport.Category"/>'s
    /// own doc remarks this explicitly; the export endpoint never registers its output in
    /// <c>FixtureCatalog</c>, so nothing downstream depends on this guess being correct.
    /// </summary>
    private static string DetermineCategory(PlaybackDecision decision)
    {
        if (!decision.IsViable)
        {
            return "no-viable-plan";
        }

        return decision.Method switch
        {
            PlaybackMethod.DirectPlay => "direct-play",
            PlaybackMethod.Remux => "remux",
            _ => "audio-transcode",
        };
    }

    private static PlaybackCompatFixtureConstraints MapConstraints(PlaybackConstraints constraints) => new(
        constraints.AllowDirectPlay,
        constraints.AllowDirectStream,
        constraints.AllowTranscoding,
        constraints.AllowVideoStreamCopy,
        constraints.AllowAudioStreamCopy,
        constraints.MaxBitrate,
        constraints.MaxAudioChannels,
        constraints.PreferredAudioStreamIndex,
        constraints.PreferredSubtitleStreamIndex,
        constraints.SubtitleMode,
        constraints.PreferredSubtitleLanguages,
        constraints.AlwaysBurnInSubtitleWhenTranscoding);

    private static PlaybackCompatFixtureExpected MapExpected(PlaybackDecision decision) => new(
        decision.Method,
        new PlaybackCompatFixtureSelectedStreams(
            decision.SelectedStreams.Video,
            decision.SelectedStreams.Audio,
            decision.SelectedStreams.Subtitle?.Index),
        MapOutput(decision.Output),
        decision.Transforms.Select(static t => t.ToString()).ToList(),
        FlattenReasonCodes(decision.Reasoning).Select(static c => c.ToString()).Distinct().ToList(),
        decision.IsViable);

    private static PlaybackCompatFixtureOutput MapOutput(OutputSpec output) => new(
        output.Container,
        output.VideoCodec,
        output.AudioCodec,
        output.Resolution,
        output.VideoRange,
        output.AudioChannels,
        output.TotalBitrate,
        output.VideoBitrate,
        output.AudioBitrate,
        output.Protocol);

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
