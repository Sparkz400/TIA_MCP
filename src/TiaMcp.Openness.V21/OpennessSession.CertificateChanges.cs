#nullable disable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.Security;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        // Exact payload fields per kind. certificate_import's derBase64/sha256 are added by the Host from a committed upload.
        private static readonly Dictionary<string, string[]> CertificatePayloads = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["certificate_create"] = new[] { "hardwarePath", "usage", "subjectCommonName", "subjectAlternativeNames", "validFromUtc", "validUntilUtc" },
            ["certificate_import"] = new[] { "hardwarePath", "uploadRef", "format", "derBase64", "sha256" },
            ["certificate_assign"] = new[] { "hardwarePath", "targetPath", "attributeName", "certificateId", "expectedCertificateId" },
            ["certificate_delete"] = new[] { "hardwarePath", "certificateId" },
        };

        private void PrepareCertificateChange(EngineeringChange change, Project project, JObject preview)
        {
            var payload = change.Payload; string[] fields = CertificatePayloads[change.Kind];
            // Exact fields only, so a password, private key or key container can never reach a native call.
            if (payload.Count != fields.Length || fields.Any(name => payload[name] == null))
                throw new BridgeException("InvalidRequest", change.Kind + " takes exactly these payload fields: " + string.Join(", ", fields) + ". Passwords, private keys and key containers are never accepted.");
            var owner = ChangeHardware(project, payload); var store = CertificateStore(owner.Item);
            var effect = new JObject { ["action"] = change.Kind, ["store"] = owner.Path + "/certificates", ["globalCertificateStoreEnabled"] = false,
                ["storeCertificateIds"] = new JArray(store.Certificates.Select(c => (JToken)c.Id)) };
            ((JArray)preview["effects"]).Add(effect);
            var warnings = (JArray)preview["warnings"];
            warnings.Add("Previews, readbacks and journals carry public identity only; private keys are never read, exported or accepted. The recovery checkpoint restores the project but does not prove private-key recoverability; device and client trust are not changed.");
            if (change.Kind == "certificate_create")
            {
                effect["request"] = CertificateCreateRequest(payload);
                effect["issuer"] = "Chosen by TIA Portal; V21 Openness exposes no CA or self-signed selection. The readback reports the actual issuer.";
                effect["signatureAlgorithm"] = "Template default of LocalCertificateStore.GetCertificateTemplate(usage).";
            }
            else if (change.Kind == "certificate_import")
            {
                // The DER lives only in the hash-pinned change artifact, never in the preview or journal.
                byte[] der;
                try { der = Convert.FromBase64String(ChangeText(payload, "derBase64", 96 * 1024)); }
                catch (FormatException) { throw new BridgeException("InvalidRequest", "derBase64 is not base64."); }
                payload.Remove("derBase64");
                using (var sha = SHA256.Create())
                    if (der.Length > 64 * 1024 || X509Certificate2.GetCertContentType(der) != X509ContentType.Cert || BitConverter.ToString(sha.ComputeHash(der)).Replace("-", "").ToLowerInvariant() != ChangeText(payload, "sha256", 64))
                        throw new BridgeException("InvalidRequest", "Only one exact public DER certificate matching sha256 is imported.");
                using (var parsed = new X509Certificate2(der))
                    if (parsed.HasPrivateKey || !parsed.RawData.SequenceEqual(der)) throw new BridgeException("InvalidRequest", "Only one exact public DER certificate is imported.");
                change.InputPath = Path.Combine(change.Directory, "requested.cer");
                File.WriteAllBytes(change.InputPath, der);
                change.InputHash = ChangeFileHash(change.InputPath);
                effect["certificate"] = PublicIdentity(der); effect["certificate"]["privateKeyPresent"] = false;
                warnings.Add("Importing a public certificate does not establish trust in it; CertificateComposition.Import(FileInfo, SecureString) is never called.");
            }
            else if (change.Kind == "certificate_assign")
            {
                var assignment = CertificateAssignment(project, payload, owner, store);
                effect["targetPath"] = assignment.Target.Path; effect["attributeName"] = assignment.Attribute;
                effect["before"] = CertificateSummary(assignment.Current, change.Directory); effect["after"] = CertificateSummary(assignment.Desired, change.Directory);
                warnings.Add("The attribute is discovered at runtime (V21 PublicAPI documents no certificate assignment attribute); usage, key and endpoint suitability are not checked.");
            }
            else
            {
                var certificate = StoreCertificate(store, payload);
                RequireUnreferencedCertificate(project, owner.Path, certificate.Id);
                effect["certificate"] = CertificateSummary(certificate, change.Directory);
                warnings.Add("Program, connection and online references to the certificate Id are not scanned; deletion is refused only for references read on the device.");
            }
        }

        private void ApplyCertificateChange(EngineeringChange change, Project project)
        {
            var payload = change.Payload;
            var owner = ChangeHardware(project, payload); var store = CertificateStore(owner.Item);
            var before = store.Certificates.Select(c => c.Id).ToArray();
            Certificate result = null; uint? expectedNew = null, removed = null; JObject assigned = null;
            if (change.Kind == "certificate_create")
            {
                var request = CertificateCreateRequest(payload);
                var template = store.GetCertificateTemplate((CertificateUsage)Enum.Parse(typeof(CertificateUsage), (string)request["usage"]));
                template.SubjectCommonName = (string)request["subjectCommonName"];
                template.ValidFrom = CertificateTime(payload, "validFromUtc"); template.ValidUntil = CertificateTime(payload, "validUntilUtc");
                // The template's default alternative names are replaced by exactly the requested ones.
                foreach (var name in template.SubjectAlternativeNames.ToArray()) name.Delete();
                foreach (JObject name in request["subjectAlternativeNames"])
                    template.SubjectAlternativeNames.Create((SubjectAlternativeNameType)Enum.Parse(typeof(SubjectAlternativeNameType), (string)name["type"]), (string)name["value"]);
                result = store.Certificates.Create(template);
                expectedNew = result?.Id;
            }
            else if (change.Kind == "certificate_import")
            {
                result = store.Certificates.Import(new FileInfo(change.InputPath));
                expectedNew = result?.Id;
            }
            else if (change.Kind == "certificate_assign")
            {
                var assignment = CertificateAssignment(project, payload, owner, store);
                assignment.Target.Item.SetAttribute(assignment.Attribute, assignment.Desired);
                var after = assignment.Target.Item.GetAttribute(assignment.Attribute);
                if (!(after == null ? assignment.Desired == null : after is Certificate readAssigned && assignment.Desired != null && readAssigned.Id == assignment.Desired.Id))
                    throw new BridgeException("MutationVerificationFailed", "The certificate attribute does not read back the requested assignment.");
                assigned = new JObject { ["targetPath"] = assignment.Target.Path, ["attributeName"] = assignment.Attribute, ["certificate"] = CertificateSummary(assignment.Desired, change.Directory) };
            }
            else
            {
                var certificate = StoreCertificate(store, payload);
                RequireUnreferencedCertificate(project, owner.Path, certificate.Id);
                removed = certificate.Id;
                certificate.Delete();
            }
            change.Stages.Add("certificate_mutation_dispatched");
            var expected = before.Where(id => id != removed).Concat(expectedNew.HasValue && !before.Contains(expectedNew.Value) ? new[] { expectedNew.Value } : new uint[0]).OrderBy(id => id).ToArray();
            var actual = store.Certificates.Select(c => c.Id).OrderBy(id => id).ToArray();
            bool created = change.Kind == "certificate_create" || change.Kind == "certificate_import";
            if (!actual.SequenceEqual(expected) || created && (result == null || before.Contains(result.Id)))
                throw new BridgeException("MutationVerificationFailed", "The local certificate store does not contain exactly the expected certificates after the change.");
            var readback = new JObject { ["role"] = "certificate-readback", ["store"] = owner.Path + "/certificates", ["storeCertificateIds"] = new JArray(actual.Select(id => (JToken)id)) };
            if (created)
            {
                byte[] der;
                try { der = ExportPublicDer(result, change.Directory); }
                catch (Exception) { throw new BridgeException("MutationVerificationFailed", "The new certificate did not export exactly one public certificate for readback."); }
                var identity = PublicIdentity(der); identity["certificateId"] = result.Id; identity["privateKeyPresent"] = result.HasPrivateKey;
                readback["certificate"] = identity;
                if (change.Kind == "certificate_import" && ((string)identity["sha256"] != payload.Value<string>("sha256") || result.HasPrivateKey))
                    throw new BridgeException("MutationVerificationFailed", "The imported certificate differs from the reviewed public DER.");
                if (change.Kind == "certificate_create") RequireCreatedCertificate(CertificateCreateRequest(payload), identity, result);
            }
            if (assigned != null) readback["assignment"] = assigned;
            change.Readbacks.Add(readback);
        }

        private static void RequireCreatedCertificate(JObject request, JObject identity, Certificate created)
        {
            var names = identity["subjectAlternativeNames"] as JArray;
            string Key(JToken name) => (string)name["type"] + ":" + (string)name["value"];
            if (created.SubjectCommonName != (string)request["subjectCommonName"] || (string)identity["notBeforeUtc"] != (string)request["validFromUtc"] || (string)identity["notAfterUtc"] != (string)request["validUntilUtc"]
                || names == null || !names.Select(Key).OrderBy(k => k, StringComparer.Ordinal).SequenceEqual(request["subjectAlternativeNames"].Select(Key).OrderBy(k => k, StringComparer.Ordinal)))
                throw new BridgeException("MutationVerificationFailed", "The created certificate's subject, validity or alternative names differ from the request.", new JObject { ["readback"] = identity.DeepClone() });
        }

        private static LocalCertificateStore CertificateStore(DeviceItem item)
        {
            var manager = item.GetService<LocalCertificateManager>() ?? throw new BridgeException("UnsupportedCapability", "hardwarePath exposes no LocalCertificateManager.");
            // V21 exposes only the device-local store; the project-wide store and switching to it are separate, unexposed workflows.
            if (manager.EnableGlobalCertificatesStore) throw new BridgeException("GlobalCertificateStoreEnabled", "The device uses the global certificate store, which V21 Openness does not expose; local-store changes are refused.");
            return manager.LocalCertificateStore;
        }

        private static uint? CertificateId(JObject payload, string key)
        {
            var token = payload[key];
            if (token == null || token.Type == JTokenType.Null) return null;
            if (token.Type != JTokenType.Integer || !uint.TryParse(token.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out uint id))
                throw new BridgeException("InvalidRequest", key + " must be a certificate Id (unsigned 32-bit integer) or null.");
            return id;
        }

        private static Certificate StoreCertificate(LocalCertificateStore store, JObject payload)
        {
            uint id = CertificateId(payload, "certificateId") ?? throw new BridgeException("InvalidRequest", "certificateId is required.");
            return store.Certificates.FirstOrDefault(c => c.Id == id) ?? throw new BridgeException("CertificateNotFound", "No certificate with certificateId exists in the selected local store.");
        }

        private static DateTime CertificateTime(JObject payload, string key)
        {
            if (!DateTime.TryParseExact(ChangeText(payload, key, 20), "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value))
                throw new BridgeException("InvalidRequest", key + " must be explicit UTC YYYY-MM-DDTHH:mm:ssZ.");
            return value;
        }

        private static JObject CertificateCreateRequest(JObject payload)
        {
            string usage = ChangeText(payload, "usage", 32), name = ChangeText(payload, "subjectCommonName", 64);
            if (usage == "None" || !Enum.GetNames(typeof(CertificateUsage)).Contains(usage, StringComparer.Ordinal))
                throw new BridgeException("InvalidRequest", "usage must be one of Tls, OpcUaClient, OpcUaClientServer, OpcUaServer, WebServer.");
            if (name.Any(char.IsControl)) throw new BridgeException("InvalidRequest", "subjectCommonName must not contain control characters.");
            if (CertificateTime(payload, "validUntilUtc") <= CertificateTime(payload, "validFromUtc")) throw new BridgeException("InvalidRequest", "validUntilUtc must be after validFromUtc.");
            var requested = payload["subjectAlternativeNames"] as JArray;
            if (requested == null || requested.Count > 32) throw new BridgeException("InvalidRequest", "subjectAlternativeNames must be an array of at most 32 {type, value} entries.");
            var names = new JArray();
            foreach (var row in requested)
            {
                var entry = row as JObject;
                if (entry == null || entry.Count != 2) throw new BridgeException("InvalidRequest", "Each subject alternative name is exactly {type, value}.");
                string type = ChangeText(entry, "type", 8), value = ChangeText(entry, "value", 256);
                if (type == "IP") value = IPAddress.TryParse(value, out var address) ? address.ToString() : throw new BridgeException("InvalidRequest", "IP alternative names must be IPv4 or IPv6 addresses.");
                else if (type != "Dns" && type != "Email" && type != "Uri" || value.Any(c => c < '!' || c > '~') || type == "Uri" && !Uri.IsWellFormedUriString(value, UriKind.Absolute))
                    throw new BridgeException("InvalidRequest", "Alternative names are Dns, Email, Uri (absolute) or IP with printable ASCII values.");
                names.Add(new JObject { ["type"] = type, ["value"] = value });
            }
            return new JObject { ["usage"] = usage, ["subjectCommonName"] = name, ["subjectAlternativeNames"] = names,
                ["validFromUtc"] = payload["validFromUtc"].DeepClone(), ["validUntilUtc"] = payload["validUntilUtc"].DeepClone() };
        }

        private (HardwareEntry Target, string Attribute, Certificate Current, Certificate Desired) CertificateAssignment(Project project, JObject payload, HardwareEntry owner, LocalCertificateStore store)
        {
            var target = ChangeHardware(project, payload, "targetPath");
            if (target.Path != owner.Path && !target.Path.StartsWith(owner.Path + "/", StringComparison.Ordinal))
                throw new BridgeException("InvalidRequest", "targetPath must be hardwarePath or one of its subitems.");
            string name = ChangeText(payload, "attributeName", 128);
            if (!Regex.IsMatch(name, "^[A-Za-z][A-Za-z0-9_]*Certificate$", RegexOptions.CultureInvariant) || IsSensitiveAttribute(name))
                throw new BridgeException("ProtectedAttribute", "attributeName must be a certificate attribute name ending in Certificate.");
            var info = target.Item.GetAttributeInfos().FirstOrDefault(i => i.Name == name) ?? throw new BridgeException("AttributeNotFound", "targetPath exposes no attribute named attributeName.");
            if (info.AccessMode != EngineeringAttributeAccessMode.ReadWrite) throw new BridgeException("AttributeNotWritable", "The certificate attribute is not native read-write.");
            object value = target.Item.GetAttribute(name);
            if (value != null && !(value is Certificate)) throw new BridgeException("UnsupportedAttributeType", "The attribute does not hold a Siemens.Engineering.Security.Certificate.");
            var current = (Certificate)value; uint? expected = CertificateId(payload, "expectedCertificateId"), wanted = CertificateId(payload, "certificateId");
            if (current?.Id != expected)
                throw new BridgeException("AssignmentDrift", "The current assignment differs from expectedCertificateId; nothing was changed.", new JObject { ["currentCertificateId"] = current?.Id, ["expectedCertificateId"] = expected });
            if (wanted == expected) throw new BridgeException("InvalidRequest", "certificateId is already assigned.");
            var desired = wanted == null ? null : store.Certificates.FirstOrDefault(c => c.Id == wanted.Value) ?? throw new BridgeException("CertificateNotFound", "No certificate with certificateId exists in the selected local store.");
            return (target, name, current, desired);
        }

        // Public summary; certificates holding a private key may not export a public form, so their identity can stay unavailable.
        private static JObject CertificateSummary(Certificate certificate, string directory)
        {
            if (certificate == null) return null;
            var summary = new JObject { ["certificateId"] = certificate.Id, ["subjectCommonName"] = certificate.SubjectCommonName, ["privateKeyPresent"] = certificate.HasPrivateKey, ["publicIdentity"] = null };
            try { summary["publicIdentity"] = PublicIdentity(ExportPublicDer(certificate, directory)); }
            catch (Exception) { summary["publicIdentityUnavailable"] = "Native export did not yield exactly one public certificate."; }
            return summary;
        }

        private void RequireUnreferencedCertificate(Project project, string ownerPath, uint id)
        {
            var blocking = CertificateReferences(project, ownerPath).Where(r => r["certificateIds"].Type == JTokenType.Null || r["certificateIds"].Any(v => v.Value<uint>() == id)).ToArray();
            if (blocking.Length != 0)
                throw new BridgeException("CertificateInUse", "The certificate is referenced, or a certificate reference on its device could not be read; nothing was deleted.", new JObject { ["references"] = new JArray(blocking) });
        }

        // Certificate references read on the store owner's device: certificate-named attributes holding certificates, runtime-managed supported
        // services and UMC trusted ids. A reference that cannot be read has null certificateIds, so deletion fails closed.
        private JArray CertificateReferences(Project project, string ownerPath)
        {
            int items = ownerPath.IndexOf("/items/", StringComparison.Ordinal);
            if (items < 0) throw new BridgeException("InvalidRequest", "hardwarePath must be a device item path.");
            string device = ownerPath.Substring(0, items + "/items/".Length);
            var rows = new JArray(); var budget = new ScanBudget(50000);
            JObject Reference(string path, string source, Func<IEnumerable<uint>> read)
            {
                JToken ids;
                try { ids = new JArray(read().Select(id => (JToken)id)); } catch (Exception) { ids = JValue.CreateNull(); }
                return new JObject { ["path"] = path, ["source"] = source, ["certificateIds"] = ids };
            }
            foreach (var entry in DeviceItems(project, budget).Where(e => e.Path.StartsWith(device, StringComparison.Ordinal)))
            {
                try
                {
                    var item = entry.Item;
                    foreach (var info in item.GetAttributeInfos().Where(i => i.Name.IndexOf("certificate", StringComparison.OrdinalIgnoreCase) >= 0 && !IsSensitiveAttribute(i.Name)))
                        rows.Add(Reference(entry.Path, info.Name, () =>
                        {
                            object value = item.GetAttribute(info.Name);
                            return value is Certificate single ? new[] { single.Id } : value is IEnumerable many && !(value is string) ? many.OfType<Certificate>().Select(c => c.Id).ToArray() : new uint[0];
                        }));
                    var management = item.GetService<CertificateManagementConfiguration>();
                    if (management != null) rows.Add(Reference(entry.Path, "CertificateSupportedServices", () => management.CertificateSupportedServices.Select(s => s.Id).ToArray()));
                    var access = item.GetService<PlcAccessControlConfigurationProvider>();
                    if (access != null) rows.Add(Reference(entry.Path, "UmcTrustedCertificateIds", () => Regex.Matches(access.UmcTrustedCertificateIds ?? "", @"\d+").Cast<Match>().Select(m => uint.Parse(m.Value, CultureInfo.InvariantCulture)).ToArray()));
                }
                catch (Exception) { rows.Add(new JObject { ["path"] = entry.Path, ["source"] = null, ["certificateIds"] = null }); }
            }
            if (budget.Truncated) throw new BridgeException("TraversalLimit", "Certificate references could not be enumerated within the hardware traversal bound.");
            return rows;
        }

        private JObject CertificateFingerprint(Project project, JObject payload)
        {
            var owner = ChangeHardware(project, payload);
            var manager = owner.Item.GetService<LocalCertificateManager>();
            return new JObject { ["globalStoreEnabled"] = manager?.EnableGlobalCertificatesStore,
                ["store"] = manager == null ? null : new JArray(manager.LocalCertificateStore.Certificates.Select(c => new JObject { ["id"] = c.Id, ["subjectCommonName"] = c.SubjectCommonName, ["validUntil"] = ChangeScalar(c.ValidUntil), ["hasPrivateKey"] = c.HasPrivateKey })),
                ["references"] = CertificateReferences(project, owner.Path) };
        }
    }
}
