using System.ComponentModel.DataAnnotations;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Tesserafin.Server.Diagnostics.RemoteAccess;

namespace Tesserafin.Api.Controllers;

/// <summary>
/// Read-only remote-access diagnostics (R1-P).
/// </summary>
/// <remarks>
/// Returns the diagnostic report produced by <see cref="RemoteAccessDiagnosticCollector"/>.
/// No mutation. Authorization requires elevation (administrator).
/// Schema version is carried in the report itself.
/// </remarks>
[Route("System/RemoteAccess")]
[Authorize(Policy = Policies.RequiresElevation)]
[Tags("System")]
public class RemoteAccessDiagnosticsController : BaseTesserafinApiController
{
    private readonly RemoteAccessDiagnosticCollector _collector;

    /// <summary>
    /// Initializes a new instance of the <see cref="RemoteAccessDiagnosticsController"/> class.
    /// </summary>
    /// <param name="collector">The diagnostic collector.</param>
    public RemoteAccessDiagnosticsController(RemoteAccessDiagnosticCollector collector)
    {
        _collector = collector;
    }

    /// <summary>
    /// Collects a remote-access diagnostic report for a proposed hostname.
    /// </summary>
    /// <param name="hostname">Proposed public hostname (optional).</param>
    /// <param name="publishIpv4">Whether IPv4 publication is intended.</param>
    /// <param name="publishIpv6">Whether IPv6 publication is intended.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">Diagnostic report returned.</response>
    /// <returns>The diagnostic report.</returns>
    [HttpGet("Diagnostics")]
    [ProducesResponseType(typeof(RemoteAccessDiagnosticReport), StatusCodes.Status200OK)]
    public async Task<ActionResult<RemoteAccessDiagnosticReport>> GetDiagnostics(
        [FromQuery] string? hostname,
        [FromQuery] bool? publishIpv4,
        [FromQuery] bool? publishIpv6,
        CancellationToken cancellationToken)
    {
        var input = new PublicationPolicyInput(hostname, publishIpv4, publishIpv6);
        var report = await _collector.CollectAsync(input, cancellationToken).ConfigureAwait(false);
        return Ok(report);
    }
}
