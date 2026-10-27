using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;
using TiaMcp.Model;

namespace TiaMcp.Openness.V21
{
    // Pure planning only. Native observations are supplied by the library adapter,
    // never accepted as caller assertions. No mutation/executor lives in this class.
    internal static class PlcLibraryIntegrationPlanner
    {
        internal const string ApplyError = "PlcIntegrationNotQualified";
        private static readonly HashSet<string> Scalars = new HashSet<string>(
            "Bool Byte Word DWord LWord SInt USInt Int UInt DInt UDInt LInt ULInt Real LReal Time LTime".Split(' '), StringComparer.Ordinal);

        internal static void ValidatePayload(JObject payload)
        {
            Fields(payload, "payload", "schemaVersion action planOnly source selection target integration");
            if (payload["schemaVersion"]?.Type != JTokenType.Integer || payload.Value<int>("schemaVersion") != 1) Fail("InvalidIntegrationRecipe", "schemaVersion must be integer 1.");
            if (Text(payload, "action") != "plc_component_plan" || payload["planOnly"]?.Type != JTokenType.Boolean || payload.Value<bool>("planOnly") != true)
                Fail("InvalidIntegrationRecipe", "PLC integration requires action=plc_component_plan and planOnly=true. Combined application is unavailable.");
            var source = Obj(payload, "source"); Fields(source, "source", "libraryId revision"); Text(source, "libraryId", 64); Text(source, "revision", 64);
            var target = Obj(payload, "target"); Fields(target, "target", "projectName plcPath scopePath"); Text(target, "projectName", 128); Text(target, "plcPath");
            string units = Text(target, "plcPath") + "/units/", scope = target["scopePath"] == null ? Text(target, "plcPath") : Text(target, "scopePath");
            if (scope != Text(target, "plcPath") && (!scope.StartsWith(units, StringComparison.Ordinal) || scope.IndexOf('/', units.Length) >= 0 || scope.Length == units.Length))
                Fail("UnsupportedIntegrationScope", "scopePath must be the PLC itself or one of its software units; safety units are excluded.");
            var selection = Array(payload, "selection", 1, 1); Pin(selection[0], "selection");
            var recipe = Obj(payload, "integration"); Fields(recipe, "integration", "dependencyPins instanceDb multiInstance caller mappings");
            foreach (var pin in Array(recipe, "dependencyPins", 0, 999)) Pin(pin, "dependency pin");
            if ((recipe["instanceDb"] == null) == (recipe["multiInstance"] == null)) Fail("InvalidIntegrationRecipe", "Select exactly one of instanceDb or multiInstance.");
            if (recipe["multiInstance"] != null)
            {
                var multi = Obj(recipe, "multiInstance"); Fields(multi, "multiInstance", "name"); Identifier(Text(multi, "name", 80));
                if (CreatesStandaloneCaller(payload)) Fail("InvalidIntegrationRecipe", "A multi-instance needs an existing FB caller; a standalone caller is an FC.");
            }
            else
            {
                var instance = Obj(recipe, "instanceDb"); Fields(instance, "instanceDb", "name numberPolicy"); Identifier(Text(instance, "name", 80));
                if (Text(instance, "numberPolicy") != "firstAvailable") Fail("InvalidIntegrationRecipe", "numberPolicy must explicitly be firstAvailable; exact numbering is not supported by the existing typed instance route.");
            }
            var caller = Obj(recipe, "caller");
            if (CreatesStandaloneCaller(payload))
            {
                Fields(caller, "caller", "mode name language numberPolicy"); Identifier(Text(caller, "name", 80));
                if (!new[] { "LAD", "FBD" }.Contains(Text(caller, "language")) || Text(caller, "numberPolicy") != "firstAvailable")
                    Fail("InvalidIntegrationRecipe", "A standalone caller requires LAD/FBD and numberPolicy=firstAvailable.");
            }
            else
            {
                Fields(caller, "caller", "mode blockPath insertAfterNetworkId"); Text(caller, "blockPath");
                if (caller["mode"] != null && Text(caller, "mode") != "existing") Fail("InvalidIntegrationRecipe", "caller.mode must be existing or createStandaloneFc.");
                if (!Regex.IsMatch(Text(caller, "insertAfterNetworkId", 32), @"\A[0-9A-Fa-f]+\z")) Fail("InvalidIntegrationRecipe", "insertAfterNetworkId must be an exact native hexadecimal ID.");
            }
            foreach (var token in Array(recipe, "mappings", 1, 128))
            {
                var mapping = token as JObject; Fields(mapping, "mapping", "parameter direction dataType tagPath dbPath members");
                Identifier(Text(mapping, "parameter", 128));
                bool db = mapping["dbPath"] != null;
                if (db == (mapping["tagPath"] != null)) Fail("InvalidIntegrationRecipe", "Each mapping selects exactly one of tagPath or dbPath.");
                Text(mapping, db ? "dbPath" : "tagPath");
                if (db || mapping["members"] != null)
                    foreach (var member in Array(mapping, "members", 1, 16)) if (member.Type != JTokenType.String) Fail("InvalidIntegrationRecipe", "members must be simple native member names."); else Identifier((string)member);
                if (!new[] { "Input", "Output", "InOut" }.Contains(Text(mapping, "direction"))) Fail("InvalidIntegrationRecipe", "direction must be Input, Output or InOut.");
                if (!Scalars.Contains(Text(mapping, "dataType", 64))) Fail("UnsupportedIntegrationType", "Only explicitly supported scalar parameter mappings are accepted.");
            }
        }

