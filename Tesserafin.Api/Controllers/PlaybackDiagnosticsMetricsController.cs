using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Tesserafin.Api.Models.PlaybackSessionDtos;
using Tesserafin.Common.Api;
using Tesserafin.Controller.Configuration;
using Tesserafin.MediaEncoding.Playback;

namespace Tesserafin.Api.Controllers;

/// <summary>
/// PR115d: the admin-only operational gate surface for the v2 canary - separate from
/// <see cref="PlaybackDiagnosticsSessionsController"/> (which is scoped to individual tracked
/// sessions) because this exposes cumulative, cross-session counters instead: the served-by-v2/
/// fallback-by-reason/transcode-start-failure aggregate <see cref="PlaybackOperationalMetrics"/>
/// tracks, plus whether the operational stop-threshold guard is currently forcing legacy. This is
/// the endpoint the PR115d rollout runbook (docs/pr115d-rollout-runbook.md) tells an operator to
/// watch at every canary stage.
/// </summary>
[Route("System/PlaybackDiagnostics/Metrics")]
[Authorize(Policy = Policies.RequiresElevation)]
[Tags("System")]
public class PlaybackDiagnosticsMetricsController : BaseTesserafinApiController
{
    private readonly PlaybackOperationalMetrics _operationalMetrics;
    private readonly PlaybackStopThresholdGuard _stopThresholdGuard;
    private readonly IServerConfigurationManager _serverConfigurationManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackDiagnosticsMetricsController"/> class.
    /// </summary>
    /// <param name="operationalMetrics">Instance of the <see cref="PlaybackOperationalMetrics"/> singleton.</param>
    /// <param name="stopThresholdGuard">Instance of the <see cref="PlaybackStopThresholdGuard"/> singleton.</param>
    /// <param name="serverConfigurationManager">Instance of the <see cref="IServerConfigurationManager"/> interface.</param>
    public PlaybackDiagnosticsMetricsController(
        PlaybackOperationalMetrics operationalMetrics,
        PlaybackStopThresholdGuard stopThresholdGuard,
        IServerConfigurationManager serverConfigurationManager)
    {
        _operationalMetrics = operationalMetrics;
        _stopThresholdGuard = stopThresholdGuard;
        _serverConfigurationManager = serverConfigurationManager;
    }

    /// <summary>
    /// Gets the current cumulative operational metrics for the v2 live streaming path, plus the
    /// current stop-threshold guard state.
    /// </summary>
    /// <response code="200">Metrics returned.</response>
    /// <returns>The current metrics snapshot.</returns>
    [HttpGet]
    [System.Obsolete("Removed with the shadow comparison (tesserafin#129 step 7): nothing records these diagnostics any more.")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<PlaybackOperationalMetricsResponse> GetOperationalMetrics()
    {
        var snapshot = _operationalMetrics.GetSnapshot();
        var tripped = _stopThresholdGuard.Evaluate();
        var options = _serverConfigurationManager.Configuration.PlaybackShadow.StopThresholds;
        return Ok(PlaybackOperationalMetricsMapper.Map(snapshot, options, tripped));
    }
}
