using System;
using Newtonsoft.Json.Linq;

namespace TiaMcp.Openness.V21
{
    internal sealed class BridgeException : Exception
    {
        internal string Code { get; }
        internal JObject Details { get; }
        internal BridgeException(string code, string message, JObject details = null) : base(message) { Code = code; Details = details; }
    }
}
