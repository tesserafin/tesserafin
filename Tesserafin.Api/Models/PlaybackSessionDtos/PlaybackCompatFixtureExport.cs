using Tesserafin.Playback.Shadow;

namespace Tesserafin.Api.Models.PlaybackSessionDtos;

/// <summary>
/// A playback-compatibility-lab fixture (PR93, schema v5: tests/PlaybackCompat/schema/fixture.schema.json),
/// built from a real, retained <see cref="ShadowDiagnosticRecord"/> by
/// <see cref="PlaybackCompatFixtureExporter"/> (PR113b), so a production case observed via the admin
/// diagnostics endpoint can be replayed as a fixture in tests/PlaybackCompat/fixtures/.
/// </summary>
/// <remarks>
/// Deliberately its own DTO tree rather than a direct serialization of the domain
/// <see cref="Tesserafin.Playback.Decision"/> records: the fixture schema's <c>constraints</c> object has
/// no <c>startTimeTicks</c> property, and its <c>expected.output</c> object has no
/// <c>subtitleFormat</c> property, both because those fields describe pipeline behavior the lab does
/// not model. Every other field here matches its domain counterpart name-for-name (only camelCased
/// by <see cref="PlaybackCompatFixtureExporter.Options"/> at serialization time) - see
/// <see cref="PlaybackCompatFixtureExporter"/> for the field-by-field derivation.
/// </remarks>
/// <param name="FixtureVersion">The fixture schema version this export targets. Always <c>5</c>.</param>
/// <param name="Id">A schema-conformant (<c>^[a-z0-9-]+$</c>) id derived from the source session id.</param>
/// <param name="Category">
/// A best-guess compatibility-lab category (see <see cref="PlaybackCompatFixtureExporter"/> remarks) -
/// a real production case does not self-classify into the lab's curated category vocabulary, so this
/// is a heuristic starting point for the admin to correct before adding the export to the curated
/// fixture catalog, not an authoritative classification.
/// </param>
/// <param name="EngineVersion">The v2 engine version that produced <see cref="Expected"/>.</param>
/// <param name="Description">A human-readable note on this export's provenance.</param>
/// <param name="Input">The v2 engine inputs the retained shadow run captured.</param>
/// <param name="Expected">The v2 engine's observed decision for those inputs.</param>
public sealed record PlaybackCompatFixtureExport(
    int FixtureVersion,
    string Id,
    string Category,
    int EngineVersion,
    string? Description,
    PlaybackCompatFixtureInput Input,
    PlaybackCompatFixtureExpected Expected);
