using System;
using System.Threading;
using System.Threading.Tasks;

namespace Tesserafin.Server.RemoteAccess;

/// <summary>
/// Managed Caddy lifecycle for .deb / .rpm (R2-L).
/// </summary>
/// <remarks>
/// Applies a Tesserafin-owned Caddyfile fragment, KnownProxies, and backend bind narrowing
/// as a single atomic transaction. Rolls back on any failure.
/// Does not ship a Caddy binary; expects Caddy to be installed as a system prerequisite (D-4).
/// Prefers a Unix domain socket for the backend (D-3).
/// </remarks>
public interface IManagedCaddyService
{
    Task<ApplyResult> ApplyAsync(ApplyRequest request, CancellationToken cancellationToken);
    Task<DisableResult> DisableAsync(CancellationToken cancellationToken);
    Task<ManagedState> GetStateAsync(CancellationToken cancellationToken);
}

public sealed record ApplyRequest(string Hostname, string UnixSocketPath, string KnownProxyAddress);
public sealed record ApplyResult(bool Succeeded, string? Error, ManagedState? ResultingState);
public sealed record DisableResult(bool Succeeded, string? Error);
public sealed record ManagedState(bool IsEnabled, string? Hostname, string? UnixSocketPath, string? FragmentPath, DateTimeOffset? LastAppliedAt);
