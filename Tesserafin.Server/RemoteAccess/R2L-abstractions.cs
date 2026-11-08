using System.Threading;
using System.Threading.Tasks;

namespace Tesserafin.Server.RemoteAccess;

/// <summary>
/// Runs external processes (caddy validate, systemctl reload, etc.).
/// </summary>
public interface IProcessRunner
{
    Task<(int ExitCode, string Output)> RunAsync(string fileName, string arguments, CancellationToken cancellationToken);
}

/// <summary>
/// Applies and restores the server's network configuration (KnownProxies, bind).
/// </summary>
public interface IServerNetworkConfigApplier
{
    Task<IConfigRestoreToken> ApplyAsync(string knownProxyAddress, string unixSocketPath, CancellationToken cancellationToken);
}

/// <summary>
/// Opaque token that restores the previous network configuration.
/// </summary>
public interface IConfigRestoreToken
{
    Task RestoreAsync(CancellationToken cancellationToken);
}
