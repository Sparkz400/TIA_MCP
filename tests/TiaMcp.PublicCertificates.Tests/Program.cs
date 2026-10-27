using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core;
using TiaMcp.Core.Security;

if (args.Length != 1 || Directory.Exists(args[0])) throw new ArgumentException("Fresh evidence directory required.");
string root = Path.GetFullPath(args[0]); Directory.CreateDirectory(root); var checks = new List<string>();
void Check(bool ok, string label) { if (!ok) throw new Exception(label); checks.Add(label); }
void Reject(Action action, string label, string code = "InvalidPublicCertificateReview")
{ try { action(); } catch (BridgeException e) when (e.Code == code) { Check(true, label); return; } throw new Exception("Expected refusal: " + label); }
string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
byte[] Bytes(JsonObject p) => Encoding.UTF8.GetBytes(p.ToJsonString()+"\n");
const string project = "/projects/Fixture", hardware = project + "/devices/CPU/items/CPU";
var model = new JsonObject { ["schemaVersion"] = "project-model.v1", ["project"] = new JsonObject { ["projectPath"] = "synthetic.ap21", ["host"] = "synthetic" },
    ["nodes"] = new JsonArray(new JsonObject { ["nodeId"] = "project", ["objectPath"] = project, ["kind"] = "project", ["name"] = "Fixture", ["parentId"] = null, ["metadata"] = new JsonObject() },
    new JsonObject { ["nodeId"] = "cpu", ["objectPath"] = hardware, ["kind"] = "hardware", ["name"] = "CPU", ["parentId"] = "project", ["metadata"] = new JsonObject() }), ["edges"] = new JsonArray() };