        // Native adapters must bind the XML consumed by the planner to the same
        // original descriptors retained in the plan. Build remains the pure XML builder.
        internal static JObject BuildFromArtifacts(JObject payload, JArray versions, JObject callerArtifact, JObject inventory)
        {
            ValidatePayload(payload);
            if (versions == null || versions.Count == 0 || versions.Count > 1000 || versions.Any(v => !(v is JObject)))
                Fail("DependencyUnresolved", "Native dependency evidence is incomplete.");
            var pin = (JObject)payload["selection"][0];
            var roots = versions.Cast<JObject>().Where(v => SamePin(pin, v)).ToArray();
            if (roots.Length != 1) Fail("DependencyPinMismatch", "Exactly one original root artifact must match the selected type/version.");
            if (CreatesStandaloneCaller(payload) != (callerArtifact == null))
                Fail("IntegrationCallerMismatch", "Existing callers require their original artifact; standalone callers must be absent.");
            var caller = callerArtifact == null ? null : (JObject)callerArtifact.DeepClone();
            string rootXml = PlcComponentContentContract.Artifact(roots[0]["artifact"] as JObject, roots[0].Value<string>("contentSha256"));
            string callerXml = caller == null ? null : PlcComponentContentContract.Artifact(caller, caller.Value<string>("sha256"));
            var plan = Build(payload, versions, EngineeringSources.ReadXml(rootXml),
                callerXml == null ? null : EngineeringSources.ReadXml(callerXml), inventory);
            if (caller != null) plan["callerArtifact"] = caller;
            return plan;
        }

