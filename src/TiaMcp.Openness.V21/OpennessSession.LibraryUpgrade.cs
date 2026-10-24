using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.Library;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        /// <summary>
        /// Native V21 upgrade of an owned working copy. Observed on V21: the conversion completes and saves
        /// "&lt;name&gt;_V21", then returning the upgraded library throws "Could not load type 'Dummy'". Only when
        /// exactly one new upgraded library exists and its conversion log records success is it opened; any
        /// other failure is rethrown unchanged.
        /// </summary>
        private GlobalLibrary UpgradeLibrary(bool archive, string snapshotPath, string directory, string fileName, JObject result)
        {
            try
            {
                return archive
                    ? _portal.GlobalLibraries.RetrieveWithUpgrade(new FileInfo(snapshotPath), new DirectoryInfo(directory), OpenMode.ReadWrite)
                    : _portal.GlobalLibraries.OpenWithUpgrade(new FileInfo(Path.Combine(directory, fileName)));
            }
            catch (Exception error)
            {
                var upgraded = Directory.GetFiles(directory, "*_V21.al21", SearchOption.AllDirectories);
                if (upgraded.Length != 1) throw;
                string logDirectory = Path.Combine(Path.GetDirectoryName(upgraded[0]), "Logs");
                var logs = Directory.Exists(logDirectory) ? Directory.GetFiles(logDirectory, "ConversionLog_*_to_21.0.0.0.xml") : new string[0];
                if (logs.Length != 1 || !File.ReadAllText(logs[0]).Contains("ConversionInfoTabLibrarySucceeded")) throw;
                GlobalLibrary opened = null;
                try { opened = _portal.GlobalLibraries.FirstOrDefault(x => string.Equals(x.Path?.FullName, upgraded[0], StringComparison.OrdinalIgnoreCase)); }
                catch (Exception) { } // Enumerating can hit the same type-load failure; opening by path follows.
                result["upgradeReturnFailure"] = new JObject { ["nativeType"] = error.GetType().FullName, ["nativeMessage"] = error.Message, ["upgradedLibrary"] = upgraded[0],
                    ["conversionLog"] = FileArtifact(new FileInfo(logs[0])), ["openedBy"] = opened != null ? "alreadyOpenInstance" : "openByPath",
                    ["handling"] = "The conversion log records a successful upgrade; the saved V21 library was opened explicitly. Readback, archive and reopen remain the caller's verification." };
                try { return opened ?? _portal.GlobalLibraries.Open(new FileInfo(upgraded[0]), OpenMode.ReadWrite); }
                catch (Exception second) { throw new BridgeException("NativeLibraryError", error.Message + " Opening the saved V21 library also failed: " + second.Message, (JObject)result["upgradeReturnFailure"].DeepClone()); }
            }
        }
    }
}