var reviews = new ProjectReviewStore(root); string id = (string)reviews.Save(model, new(), new() { ["syntheticFixture"] = true })["reviewId"]!; var review = reviews.Read(id);
using var key=RSA.Create(2048);
var from=new DateTimeOffset(2026,1,1,0,0,0,TimeSpan.Zero);var until=new DateTimeOffset(2027,1,1,0,0,0,TimeSpan.Zero);
CertificateRequest Request(string name)
{
    var r=new CertificateRequest("CN="+name,key,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
    r.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature|X509KeyUsageFlags.KeyEncipherment,true));
    r.CertificateExtensions.Add(new X509BasicConstraintsExtension(false,false,0,true));
    r.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection{new Oid("1.3.6.1.5.5.7.3.1")},false));
    var san=new SubjectAlternativeNameBuilder();san.AddDnsName("fixture.invalid");san.AddIpAddress(System.Net.IPAddress.Parse("192.0.2.10"));san.AddEmailAddress("fixture@example.invalid");san.AddUri(new Uri("urn:fixture:cpu"));r.CertificateExtensions.Add(san.Build());
    r.CertificateExtensions.Add(new X509Extension("1.3.6.1.4.1.55555.1",[0x04,0x03,0x61,0x62,0x63],true));return r;
}
using var oldCertificate=Request("Before").CreateSelfSigned(from,until);using var newCertificate=Request("After").CreateSelfSigned(from,until);
byte[] der=oldCertificate.RawData;byte[] nextDer=newCertificate.RawData;string pem=oldCertificate.ExportCertificatePem();
JsonObject Phase(byte[] data,string format="der")=>new(){["association"]=new JsonObject{["storeId"]="local:CPU",["certificateId"]="declared-id",["usage"]="webServer"},["format"]=format,["contentBase64"]=Convert.ToBase64String(data),["expectedSha256"]=Hash(der),["unavailableReason"]=null};
var packet=new JsonObject{["schemaVersion"]="public-certificate-review-request.v1",["reviewId"]=id,["owner"]=new JsonObject{["nodeId"]="cpu",["objectPath"]=hardware},["evaluatedAtUtc"]="2026-10-17T00:00:00Z",["afterKind"]="proposal",["before"]=Phase(der),["after"]=Phase(nextDer)};
packet["after"]!["association"]!["certificateId"]="replacement";
JsonObject Mutate(Action<JsonObject> edit){var p=packet.DeepClone().AsObject();edit(p);return p;}
JsonObject Build(JsonObject? p=null,JsonObject? r=null)=>PublicCertificateReview.Build(id,r??review,p??packet);
JsonNode Cert(JsonObject report,int index=0)=>report["observations"]![index]!["certificate"]!;
var report=Build();
Check((string)Cert(report)["sha256"]! ==Hash(der),"Exact public DER fingerprint");
Check((bool)report["summary"]!["associationChanged"]!&&!(bool)report["summary"]!["samePublicCertificate"]!,"Replacement and declared association change remain distinct");
Check((int)report["summary"]!["fingerprintMismatchCount"]! ==1,"Mismatched supplied expected fingerprint is a finding");
Check((string)Cert(report)["subject"]! =="CN=Before"&&(string)Cert(report)["issuer"]! =="CN=Before","Independent declared subject and issuer oracle");
Check((string)Cert(report)["notBeforeUtc"]! =="2026-01-01T00:00:00Z"&&(string)Cert(report)["notAfterUtc"]! =="2027-01-01T00:00:00Z","Exact UTC validity bounds");
var ext=Cert(report)["extensions"]!.AsArray();
JsonNode Decoded(string oid)=>ext.Single(e=>(string?)e!["oid"]==oid)!["decoded"]!;
Check(Decoded("2.5.29.15")["keyUsages"]!.AsArray().Select(n=>(string)n!).SequenceEqual(new[]{"digitalSignature","keyEncipherment"}),"Known key usage bits decoded without native policy");
Check(!(bool)Decoded("2.5.29.19")["certificateAuthority"]!&&Decoded("2.5.29.19")["pathLength"] is null,"Basic constraints retained");
Check((string)Decoded("2.5.29.37")["usageOids"]![0]! =="1.3.6.1.5.5.7.3.1","EKU OID retained exactly");
Check(Decoded("2.5.29.17")["names"]!.AsArray().Select(n=>(string)n!["value"]!).SequenceEqual(new[]{"fixture.invalid","192.0.2.10","fixture@example.invalid","urn:fixture:cpu"}),"DNS IP email URI names preserve independent source values");
var opaque=ext.Single(e=>(string?)e!["oid"]=="1.3.6.1.4.1.55555.1")!;Check((bool)opaque["critical"]!&&(string)opaque["status"]! =="opaque"&&(string)opaque["rawBase64"]! =="BANhYmM=","Unknown critical extension preserved without trust assertion");
foreach(string flag in new[]{"nativeQualified","sourceAuthenticityVerified","associationVerified","signatureVerified","chainVerified","revocationChecked","endpointAuthenticated","privateKeyAvailabilityVerified","usagePolicyVerified"})Check(!(bool)report[flag]!,"No promotion of "+flag);
var equivalent=Build(Mutate(p=>p["after"]=Phase(Encoding.UTF8.GetBytes(" \r\n"+pem+"\n\t"),"pem")));
Check((bool)equivalent["summary"]!["samePublicCertificate"]!&&equivalent["changedFields"]!.AsArray().Any(n=>(string?)n=="inputSha256"),"PEM formatting changes original bytes without changing DER identity");
foreach(var (time,status) in new[]{("2025-12-31T23:59:59Z","notYetValid"),("2026-01-01T00:00:00Z","withinInterval"),("2027-01-01T00:00:00Z","withinInterval"),("2027-01-01T00:00:01Z","expired")})Check((string)Cert(Build(Mutate(p=>p["evaluatedAtUtc"]=time)))["validity"]! ==status,"Explicit evaluation "+time);
var unknown=Build(Mutate(p=>{p["before"]!["contentBase64"]=null;p["before"]!["format"]=null;p["before"]!["unavailableReason"]="protected store";}));
Check((int)unknown["summary"]!["unavailableCount"]! ==1&&unknown["summary"]!["samePublicCertificate"] is null,"Missing certificate stays unknown");
Check((string)Build(Mutate(p=>p["after"]!["expectedSha256"]=null))["observations"]![1]!["fingerprintMatch"]! =="notProvided","Absent expected fingerprint stays absent");
Check(Build(Mutate(p=>{p["before"]!["association"]!["storeId"]=null;p["before"]!["association"]!["certificateId"]=null;}))["observations"]![0]!["association"]!["storeId"] is null,"Unknown native association represented by null");
Check((string)Build(Mutate(p=>{p["owner"]!["nodeId"]="project";p["owner"]!["objectPath"]=project;}))["owner"]!["nodeId"]! =="project","Public global-store declaration can select exact project owner");
using(var ec=ECDsa.Create(ECCurve.NamedCurves.nistP256))
{var r=new CertificateRequest("CN=EC",ec,HashAlgorithmName.SHA256);using var c=r.CreateSelfSigned(from,until);Check((string)Cert(Build(Mutate(p=>p["before"]=Phase(c.RawData))))["publicKeyAlgorithmOid"]! =="1.2.840.10045.2.1","ECDSA public certificate accepted without private key");}
byte[] changedSignature=(byte[])der.Clone();changedSignature[^1]^=1;Check(!(bool)Build(Mutate(p=>p["before"]=Phase(changedSignature)))["signatureVerified"]!,"Parseable changed signature never receives verification credit");
var store=new PublicCertificateWorkflowStore(root);
Dictionary<string,string> Files()=>Directory.GetFiles(root,"*",SearchOption.AllDirectories).ToDictionary(p=>p,p=>Hash(File.ReadAllBytes(p)));
foreach(var (label,data,format) in new[]{("trailing DER",der.Concat(new byte[]{0}).ToArray(),"der"),("two DER",der.Concat(der).ToArray(),"der"),("two PEM",Encoding.UTF8.GetBytes(pem+"\n"+pem),"pem"),("PEM text",Encoding.UTF8.GetBytes(pem+"\nREJECTED-SENTINEL"),"pem"),("private PEM suffix",Encoding.UTF8.GetBytes(pem+"\n"+key.ExportPkcs8PrivateKeyPem()),"pem"),("private PEM prefix",Encoding.UTF8.GetBytes(key.ExportPkcs8PrivateKeyPem()+"\n"+pem),"pem"),("PFX",oldCertificate.Export(X509ContentType.Pkcs12,"fixture-only"),"der"),("PKCS7",new X509Certificate2Collection(oldCertificate).Export(X509ContentType.Pkcs7)!,"der"),("wrong format",Encoding.UTF8.GetBytes(pem),"der"),("malformed DER",new byte[]{0x30,0x01,0xff},"der"),("invalid UTF8 PEM",new byte[]{0xff},"pem")})
{
    var prior=Files();Reject(()=>store.Review(Bytes(Mutate(p=>p["after"]=Phase(data,format)))),label+" refused before retention");Check(prior.OrderBy(p=>p.Key).SequenceEqual(Files().OrderBy(p=>p.Key)),label+" leaves retained files unchanged");CryptographicOperations.ZeroMemory(data);
}
foreach(var (label,edit) in new (string,Action<JsonObject>)[]{("unknown secret field",p=>p["privateKey"]="REJECTED-SENTINEL"),("foreign review",p=>p["reviewId"]=new string('a',64)),("owner ID",p=>p["owner"]!["nodeId"]="other"),("owner path",p=>p["owner"]!["objectPath"]="other"),("missing evaluation",p=>p["evaluatedAtUtc"]=null),("local time",p=>p["evaluatedAtUtc"]="2026-10-17T00:00:00"),("offset time",p=>p["evaluatedAtUtc"]="2026-10-17T00:00:00+00:00"),("invalid date",p=>p["evaluatedAtUtc"]="2026-02-30T00:00:00Z"),("bad usage",p=>p["after"]!["association"]!["usage"]="nativeApproved"),("bad expected hash",p=>p["after"]!["expectedSha256"]="bad"),("base64 whitespace",p=>p["after"]!["contentBase64"]=" "+p["after"]!["contentBase64"]!.GetValue<string>()),("unavailable format",p=>p["after"]!["contentBase64"]=null),("unexpected reason",p=>p["after"]!["unavailableReason"]="bad"),("bad base64",p=>p["after"]!["contentBase64"]="???"),("unknown after kind",p=>p["afterKind"]="install")})Reject(()=>Build(Mutate(edit)),label);
var virtualReview=review.DeepClone().AsObject();virtualReview["model"]!["nodes"]![1]!["origin"]="virtual";Reject(()=>Build(r:virtualReview),"Virtual owner refused");
Reject(()=>PublicCertificateReview.Parse(Encoding.UTF8.GetBytes("{\"x\":1,\"x\":2}")),"Duplicate JSON fields");Reject(()=>PublicCertificateReview.Parse([0xff]),"Invalid JSON UTF8");Reject(()=>PublicCertificateReview.Parse(new byte[PublicCertificateReview.InputLimit+1]),"Input byte bound");
var malformed=Request("Malformed");malformed.CertificateExtensions.Add(new X509Extension("2.5.29.17",[0x30,0x03,0x02,0x01,0x01],false));
// Rebuild an extension set to avoid a request-side duplicate check masking the parser test.
malformed.CertificateExtensions.Remove(malformed.CertificateExtensions.Cast<X509Extension>().First(e=>e.Oid!.Value=="2.5.29.17"));using(var c=malformed.CreateSelfSigned(from,until))Reject(()=>Build(Mutate(p=>p["after"]=Phase(c.RawData))),"Malformed known SAN tag");
var trailing=Request("Trailing");var ku=trailing.CertificateExtensions.Cast<X509Extension>().Single(e=>e.Oid!.Value=="2.5.29.15");trailing.CertificateExtensions.Remove(ku);trailing.CertificateExtensions.Add(new X509Extension("2.5.29.15",ku.RawData.Concat(new byte[]{0}).ToArray(),true));using(var c=trailing.CreateSelfSigned(from,until))Reject(()=>Build(Mutate(p=>p["after"]=Phase(c.RawData))),"Trailing known extension payload");
var dup=DuplicateFirstExtension(der);Reject(()=>Build(Mutate(p=>p["after"]=Phase(dup))),"Duplicate extension OID");
foreach(bool insideTbs in new[]{false,true})Reject(()=>Build(Mutate(p=>p["after"]=Phase(AppendAsnField(der,insideTbs)))),"Extra ASN.1 field inside "+(insideTbs?"TBS":"certificate"));
var namedReader=new AsnReader(der,AsnEncodingRules.DER).ReadSequence();var namedTbs=namedReader.ReadSequence();var namedWriter=new AsnWriter(AsnEncodingRules.DER);namedWriter.PushSequence();namedWriter.PushSequence();int nameIndex=0;while(namedTbs.HasData){byte[] field=namedTbs.ReadEncodedValue().ToArray();namedWriter.WriteEncodedValue(nameIndex++==3?AppendAsnField(field,false):field);}namedWriter.PopSequence();while(namedReader.HasData)namedWriter.WriteEncodedValue(namedReader.ReadEncodedValue().Span);namedWriter.PopSequence();Reject(()=>Build(Mutate(p=>p["after"]=Phase(namedWriter.Encode()))),"Malformed issuer RDN wrapper");
var excessive=Request("Too many extensions");for(int i=0;i<129;i++)excessive.CertificateExtensions.Add(new X509Extension("1.3.6.1.4.1.55555.3."+i,[0x05,0x00],false));using(var c=excessive.CreateSelfSigned(from,until))Reject(()=>Build(Mutate(p=>p["after"]=Phase(c.RawData))),"Extension count bound");
var excessiveNames=Request("Too many SANs");excessiveNames.CertificateExtensions.Remove(excessiveNames.CertificateExtensions.Cast<X509Extension>().Single(e=>e.Oid!.Value=="2.5.29.17"));var largeSan=new SubjectAlternativeNameBuilder();for(int i=0;i<513;i++)largeSan.AddDnsName("name"+i+".invalid");excessiveNames.CertificateExtensions.Add(largeSan.Build());using(var c=excessiveNames.CreateSelfSigned(from,until))Reject(()=>Build(Mutate(p=>p["after"]=Phase(c.RawData))),"SAN count bound");
var excessiveUsages=Request("Too many EKUs");excessiveUsages.CertificateExtensions.Remove(excessiveUsages.CertificateExtensions.Cast<X509Extension>().Single(e=>e.Oid!.Value=="2.5.29.37"));var usageOids=new OidCollection();for(int i=0;i<129;i++)usageOids.Add(new Oid("1.3.6.1.4.1.55555.4."+i));excessiveUsages.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(usageOids,false));using(var c=excessiveUsages.CreateSelfSigned(from,until))Reject(()=>Build(Mutate(p=>p["after"]=Phase(c.RawData))),"EKU count bound");
var excessiveBytes=Request("Too large DER");excessiveBytes.CertificateExtensions.Add(new X509Extension("1.3.6.1.4.1.55555.5",new byte[66*1024],false));using(var c=excessiveBytes.CreateSelfSigned(from,until))Reject(()=>Build(Mutate(p=>p["after"]=Phase(c.RawData))),"DER byte bound");
byte[] originalInput=Bytes(packet);var receipt=store.Review(originalInput);string record=(string)receipt["recordId"]!;
Check(JsonNode.DeepEquals(receipt,store.Review(originalInput)),"Idempotent direct public review");Check(ProjectReviewStore.ReadArtifact(root,receipt["input"]!.AsObject()).SequenceEqual(originalInput),"Exact direct public request retained");Check(JsonNode.DeepEquals(receipt,new PublicCertificateWorkflowStore(root).Page(record)),"Fresh store revalidates immutable review");
for(int i=0;i<2;i++)Check(JsonNode.DeepEquals(store.Page(record,"observations",i,1)["rows"]![0]!["value"],report["observations"]![i]),"Exact observation page "+i);
foreach(var (section,offset,count) in new[]{("bad",0,1),("observations",3,1),("observations",-1,1),("observations",0,201)})Reject(()=>store.Page(record,section,offset,count),"Bad page refused","InvalidRequest");
foreach(string field in new[]{"input","reportArtifact"}){string path=Path.Combine(root,(string)receipt[field]!["path"]!);byte[] bytes=File.ReadAllBytes(path);File.WriteAllText(path,"tampered");Reject(()=>store.Page(record),"Corrupt "+field,"ArtifactChanged");File.WriteAllBytes(path,bytes);}
var forged=JsonNode.Parse(ProjectReviewStore.ReadArtifact(root,receipt["reportArtifact"]!.AsObject()))!;forged["report"]!["chainVerified"]=true;byte[] forgedBytes=JsonSerializer.SerializeToUtf8Bytes(forged,BridgeProtocol.Json);File.WriteAllBytes(Path.Combine(root,"public-certificate-reviews",Hash(forgedBytes)+".json"),forgedBytes);Reject(()=>store.Page(Hash(forgedBytes)),"Rehashed trust claim refused","ArtifactChanged");
var huge=Request("Huge");huge.CertificateExtensions.Add(new X509Extension("1.3.6.1.4.1.55555.2",new byte[50*1024],false));using(var c=huge.CreateSelfSigned(from,until)){var wide=store.Review(Bytes(Mutate(p=>p["after"]=Phase(c.RawData))));var page=store.Page((string)wide["recordId"]!,"observations",1,1);Check(JsonSerializer.SerializeToUtf8Bytes(page,BridgeProtocol.Json).Length<128*1024,"Large opaque extension stays within page budget");}
string saved=Path.Combine(root,"project-reviews",id+".json");byte[] savedBytes=File.ReadAllBytes(saved);File.WriteAllText(saved,"tampered");Reject(()=>store.Page(record),"Saved owner corruption refused","ArtifactChanged");File.WriteAllBytes(saved,savedBytes);
// certificate_import worker payload: exact public DER from a committed upload; key containers, keys and extra fields refuse.
string upload=Path.Combine(root,"import-upload.bin");
JsonObject Import(byte[] data,string format,Action<JsonObject>? edit=null){File.WriteAllBytes(upload,data);var p=new JsonObject{["hardwarePath"]=hardware,["uploadRef"]="upload:fixture",["format"]=format};edit?.Invoke(p);return PublicCertificateReview.ImportPayload(JsonSerializer.SerializeToElement(p),_=>(upload,"certificate",Hash(data)));}
var imported=Import(der,"der");Check(imported.Count==5&&(string)imported["sha256"]! ==Hash(der)&&(string)imported["derBase64"]! ==Convert.ToBase64String(der),"Import payload carries only exact public DER and its fingerprint");
Check((string)Import(Encoding.UTF8.GetBytes(" \r\n"+pem+"\n"),"pem")["derBase64"]! ==Convert.ToBase64String(der),"PEM upload imports the same DER");
foreach(var (label,data,format) in new[]{("PFX",oldCertificate.Export(X509ContentType.Pkcs12,"fixture-only"),"der"),("PFX as PEM",oldCertificate.Export(X509ContentType.Pkcs12,"fixture-only"),"pem"),("certificate with private key PEM",Encoding.UTF8.GetBytes(pem+"\n"+key.ExportPkcs8PrivateKeyPem()),"pem"),("private key PEM",Encoding.UTF8.GetBytes(key.ExportPkcs8PrivateKeyPem()),"pem"),("encrypted private key PEM",Encoding.UTF8.GetBytes(key.ExportEncryptedPkcs8PrivateKeyPem("fixture-only",new PbeParameters(PbeEncryptionAlgorithm.Aes128Cbc,HashAlgorithmName.SHA256,1))),"pem"),("PKCS7",new X509Certificate2Collection(oldCertificate).Export(X509ContentType.Pkcs7)!,"der"),("trailing DER",der.Concat(new byte[]{0}).ToArray(),"der"),("PEM as DER",Encoding.UTF8.GetBytes(pem),"der"),("unknown format",der,"pfx")})
    Reject(()=>Import(data,format),"Import refuses "+label);
