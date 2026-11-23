using System;
using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Common.Configuration;
using Tesserafin.Common.Net;
using Tesserafin.Model.Net;
using Tesserafin.Networking.Manager;
using Xunit;
using IConfigurationManager = Tesserafin.Common.Configuration.IConfigurationManager;

namespace Tesserafin.Networking.Tests
{
    public class NetworkTopologyTests
    {
        private const string Interfaces = "192.168.1.208/24,1,eth0|10.0.0.1/24,2,eth1";

        [Fact]
        public void ConfigurationUpdate_ClearingTheRemoteFilter_StopsFiltering()
        {
            var filtered = new NetworkConfiguration
            {
                EnableIPv4 = true,
                EnableRemoteAccess = true,
                RemoteIPFilter = ["185.10.10.10"],
                IsRemoteIPFilterBlacklist = false
            };
            var configManager = new Mock<IConfigurationManager>();
            configManager.Setup(x => x.GetConfiguration(NetworkConfigurationStore.StoreKey)).Returns(filtered);
            using var nm = new NetworkManager(configManager.Object, new Mock<IConfiguration>().Object, new MockNetworkInterfaceSource(Interfaces), NullLogger<NetworkManager>.Instance);
            var remote = IPAddress.Parse("79.2.3.4");
            Assert.Equal(RemoteAccessPolicyResult.RejectDueToNotAllowlistedRemoteIP, nm.ShouldAllowServerAccess(remote));

            var cleared = new NetworkConfiguration
            {
                EnableIPv4 = true,
                EnableRemoteAccess = true,
                RemoteIPFilter = [],
                IsRemoteIPFilterBlacklist = false
            };
            configManager.Raise(x => x.NamedConfigurationUpdated += null, new ConfigurationUpdateEventArgs(NetworkConfigurationStore.StoreKey, cleared));

            Assert.Equal(RemoteAccessPolicyResult.Allow, nm.ShouldAllowServerAccess(remote));
            Assert.Empty(nm.Topology.RemoteAddressFilter);
        }

        [Fact]
        public void ConfigurationUpdate_PublishesANewTopologyInstance()
        {
            var config = new NetworkConfiguration { EnableIPv4 = true, LocalNetworkSubnets = ["192.168.1.0/24"] };
            var configManager = new Mock<IConfigurationManager>();
            configManager.Setup(x => x.GetConfiguration(NetworkConfigurationStore.StoreKey)).Returns(config);
            using var nm = new NetworkManager(configManager.Object, new Mock<IConfiguration>().Object, new MockNetworkInterfaceSource(Interfaces), NullLogger<NetworkManager>.Instance);
            var before = nm.Topology;
            Assert.False(nm.IsInLocalNetwork(IPAddress.Parse("10.0.0.5")));

            var updated = new NetworkConfiguration { EnableIPv4 = true, LocalNetworkSubnets = ["10.0.0.0/24"] };
            configManager.Raise(x => x.NamedConfigurationUpdated += null, new ConfigurationUpdateEventArgs(NetworkConfigurationStore.StoreKey, updated));

            Assert.NotSame(before, nm.Topology);
            Assert.True(nm.IsInLocalNetwork(IPAddress.Parse("10.0.0.5")));
            Assert.False(nm.IsInLocalNetwork(IPAddress.Parse("192.168.1.5")));

            // The superseded topology keeps answering from its own configuration.
            Assert.True(before.IsInLocalNetwork(IPAddress.Parse("192.168.1.5")));
        }

        [Fact]
        public void Create_MalformedOverride_DoesNotDropTheFollowingOnes()
        {
            var config = new NetworkConfiguration
            {
                EnableIPv4 = true,
                LocalNetworkSubnets = ["192.168.1.0/24"],
                PublishedServerUriBySubnet = ["not-an-override", "external=media.example.org"]
            };

            var topology = NetworkTopology.Create(config, null, new MockNetworkInterfaceSource(Interfaces).GetInterfaces(true, false), NullLogger.Instance);

            Assert.Equal("media.example.org", topology.GetBindAddress(IPAddress.Parse("8.8.8.8"), out _));
        }

        [Fact]
        public void GetBindAddress_OverrideWithPort_ReturnsThePort()
        {
            var config = new NetworkConfiguration
            {
                EnableIPv4 = true,
                LocalNetworkSubnets = ["192.168.1.0/24"],
                PublishedServerUriBySubnet = ["external=media.example.org:8920"]
            };

            var topology = NetworkTopology.Create(config, null, new MockNetworkInterfaceSource(Interfaces).GetInterfaces(true, false), NullLogger.Instance);

            Assert.Equal("media.example.org", topology.GetBindAddress(IPAddress.Parse("8.8.8.8"), out var port));
            Assert.Equal(8920, port);
        }

        [Fact]
        public void GetBindAddress_UriOverride_IsReturnedWhole()
        {
            var config = new NetworkConfiguration
            {
                EnableIPv4 = true,
                LocalNetworkSubnets = ["192.168.1.0/24"],
                PublishedServerUriBySubnet = ["all=https://media.example.org:8920"]
            };

            var topology = NetworkTopology.Create(config, null, new MockNetworkInterfaceSource(Interfaces).GetInterfaces(true, false), NullLogger.Instance);

            Assert.Equal("https://media.example.org:8920", topology.GetBindAddress(IPAddress.Parse("192.168.1.20"), out var port));
            Assert.Null(port);
        }

        [Fact]
        public void Create_StartupOverride_ReplacesConfiguredOverrides()
        {
            var config = new NetworkConfiguration
            {
                EnableIPv4 = true,
                LocalNetworkSubnets = ["192.168.1.0/24"],
                PublishedServerUriBySubnet = ["external=media.example.org"]
            };

            var topology = NetworkTopology.Create(config, "startup.example.org", new MockNetworkInterfaceSource(Interfaces).GetInterfaces(true, false), NullLogger.Instance);

            Assert.Equal("startup.example.org", topology.GetBindAddress(IPAddress.Parse("8.8.8.8"), out _));
            Assert.Equal("startup.example.org", topology.GetBindAddress(IPAddress.Parse("192.168.1.20"), out _));
        }
    }
}
