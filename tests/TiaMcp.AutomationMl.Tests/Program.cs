using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TiaMcp.Core;
using TiaMcp.Core.AutomationMl;

if (args.Length != 1 || Directory.Exists(args[0])) throw new ArgumentException("Supply a fresh evidence directory.");
string output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output); int checks = 0;
void Check(bool value, string name) { if (!value) throw new Exception(name); checks++; Console.WriteLine("PASS AutomationML " + name); }
void Reject(string code, Action action)
{ try { action(); } catch (BridgeException error) { Check(error.Code == code, "refuses " + code + ": " + error.Code); return; } throw new Exception("Expected " + code); }
byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);
string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, BridgeProtocol.Json);
string Text(JsonElement value, string key) => value.GetProperty(key).GetString()!;
AutomationMlDocument Parse(string text) => AutomationMlDocument.Parse(Bytes(text));
string Wrap(string content) => "<CAEXFile FileName=\"synthetic.aml\" SchemaVersion=\"2.15\">" + content + "</CAEXFile>";
var inputs = new List<object>();
foreach (string path in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "fixtures/upstream"), "*.aml").Order())
{
    var bytes = File.ReadAllBytes(path); var document = AutomationMlDocument.Parse(bytes);
    Check(document.SchemaValid, "independent upstream schema-valid " + Path.GetFileName(path));
    Check(!document.NativeQualified && !document.ImportabilityCertified && !document.ImpactScopeComplete, "upstream fixture is not native evidence");
    Check(document.SourceSha256 == Hash(bytes), "upstream source identity");
    if (path.EndsWith("9_ExtInt_IntLink.aml"))
        Check(document.Links.Count == 1 && document.Links.All(l => l.TargetA is not null && l.TargetB is not null), "independent CAEX 2.15 IE-ID/interface-name endpoints");
    if (path.EndsWith("medium-valid-robot-cell.aml"))
        Check(document.Links.Count > 0 && document.Links.All(l => l.TargetA is not null && l.TargetB is not null), "independent CAEX 3.0 direct interface-ID endpoints");
    inputs.Add(new { name = Path.GetFileName(path), sha256 = Hash(bytes), document.Profile, document.SchemaSha256, document.SchemaValid, objects = document.Objects.Count, links = document.Links.Count });
}
string source = Wrap("""
  <AdditionalInformation><Vendor><InternalElement Name="Not a device" ID="opaque"/>before<!--offset-->after</Vendor></AdditionalInformation>
  <InstanceHierarchy Name="Plant" ID="plant">
    <InternalElement Name="CPU" ID="cpu" RefBaseSystemUnitPath="Devices/Controller">
      <Attribute Name="Address"><Attribute Name="StartAddress" AttributeDataType="xs:int"><Value>12</Value></Attribute></Attribute>
      <ExternalInterface Name="Port" ID="cpu-port" RefBaseClassPath="Ports/Ethernet"/>
      <InternalLink Name="Cable" RefPartnerSideA="cpu:Port" RefPartnerSideB="drive:Port"/>
      <InternalLink Name="Parallel" RefPartnerSideA="cpu:Port" RefPartnerSideB="drive:Port"/>
    </InternalElement>
    <InternalElement Name="Drive" ID="drive"><ExternalInterface Name="Port" ID="drive-port" RefBaseClassPath="Ports/Ethernet"/></InternalElement>
  </InstanceHierarchy>
  <InterfaceClassLib Name="Ports"><InterfaceClass Name="Ethernet"/></InterfaceClassLib>
  <SystemUnitClassLib Name="Devices"><SystemUnitClass Name="Controller"/></SystemUnitClassLib>
""");
var before = Parse(source);
Check(before.SchemaValid && before.MatchingReliable, "synthetic declared structure validates and has explicit unique keys");
Check(before.Objects.All(o => o.Id != "opaque") && before.OpaqueContent.Count > 0, "opaque familiar names do not become devices");
Check(before.Attributes.Single(a => a.Name == "StartAddress").Values.SequenceEqual(["12"]), "nested address values retained without interpreting device semantics");
var opaqueValue = Parse(source.Replace("<Value>12</Value>", "<Value><vendor>12</vendor></Value>"));
Check(opaqueValue.SchemaValid && opaqueValue.Attributes.Single(a=>a.Name=="StartAddress").Values.Single() is null && opaqueValue.Diagnostics.Any(d=>d.Code=="OpaqueAttributeValue"), "schema-valid opaque Value is never flattened into a scalar");
var opaqueDefault = Parse(source.Replace("<Value>12</Value>", "<DefaultValue xml:lang=\"en\">12</DefaultValue><Value>1<!--x-->2</Value>"));
Check(opaqueDefault.Attributes.Single(a=>a.Name=="StartAddress").Defaults.Single() is null && opaqueDefault.Attributes.Single(a=>a.Name=="StartAddress").Values.Single() is null, "attributed defaults and annotated values stay opaque");
Check(Parse(source.Replace("<Value>12</Value>", "<Value><![CDATA[12]]></Value>")).Attributes.Single(a=>a.Name=="StartAddress").Values.Single()=="12", "CDATA text remains a declared scalar");
Check(Parse(source.Replace("<Value>12</Value>", "<Value/>" )).Attributes.Single(a=>a.Name=="StartAddress").Values.SequenceEqual([""]) && Parse(source.Replace("<Value>12</Value>", "")).Attributes.Single(a=>a.Name=="StartAddress").Values.Length==0, "empty and absent values remain distinct");
Check(before.References.All(r => r.State == "resolved") && before.Links.Count == 2, "class paths resolved and parallel links retained");
var after = Parse(source.Replace("<Value>12", "<Value>16")); var comparison = Json(before.CompareTo(after));
Check(comparison.GetProperty("changes").GetArrayLength() > 0 && comparison.GetProperty("sourceBytesChanged").GetBoolean(), "address edit is visible");
Check(comparison.GetProperty("beforeImpacts").EnumerateArray().Any(i => Text(i,"id") == "drive"), "declared connected peer remains in conservative impact scope");
Check(comparison.GetProperty("wholeDocumentReviewRequired").GetBoolean() && !comparison.GetProperty("impactScopeComplete").GetBoolean(), "declared impact never certifies complete affected-device scope");
Check(Json(before.CompareTo(before)).GetProperty("changes").GetArrayLength() == 0, "identical bytes have no declared changes");
Check(Json(before.CompareTo(Parse(source.Replace("before<!--offset-->after", "beforeafter<!--offset-->")))).GetProperty("wholeDocumentReviewRequired").GetBoolean(), "opaque mixed-text positions require whole-document review");
Check(Json(before.CompareTo(Parse("<!--outer-->"+source))).GetProperty("sourceBytesChanged").GetBoolean(), "root-external annotations affect identity");
Check(Json(before.CompareTo(Parse(source.Replace("ID=\"drive\"", "ID=\"new-drive\"")))).GetProperty("changes").EnumerateArray().Any(c => Text(c,"kind")=="removed"), "ID changes do not silently match names");
var duplicates = Parse(source.Replace("ID=\"drive\"", "ID=\"cpu\""));
Check(!duplicates.MatchingReliable && duplicates.Diagnostics.Any(d=>d.Code=="DuplicateId") && duplicates.Links.All(l=>l.TargetA is null), "duplicate IDs refuse endpoint identity");
var attributeCollision = Parse(source.Replace("<Attribute Name=\"Address\">", "<Attribute ID=\"cpu\" Name=\"Address\">"));
Check(!attributeCollision.MatchingReliable && attributeCollision.Links.All(l=>l.TargetA is null), "attribute ID collision cannot resolve a device endpoint");
Check(Json(before.CompareTo(duplicates)).GetProperty("changes").EnumerateArray().Any(c=>Text(c,"kind")=="ambiguous"), "comparison exposes ambiguous identities");
var noId = Parse(source.Replace("ID=\"drive\"", ""));
Check(!noId.MatchingReliable && noId.Diagnostics.Any(d=>d.Code=="MissingId"), "missing IDs explicit");
Check(Json(before.CompareTo(noId)).GetProperty("changes").EnumerateArray().Any(c=>Text(c,"kind")=="unmatched-after"), "missing IDs are not matched by source position");
var duplicatePort = Parse(source.Replace("<ExternalInterface Name=\"Port\" ID=\"drive-port\" RefBaseClassPath=\"Ports/Ethernet\"/>", "<ExternalInterface Name=\"Port\" ID=\"a\"/><ExternalInterface Name=\"Port\" ID=\"b\"/>"));
Check(duplicatePort.Diagnostics.Any(d=>d.Code=="DuplicateName") && duplicatePort.Links.All(l=>l.TargetB is null), "duplicate interface names make 2.15 links ambiguous");
var colonPorts = Parse(source.Replace("Name=\"Port\"", "Name=\"Port:1\"").Replace(":Port\"", ":Port:1\""));
Check(colonPorts.SchemaValid && colonPorts.Links.All(l=>l.TargetA is not null && l.TargetB is not null), "2.15 endpoint uses full declared interface name including colon");
var ambiguousColon = Parse(Wrap("<InstanceHierarchy Name=\"P\"><InternalElement ID=\"A\" Name=\"One\"><ExternalInterface ID=\"i1\" Name=\"B:C\"/><InternalLink Name=\"L\" RefPartnerSideA=\"A:B:C\" RefPartnerSideB=\"A:B:C\"/></InternalElement><InternalElement ID=\"A:B\" Name=\"Two\"><ExternalInterface ID=\"i2\" Name=\"C\"/></InternalElement></InstanceHierarchy>"));
Check(ambiguousColon.SchemaValid && ambiguousColon.Links.Single().TargetA is null && ambiguousColon.Links.Single().TargetB is null, "two possible ID-name splits remain ambiguous");
var external = Parse(source.Replace("Devices/Controller", "outside@Devices/Controller"));
Check(external.References.Any(r=>r.State=="external-not-loaded"), "external class alias stays unloaded");
Check(Parse(Wrap("<ExternalReference Alias=\"secret\" Path=\"file:///denied\"/>")).References.Single().State=="external-not-loaded", "external file references stay unloaded");
Check(Parse(source.Replace("FileName=\"synthetic.aml\"", "FileName=\"synthetic.aml\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xsi:noNamespaceSchemaLocation=\"http://127.0.0.1:1/denied.xsd\"" )).SchemaValid, "untrusted schema location is ignored");
var invalid = Parse(source.Replace("SchemaVersion=\"2.15\"", "SchemaVersion=\"2.15\" Unexpected=\"x\""));
Check(!invalid.SchemaValid && invalid.Diagnostics.Any(d=>d.Code=="SchemaValidation"), "schema-invalid supported document remains inspectable with failure diagnostics");
Reject("AutomationMlUnsupportedProfile", ()=>Parse(source.Replace("2.15","2.16")));
Reject("AutomationMlUnsupportedProfile", ()=>Parse(source.Replace("2.15","3.0")));
Reject("AutomationMlInvalidXml", ()=>Parse("<!DOCTYPE CAEXFile [<!ENTITY e SYSTEM 'file:///denied'>]>"+source));
Reject("AutomationMlInvalidXml", ()=>Parse("<CAEXFile"));
Reject("AutomationMlCapacity", ()=>AutomationMlDocument.Parse(new byte[AutomationMlDocument.MaxInputBytes+1]));
Reject("AutomationMlCapacity", ()=>Parse(Wrap("<AdditionalInformation>"+string.Concat(Enumerable.Repeat("<x>",70))+string.Concat(Enumerable.Repeat("</x>",70))+"</AdditionalInformation>")));
Reject("AutomationMlCapacity", ()=>Parse(Wrap("<AdditionalInformation>"+string.Concat(Enumerable.Repeat("<x/>",50001))+"</AdditionalInformation>")));
Reject("AutomationMlCapacity", ()=>Parse(Wrap("<AdditionalInformation>"+string.Concat(Enumerable.Repeat("<!--x-->",250001))+"</AdditionalInformation>")));
Reject("AutomationMlCapacity", ()=>Parse(source.Replace("Name=\"CPU\"", "Name=\""+new string('x',8193)+"\"")));
// A large flat declared graph catches accidental quadratic path construction and parent lookup.
string large = Wrap("<InstanceHierarchy Name=\"Large\" ID=\"large\">"+string.Concat(Enumerable.Range(0,10000).Select(i=>$"<InternalElement Name=\"Device{i}\" ID=\"d{i}\"/>"))+"</InstanceHierarchy>");
var largeDoc=Parse(large); Check(largeDoc.Objects.Count==10001, "10000 devices inspect without truncation");
Check(Json(largeDoc.CompareTo(Parse(large.Replace("Device9999", "Renamed")))).GetProperty("afterImpacts").GetArrayLength()==10001, "large hierarchy impact retains every declared peer conservatively");

