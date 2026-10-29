using System;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TiaMcp.Simulation;

namespace TiaMcp.Simulator.V8
{
    /// <summary>Pure protocol/value and current-user DPAPI checks. Never loads or starts Siemens runtime.</summary>
    internal static class SimulatorSelfTests
    {
        internal static int Run(string root)
        {
            SimulationTicket.VerifyPlainDirectory(root);
            string directory = Path.Combine(Path.GetFullPath(root), "simulator-selftest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var checks = new JArray();
            Check(checks, "uint64 retains least significant bit", () =>
                Require(!SimulationAssertions.Compare("uint64", new JValue(ulong.MaxValue), new JValue(ulong.MaxValue - 1), "eq", 0)));
            Check(checks, "uint64 JSON BigInteger input preserved", () =>
                Require(SimulationAssertions.Compare("uint64", new JValue(ulong.MaxValue), JToken.Parse("18446744073709551615"), "eq", 0)));
            Check(checks, "int64 negative bound", () =>
                Require(SimulationAssertions.Compare("int64", new JValue(long.MinValue), new JValue(long.MinValue + 1), "lt", 0)));
            Check(checks, "large double does not overflow decimal", () =>
                Require(SimulationAssertions.Compare("double", new JValue(1e300), new JValue(1e299), "gt", 0)));
            Check(checks, "float equality tolerance", () =>
                Require(SimulationAssertions.Compare("float", new JValue(0.10000000149011612), new JValue(0.1), "eq", 0.00001)));
            Check(checks, "owned write precondition and readback compare at Real precision", () =>
                Require(SimulationAssertions.Same("float", new JValue((double)0.1f), new JValue(0.1)) && !SimulationAssertions.Same("double", new JValue((double)0.1f), new JValue(0.1))
                    && !SimulationAssertions.Same("float", new JValue(0.5), new JValue(0.25))));
            Check(checks, "NaN cannot pass comparison", () => Throws(() => SimulationAssertions.Compare("double", new JValue(double.NaN), new JValue(1.0), "eq", 0)));
            Check(checks, "boolean relational comparison rejected", () => Throws(() => SimulationAssertions.Compare("bool", new JValue(true), new JValue(false), "gt", 0)));
            Check(checks, "negative tolerance rejected", () => Throws(() => SimulationAssertions.Compare("int32", new JValue(2), new JValue(1), "eq", -1)));
            Check(checks, "large integer tolerance does not overflow", () =>
                Require(SimulationAssertions.Compare("uint64", new JValue(ulong.MaxValue), new JValue(0UL), "eq", 1e100)));
            Check(checks, "configuration-password warning identifies protection credential and target", () =>
            {
                var error = SimulationPowerOnFailure.Create("WarningPasswordForProtectionError", new JObject { ["id"] = 0, ["name"] = "TiaMcp_selftest" });
                var request = error.Details["credentialRequest"];
                Require(error.Code == "SimulationConfigurationPasswordRequired" && error.Details.Value<string>("runtimeCode") == "WarningPasswordForProtectionError");
                Require(request.Value<string>("kind") == "cpuConfigurationProtectionPassword" && request.Value<bool>("askAgentToPrompt"));
                Require(request["target"].Value<int>("instanceId") == 0 && request["target"].Value<string>("instanceName") == "TiaMcp_selftest");
            });
            Check(checks, "configuration-password guidance separates account login and plaintext handling", () =>
            {
                var request = SimulationPowerOnFailure.Create("WarningPasswordForProtectionError", new JObject { ["id"] = 0, ["name"] = "TiaMcp_selftest" }).Details["credentialRequest"];
                var account = request["separateUserAuthentication"];
                Require(account.Value<string>("kind") == "usernameAndPassword" && !account.Value<bool>("assumeSharedOrDefaultCredentials"));
                Require(!request.Value<bool>("credentialSubmissionImplemented") && !request.Value<bool>("retainPlaintext"));
            });
            Check(checks, "trial and other power-on failures remain rejected without credential prompts", () =>
            {
                foreach (string status in new[] { "WarningTrialModeActive", "WarningRunningOnTiaPortalTestSuite", "ErrorLoadingDll", "UnknownResult" })
                {
                    var error = SimulationPowerOnFailure.Create(status, new JObject { ["id"] = 0, ["name"] = "TiaMcp_selftest" });
                    Require(error.Code == "SimulationPowerOnRejected" && error.Details["credentialRequest"] == null);
                    Require(error.Message == "Power-on did not return a normal licensed result: " + status);
                }
            });
            Check(checks, "complete frame accepted", () => Require(Program.ReadLine(new StringReader("{}\n")) == "{}"));
            Check(checks, "clean EOF accepted", () => Require(Program.ReadLine(new StringReader("")) == null));
            Check(checks, "incomplete request never dispatched", () => Throws(() => Program.ReadLine(new StringReader("{}"))));
            Check(checks, "oversized request rejected", () => Throws(() => Program.ReadLine(new StringReader(new string('a', 4 * 1024 * 1024 + 1) + "\n"))));
            Check(checks, "UTF8 byte limit enforced", () => Throws(() => Program.ReadLine(new StringReader(new string('\u00e4', 3 * 1024 * 1024) + "\n"))));
            Check(checks, "relative storage rejected", () => Throws(() => SimulationTicket.VerifyPlainDirectory("relative")));
            Check(checks, "drive-relative storage rejected", () => Throws(() => SimulationTicket.VerifyPlainDirectory("C:relative")));
            Check(checks, "UNC storage rejected", () => Throws(() => SimulationTicket.VerifyPlainDirectory(@"\\host\share\instance")));
            Check(checks, "DPAPI ticket roundtrip with ownership marker", () =>
            {
                string storage = Path.Combine(directory, "roundtrip"); Directory.CreateDirectory(storage);
                var marker = SimulationTicket.Create(1, "TiaMcp_selftest", storage);
                var actual = SimulationTicket.Decode(SimulationTicket.Encode(marker));
                Require(actual.Value<int>("instanceId") == 1 && actual.Value<string>("storagePath") == storage);
                Require(actual["communicationInterface"] == null && SimulationTicket.Interface(actual) == "Softbus");
            });
            Check(checks, "TCP/IP ticket names its interface", () =>
            {
                string storage = Path.Combine(directory, "tcpip"); Directory.CreateDirectory(storage);
                var actual = SimulationTicket.Decode(SimulationTicket.Encode(SimulationTicket.Create(5, "TiaMcp_selftest", storage, "TCPIP")));
                Require(SimulationTicket.Interface(actual) == "TCPIP");
            });
            Check(checks, "ownership marker mismatch rejected", () =>
            {
                string storage = Path.Combine(directory, "mismatch"); Directory.CreateDirectory(storage);
                var marker = SimulationTicket.Create(2, "TiaMcp_selftest", storage); string ticket = SimulationTicket.Encode(marker);
                marker["instanceName"] = "changed"; File.WriteAllText(Path.Combine(storage, SimulationTicket.MarkerName), marker.ToString());
                Throws(() => SimulationTicket.Decode(ticket));
            });
            Check(checks, "expired ticket rejected", () =>
            {
                string storage = Path.Combine(directory, "expired"); Directory.CreateDirectory(storage);
                var marker = SimulationTicket.Create(3, "TiaMcp_selftest", storage); marker["expiresAtUtc"] = DateTime.UtcNow.AddMinutes(-1).ToString("O");
                File.WriteAllText(Path.Combine(storage, SimulationTicket.MarkerName), marker.ToString());
                Throws(() => SimulationTicket.Decode(SimulationTicket.Encode(marker)));
            });
            Check(checks, "tampered ciphertext rejected", () =>
            {
                string storage = Path.Combine(directory, "tampered"); Directory.CreateDirectory(storage);
                var marker = SimulationTicket.Create(4, "TiaMcp_selftest", storage);
                byte[] ciphertext = Convert.FromBase64String(SimulationTicket.Encode(marker)); ciphertext[ciphertext.Length - 1] ^= 1;
                Throws(() => SimulationTicket.Decode(Convert.ToBase64String(ciphertext)));
            });
            Check(checks, "creation intent is durable before native identity exists", () =>
            {
                string id = Guid.NewGuid().ToString("N");
                var receipt = SimulationCreationJournal.Begin(directory, id, "owned", directory, "CPU1511");
                var read = SimulationCreationJournal.Read(directory, id);
                Require(read.Value<string>("stage") == "registrationIntent" && read["instanceId"] == null);
                Require(!SimulationCreationJournal.WorkerExited(read));
                Require(read.Value<string>("workerStartedAtUtc") == receipt.Value<string>("workerStartedAtUtc"));
                Throws(() => SimulationCreationJournal.Begin(directory, id, "other", directory, "CPU1511"));
            });
            Check(checks, "receipt adoption requires terminal creation, native identity, marker and exited worker", () =>
            {
                string storage = Path.Combine(directory, "creation"); Directory.CreateDirectory(storage);
                string id = Guid.NewGuid().ToString("N");
                var receipt = SimulationCreationJournal.Begin(directory, id, "owned", storage, "CPU1511");
                SimulationTicket.Create(7, "owned", storage);
                receipt["instanceId"] = 7;
                receipt["markerSha256"] = SimulationCreationJournal.MarkerHash(storage);
                Throws(() => SimulationCreationJournal.VerifyAdoption(receipt, 7, "owned", storage, "CPU1511", true));
                receipt["stage"] = "ready"; SimulationCreationJournal.Save(directory, receipt);
                receipt = SimulationCreationJournal.Read(directory, id);
                SimulationCreationJournal.VerifyAdoption(receipt, 7, "owned", storage, "CPU1511", true);
                Throws(() => SimulationCreationJournal.VerifyAdoption(receipt, 7, "owned", storage, "CPU1511", false));
                Throws(() => SimulationCreationJournal.VerifyAdoption(receipt, 8, "owned", storage, "CPU1511", true));
                Throws(() => SimulationCreationJournal.VerifyAdoption(receipt, 7, "other", storage, "CPU1511", true));
                Throws(() => SimulationCreationJournal.VerifyAdoption(receipt, 7, "owned", storage, "CPU1513", true));
                File.AppendAllText(Path.Combine(storage, SimulationTicket.MarkerName), " ");
                Throws(() => SimulationCreationJournal.VerifyAdoption(receipt, 7, "owned", storage, "CPU1511", true));
            });
            Check(checks, "tampered creation receipt and alternate ID are rejected", () =>
            {
                string id = Guid.NewGuid().ToString("N");
                SimulationCreationJournal.Begin(directory, id, "owned", directory, "CPU1511");
                string path = SimulationCreationJournal.PathFor(directory, id);
                string otherId = Guid.NewGuid().ToString("N");
                File.Copy(path, SimulationCreationJournal.PathFor(directory, otherId));
                Throws(() => SimulationCreationJournal.Read(directory, otherId));
                var json = JObject.Parse(File.ReadAllText(path));
                byte[] bytes = Convert.FromBase64String(json.Value<string>("protectedReceipt")); bytes[bytes.Length - 1] ^= 1;
                json["protectedReceipt"] = Convert.ToBase64String(bytes); File.WriteAllText(path, json.ToString());
                Throws(() => SimulationCreationJournal.Read(directory, id));
                Throws(() => SimulationCreationJournal.PathFor(directory, "../other"));
            });
            var report = new JObject { ["passed"] = checks.All(check => check.Value<bool>("passed")), ["checks"] = checks,
                ["count"] = checks.Count, ["artifactDirectory"] = directory, ["siemensApiInitialized"] = false };
            File.WriteAllText(Path.Combine(directory, "report.json"), report.ToString(Formatting.Indented), new UTF8Encoding(false));
            Console.WriteLine(report.ToString(Formatting.Indented));
            return report.Value<bool>("passed") ? 0 : 1;
        }

        private static void Check(JArray checks, string name, Action action)
        {
            var check = new JObject { ["name"] = name };
            try { action(); check["passed"] = true; }
            catch (Exception ex) { check["passed"] = false; check["error"] = ex.Message; }
            checks.Add(check);
        }
        private static void Require(bool predicate) { if (!predicate) throw new InvalidOperationException("Assertion failed."); }
        private static void Throws(Action action)
        {
            try { action(); }
            catch { return; }
            throw new InvalidOperationException("Expected rejection did not occur.");
        }
    }
}
