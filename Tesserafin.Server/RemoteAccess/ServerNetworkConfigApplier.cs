using System;
using System.Threading;
using System.Threading.Tasks;

namespace Tesserafin.Server.RemoteAccess;

/// <summary>
/// Skeleton that records the intended network mutation and returns a restore token.
/// </summary>
/// <remarks>
/// Production code will write the Unix socket bind and KnownProxies through the real
/// IServerConfigurationManager and persist the previous values so RestoreAsync can
/// reverse them after a crash. This implementation is in-memory and process-local.
/// </remarks>
public sealed class ServerNetworkConfigApplier : IServerNetworkConfigApplier
{
    public Task<IConfigRestoreToken> ApplyAsync(
        string knownProxyAddress,
        string unixSocketPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(knownProxyAddress);
        ArgumentException.ThrowIfNullOrWhiteSpace(unixSocketPath);

        // In-memory snapshot of the previous values. Real implementation will load
        // the current NetworkConfiguration, persist it, then write the new values.
        var previous = new NetworkSnapshot(
            KnownProxies: Array.Empty<string>(),
            BindAddress: null);

        return Task.FromResult<IConfigRestoreToken>(new RestoreToken(previous));
    }

    private sealed record NetworkSnapshot(string[] KnownProxies, string? BindAddress);

    private sealed class RestoreToken : IConfigRestoreToken
    {
        private readonly NetworkSnapshot _previous;

        public RestoreToken(NetworkSnapshot previous) => _previous = previous;

        public Task RestoreAsync(CancellationToken cancellationToken)
        {
            // Real implementation will write _previous back through the config manager
            // and request a reload. This skeleton only proves the token exists and is awaitable.
            return Task.CompletedTask;
        }
    }
}
