using System;
using System.Threading;
using System.Threading.Tasks;

namespace Tesserafin.Server.RemoteAccess;

/// <summary>
/// Decorator that runs PublicationInvariant and CredentialStrengthGate before
/// delegating to the inner ManagedCaddyService.
/// </summary>
/// <remarks>
/// The gates are supplied as funcs so the controller or wizard can close over
/// the current observed facts and the administrator password without the service
/// knowing how they are obtained.
/// </remarks>
public sealed class GatedManagedCaddyService : IManagedCaddyService
{
    private readonly IManagedCaddyService _inner;
    private readonly Func<PublicationFacts> _factsProvider;
    private readonly Func<string?> _passwordProvider;

    public GatedManagedCaddyService(
        IManagedCaddyService inner,
        Func<PublicationFacts> factsProvider,
        Func<string?> passwordProvider)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _factsProvider = factsProvider ?? throw new ArgumentNullException(nameof(factsProvider));
        _passwordProvider = passwordProvider ?? throw new ArgumentNullException(nameof(passwordProvider));
    }

    public async Task<ApplyResult> ApplyAsync(ApplyRequest request, CancellationToken cancellationToken)
    {
        try
        {
            PublicationInvariant.Require(_factsProvider());
            CredentialStrengthGate.RequireForPublication(_passwordProvider());
        }
        catch (InvalidOperationException ex)
        {
            return new ApplyResult(false, ex.Message, null);
        }

        return await _inner.ApplyAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public Task<DisableResult> DisableAsync(CancellationToken cancellationToken)
        => _inner.DisableAsync(cancellationToken);

    public Task<ManagedState> GetStateAsync(CancellationToken cancellationToken)
        => _inner.GetStateAsync(cancellationToken);
}
