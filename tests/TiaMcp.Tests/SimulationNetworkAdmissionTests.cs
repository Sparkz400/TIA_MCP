using TiaMcp.Simulation;

namespace TiaMcp.Tests;

internal static class SimulationNetworkAdmissionTests
{
    internal static Task SoftbusAndSingleAdapterAdmission()
    {
        Check.Equal(null, SimulationNetworkAdmission.Refusal("Softbus", "Softbus", "Softbus"));
        Check.Equal(null, SimulationNetworkAdmission.Refusal("TCPIPSingleAdapter", "TCPIP", "TCPIP"));
        Check.Equal("SoftbusRequired", SimulationNetworkAdmission.Refusal("TCPIPSingleAdapter", "TCPIP", "Softbus"));
        Check.Equal("SoftbusRequired", SimulationNetworkAdmission.Refusal("Softbus", "TCPIP", "Softbus"));
        return Task.CompletedTask;
    }

    internal static Task ModeSwitchesAndUnknownValuesRefuse()
    {
        // The operator switched back to Softbus mid-window, or chose multi-adapter mode.
        Check.Equal("TcpIpModeRequired", SimulationNetworkAdmission.Refusal("Softbus", "Softbus", "TCPIP"));
        Check.Equal("TcpIpModeRequired", SimulationNetworkAdmission.Refusal("TCPIPMultipleAdapter", "TCPIP", "TCPIP"));
        Check.Equal("TcpIpModeRequired", SimulationNetworkAdmission.Refusal("TCPIPSingleAdapter", "Softbus", "TCPIP"));
        Check.Equal("TcpIpModeRequired", SimulationNetworkAdmission.Refusal("UnknownState", "TCPIP", "TCPIP"));
        Check.Equal("SoftbusRequired", SimulationNetworkAdmission.Refusal("UnknownState", "Softbus", "Softbus"));
        Check.Equal("SoftbusRequired", SimulationNetworkAdmission.Refusal("softbus", "Softbus", "Softbus"));
        Check.Equal("SoftbusRequired", SimulationNetworkAdmission.Refusal("TCPIPSingleAdapter", "TCPIP", "None"));
        Check.Equal("SoftbusRequired", SimulationNetworkAdmission.Refusal(null, null, null));
        return Task.CompletedTask;
    }

    internal static Task EmptyStartupEndpointRecognition()
    {
        const string boot = "192.168.0.1", mac = "02-C0-A8-00-0A-00";
        // Exact observation of the first TCP/IP window (29 October 2026): no IP yet, so TIA shows the MAC as the address.
        Check.Equal(true, SimulationNetworkAdmission.EmptyStartupEndpoint("TCPIP", mac, mac, "S7-1500 (PLCSIM)", boot));
        Check.Equal(true, SimulationNetworkAdmission.EmptyStartupEndpoint("TCPIP", "02:c0:a8:00:0a:01", mac, "S7-1500 (PLCSIM)", boot));
        foreach (var none in new[] { null, "", "0.0.0.0" })
            Check.Equal(true, SimulationNetworkAdmission.EmptyStartupEndpoint("TCPIP", none, mac, "S7-1500 (PLCSIM)", boot));
        Check.Equal(true, SimulationNetworkAdmission.EmptyStartupEndpoint("TCPIP", boot, mac, "S7-1500", boot));
        // Another IP, a partial MAC or another device series is never the owned empty CPU.
        Check.Equal(false, SimulationNetworkAdmission.EmptyStartupEndpoint("TCPIP", "192.168.0.7", mac, "S7-1500 (PLCSIM)", boot));
        Check.Equal(false, SimulationNetworkAdmission.EmptyStartupEndpoint("TCPIP", "02-C0-A8-00-0A", mac, "S7-1500 (PLCSIM)", boot));
        Check.Equal(false, SimulationNetworkAdmission.EmptyStartupEndpoint("TCPIP", mac, mac, "S7-1200 (PLCSIM)", boot));
        Check.Equal(false, SimulationNetworkAdmission.EmptyStartupEndpoint("TCPIP", mac, mac, null, boot));
        // Softbus stays exactly as before: only the documented address and the two plain series names.
        Check.Equal(true, SimulationNetworkAdmission.EmptyStartupEndpoint("Softbus", boot, mac, "S7-1500", boot));
        Check.Equal(true, SimulationNetworkAdmission.EmptyStartupEndpoint("Softbus", boot, mac, "S7-1500 CPU", boot));
        Check.Equal(false, SimulationNetworkAdmission.EmptyStartupEndpoint("Softbus", mac, mac, "S7-1500", boot));
        Check.Equal(false, SimulationNetworkAdmission.EmptyStartupEndpoint("Softbus", "0.0.0.0", mac, "S7-1500", boot));
        Check.Equal(false, SimulationNetworkAdmission.EmptyStartupEndpoint("Softbus", boot, mac, "S7-1500 (PLCSIM)", boot));
        return Task.CompletedTask;
    }

