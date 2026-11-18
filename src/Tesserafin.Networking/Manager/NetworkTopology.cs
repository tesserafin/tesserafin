using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Tesserafin.Common.Net;
using Tesserafin.Model.Net;

namespace Tesserafin.Networking.Manager;

/// <summary>
/// Immutable view of the server's network: filtered bind interfaces, LAN and excluded subnets,
/// the remote access filter and published server URL overrides, all derived from one
/// <see cref="NetworkConfiguration"/> and one interface discovery.
/// </summary>
/// <remarks>
/// Every decision about one request (LAN membership, remote access, bind address) reads a single
/// instance, so a concurrent configuration or interface change can never mix old and new values.
/// </remarks>
public sealed class NetworkTopology
{
    private readonly ILogger _logger;

    private NetworkTopology(
        ILogger logger,
        bool isIPv4Enabled,
        bool isIPv6Enabled,
        bool enableRemoteAccess,
        bool isRemoteIPFilterBlocklist,
        bool hasExplicitBindAddresses,
        IReadOnlyList<IPData> interfaces,
        IReadOnlyList<IPNetwork> lanSubnets,
        IReadOnlyList<IPNetwork> excludedSubnets,
        IReadOnlyList<IPNetwork> remoteAddressFilter,
        IReadOnlyList<PublishedServerUriOverride> publishedServerUrls)
    {
        _logger = logger;
        IsIPv4Enabled = isIPv4Enabled;
        IsIPv6Enabled = isIPv6Enabled;
        EnableRemoteAccess = enableRemoteAccess;
        IsRemoteIPFilterBlocklist = isRemoteIPFilterBlocklist;
        HasExplicitBindAddresses = hasExplicitBindAddresses;
        Interfaces = interfaces;
        LanSubnets = lanSubnets;
        ExcludedSubnets = excludedSubnets;
        RemoteAddressFilter = remoteAddressFilter;
        PublishedServerUrls = publishedServerUrls;
    }

    /// <summary>
    /// Gets a value indicating whether IPv4 is enabled.
    /// </summary>
    public bool IsIPv4Enabled { get; }

    /// <summary>
    /// Gets a value indicating whether IPv6 is enabled.
    /// </summary>
    public bool IsIPv6Enabled { get; }

    /// <summary>
    /// Gets a value indicating whether connections from outside the LAN may be accepted.
    /// </summary>
    public bool EnableRemoteAccess { get; }

    /// <summary>
    /// Gets a value indicating whether <see cref="RemoteAddressFilter"/> is a blocklist rather than an allowlist.
    /// </summary>
    public bool IsRemoteIPFilterBlocklist { get; }

    /// <summary>
    /// Gets a value indicating whether the configuration names explicit bind addresses.
    /// </summary>
    public bool HasExplicitBindAddresses { get; }

    /// <summary>
    /// Gets the usable interface addresses after bind, virtual-interface and address-family filtering.
    /// </summary>
    public IReadOnlyList<IPData> Interfaces { get; }

    /// <summary>
    /// Gets the LAN subnets: the configured ones, or the private and loopback ranges when none are configured.
    /// </summary>
    public IReadOnlyList<IPNetwork> LanSubnets { get; }

    /// <summary>
    /// Gets the subnets excluded from the LAN.
    /// </summary>
    public IReadOnlyList<IPNetwork> ExcludedSubnets { get; }

    /// <summary>
    /// Gets the remote address filter; empty means no filtering.
    /// </summary>
    public IReadOnlyList<IPNetwork> RemoteAddressFilter { get; }

    /// <summary>
    /// Gets the published server URL overrides.
    /// </summary>
    public IReadOnlyList<PublishedServerUriOverride> PublishedServerUrls { get; }

