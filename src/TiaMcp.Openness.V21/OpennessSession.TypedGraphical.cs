using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using Siemens.Engineering;
using Siemens.Engineering.SW.Blocks;
using TiaMcp.Analysis;
using TiaMcp.Model;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        private static readonly JsonSerializer TypedSerializer = JsonSerializer.Create(new JsonSerializerSettings
        { ContractResolver = new CamelCasePropertyNamesContractResolver() });

        private static JObject TypedChangeQualification(EngineeringChange change) => new JObject
        {
            ["engineeringVerified"] = CompilationEvidenceRules.IsSuccessful(change.Compilation),
            ["preservationVerified"] = change.Stages.Values<string>().Contains("native_preservation_verified") && change.Stages.Values<string>().Contains("typed_instance_readbacks_verified"),
            ["saved"] = change.Stages.Values<string>().Contains("changed_project_saved"),
            ["persistenceVerified"] = false, ["recoveryVerified"] = false, ["runtimeVerified"] = false,
            ["scope"] = "Recorded apply of the immutable prepared artifact. Saving is not fresh-session persistence; archive retrieval is not complete recovery comparison. Reopen, recovery and runtime assertions require separate qualification evidence."
        };

        private JObject ReadTypedGraphical(JObject args)
        {
            var selected = SelectBlock(args);
            var language = selected.Block.Item.ProgrammingLanguage.ToString();
            if (language != "LAD" && language != "FBD") throw new BridgeException("UnsupportedLanguage", "Typed network inspection requires a LAD or FBD block.");
            string path = Path.Combine(ArtifactDirectory(args), "typed-read-" + Guid.NewGuid().ToString("N") + ".xml");
            selected.Block.Item.Export(new FileInfo(path), ExportOptions.WithDefaults, DocumentInfoOptions.None);
            var read = TypedGraphical.Read(EngineeringSources.ReadXml(File.ReadAllText(path)), selected.Block.Path, LoadTypedProfiles(null));
            int max = Limit(args, 500);
            var result = Envelope();
            result["graph"] = JObject.FromObject(read, TypedSerializer);
            string networkId = args.Value<string>("networkId");
            var selectedNetworks = read.Networks.Where(n => networkId == null || n.NetworkId == networkId).ToArray();
            if (networkId != null && selectedNetworks.Length == 0) throw new BridgeException("NetworkNotFound", "The exact selected native compile-unit ID does not exist.");
            result["graph"]["networks"] = JArray.FromObject(selectedNetworks.Take(max), TypedSerializer);
            bool inlineXml = args.Value<bool?>("includeNativeXml") ?? false;
            if (!inlineXml)
                foreach (var property in ((JContainer)result["graph"]).DescendantsAndSelf().OfType<JProperty>().Where(p => p.Name == "nativeXml").ToArray()) property.Value = "";
            result["nativeXmlInline"] = inlineXml;
            result["truncated"] = selectedNetworks.Length > max;
            result["nativeXml"] = ChangeArtifact(path);
            result["readOnly"] = true;
            result["coverage"] = "Native network XML and hashes retained. Supported syntax is not type resolution or runtime qualification. Opaque nodes and unresolved connections remain explicit.";
            int byteLimit = args.Value<int?>("maxGraphBytes") ?? 1024 * 1024;
            if (byteLimit < 4096 || byteLimit > 2 * 1024 * 1024) throw new BridgeException("InvalidRequest", "maxGraphBytes must be between 4096 and 2097152.");
            if (Encoding.UTF8.GetByteCount(result.ToString(Formatting.None)) > byteLimit)
                throw new BridgeException("GraphicalReadLimit", "The selected graph exceeds the response limit. Select one networkId or inspect the retained native artifact.", new JObject { ["nativeXml"] = ChangeArtifact(path), ["maxGraphBytes"] = byteLimit, ["complete"] = false });
            return result;
        }

        private void PrepareTypedGraphical(EngineeringChange change, JObject preview)
        {
            var payload = change.Payload;
            TypedGraphicalPayload.Validate(payload);
            var modelToken = payload["model"] as JObject ?? payload;
            TypedGraphicalModel model;
            try { model = modelToken.ToObject<TypedGraphicalModel>(); }
            catch (JsonException ex) { throw new BridgeException("InvalidTypedModel", ex.Message); }
            if (model == null || model.SchemaVersion != 1) throw new BridgeException("UnsupportedModelVersion", "Typed graphical schemaVersion must be 1.");
            if (model.Language != "LAD" && model.Language != "FBD") throw new BridgeException("UnsupportedLanguage", "Typed language must be explicitly LAD or FBD.");
            var plc = SelectPlc(change.Selector);
            bool create = model.Operation == "createBlock";
            string name = create ? model.Creation?.Name : MutationBlockName(payload, plc);
            if (string.IsNullOrEmpty(name)) throw new BridgeException("InvalidTypedModel", "The exact existing block or creation name is required.");
            change.BlockNames = new[] { name };
            PrepareMutationScope(change, plc, "block", change.BlockNames);
            var block = FindBlockByName(plc, name, !create, change);
            if (create && block != null) throw new BridgeException("TargetAlreadyExists", "Typed block creation cannot replace an existing block.");
            if (block != null && block.Item.ProgrammingLanguage.ToString() != model.Language)
                throw new BridgeException("LanguageConversionNotSupported", "Typed edits must retain the selected block's language.");
            PrepareBlockTargets(change, plc, false, !create, (JArray)preview["effects"]);
            var original = create ? PrepareTypedCreationDocument(model, change, plc) : EngineeringSources.ReadXml(File.ReadAllText(change.Backups[0]["artifact"].Value<string>("path")));
            string callerPath = block?.Path ?? ObjectIdentity.ObjectPath(change.GroupPath, name, change.Namespace);
            var capture = new TypedResolutionCapture(this, plc, change, callerPath, original);
            var context = capture.Resolve(model);
            var templateUnit = original.Descendants("SW.Blocks.CompileUnit").FirstOrDefault(unit => unit.Element("AttributeList")?.Element("ProgrammingLanguage")?.Value == model.Language);
            if (templateUnit != null)
            {
                var envelope = new XElement(templateUnit);
                foreach (var text in envelope.Descendants("Text")) text.Value = "";
                context.NetworkTemplateXml = envelope.ToString(SaveOptions.DisableFormatting);
            }
            PrepareTypedInstances(change, model, original, context, capture, preview);
            // Creation is seeded by the coordinator so explicit multi-instance declarations
            // and namespace attributes are in the same immutable prepared artifact.
            var buildModel = model;
            if (create)
            {
                buildModel = modelToken.ToObject<TypedGraphicalModel>();
                buildModel.Operation = "replaceNetwork";
                buildModel.NetworkId = original.Descendants("SW.Blocks.CompileUnit").Single().Attribute("ID").Value;
            }
            var generated = TypedGraphical.Build(original, buildModel, context);
            EngineeringSources.BlockDeclarations(generated.Document);
            ValidateXmlBlockNumbers(generated.Document, plc, change);
            change.InputPath = Path.Combine(change.Directory, "requested.xml");
            File.WriteAllText(change.InputPath, generated.Document.ToString(SaveOptions.DisableFormatting), new UTF8Encoding(true));
            change.InputHash = ChangeFileHash(change.InputPath);
            preview["typed"] = new JObject
            {
                ["schemaVersion"] = 1, ["language"] = model.Language, ["operation"] = model.Operation,
                ["blockPath"] = callerPath, ["networkId"] = generated.NetworkId,
                ["idMapping"] = JObject.FromObject(generated.IdMapping), ["profiles"] = new JArray(generated.Profiles),
                ["diagnostics"] = JArray.FromObject(generated.Diagnostics, TypedSerializer),
                ["resolvedOperands"] = JArray.FromObject(context.Operands.Values, TypedSerializer),
                ["resolvedCallees"] = JArray.FromObject(context.Callees.Values, TypedSerializer),
                ["resolvedInstances"] = JArray.FromObject(context.Instances.Values, TypedSerializer),
                ["target"] = new JObject { ["tiaVersion"] = context.TiaVersion, ["cpuFamily"] = context.CpuFamily, ["route"] = context.Route },
                ["sourceSha256"] = generated.SourceSha256, ["resultSha256"] = generated.ResultSha256,
                ["nativeArtifact"] = ChangeArtifact(change.InputPath),
                ["qualification"] = new JObject { ["engineeringVerified"] = false, ["preservationVerified"] = false,
                    ["persistenceVerified"] = false, ["recoveryVerified"] = false, ["runtimeVerified"] = false }
            };
            preview["declarationDependencies"] = change.TypedDependencies.DeepClone();
            preview["nativeDiff"] = new JObject { ["before"] = change.Backups.DeepClone(), ["after"] = ChangeArtifact(change.InputPath),
                ["selectedNetworkId"] = generated.NetworkId, ["comparison"] = "Complete native XML; existing normalization rules only. All untouched XML remains in the prepared tree." };
            ((JArray)preview["effects"]).Merge(JArray.FromObject(generated.Effects, TypedSerializer));
            ((JArray)preview["warnings"]).Add("Compilation is mandatory. Checkpointing saves all current project edits. Declaration changes may regenerate dependent instance DB layouts; offline start values are included in the preview, running retained values are not captured.");
        }

        private XDocument PrepareTypedCreationDocument(TypedGraphicalModel model, EngineeringChange change, PlcSelection plc)
        {
            string catalogPath = TypedCatalogPath();
            if (catalogPath == null) throw new BridgeException("CreationProfileUnavailable", "No deployed native block creation catalog exists.");
            var catalog = JObject.Parse(File.ReadAllText(catalogPath));
            string target = TypedCpuIdentifier(plc);
            var scope = ResolveSoftwareScope(plc, change.ScopePath);
            string route = TypedRoute(scope, change.Namespace);
            var candidates = (catalog["creationTemplates"] as JArray ?? new JArray()).OfType<JObject>().Where(t => t.Value<string>("tiaVersion") == "V21" && t.Value<string>("cpuFamily") == target &&
                t.Value<string>("route") == route && t.Value<string>("language") == model.Language && t.Value<string>("blockType") == model.Creation.BlockType).ToArray();
            if (candidates.Length == 0) throw new BridgeException("CreationProfileUnavailable", "No independently captured creation template matches the exact language/block/CPU/route.");
            var budget = new ScanBudget(50000); var blocks = AllBlocks(plc, budget).ToArray();
            if (budget.Truncated) throw new BridgeException("TraversalLimit", "New block number allocation requires a complete PLC inventory.");
            var used = new HashSet<int>(blocks.Where(b => BlockNumberKind(b.Item) == model.Creation.BlockType).Select(b => b.Item.Number));
            int number = Enumerable.Range(1, 65535).FirstOrDefault(n => !used.Contains(n));
            if (number == 0) throw new BridgeException("BlockNumberExhausted", "No eligible block number is available.");
            BridgeException last = null;
            foreach (var candidate in candidates)
            {
                string path = TypedResourcePath(catalogPath, candidate.Value<string>("path"));
                if (!File.Exists(path) || ChangeFileHash(path) != candidate.Value<string>("sha256")) throw new BridgeException("ProfileCatalogInvalid", "Native creation template source hash does not match.");
                try
                {
                    var result = TypedGraphicalCreationTemplate.Create(model, change.Namespace, number, EngineeringSources.ReadXml(File.ReadAllText(path)));
                    change.TypedDependencies.Add(new JObject { ["kind"] = "profileCatalog", ["artifact"] = ChangeArtifact(path) });
                    return result;
                }
                catch (BridgeException ex) { last = ex; }
            }
            throw new BridgeException("CreationProfileUnavailable", "No captured native creation template covers the exact requested declarations: " + last?.Message);
        }

        private static string TypedCpuIdentifier(PlcSelection plc)
        {
            var budget = new ScanBudget(50000);
            string hardwarePath = plc.Path.Substring(0, plc.Path.LastIndexOf("/plc/", StringComparison.Ordinal));
            var cpu = DeviceItems(plc.Project, budget).SingleOrDefault(h => h.Path == hardwarePath);
            if (budget.Truncated || cpu?.Item.TypeIdentifier == null) throw new BridgeException("TargetProfileUnavailable", "Cannot establish the exact CPU target identifier.");
            return cpu.Item.TypeIdentifier;
        }

        private static string TypedRoute(SoftwareScope scope, string blockNamespace)
        {
            if (scope.Unit != null && string.Equals(blockNamespace ?? "", scope.Unit.NamespacePreset ?? "", StringComparison.Ordinal)) return "softwareUnit";
            return string.IsNullOrEmpty(blockNamespace) && scope.Unit == null ? "standard" : "namespace";
        }

        private static bool TypedScalar(string type) => new[] { "Bool", "SInt", "USInt", "Int", "UInt", "DInt", "UDInt", "LInt", "ULInt", "Real", "LReal", "Byte", "Word", "DWord", "LWord", "Time", "LTime", "Void" }.Contains(type);

        private sealed class TypedResolutionCapture
        {
            internal readonly OpennessSession Owner;
            internal readonly PlcSelection Plc;
            internal readonly EngineeringChange Change;
            internal readonly string CallerPath;
            internal readonly XDocument Caller;
            internal readonly BlockEntry[] Blocks;
            internal readonly Dictionary<string, XDocument> Documents = new Dictionary<string, XDocument>(StringComparer.Ordinal);
            internal readonly Dictionary<string, string> Hashes = new Dictionary<string, string>(StringComparer.Ordinal);
            internal TypedResolutionCapture(OpennessSession owner, PlcSelection plc, EngineeringChange change, string callerPath, XDocument caller)
            {
                Owner = owner; Plc = plc; Change = change; CallerPath = callerPath; Caller = caller;
                var budget = new ScanBudget(50000); Blocks = AllBlocks(plc, budget).ToArray();
                if (budget.Truncated) throw new BridgeException("TraversalLimit", "Exact typed resolution requires a complete block identity index.");
                Documents[callerPath] = caller;
                Hashes[callerPath] = ChangeHash(caller.ToString(SaveOptions.DisableFormatting));
            }
            internal XDocument Export(BlockEntry block)
            {
                if (Documents.TryGetValue(block.Path, out var cached)) return cached;
                if (block.Item.IsKnowHowProtected || block.NativeSystemGroup) throw new BridgeException("DeclarationUnavailable", "Typed resolution requires an accessible unprotected declaration: " + block.Path);
                string path = Path.Combine(Change.Directory, "dependency-" + Guid.NewGuid().ToString("N") + ".xml");
                block.Item.Export(new FileInfo(path), ExportOptions.WithDefaults, DocumentInfoOptions.None);
                var document = EngineeringSources.ReadXml(File.ReadAllText(path));
                Documents[block.Path] = document; Hashes[block.Path] = ChangeHash(document.ToString(SaveOptions.DisableFormatting));
                Change.TypedDependencies.Add(new JObject { ["kind"] = "block", ["path"] = block.Path, ["artifact"] = ChangeArtifact(path), ["nativeHash"] = Hashes[block.Path] });
                return document;
            }
            internal BlockEntry Block(string path)
            {
                var block = TypedGraphicalResolution.Exact(Blocks, b => b.Path == path, "ExactTargetRequired", "No unique exact block identity matches " + path);
                if (ObjectIdentity.ScopePath(Plc.Path, path) != Change.ScopePath) throw new BridgeException("UnsupportedScopeReference", "Typed references across software scopes require an explicitly qualified inter-unit route.");
                return block;
            }
            internal TypedGraphicalContext Resolve(TypedGraphicalModel model)
            {
                var scope = ResolveSoftwareScope(Plc, Change.ScopePath);
                string cpuType = TypedCpuIdentifier(Plc);
                var context = new TypedGraphicalContext { CpuFamily = cpuType, Route = TypedRoute(scope, Change.Namespace), TargetNamespace = Change.Namespace ?? "" };
                context.Profiles = LoadTypedProfiles(Change);
                foreach (var requested in model.Operands ?? new TypedGraphicalOperandRequest[0])
                {
                    if (requested == null || string.IsNullOrEmpty(requested.Key) || context.Operands.ContainsKey(requested.Key)) throw new BridgeException("DuplicateOperand", "Operand keys must be nonempty and unique.");
                    context.Operands.Add(requested.Key, ResolveOperand(requested));
                }
                foreach (string path in (model.Nodes ?? new TypedGraphicalNode[0]).Where(n => n != null && n.Kind == "call").Select(n => n.CalleePath).Distinct(StringComparer.Ordinal))
                {
                    var callee = Block(path);
                    if (!(callee.Item is FC) && !(callee.Item is FB)) throw new BridgeException("UnsupportedCallee", "A typed call requires an exact FC or FB.");
                    if (callee.Item.ProgrammingLanguage.ToString().StartsWith("F_", StringComparison.Ordinal)) throw new BridgeException("SafetyBoundary", "Typed standard calls cannot target safety blocks.");
                    var xml = Export(callee);
                    var parameters = Members(xml).Where(m => new[] { "Input", "Output", "InOut", "Return" }.Contains(m.Section) && m.Path.Length == 1 && m.DataType != "Void").ToArray();
                    if (parameters.Any(p => !TypedScalar(p.DataType))) throw new BridgeException("UnsupportedParameterType", "Call interface contains a compound, unresolved or unsupported parameter type.");
                    context.Callees[path] = new TypedGraphicalCallee { Path = path, Name = callee.Item.Name, Namespace = callee.Item.Namespace ?? "", BlockType = callee.Item is FB ? "FB" : "FC", InterfaceHash = Hashes[path],
                        Parameters = parameters.Select(p => new TypedGraphicalParameter { Name = p.Name, Section = p.Section, DataType = p.DataType, Required = true }).ToArray() };
                }
                return context;
            }
            private TypedGraphicalOperand ResolveOperand(TypedGraphicalOperandRequest request)
            {
                if (request.Scope == "constant")
                    return new TypedGraphicalOperand { Key = request.Key, Scope = "LiteralConstant", DataType = request.DataType, Value = request.Value, Writable = false, Identity = "constant:" + request.DataType + ":" + request.Value, DeclarationHash = ChangeHash(request.DataType + ":" + request.Value) };
                string[] components = request.Components ?? new string[0];
                if (components.Length == 0 || components.Length > 32 || components.Any(c => string.IsNullOrEmpty(c) || c.Length > 128)) throw new BridgeException("InvalidOperand", "A symbolic operand requires bounded literal name components.");
                if (components.Any(c => c.IndexOfAny(new[] { '[', ']', '^', '%' }) >= 0)) throw new BridgeException("UnsupportedIndexing", "Indirect, address and indexed operands are not enabled by this typed profile; symbolic array bounds are never guessed.");
                if (request.Scope == "tag")
                {
                    if (ResolveSoftwareScope(Plc, Change.ScopePath).Unit != null)
                        throw new BridgeException("UnsupportedScopeReference", "Software-unit tag symbol encoding requires an independently validated reference profile; exact table selection alone cannot prove emitted symbol binding.");
                    if (components.Length != 1 || string.IsNullOrEmpty(request.OwnerPath)) throw new BridgeException("ExactTargetRequired", "A tag requires an exact tag-table ownerPath and one member name.");
                    var budget = new ScanBudget(50000);
                    var tables = TagTables(ResolveSoftwareScope(Plc, Change.ScopePath).Tags, Change.ScopePath + "/tags", budget).ToArray();
                    if (budget.Truncated) throw new BridgeException("TraversalLimit", "Tag resolution was incomplete.");
                    var table = TypedGraphicalResolution.Exact(tables, t => t.Path == request.OwnerPath, "ExactTargetRequired", "No unique tag table exists in the selected scope.");
                    var tag = TypedGraphicalResolution.Exact(table.Item.Tags, t => t.Name == components[0], "OperandNotFound", "No unique exact tag member matches.");
                    TypedGraphicalResolution.RequireUniqueSymbol(tables.SelectMany(t => t.Item.Tags).Select(t => t.Name), tag.Name);
                    if (tag.IsSafety) throw new BridgeException("SafetyBoundary", "Safety tags are excluded from standard typed generation.");
                    if (!TypedScalar(tag.DataTypeName)) throw new BridgeException("UnsupportedOperandType", "Tag type is not a supported scalar.");
                    if (request.DataType != null && request.DataType != tag.DataTypeName) throw new BridgeException("OperandTypeMismatch", "Requested and native tag types differ.");
                    string fingerprint = ChangeHash(tag.Name + "\n" + tag.DataTypeName + "\n" + tag.LogicalAddress);
                    Change.TypedDependencies.Add(new JObject { ["kind"] = "tag", ["path"] = table.Path, ["name"] = tag.Name, ["nativeHash"] = fingerprint });
                    return new TypedGraphicalOperand { Key = request.Key, Scope = "GlobalVariable", Components = components, DataType = tag.DataTypeName, Writable = !Regex.IsMatch(tag.LogicalAddress ?? "", @"^%?[IE]", RegexOptions.IgnoreCase), Identity = TypedMemberIdentity(table.Path, components), DeclarationHash = fingerprint };
                }
                XDocument source; string identity; string[] native;
                if (request.Scope == "local")
                {
                    if (request.OwnerPath != null && request.OwnerPath != CallerPath) throw new BridgeException("ScopeMismatch", "Local operands must belong to the exact caller.");
                    source = Caller; identity = CallerPath; native = components;
                }
                else if (request.Scope == "db")
                {
                    var db = Block(request.OwnerPath);
                    if (!(db.Item is DataBlock)) throw new BridgeException("OperandOwnerMismatch", "DB operands require an exact data-block owner.");
                    if (!string.IsNullOrEmpty(db.Item.Namespace)) throw new BridgeException("UnsupportedNamespaceReference", "Namespaced DB symbolic encoding needs its own native profile.");
                    TypedGraphicalResolution.RequireUniqueSymbol(Blocks.Where(b => b.Item is DataBlock).Select(b => b.Item.Name), db.Item.Name);
                    source = Export(db); identity = db.Path;
                    // XML alone cannot prove a symbolic binding remains unique at apply.
                    // Record uniqueness separately from the exported declaration dependency.
                    Change.TypedDependencies.Add(new JObject { ["kind"] = "dbSymbol", ["path"] = db.Path, ["name"] = db.Item.Name });
                    native = new[] { QualifiedName(db.Item) }.Concat(components).ToArray();
                }
                else throw new BridgeException("InvalidOperandScope", "Operand scope must be local, tag, db or constant.");
                var member = ResolveMember(source, components, identity);
                var rootSection = Members(source).Single(m => m.Path.Length == 1 && m.Name == components[0]).Section;
                if (!TypedScalar(member.DataType) || member.DataType == "Void") throw new BridgeException("UnsupportedOperandType", "Operand type is not a supported resolved scalar: " + member.DataType);
                if (request.DataType != null && request.DataType != member.DataType) throw new BridgeException("OperandTypeMismatch", "Requested and native declaration types differ.");
                return new TypedGraphicalOperand { Key = request.Key, Scope = request.Scope == "local" ? "LocalVariable" : "GlobalVariable", Components = native, DataType = member.DataType,
                    Writable = request.Scope != "local" || !new[] { "Input", "Constant" }.Contains(rootSection), Identity = TypedMemberIdentity(identity, components), DeclarationHash = Hashes[identity] };
            }
            internal OfflineEvidence.InterfaceMember ResolveMember(XDocument source, string[] path, string ownerPath)
            {
                string declarationNamespace = Blocks.FirstOrDefault(b => b.Path == ownerPath)?.Item.Namespace ?? Change.Namespace;
                return TypedGraphicalResolution.ResolveMember(source, path, ownerPath, declarationNamespace, (name, currentNamespace) =>
                {
                    var scan = new ScanBudget(50000);
                    var types = SoftwareScopes(Plc, scan).SelectMany(s => ScopeTypes(s, scan)).ToArray();
                    if (scan.Truncated) throw new BridgeException("TraversalLimit", "UDT identity resolution was incomplete.");
                    var type = TypedGraphicalResolution.ResolveType(types, name, currentNamespace, Change.ScopePath,
                        t => t.Item.Name, t => t.Item.Namespace, t => ObjectIdentity.ScopePath(Plc.Path, t.Path));
                    if (!Documents.TryGetValue(type.Path, out var typeXml))
                    {
                        string file = Path.Combine(Change.Directory, "type-dependency-" + Guid.NewGuid().ToString("N") + ".xml");
                        type.Item.Export(new FileInfo(file), ExportOptions.WithDefaults, DocumentInfoOptions.None);
                        typeXml = EngineeringSources.ReadXml(File.ReadAllText(file)); Documents[type.Path] = typeXml;
                        Hashes[type.Path] = ChangeHash(typeXml.ToString(SaveOptions.DisableFormatting));
                        Change.TypedDependencies.Add(new JObject { ["kind"] = "type", ["path"] = type.Path, ["artifact"] = ChangeArtifact(file), ["nativeHash"] = Hashes[type.Path] });
                    }
                    return Tuple.Create(type.Item.Namespace, typeXml);
                });
            }
            internal static OfflineEvidence.InterfaceMember[] Members(XDocument xml) => TypedGraphicalResolution.Members(xml);
        }

        private static string QualifiedName(PlcBlock block) => string.IsNullOrEmpty(block.Namespace) ? block.Name : block.Namespace + "." + block.Name;
        private static string TypedMemberIdentity(string owner, string[] components) => owner + "::" + string.Concat(components.Select(value => value.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + value));

        private static TypedGraphicalProfile[] LoadTypedProfiles(EngineeringChange change)
        {
            string path = TypedCatalogPath();
            if (path == null) throw new BridgeException("ProfileCatalogUnavailable", "The deployed typed graphical profile catalog is missing.");
            var json = JObject.Parse(File.ReadAllText(path));
            if (json.Value<int>("schemaVersion") != 1) throw new BridgeException("ProfileCatalogInvalid", "Unsupported deployed instruction profile version.");
            change?.TypedDependencies.Add(new JObject { ["kind"] = "profileCatalog", ["artifact"] = ChangeArtifact(path) });
            var sourceHashes = new HashSet<string>(StringComparer.Ordinal);
            foreach (JObject source in json["sources"] as JArray ?? new JArray())
            {
                string file = TypedResourcePath(path, source.Value<string>("path"));
                if (!File.Exists(file) || ChangeFileHash(file) != source.Value<string>("sha256")) throw new BridgeException("ProfileCatalogInvalid", "A deployed independent native profile source is missing or changed: " + file);
                sourceHashes.Add(source.Value<string>("sha256"));
                change?.TypedDependencies.Add(new JObject { ["kind"] = "profileCatalog", ["artifact"] = ChangeArtifact(file) });
            }
            var profiles = json["profiles"]?.ToObject<TypedGraphicalProfile[]>() ?? new TypedGraphicalProfile[0];
            if (profiles.Any(p => !sourceHashes.Contains(p.SourceSha256))) throw new BridgeException("ProfileCatalogInvalid", "A profile does not reference a hash-verified independent native fixture.");
            return profiles;
        }

        private static string TypedResourcePath(string catalogPath, string relative)
        {
            if (string.IsNullOrEmpty(relative) || Path.IsPathRooted(relative) || relative.Contains(":") || relative.Split('/', '\\').Any(p => p == ".." || p == "." || p.Length == 0))
                throw new BridgeException("ProfileCatalogInvalid", "Profile resources require bounded relative paths within the shipped catalog.");
            string root = Path.GetDirectoryName(catalogPath);
            if (relative.Replace('\\', '/').StartsWith("fixtures/typed-graphical/", StringComparison.Ordinal)) root = Path.GetFullPath(Path.Combine(root, "..", ".."));
            string resolved = Path.GetFullPath(Path.Combine(root, relative));
            if (!resolved.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new BridgeException("ProfileCatalogInvalid", "Profile resource escaped its deployment root.");
            return resolved;
        }

        private static string TypedCatalogPath()
        {
            // Resource lookup is relative to immutable binaries, not the working directory.
            string[] roots = { AppDomain.CurrentDomain.BaseDirectory, Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..")), Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..")) };
            foreach (string root in roots)
            {
                string path = Path.Combine(root, "fixtures", "typed-graphical", "profiles.v1.json");
                if (!File.Exists(path)) continue;
                return path;
            }
            return null;
        }
    }
}
