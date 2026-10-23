using System;
using Newtonsoft.Json.Linq;
using TiaMcp.Model;

namespace TiaMcp.Openness.V21
{
    internal static class HmiInternalTagCreation
    {
        // Delegates keep optional HMI assemblies out of this testable boundary.
        // Create once; any subsequent failure belongs to the enclosing recovery journal.
        internal static JObject Create<T>(string name, string dataType, Func<string, T> create,
            Action<T, string> setDataType, Func<T, string> readName, Func<T, string> readDataType,
            Func<T, string> readConnection, Func<T, string> readPlcTag, Func<string, bool> resolvesConnection)
        {
            var tag = create(name);
            string defaultConnection = readConnection(tag);
            setDataType(tag, dataType);
            string observedName = readName(tag), observedType = readDataType(tag);
            string connection = readConnection(tag), plcTag = readPlcTag(tag);
            bool resolved = !string.IsNullOrEmpty(connection) && resolvesConnection(connection);
            if (observedName != name || observedType != dataType)
                throw new BridgeException("PreservationMismatch", "Created internal HMI tag name or data type differs from the request.");
            try { HmiRecipeRules.RequireNewInternalTagDefault(defaultConnection, connection, plcTag, resolved); }
            catch (ArgumentException error) { throw new BridgeException("PreservationMismatch", error.Message); }
            return new JObject { ["name"] = observedName, ["dataType"] = observedType, ["connection"] = connection,
                ["defaultConnection"] = defaultConnection, ["plcTag"] = plcTag, ["nativeConnectionResolved"] = resolved,
                ["runtimeVerified"] = false };
        }
    }
}