var store = new AutomationMlWorkflowStore(output); var uploads = new ArtifactUploadStore(output);
string Upload(byte[] bytes)
{ var status=uploads.Begin("source.aml",bytes.Length,Hash(bytes)); for(int offset=0;offset<bytes.Length;offset+=status.MaxChunkBytes) uploads.Write(status.UploadId,offset,Convert.ToBase64String(bytes,offset,Math.Min(status.MaxChunkBytes,bytes.Length-offset))); return uploads.Commit(status.UploadId).ArtifactRef!; }
byte[] Download(string reference,string name)
{ using var buffer=new MemoryStream(); while(true) { var part=Json(store.ReadArtifact(reference,name,buffer.Length)); buffer.Write(Convert.FromBase64String(Text(part,"data"))); if(part.GetProperty("complete").GetBoolean()) { Check(Hash(buffer.ToArray())==Text(part,"sha256"),"independent transfer hash");return buffer.ToArray(); } } }
var upload=Upload(Bytes(source)); var first=Json(store.Inspect(upload)); string beforeRef=Text(first,"recordRef");
uploads.Release(upload["upload:".Length..]);store=new(output);
Check(Download(beforeRef,"original.aml").SequenceEqual(Bytes(source)),"original bytes survive upload release and store restart");
Check(Hash(Download(beforeRef,"receipt.json"))==beforeRef.Split(':')[2],"receipt binds immutable handle");
var resolved=store.ResolveDocument(beforeRef);
Check(Hash(File.ReadAllBytes(resolved.Path))==resolved.Sha256&&resolved.Sha256==Text(first,"sourceSha256"),"document resolves to its verified original for native import");
Check(Text(Json(store.InspectFile(resolved.Path,resolved.Sha256)),"sourceSha256")==resolved.Sha256,"pinned native export publishes as a document record");
Reject("AutomationMlInputChanged",()=>store.InspectFile(resolved.Path,new string('0',64)));
var page=Json(store.ReportPage(beforeRef,"/objects",count:1));
Check(page.GetProperty("rows").GetArrayLength()==1&&page.GetProperty("nextOffset").GetInt32()==1,"bounded pages continue");
var next=Json(store.Inspect(Upload(Bytes(source.Replace("<Value>12","<Value>16")))));
var diff=Json(store.Compare(beforeRef,Text(next,"recordRef")));string diffRef=Text(diff,"recordRef");
Check(diff.GetProperty("sourceBytesChanged").GetBoolean()&&!diff.GetProperty("importabilityCertified").GetBoolean(),"retained comparison remains offline");
Reject("AutomationMlInvalidReference",()=>store.Compare(diffRef,beforeRef));
Reject("AutomationMlInvalidReference",()=>store.Reopen(new string(':',1000000)));
Reject("AutomationMlInvalidSection",()=>store.ReportPage(beforeRef,"/sourceSha256"));
Reject("AutomationMlPageLimit",()=>store.ReportPage(beforeRef,"/objects",count:201));
Reject("AutomationMlArtifactMissing",()=>store.ReadArtifact(beforeRef,"../intent.json"));
var longValue=Json(store.Inspect(Upload(Bytes(Wrap("<InstanceHierarchy Name=\"Large\"><InternalElement Name=\"N\" ID=\"n\"><Attribute Name=\"Long\"><Value>"+new string('x',150000)+"</Value></Attribute></InternalElement></InstanceHierarchy>")))));
Check(Json(store.ReportPage(Text(longValue,"recordRef"),"/attributes")).GetProperty("rows")[0].GetProperty("omitted").GetBoolean(),"oversized row explicitly omitted but downloadable");
string original=Path.Combine(output,"automationml-workflow",beforeRef.Split(':')[1],"original.aml");File.AppendAllText(original," ");
Reject("AutomationMlIntegrityFailure",()=>store.Reopen(diffRef));File.WriteAllBytes(original,Bytes(source));
Check(Text(Json(store.Reopen(diffRef)),"state")=="complete","restored source revalidates exact comparison lineage");
Directory.CreateDirectory(Path.Combine(output,"automationml-workflow",new string('a',32)));
Check(Json(store.List()).GetProperty("rows").EnumerateArray().Any(r=>Text(r,"state")=="incomplete"),"incomplete publication discoverable");
File.AppendAllText(Path.Combine(output,"automationml-workflow",beforeRef.Split(':')[1],"report.json")," ");
Check(Json(store.List()).GetProperty("rows").EnumerateArray().Any(r=>Text(r,"state")=="integrity-failed"),"tampered report discoverable");
File.WriteAllText(Path.Combine(output,"verification.json"),JsonSerializer.Serialize(new {passed=true,checks,nativeExecuted=false,inputs},BridgeProtocol.Json));
Console.WriteLine($"{checks} AutomationML parser/comparison/workflow checks passed; no Siemens execution.");
