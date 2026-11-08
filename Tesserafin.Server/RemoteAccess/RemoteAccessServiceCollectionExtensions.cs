using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Tesserafin.Server.RemoteAccess;

/// <summary>
/// DI registration for the R2-L managed remote-access path.
/// </summary>
public static class RemoteAccessServiceCollectionExtensions
{
    /// <summary>
    /// Registers the managed Caddy lifecycle and its collaborators.
    /// </summary>
    /// <remarks>
    /// The fragment path is a fixed well-known location under the application paths.
    /// Concrete process runner and network config applier are left as extension points
    /// so the atomic sequence can be tested with fakes and later bound to production types.
    /// </remarks>
    public static IServiceCollection AddManagedRemoteAccess(this IServiceCollection services)
    {
        // Well-known fragment location. Production code will resolve this from ApplicationPaths.
        const string fragmentPath = "/etc/caddy/tesserafin.caddy";

        services.TryAddSingleton<IProcessRunner, SystemProcessRunner>();
        services.TryAddSingleton<IServerNetworkConfigApplier, ServerNetworkConfigApplier>();

        services.AddSingleton<IManagedCaddyService>(sp =>
        {
            var runner = sp.GetRequiredService<IProcessRunner>();
            var network = sp.GetRequiredService<IServerNetworkConfigApplier>();
            // Publication gate is optional; callers may wrap with PublicationInvariant + CredentialStrengthGate.
            return new ManagedCaddyService(fragmentPath, runner, network);
        });

        return services;
    }
}
