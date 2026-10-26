using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.CrossReference;
using Siemens.Engineering.SW.Blocks;
using TiaMcp.Analysis;
using TiaMcp.Model;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        private void PrepareTypedInstances(EngineeringChange change, TypedGraphicalModel model, XDocument document,
            TypedGraphicalContext context, TypedResolutionCapture capture, JObject preview)
        {
            var requests = change.Payload["instances"] as JArray ?? new JArray();
            if (requests.Count > 128) throw new BridgeException("ModelLimitExceeded", "At most 128 explicit instances may be planned.");
            var keys = requests.OfType<JObject>().Select(r => r.Value<string>("key")).ToArray();
            if (keys.Length != requests.Count || keys.Any(string.IsNullOrEmpty) || keys.Distinct(StringComparer.Ordinal).Count() != keys.Length)
                throw new BridgeException("InvalidInstance", "Instance requests must be objects with unique explicit keys.");
            foreach (JObject request in requests)
            {
                string key = ChangeText(request, "key", 128), kind = ChangeText(request, "kind", 32);
                var users = model.Nodes.Where(n => n.Instance == key).ToArray();
                if (users.Length == 0) throw new BridgeException("UnusedInstanceEffect", "An explicit instance must be used by this prepared network.");
                if (users.Length > 1 && users.Any(n => !n.AllowSharedInstance)) throw new BridgeException("SharedInstanceRejected", "All users of shared state must explicitly allowSharedInstance.");
                string[] targets = users.Select(n => n.Kind == "call" ? n.CalleePath : "instruction:" + n.Instruction).Distinct(StringComparer.Ordinal).ToArray();
                if (targets.Length != 1) throw new BridgeException("InstanceTypeMismatch", "Shared storage cannot refer to different callees or instruction variants.");
                string calleePath = targets[0];
                string dataType;
                TypedGraphicalProfile storageProfile = null;
                bool system = calleePath.StartsWith("instruction:", StringComparison.Ordinal);
                if (!system)
                {
                    if (!context.Callees.TryGetValue(calleePath, out var callee) || callee.BlockType != "FB") throw new BridgeException("InstanceTypeMismatch", "Only an exact FB call accepts instance storage.");
                    dataType = "\"" + QualifiedName(capture.Block(calleePath).Item) + "\"";
                }
                else
                {
                    var node = users[0];
                    var matches = context.Profiles.Where(p => p.Language == model.Language && p.Kind == node.Kind && p.Instruction == node.Instruction &&
                        p.NativeFormatValidated && p.TiaVersion == context.TiaVersion && p.CpuFamily == context.CpuFamily && p.Route == context.Route &&
                        (p.DataType == null || p.DataType == node.DataType)).ToArray();
                    if (matches.Length != 1 || string.IsNullOrEmpty(matches[0].InstanceType))
                        throw new BridgeException("InstructionProfileUnavailable", "No unique native storage profile exists for this timer/counter target combination.");
                    dataType = matches[0].InstanceType;
                    storageProfile = matches[0];
                }
                TypedGraphicalInstance instance;
                if (kind == "existingGlobal")
                {
                    var block = capture.Block(ChangeText(request, "blockPath", 4096));
                    if (!(block.Item is InstanceDB idb)) throw new BridgeException("InstanceTypeMismatch", "Existing global storage must be a native InstanceDB.");
                    var xml = capture.Export(block);
                    if (!system)
                    {
                        var analysis = ReadNativeAnalysis(new JObject { ["projectName"] = capture.Plc.Project.Name, ["plcPath"] = capture.Plc.Path, ["blockPath"] = block.Path, ["maxItems"] = 2000 });
                        var native = analysis.References.Where(r => (r.Relation == ReferenceType.InstanceType || r.Relation == ReferenceType.TypeInstance) && r.Actor?.Key == block.Path && r.Subject?.Key == r.Subject?.BlockPath)
                            .Select(r => r.Subject?.BlockPath).Where(p => p != null);
                        var resolved = OfflineEvidence.ResolveInstanceTarget(capture.Plc.Path, block.Path, idb.Namespace, idb.InstanceOfName, false,
                            capture.Blocks.Where(b => b.Item is FB).Select(b => new OfflineEvidence.InstanceTarget { Path = b.Path, Name = b.Item.Name, Namespace = b.Item.Namespace }), native);
                        if (resolved.Path != calleePath) throw new BridgeException("InstanceTypeMismatch", "Native instance ownership does not establish the selected FB: " + resolved.Reason);
                    }
                    else
                    {
                        var attributes = xml.Root.Elements().Single(e => e.Name.LocalName.StartsWith("SW.Blocks.", StringComparison.Ordinal)).Element("AttributeList");
                        if (string.IsNullOrEmpty(storageProfile.GlobalInstanceType) || attributes?.Element("InstanceOfName")?.Value != storageProfile.GlobalInstanceType) throw new BridgeException("InstanceTypeMismatch", "Native timer/counter global instance type differs from the validated profile.");
                        dataType = storageProfile.GlobalInstanceType;
                    }
                    instance = new TypedGraphicalInstance { Identity = block.Path, Scope = "GlobalVariable", Components = new[] { QualifiedName(block.Item) }, CalleePath = calleePath, DataType = dataType, DeclarationHash = capture.Hashes[block.Path] };
                }
                else if (kind == "existingMulti")
                {
                    var components = ChangeStringArray(request, "components", 32, 128);
                    var member = capture.ResolveMember(document, components, capture.CallerPath);
                    if (member.Section != "Static" || !SameDeclaredType(member.DataType, dataType)) throw new BridgeException("InstanceTypeMismatch", "Multi-instance must be a caller Static declaration of the exact selected type.");
                    if (!system) RequireUniqueDeclaredFb(capture, member.DataType, calleePath);
                    instance = new TypedGraphicalInstance { Identity = TypedMemberIdentity(capture.CallerPath, components), Scope = "LocalVariable", Components = components, CalleePath = calleePath, DataType = dataType, DeclarationHash = capture.Hashes[capture.CallerPath] };
                }
                else if (kind == "createMulti")
                {
                    string name = ChangeText(request, "name", 128);
                    ValidateTypedName(name);
                    var owner = document.Root.Elements().Single(e => e.Name.LocalName.StartsWith("SW.Blocks.", StringComparison.Ordinal));
                    if (owner.Name.LocalName != "SW.Blocks.FB") throw new BridgeException("InstanceScopeMismatch", "Only an FB caller can own a multi-instance.");
                    if (TypedResolutionCapture.Members(document).Any(m => m.Path.Length == 1 && string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase))) throw new BridgeException("DeclarationCollision", "A caller declaration already uses the new multi-instance name.");
                    var sections = owner.Element("AttributeList").Element("Interface").Elements().Single(e => e.Name.LocalName == "Sections");
                    var section = sections.Elements().Single(e => e.Name.LocalName == "Section" && e.Attribute("Name")?.Value == "Static");
                    XElement declaration;
                    if (system)
                    {
                        if (string.IsNullOrEmpty(storageProfile.InstanceDeclarationXml)) throw new BridgeException("InstanceCreationUnsupported", "The profile has no independently captured storage declaration template.");
                        declaration = EngineeringSources.ReadXml(storageProfile.InstanceDeclarationXml).Root;
                        if (declaration.Name != section.Name.Namespace + "Member" || declaration.Attribute("Datatype")?.Value != dataType)
                            throw new BridgeException("ProfileCatalogInvalid", "Native storage declaration does not match the profile type.");
                        declaration.SetAttributeValue("Name", name);
                    }
                    else declaration = TypedFbInstanceDeclaration(capture, context.Callees[calleePath], section.Name.Namespace, name, dataType);
                    section.Add(declaration);
                    CaptureTypedDependents(capture, declaration, preview);
                    ((JArray)preview["effects"]).Add(new JObject { ["action"] = "createMultiInstance", ["callerPath"] = capture.CallerPath, ["member"] = name, ["dataType"] = dataType,
                        ["startValueImpact"] = "New static instance is initialized from its type defaults; existing instance DB layouts may be regenerated by native compilation." });
                    instance = new TypedGraphicalInstance { Identity = TypedMemberIdentity(capture.CallerPath, new[] { name }), Scope = "LocalVariable", Components = new[] { name }, CalleePath = calleePath, DataType = dataType, DeclarationHash = ChangeHash(document.ToString(SaveOptions.DisableFormatting)) };
                }
                else if (kind == "createGlobal")
                {
                    string name = ChangeText(request, "name", 128); ValidateTypedName(name);
                    if (model.Operation == "createBlock" && change.BlockNames.Any(callerName => string.Equals(callerName, name, StringComparison.OrdinalIgnoreCase)))
                        throw new BridgeException("DeclarationCollision", "The new instance DB name is already reserved by the caller block in this prepared change.");
                    if (capture.Blocks.Any(b => ObjectIdentity.ScopePath(capture.Plc.Path, b.Path) == change.ScopePath && b.Item.Namespace == change.Namespace && string.Equals(b.Item.Name, name, StringComparison.OrdinalIgnoreCase)))
                        throw new BridgeException("DeclarationCollision", "The new instance DB name already exists in the exact software scope and namespace.");
                    if (change.TypedInstanceCreations.Any(c => string.Equals(c.Value<string>("name"), name, StringComparison.OrdinalIgnoreCase))) throw new BridgeException("DeclarationCollision", "Multiple planned instances use the same DB name.");
                    var usedNumbers = new System.Collections.Generic.HashSet<int>(capture.Blocks.Where(b => b.Item is DataBlock).Select(b => b.Item.Number).Concat(change.TypedInstanceCreations.Select(c => c.Value<int>("number"))));
                    int number = Enumerable.Range(1, 65535).FirstOrDefault(n => !usedNumbers.Contains(n));
                    if (number == 0) throw new BridgeException("BlockNumberExhausted", "No eligible instance DB number is available.");
                    string path = ObjectIdentity.ObjectPath(change.GroupPath, name, change.Namespace);
                    var creation = new JObject { ["name"] = name, ["path"] = path, ["calleePath"] = calleePath, ["number"] = number };
                    string declarationHash;
                    if (system)
                    {
                        if (string.IsNullOrEmpty(storageProfile.GlobalInstanceType) || string.IsNullOrEmpty(storageProfile.GlobalInstanceTemplateXml))
                            throw new BridgeException("InstanceCreationUnsupported", "The profile lacks an independently captured global IEC storage document.");
                        var dbXml = EngineeringSources.ReadXml(storageProfile.GlobalInstanceTemplateXml);
                        var db = dbXml.Root.Elements("SW.Blocks.InstanceDB").SingleOrDefault();
                        var attrs = db?.Element("AttributeList");
                        if (attrs == null || attrs.Element("InstanceOfName")?.Value != storageProfile.GlobalInstanceType || attrs.Element("OfSystemLibElement")?.Value != storageProfile.GlobalInstanceType)
                            throw new BridgeException("ProfileCatalogInvalid", "Captured global IEC storage type does not match the instruction profile.");
                        if (attrs.Element("Namespace")?.Value != change.Namespace)
                            throw new BridgeException("InstanceCreationRouteUnsupported", "The captured global IEC storage namespace differs from the selected exact namespace.");
                        attrs.Element("Name").Value = name; attrs.Element("Number").Value = number.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        if (context.Route == "standard") attrs.Element("AutoNumber").Value = "false";
                        UseProjectCultures(dbXml, capture.Plc.Project);
                        string file = Path.Combine(change.Directory, "new-instance-" + key + ".xml");
                        File.WriteAllText(file, dbXml.ToString(SaveOptions.DisableFormatting), new System.Text.UTF8Encoding(true));
                        creation["artifact"] = ChangeArtifact(file); creation["instanceOfName"] = storageProfile.GlobalInstanceType;
                        creation["autoNumber"] = attrs.Element("AutoNumber")?.Value == "true";
                        dataType = storageProfile.GlobalInstanceType; declarationHash = ChangeFileHash(file);
                    }
                    else
                    {
                        var callee = capture.Block(calleePath); RequireUniqueDeclaredFb(capture, dataType, calleePath);
                        creation["instanceOfName"] = QualifiedName(callee.Item);
                        var dbXml = PrepareTypedFbInstanceDocument(capture, name, number, callee);
                        UseProjectCultures(dbXml, capture.Plc.Project);
                        string file = Path.Combine(change.Directory, "new-instance-" + key + ".xml");
                        string xml = change.CapturedStorageQualification != null
                            ? TypedGraphicalCapturedStorageTemplate.SerializePreparedDocument(dbXml)
                            : dbXml.ToString(SaveOptions.DisableFormatting);
                        File.WriteAllText(file, xml, new System.Text.UTF8Encoding(true));
                        creation["artifact"] = ChangeArtifact(file);
                        creation["autoNumber"] = dbXml.Root.Element("SW.Blocks.InstanceDB").Element("AttributeList").Element("AutoNumber")?.Value == "true";
                        declarationHash = ChangeFileHash(file);
                    }
                    change.TypedInstanceCreations.Add(creation);
                    var effect = (JObject)creation.DeepClone(); effect["action"] = "createInstanceDB";
                    effect["startValueImpact"] = "New instance DB initialized from the exact captured system storage or callee's declared start values.";
                    ((JArray)preview["effects"]).Add(effect);
                    string qualifiedName = string.IsNullOrEmpty(change.Namespace) ? name : change.Namespace + "." + name;
                    instance = new TypedGraphicalInstance { Identity = path, Scope = "GlobalVariable", Components = new[] { qualifiedName }, CalleePath = calleePath, DataType = dataType, DeclarationHash = declarationHash };
                }
                else throw new BridgeException("InvalidInstanceKind", "Instance kind must be existingGlobal, existingMulti, createGlobal or createMulti.");
                context.Instances.Add(key, instance);
            }
        }

        private static void ValidateTypedName(string value)
        { if (!Regex.IsMatch(value, @"^[A-Za-z_][A-Za-z0-9_]{0,79}$")) throw new BridgeException("InvalidDeclarationName", "New storage names must be simple identifiers of at most 80 characters."); }
        private static bool SameDeclaredType(string left, string right) => string.Equals(left?.Trim('"'), right?.Trim('"'), StringComparison.Ordinal);

        private XDocument PrepareTypedFbInstanceDocument(TypedResolutionCapture capture, string name, int number, BlockEntry callee)
        {
            if (capture.Change.CapturedStorageQualification != null)
                return PrepareCapturedStorageInstance(capture, name, number, callee);
            string catalogPath = TypedCatalogPath();
            if (catalogPath == null) throw new BridgeException("InstanceCreationProfileUnavailable", "No deployed native instance creation catalog exists.");
            var catalog = JObject.Parse(File.ReadAllText(catalogPath));
            string cpu = TypedCpuIdentifier(capture.Plc);
            string route = TypedRoute(ResolveSoftwareScope(capture.Plc, capture.Change.ScopePath), capture.Change.Namespace);
            var templates = (catalog["globalInstanceTemplates"] as JArray ?? new JArray()).OfType<JObject>().Where(t => t.Value<string>("tiaVersion") == "V21" && t.Value<string>("cpuFamily") == cpu && t.Value<string>("route") == route).ToArray();
            BridgeException last = null;
            foreach (var template in templates)
            {
                string path = TypedResourcePath(catalogPath, template.Value<string>("path"));
                if (!File.Exists(path) || ChangeFileHash(path) != template.Value<string>("sha256")) throw new BridgeException("ProfileCatalogInvalid", "Captured native global instance template source hash does not match.");
                try
                {
                    var result = TypedGraphicalInstanceTemplate.Create(name, number, capture.Export(callee), EngineeringSources.ReadXml(File.ReadAllText(path)));
                    capture.Change.TypedDependencies.Add(new JObject { ["kind"] = "profileCatalog", ["artifact"] = ChangeArtifact(path) });
                    return result;
                }
                catch (BridgeException ex) { last = ex; }
            }
            throw new BridgeException("InstanceCreationProfileUnavailable", "No captured instance template covers the exact callee persistent declarations: " + last?.Message);
        }

        private static XElement TypedFbInstanceDeclaration(TypedResolutionCapture capture, TypedGraphicalCallee callee, XNamespace ns, string name, string dataType)
        {
            return capture.Owner.PrepareTypedMultiDeclaration(capture, callee, name, dataType);
        }
        private static void RequireUniqueDeclaredFb(TypedResolutionCapture capture, string type, string path)
        {
            var matches = capture.Blocks.Where(b => b.Item is FB && QualifiedName(b.Item) == type.Trim('"') && ObjectIdentity.ScopePath(capture.Plc.Path, b.Path) == capture.Change.ScopePath).ToArray();
            if (matches.Length != 1 || matches[0].Path != path) throw new BridgeException("AmbiguousInstanceType", "The native declaration cannot uniquely identify the selected FB across the complete PLC index.");
        }
        private void CaptureTypedDependents(TypedResolutionCapture capture, XElement declaration, JObject preview)
        {
            if (!capture.Blocks.Any(b => b.Path == capture.CallerPath)) return;
            var analysis = ReadNativeAnalysis(new JObject { ["projectName"] = capture.Plc.Project.Name, ["plcPath"] = capture.Plc.Path, ["maxItems"] = 2000 });
            if (analysis.IndexTruncated || analysis.Budget.Truncated || analysis.Warnings.Count > 0)
                throw new BridgeException("DependencyUnresolved", "Interface-changing typed edits require complete native dependent coverage.");
            var affected = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal) { capture.CallerPath };
            bool grew;
            do
            {
                grew = false;
                foreach (var edge in analysis.References)
                {
                    if (!new[] { ReferenceType.InstanceType, ReferenceType.TypeInstance, ReferenceType.Uses, ReferenceType.UsedBy }.Contains(edge.Relation)) continue;
                    var actor = edge.Actor?.BlockPath; var subject = edge.Subject?.BlockPath;
                    if (subject != null && actor != null && affected.Contains(subject) && affected.Add(actor)) grew = true;
                }
            } while (grew);
            foreach (string path in affected.Where(p => p != capture.CallerPath))
            {
                var block = capture.Blocks.SingleOrDefault(b => b.Path == path) ?? throw new BridgeException("DependencyUnresolved", "A dependent native block lacks exact identity.");
                capture.Export(block);
                var dependency = capture.Change.TypedDependencies.OfType<JObject>().Single(d => d.Value<string>("kind") == "block" && d.Value<string>("path") == path);
                if (block.Item is InstanceDB instance)
                {
                    var caller = capture.Blocks.Single(b => b.Path == capture.CallerPath);
                    if (instance.InstanceOfName != QualifiedName(caller.Item) || instance.Namespace != caller.Item.Namespace)
                        throw new BridgeException("DependencyProfileUnavailable", "Transitive dependent instance layout changes require an independently captured profile.");
                    PrepareTypedDependentInstance(capture, dependency, QualifiedName(caller.Item), declaration);
                }
                else if (block.Item is FB && TypedResolutionCapture.Members(capture.Documents[path]).Any(m => m.Section == "Static" && SameDeclaredType(m.DataType, QualifiedName(capture.Blocks.Single(b => b.Path == capture.CallerPath).Item))))
                    throw new BridgeException("DependencyProfileUnavailable", "A parent multi-instance embeds the changed caller; transitive declaration propagation needs a captured dependency profile.");
                ((JArray)preview["effects"]).Add(new JObject { ["action"] = "nativeDependentRecompile", ["path"] = path, ["declarationHash"] = capture.Hashes[path],
                    ["expectedArtifact"] = dependency["expectedArtifact"]?.DeepClone(),
                    ["startValueImpact"] = block.Item is InstanceDB ? "Only the explicit new storage member is added using captured native defaults; existing full XML and start values must match the prepared document." : "Dependent type/call consistency is compiled; complete native XML must remain unchanged." });
            }
        }

        private void ValidateTypedDependencies(EngineeringChange change)
        {
            if (change.Kind != "graphical_typed_edit") return;
            ValidateCapturedStorageBindings(change);
            var plc = SelectPlc(change.Selector); var budget = new ScanBudget(50000);
            var blocks = AllBlocks(plc, budget).ToArray();
            if (budget.Truncated) throw new BridgeException("TraversalLimit", "Typed dependency revalidation was incomplete.");
            foreach (JObject dependency in change.TypedDependencies)
            {
                if (dependency["expectedArtifact"] is JObject expected && (!File.Exists(expected.Value<string>("path")) || ChangeFileHash(expected.Value<string>("path")) != expected.Value<string>("sha256")))
                    throw new BridgeException("ArtifactChanged", "A prepared dependent declaration effect changed.");
                if (dependency.Value<string>("kind") == "profileCatalog")
                {
                    var artifact = dependency["artifact"];
                    if (!File.Exists(artifact.Value<string>("path")) || ChangeFileHash(artifact.Value<string>("path")) != artifact.Value<string>("sha256"))
                        throw new BridgeException("ArtifactChanged", "Instruction profile catalog changed after preparation.");
                }
                if (dependency.Value<string>("kind") == "block")
                {
                    string path = dependency.Value<string>("path");
                    var target = blocks.SingleOrDefault(b => b.Path == path) ?? throw new BridgeException("StaleDeclaration", "Dependency no longer exists: " + path);
                    var artifact = dependency["artifact"];
                    if (ChangeFileHash(artifact.Value<string>("path")) != artifact.Value<string>("sha256")) throw new BridgeException("ArtifactChanged", "Prepared declaration artifact changed.");
                    string fresh = Path.Combine(change.Directory, "revalidate-" + Guid.NewGuid().ToString("N") + ".xml");
                    target.Item.Export(new FileInfo(fresh), ExportOptions.WithDefaults, DocumentInfoOptions.None);
                    try { EngineeringSources.RequireNativePreservation(EngineeringSources.ReadXml(File.ReadAllText(artifact.Value<string>("path"))), EngineeringSources.ReadXml(File.ReadAllText(fresh))); }
                    catch (BridgeException ex) { throw new BridgeException("StaleDeclaration", "Typed dependency changed: " + path + "; " + ex.Message); }
                }
                else if (dependency.Value<string>("kind") == "type")
                {
                    var scan = new ScanBudget(50000);
                    var types = SoftwareScopes(plc, scan).SelectMany(s => ScopeTypes(s, scan)).ToArray();
                    var type = types.SingleOrDefault(t => t.Path == dependency.Value<string>("path"));
                    if (scan.Truncated || type == null) throw new BridgeException("StaleDeclaration", "The exact UDT dependency is unavailable.");
                    var artifact = dependency["artifact"];
                    if (ChangeFileHash(artifact.Value<string>("path")) != artifact.Value<string>("sha256")) throw new BridgeException("ArtifactChanged", "Prepared UDT dependency artifact changed.");
                    string fresh = Path.Combine(change.Directory, "revalidate-type-" + Guid.NewGuid().ToString("N") + ".xml");
                    type.Item.Export(new FileInfo(fresh), ExportOptions.WithDefaults, DocumentInfoOptions.None);
                    try { EngineeringSources.RequireNativePreservation(EngineeringSources.ReadXml(File.ReadAllText(artifact.Value<string>("path"))), EngineeringSources.ReadXml(File.ReadAllText(fresh))); }
                    catch (BridgeException ex) { throw new BridgeException("StaleDeclaration", "Typed UDT dependency changed: " + ex.Message); }
                }
                else if (dependency.Value<string>("kind") == "tag")
                {
                    var scope = ResolveSoftwareScope(plc, change.ScopePath); var scan = new ScanBudget(50000);
                    var tables = TagTables(scope.Tags, scope.Path + "/tags", scan).ToArray();
                    if (scan.Truncated) throw new BridgeException("StaleDeclaration", "Tag identity revalidation was incomplete.");
                    var table = TypedGraphicalResolution.Exact(tables, t => t.Path == dependency.Value<string>("path"),
                        "StaleDeclaration", "The exact tag table owner is unavailable or ambiguous.");
                    var tag = TypedGraphicalResolution.Exact(table.Item.Tags, t => t.Name == dependency.Value<string>("name"),
                        "StaleDeclaration", "The exact tag declaration is unavailable or ambiguous.");
                    TypedGraphicalResolution.RequireUniqueSymbol(tables.SelectMany(t => t.Item.Tags).Select(t => t.Name), tag.Name, "StaleDeclaration");
                    if (tag.IsSafety || ChangeHash(tag.Name + "\n" + tag.DataTypeName + "\n" + tag.LogicalAddress) != dependency.Value<string>("nativeHash"))
                        throw new BridgeException("StaleDeclaration", "The selected tag declaration changed after preparation.");
                }
                else if (dependency.Value<string>("kind") == "dbSymbol")
                {
                    var db = TypedGraphicalResolution.Exact(blocks, b => b.Path == dependency.Value<string>("path"),
                        "StaleDeclaration", "The exact DB symbol owner is unavailable or ambiguous.");
                    if (!(db.Item is DataBlock) || ObjectIdentity.ScopePath(plc.Path, db.Path) != change.ScopePath || QualifiedName(db.Item) != dependency.Value<string>("name"))
                        throw new BridgeException("StaleDeclaration", "The selected DB symbol changed after preparation.");
                    TypedGraphicalResolution.RequireUniqueSymbol(blocks.Where(b => b.Item is DataBlock).Select(b => QualifiedName(b.Item)), QualifiedName(db.Item), "StaleDeclaration");
                }
            }
            foreach (JObject creation in change.TypedInstanceCreations)
            {
                if (blocks.Any(b => ObjectIdentity.ScopePath(plc.Path, b.Path) == change.ScopePath && b.Item.Namespace == change.Namespace && string.Equals(b.Item.Name, creation.Value<string>("name"), StringComparison.OrdinalIgnoreCase)))
                    throw new BridgeException("StaleDeclaration", "A planned new instance name now exists.");
                if (creation["artifact"] is JObject artifact && (!File.Exists(artifact.Value<string>("path")) || ChangeFileHash(artifact.Value<string>("path")) != artifact.Value<string>("sha256")))
                    throw new BridgeException("ArtifactChanged", "Prepared instance storage XML changed.");
            }
        }

        private void ApplyTypedGraphical(EngineeringChange change)
        {
            // Checkpoint save/archive can invoke native processing. Repeat the
            // exact qualification binding before the first storage import.
            ValidateCapturedStorageBindings(change);
            var plc = SelectPlc(change.Selector);
            VerifyBlockTargets(change, plc, false);
            foreach (JObject creation in change.TypedInstanceCreations)
            {
                var artifact = creation["artifact"] as JObject ?? throw new BridgeException("InstanceCreationProfileUnavailable", "Every typed storage creation requires a complete immutable native document.");
                MutationBlockGroup(change, plc).Blocks.Import(new FileInfo(artifact.Value<string>("path")), ImportOptions.None);
                var created = MutationBlockGroup(change, plc).Blocks.OfType<InstanceDB>().SingleOrDefault(b => b.Name == creation.Value<string>("name") && b.Namespace == change.Namespace);
                if (created == null || created.InstanceOfName != creation.Value<string>("instanceOfName") || created.Number != creation.Value<int>("number")) throw new BridgeException("InstanceVerificationFailed", "Native instance type/number does not match the prepared exact storage.");
                change.Stages.Add("instance_created:" + creation.Value<string>("path"));
                WriteChangeJournal(change);
            }
            MutationBlockGroup(change, plc).Blocks.Import(new FileInfo(change.InputPath), ImportOptions.Override);
            VerifyExpectedBlocksExist(change, plc);
        }

        private void VerifyTypedInstanceReadbacks(EngineeringChange change)
        {
            ValidateCapturedStorageBindings(change);
            var plc = SelectPlc(change.Selector);
            var budget = new ScanBudget(50000); var blocks = AllBlocks(plc, budget).ToArray();
            if (budget.Truncated) throw new BridgeException("TraversalLimit", "Typed storage readback could not establish complete identities.");
            foreach (JObject creation in change.TypedInstanceCreations)
            {
                var block = blocks.SingleOrDefault(b => b.Path == creation.Value<string>("path"));
                if (!(block?.Item is InstanceDB instance) || instance.InstanceOfName != creation.Value<string>("instanceOfName") || instance.Number != creation.Value<int>("number"))
                    throw new BridgeException("InstanceVerificationFailed", "Created storage identity, type or number differs from its prepared effect.");
                string output = Path.Combine(change.Directory, "instance-readback-" + Guid.NewGuid().ToString("N") + ".xml");
                instance.Export(new FileInfo(output), ExportOptions.WithDefaults, DocumentInfoOptions.None);
                change.Readbacks.Add(new JObject { ["role"] = "typed-instance-readback", ["path"] = block.Path, ["artifact"] = ChangeArtifact(output) });
                if (creation["artifact"] is JObject expected)
                {
                    var before = EngineeringSources.ReadXml(File.ReadAllText(expected.Value<string>("path")));
                    var after = EngineeringSources.ReadXml(File.ReadAllText(output));
                    if (change.CapturedStorageQualification != null) TypedGraphicalCapturedStorageTemplate.RequireReadback(before, after);
                    else EngineeringSources.RequireNativePreservation(before, after);
                }
                else
                    throw new BridgeException("InstancePreservationUnqualified", "The native instance was created, but there is no complete independent expected instance document. Recovery is available; full storage preservation cannot be claimed.");
            }
            // Compilation can regenerate a dependent block. Compare every captured
            // dependency, not only the caller and explicitly created storage.
            foreach (JObject dependency in change.TypedDependencies.Where(d => d.Value<string>("kind") == "block"))
            {
                string path = dependency.Value<string>("path");
                if (change.Backups.Any(b => b.Value<string>("path") == path || b["block"]?.Value<string>("path") == path)) continue;
                var block = blocks.SingleOrDefault(b => b.Path == path) ?? throw new BridgeException("DependencyReadbackFailed", "A prepared block dependency disappeared during apply.");
                // The caller is already checked against requested.xml by the shared
                // structured-edit verifier; its backup identity may be name-only.
                if (change.BlockNames.Contains(block.Item.Name) && ObjectIdentity.ScopePath(plc.Path, path) == change.ScopePath && block.Item.Namespace == change.Namespace) continue;
                string output = Path.Combine(change.Directory, "dependency-readback-" + Guid.NewGuid().ToString("N") + ".xml");
                block.Item.Export(new FileInfo(output), ExportOptions.WithDefaults, DocumentInfoOptions.None);
                var expected = dependency["expectedArtifact"] ?? dependency["artifact"];
                change.Readbacks.Add(new JObject { ["role"] = "typed-dependency-readback", ["path"] = path, ["artifact"] = ChangeArtifact(output) });
                EngineeringSources.RequireNativePreservation(EngineeringSources.ReadXml(File.ReadAllText(expected.Value<string>("path"))), EngineeringSources.ReadXml(File.ReadAllText(output)));
            }
            change.Stages.Add("typed_instance_readbacks_verified");
        }
    }
}
