using System.Security.Cryptography;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using TiaMcp.Openness.V21;
using EngineeringBridgeException = TiaMcp.Openness.V21.BridgeException;

namespace TiaMcp.Tests
{
    internal static class SimulationCheckpointTests
    {
        internal static async Task SavedStateAndCollisions()
        {
            using var f = new Fixture();
            var error = await Check.ThrowsAsync<EngineeringBridgeException>(() => Task.Run(() => OpennessSession.CheckpointPathForTest(f.Root, true)));
            Check.Equal("UnsavedProject", error.Code); Check.Equal(0, Directory.GetFileSystemEntries(f.Root).Length);
            string archive = OpennessSession.CheckpointPathForTest(f.Root, false);
            foreach (bool directory in new[] { false, true })
                foreach (string collision in new[] { archive, Path.Combine(f.Root, "before-provision") })
                {
                    if (directory) Directory.CreateDirectory(collision); else File.WriteAllText(collision, "retained");
                    error = await Check.ThrowsAsync<EngineeringBridgeException>(() => Task.Run(() => OpennessSession.CheckpointPathForTest(f.Root, false)));
                    Check.Equal("SimulationProvisionStorageCollision", error.Code);
                    if (directory) Directory.Delete(collision); else { Check.Equal("retained", File.ReadAllText(collision)); File.Delete(collision); }
                }
        }

        internal static async Task ArchiveAndDurableReceiptPrecedeContinuation()
        {
            foreach (string fault in new[] { "none", "before", "archive", "empty", "dirty", "after" })
            {
                using var f = new Fixture();
                string archive = OpennessSession.CheckpointPathForTest(f.Root, false);
                var baseline = new JObject { ["projectArchivePath"] = archive, ["projectArchive"] = null };
                var events = new List<string>();
                var project = new Project();
                project.ArchiveAction = (directory, name, mode) => {
                    events.Add("archive"); Check.Equal(ProjectArchivationMode.Compressed, mode);
                    Check.Equal(archive, Path.Combine(directory.FullName, name));
                    if (fault == "archive") throw new IOException("native archive failed");
                    File.WriteAllText(archive, fault == "empty" ? "" : "complete project bytes");
                    if (fault == "dirty") project.IsModified = true;
                };
                void Capture()
                {
                    OpennessSession.CheckpointForTest(project, baseline, phase => {
                        events.Add(phase);
                        if (phase == "beforeProjectArchive") Check.Equal(JTokenType.Null, baseline["projectArchive"]!.Type);
                        else {
                            Check.Equal(new FileInfo(archive).Length, baseline["projectArchive"]!.Value<long>("bytes"));
                            Check.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(archive))), baseline["projectArchive"]!.Value<string>("sha256"));
                            Check.Equal("application/octet-stream", baseline["projectArchive"]!.Value<string>("mediaType"));
                        }
                        if ((fault == "before" && phase == "beforeProjectArchive") || (fault == "after" && phase == "afterProjectArchive"))
                            throw new IOException("durable receipt failed");
                    });
                    events.Add("continueToRuntime");
                }
                if (fault == "none") { Capture(); Check.Equal("beforeProjectArchive,archive,afterProjectArchive,continueToRuntime", string.Join(',', events)); }
                else
                {
                    await Check.ThrowsAsync<Exception>(() => Task.Run(Capture));
                    Check.Equal(false, events.Contains("continueToRuntime"));
                    if (fault == "before") Check.Equal(false, events.Contains("archive"));
                    if (fault != "after") Check.Equal(false, events.Contains("afterProjectArchive"));
                    if (fault == "after") Check.Equal(true, File.Exists(archive));
                }
            }
        }

        private sealed class Fixture : IDisposable
        {
            // Leave room for the native 143-character archive staging limit under CI's isolated temp root.
            internal readonly string Root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            internal Fixture() => Directory.CreateDirectory(Root);
            public void Dispose()
            {
                if (!Path.GetFullPath(Root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
                Directory.Delete(Root, true);
            }
        }
    }
}

// Only SDK and established artifact/ownership services are synthetic; the checkpoint sequence is production source.
namespace Siemens.Engineering { internal enum ProjectArchivationMode { Compressed } }
namespace TiaMcp.Simulation
{
    internal static class SimulationTicket
    {
        internal static void VerifyPlainDirectory(string path)
        {
            if (!Path.IsPathFullyQualified(path)) throw new InvalidOperationException();
            TiaMcp.Core.PackageStorage.EnsureNoLinks(path);
        }
    }
}
namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        internal static string CheckpointPathForTest(string path, bool modified) => SimulationProvisionCheckpointPath(path, modified);
        internal static void CheckpointForTest(Project project, JObject baseline, Action<string> checkpoint) => SimulationProvisionProjectCheckpoint(project, baseline, checkpoint);
        private static JObject FileArtifact(FileInfo file) => new() { ["path"] = file.FullName, ["bytes"] = file.Length,
            ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file.FullName))) };
    }
}
