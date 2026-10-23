#nullable disable
using System;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TiaMcp.Indexing
{
    // Presentation adapter only. Parsing, supported/opaque instruction decisions
    // and instruction profiles belong to the existing typed graphical reader.
    internal static class ProjectModelTypedPreview
    {
        internal const string ProviderId = "typedLadFbd.v21";

        internal static JObject Adapt(ProjectModelProviderRequest request, JObject observation)
        {
            var graph = observation?["graph"] as JObject;
            var artifact = observation?["nativeXml"] as JObject;
            string hash = artifact?.Value<string>("sha256");
            if (graph == null || graph.Value<string>("blockPath") != request.Reference.Value<string>("objectPath") ||
                hash == null || hash.Length != 64 || !hash.All(Uri.IsHexDigit))
                throw new ProjectModelStore.ModelException("InvalidProviderResponse", "Typed reader must supply the exact block path and an immutable native export SHA-256.");
            var result = ProjectModelProviders.Describe(request.Reference, request.Facet);
            result["provider"] = ProviderId;
            result["capturedAtUtc"] = observation["observedAtUtc"] ?? DateTime.UtcNow.ToString("O");
            var revision = new JObject { ["status"] = "available", ["algorithm"] = "sha256", ["value"] = hash, ["scope"] = "exportedNativeBlockXml" };
            result["contentRevision"] = revision;
            result["sourceArtifact"] = artifact.DeepClone();
            result["readerSourceSha256"] = graph["sourceSha256"];
            result["reason"] = "Read-only structural preview from the existing native LAD/FBD reader. Opaque instructions and unresolved connections remain explicit; this is not native layout or runtime verification.";
            var attachments = (JArray)result["attachments"];
            var networks = graph["networks"] as JArray ?? new JArray();
            bool partial = observation.Value<bool?>("truncated") == true || networks.Count > request.MaxItems;
            foreach (var network in networks.OfType<JObject>().Take(request.MaxItems))
            {
                var content = (JObject)network.DeepClone();
                // Original XML remains in the hashed host artifact, including
                // unknown payloads. Never embed native/project script text as HTML.
                foreach (var property in content.Descendants().OfType<JProperty>().Where(p => p.Name == "nativeXml").ToArray()) property.Remove();
                string status = network.Value<string>("coverage") == "complete" ? "complete" : "partial";
                if (status != "complete") partial = true;
                var attachment = new JObject { ["kind"] = "ladFbdNetwork", ["title"] = (network.Value<string>("language") ?? "Graphical") + " network " + network.Value<string>("networkId"),
                    ["source"] = new JObject { ["objectPath"] = request.Reference["objectPath"], ["contentRevision"] = revision.DeepClone(),
                        ["capturedAtUtc"] = result["capturedAtUtc"].DeepClone(), ["networkId"] = network["networkId"] },
                    ["content"] = content, ["coverage"] = new JObject { ["status"] = status, ["nativeReaderCoverage"] = network["coverage"],
                        ["nativeNodeCount"] = (network["nodes"] as JArray)?.Count ?? 0, ["nativeConnectionCount"] = (network["connections"] as JArray)?.Count ?? 0,
                        ["reason"] = "Only reader-supported instruction syntax is normalized. Unknown content remains in the original export; preview positions are schematic." } };
                attachments.Add(attachment);
                // Leave room for final provider coverage, timestamps and status.
                if (Bytes(result) > request.MaxBytes - 2048)
                {
                    attachment.Remove(); partial = true;
                    break;
                }
            }
            result["status"] = partial ? "partial" : "available";
            result["coverage"] = new JArray(new JObject { ["provider"] = ProviderId, ["scope"] = request.Reference["objectPath"],
                ["status"] = partial ? "partial" : "complete", ["visited"] = networks.Count, ["returned"] = attachments.Count,
                ["warnings"] = new JArray(partial ? "Instruction coverage, network item bound or preview byte bound is partial; inspect the original native export for omitted/opaque content." :
                    "Supported syntax coverage does not prove operand types, execution, native visual layout or runtime behavior."), ["continuationAvailable"] = attachments.Count < networks.Count } );
            ProjectModelProviders.Validate(result, request, ProviderId);
            return result;
        }

        private static int Bytes(JObject value) => Encoding.UTF8.GetByteCount(value.ToString(Formatting.None));
    }
}
