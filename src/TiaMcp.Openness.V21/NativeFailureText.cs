using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;

namespace TiaMcp.Openness.V21
{
    // Siemens reports the reason of a failed Openness call in MessageData/DetailMessageData; Message is often only
    // "Error when calling method ...". Bounded, so a failed apply journals what TIA actually said.
    internal static class NativeFailureText
    {
        internal static JArray Chain(Exception error)
        {
            var chain = new JArray();
            for (var current = error; current != null && chain.Count < 8; current = current.InnerException)
            {
                var row = new JObject { ["type"] = current.GetType().FullName, ["message"] = Bounded(current.Message) };
                if (current is EngineeringException engineering)
                {
                    row["text"] = Bounded(engineering.MessageData.Text);
                    row["detail"] = Bounded(engineering.MessageData.DetailText);
                    row["details"] = new JArray((engineering.DetailMessageData ?? Enumerable.Empty<ExceptionMessageData>()).Take(16)
                        .Select(detail => new JObject { ["text"] = Bounded(detail.Text), ["detail"] = Bounded(detail.DetailText) }));
                }
                chain.Add(row);
            }
            return chain;
        }

        private static string Bounded(string text) => text == null || text.Length <= 2048 ? text : text.Substring(0, 2048);
    }
}
