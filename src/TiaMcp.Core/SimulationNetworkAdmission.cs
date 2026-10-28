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
    }
}
