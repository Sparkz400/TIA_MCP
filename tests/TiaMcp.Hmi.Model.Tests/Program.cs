using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TiaMcp.Openness.V21;

int checks = 0;
void Check(bool value, string label) { if (!value) throw new Exception(label); checks++; }
JObject Result(params JObject[] edges) => new() { ["edges"] = new JArray(edges), ["truncated"] = false, ["payloadBytes"] = 0 };
JObject Edge(string id, string source = "/hmi/screen/item", string target = null, string kind = "hmiBinding", int padding = 0) => new()
{
    ["edgeId"] = id, ["source"] = null, ["target"] = null, ["kind"] = kind,
    ["evidence"] = new JObject { ["sourcePath"] = source, ["native"] = new JObject { ["raw"] = new string('x', padding), ["target"] = target == null ? null : new JObject { ["objectPath"] = target } } }
};
void Retain(JObject result, JArray edges, string[] kinds = null, string direction = "both", int max = 200) => HmiProjectModelRules.RetainUncaptured(result, edges, "/hmi/screen", kinds, direction, max);

Check(HmiProjectModelRules.CoverageState("failed") == "unavailable" && HmiProjectModelRules.CoverageState("truncated") == "partial", "native states normalize to shared coverage");
Check(HmiProjectModelRules.ProviderState("unavailable", 2) == "partial" && HmiProjectModelRules.ProviderState("unavailable", 0) == "unavailable", "broken properties retain accessible detail rows without claiming complete native coverage");
Check(HmiProjectModelRules.ProviderState("complete", 0) == "available" && HmiProjectModelRules.ProviderState("unsupported", 0) == "unsupported", "empty available sections and unsupported sections remain distinct");
var warnings = HmiProjectModelRules.Warnings(new JArray(new JObject { ["error"] = "native failure" }, "message"));
Check(warnings.All(t => t.Type == JTokenType.String) && (string)warnings[1] == "message", "diagnostics preserve content as strings");
var selected = Result(Edge("known"));
Retain(selected, new JArray(Edge("known"), Edge("other"), Edge("overflow")), max: 2);
Check(((JArray)selected["unresolvedObservations"]).Count == 1 && selected.Value<bool>("truncated"), "captured and uncaptured relationships share edge ceiling and deduplicate");
Check(selected.Value<bool>("continuationAvailable"), "truncation exposes continuation limitation");
var scoped = Result();
Retain(scoped, new JArray(Edge("inside"), Edge("prefixCollision", "/hmi/screen2/item"), Edge("elsewhere", "/hmi/elsewhere")));
Check(((JArray)scoped["unresolvedObservations"]).Count == 1, "scope has path segment boundaries");
var incoming = Result();
Retain(incoming, new JArray(Edge("outgoing"), Edge("incoming", "/other/item", "/hmi/screen/item")), direction: "in");
Check(incoming["unresolvedObservations"][0].Value<string>("edgeId") == "incoming" && incoming["unresolvedObservations"].Count() == 1, "direction retains only proven target path scope");
var filtered = Result();
Retain(filtered, new JArray(Edge("binding"), Edge("navigation", kind: "screenNavigation")), new[] { "screenNavigation" });
Check(filtered["unresolvedObservations"].Count() == 1 && filtered["unresolvedObservations"][0].Value<string>("edgeId") == "navigation", "kind filters apply to uncaptured evidence");
var bounded = Result();
Retain(bounded, new JArray(Edge("large", padding: 600000)));
Check(bounded.Value<bool>("truncated") && !bounded["unresolvedObservations"].Any(), "oversized raw evidence cannot bypass envelope bound");
Check(bounded.Value<int>("payloadBytes") == Encoding.UTF8.GetByteCount(bounded.ToString(Formatting.None)), "final payload counter includes adapter metadata");
Check(scoped["unresolvedObservations"][0]["source"].Type == JTokenType.Null, "uncaptured endpoint remains null");
var native = new JObject { ["sourcePath"] = "/hmi/tag", ["relation"] = "hmiPlcDeclaration", ["sourceProperty"] = "PlcTag", ["nativeSpelling"] = "DB.Value",
    ["target"] = new JObject { ["objectPath"] = "/plc/DB/interface/Static/members/Value", ["exportSha256"] = "first" } };
string key = HmiProjectModelRules.EdgeKey(native);
native["target"]["exportSha256"] = "second";
Check(HmiProjectModelRules.EdgeKey(native) == key, "repeated export evidence refreshes one semantic edge");
native["target"]["objectPath"] = "/other-plc/DB/interface/Static/members/Value";
Check(HmiProjectModelRules.EdgeKey(native) != key, "different exact targets retain distinct edge identities");
var faceplate = new JObject { ["sourcePath"] = "/hmi/screen/item", ["relation"] = "hmiFaceplateTypeVersion",
    ["sourceProperty"] = "LibraryTypeInstanceInfo.LibraryTypeVersion", ["target"] = null,
    ["libraryReference"] = new JObject { ["typeGuid"] = "146cafdb-cc89-4ab9-9fb1-013da44f3a12", ["versionGuid"] = "43b5c338-b589-4ab5-8c82-cae3e08e9d06" } };
