#nullable disable
using System;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.Library.Types;
using TiaMcp.Model;

namespace TiaMcp.Openness.V21
{
    internal static class HmiLibraryLink
    {
        internal static JObject Read(IEngineeringObject item, LibraryTypeVersion expected = null)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));
            var result = new JObject { ["nativeObjectType"] = item.GetType().FullName,
                ["typeGuid"] = null, ["versionGuid"] = null, ["version"] = null,
                ["nativeLinkVerified"] = false, ["runtimeVerified"] = false };
            JObject Unavailable(string reason)
            {
                if (expected != null) throw new BridgeException("PreservationMismatch", "Native library-link readback is unavailable: " + reason);
                result["linkStatus"] = "unavailable";
                result["linkReadError"] = reason;
                return result;
            }
            try
            {
                var provider = item as IEngineeringServiceProvider;
                if (provider == null) return Unavailable("missingServiceProvider");
                var info = provider.GetService<LibraryTypeInstanceInfo>();
                if (info == null)
                {
                    if (expected != null) return Unavailable("missingService");
                    result["linkStatus"] = "noService";
                    return result;
                }
                // Equals compares native identity, including distinct wrappers for the same object.
                var instance = info.LibraryTypeInstance;
                if (instance == null || !item.Equals(instance)) return Unavailable("instanceMismatch");
                var version = info.LibraryTypeVersion;
                if (version == null) return Unavailable("missingVersion");
                var type = version.TypeObject;
                if (type == null) return Unavailable("missingType");
                var typeGuid = type.Guid;
                var versionGuid = version.Guid;
                var number = version.VersionNumber;
                if (typeGuid == Guid.Empty || versionGuid == Guid.Empty) return Unavailable("emptyIdentity");
                if (number == null) return Unavailable("missingVersionNumber");
                if (expected != null)
                {
                    var expectedType = expected.TypeObject;
                    if (expectedType == null || expectedType.Guid != typeGuid) return Unavailable("typeMismatch");
                    var expectedGuid = expected.Guid;
                    try { HmiRecipeRules.RequireExactVersion(expectedGuid, versionGuid); }
                    catch (ArgumentException ex) { throw new BridgeException("PreservationMismatch", ex.Message); }
                }
                // Capture once per role; this does not claim an atomic native snapshot.
                result["typeGuid"] = typeGuid.ToString("D");
                result["versionGuid"] = versionGuid.ToString("D");
                result["version"] = number.ToString();
                result["linkStatus"] = "linked";
                result["nativeLinkVerified"] = expected != null;
                return result;
            }
            catch (EngineeringException ex) { return Unavailable(ex.Message); }
        }

        internal static void RequireNoLink(IEngineeringObject item)
        {
            var observed = Read(item);
            if (observed.Value<string>("linkStatus") == "noService") return;
            if (observed.Value<string>("linkStatus") == "linked")
                throw new BridgeException("LibraryHandoffRequired", "This object has a native library type link; preserving import requires the library subsystem.", observed);
            throw new BridgeException("HmiPreservationUnavailable", "The native library link could not be established; preserving import cannot assume it is absent.", observed);
        }
    }
}
