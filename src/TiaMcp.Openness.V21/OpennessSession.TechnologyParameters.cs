#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.SW.TechnologicalObjects;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        private static object ReadTechnologyParameterValue(TechnologicalInstanceDB technology, string name,
            bool requireWrite, out TechnologicalParameter parameter, out Type type)
        {
            RejectSensitiveName(name);
            parameter = technology.Parameters.Find(name) ?? throw new BridgeException("ParameterNotFound", "Unknown technology parameter: " + name);
            if (!string.Equals(parameter.Name, name, StringComparison.Ordinal))
                throw new BridgeException("ParameterNotFound", "Technology parameter lookup did not return the exact requested name: " + name);
            var attributes = parameter.GetAttributeInfos();
            var values = attributes?.Where(info => info.Name == "Value").Take(2).ToArray();
            if (values == null || values.Length != 1)
                throw new BridgeException("ParameterAccessUnknown", "Technology parameter requires one explicit Value access declaration: " + name);
            if (requireWrite && (values[0].AccessMode & EngineeringAttributeAccessMode.Write) == 0)
                throw new BridgeException("ReadOnlyParameter", "Technology parameter does not permit writes: " + name);
            if ((values[0].AccessMode & EngineeringAttributeAccessMode.Read) == 0)
                throw new BridgeException("ParameterNotReadable", "Technology parameter must permit readback: " + name);
            object value = parameter.Value;
            type = value?.GetType();
            if (type == null)
            {
                var declared = values[0].SupportedTypes?.Take(2).ToArray();
                if (declared == null || declared.Length != 1 || declared[0] == null)
                    throw new BridgeException("ParameterTypeUnknown", "Null technology value requires one explicit supported type: " + name);
                type = declared[0];
            }
            return value;
        }

        private static JArray PrepareTechnologyParameters(TechnologicalInstanceDB technology, JObject parameters,
            out Dictionary<string, object> converted)
        {
            if (parameters.Count > 500) throw new BridgeException("InvalidRequest", "TO parameter batches are limited to 500 scalar values.");
            converted = new Dictionary<string, object>(StringComparer.Ordinal);
            var preview = new JArray();
            foreach (var property in parameters.Properties())
            {
                object before = ReadTechnologyParameterValue(technology, property.Name, true, out _, out var type);
                converted.Add(property.Name, ConvertChangeValue(property.Value, type));
                preview.Add(new JObject { ["name"] = property.Name, ["type"] = type.FullName,
                    ["before"] = ChangeScalar(before), ["after"] = property.Value.DeepClone() });
            }
            return preview;
        }

        private static void ApplyTechnologyParameters(TechnologicalInstanceDB technology, JObject parameters)
        {
            // Preflight the entire batch before its first setter; native failures after dispatch still require checkpoint recovery.
            PrepareTechnologyParameters(technology, parameters, out var converted);
            foreach (var property in parameters.Properties())
            {
                ReadTechnologyParameterValue(technology, property.Name, true, out var parameter, out var type);
                object value = converted[property.Name];
                if (type != value.GetType())
                    throw new BridgeException("PreviewStale", "Technology parameter type changed after batch preflight: " + property.Name);
                parameter.Value = value;
            }
            foreach (var property in parameters.Properties())
            {
                object expected = converted[property.Name];
                object actual = ReadTechnologyParameterValue(technology, property.Name, false, out _, out var type);
                if (type != expected.GetType() || !JToken.DeepEquals(ChangeScalar(expected), ChangeScalar(actual)))
                    throw new BridgeException("MutationVerificationFailed", "Technology parameter type/value read-back differs from the request: " + property.Name);
            }
        }
    }
}