foreach(var (label,edit) in new (string,Action<JsonObject>)[]{("password",p=>p["password"]="REJECTED-SENTINEL"),("private key",p=>p["privateKey"]="REJECTED-SENTINEL"),("Host-owned DER",p=>p["derBase64"]=Convert.ToBase64String(der)),("missing upload",p=>p.Remove("uploadRef"))})
    Reject(()=>Import(der,"der",edit),"Import payload refuses "+label);
Reject(()=>Import(new byte[96*1024+1],"der"),"Import upload byte bound");File.Delete(upload);
string filler=Path.Combine(root,"public-certificate-reviews","capacity.tmp");using(var f=File.Create(filler))f.SetLength(512L*1024*1024);Reject(()=>store.Review(Bytes(Mutate(p=>p["afterKind"]="observation"))),"Quota counts incomplete files","ArtifactCapacity");File.Delete(filler);
File.WriteAllText(Path.Combine(root,"mcp-fixture.json"),JsonSerializer.Serialize(new{artifactRoot=root,reviewId=id,packet,beforeSha256=Hash(der),afterSha256=Hash(nextDer),extraEnvelopeBase64=Convert.ToBase64String(AppendAsnField(der,false)),extraTbsBase64=Convert.ToBase64String(AppendAsnField(der,true)),duplicateExtensionBase64=Convert.ToBase64String(dup),malformedIssuerBase64=Convert.ToBase64String(namedWriter.Encode())},BridgeProtocol.Json));
File.WriteAllText(Path.Combine(root,"verification.json"),JsonSerializer.Serialize(new{completed=true,nativeExecuted=false,count=checks.Count,checks},BridgeProtocol.Json));Console.WriteLine($"PASS public certificates: {checks.Count} checks; generated public fixtures, no native or trust qualification.");

