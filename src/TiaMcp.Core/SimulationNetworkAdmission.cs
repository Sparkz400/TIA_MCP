#nullable disable
namespace TiaMcp.Simulation
{
    public static class SimulationNetworkAdmission
    {
        // Inputs are exact PLCSIM Advanced V8 enum names. The worker only observes the
        // machine-wide network mode; it never assigns it. Only the interface recorded at
        // creation decides which path applies, and an unknown value is never admitted.
        public static string Refusal(string networkMode, string instanceInterface, string requiredInterface)
        {
            if (requiredInterface == "TCPIP")
                return networkMode == "TCPIPSingleAdapter" && instanceInterface == "TCPIP" ? null : "TcpIpModeRequired";
            return requiredInterface == "Softbus" && networkMode == "Softbus" && instanceInterface == "Softbus" ? null : "SoftbusRequired";
        }

        // TCP/IP instances answer only on PLCSIM's virtual Ethernet adapter: a TIA PG/PC interface name or a Windows adapter description.
        public static bool PlcsimAdapter(string name) =>
            (name ?? "").IndexOf("PLCSIM Virtual Ethernet Adapter", System.StringComparison.OrdinalIgnoreCase) >= 0;

        // A write to a TCP/IP instance's address must leave through that adapter: a physical PLC with the same address can sit
        // behind any other network card. routeAdapter comes from SimulationNetworkRoute.Adapter; null means undetermined.
        public static string RouteRefusal(string routeAdapter) =>
            routeAdapter == null ? "RuntimeTargetRouteUnverified" : PlcsimAdapter(routeAdapter) ? null : "RuntimeTargetNotOwnedPlcsim";

        // The one new device an owned empty startup produced qualifies for the documented bootstrap address. Softbus: only at
        // that address. TCP/IP: also without an IP, which TIA shows as no address, 0.0.0.0 or the device's MAC
        // (observed 29 October 2026: address = MAC 02-C0-A8-00-0A-00, series "S7-1500 (PLCSIM)").
        public static bool EmptyStartupEndpoint(string network, string address, string macAddress, string deviceSeries, string bootstrapAddress)
        {
            bool tcpip = network == "TCPIP";
            if (!(deviceSeries == "S7-1500" || deviceSeries == "S7-1500 CPU" || tcpip && deviceSeries == "S7-1500 (PLCSIM)")) return false;
            return address == bootstrapAddress || tcpip && (string.IsNullOrEmpty(address) || address == "0.0.0.0" ||
                string.Equals(address, macAddress, System.StringComparison.OrdinalIgnoreCase) ||
                System.Text.RegularExpressions.Regex.IsMatch(address, "^[0-9A-Fa-f]{2}([-:][0-9A-Fa-f]{2}){5}$"));
        }

        // One address-discovery sample decides the provisioning target: the one device shows a controller address of the
        // instance. TCP/IP only, once its budget is spent: TIA's TCP/IP discovery kept naming the CPU by its MAC after the
        // bootstrap (29 October 2026), so the same PLCSIM device as before the bootstrap is accepted while the instance reads
        // back exactly the bootstrap address. startupMac and bootstrapAddress are null when no bootstrap ran.
        public static string RuntimeTargetBasis(string network, bool budgetSpent, int deviceCount, string address, string macAddress, string deviceSeries,
            string[] runtimeAddresses, string bootstrapAddress, string startupMac)
        {
            if (deviceCount != 1) return null;
            if (System.Array.IndexOf(runtimeAddresses, address) >= 0) return "runtimeAddressMatch";
            return network == "TCPIP" && budgetSpent && bootstrapAddress != null && runtimeAddresses.Length == 1 && runtimeAddresses[0] == bootstrapAddress &&
                SameRuntimeTarget("tcpipMacIdentity", address, macAddress, deviceSeries, null, startupMac) ? "tcpipMacIdentity" : null;
        }

        // TCP/IP only, once: GoOnline threw before any online callback, nothing was opened or submitted and the connection is
        // still offline (seen once, 29 October 2026, minutes after another owned CPU left the same address). Softbus never retries.
        public static bool GoOnlineRetryAllowed(string network, int retriesDone, bool onlineCallbackSeen, bool credentialTouched, string onlineState) =>
            network == "TCPIP" && retriesDone == 0 && !onlineCallbackSeen && !credentialTouched && onlineState == "Offline";

        // Every later recheck of the decided target: the same address, or for tcpipMacIdentity the same MAC and PLCSIM series.
        public static bool SameRuntimeTarget(string basis, string address, string macAddress, string deviceSeries, string targetAddress, string startupMac) =>
            basis == "tcpipMacIdentity"
                ? !string.IsNullOrEmpty(startupMac) && string.Equals(macAddress, startupMac, System.StringComparison.OrdinalIgnoreCase) && deviceSeries == "S7-1500 (PLCSIM)"
                : address == targetAddress;
    }
}
