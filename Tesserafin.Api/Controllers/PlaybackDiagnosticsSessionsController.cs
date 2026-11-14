using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Tesserafin.Api.Models.PlaybackSessionDtos;
using Tesserafin.Common.Api;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.MediaEncoding.Playback;
using Tesserafin.Playback.Shadow;

namespace Tesserafin.Api.Controllers;

/// <summary>
/// The admin-only playback diagnostics surface (docs/pr92-design-playback-api-and-diagnostics.md
/// §2): observes currently tracked playback sessions. Split out of the old
/// <c>System/PlaybackSessions</c> route, which mixed this admin listing with the client
/// create/replace/delete verbs under a single controller and route — this GET never shared the
/// client protocol's authorization scope or public, only its route. PR113: both routes now return
/// filtered projections (<see cref="PlaybackSessionListItem"/>, <see cref="PlaybackDiagnosticDetail"/>)
/// rather than the internal <see cref="PlaybackSession"/> record, closing the
/// <c>MediaSourceInfo.Path</c>/<c>OpenToken</c>/<c>TranscodingUrl</c> leak that returning it directly
/// would otherwise carry.
/// </summary>
[Route("System/PlaybackDiagnostics/Sessions")]
[Authorize(Policy = Policies.RequiresElevation)]
[Tags("System")]
public class PlaybackDiagnosticsSessionsController : BaseTesserafinApiController
{
    private readonly IPlaybackSessionManager _playbackSessionManager;
    private readonly IShadowDiagnosticsStore _diagnosticsStore;
    private readonly IPlaybackLiveWiringDiagnosticsStore _liveWiringDiagnosticsStore;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackDiagnosticsSessionsController"/> class.
    /// </summary>
    /// <param name="playbackSessionManager">Instance of the <see cref="IPlaybackSessionManager"/> interface.</param>
    /// <param name="diagnosticsStore">Instance of the <see cref="IShadowDiagnosticsStore"/> interface.</param>
    /// <param name="liveWiringDiagnosticsStore">
    /// PR115c: Instance of the <see cref="IPlaybackLiveWiringDiagnosticsStore"/> interface - surfaces
    /// the live streaming path's serve-v2-or-fallback decision on <see cref="GetPlaybackSession"/>.
    /// </param>
    public PlaybackDiagnosticsSessionsController(
        IPlaybackSessionManager playbackSessionManager,
        IShadowDiagnosticsStore diagnosticsStore,
        IPlaybackLiveWiringDiagnosticsStore liveWiringDiagnosticsStore)
    {
        _playbackSessionManager = playbackSessionManager;
        _diagnosticsStore = diagnosticsStore;
        _liveWiringDiagnosticsStore = liveWiringDiagnosticsStore;
    }

    /// <summary>
    /// Gets a snapshot of all currently tracked playback sessions.
    /// </summary>
    /// <response code="200">Playback sessions returned.</response>
    /// <returns>The current sessions, each flagged with whether a richer diagnostic is available.</returns>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<PlaybackSessionListItem>> GetPlaybackSessions()
    {
        IReadOnlyList<PlaybackSessionListItem> items = _playbackSessionManager.GetAll()
            .Select(session => new PlaybackSessionListItem(
                PlaybackSessionResponseMapper.Map(session),
                _diagnosticsStore.TryGet(session.Id, out _),
                session.Request?.Options.ItemId,
                session.Request?.Options.DeviceId))
            .ToList();

        return Ok(items);
    }

    /// <summary>
    /// Gets a single tracked playback session's diagnostic detail.
    /// </summary>
    /// <param name="id">The session to look up.</param>
    /// <response code="200">Session found; detail returned. The v2-sourced fields are populated only when a diagnostic was retained.</response>
    /// <response code="404">Session not found.</response>
    /// <returns>The filtered diagnostic detail.</returns>
    [HttpGet("{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<PlaybackDiagnosticDetail> GetPlaybackSession([FromRoute] PlaybackSessionId id)
    {
        var session = _playbackSessionManager.Get(id);
        if (session is null)
        {
            return NotFound();
        }

        _diagnosticsStore.TryGet(id, out var diagnostic);
        var events = _diagnosticsStore.GetEvents(id);
        _liveWiringDiagnosticsStore.TryGet(id, out var liveWiring);
        return Ok(PlaybackDiagnosticDetailMapper.Map(session, diagnostic, events, liveWiring));
    }

    /// <summary>
    /// PR113b: exports a session's retained shadow diagnostic as a playback-compatibility-lab
    /// fixture (docs/pr92-design-playback-api-and-diagnostics.md §5, "Exporter le cas de test"),
    /// conforming to tests/PlaybackCompat/schema/fixture.schema.json, so a real case observed in
    /// production can be dropped into tests/PlaybackCompat/fixtures/ and replayed by the lab.
    /// Serialized with <see cref="PlaybackCompatFixtureExporter.Options"/> (camelCase property
    /// names) directly, bypassing this API's normal PascalCase-by-default content negotiation
    /// (see <see cref="PlaybackCompatFixtureExporter"/> remarks) - the schema requires camelCase
    /// regardless of what an admin client's Accept header might otherwise negotiate.
    /// </summary>
    /// <param name="id">The session to export.</param>
    /// <response code="200">Fixture exported.</response>
    /// <response code="404">Session not found.</response>
    /// <response code="422">The session has no retained shadow diagnostic to export.</response>
    /// <returns>The fixture, as schema-conformant JSON.</returns>
    [HttpGet("{id}/Fixture")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public ActionResult ExportFixture([FromRoute] PlaybackSessionId id)
    {
        var session = _playbackSessionManager.Get(id);
        if (session is null)
        {
            return NotFound();
        }

        if (!_diagnosticsStore.TryGet(id, out var diagnostic) || diagnostic is null)
        {
            return UnprocessableEntity();
        }

        var export = PlaybackCompatFixtureExporter.Export(id, diagnostic);
        return Content(PlaybackCompatFixtureExporter.ToJson(export), "application/json");
    }
}
