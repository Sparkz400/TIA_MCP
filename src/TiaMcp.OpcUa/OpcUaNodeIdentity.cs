using System.Globalization;
using TiaMcp.Core;

namespace TiaMcp.OpcUa;

/// <summary>A URI-based identity. Namespace indexes are local to a particular artifact/session.</summary>
public sealed record OpcUaNodeIdentity(string NamespaceUri, string IdentifierType, string Identifier)
{
    public const string StandardNamespace = "http://opcfoundation.org/UA/";
    // Percent escaping makes semicolons and literal percent signs unambiguous in this report key.
    public string Key => $"nsu={Uri.EscapeDataString(NamespaceUri)};{IdentifierType}={Identifier}";

    public static OpcUaNodeIdentity Parse(string text, IReadOnlyList<string> namespaces,
        IReadOnlyDictionary<string, string>? aliases = null)
    {
        if (text.Length > 4096) throw Invalid("NodeId exceeds 4096 characters.");
        if (aliases is not null && aliases.TryGetValue(text, out var target)) text = target;
        int index = 0;
        if (text.StartsWith("ns=", StringComparison.Ordinal))
        {
            int separator = text.IndexOf(';');
            if (separator < 0 || !ushort.TryParse(text.AsSpan(3, separator - 3), NumberStyles.None,
                CultureInfo.InvariantCulture, out var parsed)) throw Invalid("Invalid namespace index.");
            index = parsed;
            text = text[(separator + 1)..];
        }
        if (index >= namespaces.Count) throw Invalid("NodeId refers to an absent namespace index.");
        if (text.Length < 2 || text[1] != '=') throw Invalid("Expected an indexed NodeId or a declared alias.");
        string value = text[2..];
        switch (text[0])
        {
            case 'i':
                if (!uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                    throw Invalid("Numeric NodeId is outside UInt32.");
                value = number.ToString(CultureInfo.InvariantCulture);
                break;
            case 's': break; // Empty string identifiers are retained, never silently rewritten.
            case 'g':
                if (!Guid.TryParseExact(value, "D", out var guid)) throw Invalid("Invalid GUID NodeId.");
                value = guid.ToString("D");
                break;
            case 'b':
                try { value = Convert.ToBase64String(Convert.FromBase64String(value)); }
                catch (FormatException) { throw Invalid("Invalid opaque NodeId."); }
                break;
            default: throw Invalid("Unsupported NodeId form; expanded/server-relative IDs need a separate profile.");
        }
        return new(namespaces[index], text[..1], value);
    }

    private static BridgeException Invalid(string message) => new("OpcUaInvalidIdentity", message);
}
