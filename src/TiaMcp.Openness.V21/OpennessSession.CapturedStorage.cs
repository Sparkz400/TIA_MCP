using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.SW.Blocks;
using TiaMcp.Model;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        private const string CapturedStorageProfileHash = "642a0895d7be9ac45c135e4704a0822446dcb6a183c228ddd9f38a426357afa0";
        private const string CapturedStorageArchiveHash = "9d691a31d60863e7384928df09eb37345eb20bd791f18d919cf7960fbfc750cc";
        private sealed class CapturedStorageQualification
        {
            internal LibraryChange Plan;
            internal JObject Descriptor;
            internal string CatalogPath, PlanHash;
            internal JArray Artifacts;
        }

        // The native component adapter alone creates this context. It is never
        // accepted from a request or persisted/reconstructed as a live handle.
        private CapturedStorageQualification CapturedStorageContext(LibraryChange plan)
        {
            if (plan.Source?.SourceHash != CapturedStorageArchiveHash) return null;
            RequireComponentScratch(plan.Project);
            if (!PlcLibraryIntegrationPlanner.CreatesStandaloneCaller(plan.Payload))
                throw new BridgeException("CapturedStorageProfileUnavailable", "Exact captured storage qualification requires a new standalone FC caller.");
            string catalog = TypedCatalogPath();
            if (catalog == null) throw new BridgeException("CapturedStorageProfileUnavailable", "Trusted captured-storage profile is not deployed.");
            string file = Path.Combine(Path.GetDirectoryName(catalog), "component04-storage-profile.json");
            var descriptorBytes = File.Exists(file) ? File.ReadAllBytes(file) : null;
            if (descriptorBytes == null || CapturedBytesHash(descriptorBytes) != CapturedStorageProfileHash)
                throw new BridgeException("ProfileCatalogInvalid", "The internally pinned captured-storage receipt is absent or changed.");
            var profile = JObject.Parse(new UTF8Encoding(false, true).GetString(descriptorBytes).TrimStart('\uFEFF'));
            var artifacts = new JArray { new JObject { ["path"] = file, ["bytes"] = descriptorBytes.Length, ["sha256"] = CapturedStorageProfileHash } };
            foreach (JProperty entry in ((JObject)profile["artifacts"]).Properties())
            {
                var row = (JObject)entry.Value; string path = TypedResourcePath(catalog, row.Value<string>("path"));
                var bytes = File.Exists(path) ? File.ReadAllBytes(path) : null;
                if (bytes == null || CapturedBytesHash(bytes) != row.Value<string>("sha256"))
                    throw new BridgeException("ProfileCatalogInvalid", "An exact captured-storage reference/provenance artifact changed.");
                artifacts.Add(new JObject { ["path"] = path, ["bytes"] = bytes.Length, ["sha256"] = row.Value<string>("sha256") });
            }
            var pins = (JArray)plan.Preview["integrationPlan"]["versions"];
            CapturedStorageContract.RequirePlan(profile, pins, plan.Source.SourceHash);
            foreach (JObject pin in pins)
            {
                PlcComponentContentContract.ValidatePin(pin);
            }
            return new CapturedStorageQualification { Plan = plan, Descriptor = profile, CatalogPath = catalog,
                PlanHash = ChangeHash(plan.Preview.ToString(Newtonsoft.Json.Formatting.None)), Artifacts = artifacts };
        }
        private static string CapturedBytesHash(byte[] bytes)
        { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }

        private TypedGraphicalCapturedStorageTemplate.Profile CapturedStorageProfile(CapturedStorageQualification context)
        {
            var d = context.Descriptor;
            Func<string, TypedGraphicalCapturedStorageTemplate.Artifact> read = key => {
                var row = d["artifacts"][key]; string path = TypedResourcePath(context.CatalogPath, row.Value<string>("path"));
                return new TypedGraphicalCapturedStorageTemplate.Artifact(File.ReadAllBytes(path), row.Value<string>("sha256"));
            };
            return new TypedGraphicalCapturedStorageTemplate.Profile(d.Value<string>("id"), d.Value<string>("cpuIdentifier"),
                d.Value<string>("nativeRelease"), d.Value<string>("nativeApiFileVersion"), d.Value<string>("route"), d.Value<string>("callerLanguage"),
                read("callee"), read("type"), read("instance"));
        }

        private JObject CapturedStorageEnvironment(CapturedStorageQualification context)
        {
            var observations = new JArray();
            foreach (var assembly in new[] { typeof(TiaPortal).Assembly, typeof(FB).Assembly }.Distinct())
            {
                string path = assembly.Location;
                var expected = ((JArray)context.Descriptor["loadedApiFiles"]).OfType<JObject>().SingleOrDefault(x => x.Value<string>("name") == Path.GetFileName(path));
                var version = FileVersionInfo.GetVersionInfo(path);
                if (expected == null || !string.Equals(Path.GetDirectoryName(path), Path.GetFullPath(_apiDirectory), StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(Path.GetFullPath(path), Path.GetFullPath(expected.Value<string>("path")), StringComparison.OrdinalIgnoreCase) ||
                    version.FileVersion != expected.Value<string>("fileVersion") || ChangeFileHash(path) != expected.Value<string>("sha256"))
                    throw new BridgeException("CapturedStorageProfileUnavailable", "Actual loaded Siemens API path/version/bytes differ from the accepted profile.");
                observations.Add(new JObject { ["path"] = path, ["assemblyVersion"] = assembly.GetName().Version.ToString(), ["fileVersion"] = version.FileVersion, ["sha256"] = expected["sha256"].DeepClone() });
            }
            if (observations.Count != 2) throw new BridgeException("CapturedStorageProfileUnavailable", "Both exact loaded Base and Step7 API assemblies are required.");
            return new JObject { ["files"] = observations, ["nativeRelease"] = context.Descriptor["nativeRelease"].DeepClone(),
                ["releaseEvidence"] = "Accepted raw DocumentInfo and exact observed/loaded Base+Step7 files." };
        }

        // Called under the existing preparation/apply exclusive access; this
        // performs reads only and does not acquire another exclusive lease.
        private JObject CaptureStorageBindings(EngineeringChange change, out TypedGraphicalCapturedStorageTemplate.Binding callee,
            out TypedGraphicalCapturedStorageTemplate.Binding udt)
        {
            var context = change.CapturedStorageQualification;
            if (context == null) throw new BridgeException("CapturedStorageProfileUnavailable", "Private current-session qualification context is missing.");
            var plan = context.Plan; RequireComponentScratch(plan.Project);
            if (!_libraryChanges.TryGetValue(plan.Id, out var current) || !ReferenceEquals(current, plan) ||
                plan.Project.Path.FullName != change.ProjectPath || ChangeHash(plan.Preview.ToString(Newtonsoft.Json.Formatting.None)) != context.PlanHash)
                throw new BridgeException("StaleDeclaration", "Captured-storage prepared plan/session changed.");
            if (plan.Source.SourceHash != CapturedStorageArchiveHash || !File.Exists(plan.Source.SourcePath) || ChangeFileHash(plan.Source.SourcePath) != CapturedStorageArchiveHash)
                throw new BridgeException("ArtifactChanged", "Exact captured source archive changed.");
            foreach (JObject artifact in context.Artifacts)
                if (!File.Exists(artifact.Value<string>("path")) || ChangeFileHash(artifact.Value<string>("path")) != artifact.Value<string>("sha256"))
                    throw new BridgeException("ArtifactChanged", "Captured profile or complete reference/provenance changed.");
            string descriptorPath = Path.Combine(Path.GetDirectoryName(context.CatalogPath), "component04-storage-profile.json");
            if (ChangeFileHash(descriptorPath) != CapturedStorageProfileHash)
                throw new BridgeException("ArtifactChanged", "Trusted captured-storage descriptor no longer has its fixed compiled hash.");
            var plc = SelectPlc(change.Selector); var scope = ResolveSoftwareScope(plc, change.ScopePath);
            if (scope.Unit != null || change.ScopePath != plc.Path || change.Namespace != "" ||
                PlcLibraryIntegrationPlanner.CallerPath(plan.Payload) != ObjectIdentity.ObjectPath(change.GroupPath, change.BlockNames.Single(), change.Namespace))
                throw new BridgeException("CapturedStorageProfileUnavailable", "Exact new caller and standard PLC scope must match the current plan.");
            var model = change.Payload["model"] as JObject ?? change.Payload;
            if (model.Value<string>("operation") != "createBlock" || model.Value<string>("language") != "FBD" || model["creation"]?.Value<string>("blockType") != "FC")
                throw new BridgeException("CapturedStorageProfileUnavailable", "Only a new FBD FC is eligible for qualification for this stored profile.");
            var scan = new ScanBudget(50000); var blocks = ScopeBlocks(scope, scan).ToArray(); var types = ScopeTypes(scope, scan).ToArray();
            if (scan.Truncated) throw new BridgeException("TraversalLimit", "Exact captured storage requires complete native scope inventory.");
            if (blocks.Count(b => string.Equals(b.Item.Name, TypedGraphicalCapturedStorageTemplate.FbName, StringComparison.OrdinalIgnoreCase)) != 1 ||
                types.Count(t => string.Equals(t.Item.Name, TypedGraphicalCapturedStorageTemplate.TypeName, StringComparison.OrdinalIgnoreCase)) != 1)
                throw new BridgeException("StaleDeclaration", "Captured FB/UDT names must be unambiguous across the complete standard scope.");
            var paths = new Dictionary<IEngineeringObject, string>();
            foreach (var b in blocks) paths[b.Item] = b.Path;
            foreach (var t in types) paths[t.Item] = t.Path;
            var values = new JArray(); var artifacts = new JArray();
            var bindings = new Dictionary<string, TypedGraphicalCapturedStorageTemplate.Binding>(StringComparer.Ordinal);
            foreach (JObject pin in (JArray)plan.Preview["integrationPlan"]["versions"])
            {
                string versionGuid = pin.Value<string>("versionGuid"), name = pin.Value<string>("typeName");
                var version = plan.Project.ProjectLibrary.FindVersion(Guid.Parse(versionGuid));
                if (version == null || version.TypeObject.Guid.ToString("D") != pin.Value<string>("typeGuid"))
                    throw new BridgeException("StaleDeclaration", "Captured native library version identity is absent or changed.");
                var info = LibraryVersionInfo(version); var uses = version.FindInstances(plc.Software).Take(2).ToArray();
                if (uses.Length != 1 || uses[0].LibraryTypeVersion.Guid != version.Guid || !(uses[0].LibraryTypeInstance is IEngineeringObject item) || !paths.TryGetValue(item, out var path))
                    throw new BridgeException("StaleDeclaration", "The exact captured version must have one resolvable native PLC use.");
                string expectedPath = plc.Path + (name == TypedGraphicalCapturedStorageTemplate.FbName ? "/blocks/" : "/types/") + name;
                string nativeNamespace; string direct = Path.Combine(change.Directory, "storage-direct-" + Guid.NewGuid().ToString("N") + ".xml");
                if (item is FB fb && name == TypedGraphicalCapturedStorageTemplate.FbName) { nativeNamespace = fb.Namespace; fb.Export(new FileInfo(direct), ExportOptions.WithDefaults, DocumentInfoOptions.None); }
                else if (item is Siemens.Engineering.SW.Types.PlcType type && name == TypedGraphicalCapturedStorageTemplate.TypeName) { nativeNamespace = type.Namespace; type.Export(new FileInfo(direct), ExportOptions.WithDefaults, DocumentInfoOptions.None); }
                else throw new BridgeException("StaleDeclaration", "The captured native object class differs.");
                string sourceFile = Path.Combine(change.Directory, "storage-source-" + Guid.NewGuid().ToString("N") + ".xml");
                string installedFile = Path.Combine(change.Directory, "storage-installed-" + Guid.NewGuid().ToString("N") + ".xml");
                var source = ResolveLibraryVersion(plan.Source, pin);
                source.Export(new FileInfo(sourceFile), ExportOptions.WithDefaults, DocumentInfoOptions.None);
                version.Export(new FileInfo(installedFile), ExportOptions.WithDefaults, DocumentInfoOptions.None);
                if (ChangeFileHash(sourceFile) != pin.Value<string>("contentSha256")) throw new BridgeException("StaleDeclaration", "Exact source library content changed.");
                JObject contentProof = null;
                if (pin["contentPolicy"] is JObject policy)
                {
                    string oracle = Path.Combine(change.Directory, "storage-source-oracle-" + Guid.NewGuid().ToString("N") + ".xml");
                    string installedOracle = Path.Combine(change.Directory, "storage-installed-oracle-" + Guid.NewGuid().ToString("N") + ".xml");
                    source.Export(new FileInfo(oracle), ExportOptions.WithDefaults | ExportOptions.WithReadOnly, DocumentInfoOptions.None);
                    version.Export(new FileInfo(installedOracle), ExportOptions.WithDefaults | ExportOptions.WithReadOnly, DocumentInfoOptions.None);
                    if (ChangeFileHash(oracle) != policy.Value<string>("sourceOracleSha256")) throw new BridgeException("StaleDeclaration", "Exact source full oracle changed.");
                    var nativeFb = item as FB ?? throw new BridgeException("StaleDeclaration", "The exact FB source contract requires its linked native FB instance.");
                    var contentEvidence = new JObject { ["typeGuid"] = pin["typeGuid"].DeepClone(), ["versionGuid"] = pin["versionGuid"].DeepClone(),
                        ["contentPolicyId"] = policy["policyId"].DeepClone(), ["sourceContentSha256"] = ChangeFileHash(sourceFile), ["sourceArtifact"] = ChangeArtifact(sourceFile),
                        ["contentSha256"] = ChangeFileHash(installedFile), ["artifact"] = ChangeArtifact(installedFile),
                        ["sourceOracleSha256"] = ChangeFileHash(oracle), ["sourceOracleArtifact"] = ChangeArtifact(oracle),
                        ["installedOracleSha256"] = ChangeFileHash(installedOracle), ["installedOracleArtifact"] = ChangeArtifact(installedOracle),
                        ["nativeFbSettings"] = new JObject { ["AutoNumber"] = CapturedSetting(() => nativeFb.AutoNumber), ["MemoryLayout"] = CapturedSetting(() => nativeFb.MemoryLayout),
                            ["IsIECCheckEnabled"] = CapturedSetting(() => {
                                var setting = ((IEngineeringObject)nativeFb).GetAttributeInfos().SingleOrDefault(a => a.Name == "IsIECCheckEnabled");
                                if (setting == null || (setting.AccessMode & EngineeringAttributeAccessMode.Read) == 0) throw new InvalidOperationException("IEC check getter is not advertised readable.");
                                return ((IEngineeringObject)nativeFb).GetAttribute("IsIECCheckEnabled"); }),
                            ["CreationDate"] = CapturedSetting(() => nativeFb.CreationDate) } };
                    contentProof = PlcComponentContentContract.Validate(pin, contentEvidence);
                    artifacts.Add(ChangeArtifact(oracle)); artifacts.Add(ChangeArtifact(installedOracle));
                }
                else if (ChangeFileHash(installedFile) != pin.Value<string>("contentSha256"))
                    throw new BridgeException("StaleDeclaration", "Exact UDT installed library bytes differ from the pinned source; no content exception applies.");
                var deps = LibraryDependencyCapture.Read(version).VersionGuids.OrderBy(v => v, StringComparer.Ordinal).ToArray();
                var binding = new TypedGraphicalCapturedStorageTemplate.Binding(expectedPath, path, scope.Path, nativeNamespace,
                    version.TypeObject.Guid.ToString("D"), version.Guid.ToString("D"), info.Value<string>("state"), info.Value<bool>("doNotUse"), true, uses.Length,
                    deps, new TypedGraphicalCapturedStorageTemplate.Artifact(File.ReadAllBytes(direct), ChangeFileHash(direct)));
                bindings.Add(name, binding);
                values.Add(new JObject { ["name"] = name, ["plannedPath"] = expectedPath, ["nativePath"] = path, ["scopePath"] = scope.Path, ["namespace"] = nativeNamespace,
                    ["typeGuid"] = binding.TypeGuid, ["versionGuid"] = binding.VersionGuid, ["state"] = binding.VersionState, ["doNotUse"] = binding.DoNotUse,
                    ["nativeUseCount"] = uses.Length, ["requiredVersionGuids"] = new JArray(deps), ["directSha256"] = ChangeFileHash(direct),
                    ["sourceSha256"] = ChangeFileHash(sourceFile), ["installedSha256"] = ChangeFileHash(installedFile), ["contentContract"] = contentProof });
                artifacts.Add(ChangeArtifact(direct)); artifacts.Add(ChangeArtifact(sourceFile)); artifacts.Add(ChangeArtifact(installedFile));
            }
            callee = bindings[TypedGraphicalCapturedStorageTemplate.FbName]; udt = bindings[TypedGraphicalCapturedStorageTemplate.TypeName];
            return new JObject { ["bindings"] = values, ["artifacts"] = artifacts, ["environment"] = CapturedStorageEnvironment(context),
                ["cpuIdentifier"] = TypedCpuIdentifier(plc), ["route"] = TypedRoute(scope, change.Namespace), ["callerLanguage"] = model.Value<string>("language") };
        }
        private static JObject CapturedSetting(Func<object> getter)
        {
            try
            {
                var value = getter(); var row = new JObject { ["type"] = value?.GetType().FullName };
                if (value is DateTime date) { row["value"] = date.ToString("O", CultureInfo.InvariantCulture); row["ticks"] = date.Ticks; row["kind"] = date.Kind.ToString(); }
                else row["value"] = value is bool boolean ? new JValue(boolean) : new JValue(value?.ToString());
                return row;
            }
            catch (Exception error) { return new JObject { ["failure"] = error.Message, ["errorType"] = error.GetType().FullName }; }
        }

        private XDocument PrepareCapturedStorageInstance(TypedResolutionCapture capture, string name, int number, BlockEntry target)
        {
            var change = capture.Change; var context = change.CapturedStorageQualification;
            var observation = CaptureStorageBindings(change, out var callee, out var udt);
            if (target.Path != callee.NativePath) throw new BridgeException("CapturedStorageProfileUnavailable", "The typed call selected a different FB.");
            var document = BuildCapturedStorage(change, name, number, observation, callee, udt);
            foreach (JObject artifact in context.Artifacts) change.TypedDependencies.Add(new JObject { ["kind"] = "profileCatalog", ["artifact"] = artifact.DeepClone() });
            change.TypedDependencies.Add(new JObject { ["kind"] = "capturedStorageQualification", ["qualificationOnly"] = true, ["productionApplySupported"] = false,
                ["planChangeId"] = context.Plan.Id, ["planPreviewHash"] = context.Plan.PreviewHash, ["planContentHash"] = context.PlanHash,
                ["sourceArchive"] = ChangeArtifact(context.Plan.Source.SourcePath), ["profileSha256"] = CapturedStorageProfileHash,
                ["name"] = name, ["number"] = number, ["bindings"] = observation["bindings"].DeepClone(), ["artifacts"] = observation["artifacts"].DeepClone(),
                ["environment"] = observation["environment"].DeepClone(), ["cpuIdentifier"] = observation["cpuIdentifier"].DeepClone(),
                ["route"] = observation["route"].DeepClone(), ["callerLanguage"] = observation["callerLanguage"].DeepClone() });
            return document;
        }
        private XDocument BuildCapturedStorage(EngineeringChange change, string name, int number, JObject observation,
            TypedGraphicalCapturedStorageTemplate.Binding callee, TypedGraphicalCapturedStorageTemplate.Binding udt) =>
            TypedGraphicalCapturedStorageTemplate.Create(name, number, CapturedStorageProfile(change.CapturedStorageQualification), SelectPlc(change.Selector).Path,
                observation.Value<string>("cpuIdentifier"), "V21 Update 2", "2100.0.121.1", observation.Value<string>("route"), observation.Value<string>("callerLanguage"), callee, udt);

        private void ValidateCapturedStorageBindings(EngineeringChange change)
        {
            var dependencies = change.TypedDependencies.OfType<JObject>().Where(d => d.Value<string>("kind") == "capturedStorageQualification").ToArray();
            if (dependencies.Length == 0)
            {
                if (change.CapturedStorageQualification != null) throw new BridgeException("StaleDeclaration", "Private captured storage context lacks its durable receipt.");
                return;
            }
            if (dependencies.Length != 1 || change.CapturedStorageQualification == null) throw new BridgeException("StaleDeclaration", "Captured storage receipt/current-session context is ambiguous or unavailable.");
            var dependency = dependencies[0]; var context = change.CapturedStorageQualification;
            if (dependency.Value<string>("planChangeId") != context.Plan.Id || dependency.Value<string>("planPreviewHash") != context.Plan.PreviewHash ||
                dependency.Value<string>("planContentHash") != context.PlanHash || dependency.Value<string>("profileSha256") != CapturedStorageProfileHash)
                throw new BridgeException("StaleDeclaration", "Captured storage receipt no longer matches the private plan/profile.");
            foreach (JObject artifact in (JArray)dependency["artifacts"])
                if (!File.Exists(artifact.Value<string>("path")) || ChangeFileHash(artifact.Value<string>("path")) != artifact.Value<string>("sha256"))
                    throw new BridgeException("ArtifactChanged", "Prepared captured source/installed/direct evidence changed.");
            var observation = CaptureStorageBindings(change, out var callee, out var udt);
            CapturedStorageContract.RequireReceipt(dependency, context.Plan.Id, context.Plan.PreviewHash, context.PlanHash, CapturedStorageProfileHash, observation);
            BuildCapturedStorage(change, dependency.Value<string>("name"), dependency.Value<int>("number"), observation, callee, udt);
        }
    }
}
