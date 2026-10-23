using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core;
using TiaMcp.Core.Drives;

internal static class TelegramTests
{
    internal static void Run(string parent, JsonObject originalReview, JsonObject originalPacket)
    {
        string root = Path.Combine(parent, "telegrams-v2"); Directory.CreateDirectory(root);
        const string plc = "/projects/Fixture/devices/CPU/plc", other = "/projects/Fixture/devices/Other/plc";
        var checks = new List<string>();
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks.Add(message); }
        void Reject(Action action, string message, string code = "InvalidDriveProfile")
        { try { action(); } catch (BridgeException e) when (e.Code == code) { Check(true, message); return; } throw new Exception("Expected refusal: " + message); }
        var model = originalReview["model"]!.DeepClone().AsObject();
        foreach (string path in new[] { plc, other }) model["nodes"]!.AsArray().Add(new JsonObject { ["nodeId"] = path, ["objectPath"] = path, ["kind"] = "plc", ["name"] = "CPU", ["parentId"] = "/projects/Fixture", ["metadata"] = new JsonObject() });
        var reviews = new ProjectReviewStore(root); string id = (string)reviews.Save(model, new(), new() { ["syntheticFixture"] = true })["reviewId"]!;
        var review = reviews.Read(id); var packet = originalPacket.DeepClone().AsObject(); packet["reviewId"] = id; packet["schemaVersion"] = "drive-profile-review-request.v2";
        JsonObject Address(string key, string direction, int start, string unit, int length, string lengthUnit) => new() {
            ["id"] = key, ["plcPath"] = plc, ["direction"] = direction, ["context"] = "supplied-controller-context", ["startAddress"] = start,
            ["addressUnit"] = unit, ["length"] = length, ["lengthUnit"] = lengthUnit, ["unknownReason"] = null };
        JsonObject Telegram(string key, int number) => new() { ["id"] = key, ["type"] = "MainTelegram", ["number"] = number,
            ["inputBytes"] = 8, ["outputBytes"] = 4, ["addressesComplete"] = true, ["unknownReason"] = null,
            ["addresses"] = new JsonArray(Address("input", "Input", 256, "byte", 64, "bit"), Address("output", "Output", 4096, "bit", 4, "byte")) };
        foreach (string phase in new[] { "before", "after" }) { packet[phase]!["telegramsComplete"] = false; packet[phase]!["telegrams"] = new JsonArray(Telegram("😀", 81), Telegram("\uE000", 999)); }
        packet["after"]!["telegrams"]![0]!["number"] = 83; packet["after"]!["telegrams"]![0]!["inputBytes"] = 9;
        JsonObject Mutate(Action<JsonObject> edit) { var p = packet.DeepClone().AsObject(); edit(p); return p; }
        JsonObject Build(JsonObject? p = null, JsonObject? r = null) => DriveProfileReview.Build(id, r ?? review, p ?? packet);
        JsonObject Row(JsonObject p) => p["before"]!["telegrams"]![0]!.AsObject();
        JsonObject Addr(JsonObject p) => Row(p)["addresses"]![0]!.AsObject();
        var result = Build(); var first = result["telegrams"]![0]!; var address = first["addresses"]![0]!;
        Check((long)address["startBit"]! == 2048 && (long)address["lengthBits"]! == 64 && (long)address["endBitExclusive"]! == 2112, "Independent explicit units normalize exactly to bits");
        Check((string)address["plcResolution"]! == "capturedPlc" && (string)address["directionalSizeComparison"]! == "equalDeclaredSize", "Saved exact PLC and declared directional size remain separate observations");
        Check((string)result["telegrams"]![2]!["addresses"]![0]!["directionalSizeComparison"]! == "differentDeclaredSize", "Changed directional size is visible without native validity claim");
        Check((string)result["telegrams"]![1]!["numberSemantics"]! == "freelyConfigurable", "Main telegram 999 is explicitly freely configurable without invented layout");
        Check((int)result["summary"]!["changedTelegramCount"]! == 1 && (string)result["telegramChanges"]![0]!["id"]! == "😀" && (string)result["telegramChanges"]![0]!["changedFields"]![0]! == "number", "Exact Unicode IDs correlate changes with ordinal UTF-16 ordering");
        var old = packet.DeepClone().AsObject(); old["schemaVersion"] = "drive-profile-review-request.v1";
        foreach (string phase in new[] { "before", "after" }) { old[phase]!.AsObject().Remove("telegrams"); old[phase]!.AsObject().Remove("telegramsComplete"); }
        var legacy = Build(old);
        Check(legacy["telegrams"] is null && JsonNode.DeepEquals(legacy["observations"], result["observations"]) && JsonNode.DeepEquals(legacy["changes"], result["changes"]), "V1 parameter semantics are unchanged by v2 telegram extension");
        foreach (string field in new[] { "type", "number", "inputBytes", "outputBytes" })
        {
            Reject(() => Build(Mutate(p => Row(p)[field] = null)), "Unknown telegram " + field + " needs reason");
            var value = Build(Mutate(p => { Row(p)[field] = null; Row(p)["unknownReason"] = "Not captured"; }));
            Check(value["telegrams"]![0]!["telegram"]![field] is null, "Unknown telegram " + field + " remains null");
        }
        var unknown = Build(Mutate(p => { Addr(p)["addressUnit"] = "unknown"; Addr(p)["unknownReason"] = "Readback unit not documented"; }));
        Check(unknown["telegrams"]![0]!["addresses"]![0]!["startBit"] is null && unknown["telegrams"]![0]!["addresses"]![0]!["endBitExclusive"] is null, "Unknown readback address unit never becomes bytes");
        var unknownLength=Build(Mutate(p=>{Addr(p)["lengthUnit"]="unknown";Addr(p)["unknownReason"]="Unit unavailable";}));
        Check(unknownLength["telegrams"]![0]!["addresses"]![0]!["lengthBits"] is null && (string)unknownLength["telegrams"]![0]!["addresses"]![0]!["directionalSizeComparison"]! == "unknown", "Unknown length unit cannot pass declared size comparison");
        foreach(string field in new[]{"plcPath","direction","context","startAddress","length"})
        {
            Reject(()=>Build(Mutate(p=>Addr(p)[field]=null)),"Unknown address "+field+" requires reason");
            var value=Build(Mutate(p=>{Addr(p)[field]=null;Addr(p)["unknownReason"]="Not observed";}));
            Check(value["telegrams"]![0]!["addresses"]![0]!["address"]![field] is null,"Unknown address "+field+" remains null");
        }
        var maximum = Build(Mutate(p => { Addr(p)["startAddress"] = int.MaxValue; Addr(p)["length"] = int.MaxValue; Addr(p)["lengthUnit"] = "byte"; }));
        Check((long)maximum["telegrams"]![0]!["addresses"]![0]!["endBitExclusive"]! == 34359738352L, "Maximum nonnegative Int32 quantities use exact nonoverflowing Int64 arithmetic");
        foreach (var (field, bad) in new (string, JsonNode?)[] { ("direction", JsonValue.Create("input")), ("addressUnit", JsonValue.Create("word")), ("lengthUnit", JsonValue.Create("Byte")), ("startAddress", JsonValue.Create(-1)), ("length", JsonValue.Create(1.5)), ("id", JsonValue.Create("bad\nidentity")) })
            Reject(() => Build(Mutate(p => Addr(p)[field] = bad?.DeepClone())), "Invalid address " + field + " refused");
        Reject(() => Build(Mutate(p => Row(p)["type"] = "InventedTelegram")), "Unknown type cannot silently acquire semantics");
        Reject(() => Build(Mutate(p => Row(p)["number"] = 2147483648L)), "Oversized telegram number refused");
        Reject(() => Build(Mutate(p => Row(p)["addressesComplete"] = false)), "Incomplete addresses need explicit reason");
        Reject(() => Build(Mutate(p => Row(p)["addresses"]!.AsArray().Add(Addr(p).DeepClone()))), "Duplicate exact address IDs refused");
        Reject(() => Build(Mutate(p => p["before"]!["telegrams"]!.AsArray().Add(Row(p).DeepClone()))), "Duplicate exact telegram IDs refused");
        Reject(() => Build(Mutate(p => Row(p)["extra"] = true)), "Unknown telegram field refused");
        Reject(() => Build(Mutate(p => Addr(p).Remove("context"))), "Missing address context field refused");
        Reject(() => Build(Mutate(p => p["schemaVersion"] = true)), "Malformed request version retains structured refusal");
        var another = Build(Mutate(p => Addr(p)["plcPath"] = other));
        Check((string)another["telegrams"]![0]!["addresses"]![0]!["address"]!["plcPath"]! == other, "Equal addresses in another PLC retain distinct explicit owner context");
        foreach (var (path, state) in new[] { ("missing", "missing"), ((string)packet["hardwarePath"]!, "wrongKind") })
            Check((string)Build(Mutate(p => Addr(p)["plcPath"] = path))["telegrams"]![0]!["addresses"]![0]!["plcResolution"]! == state, "Address owner resolution: " + state);
        foreach (string defect in new[] { "duplicatePath", "duplicateId", "virtual", "invalidId" })
        {
            var r = review.DeepClone().AsObject(); var nodes = r["model"]!["nodes"]!.AsArray();
            if (defect == "duplicatePath") nodes.Add(nodes[2]!.DeepClone());
            else if (defect == "duplicateId") nodes[3]!["nodeId"] = plc;
            else if (defect == "virtual") nodes[2]!["origin"] = "virtual";
            else nodes[2]!["nodeId"] = "bad\nidentity";
            Check((string)Build(r:r)["telegrams"]![0]!["addresses"]![0]!["plcResolution"]! != "capturedPlc", "Unusable saved PLC identity: " + defect);
        }
        var reordered = Build(Mutate(p => { var rows = p["after"]!["telegrams"]!.AsArray(); var a = rows[0]; rows.RemoveAt(0); rows.Add(a); }));
        Check(JsonNode.DeepEquals(result["telegramChanges"], reordered["telegramChanges"]), "Collection order does not change exact telegram ID matching");
        var reorderedAddresses = Build(Mutate(p => { var rows=p["after"]!["telegrams"]![0]!["addresses"]!.AsArray();var a=rows[0];rows.RemoveAt(0);rows.Add(a); }));
        Check(JsonNode.DeepEquals(result["telegramChanges"],reorderedAddresses["telegramChanges"]), "Address collection order cannot manufacture a semantic change");
        Check((string)Build(Mutate(p => p["before"]!["telegrams"]![1]!["type"]="AdditionalTelegram"))["telegrams"]![1]!["numberSemantics"]! == "declaredNumber", "Main-telegram 999 meaning is not applied to another telegram type");
        var distinct = Build(Mutate(p => p["before"]!["telegrams"]![1]!["number"] = 81));
        Check((int)distinct["summary"]!["beforeTelegramCount"]! == 2, "Equal numbers cannot merge distinct supplied telegram IDs");
        var full = Mutate(p => { foreach (string phase in new[] { "before", "after" }) p[phase]!["telegrams"] = new JsonArray(Enumerable.Range(0,128).Select(i => (JsonNode)Telegram("t"+i,i)).ToArray()); });
        Check(Build(full)["telegrams"]!.AsArray().Count == 256, "Maximum phase telegram counts remain bounded and represented");
        foreach(string phase in new[]{"before","after"})foreach(var t in full[phase]!["telegrams"]!.AsArray())
            t!["addresses"]=new JsonArray(Enumerable.Range(0,16).Select(i=>(JsonNode)Address("a"+i,"Input",i,"byte",1,"byte")).ToArray());
        Check(Build(full)["telegrams"]!.AsArray().Sum(t=>t!["addresses"]!.AsArray().Count)==4096,"Maximum combined telegram and address counts preserve every source row");
        Reject(() => Build(Mutate(p => p["before"]!["telegrams"] = new JsonArray(Enumerable.Range(0,129).Select(i => (JsonNode)Telegram("t"+i,i)).ToArray()))), "Telegram count bound refused");
        Reject(() => Build(Mutate(p => Row(p)["addresses"] = new JsonArray(Enumerable.Range(0,17).Select(i => (JsonNode)Address("a"+i,"Input",0,"byte",1,"byte")).ToArray()))), "Address count bound refused");
        var uploads = new ArtifactUploadStore(root); byte[] bytes = Encoding.UTF8.GetBytes(packet.ToJsonString()+"\n");
        var upload = uploads.Begin("telegrams.json",bytes.Length,Convert.ToHexStringLower(SHA256.HashData(bytes)));
        for (int offset=0;offset<bytes.Length;offset+=upload.MaxChunkBytes) uploads.Write(upload.UploadId,offset,Convert.ToBase64String(bytes,offset,Math.Min(upload.MaxChunkBytes,bytes.Length-offset)));
        var receipt = new DriveProfileWorkflowStore(root).Review(id,uploads.Commit(upload.UploadId).ArtifactRef!); uploads.Release(upload.UploadId);
        var store = new DriveProfileWorkflowStore(root); string record = (string)receipt["recordId"]!;
        Check(JsonNode.DeepEquals(receipt,store.Page(record)), "V2 retained receipt survives upload release and store recreation");
        foreach (string section in new[] { "telegrams", "telegramChanges" }) Check((int)store.Page(record,section,0,1)["total"]! == result[section]!.AsArray().Count, "Typed section pages: "+section);
        var forged=JsonNode.Parse(ProjectReviewStore.ReadArtifact(root,receipt["reportArtifact"]!.AsObject()))!.AsObject();forged["schemaVersion"]="drive-profile-review-record.v1";
        byte[] forgedBytes=JsonSerializer.SerializeToUtf8Bytes(forged,BridgeProtocol.Json);string forgedHash=Convert.ToHexStringLower(SHA256.HashData(forgedBytes));File.WriteAllBytes(Path.Combine(root,"drive-profile-reviews",forgedHash+".json"),forgedBytes);
        Reject(()=>store.Page(forgedHash),"Rehashed envelope cannot disagree with original request version");
        foreach (string key in new[] { "input", "reportArtifact" })
        { string file=Path.Combine(root,(string)receipt[key]!["path"]!);byte[] original=File.ReadAllBytes(file);File.WriteAllText(file,"tampered");Reject(()=>store.Page(record,"telegrams"),"V2 refuses original corruption: "+key,"ArtifactChanged");File.WriteAllBytes(file,original); }
        foreach (var (name,value) in new[] { ("request.json",packet),("report.json",result),("receipt.json",receipt) }) File.WriteAllText(Path.Combine(root,name),value.ToJsonString());
        File.WriteAllText(Path.Combine(root,"mcp-fixture.json"),JsonSerializer.Serialize(new {artifactRoot=root,reviewId=id,packet},BridgeProtocol.Json));
        File.WriteAllText(Path.Combine(root,"verification.json"),JsonSerializer.Serialize(new {completed=true,nativeExecuted=false,count=checks.Count,checks},BridgeProtocol.Json));
        Console.WriteLine($"PASS drive telegrams: {checks.Count} checks; explicit supplied metadata only.");
    }
}