var changedVersion = (JObject)faceplate.DeepClone();
changedVersion["libraryReference"]["versionGuid"] = "1846595b-bd59-43f9-a01c-923a67aa9a90";
JObject LibraryEdge(JObject relation)
{
    var edge = Edge(HmiProjectModelRules.EdgeKey(relation), kind: "libraryVersion");
    edge["evidence"]["native"] = relation.DeepClone();
    return edge;
}
var libraryObservations = Result();
Retain(libraryObservations, new JArray(LibraryEdge(faceplate), LibraryEdge(changedVersion)), new[] { "libraryVersion" });
Check(libraryObservations["unresolvedObservations"].Count() == 2, "different original library versions cannot collapse into one uncaptured observation");
Check(JToken.DeepEquals(libraryObservations["unresolvedObservations"][0]["evidence"]["native"], faceplate) &&
    JToken.DeepEquals(libraryObservations["unresolvedObservations"][1]["evidence"]["native"], changedVersion), "both original library observations remain exact");
Check(libraryObservations["unresolvedObservations"].All(e => e["source"].Type == JTokenType.Null && e["target"].Type == JTokenType.Null), "library GUIDs never invent captured endpoints");
var changedType = (JObject)faceplate.DeepClone(); changedType["libraryReference"]["typeGuid"] = "f8181eac-78e0-45d7-a7c5-e3308a09d86e";
Check(HmiProjectModelRules.EdgeKey(changedType) != HmiProjectModelRules.EdgeKey(faceplate), "type identity also distinguishes a library observation");
var refreshed = (JObject)faceplate.DeepClone(); refreshed["observedAtUtc"] = "2026-10-18T00:00:00Z";
refreshed["libraryReference"]["exportSha256"] = "different"; refreshed["libraryReference"]["interface"] = new JArray("changed supplied observation");
Check(HmiProjectModelRules.EdgeKey(refreshed) == HmiProjectModelRules.EdgeKey(faceplate), "export and interface evidence do not manufacture new library identities");
var repeated = Result(); Retain(repeated, new JArray(LibraryEdge(faceplate), LibraryEdge(refreshed)));
Check(repeated["unresolvedObservations"].Count() == 1, "repeated observations of one library identity still deduplicate");
var limitedLibrary = Result(); Retain(limitedLibrary, new JArray(LibraryEdge(faceplate), LibraryEdge(changedVersion)), max: 1);
Check(limitedLibrary["unresolvedObservations"].Count() == 1 && limitedLibrary.Value<bool>("truncated"), "different library versions debit the shared edge ceiling");
var missingLibrary = (JObject)faceplate.DeepClone(); missingLibrary.Remove("libraryReference");
Check(HmiProjectModelRules.EdgeKey(missingLibrary) != HmiProjectModelRules.EdgeKey(faceplate), "missing library identity cannot collapse an observed version");
const string legacyUnresolvedKey = "{\"sourcePath\":\"/hmi/screen/item\",\"relation\":\"hmiFaceplateTypeVersion\",\"sourceProperty\":\"LibraryTypeInstanceInfo.LibraryTypeVersion\",\"nativeSpelling\":null,\"targetPath\":null}";
Check(HmiProjectModelRules.EdgeKey(missingLibrary) == legacyUnresolvedKey, "no-identity library observations retain their historical key");
missingLibrary["libraryReference"] = new JObject { ["typeGuid"] = null, ["versionGuid"] = null };
Check(HmiProjectModelRules.EdgeKey(missingLibrary) == legacyUnresolvedKey, "unavailable native GUID values retain the same unresolved key");
var unrelated = (JObject)native.DeepClone(); unrelated["libraryReference"] = faceplate["libraryReference"].DeepClone();
Check(HmiProjectModelRules.EdgeKey(unrelated) == HmiProjectModelRules.EdgeKey(native), "other relationship kinds retain their existing identity contract");
foreach (string role in new[] { "template", "popup", "slideIn", "globalElements", "overview", "unknown" })
    Check(!HmiProjectModelRules.ScreenItemsSupported("classic", role), "Additional Classic screen role remains metadata-only: " + role);
Check(HmiProjectModelRules.ScreenItemsSupported("classic", "regular") && HmiProjectModelRules.ScreenItemsSupported("classic", null), "Regular and historical Classic screen item routing remains compatible");
Check(HmiProjectModelRules.ScreenItemsSupported("unified", null), "Unified screen details keep their native item route");
Console.WriteLine($"HMI project model: {checks} checks passed.");
