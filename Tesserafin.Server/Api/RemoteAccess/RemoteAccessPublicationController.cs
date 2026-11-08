using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Tesserafin.Server.RemoteAccess;

namespace Tesserafin.Server.Api.RemoteAccess;

/// <summary>
/// Elevation-protected endpoints that drive the managed Caddy lifecycle (R2-L).
/// </summary>
/// <remarks>
/// Skeleton only. Real publication must call PublicationInvariant and
/// CredentialStrengthGate before ApplyAsync. This controller exists so the
/// DI graph and route shape can be reviewed independently of the full wizard.
/// </remarks>
[ApiController]
[Route("System/RemoteAccess")]
[Authorize(Policy = "RequiresElevation")]
public sealed class RemoteAccessPublicationController : ControllerBase
{
    private readonly IManagedCaddyService _caddy;

    public RemoteAccessPublicationController(IManagedCaddyService caddy)
    {
        _caddy = caddy;
    }

    /// <summary>
    /// Returns the current managed-publication state.
    /// </summary>
    [HttpGet("Publication")]
    public async Task<ActionResult<ManagedState>> GetState(CancellationToken cancellationToken)
    {
        var state = await _caddy.GetStateAsync(cancellationToken).ConfigureAwait(false);
        return Ok(state);
    }

    /// <summary>
    /// Applies a managed publication. Callers must already have satisfied the gates.
    /// </summary>
    [HttpPost("Publication")]
    public async Task<ActionResult<ApplyResult>> Apply(
        [FromBody] ApplyRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _caddy.ApplyAsync(request, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return BadRequest(result);
        }

        return Ok(result);
    }

    /// <summary>
    /// Disables the managed publication and reloads Caddy.
    /// </summary>
    [HttpDelete("Publication")]
    public async Task<ActionResult<DisableResult>> Disable(CancellationToken cancellationToken)
    {
        var result = await _caddy.DisableAsync(cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return BadRequest(result);
        }

        return Ok(result);
    }
}
