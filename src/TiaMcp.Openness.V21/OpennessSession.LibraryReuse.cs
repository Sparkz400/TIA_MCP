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
        /// identical type and version GUID, and a native comparison with every property identical. A name
        /// match alone, a different GUID, a different version or any non-identical property is a conflict.
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
            var different = properties.Where(x => x.DetailCompareStatus != DetailCompareStatus.ObjectsIdentical).ToList();
            if (different.Count != 0)
                throw new BridgeException("DependencyConflict", "The installed dependency has the requested identity but native content is not proven identical: " + name,
                    new JObject { ["typeName"] = name, ["versionGuid"] = source.Guid.ToString("D"), ["properties"] = new JArray(different.Select(x => new JObject { ["description"] = x.Description, ["status"] = x.DetailCompareStatus.ToString() })) });
            return new JObject { ["typeName"] = name, ["typeGuid"] = source.TypeObject.Guid.ToString("D"), ["versionGuid"] = source.Guid.ToString("D"), ["version"] = source.VersionNumber.ToString(),
                ["object"] = LibraryObjectDescription(uses[0].LibraryTypeInstance), ["comparedProperties"] = properties.Count, ["proof"] = "Exact installed type/version GUID, one native link and all native comparison properties identical." };
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
