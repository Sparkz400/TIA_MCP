using System.Formats.Asn1;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TiaMcp.Core.Security;

/// <summary>Single public certificate intake and supplied association review; no trust or network operation.</summary>
public static class PublicCertificateReview
{
    public const int InputLimit = 384 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private const string TimeFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";
    public static JsonObject Parse(byte[] bytes)
    {
        if (bytes.Length is < 1 or > InputLimit) throw Error("Request must contain 1 byte..384 KiB.");
        try
        {
            string text = Utf8.GetString(bytes); using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 16 });
            void Unique(JsonElement element)
            {
                if (element.ValueKind == JsonValueKind.Object)
                {
                    var names = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var p in element.EnumerateObject()) { _ = Utf8.GetByteCount(p.Name); if (!names.Add(p.Name)) throw Error("Duplicate JSON field."); Unique(p.Value); }
                }
                else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) Unique(item);
                else if (element.ValueKind == JsonValueKind.String) _ = Utf8.GetByteCount(element.GetString()!);
            }
            Unique(document.RootElement); return JsonNode.Parse(text) as JsonObject ?? throw Error("Expected a request object.");
        }
        catch (Exception e) when (e is JsonException or DecoderFallbackException) { throw Error("Invalid UTF-8 JSON request."); }
    }
    public static string ReviewId(JsonObject request) => Text(request, "reviewId");
    public static JsonObject Build(string reviewId, JsonObject review, JsonObject request)
    {
        Fields(request, "schemaVersion reviewId owner evaluatedAtUtc afterKind before after");
        if (Text(request, "schemaVersion") != "public-certificate-review-request.v1" || ReviewId(request) != reviewId) throw Error("Select this exact saved review and schema.");
        string evaluation = Text(request, "evaluatedAtUtc"), afterKind = Text(request, "afterKind");
        if (!DateTimeOffset.TryParseExact(evaluation, TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at)) throw Error("Evaluation time must be explicit UTC YYYY-MM-DDTHH:mm:ssZ.");
        if (afterKind is not ("observation" or "proposal")) throw Error("afterKind must be observation or proposal.");
        var owner = Object(request["owner"]); Fields(owner, "nodeId objectPath"); string path = Text(owner, "objectPath"), nodeId = Text(owner, "nodeId");
        var matches = review["model"]!["nodes"]!.AsArray().Where(n => (string?)n?["objectPath"] == path).ToArray();
        if (matches.Length != 1 || (string?)matches[0]!["nodeId"] != nodeId || (string?)matches[0]!["origin"] == "virtual" || (string?)matches[0]!["kind"] is not ("hardware" or "project")) throw Error("Select an exact nonvirtual captured project or hardware owner.");
        var before = Inspect(Object(request["before"]), "before", at); var after = Inspect(Object(request["after"]), "after", at);
        var changes = new JsonArray();
        foreach (string field in new[] { "association", "expectedSha256", "status", "unavailableReason", "inputSha256", "certificate" })
            if (!JsonNode.DeepEquals(before[field], after[field])) changes.Add(field);
        bool both = before["certificate"] is JsonObject && after["certificate"] is JsonObject;
        return new JsonObject { ["schemaVersion"] = "public-certificate-review.v1", ["reviewId"] = reviewId, ["owner"] = owner.DeepClone(), ["evaluatedAtUtc"] = evaluation, ["afterKind"] = afterKind,
            ["observations"] = new JsonArray(before, after), ["changedFields"] = changes,
            ["summary"] = new JsonObject { ["samePublicCertificate"] = both ? JsonNode.DeepEquals(before["certificate"]!["sha256"], after["certificate"]!["sha256"]) : null,
                ["associationChanged"] = !JsonNode.DeepEquals(before["association"], after["association"]),
                ["unavailableCount"] = new[] { before, after }.Count(p => p["certificate"] is null),
                ["fingerprintMismatchCount"] = new[] { before, after }.Count(p => (string?)p["fingerprintMatch"] == "mismatch"),
                ["outsideValidityCount"] = new[] { before, after }.Count(p => p["certificate"] is JsonObject c && (string?)c["validity"] != "withinInterval") },
            ["nativeQualified"] = false, ["sourceAuthenticityVerified"] = false, ["associationVerified"] = false, ["signatureVerified"] = false,
            ["chainVerified"] = false, ["revocationChecked"] = false, ["endpointAuthenticated"] = false, ["privateKeyAvailabilityVerified"] = false, ["usagePolicyVerified"] = false,
            ["limits"] = "Strict single public certificate per supplied phase. A parsed certificate or matching caller-supplied fingerprint does not establish ownership, trust, signature validity, endpoint identity, key availability or native usage policy. No chain building, network, certificate-store access, key import/export, native mutation or deployment. Unknown extensions are retained as opaque public bytes. Identifier/extension content is not a secret detector; send only public metadata." };
    }
    private static JsonObject Inspect(JsonObject input, string phase, DateTimeOffset at)
    {
        Fields(input, "association format contentBase64 expectedSha256 unavailableReason");
        var association = Object(input["association"]); Fields(association, "storeId certificateId usage");
        foreach (string key in new[] { "storeId", "certificateId" }) if (association[key] is not null) Text(association, key);
        if (Text(association, "usage") is not ("webServer" or "opcUaServer" or "opcUaClient" or "tls" or "unspecified")) throw Error("Unknown supplied usage label.");
        string? expected = input["expectedSha256"] is null ? null : Text(input, "expectedSha256");
        if (expected is not null && (expected.Length != 64 || expected.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))) throw Error("Expected fingerprint must be lowercase SHA-256 or null.");
        var result = new JsonObject { ["phase"] = phase, ["association"] = association.DeepClone(), ["expectedSha256"] = expected,
            ["status"] = "unavailable", ["unavailableReason"] = input["unavailableReason"]?.DeepClone(), ["inputSha256"] = null,
            ["inputBytes"] = null, ["format"] = input["format"]?.DeepClone(), ["fingerprintMatch"] = "unavailable", ["certificate"] = null };
        if (input["contentBase64"] is null)
        { if (input["format"] is not null) throw Error("Unavailable content must have null format."); Text(input, "unavailableReason"); return result; }
        if (input["unavailableReason"] is not null) throw Error("Available content cannot also claim an unavailable reason.");
        string format = Text(input, "format"); if (format is not ("der" or "pem")) throw Error("Select explicit der or pem format.");
        try
        {
            string encoded = Text(input, "contentBase64", 128 * 1024); byte[] bytes = Convert.FromBase64String(encoded);
            if (bytes.Length is < 1 or > 96 * 1024 || Convert.ToBase64String(bytes) != encoded) throw Error("Content must be canonical base64 of 1 byte..96 KiB.");
            byte[] der = bytes;
            if (format == "pem")
            {
                string pem = Utf8.GetString(bytes);
                if (!PemEncoding.TryFind(pem, out var fields) || pem[fields.Label] != "CERTIFICATE") throw Error("Expected exactly one public CERTIFICATE PEM block.");
                var (offset, length) = fields.Location.GetOffsetAndLength(pem.Length);
                if (pem[..offset].Any(c => c is not (' ' or '\t' or '\r' or '\n')) || pem[(offset + length)..].Any(c => c is not (' ' or '\t' or '\r' or '\n'))) throw Error("Extra data around the certificate is refused.");
                der = Convert.FromBase64String(pem[fields.Base64Data]);
            }
            if (der.Length is < 1 or > 64 * 1024) throw Error("Public DER certificate exceeds 64 KiB.");
            ValidateEnvelope(der);
            using var cert = X509CertificateLoader.LoadCertificate(der);
            if (cert.HasPrivateKey || !cert.RawData.AsSpan().SequenceEqual(der)) throw Error("Only an exact single public DER certificate is accepted.");
            var extensions = new JsonArray(); var seen = new HashSet<string>(StringComparer.Ordinal);
            if (cert.Extensions.Count > 128) throw Error("Certificate exceeds 128 extensions.");
            foreach (X509Extension extension in cert.Extensions)
            {
                string oid = extension.Oid?.Value ?? throw Error("Extension OID is absent."); if (!seen.Add(oid)) throw Error("Duplicate extension OIDs are refused.");
                var decoded = DecodeExtension(oid, extension.RawData);
                extensions.Add(new JsonObject { ["oid"] = oid, ["critical"] = extension.Critical, ["rawBase64"] = Convert.ToBase64String(extension.RawData), ["decoded"] = decoded, ["status"] = decoded is null ? "opaque" : "decoded" });
            }
            var from = cert.NotBefore.ToUniversalTime(); var until = cert.NotAfter.ToUniversalTime();
            string hash = Convert.ToHexStringLower(SHA256.HashData(der));
            result["certificate"] = new JsonObject { ["sha256"] = hash, ["derBytes"] = der.Length, ["version"] = cert.Version, ["serialNumber"] = cert.SerialNumber,
                ["subject"] = cert.Subject, ["issuer"] = cert.Issuer, ["subjectNameDerBase64"] = Convert.ToBase64String(cert.SubjectName.RawData), ["issuerNameDerBase64"] = Convert.ToBase64String(cert.IssuerName.RawData),
                ["notBeforeUtc"] = from.ToString(TimeFormat, CultureInfo.InvariantCulture), ["notAfterUtc"] = until.ToString(TimeFormat, CultureInfo.InvariantCulture),
                ["validity"] = from > until ? "invalidInterval" : at.UtcDateTime < from ? "notYetValid" : at.UtcDateTime > until ? "expired" : "withinInterval",
                ["signatureAlgorithmOid"] = cert.SignatureAlgorithm?.Value, ["publicKeyAlgorithmOid"] = cert.PublicKey.Oid?.Value,
                ["publicKeyParametersBase64"] = cert.PublicKey.EncodedParameters is { } parameters ? Convert.ToBase64String(parameters.RawData) : null, ["publicKeyValueBase64"] = Convert.ToBase64String(cert.PublicKey.EncodedKeyValue.RawData), ["extensions"] = extensions };
            result["status"] = "suppliedPublicCertificate"; result["inputSha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes)); result["inputBytes"] = bytes.Length;
            result["fingerprintMatch"] = expected is null ? "notProvided" : expected == hash ? "matches" : "mismatch"; return result;
        }
        catch (Exception e) when (e is CryptographicException or AsnContentException or FormatException or DecoderFallbackException or ArgumentException)
        { throw Error("Invalid or unsupported single public certificate payload; container/key/trailing content is not accepted."); }
    }
    private static void ValidateEnvelope(byte[] der)
    {
        // The platform loader accepts surplus fields inside an otherwise recognizable certificate.
        var reader = new AsnReader(der, AsnEncodingRules.DER); var certificate = reader.ReadSequence(); reader.ThrowIfNotEmpty();
        var tbs = certificate.ReadSequence();
        if (tbs.HasData && tbs.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 0)))
        { var version = tbs.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true)); var value = version.ReadInteger(); if (value < 0 || value > 2) throw Error("Unsupported certificate version."); version.ThrowIfNotEmpty(); }
        _ = tbs.ReadIntegerBytes(); Algorithm(tbs); Name(tbs);
        var validity = tbs.ReadSequence(); Time(validity); Time(validity); validity.ThrowIfNotEmpty();
        Name(tbs); var key = tbs.ReadSequence(); Algorithm(key); _ = key.ReadBitString(out _); key.ThrowIfNotEmpty();
        foreach (int tag in new[] { 1, 2 })
            if (tbs.HasData && tbs.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, tag))) _ = tbs.ReadBitString(out _, new Asn1Tag(TagClass.ContextSpecific, tag));
        if (tbs.HasData && tbs.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 3)))
        {
            var wrapper = tbs.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 3, true)); var extensions = wrapper.ReadSequence(); wrapper.ThrowIfNotEmpty();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (extensions.HasData)
            {
                var extension = extensions.ReadSequence(); string oid = extension.ReadObjectIdentifier();
                if (seen.Count == 128 || !seen.Add(oid)) throw Error("Duplicate or excessive extensions.");
                if (extension.HasData && extension.PeekTag().HasSameClassAndValue(Asn1Tag.Boolean)) _ = extension.ReadBoolean();
                _ = extension.ReadOctetString(); extension.ThrowIfNotEmpty();
            }
        }
        tbs.ThrowIfNotEmpty(); Algorithm(certificate); _ = certificate.ReadBitString(out _); certificate.ThrowIfNotEmpty();
        static void Algorithm(AsnReader source)
        { var algorithm = source.ReadSequence(); _ = algorithm.ReadObjectIdentifier(); if (algorithm.HasData) _ = algorithm.ReadEncodedValue(); algorithm.ThrowIfNotEmpty(); }
        static void Time(AsnReader source)
        { if (source.PeekTag().HasSameClassAndValue(Asn1Tag.UtcTime)) _ = source.ReadUtcTime(); else _ = source.ReadGeneralizedTime(); }
        static void Name(AsnReader source)
        {
            var name = source.ReadSequence();
            while (name.HasData)
            {
                var rdn = name.ReadSetOf(); if (!rdn.HasData) throw Error("Empty relative distinguished name.");
                while (rdn.HasData) { var attribute = rdn.ReadSequence(); _ = attribute.ReadObjectIdentifier(); _ = attribute.ReadEncodedValue(); attribute.ThrowIfNotEmpty(); }
            }
        }
    }
    private static JsonObject? DecodeExtension(string oid, byte[] bytes)
    {
        if (oid is not ("2.5.29.15" or "2.5.29.17" or "2.5.29.19" or "2.5.29.37")) return null;
        var reader = new AsnReader(bytes, AsnEncodingRules.DER); var result = new JsonObject();
        if (oid == "2.5.29.15")
        {
            byte[] bits = reader.ReadBitString(out int unused); var usages = new JsonArray();
            string[] names = ["digitalSignature", "contentCommitment", "keyEncipherment", "dataEncipherment", "keyAgreement", "keyCertSign", "cRLSign", "encipherOnly", "decipherOnly"];
            bool unknown = false;
            for (int i = 0; i < bits.Length * 8 - unused; i++) if ((bits[i / 8] & (128 >> (i % 8))) != 0) { if (i < names.Length) usages.Add(names[i]); else unknown = true; }
            result["keyUsages"] = usages; result["unknownBitsPresent"] = unknown;
        }
        else
        {
            var sequence = reader.ReadSequence();
            if (oid == "2.5.29.19")
            {
                bool ca = sequence.HasData && sequence.PeekTag().HasSameClassAndValue(Asn1Tag.Boolean) && sequence.ReadBoolean();
                string? path = null;
                if (sequence.HasData) { var length = sequence.ReadInteger(); if (length.Sign < 0) throw Error("Negative basic-constraints path length."); path = length.ToString(CultureInfo.InvariantCulture); }
                result["certificateAuthority"] = ca; result["pathLength"] = path; result["pathLengthWithoutCa"] = path is not null && !ca;
            }
            else if (oid == "2.5.29.37")
            {
                var usages = new JsonArray(); while (sequence.HasData) { if (usages.Count == 128) throw Error("Extended usage exceeds 128 OIDs."); usages.Add(sequence.ReadObjectIdentifier()); } result["usageOids"] = usages;
            }
            else
            {
                var names = new JsonArray();
                while (sequence.HasData)
                {
                    if (names.Count == 512) throw Error("Subject alternative names exceed 512 entries.");
                    var tag = sequence.PeekTag(); if (tag.TagClass != TagClass.ContextSpecific || tag.TagValue is < 0 or > 8) throw Error("Invalid alternative-name tag."); var name = new JsonObject { ["tagClass"] = (int)tag.TagClass, ["tag"] = tag.TagValue };
                    if (tag.TagClass == TagClass.ContextSpecific && tag.TagValue is 1 or 2 or 6)
                    { name["kind"] = tag.TagValue == 1 ? "email" : tag.TagValue == 2 ? "dns" : "uri"; name["value"] = sequence.ReadCharacterString(UniversalTagNumber.IA5String, tag); }
                    else if (tag.TagClass == TagClass.ContextSpecific && tag.TagValue == 7)
                    { byte[] address = sequence.ReadOctetString(tag); if (address.Length is not (4 or 16)) throw Error("Invalid IP alternative name."); name["kind"] = "ip"; name["value"] = new IPAddress(address).ToString(); }
                    else { name["kind"] = "opaque"; name["rawBase64"] = Convert.ToBase64String(sequence.ReadEncodedValue().Span); }
                    names.Add(name);
                }
                result["names"] = names;
            }
            sequence.ThrowIfNotEmpty();
        }
        reader.ThrowIfNotEmpty(); return result;
    }
    private static JsonObject Object(JsonNode? value) => value as JsonObject ?? throw Error("Expected an object.");
    private static void Fields(JsonObject value, string fields)
    { var keys = fields.Split(' '); if (value.Count != keys.Length || keys.Any(k => !value.ContainsKey(k))) throw Error("Missing or unknown public-certificate fields."); }
    private static string Text(JsonObject value, string key, int max = 8192)
    { if (value[key] is not JsonValue v || !v.TryGetValue<string>(out string? text) || string.IsNullOrWhiteSpace(text) || text.Length > max || text.Any(char.IsControl)) throw Error("Expected bounded public text."); _ = Utf8.GetByteCount(text); return text; }
    private static BridgeException Error(string message) => new("InvalidPublicCertificateReview", message);
}