    /// <summary>
    /// Builds a topology from a configuration and a set of discovered interfaces.
    /// </summary>
    /// <param name="config">The network configuration.</param>
    /// <param name="startupPublishedServerUrl">The published server URL given at startup, which overrides every configured override.</param>
    /// <param name="discoveredInterfaces">The interfaces read from the host.</param>
    /// <param name="logger">The logger.</param>
    /// <returns>The topology.</returns>
    public static NetworkTopology Create(
        NetworkConfiguration config,
        string? startupPublishedServerUrl,
        IReadOnlyList<IPData> discoveredInterfaces,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(discoveredInterfaces);
        ArgumentNullException.ThrowIfNull(logger);

        var (lanSubnets, excludedSubnets) = ParseLan(config, logger);
        var interfaces = FilterBindSettings(config, discoveredInterfaces);
        return new NetworkTopology(
            logger,
            config.EnableIPv4,
            config.EnableIPv6,
            config.EnableRemoteAccess,
            config.IsRemoteIPFilterBlacklist,
            HasBindAddresses(config),
            interfaces,
            lanSubnets,
            excludedSubnets,
            ParseRemoteFilter(config),
            ParseOverrides(config, startupPublishedServerUrl, lanSubnets, interfaces, logger));
    }

    /// <summary>
    /// Computes the addresses Kestrel listens on, without building a full topology.
    /// </summary>
    /// <param name="config">The network configuration.</param>
    /// <param name="discoveredInterfaces">The interfaces read from the host.</param>
    /// <param name="logger">The logger.</param>
    /// <returns>The addresses to bind.</returns>
    public static IReadOnlyList<IPData> GetListenInterfaces(NetworkConfiguration config, IReadOnlyList<IPData> discoveredInterfaces, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(discoveredInterfaces);
        ArgumentNullException.ThrowIfNull(logger);

        return SelectListenInterfaces(
            HasBindAddresses(config),
            FilterBindSettings(config, discoveredInterfaces),
            config.EnableIPv4,
            config.EnableIPv6,
            individualInterfaces: false,
            logger);
    }

    /// <summary>
    /// Gets the addresses Kestrel listens on.
    /// </summary>
    /// <param name="individualInterfaces">Whether only known interfaces are returned, never a wildcard.</param>
    /// <returns>The addresses to bind.</returns>
    public IReadOnlyList<IPData> GetListenInterfaces(bool individualInterfaces)
        => SelectListenInterfaces(HasExplicitBindAddresses, Interfaces, IsIPv4Enabled, IsIPv6Enabled, individualInterfaces, _logger);

    /// <summary>
    /// Gets the loopback addresses of the enabled address families.
    /// </summary>
    /// <returns>The loopback addresses.</returns>
    public IReadOnlyList<IPData> GetLoopbacks()
    {
        var loopbacks = new List<IPData>(2);
        if (IsIPv4Enabled)
        {
            loopbacks.Add(new IPData(IPAddress.Loopback, NetworkConstants.IPv4RFC5735Loopback, "lo"));
        }

        if (IsIPv6Enabled)
        {
            loopbacks.Add(new IPData(IPAddress.IPv6Loopback, NetworkConstants.IPv6RFC4291Loopback, "lo"));
        }

        return loopbacks;
    }

    /// <summary>
    /// Gets the interface addresses inside the LAN, ordered by interface index.
    /// </summary>
    /// <returns>The internal bind addresses.</returns>
    public IReadOnlyList<IPData> GetInternalBindAddresses()
        => Interfaces.Where(x => IsInLocalNetwork(x.Address)).OrderBy(x => x.Index).ToList();

    /// <summary>
    /// Finds the interface addresses of the named interface.
    /// </summary>
    /// <param name="name">The interface name.</param>
    /// <param name="result">The matching addresses, ordered by interface index.</param>
    /// <returns><c>true</c> if at least one address matches.</returns>
    public bool TryParseInterface(string name, [NotNullWhen(true)] out IReadOnlyList<IPData>? result)
        => TryParseInterface(Interfaces, IsIPv4Enabled, IsIPv6Enabled, name, out result);

