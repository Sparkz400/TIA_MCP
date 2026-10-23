using System;
using System.Collections.Generic;
using System.IO;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        private static readonly byte[] ScratchCredentialEntropy = Encoding.UTF8.GetBytes("TiaMcp.V21.ScratchCpuCredential.v1");

        private void EnsureCurrentScratchProject(Project project)
        {
            if (!_ownsPortal || _scratchProject == null || !project.Equals(_scratchProject))
                throw new BridgeException("ScratchIsolationRequired", "Generated security configuration is restricted to newly created CPUs in this worker's current scratch project.");
        }

        /// <summary>Called only immediately after device creation, never on discovered existing CPUs.</summary>
        private JObject ConfigureNewScratchCpu(Project project, Device newlyCreatedDevice, string artifactDirectory)
        {
            EnsureCurrentScratchProject(project);
            var configured = new JArray();
            foreach (var cpu in ScratchDeviceItems(newlyCreatedDevice.DeviceItems, 0))
            {
                var service = cpu.GetService<PlcMasterSecretConfigurator>();
                if (service == null) continue;
                string before = service.MasterSecretConfiguration.ToString();
                if (before == "WithPassword" || before == "WithPasswordAllDataProtection")
                {
                    configured.Add(new JObject { ["cpuName"] = cpu.Name, ["configuration"] = before,
                        ["action"] = "Existing protected configuration retained; no password changed.",
                        ["runtimeUser"] = ConfigureNewScratchRuntimeUser(project, cpu, artifactDirectory) });
                    continue;
                }
                string credentialDirectory = Path.Combine(artifactDirectory, "scratch-credentials");
                Directory.CreateDirectory(credentialDirectory);
                string credentialId = Guid.NewGuid().ToString("N");
                string protectedPath = Path.Combine(credentialDirectory, credentialId + ".dpapi");
                byte[] random = new byte[32];
                byte[] clear = null;
                byte[] encrypted = null;
                char[] characters = null;
                try
                {
                    using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(random);
                    // Include each common complexity class; the random suffix supplies entropy.
                    characters = new char[4 + ((random.Length + 2) / 3) * 4];
                    characters[0] = 'T'; characters[1] = 'm'; characters[2] = '9'; characters[3] = '!';
                    Convert.ToBase64CharArray(random, 0, random.Length, characters, 4);
                    clear = Encoding.UTF8.GetBytes(characters);
                    encrypted = ProtectedData.Protect(clear, ScratchCredentialEntropy, DataProtectionScope.CurrentUser);
                    using (var stream = new FileStream(protectedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) stream.Write(encrypted, 0, encrypted.Length);
                    var metadata = new JObject { ["format"] = "Windows DPAPI CurrentUser", ["credentialId"] = credentialId,
                        ["projectPath"] = project.Path.FullName, ["cpuName"] = cpu.Name, ["typeIdentifier"] = cpu.TypeIdentifier,
                        ["protectedPath"] = protectedPath, ["purpose"] = "Generated confidential configuration protection for a new isolated scratch CPU", ["state"] = "prepared" };
                    string metadataPath = Path.Combine(credentialDirectory, credentialId + ".json");
                    File.WriteAllText(metadataPath, metadata.ToString(Formatting.Indented), new UTF8Encoding(false));
                    using (var password = new SecureString())
                    {
                        foreach (char character in characters) password.AppendChar(character);
                        password.MakeReadOnly();
                        service.Protect(password);
                    }
                    metadata["state"] = "configured";
                    metadata["configuration"] = service.MasterSecretConfiguration.ToString();
                    File.WriteAllText(metadataPath, metadata.ToString(Formatting.Indented), new UTF8Encoding(false));
                    configured.Add(new JObject { ["cpuName"] = cpu.Name, ["configuration"] = service.MasterSecretConfiguration.ToString(),
                        ["credentialMetadataPath"] = metadataPath, ["credentialStorage"] = "Windows DPAPI CurrentUser", ["secretReturned"] = false,
                        ["runtimeUser"] = ConfigureNewScratchRuntimeUser(project, cpu, artifactDirectory) });
                }
                finally
                {
                    Array.Clear(random, 0, random.Length);
                    if (clear != null) Array.Clear(clear, 0, clear.Length);
                    if (encrypted != null) Array.Clear(encrypted, 0, encrypted.Length);
                    if (characters != null) Array.Clear(characters, 0, characters.Length);
                }
            }
            return new JObject { ["cpus"] = configured, ["scope"] = "Only newly created CPUs in the current worker-owned scratch project", ["existingProtectionDisabled"] = false };
        }

        private static IEnumerable<DeviceItem> ScratchDeviceItems(IEnumerable<DeviceItem> items, int depth)
        {
            if (depth > 32) throw new BridgeException("TraversalLimit", "Scratch device hierarchy exceeds its expected depth.");
            foreach (var item in items)
            {
                yield return item;
                foreach (var child in ScratchDeviceItems(item.DeviceItems, depth + 1)) yield return child;
            }
        }
    }
}
