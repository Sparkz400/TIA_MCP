#nullable disable
using System;
using Newtonsoft.Json.Linq;
using Siemens.Engineering.Compiler;

namespace TiaMcp.Openness.V21
{
    internal static class PlcCompilation
    {
        internal static JObject Prepare(JObject payload, string requestedPath, string selectedPath, ICompilable compiler)
        {
            if (payload == null || payload.Count != 0 || string.IsNullOrWhiteSpace(requestedPath) || requestedPath != selectedPath)
                throw new BridgeException("InvalidRequest", "PLC compilation requires an empty payload and the exact selected plcPath.");
            if (compiler == null) throw new BridgeException("UnsupportedCapability", "PLC compiler service unavailable.");
            return new JObject { ["action"] = "compile", ["plcPath"] = selectedPath,
                ["scope"] = "Selected PLC software; native generated state may change." };
        }

        internal static JObject Compile(ICompilable compiler, string path, Action<string> journal = null)
        {
            if (compiler == null) throw new BridgeException("UnsupportedCapability", "PLC compiler service unavailable.");
            if (string.IsNullOrWhiteSpace(path)) throw new BridgeException("TargetIdentityUnavailable", "An exact PLC compiler path is required.");
            journal?.Invoke("compiler_dispatching");
            var result = compiler.Compile();
            return new JObject { ["errorCount"] = result.ErrorCount, ["warningCount"] = result.WarningCount,
                ["targets"] = new JArray(DeviceCompilation.Describe(result, path)) };
        }
    }
}
