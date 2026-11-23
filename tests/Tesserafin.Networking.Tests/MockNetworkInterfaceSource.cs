using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Sockets;
using Tesserafin.Common.Net;
using Tesserafin.Model.Net;
using Tesserafin.Networking.Manager;

namespace Tesserafin.Networking.Tests
{
    /// <summary>
    /// Interface source parsed from <c>address/prefix,index,name|...</c>; a negative index simulates a gateway.
    /// </summary>
    internal sealed class MockNetworkInterfaceSource : INetworkInterfaceSource
    {
        private readonly string _interfaces;

        public MockNetworkInterfaceSource(string interfaces)
        {
            _interfaces = interfaces;
        }

        public IReadOnlyList<IPData> GetInterfaces(bool includeIPv4, bool includeIPv6)
        {
            var interfaces = new List<IPData>();
            foreach (var details in _interfaces.Split('|', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = details.Split(',');
                if (!NetworkUtils.TryParseToSubnet(parts[0], out var data))
                {
                    throw new FormatException("Invalid mock interface: " + details);
                }

                if (data.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                {
                    data.Index = int.Parse(parts[1], CultureInfo.InvariantCulture);
                    data.Name = parts[2];
                    interfaces.Add(data);
                }
            }

            return interfaces;
        }
    }
}
