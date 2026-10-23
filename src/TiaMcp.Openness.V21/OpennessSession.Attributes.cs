#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        private static object ReadAttributeValue(IEngineeringObject target, string name, HashSet<string> allowlist,
            bool requireWrite, out Type type)
        {
            RejectSensitiveName(name);
            if (!allowlist.Contains(name))
                throw new BridgeException("UnsupportedAttribute", "Attribute is outside the reviewed write subset: " + name);
            var declarations = target.GetAttributeInfos()?.Where(info => info.Name == name).Take(2).ToArray();
            if (declarations == null || declarations.Length != 1)
                throw new BridgeException("AttributeAccessUnknown", "Attribute requires one explicit access declaration: " + name);
            var info = declarations[0];
            if (requireWrite && (info.AccessMode & EngineeringAttributeAccessMode.Write) == 0)
                throw new BridgeException("AttributeNotWritable", "Attribute must permit writes: " + name);
            if ((info.AccessMode & EngineeringAttributeAccessMode.Read) == 0)
                throw new BridgeException("AttributeNotReadable", "Attribute must permit preview and readback: " + name);
            object value = target.GetAttribute(name);
            type = value?.GetType();
            if (type == null)
            {
                var declared = info.SupportedTypes?.Take(2).ToArray();
                if (declared == null || declared.Length != 1 || declared[0] == null)
                    throw new BridgeException("AttributeTypeUnknown", "Null attribute value requires one explicit supported type: " + name);
                type = declared[0];
            }
            return value;
        }

        private static JArray PrepareAttributes(IEngineeringObject target, JObject attributes, HashSet<string> allowlist)
            => PrepareAttributes(target, attributes, allowlist, out _);

        private static JArray PrepareAttributes(IEngineeringObject target, JObject attributes, HashSet<string> allowlist,
            out Dictionary<string, object> converted)
        {
            converted = new Dictionary<string, object>(StringComparer.Ordinal);
            var result = new JArray();
            foreach (var property in attributes.Properties())
            {
                object before = ReadAttributeValue(target, property.Name, allowlist, true, out var type);
                converted.Add(property.Name, ConvertChangeValue(property.Value, type));
                result.Add(new JObject { ["name"] = property.Name, ["type"] = type.FullName,
                    ["before"] = ChangeScalar(before), ["after"] = property.Value.DeepClone() });
            }
            return result;
        }

        private static void ApplyAttributes(IEngineeringObject target, JObject attributes, HashSet<string> allowlist)
        {
            // Preflight every value before dispatch; partial native failures still require checkpoint recovery.
            PrepareAttributes(target, attributes, allowlist, out var converted);
            foreach (var property in attributes.Properties())
            {
                ReadAttributeValue(target, property.Name, allowlist, true, out var type);
                object value = converted[property.Name];
                if (type != value.GetType())
                    throw new BridgeException("PreviewStale", "Attribute type changed after batch preflight: " + property.Name);
                target.SetAttribute(property.Name, value);
            }
            foreach (var property in attributes.Properties())
            {
                object expected = converted[property.Name];
                object actual = ReadAttributeValue(target, property.Name, allowlist, false, out var type);
                if (type != expected.GetType() || !JToken.DeepEquals(ChangeScalar(expected), ChangeScalar(actual)))
                    throw new BridgeException("MutationVerificationFailed", "Native attribute type/value read-back differs from the request: " + property.Name);
            }
        }
    }
}