    /// <summary>
    /// Determines whether an address belongs to the LAN.
    /// </summary>
    /// <param name="address">The address.</param>
    /// <returns><c>true</c> for loopback and for addresses in a LAN subnet but in no excluded subnet.</returns>
    public bool IsInLocalNetwork(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        // Map IPv6 mapped IPv4 back to IPv4 (happens if Kestrel runs in dual-socket mode)
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        foreach (var lanSubnet in LanSubnets)
        {
            if (lanSubnet.Contains(address))
            {
                foreach (var excludedSubnet in ExcludedSubnets)
                {
                    if (excludedSubnet.Contains(address))
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Decides whether a remote address may reach the server.
    /// </summary>
    /// <param name="remoteIP">The remote address.</param>
    /// <returns>The decision.</returns>
    public RemoteAccessPolicyResult ShouldAllowServerAccess(IPAddress remoteIP)
    {
        if (IsInLocalNetwork(remoteIP))
        {
            return RemoteAccessPolicyResult.Allow;
        }

        if (!EnableRemoteAccess)
        {
            return RemoteAccessPolicyResult.RejectDueToRemoteAccessDisabled;
        }

        if (RemoteAddressFilter.Count == 0)
        {
            return RemoteAccessPolicyResult.Allow;
        }

        var anyMatches = RemoteAddressFilter.Any(remoteNetwork => NetworkUtils.SubnetContainsAddress(remoteNetwork, remoteIP));
        if (IsRemoteIPFilterBlocklist)
        {
            return anyMatches ? RemoteAccessPolicyResult.RejectDueToIPBlocklist : RemoteAccessPolicyResult.Allow;
        }

        return anyMatches ? RemoteAccessPolicyResult.Allow : RemoteAccessPolicyResult.RejectDueToNotAllowlistedRemoteIP;
    }

    /// <summary>
    /// Selects the address advertised to a client.
    /// </summary>
    /// <param name="source">The client address, or <c>null</c> for the preferred local address.</param>
    /// <param name="port">The port named by a matching published server URL override, otherwise <c>null</c>.</param>
    /// <param name="skipOverrides">Whether published server URL overrides are ignored.</param>
    /// <returns>A host, an IP address or a published URL.</returns>
    public string GetBindAddress(IPAddress? source, out int? port, bool skipOverrides = false)
    {
        port = null;

        string result;
        if (source is not null)
        {
            if (IsIPv4Enabled && !IsIPv6Enabled && source.AddressFamily == AddressFamily.InterNetworkV6)
            {
                _logger.LogWarning("IPv6 is disabled in Tesserafin, but enabled in the OS. This may affect how the interface is selected.");
            }

            if (!IsIPv4Enabled && IsIPv6Enabled && source.AddressFamily == AddressFamily.InterNetwork)
            {
                _logger.LogWarning("IPv4 is disabled in Tesserafin, but enabled in the OS. This may affect how the interface is selected.");
            }

            bool isExternal = !IsInLocalNetwork(source);
            _logger.LogDebug("Trying to get bind address for source {Source} - External: {IsExternal}", source, isExternal);

            if (!skipOverrides && MatchesPublishedServerUrl(source, isExternal, out result, out port))
            {
                return result;
            }

            if (MatchesBindInterface(source, isExternal, out result))
            {
                return result;
            }

            if (isExternal && MatchesExternalInterface(source, out result))
            {
                return result;
            }
        }

        // Get the first LAN interface address that's not excluded and not a loopback address.
        var availableInterfaces = Interfaces.Where(x => !IPAddress.IsLoopback(x.Address))
            .OrderByDescending(x => IsInLocalNetwork(x.Address))
            .ThenBy(x => x.Index)
            .ToList();

        if (availableInterfaces.Count == 0)
        {
            // Prefer the loopback address matching the source's address family.
            if (source is not null && source.AddressFamily == AddressFamily.InterNetwork && IsIPv4Enabled)
            {
                result = "127.0.0.1";
            }
            else if (source is not null && source.AddressFamily == AddressFamily.InterNetworkV6 && IsIPv6Enabled)
            {
                result = "::1";
            }
            else
            {
                result = IsIPv4Enabled ? "127.0.0.1" : "::1";
            }

            _logger.LogWarning("{Source}: Only loopback {Result} returned, using that as bind address.", source, result);
            return result;
        }

        if (source is null)
        {
            result = NetworkUtils.FormatIPString(availableInterfaces[0].Address);
            _logger.LogDebug("{Source}: Using first internal interface as bind address: {Result}", source, result);
            return result;
        }

        // Does the request originate in one of the interface subnets?
        foreach (var intf in availableInterfaces)
        {
            if (NetworkUtils.SubnetContainsAddress(intf.Subnet, source))
            {
                result = NetworkUtils.FormatIPString(intf.Address);
                _logger.LogDebug("{Source}: Found interface with matching subnet, using it as bind address: {Result}", source, result);
                return result;
            }
        }

        var preferredInterface = availableInterfaces.FirstOrDefault(x => x.Address.AddressFamily == source.AddressFamily);
        if (preferredInterface is not null)
        {
            result = NetworkUtils.FormatIPString(preferredInterface.Address);
            _logger.LogDebug("{Source}: No matching subnet found, using interface with matching address family: {Result}", source, result);
            return result;
        }

        result = NetworkUtils.FormatIPString(availableInterfaces[0].Address);
        _logger.LogDebug("{Source}: No matching interfaces found, using first available interface as bind address: {Result}", source, result);
        return result;
    }

    /// <summary>
    /// Writes the topology to the log.
    /// </summary>
    /// <param name="logLevel">The level to log at.</param>
    public void Log(LogLevel logLevel)
    {
        if (!_logger.IsEnabled(logLevel))
        {
            return;
        }

        _logger.Log(logLevel, "Defined LAN subnets: {Subnets}", LanSubnets.Select(s => s.BaseAddress + "/" + s.PrefixLength));
        _logger.Log(logLevel, "Defined LAN exclusions: {Subnets}", ExcludedSubnets.Select(s => s.BaseAddress + "/" + s.PrefixLength));
        _logger.Log(logLevel, "Used LAN subnets: {Subnets}", LanSubnets.Where(s => !ExcludedSubnets.Contains(s)).Select(s => s.BaseAddress + "/" + s.PrefixLength));
        _logger.Log(logLevel, "Filtered interface addresses: {Addresses}", Interfaces.OrderByDescending(x => x.AddressFamily == AddressFamily.InterNetwork).Select(x => x.Address));
        _logger.Log(logLevel, "Bind Addresses {Addresses}", GetListenInterfaces(false).OrderByDescending(x => x.AddressFamily == AddressFamily.InterNetwork).Select(x => x.Address));
        _logger.Log(logLevel, "Remote IP filter is {Type}", IsRemoteIPFilterBlocklist ? "Blocklist" : "Allowlist");
        _logger.Log(logLevel, "Filtered subnets: {Subnets}", RemoteAddressFilter.Select(s => s.BaseAddress + "/" + s.PrefixLength));
    }

    private static bool HasBindAddresses(NetworkConfiguration config)
        => config.LocalNetworkAddresses.Length > 0 && !string.IsNullOrWhiteSpace(config.LocalNetworkAddresses[0]);

    private static bool TryParseInterface(
        IReadOnlyList<IPData> interfaces,
        bool isIPv4Enabled,
        bool isIPv6Enabled,
        string name,
        [NotNullWhen(true)] out IReadOnlyList<IPData>? result)
    {
        if (string.IsNullOrEmpty(name) || interfaces.Count == 0)
        {
            result = null;
            return false;
        }

        result = interfaces
            .Where(i => i.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                        && ((isIPv4Enabled && i.Address.AddressFamily == AddressFamily.InterNetwork)
                            || (isIPv6Enabled && i.Address.AddressFamily == AddressFamily.InterNetworkV6)))
            .OrderBy(x => x.Index)
            .ToArray();
        return result.Count > 0;
    }

    private static (IReadOnlyList<IPNetwork> Lan, IReadOnlyList<IPNetwork> Excluded) ParseLan(NetworkConfiguration config, ILogger logger)
    {
        var subnets = config.LocalNetworkSubnets;

        IReadOnlyList<IPNetwork> lan;
        if (!NetworkUtils.TryParseToSubnets(subnets, out var lanSubnets, false, logger) || lanSubnets.Count == 0)
        {
            logger.LogDebug("Using LAN interface addresses as user provided no LAN details.");

            var fallback = new List<IPNetwork>();
            if (config.EnableIPv6)
            {
                fallback.Add(NetworkConstants.IPv6RFC4291Loopback);
                fallback.Add(NetworkConstants.IPv6RFC4291SiteLocal);
                fallback.Add(NetworkConstants.IPv6RFC4193UniqueLocal);
            }

            if (config.EnableIPv4)
            {
                fallback.Add(NetworkConstants.IPv4RFC5735Loopback);
                fallback.Add(NetworkConstants.IPv4RFC1918PrivateClassA);
                fallback.Add(NetworkConstants.IPv4RFC1918PrivateClassB);
                fallback.Add(NetworkConstants.IPv4RFC1918PrivateClassC);
            }

            lan = fallback;
        }
        else
        {
            lan = lanSubnets.Select(x => x.Subnet).ToArray();
        }

        IReadOnlyList<IPNetwork> excluded = NetworkUtils.TryParseToSubnets(subnets, out var excludedSubnets, true, logger)
            ? excludedSubnets.Select(x => x.Subnet).ToArray()
            : Array.Empty<IPNetwork>();

        return (lan, excluded);
    }

    private static IReadOnlyList<IPNetwork> ParseRemoteFilter(NetworkConfiguration config)
    {
        var remoteIPFilter = config.RemoteIPFilter;
        if (remoteIPFilter.Length == 0 || string.IsNullOrWhiteSpace(remoteIPFilter[0]))
        {
            return Array.Empty<IPNetwork>();
        }

        var filter = new List<IPNetwork>();
        var subnets = remoteIPFilter.Where(x => x.Contains('/', StringComparison.Ordinal)).ToArray();
        if (NetworkUtils.TryParseToSubnets(subnets, out var parsedSubnets, false))
        {
            filter.AddRange(parsedSubnets.Select(x => x.Subnet));
        }

        foreach (var ip in remoteIPFilter.Where(x => !x.Contains('/', StringComparison.Ordinal)))
        {
            if (IPAddress.TryParse(ip, out var address))
            {
                filter.Add(new IPNetwork(address, address.AddressFamily == AddressFamily.InterNetwork ? NetworkConstants.MinimumIPv4PrefixSize : NetworkConstants.MinimumIPv6PrefixSize));
            }
        }

        return filter;
    }

    private static IReadOnlyList<IPData> FilterBindSettings(NetworkConfiguration config, IReadOnlyList<IPData> discovered)
    {
        var interfaces = discovered.ToList();

        // Respect explicit bind addresses
        if (HasBindAddresses(config))
        {
            var bindAddresses = config.LocalNetworkAddresses.Select(p => NetworkUtils.TryParseToSubnet(p, out var network)
                    ? network.Address
                    : (interfaces.Where(x => x.Name.Equals(p, StringComparison.OrdinalIgnoreCase))
                        .Select(x => x.Address)
                        .FirstOrDefault() ?? IPAddress.None))
                .Where(x => x != IPAddress.None)
                .ToHashSet();
            interfaces = interfaces.Where(x => bindAddresses.Contains(x.Address)).ToList();

            if (bindAddresses.Contains(IPAddress.Loopback) && !interfaces.Any(i => i.Address.Equals(IPAddress.Loopback)))
            {
                interfaces.Add(new IPData(IPAddress.Loopback, NetworkConstants.IPv4RFC5735Loopback, "lo"));
            }

            if (bindAddresses.Contains(IPAddress.IPv6Loopback) && !interfaces.Any(i => i.Address.Equals(IPAddress.IPv6Loopback)))
            {
                interfaces.Add(new IPData(IPAddress.IPv6Loopback, NetworkConstants.IPv6RFC4291Loopback, "lo"));
            }
        }

        // Remove all interfaces matching any virtual machine interface prefix
        if (config.IgnoreVirtualInterfaces)
        {
            foreach (var prefix in config.VirtualInterfaceNames.Select(i => i.Replace("*", string.Empty, StringComparison.Ordinal)))
            {
                interfaces.RemoveAll(x => x.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            }
        }

        if (!config.EnableIPv4)
        {
            interfaces.RemoveAll(x => x.AddressFamily == AddressFamily.InterNetwork);
        }

        if (!config.EnableIPv6)
        {
            interfaces.RemoveAll(x => x.AddressFamily == AddressFamily.InterNetworkV6);
        }

        // Multiple interfaces may share an address; bind it once and let the OS route.
        return interfaces.DistinctBy(iface => iface.Address).ToArray();
    }

    private static IReadOnlyList<IPData> SelectListenInterfaces(
        bool hasExplicitBindAddresses,
        IReadOnlyList<IPData> knownInterfaces,
        bool readIpv4,
        bool readIpv6,
        bool individualInterfaces,
        ILogger logger)
    {
        if ((hasExplicitBindAddresses && knownInterfaces.Count > 0) || individualInterfaces)
        {
            return knownInterfaces;
        }

        if (readIpv6 && !Socket.OSSupportsIPv6)
        {
            logger.LogWarning("IPv6 Unsupported by OS, not listening on IPv6");
            readIpv6 = false;
        }

        // No bind address and no exclusions, so listen on all interfaces.
        var result = new List<IPData>();
        if (readIpv4 && readIpv6)
        {
            // Kestrel uses Sockets.DualMode, so this also covers IPAddress.Any.
            result.Add(new IPData(IPAddress.IPv6Any, NetworkConstants.IPv6Any));
        }
        else if (readIpv4)
        {
            result.Add(new IPData(IPAddress.Any, NetworkConstants.IPv4Any));
        }
        else if (readIpv6)
        {
            // Cannot use IPv6Any as Kestrel would bind to IPv4 addresses too.
            result.AddRange(knownInterfaces.Where(iface => iface.AddressFamily == AddressFamily.InterNetworkV6));
        }

        return result;
    }

    /// <summary>
    /// Parses published server URL overrides. Format is <c>subnet=ipaddress|host|uri</c>, where
    /// subnet may also be <c>all</c>, <c>external</c>, <c>internal</c> or an interface name.
    /// </summary>
    private static IReadOnlyList<PublishedServerUriOverride> ParseOverrides(
        NetworkConfiguration config,
        string? startupPublishedServerUrl,
        IReadOnlyList<IPNetwork> lanSubnets,
        IReadOnlyList<IPData> interfaces,
        ILogger logger)
    {
        if (!string.IsNullOrEmpty(startupPublishedServerUrl))
        {
            return AnyAddress(startupPublishedServerUrl, isInternal: true);
        }

        var overrides = new List<PublishedServerUriOverride>();
        foreach (var entry in config.PublishedServerUriBySubnet)
        {
            var parts = entry.Split('=');
            if (parts.Length != 2)
            {
                logger.LogError("Unable to parse bind override: {Entry}", entry);
                continue;
            }

            var identifier = parts[0];
            var replacement = parts[1].Trim();
            if (string.Equals(identifier, "all", StringComparison.OrdinalIgnoreCase))
            {
                // An "all" override replaces every other override.
                return AnyAddress(replacement, isInternal: true);
            }

            if (string.Equals(identifier, "external", StringComparison.OrdinalIgnoreCase))
            {
                overrides.AddRange(AnyAddress(replacement, isInternal: false));
            }
            else if (string.Equals(identifier, "internal", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var lan in lanSubnets)
                {
                    var lanPrefix = lan.BaseAddress;
                    overrides.Add(new PublishedServerUriOverride(new IPData(lanPrefix, new IPNetwork(lanPrefix, lan.PrefixLength)), replacement, true, false));
                }
            }
            else if (NetworkUtils.TryParseToSubnet(identifier, out var subnet))
            {
                overrides.Add(new PublishedServerUriOverride(subnet, replacement, true, true));
            }
            else if (TryParseInterface(interfaces, config.EnableIPv4, config.EnableIPv6, identifier, out var ifaces))
            {
                overrides.AddRange(ifaces.Select(iface => new PublishedServerUriOverride(iface, replacement, true, true)));
            }
            else
            {
                logger.LogError("Unable to parse bind override: {Entry}", entry);
            }
        }

        return overrides;
    }

    private static PublishedServerUriOverride[] AnyAddress(string replacement, bool isInternal)
        =>
        [
            new PublishedServerUriOverride(new IPData(IPAddress.Any, NetworkConstants.IPv4Any), replacement, isInternal, true),
            new PublishedServerUriOverride(new IPData(IPAddress.IPv6Any, NetworkConstants.IPv6Any), replacement, isInternal, true)
        ];

    private bool MatchesPublishedServerUrl(IPAddress source, bool isExternal, out string bindPreference, out int? port)
    {
        bindPreference = string.Empty;
        port = null;

        // Only consider overrides for the source's side whose subnet contains it, most specific first.
        var candidates = PublishedServerUrls
            .Where(x => (isExternal ? x.IsExternalOverride : x.IsInternalOverride) && NetworkUtils.SubnetContainsAddress(x.Data.Subnet, source))
            .OrderByDescending(x => x.Data.Subnet.PrefixLength);

        foreach (var data in candidates)
        {
            var intf = Interfaces.OrderBy(x => x.Index).FirstOrDefault(x => NetworkUtils.SubnetContainsAddress(data.Data.Subnet, x.Address));
            if (intf?.Address is not null
                || (data.Data.AddressFamily == AddressFamily.InterNetwork && data.Data.Address.Equals(IPAddress.Any))
                || (data.Data.AddressFamily == AddressFamily.InterNetworkV6 && data.Data.Address.Equals(IPAddress.IPv6Any)))
            {
                bindPreference = data.OverrideUri;
                break;
            }
        }

        if (string.IsNullOrEmpty(bindPreference))
        {
            _logger.LogDebug("{Source}: No matching bind address override found", source);
            return false;
        }

        // "host:port" names the advertised port; URIs and IPv6 literals are returned unchanged.
        var parts = bindPreference.Split(':');
        if (parts.Length == 2 && int.TryParse(parts[1], out var overridePort))
        {
            bindPreference = parts[0];
            port = overridePort;
            _logger.LogDebug("{Source}: Matching bind address override found: {Address}:{Port}", source, bindPreference, port);
            return true;
        }

        _logger.LogDebug("{Source}: Matching bind address override found: {Address}", source, bindPreference);
        return true;
    }

    private bool MatchesBindInterface(IPAddress source, bool isExternal, out string result)
    {
        result = string.Empty;

        int count = Interfaces.Count;
        if (count == 1 && (Interfaces[0].Address.Equals(IPAddress.Any) || Interfaces[0].Address.Equals(IPAddress.IPv6Any)))
        {
            // Ignore IPAny addresses.
            count = 0;
        }

        if (count == 0)
        {
            return false;
        }

        if (isExternal)
        {
            var externalInterfaces = Interfaces.Where(x => !IsInLocalNetwork(x.Address) && !IsLinkLocal(x.Address)).ToList();
            if (externalInterfaces.Count > 0)
            {
                // Prefer an external bind interface in the source's subnet, else the first one.
                var address = externalInterfaces
                    .OrderByDescending(x => NetworkUtils.SubnetContainsAddress(x.Subnet, source))
                    .ThenByDescending(x => x.Subnet.PrefixLength)
                    .ThenBy(x => x.Index)
                    .Select(x => x.Address)
                    .First();

                result = NetworkUtils.FormatIPString(address);
                _logger.LogDebug("{Source}: External request received, matching external bind address found: {Result}", source, result);
                return true;
            }

            _logger.LogDebug("{Source}: External request received, no matching external bind address found, trying internal addresses", source);
            return false;
        }

        // Prefer an internal bind interface in the source's subnet, else the first one.
        var bindAddress = Interfaces.Where(x => IsInLocalNetwork(x.Address))
            .OrderByDescending(x => NetworkUtils.SubnetContainsAddress(x.Subnet, source))
            .ThenByDescending(x => x.Subnet.PrefixLength)
            .ThenBy(x => x.Index)
            .Select(x => x.Address)
            .FirstOrDefault();

        if (bindAddress is null)
        {
            return false;
        }

        result = NetworkUtils.FormatIPString(bindAddress);
        _logger.LogDebug("{Source}: Internal request received, matching internal bind address found: {Result}", source, result);
        return true;
    }

    private bool MatchesExternalInterface(IPAddress source, out string result)
    {
        var external = Interfaces
            .Where(p => !IsInLocalNetwork(p.Address) && p.Address.AddressFamily == source.AddressFamily && !IsLinkLocal(p.Address))
            .OrderBy(x => x.Index)
            .ToArray();

        if (external.Length == 0)
        {
            result = string.Empty;
            _logger.LogDebug("{Source}: External request received, but no external interface found. Need to route through internal network", source);
            return false;
        }

        foreach (var intf in external)
        {
            if (NetworkUtils.SubnetContainsAddress(intf.Subnet, source))
            {
                result = NetworkUtils.FormatIPString(intf.Address);
                _logger.LogDebug("{Source}: Found external interface with matching subnet, using it as bind address: {Result}", source, result);
                return true;
            }
        }

        result = NetworkUtils.FormatIPString(external[0].Address);
        _logger.LogDebug("{Source}: Using first external interface as bind address: {Result}", source, result);
        return true;
    }

    private static bool IsLinkLocal(IPAddress address)
        => NetworkConstants.IPv4RFC3927LinkLocal.Contains(address) || address.IsIPv6LinkLocal;
}
