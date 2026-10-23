#nullable disable
using System;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.Library.Types;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Types;

namespace TiaMcp.Openness.V21
{
    internal static class PlcLibraryInstanceCapture
    {
        internal static JObject Read(IEngineeringObject item, Func<bool> admitDependencyRead = null)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));
            var result = new JObject { ["schemaVersion"] = "plc-library-instance-observation.v1",
                ["source"] = "LibraryTypeInstanceInfo.LibraryTypeVersion", ["nativeQualified"] = false };
            JObject State(string status, string reason = null)
            {
                result["status"] = status;
                if (reason != null) result["reason"] = reason;
                return result;
            }
            if (!(item is FB) && !(item is FC) && !(item is PlcType)) return State("notApplicable");
            try
            {
                var provider = item as IEngineeringServiceProvider;
                if (provider == null) return State("unavailable", "missingServiceProvider");
                var info = provider.GetService<LibraryTypeInstanceInfo>();
                if (info == null) return State("noService");
                // Openness Equals compares native identity; wrapper reference equality does not.
                var instance = info.LibraryTypeInstance;
                if (instance == null || !item.Equals(instance)) return State("unavailable", "instanceMismatch");
                var version = info.LibraryTypeVersion;
                if (version == null) return State("unavailable", "missingVersion");
                var type = version.TypeObject;
                if (type == null) return State("unavailable", "missingType");
                var typeGuid = type.Guid;
                var versionGuid = version.Guid;
                if (typeGuid == Guid.Empty || versionGuid == Guid.Empty) return State("unavailable", "emptyIdentity");
                result["typeGuid"] = typeGuid.ToString("D");
                result["versionGuid"] = versionGuid.ToString("D");
                result["dependencies"] = Dependencies(version, admitDependencyRead);
                return State("linked");
            }
            catch (EngineeringException) { return State("unavailable", "nativeReadFailed"); }
        }

        private static JObject Dependencies(LibraryTypeVersion version, Func<bool> admitRead)
        {
            var result = new JObject { ["schemaVersion"] = "plc-library-dependencies.v1",
                ["source"] = "LibraryTypeVersion.Dependencies", ["status"] = "unavailable",
                ["dependencyClosureVerified"] = false, ["nativeQualified"] = false };
            try
            {
                var captured = LibraryDependencyCapture.Read(version, admitRead);
                result["status"] = "observed";
                result["requiredVersionGuids"] = new JArray(captured.VersionGuids);
            }
            catch (BridgeException error) when (error.Code == "TraversalLimit" || error.Code == "DependencyUnresolved")
            { result["reason"] = error.Code == "TraversalLimit" ? "budgetExhausted" : "invalidOrUnboundedDependencies"; }
            catch (EngineeringException) { result["reason"] = "nativeReadFailed"; }
            return result;
        }
    }
}
