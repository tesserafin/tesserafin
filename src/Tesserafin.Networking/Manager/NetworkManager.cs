using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Tesserafin.Common.Configuration;
using Tesserafin.Common.Net;
using Tesserafin.Model.Net;
using static Tesserafin.Controller.Extensions.ConfigurationExtensions;
using IConfigurationManager = Tesserafin.Common.Configuration.IConfigurationManager;

namespace Tesserafin.Networking.Manager;

/// <summary>
/// Publishes the current <see cref="NetworkTopology"/> and rebuilds it when the network
/// configuration or the host interfaces change.
/// </summary>
/// <remarks>
/// Each rebuild produces a new immutable topology that replaces the previous one in a single
/// reference write; every query reads one topology, so it never observes a partial update.
/// </remarks>
public sealed class NetworkManager : INetworkManager, IDisposable
{
    /// <summary>
    /// Delay after the last OS network event before interfaces are re-read; such events arrive in bursts.
    /// </summary>
    private static readonly TimeSpan _networkChangeSettleDelay = TimeSpan.FromSeconds(2);

    private readonly ILogger<NetworkManager> _logger;
    private readonly IConfigurationManager _configurationManager;
    private readonly INetworkInterfaceSource _interfaceSource;
    private readonly string? _startupPublishedServerUrl;
    private readonly Lock _rebuildLock = new();
    private readonly Timer? _networkChangeTimer;
    private volatile NetworkTopology _topology;
    private volatile bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="NetworkManager"/> class reading the host's interfaces.
    /// </summary>
    /// <param name="configurationManager">The <see cref="IConfigurationManager"/> instance.</param>
    /// <param name="startupConfig">The <see cref="IConfiguration"/> instance holding startup parameters.</param>
    /// <param name="logger">Logger to use for messages.</param>
    public NetworkManager(IConfigurationManager configurationManager, IConfiguration startupConfig, ILogger<NetworkManager> logger)
        : this(configurationManager, startupConfig, new SystemNetworkInterfaceSource(logger), logger)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="NetworkManager"/> class.
    /// </summary>
    /// <param name="configurationManager">The <see cref="IConfigurationManager"/> instance.</param>
    /// <param name="startupConfig">The <see cref="IConfiguration"/> instance holding startup parameters.</param>
    /// <param name="interfaceSource">The source of host interface addresses.</param>
    /// <param name="logger">Logger to use for messages.</param>
    public NetworkManager(
        IConfigurationManager configurationManager,
        IConfiguration startupConfig,
        INetworkInterfaceSource interfaceSource,
        ILogger<NetworkManager> logger)
    {
        ArgumentNullException.ThrowIfNull(configurationManager);
        ArgumentNullException.ThrowIfNull(startupConfig);
        ArgumentNullException.ThrowIfNull(interfaceSource);
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;
        _configurationManager = configurationManager;
        _interfaceSource = interfaceSource;
        _startupPublishedServerUrl = startupConfig[AddressOverrideKey];

        _topology = Rebuild(_configurationManager.GetNetworkConfiguration(), LogLevel.Information);

        _ = bool.TryParse(startupConfig[DetectNetworkChangeKey], out var detectNetworkChange);
        if (detectNetworkChange)
        {
            _networkChangeTimer = new Timer(OnNetworkChangeSettled);
            NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
            NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
        }

        _configurationManager.NamedConfigurationUpdated += OnConfigurationUpdated;
    }

    /// <summary>
    /// Gets the current topology.
    /// </summary>
    public NetworkTopology Topology => _topology;

    /// <inheritdoc/>
    public bool IsIPv4Enabled => _topology.IsIPv4Enabled;

    /// <inheritdoc/>
    public bool IsIPv6Enabled => _topology.IsIPv6Enabled;

    /// <inheritdoc/>
    public IReadOnlyList<IPData> GetAllBindInterfaces(bool individualInterfaces = false)
        => _topology.GetListenInterfaces(individualInterfaces);

    /// <inheritdoc/>
    public IReadOnlyList<IPData> GetLoopbacks() => _topology.GetLoopbacks();

