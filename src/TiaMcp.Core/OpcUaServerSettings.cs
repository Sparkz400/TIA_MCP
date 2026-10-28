#nullable disable
using System;
using System.Globalization;
using System.Linq;

namespace TiaMcp.Core
{
    /// <summary>Payload rules of opcua_server_settings_set. Attribute names are pinned from the native capture in
    /// fixtures/opcua/server-settings-v21-1511-v41.json; a null name has no attribute there, so its field is refused.</summary>
    public static class OpcUaServerSettings
    {
        public sealed class Setting
        {
            public string Field; public bool OnCpu; public string Attribute;
            internal Setting(string field, bool onCpu, string attribute) { Field = field; OnCpu = onCpu; Attribute = attribute; }
        }

        // OnCpu: the CPU device item; otherwise its "OPC UA" sub-item, the one that exposes OpcUaServer. That item exposes
        // OpcUaPort and OpcUaAutoAcceptAllClientCertificates only while the server is on. V21 models guest access as the
        // UMAC anonymous user holding the OPCUAServerAccess device right, not as an attribute.
        public static readonly Setting[] Settings =
        {
            new Setting("serverEnabled", false, "OpcUaServer"),
            new Setting("port", false, "OpcUaPort"),
            new Setting("guestAccess", false, null),
            new Setting("autoAcceptClientCertificates", false, "OpcUaAutoAcceptAllClientCertificates"),
            new Setting("runtimeLicense", true, "OpcUaPurchasedLicense"),
        };

        // UInt64 values 0..3 of OpcUaPurchasedLicense, as the V21 hardware parameter description names them.
        public static readonly string[] Licenses = { "NoLicense", "SimaticOpcUaS71500Small", "SimaticOpcUaS71500Medium", "SimaticOpcUaS71500Large" };

        public static Setting Find(string field) => Settings.FirstOrDefault(s => s.Field == field);

        /// <summary>Null when expected (payload form) equals the current native value and value is well formed;
        /// otherwise InvalidRequest or ExpectedValueMismatch.</summary>
        public static string Refusal(string field, object expected, object value, object current)
        {
            if (Find(field) == null || !Valid(field, expected) || !Valid(field, value)) return "InvalidRequest";
            return Equals(PayloadValue(field, current), expected) ? null : "ExpectedValueMismatch";
        }

        /// <summary>The native value in payload form (bool, long port, license name), or null outside the known forms.</summary>
        public static object PayloadValue(string field, object native)
        {
            if (field == "runtimeLicense" || field == "port")
            {
                if (!(native is byte || native is ushort || native is uint || native is ulong || native is short || native is int || native is long)) return null;
                decimal number = Convert.ToDecimal(native, CultureInfo.InvariantCulture);
                if (field == "port") return (object)(long)number;
                return number >= 0 && number < Licenses.Length ? Licenses[(int)number] : null;
            }
            return native as bool?;
        }

        public static object NativeValue(string field, object value, Type nativeType) => Convert.ChangeType(
            field == "runtimeLicense" ? (object)(ulong)Array.IndexOf(Licenses, (string)value) : value, nativeType, CultureInfo.InvariantCulture);

        private static bool Valid(string field, object value) =>
            field == "port" ? value is long port && port >= 1 && port <= 65535 :
            field == "runtimeLicense" ? value is string name && Licenses.Contains(name) : value is bool;
    }
}
