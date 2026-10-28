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
