using System.Collections.Generic;
using Tesserafin.Model.Net;

namespace Tesserafin.Networking.Manager;

/// <summary>
/// Supplies the host network interface addresses a <see cref="NetworkTopology"/> is built from.
/// </summary>
public interface INetworkInterfaceSource
{
    /// <summary>
    /// Reads the usable interface addresses of the host.
    /// </summary>
    /// <param name="includeIPv4">Whether IPv4 addresses are read.</param>
    /// <param name="includeIPv6">Whether IPv6 addresses are read.</param>
    /// <returns>The interface addresses, never empty when at least one family is enabled.</returns>
    IReadOnlyList<IPData> GetInterfaces(bool includeIPv4, bool includeIPv6);
}
