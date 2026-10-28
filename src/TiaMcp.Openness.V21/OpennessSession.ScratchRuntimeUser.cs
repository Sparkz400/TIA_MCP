using System;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.Umac;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        private static readonly byte[] ScratchRuntimeUserEntropy = Encoding.UTF8.GetBytes("TiaMcp.V21.ScratchRuntimeUser.v1");

        /// <summary>Only invoked immediately after creating a CPU in this worker's new scratch project.</summary>
        private JObject ConfigureNewScratchRuntimeUser(Project project, DeviceItem cpu, string artifactDirectory, bool failsafeRuntimeAccess = false)
        {
            EnsureCurrentScratchProject(project);
            IEngineeringObject owner = cpu;
            for (int depth = 0; owner is DeviceItem && depth < 32; depth++) owner = ((DeviceItem)owner).Parent;
            var device = owner as Device;
            if (device == null) throw new BridgeException("ScratchRuntimeUserUnavailable", "Cannot identify the new scratch CPU's owning device.");
            var umacDevice = device.GetService<UmacDevice>();
            // Older CPU firmware can have no UMAC device service; its own compiler
            // determines whether its unchanged access configuration is sufficient.
            if (umacDevice == null) return new JObject { ["cpuName"] = cpu.Name, ["supported"] = false,
                ["reason"] = "This new device exposes no UmacDevice service; existing access settings were retained." };
            var umac = project.GetService<UmacConfigurator>() ?? throw new BridgeException("ScratchRuntimeUserUnavailable", "Project does not expose UmacConfigurator.");
            var available = umacDevice.AvailableDeviceFunctionRights.ToArray();
            // An F-CPU compiles only when a local user holds the fail-safe right; the caller opts in per new scratch device.
            var fullAccess = available.Where(right => failsafeRuntimeAccess ? IsScratchFailsafeRight(right) : IsScratchFullAccessRight(right)).Take(2).ToArray();
            if (fullAccess.Length != 1)
                throw new BridgeException("ScratchRuntimeRightUnresolved", "A unique " + (failsafeRuntimeAccess ? "Full access including fail-safe" : "standard Full access") + " runtime right was not found; no user or role was created.",
                    new JObject { ["cpuName"] = cpu.Name, ["availableRights"] = new JArray(available.Select(right => new JObject {
                        ["identifier"] = right.Identifier, ["name"] = right.Name, ["group"] = right.Group })) });

            string suffix = Guid.NewGuid().ToString("N").Substring(0, 12);
            string userName = "TiaMcpTest_" + suffix;
            string roleName = "TiaMcpRuntime_" + suffix;
            string directory = Path.Combine(artifactDirectory, "scratch-credentials");
            Directory.CreateDirectory(directory);
            string credentialId = Guid.NewGuid().ToString("N");
            string protectedPath = Path.Combine(directory, credentialId + ".dpapi");
            string metadataPath = Path.Combine(directory, credentialId + ".json");
            var metadata = new JObject { ["format"] = "Windows DPAPI CurrentUser", ["credentialId"] = credentialId,
                ["purpose"] = "Generated password for one new isolated scratch CPU runtime user", ["projectPath"] = project.Path.FullName,
                ["deviceName"] = device.Name, ["cpuName"] = cpu.Name, ["username"] = userName, ["roleName"] = roleName,
                ["functionRightIdentifier"] = fullAccess[0].Identifier, ["functionRightName"] = fullAccess[0].Name,
                ["protectedPath"] = protectedPath, ["state"] = "prepared", ["anonymousAccessChanged"] = false,
                ["existingProtectionDisabled"] = false, ["secretReturned"] = false };
            byte[] random = new byte[32]; byte[] clear = null; byte[] encrypted = null; char[] characters = null;
            try
            {
                using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(random);
                characters = new char[4 + ((random.Length + 2) / 3) * 4];
                characters[0] = 'T'; characters[1] = 'm'; characters[2] = '9'; characters[3] = '!';
                Convert.ToBase64CharArray(random, 0, random.Length, characters, 4);
                clear = Encoding.UTF8.GetBytes(characters);
                encrypted = ProtectedData.Protect(clear, ScratchRuntimeUserEntropy, DataProtectionScope.CurrentUser);
                using (var stream = new FileStream(protectedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) stream.Write(encrypted, 0, encrypted.Length);
                File.WriteAllText(metadataPath, metadata.ToString(Formatting.Indented), new UTF8Encoding(false));
                ProjectUser user;
                using (var password = new SecureString())
                {
                    foreach (char character in characters) password.AppendChar(character);
                    password.MakeReadOnly();
                    user = umac.ProjectUsers.Create(userName, password);
                }
                metadata["state"] = "user-created";
                File.WriteAllText(metadataPath, metadata.ToString(Formatting.Indented), new UTF8Encoding(false));
                user.ProjectUserAuthenticationType = AuthenticationType.Password;
                user.Comment = "Generated local runtime user for an isolated TiaMcp scratch CPU; password stored using Windows CurrentUser DPAPI.";
                var role = umac.CustomRoles.Create(roleName, (failsafeRuntimeAccess ? "Full access including fail-safe" : "Full access") + " to one newly created isolated scratch CPU only.");
                role.AssignDeviceFunctionRight(umacDevice, fullAccess[0]);
                user.Roles.Add(role);
                if (!user.IsActive) user.Activate();
                if (!user.IsActive || !role.GetAssignedDeviceFunctionRights(umacDevice).Any(right => right.Identifier == fullAccess[0].Identifier) ||
                    !user.Roles.Any(assigned => assigned.Identifier == role.Identifier))
                    throw new BridgeException("ScratchRuntimeUserVerificationFailed", "The new runtime user, role or device right assignment was not observed after configuration.");
                metadata["state"] = "configured";
                metadata["roleIdentifier"] = role.Identifier;
                metadata["isActive"] = user.IsActive;
                metadata["authenticationType"] = user.ProjectUserAuthenticationType.ToString();
                File.WriteAllText(metadataPath, metadata.ToString(Formatting.Indented), new UTF8Encoding(false));
                var result = (JObject)metadata.DeepClone();
                result["credentialMetadataPath"] = metadataPath;
                result["supported"] = true;
                return result;
            }
            catch (Exception ex)
            {
                metadata["failureType"] = ex.GetType().FullName;
                // Preserve partial state and credentials for diagnosis; do not delete
                // users/roles or grant anonymous rights as a fallback.
                try { File.WriteAllText(metadataPath, metadata.ToString(Formatting.Indented), new UTF8Encoding(false)); } catch { }
                throw;
            }
            finally
            {
                Array.Clear(random, 0, random.Length);
                if (clear != null) Array.Clear(clear, 0, clear.Length);
                if (encrypted != null) Array.Clear(encrypted, 0, encrypted.Length);
                if (characters != null) Array.Clear(characters, 0, characters.Length);
            }
        }

        private static bool IsScratchFailsafeRight(DeviceFunctionRight right)
        {
            string identifier = right.Identifier ?? "";
            return string.Equals(identifier.Split('.', '/', ':').Last(), "FullAccessIncludingFailsafe", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(identifier, "ProtectionLevelFullAccessIncludingFailsafe", StringComparison.Ordinal) ||
                string.Equals(right.Name, "Full access including fail-safe", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsScratchFullAccessRight(DeviceFunctionRight right)
        {
            // UI names are localized; stable identifiers are preferred when the
            // manufacturer names their final identifier component FullAccess.
            // Never match the separate full-access-including-failsafe right here.
            string identifier = right.Identifier ?? "";
            string last = identifier.Split('.', '/', ':').Last();
            return string.Equals(identifier, "ProtectionLevelFullAccess", StringComparison.Ordinal) ||
                string.Equals(last, "FullAccess", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(right.Name, "Full access", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(right.Name, "Vollzugriff", StringComparison.OrdinalIgnoreCase);
        }
    }
}
