#nullable disable
using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        private static string ChangeText(JObject args, string name, int max)
        {
            if (args[name]?.Type != JTokenType.String) throw new BridgeException("InvalidRequest", name + " must be a string.");
            string value = args.Value<string>(name);
            if (string.IsNullOrWhiteSpace(value) || value.Length > max) throw new BridgeException("InvalidRequest", name + " length is outside the supported range.");
            return value;
        }
        private static object ConvertChangeValue(JToken token, Type type)
        {
            if (token == null || token.Type == JTokenType.Null) throw new BridgeException("InvalidRequest", "Null engineering attribute values are not supported.");
            if (type.IsEnum)
            {
                string text = token.Value<string>();
                if (!Enum.GetNames(type).Contains(text, StringComparer.Ordinal)) throw new BridgeException("InvalidRequest", "Unknown enum value for " + type.Name + ": " + text);
                return Enum.Parse(type, text, false);
            }
            if (type == typeof(string)) { if (token.Type != JTokenType.String) throw new BridgeException("InvalidRequest", "String attribute requires a JSON string."); return token.Value<string>(); }
            if (type == typeof(bool)) { if (token.Type != JTokenType.Boolean) throw new BridgeException("InvalidRequest", "Boolean attribute requires a JSON boolean."); return token.Value<bool>(); }
            if (type.IsPrimitive || type == typeof(decimal))
            {
                if (token.Type != JTokenType.Integer && token.Type != JTokenType.Float) throw new BridgeException("InvalidRequest", "Numeric attribute requires a JSON number.");
                double floating = token.Value<double>();
                if (double.IsNaN(floating) || double.IsInfinity(floating)) throw new BridgeException("InvalidRequest", "Numeric attributes must be finite.");
                bool integral = type == typeof(byte) || type == typeof(sbyte) || type == typeof(short) || type == typeof(ushort) || type == typeof(int) || type == typeof(uint) || type == typeof(long) || type == typeof(ulong);
                if (integral && token.Type != JTokenType.Integer) throw new BridgeException("InvalidRequest", "Integral engineering attributes require a JSON integer; floating-point values are not rounded.");
                if (!integral && type != typeof(float) && type != typeof(double) && type != typeof(decimal)) throw new BridgeException("UnsupportedAttributeType", "Unsupported scalar conversion: " + type.FullName);
                try
                {
                    object converted = Convert.ChangeType(((JValue)token).Value, type, CultureInfo.InvariantCulture);
                    if (converted is float single && (float.IsNaN(single) || float.IsInfinity(single))) throw new OverflowException();
                    return converted;
                }
                catch (OverflowException) { throw new BridgeException("InvalidRequest", "Numeric value is outside the range of " + type.Name + "."); }
            }
            throw new BridgeException("UnsupportedAttributeType", "Only documented scalar attribute types can be written: " + type.FullName);
        }

        private static void RejectSensitiveName(string name)
        {
            if (Regex.IsMatch(name, @"password|credential|protection|security|safety|failsafe|knowhow|accesslevel|certificate|privatekey|trust|login|^F_", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                throw new BridgeException("ProtectedAttribute", "Protection and safety controls require dedicated workflows and cannot be set through attribute tools.");
        }

        private static JToken ChangeScalar(object value)
        {
            if (value == null) return JValue.CreateNull();
            if (value is byte[] bytes) return Convert.ToBase64String(bytes);
            if (value.GetType().IsEnum) return value.ToString();
            if (value is string || value is bool || value.GetType().IsPrimitive || value is decimal || value is DateTime) return JToken.FromObject(value);
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

    }
}
