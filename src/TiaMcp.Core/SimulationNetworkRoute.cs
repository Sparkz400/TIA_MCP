#nullable disable
using System;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace TiaMcp.Simulation
{
    /// <summary>Which network adapter Windows sends traffic for an endpoint through; SimulationNetworkAdmission.RouteRefusal decides on it.</summary>
    public static class SimulationNetworkRoute
    {
        // The description of the adapter Windows' best route to the endpoint's literal IPv4 address leaves through; null when undetermined.
        public static string Adapter(string endpoint)
        {
            try
            {
                if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.HostNameType != UriHostNameType.IPv4) return null;
                var destination = new byte[16]; // sockaddr_in: AF_INET, port 0, the address
                destination[0] = 2;
                IPAddress.Parse(uri.Host).GetAddressBytes().CopyTo(destination, 4);
                if (GetBestInterfaceEx(destination, out uint index) != 0) return null;
                var adapters = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.Supports(NetworkInterfaceComponent.IPv4) && n.GetIPProperties().GetIPv4Properties()?.Index == index).ToArray();
                return adapters.Length == 1 ? adapters[0].Description : null;
            }
            catch (NetworkInformationException) { return null; }
        }

        [DllImport("iphlpapi.dll")]
        private static extern int GetBestInterfaceEx(byte[] destination, out uint index);
    }
}
