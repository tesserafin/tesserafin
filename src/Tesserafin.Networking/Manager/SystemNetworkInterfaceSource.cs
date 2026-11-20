using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Tesserafin.Common.Net;
using Tesserafin.Model.Net;

namespace Tesserafin.Networking.Manager;

/// <summary>
/// Reads interface addresses from the operating system.
/// </summary>
public sealed class SystemNetworkInterfaceSource : INetworkInterfaceSource
{
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SystemNetworkInterfaceSource"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    public SystemNetworkInterfaceSource(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <inheritdoc/>
    public IReadOnlyList<IPData> GetInterfaces(bool includeIPv4, bool includeIPv6)
    {
        _logger.LogDebug("Refreshing interfaces.");

        var interfaces = new List<IPData>();

        try
        {
            var nics = NetworkInterface.GetAllNetworkInterfaces()
                .Where(i => i.OperationalStatus == OperationalStatus.Up);

            foreach (NetworkInterface adapter in nics)
            {
                try
                {
                    var ipProperties = adapter.GetIPProperties();
                    foreach (var info in ipProperties.UnicastAddresses)
                    {
                        if (includeIPv4 && info.Address.AddressFamily == AddressFamily.InterNetwork)
                        {
                            interfaces.Add(new IPData(info.Address, new IPNetwork(info.Address, info.PrefixLength), adapter.Name)
                            {
                                Index = ipProperties.GetIPv4Properties().Index,
                                Name = adapter.Name,
                                SupportsMulticast = adapter.SupportsMulticast
                            });
                        }
                        else if (includeIPv6 && info.Address.AddressFamily == AddressFamily.InterNetworkV6)
                        {
                            interfaces.Add(new IPData(info.Address, new IPNetwork(info.Address, info.PrefixLength), adapter.Name)
                            {
                                Index = ipProperties.GetIPv6Properties().Index,
                                Name = adapter.Name,
                                SupportsMulticast = adapter.SupportsMulticast
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Ignore error, and attempt to continue.
                    _logger.LogError(ex, "Error encountered parsing interfaces.");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error obtaining interfaces.");
        }

        if (interfaces.Count == 0)
        {
            _logger.LogWarning("No interface information available. Using loopback interface(s).");

            if (includeIPv4)
            {
                interfaces.Add(new IPData(IPAddress.Loopback, NetworkConstants.IPv4RFC5735Loopback, "lo"));
            }

            if (includeIPv6)
            {
                interfaces.Add(new IPData(IPAddress.IPv6Loopback, NetworkConstants.IPv6RFC4291Loopback, "lo"));
            }
        }

        _logger.LogDebug("Discovered {NumberOfInterfaces} interfaces.", interfaces.Count);
        _logger.LogDebug("Interfaces addresses: {Addresses}", interfaces.OrderByDescending(s => s.AddressFamily == AddressFamily.InterNetwork).Select(s => s.Address.ToString()));
        return interfaces;
    }
}