    /// <inheritdoc/>
    public string GetBindAddress(HttpRequest source, out int? port)
    {
        ArgumentNullException.ThrowIfNull(source);

        var result = GetBindAddress(source.Host.Host, out port);
        port ??= source.Host.Port;
        return result;
    }

    /// <inheritdoc/>
    public string GetBindAddress(IPAddress? source, out int? port, bool skipOverrides = false)
        => _topology.GetBindAddress(source, out port, skipOverrides);

    /// <inheritdoc/>
    public string GetBindAddress(string source, out int? port)
    {
        var topology = _topology;
        if (!NetworkUtils.TryParseHost(source, out var addresses, topology.IsIPv4Enabled, topology.IsIPv6Enabled))
        {
            addresses = Array.Empty<IPAddress>();
        }

        return topology.GetBindAddress(addresses.FirstOrDefault(), out port);
    }

    /// <inheritdoc/>
    public bool IsInLocalNetwork(string address)
    {
        var topology = _topology;
        if (NetworkUtils.TryParseToSubnet(address, out var subnet))
        {
            return topology.IsInLocalNetwork(subnet.Address);
        }

        return NetworkUtils.TryParseHost(address, out var addresses, topology.IsIPv4Enabled, topology.IsIPv6Enabled)
               && addresses.Any(topology.IsInLocalNetwork);
    }

    /// <inheritdoc/>
    public bool IsInLocalNetwork(IPAddress address) => _topology.IsInLocalNetwork(address);

    /// <inheritdoc/>
    public bool TryParseInterface(string intf, [NotNullWhen(true)] out IReadOnlyList<IPData>? result)
        => _topology.TryParseInterface(intf, out result);

    /// <inheritdoc/>
    public IReadOnlyList<IPData> GetInternalBindAddresses() => _topology.GetInternalBindAddresses();

    /// <inheritdoc/>
    public RemoteAccessPolicyResult ShouldAllowServerAccess(IPAddress remoteIP) => _topology.ShouldAllowServerAccess(remoteIP);

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _configurationManager.NamedConfigurationUpdated -= OnConfigurationUpdated;
        if (_networkChangeTimer is not null)
        {
            NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
            NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
            _networkChangeTimer.Dispose();
        }
    }

    /// <summary>
    /// Builds a topology from <paramref name="config"/> and freshly read interfaces, then publishes it.
    /// </summary>
    /// <remarks>
    /// Rebuilds are serialized so that a slower, older rebuild cannot overwrite a newer one.
    /// </remarks>
    private NetworkTopology Rebuild(NetworkConfiguration config, LogLevel logLevel)
    {
        lock (_rebuildLock)
        {
            HappyEyeballs.HttpClientExtension.UseIPv6 = config.EnableIPv6;
            var interfaces = _interfaceSource.GetInterfaces(config.EnableIPv4, config.EnableIPv6);
            var topology = NetworkTopology.Create(config, _startupPublishedServerUrl, interfaces, _logger);
            _topology = topology;
            topology.Log(logLevel);
            return topology;
        }
    }

    private void OnConfigurationUpdated(object? sender, ConfigurationUpdateEventArgs evt)
    {
        if (evt.Key.Equals(NetworkConfigurationStore.StoreKey, StringComparison.Ordinal))
        {
            Rebuild((NetworkConfiguration)evt.NewConfiguration, LogLevel.Information);
        }
    }

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        _logger.LogDebug("Network availability changed.");
        ScheduleNetworkChange();
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        _logger.LogDebug("Network address change detected.");
        ScheduleNetworkChange();
    }

    private void ScheduleNetworkChange()
    {
        try
        {
            // Every event restarts the delay; one rebuild follows a burst.
            _networkChangeTimer?.Change(_networkChangeSettleDelay, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // Disposed between the event and the reschedule.
        }
    }

    private void OnNetworkChangeSettled(object? state)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            Rebuild(_configurationManager.GetNetworkConfiguration(), LogLevel.Debug);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh the network topology after a network change; keeping the previous one.");
        }
    }
}