    internal static Task RuntimeTargetFromDiscoverySamples()
    {
        const string boot = "192.168.0.1", mac = "02-C0-A8-00-0A-00", plcsim = "S7-1500 (PLCSIM)";
        string[] runtime = { boot };
        string Basis(string network, bool spent, int count = 1, string address = mac, string deviceMac = mac, string series = plcsim, string[] addresses = null,
            string bootstrap = boot, string startup = mac) =>
            SimulationNetworkAdmission.RuntimeTargetBasis(network, spent, count, address, deviceMac, series, addresses ?? runtime, bootstrap, startup);
        // Every sample of run rs_a82cdf40 (29 October 2026): the instance read back 192.168.0.1, TIA kept showing the CPU by its MAC.
        foreach (long elapsed in new long[] { 60, 380, 704, 1029, 1355, 1682, 2014, 2329, 2642, 2949, 3291, 3627, 3966, 4272, 4587, 4899, 5207, 5534,
            6431, 6880, 7214, 7544, 7864, 8192, 8500, 8835, 9185, 9526, 9867 })
        {
            Check.Equal(null, Basis("TCPIP", elapsed >= 60000));
            Check.Equal(null, Basis("Softbus", elapsed >= 10000));
        }
        // The same sample at the end of the TCP/IP budget decides by MAC; Softbus never does.
        Check.Equal("tcpipMacIdentity", Basis("TCPIP", true));
        Check.Equal(null, Basis("Softbus", true));
        // A late address update or a Softbus address match decides by address on either network, at any time.
        Check.Equal("runtimeAddressMatch", Basis("TCPIP", false, address: boot));
        Check.Equal("runtimeAddressMatch", Basis("Softbus", false, address: boot, series: "S7-1500"));
        // Never: two devices, another MAC or series, other or extra runtime addresses, or no bootstrap by this attempt.
        Check.Equal(null, Basis("TCPIP", true, count: 2));
        Check.Equal(null, Basis("TCPIP", true, deviceMac: "02-C0-A8-00-0A-01"));
        Check.Equal(null, Basis("TCPIP", true, series: "S7-1500"));
        Check.Equal(null, Basis("TCPIP", true, addresses: new[] { boot, "192.168.0.2" }));
        Check.Equal(null, Basis("TCPIP", true, addresses: new[] { "192.168.0.5" }));
        Check.Equal(null, Basis("TCPIP", true, bootstrap: null, startup: null));
        Check.Equal(null, Basis("TCPIP", true, startup: null));
        // Later rechecks: by MAC and series for the MAC basis, by exact address otherwise.
        Check.Equal(true, SimulationNetworkAdmission.SameRuntimeTarget("tcpipMacIdentity", mac, "02-c0-a8-00-0a-00", plcsim, boot, mac));
        Check.Equal(false, SimulationNetworkAdmission.SameRuntimeTarget("tcpipMacIdentity", mac, "02-C0-A8-00-0A-01", plcsim, boot, mac));
        Check.Equal(false, SimulationNetworkAdmission.SameRuntimeTarget("tcpipMacIdentity", mac, mac, "S7-1500", boot, mac));
        Check.Equal(true, SimulationNetworkAdmission.SameRuntimeTarget("runtimeAddressMatch", boot, mac, "S7-1500", boot, null));
        Check.Equal(false, SimulationNetworkAdmission.SameRuntimeTarget("runtimeAddressMatch", mac, mac, plcsim, boot, mac));
        return Task.CompletedTask;
    }

    // The machine-wide network mode is an operator setting; the simulator worker only reads it.
    internal static Task WorkerNeverAssignsNetworkMode()
    {
        string relative = Path.Combine("src", "TiaMcp.Simulator.V8", "SimulatorSession.cs");
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, relative))) root = root.Parent;
        string source = File.ReadAllText(Path.Combine(root?.FullName ?? throw new InvalidOperationException("Simulator source missing."), relative));
        Check.Equal(false, System.Text.RegularExpressions.Regex.IsMatch(source, @"NetworkMode\s*=[^=]|set_NetworkMode|SetNetInterface|SetIPSuite"));
        Check.Equal(true, source.Contains("SimulationNetworkAdmission.Refusal", StringComparison.Ordinal));
        return Task.CompletedTask;
    }
}
