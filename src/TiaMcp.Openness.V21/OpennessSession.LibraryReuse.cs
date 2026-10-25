using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Siemens.Engineering.Library.Compare;
using Siemens.Engineering.Library.Types;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        /// <summary>
        /// A same-name PLC object is reused only as an installed exact dependency: one native link to the
        /// identical type and version GUID, and identical native version metadata (Siemens' comparison does not cover
        /// content; committed versions are immutable). A name match alone, a different GUID or version, or any other
        /// differing metadata is a conflict.
        /// </summary>
        private JObject ProveInstalledLibraryDependency(LibraryChange change, PlcSelection plc, JToken node, LibraryTypeVersion source)
        {
            string name = node.Value<string>("typeName");
            var installed = change.Project.ProjectLibrary.FindVersion(source.Guid);
            if (installed == null || installed.TypeObject.Guid != source.TypeObject.Guid)
                throw new BridgeException("LibraryConflict", "An existing PLC object shares a dependency name without the exact installed library version: " + name + ". Use explicit library_update_project for existing uses.");
            var uses = installed.FindInstances(plc.Software).Where(x => string.Equals(LibraryObjectDescription(x.LibraryTypeInstance).Value<string>("Name"), name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (uses.Count != 1)
                throw new BridgeException("LibraryConflict", "The same-name PLC object is not exactly one native use of the requested dependency version: " + name);
            var properties = installed.CompareTo(source).Properties.Take(201).ToList();
            if (properties.Count > 200) throw new BridgeException("DependencyUnresolved", "Native dependency comparison exceeded 200 properties: " + name);
            // Siemens compares version metadata only. The default flag is library-local and the original library is never compared.
            var different = properties.Where(x => x.DetailCompareStatus != DetailCompareStatus.ObjectsIdentical && x.Description != "Is Default Version"
                && !(x.DetailCompareStatus == DetailCompareStatus.NotCompared && x.Description == "Original Library")).ToList();
            if (different.Count != 0 || !properties.Any(x => x.Description == "GUID of version object" && x.DetailCompareStatus == DetailCompareStatus.ObjectsIdentical))
                throw new BridgeException("DependencyConflict", "The installed dependency has the requested identity but its native version metadata is not identical: " + name,
                    new JObject { ["typeName"] = name, ["versionGuid"] = source.Guid.ToString("D"), ["properties"] = new JArray(different.Select(x => new JObject { ["description"] = x.Description, ["status"] = x.DetailCompareStatus.ToString() })) });
            return new JObject { ["typeName"] = name, ["typeGuid"] = source.TypeObject.Guid.ToString("D"), ["versionGuid"] = source.Guid.ToString("D"), ["version"] = source.VersionNumber.ToString(),
                ["object"] = LibraryObjectDescription(uses[0].LibraryTypeInstance), ["comparedProperties"] = properties.Count, ["properties"] = new JArray(properties.Select(x => new JObject { ["description"] = x.Description, ["status"] = x.DetailCompareStatus.ToString() })),
                ["proof"] = "Exact installed type/version GUID, one native link and identical native version metadata (default flag and uncompared original library excluded). Content is not compared; committed versions are immutable natively." };
        }

        // TIA allows one instance of a library type per device: an instance in another scope (root or software unit)
        // makes CreateFrom fail after the checkpoint, so it is refused in the preview.
        private void RequireNoInstanceInOtherScope(LibraryChange change, PlcSelection plc, SoftwareScope scope, JToken node)
        {
            var installed = change.Project.ProjectLibrary.FindType(Guid.Parse(node.Value<string>("typeGuid")));
            if (installed == null) return;
            var budget = new ScanBudget(50000);
            var local = ScopeBlocks(scope, budget).Select(x => (object)x.Item).Concat(ScopeTypes(scope, budget).Select(x => (object)x.Item)).ToList();
            if (budget.Truncated) throw new BridgeException("DependencyUnresolved", "Target scope inventory exceeded its completeness budget.");
            foreach (var version in installed.Versions)
                foreach (var use in version.FindInstances(plc.Software))
                    if (!local.Any(x => x.Equals(use.LibraryTypeInstance)))
                        throw new BridgeException("LibraryConflict", "TIA allows one instance of library type " + node.Value<string>("typeName") + " per device; it is already instantiated in another software scope of this PLC.");
        }

        private static void VerifyReusedLibraryDependencies(LibraryChange change, PlcSelection plc)
        {
            foreach (var reused in change.Preview["reusedDependencies"] as JArray ?? new JArray())
            {
                var installed = change.Project.ProjectLibrary.FindVersion(Guid.Parse(reused.Value<string>("versionGuid")));
                string name = reused.Value<string>("typeName");
                if (installed == null || installed.FindInstances(plc.Software).Count(x => string.Equals(LibraryObjectDescription(x.LibraryTypeInstance).Value<string>("Name"), name, StringComparison.OrdinalIgnoreCase)) != 1)
                    throw new BridgeException("PreservationMismatch", "A reused installed dependency no longer has exactly one exact native link after instantiation: " + name);
            }
        }

        private static void VerifyInstantiatedNames(LibraryChange change, IEnumerable<string> names)
        {
            var expected = new HashSet<string>((change.Preview["targetNamesBefore"] as JArray ?? new JArray()).Values<string>().Concat((change.Preview["createdNames"] as JArray ?? new JArray()).Values<string>()), StringComparer.OrdinalIgnoreCase);
            var unexpected = names.Where(x => !expected.Contains(x)).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            if (unexpected.Length != 0)
                throw new BridgeException("PreservationMismatch", "Native instantiation created unpreviewed PLC objects.", new JObject { ["unexpectedNames"] = new JArray(unexpected) });
        }
    }
}
