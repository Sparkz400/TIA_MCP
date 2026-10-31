#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TiaMcp.Openness.V21
{
    // Strict reading of the recorded-profile contracts embedded in this worker: exact fields, no duplicates, nonempty text.
    internal sealed class ContractDocument
    {
        private readonly string _name;
        internal ContractDocument(string name) { _name = name; }

        internal JObject Load(string json, string schema)
        {
            JObject document;
            using (var reader = new JsonTextReader(new System.IO.StringReader(json)) { DateParseHandling = DateParseHandling.None, MaxDepth = 16 })
                document = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            if (Text(document, "schemaVersion") != schema || document["tiaVersion"]?.Type != JTokenType.Integer || document.Value<int>("tiaVersion") != 21)
                throw Invalid("schemaVersion must be " + schema + " and tiaVersion 21.");
            Text(document, "rule");
            return document;
        }

        internal void Fields(JObject value, string names)
        {
            if (value == null) throw Invalid("Expected an object.");
            var expected = names.Split(' ');
            if (value.Count != expected.Length || expected.Any(name => value[name] == null)) throw Invalid("Expected exactly the fields " + names + ".");
        }

        internal string Text(JObject value, string name) =>
            value[name]?.Type == JTokenType.String && !string.IsNullOrWhiteSpace(value.Value<string>(name)) ? value.Value<string>(name) : throw Invalid(name + " must be a nonempty string.");

        internal IEnumerable<JObject> Objects(JObject value, string name) =>
            value[name] is JArray rows && rows.All(row => row is JObject) ? rows.Cast<JObject>() : throw Invalid(name + " must be an array of objects.");

        internal FormatException Invalid(string message) => new FormatException(_name + ": " + message);
    }
}