static byte[] DuplicateFirstExtension(byte[] input)
{
    var reader=new AsnReader(input,AsnEncodingRules.DER);var cert=reader.ReadSequence();var tbs=cert.ReadSequence();var writer=new AsnWriter(AsnEncodingRules.DER);writer.PushSequence();writer.PushSequence();
    while(tbs.HasData)
    {
        if(tbs.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific,3)))
        {var explicitExtensions=tbs.ReadSequence(new Asn1Tag(TagClass.ContextSpecific,3,true));var extensions=explicitExtensions.ReadSequence();var rows=new List<byte[]>();while(extensions.HasData)rows.Add(extensions.ReadEncodedValue().ToArray());writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific,3,true));writer.PushSequence();foreach(var row in rows)writer.WriteEncodedValue(row);writer.WriteEncodedValue(rows[0]);writer.PopSequence();writer.PopSequence(new Asn1Tag(TagClass.ContextSpecific,3,true));}
        else writer.WriteEncodedValue(tbs.ReadEncodedValue().Span);
    }
    writer.PopSequence();while(cert.HasData)writer.WriteEncodedValue(cert.ReadEncodedValue().Span);writer.PopSequence();return writer.Encode();
}

static byte[] AppendAsnField(byte[] input,bool insideTbs)
{
    var reader=new AsnReader(input,AsnEncodingRules.DER);var cert=reader.ReadSequence();var writer=new AsnWriter(AsnEncodingRules.DER);writer.PushSequence();
    if(insideTbs){var tbs=cert.ReadSequence();writer.PushSequence();while(tbs.HasData)writer.WriteEncodedValue(tbs.ReadEncodedValue().Span);writer.WriteNull();writer.PopSequence();}
    while(cert.HasData)writer.WriteEncodedValue(cert.ReadEncodedValue().Span);if(!insideTbs)writer.WriteNull();writer.PopSequence();return writer.Encode();
}