        internal static JObject Build(JObject payload, JArray versions, XDocument fbExport, XDocument callerExport, JObject inventory)
        {
            ValidatePayload(payload);
            var recipe = Obj(payload, "integration"); var rootPin = (JObject)payload["selection"][0];
            var pins = new[] { rootPin }.Concat(((JArray)recipe["dependencyPins"]).Cast<JObject>()).ToArray();
            if (pins.Select(p => GuidText(p, "typeGuid")).Distinct().Count() != pins.Length || pins.Select(p => GuidText(p, "versionGuid")).Distinct().Count() != pins.Length)
                Fail("DependencyConflict", "Root and dependency pins must contain exactly one version per native type with no duplicate versions.");
            if (versions.Count == 0 || versions.Count > 1000 || versions.Any(v => !(v is JObject))) Fail("DependencyUnresolved", "Native dependency evidence is incomplete.");
            if (versions.Select(v => GuidText((JObject)v, "typeGuid")).Distinct().Count() != versions.Count)
                Fail("DependencyConflict", "Native closure contains multiple versions of one type.");
            if (versions.Select(v => Text((JObject)v, "typeName")).Distinct(StringComparer.OrdinalIgnoreCase).Count() != versions.Count)
                Fail("LibraryConflict", "Different native dependency types share a destination name.");
            var versionsByType = versions.Cast<JObject>().ToDictionary(v => GuidText(v, "typeGuid"));
            if (pins.Length != versions.Count || pins.Any(p => !versionsByType.TryGetValue(GuidText(p, "typeGuid"), out var version) || !SamePin(p, version)))
                Fail("DependencyPinMismatch", "dependencyPins must exactly equal the complete native closure excluding the selected root; missing, extra or different versions are rejected.");
            var versionIds = new HashSet<string>(versions.Select(v => v.Value<string>("versionGuid")), StringComparer.OrdinalIgnoreCase);
            foreach (JObject version in versions)
            {
                if (version.Value<string>("state") != "Committed" || version.Value<bool?>("doNotUse") != false) Fail("DependencyConflict", "All native versions must be committed and usable.");
                if (!Regex.IsMatch(version.Value<string>("contentSha256") ?? "", @"\A[0-9a-f]{64}\z")) Fail("DependencyUnresolved", "Every native dependency needs an independently exported content hash.");
                PlcComponentContentContract.ValidatePin(version);
                if (version["requiredVersionGuids"] is not JArray edges || edges.Any(e => !versionIds.Contains((string)e)))
                    Fail("DependencyUnresolved", "Every native dependency edge must resolve within the pinned closure.");
            }
            var root = versions.Cast<JObject>().Single(v => SamePin(rootPin, v));
            var blocks = fbExport.Descendants().Where(e => e.Name.LocalName.StartsWith("SW.Blocks.", StringComparison.Ordinal) && e.Name.LocalName is "SW.Blocks.FB" or "SW.Blocks.FC").ToArray();
            if (blocks.Length != 1 || blocks[0].Name.LocalName != "SW.Blocks.FB") Fail("UnsupportedIntegrationType", "The selected exact version must export one standard FB.");
            var fb = blocks[0]; var attrs = fb.Element("AttributeList"); string fbName = attrs?.Element("Name")?.Value, fbNamespace = attrs?.Element("Namespace")?.Value ?? "";
            Identifier(fbName);
            if (fbName != root.Value<string>("typeName") || !new[] { "SCL", "STL", "LAD", "FBD" }.Contains(attrs?.Element("ProgrammingLanguage")?.Value))
                Fail("UnsupportedIntegrationType", "FB export must match the selected type name and have a standard language.");
            string plcPath = payload["target"].Value<string>("plcPath"), scopePath = ScopePath(payload); bool unit = scopePath != plcPath;
            if (!unit && fbNamespace.Length != 0) Fail("UnsupportedIntegrationScope", "A namespaced FB needs a software-unit scopePath; the standard PLC scope has no namespaces.");
            if (fb.Descendants().Any(e => (e.Name.LocalName == "IsKnowHowProtected" || e.Attribute("Name")?.Value == "KnowHowProtected") && e.Value.Equals("true", StringComparison.OrdinalIgnoreCase)))
                Fail("ProtectedObject", "Protected library contents are not eligible.");
            var sections = attrs?.Element("Interface")?.Elements().SingleOrDefault(e => e.Name.LocalName == "Sections");
            if (sections == null) Fail("IntegrationInterfaceUnavailable", "The selected FB has no exported interface.");
            var parameters = new List<JObject>();
            foreach (var section in sections.Elements().Where(s => new[] { "Input", "Output", "InOut" }.Contains(s.Attribute("Name")?.Value)))
                foreach (var member in section.Elements().Where(e => e.Name.LocalName == "Member"))
                {
                    string name = member.Attribute("Name")?.Value, type = member.Attribute("Datatype")?.Value;
                    Identifier(name);
                    if (!Scalars.Contains(type ?? "") || member.Descendants().Any(e => e.Name.LocalName is "Member" or "Sections")) Fail("UnsupportedIntegrationType", "Structured, array and reference parameters require a separately qualified mapping route: " + name);
                    parameters.Add(new JObject { ["name"] = name, ["direction"] = section.Attribute("Name").Value, ["dataType"] = type });
                }
            if (parameters.Select(p => p.Value<string>("name")).Distinct(StringComparer.OrdinalIgnoreCase).Count() != parameters.Count) Fail("IntegrationInterfaceUnavailable", "Exported parameter names are ambiguous.");
            var mappings = ((JArray)recipe["mappings"]).Cast<JObject>().ToArray();
            if (mappings.Length != parameters.Count || mappings.Select(m => m.Value<string>("parameter")).Distinct(StringComparer.OrdinalIgnoreCase).Count() != mappings.Length ||
                mappings.Any(m => !parameters.Any(p => p.Value<string>("name") == m.Value<string>("parameter") && p.Value<string>("direction") == m.Value<string>("direction") && p.Value<string>("dataType") == m.Value<string>("dataType"))))
                Fail("IntegrationMappingMismatch", "Supply each exported Input, Output and InOut exactly once with its exact direction and scalar type.");

            if (inventory.Value<bool?>("complete") != true) Fail("IntegrationInventoryIncomplete", "Complete native block/type/tag inventory is required.");
            var existing = Array(inventory, "objects", 0, 50000); var tags = Array(inventory, "tags", 0, 50000);
            var scope = inventory["scope"] as JObject;
            if (unit && (scope?.Value<string>("path") != scopePath || scope.Value<string>("kind") != "softwareUnit")) Fail("IntegrationInventoryIncomplete", "Software-unit planning requires the resolved native unit scope.");
            string scopeNamespace = unit ? scope.Value<string>("namespacePreset") ?? "" : "";
            var existingNames = new HashSet<string>(existing.Select(o => o.Value<string>("name")), StringComparer.OrdinalIgnoreCase);
            // Reuse needs a native exact-version proof (type/version GUID, one link, identical metadata); a name match is never adoption.
            var proofs = (inventory["reusedVersions"] as JArray ?? new JArray()).OfType<JObject>().ToArray();
            bool Reused(JObject v) => proofs.Any(r => SamePin(r, v) && r.Value<string>("typeName") == v.Value<string>("typeName"));
            if (versions.Any(v => existingNames.Contains(v.Value<string>("typeName")) && !Reused((JObject)v))) Fail("LibraryConflict", "An existing PLC object shares a root/dependency name without an exact installed-version proof; it is never adopted by name.");
            bool fbReused = Reused(root);
            if (fbReused && versions.Any(v => !Reused((JObject)v))) Fail("DependencyConflict", "A reused FB requires every pinned dependency to be its exact installed version.");
            string qualifiedFb = fbNamespace.Length == 0 ? fbName : fbNamespace + "." + fbName, fbPath = ObjectIdentity.ObjectPath(scopePath + "/blocks", fbName, fbNamespace);
            if (fbReused)
            {
                var installed = existing.Where(o => o.Value<string>("kind") == "FB" && o.Value<string>("name") == fbName && (o.Value<string>("namespace") ?? "") == fbNamespace).ToArray();
                if (installed.Length != 1) Fail("LibraryConflict", "The reused FB does not resolve to exactly one native FB with the pinned name and namespace.");
                fbPath = installed[0].Value<string>("path");
            }
            bool multi = recipe["multiInstance"] != null;
            string dbName = multi ? null : recipe["instanceDb"].Value<string>("name"), multiName = multi ? recipe["multiInstance"].Value<string>("name") : null;
            JObject reusedDb = null; int dbNumber = 0;
            if (!multi)
            {
                var sameName = existing.Where(o => string.Equals(o.Value<string>("name"), dbName, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (versions.Any(v => string.Equals(v.Value<string>("typeName"), dbName, StringComparison.OrdinalIgnoreCase)) || sameName.Length != 0 && !fbReused) Fail("LibraryConflict", "Instance DB name collides with an existing or planned object.");
                if (sameName.Length != 0)
                {
                    if (sameName.Length != 1 || sameName[0].Value<string>("kind") != "DB" || sameName[0].Value<string>("instanceOfName") != qualifiedFb)
                        Fail("LibraryConflict", "The same-name block is not one instance DB of the reused FB; it is never adopted by name.");
                    reusedDb = (JObject)sameName[0];
                    if (!(reusedDb["users"] is JArray users)) Fail("InstanceUseUnproven", "Native cross-references could not prove the existing instance DB unused; reusing it could share state.");
                    else if (users.Count != 0) Fail("InstanceAlreadyUsed", "The existing instance DB is already used by other blocks; reusing it would share state.", new JObject { ["instanceDbPath"] = reusedDb["path"].DeepClone(), ["users"] = users.DeepClone() });
                }
                else
                {
                    var usedNumbers = new HashSet<int>(existing.Where(o => o.Value<string>("kind") == "DB").Select(o => o.Value<int>("number")));
                    dbNumber = Enumerable.Range(1, 65535).FirstOrDefault(n => !usedNumbers.Contains(n));
                    if (dbNumber == 0) Fail("BlockNumberExhausted", "No DB number is available.");
                }
            }

            bool standalone = CreatesStandaloneCaller(payload);
            string networkId = recipe["caller"].Value<string>("insertAfterNetworkId");
            string callerPath, language, callerKind, callerNamespace, groupPath;
            var blockers = new JArray();
            if (standalone)
            {
                string name = recipe["caller"].Value<string>("name");
                if (callerExport != null) Fail("IntegrationCallerMismatch", "Standalone caller must be absent, with no existing export.");
                if (existingNames.Contains(name) ||
                    versions.Any(v => string.Equals(v.Value<string>("typeName"), name, StringComparison.OrdinalIgnoreCase)) || string.Equals(dbName, name, StringComparison.OrdinalIgnoreCase))
                    Fail("LibraryConflict", "Standalone caller name collides with an existing or planned object.");
                language = recipe["caller"].Value<string>("language"); callerKind = "FC"; callerNamespace = scopeNamespace;
                groupPath = scopePath + "/blocks"; callerPath = ObjectIdentity.ObjectPath(groupPath, name, callerNamespace);
            }
            else
            {
            callerPath = CallerPath(payload);
            var callerRows = existing.Where(o => o.Value<string>("path") == callerPath).ToArray();
            if (callerRows.Length != 1) Fail("IntegrationCallerMismatch", "Caller does not resolve uniquely in the selected native scope.");
            var caller = callerRows[0]; language = caller.Value<string>("language"); callerKind = caller.Value<string>("kind"); callerNamespace = caller.Value<string>("namespace") ?? "";
            if (caller.Value<bool?>("protected") != false || !unit && callerNamespace.Length != 0 || (language != "LAD" && language != "FBD")) Fail("UnsupportedIntegrationCaller", "Caller must be an unprotected LAD/FBD block; namespaces exist only in software units.");
            string suffix = ObjectIdentity.ObjectPath("", caller.Value<string>("name"), callerNamespace);
            if (!callerPath.StartsWith(scopePath + "/blocks/", StringComparison.Ordinal) || !callerPath.EndsWith(suffix, StringComparison.Ordinal)) Fail("IntegrationCallerMismatch", "Caller path does not match its native name and namespace in the selected scope.");
            groupPath = callerPath.Substring(0, callerPath.Length - suffix.Length);
            var nativeCallers = callerExport.Root?.Elements().Where(e => e.Name.LocalName is "SW.Blocks.OB" or "SW.Blocks.FB" or "SW.Blocks.FC").ToArray();
            if (nativeCallers == null || nativeCallers.Length != 1 || nativeCallers[0].Name.LocalName != "SW.Blocks." + callerKind || nativeCallers[0].Element("AttributeList")?.Element("Name")?.Value != caller.Value<string>("name") || nativeCallers[0].Element("AttributeList")?.Element("ProgrammingLanguage")?.Value != language)
                Fail("IntegrationCallerMismatch", "Caller XML does not match the independently resolved native block.");
            var networks = callerExport.Descendants("SW.Blocks.CompileUnit").Where(e => (string)e.Attribute("ID") == networkId).ToArray();
            if (networks.Length != 1 || networks[0].Element("AttributeList")?.Element("ProgrammingLanguage")?.Value != language) Fail("NetworkNotFound", "Exact native caller network is missing or has a different language.");
            bool terminal = !callerExport.Descendants().Any(e => XNode.DocumentOrderComparer.Compare(e, networks[0]) > 0 && !e.Ancestors().Contains(networks[0]) && e.Attribute("ID") != null);
            // Following IDs shift by the inserted count (scope decision 2026-10-25); only an unpredictable shift blocks.
            string shiftBlocker = terminal ? null : PlcComponentCallerInsertion.ShiftBlocker(callerExport);
            if (shiftBlocker != null) blockers.Add(new JObject { ["code"] = "NativeIdPreservationUnsupported", ["message"] = shiftBlocker + " Select a terminal boundary or a separate caller." });
            if (multi)
            {
                if (callerKind != "FB") Fail("UnsupportedIntegrationCaller", "A multi-instance needs an existing FB caller; FC and OB callers have no static storage.");
                var declared = callerExport.Descendants().Where(e => e.Name.LocalName == "Sections").Take(1).SelectMany(s => s.Elements()).SelectMany(s => s.Elements().Where(e => e.Name.LocalName == "Member"));
                if (declared.Any(m => string.Equals((string)m.Attribute("Name"), multiName, StringComparison.OrdinalIgnoreCase)))
                    Fail("DeclarationCollision", "The caller already declares " + multiName + ". An existing multi-instance is not reused: its other uses cannot be proven from the caller alone.");
            }
            }

            var reasons = new JArray();
            void PlanOnly(string code, string message) { if (!reasons.Any(r => r.Value<string>("code") == code)) reasons.Add(new JObject { ["code"] = code, ["message"] = message }); }
            if (unit) PlanOnly("ComponentScopeUnqualified", "Component readback covers the standard PLC scope only, and the namespace of a library instance in a software unit is not natively qualified.");
            if (fbReused) PlanOnly("ComponentRecoveryAnchorUnavailable", "The FB is reused, so no library import step exists whose checkpoint anchors component recovery; prepare this typed step on its own.");
            if (multi) PlanOnly("ComponentMultiInstanceUnqualified", "Component readback proves a global instance DB and an interface-preserving caller insertion only; createMulti adds a caller Static member and regenerates the caller's dependents.");
            var operands = new JArray(); var connections = new JArray(); var observedMappings = new JArray(); var writers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var mapping in mappings.OrderBy(m => parameters.FindIndex(p => p.Value<string>("name") == m.Value<string>("parameter"))))
            {
                string direction = mapping.Value<string>("direction"), parameter = mapping.Value<string>("parameter"), key = "p" + operands.Count.ToString(CultureInfo.InvariantCulture), writer;
                var members = mapping["members"]?.Select(m => (string)m).ToArray(); bool writable = direction != "Input";
                var observed = (JObject)mapping.DeepClone();
                if (mapping["dbPath"] != null)
                {
                    string dbPath = mapping.Value<string>("dbPath");
                    var owners = existing.Where(o => o.Value<string>("path") == dbPath).ToArray();
                    if (owners.Length != 1 || owners[0].Value<string>("kind") != "DB") Fail("IntegrationOperandMissing", "Exact mapped DB path is missing or not a data block in the selected scope.");
                    var owner = (JObject)owners[0];
                    if (owner.Value<bool?>("protected") != false) Fail("ProtectedObject", "Protected DBs cannot be bound.");
                    if ((owner.Value<string>("language") ?? "").StartsWith("F_", StringComparison.Ordinal)) Fail("SafetyBoundary", "Safety DBs are excluded.");
                    if (existing.Count(o => o.Value<string>("kind") == "DB" && string.Equals(Qualified(o), Qualified(owner), StringComparison.OrdinalIgnoreCase)) != 1) Fail("AmbiguousOperand", "The DB symbol is not unique in the complete software scope.");
                    observed["nativeBlock"] = owner.DeepClone(); observed["nativeMember"] = Member(inventory, dbPath, members, mapping).DeepClone();
                    operands.Add(new JObject { ["key"] = key, ["scope"] = "db", ["ownerPath"] = dbPath, ["components"] = new JArray(members), ["dataType"] = mapping["dataType"].DeepClone() });
                    writer = dbPath + "::" + string.Join(".", members);
                    PlanOnly("ComponentReadbackUnavailable", "Component readback re-resolves root tag mappings only; DB-member bindings are checked by the typed step alone.");
                }
                else
                {
                    var matches = tags.Where(t => t.Value<string>("path") == mapping.Value<string>("tagPath")).ToArray();
                    if (matches.Length != 1) Fail("IntegrationTagMissing", "Exact mapped native tag path is missing or ambiguous.");
                    var tag = matches[0]; string name = tag.Value<string>("name"), type = tag.Value<string>("dataType") ?? "";
                    if (tags.Count(t => string.Equals(t.Value<string>("name"), name, StringComparison.OrdinalIgnoreCase)) != 1) Fail("AmbiguousOperand", "The tag name is not unique in the complete software scope.");
                    if (tag.Value<bool?>("isSafety") != false) Fail("SafetyBoundary", "Safety or unclassified tags are excluded.");
                    if (members == null && type != mapping.Value<string>("dataType")) Fail("OperandTypeMismatch", "Mapping type differs from the exact native tag: " + name);
                    if (members != null && !(type.Length > 2 && type[0] == '"' && type[type.Length - 1] == '"')) Fail("OperandTypeMismatch", "Member mappings need a UDT-typed tag: " + name);
                    if (writable && Regex.IsMatch(tag.Value<string>("logicalAddress") ?? "", @"^%?[IE]", RegexOptions.IgnoreCase)) Fail("OperandReadOnly", "Output/InOut mappings cannot write physical input tags.");
                    observed["nativeTag"] = tag.DeepClone();
                    if (members != null)
                    {
                        observed["nativeMember"] = Member(inventory, mapping.Value<string>("tagPath"), members, mapping).DeepClone();
                        PlanOnly("UnsupportedOperandRoute", "The typed call route binds root scalar tags only; a UDT-typed tag member operand is refused at typed preparation.");
                    }
                    if (unit) PlanOnly("UnsupportedScopeReference", "The typed route refuses tag operands inside software units; bind DB members of the unit instead.");
                    operands.Add(new JObject { ["key"] = key, ["scope"] = "tag", ["ownerPath"] = tag["tablePath"].DeepClone(), ["components"] = new JArray(new[] { name }.Concat(members ?? new string[0])), ["dataType"] = members == null ? tag["dataType"].DeepClone() : mapping["dataType"].DeepClone() });
                    writer = tag.Value<string>("path") + (members == null ? "" : "::" + string.Join(".", members));
                }
                if (writable && !writers.Add(writer)) Fail("IntegrationWriteAlias", "Multiple writable parameters map to the same operand.");
                connections.Add(direction == "Output" ? Wire("call", parameter, "$" + key, "value") : Wire("$" + key, "value", "call", parameter));
                observedMappings.Add(observed);
            }
            var nodes = new JArray();
            if (language == "LAD") { nodes.Add(new JObject { ["key"] = "rail", ["kind"] = "powerrail" }); connections.Insert(0, Wire("rail", "out", "call", "en")); }
            else { operands.Add(new JObject { ["key"] = "enable", ["scope"] = "constant", ["dataType"] = "Bool", ["value"] = "true" }); connections.Add(Wire("$enable", "value", "call", "en")); }
            nodes.Add(new JObject { ["key"] = "call", ["kind"] = "call", ["calleePath"] = fbPath, ["instance"] = "instance" });
            var instanceRequest = multi ? new JObject { ["key"] = "instance", ["kind"] = "createMulti", ["name"] = multiName }
                : reusedDb != null ? new JObject { ["key"] = "instance", ["kind"] = "existingGlobal", ["blockPath"] = reusedDb["path"].DeepClone() }
                : new JObject { ["key"] = "instance", ["kind"] = "createGlobal", ["name"] = dbName };
            var graphical = new JObject { ["blockPath"] = callerPath, ["scopePath"] = scopePath, ["groupPath"] = groupPath, ["namespace"] = callerNamespace,
                ["model"] = new JObject { ["schemaVersion"] = 1, ["language"] = language, ["operation"] = "insertNetwork", ["insertAfterNetworkId"] = networkId, ["nodes"] = nodes, ["operands"] = operands, ["connections"] = connections },
                ["instances"] = new JArray(instanceRequest) };
            if (standalone)
            {
                graphical.Remove("blockPath");
                var model = (JObject)graphical["model"]; model["operation"] = "createBlock"; model.Remove("insertAfterNetworkId");
                model["creation"] = new JObject { ["name"] = recipe["caller"]["name"].DeepClone(), ["blockType"] = "FC", ["interface"] = new JArray() };
            }
            var steps = new JArray();
            if (!fbReused)
                steps.Add(Step(new JObject { ["kind"] = "library_instantiate", ["prepareArguments"] = new JObject { ["kind"] = "library_instantiate", ["payload"] = new JObject { ["schemaVersion"] = 1, ["source"] = payload["source"].DeepClone(), ["selection"] = payload["selection"].DeepClone(), ["target"] = payload["target"].DeepClone() } }, ["requiresFreshPreparation"] = true },
                    new JArray(reasons.Where(r => r.Value<string>("code") == "ComponentScopeUnqualified").Select(r => r.DeepClone()))));
            steps.Add(Step(new JObject { ["kind"] = "graphical_typed_edit", ["prepareArguments"] = new JObject { ["kind"] = "graphical_typed_edit", ["projectName"] = payload["target"]["projectName"].DeepClone(), ["plcPath"] = plcPath, ["payload"] = graphical }, ["requiresFreshPreparation"] = true,
                ["mustWaitFor"] = fbReused ? "Fresh typed preparation against the reused FB and the current caller." : "Exact imported FB/dependency identity and content readback; new native project fingerprint." }, reasons));
            for (int i = 0; i < steps.Count; i++) ((JObject)steps[i]).AddFirst(new JProperty("order", i + 1));
            var plan = new JObject {
                ["schemaVersion"] = 1, ["status"] = "planned_only", ["applySupported"] = false, ["applyError"] = ApplyError,
                ["nativeMutationDispatched"] = false, ["qualification"] = "offline_planner_only_native_qualification_required",
                ["versions"] = versions.DeepClone(), ["interface"] = new JArray(parameters), ["mappings"] = observedMappings,
                ["fb"] = new JObject { ["name"] = fbName, ["namespace"] = fbNamespace, ["scopePath"] = scopePath, ["path"] = fbPath, ["reused"] = fbReused },
                ["reuse"] = new JObject { ["versions"] = new JArray(versions.Cast<JObject>().Where(Reused).Select(v => new JObject { ["typeName"] = v["typeName"].DeepClone(), ["typeGuid"] = v["typeGuid"].DeepClone(), ["versionGuid"] = v["versionGuid"].DeepClone() })),
                    ["instanceDbPath"] = reusedDb?["path"].DeepClone(), ["duplicatesCreated"] = false },
                ["caller"] = new JObject { ["mode"] = standalone ? "createStandaloneFc" : "existing", ["blockPath"] = callerPath,
                    ["initiallyAbsent"] = standalone, ["language"] = language, ["obCallAdded"] = callerKind == "OB", ["kind"] = callerKind, ["namespace"] = callerNamespace },
                ["blockers"] = blockers,
                ["steps"] = steps,
                ["recoveryPlan"] = new JObject { ["affectedResource"] = "The complete selected project, including its project library, caller, and new FB/dependencies/instance DB.", ["requiredBeforeFirstMutation"] = "Persist an initial project checkpoint and hashes for all affected and unrelated native exports. No checkpoint is created by this plan.", ["separateStepsAreAtomic"] = false, ["automaticRetryAllowed"] = false, ["uncertainOutcome"] = "Read change_status for the dispatched step; never replay. Preserve both step journals and recover the initial project checkpoint into a new owned location, then compare after fresh reopen.", ["sourceLibraryPolicy"] = "Read-only source; no library publication, default changes or update operation." },
                ["qualificationGates"] = new JArray("Exact native root and dependency version/content readback after import", "Fresh typed preparation: scope, protection, tag types/addresses, insertion preservation and DB collision checks", "Caller and DB native readback, exact call parameter bindings and compile diagnostics", "Unrelated native content comparison; save and fresh-session reopen", "Initial-checkpoint recovery and interruption/no-replay cases", "Independent simulator input/output and state assertions")
            };
            if (multi) plan["multiInstance"] = new JObject { ["name"] = multiName, ["callerPath"] = callerPath, ["dataType"] = "\"" + qualifiedFb + "\"" };
            else plan["instanceDb"] = reusedDb != null ? new JObject { ["name"] = reusedDb["name"].DeepClone(), ["path"] = reusedDb["path"].DeepClone(), ["number"] = reusedDb["number"].DeepClone(), ["reused"] = true }
                : new JObject { ["name"] = dbName, ["path"] = ObjectIdentity.ObjectPath(groupPath, dbName, callerNamespace), ["numberPolicy"] = "firstAvailable", ["provisionalNumber"] = dbNumber, ["numberMustBeRecheckedAfterInstantiation"] = true };
            return plan;
        }

        internal static bool CreatesStandaloneCaller(JObject payload) => payload?["integration"]?["caller"]?.Value<string>("mode") == "createStandaloneFc";
        internal static string CallerPath(JObject payload) => CreatesStandaloneCaller(payload)
            ? ScopePath(payload) + "/blocks/" + Uri.EscapeDataString(payload["integration"]["caller"].Value<string>("name"))
            : payload["integration"]["caller"].Value<string>("blockPath");
        internal static string ScopePath(JObject payload) => payload["target"].Value<string>("scopePath") ?? payload["target"].Value<string>("plcPath");

        // planOnly marks a step the component route cannot dispatch or read back yet; the coordinator refuses such plans.
        private static JObject Step(JObject step, JArray reasons) { if (reasons.Count != 0) { step["planOnly"] = true; step["planOnlyReasons"] = reasons; } return step; }
        private static string Qualified(JToken row) => string.IsNullOrEmpty(row.Value<string>("namespace")) ? row.Value<string>("name") : row.Value<string>("namespace") + "." + row.Value<string>("name");
        private static JObject Member(JObject inventory, string owner, string[] path, JObject mapping)
        {
            var rows = inventory["members"]?[owner] as JArray;
            if (rows == null) Fail("IntegrationInventoryIncomplete", "Native member evidence is missing for " + owner);
            var row = rows.OfType<JObject>().FirstOrDefault(r => r["path"] is JArray p && p.Select(c => (string)c).SequenceEqual(path, StringComparer.Ordinal));
            string label = owner + ": " + string.Join(".", path);
            if (row == null) Fail("IntegrationMappingMismatch", "Member does not exist in the native declaration: " + label);
            if (row.Value<bool?>("scalar") != true) Fail("UnsupportedIntegrationType", "Only scalar members can be bound: " + label);
            if (row.Value<string>("dataType") != mapping.Value<string>("dataType")) Fail("OperandTypeMismatch", "Mapping type differs from the exact native member: " + label);
            return row;
        }

        private static JObject Wire(string from, string fromPort, string to, string toPort) => new JObject { ["from"] = new JObject { ["node"] = from, ["port"] = fromPort }, ["to"] = new JObject { ["node"] = to, ["port"] = toPort } };
        private static bool SamePin(JObject a, JObject b) => GuidText(a, "typeGuid") == GuidText(b, "typeGuid") && GuidText(a, "versionGuid") == GuidText(b, "versionGuid");
        private static void Pin(JToken token, string context) { var p = token as JObject; Fields(p, context, "typeGuid versionGuid"); GuidText(p, "typeGuid"); GuidText(p, "versionGuid"); }
        private static string GuidText(JObject value, string key) { string raw = Text(value, key, 36); if (!Guid.TryParseExact(raw, "D", out var id) || id == Guid.Empty) Fail("InvalidIntegrationRecipe", key + " must be a nonempty canonical GUID."); return id.ToString("D"); }
        private static void Identifier(string name) { if (name == null || !Regex.IsMatch(name, @"\A[A-Za-z_][A-Za-z0-9_]{0,127}\z")) Fail("InvalidIntegrationRecipe", "This recipe requires simple native names."); }
        private static JObject Obj(JObject value, string key) { if (value?[key] is JObject result) return result; Fail("InvalidIntegrationRecipe", key + " must be an object."); return null; }
        private static JArray Array(JObject value, string key, int min, int max) { if (value?[key] is JArray a && a.Count >= min && a.Count <= max) return a; Fail("InvalidIntegrationRecipe", key + " must be a bounded array."); return null; }
        private static string Text(JObject value, string key, int max = 4096) { var t = value?[key]; if (t?.Type == JTokenType.String && !string.IsNullOrWhiteSpace((string)t) && ((string)t).Length <= max) return (string)t; Fail("InvalidIntegrationRecipe", key + " must be a nonempty bounded string."); return null; }
        private static void Fields(JObject value, string context, string fields) { var allowed = new HashSet<string>(fields.Split(' '), StringComparer.Ordinal); if (value == null || value.Properties().Any(p => !allowed.Contains(p.Name))) Fail("InvalidIntegrationRecipe", context + " must be an object with only documented fields."); }
        private static void Fail(string code, string message, JObject details = null) => throw new PlcIntegrationPlanException(code, message, details);
    }

    internal sealed class PlcIntegrationPlanException : Exception
    {
        internal string Code { get; }
        internal JObject Details { get; }
        internal PlcIntegrationPlanException(string code, string message, JObject details = null) : base(message) { Code = code; Details = details; }
    }
}
