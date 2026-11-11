#nullable disable
using System;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace TiaMcp.Openness.V21
{
    // The one generic edit of a safety tag: a single tag_set that renames it, asked for with safetyAcknowledged=true. Address and
    // data type belong to the F-I/O configuration and stay excluded, as do deletion and every item of a batch.
    internal static class SafetyTagRenameRules
    {
        /// <summary>True when the payload asks to rename a safety tag. Refuses the flag on any other kind and any value but true.</summary>
        internal static bool Requested(string kind, JObject payload)
        {
            if (payload?["safetyAcknowledged"] == null) return false;
            if (kind != "tag_set")
                throw new BridgeException("InvalidRequest", "safetyAcknowledged applies to a single tag_set that renames a safety tag; " + kind + " does not take it.");
            if (payload["safetyAcknowledged"].Type != JTokenType.Boolean || !payload.Value<bool>("safetyAcknowledged"))
                throw new BridgeException("InvalidRequest", "safetyAcknowledged must be true, or be omitted for a standard tag.");
            return true;
        }

        /// <summary>A safety tag can only be renamed: attributes must hold Name and nothing else.</summary>
        internal static void RequireNameOnly(JObject attributes)
        {
            if (attributes == null || attributes.Count != 1 || attributes["Name"]?.Type != JTokenType.String || string.IsNullOrWhiteSpace(attributes.Value<string>("Name")))
                throw new BridgeException("SafetyBoundary", "A safety tag can only be renamed: attributes must contain Name and nothing else. " +
                    (attributes?.Properties().Select(p => p.Name).FirstOrDefault(name => name != "Name") is string other ? other + " stays excluded." : "Give the new Name."));
        }
    }
}
