using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TiaMcp.Core;
using TiaMcp.Core.PhysicalRuntime;
using TiaMcp.Core.Security;
using TiaMcp.Host;

// Web API tickets (service data, profiling data, backup) and Alarms.Acknowledge against the pinned-TLS loopback fake of RuntimeWebSessionChecks.
// A fake only: nothing here is verified against a real PLC or PLCSIM.
internal static class RuntimeWebTicketChecks
{
    private static string Id() => Guid.NewGuid().ToString("N");
    private static readonly string[] AllMethods = ["Modules.DownloadServiceData", "PlcProgram.DownloadProfilingData", "Plc.CreateBackup", "Api.CloseTicket",
        "Plc.ReadOperatingMode", "Alarms.Browse", "Alarms.Acknowledge"];
    private static readonly string[] AllGrants = ["read_value", "download_service_data", "backup_plc", "acknowledge_alarms"];

    internal static async Task Run(string output, Action<bool, string> check)
    {
        string vaultName = Id(), vaultRoot = CredentialVault.UserRoot(vaultName);
        _ = new CredentialVault(vaultRoot); var vault = new CredentialVault(vaultRoot);
        await using var cpu = new RuntimeWebSessionChecks.FakeWebApi();
        await using var bare = new RuntimeWebSessionChecks.FakeWebApi(); // advertises none of the new methods and grants only read_value
        try
        {
            RuntimeLoginSelection Login(RuntimeWebSessionChecks.FakeWebApi fake)
            {
                var binding = PhysicalRuntimeSessions.Binding(fake.Endpoint, fake.Pin, "reader", "local");
                return new(fake.Endpoint, fake.Pin, "reader", "local", vaultName, vault.Create(binding, Encoding.UTF8.GetBytes("\"fixture-web-password\"")).Reference);
            }
            cpu.Extra = AllMethods; cpu.Grants = AllGrants;
            await using var sessions = new PhysicalRuntimeSessions(new HostOptions("", output, null, 5, false, "", null));
            string a = Id(), b = Id();
            check((await sessions.Connect(a, Login(cpu), default)).State == "connected" && (await sessions.Connect(b, Login(bare), default)).State == "connected", "Ticket sessions connect");
            static JsonElement J(object value) => JsonSerializer.SerializeToElement(value);
            async Task<BridgeException?> Refused(Func<Task> action) { try { await action(); return null; } catch (BridgeException e) { return e; } }

            // Service data: one ticket, redeemed once with the token, stored by content, closed.
            cpu.TicketBody = Encoding.UTF8.GetBytes("service-data-one");
            var service = J(await sessions.Download(a, "serviceData", 32, default));
            string path = service.GetProperty("artifact").GetProperty("path").GetString()!, sha = service.GetProperty("artifact").GetProperty("sha256").GetString()!;
            string full = Path.Combine(output, path);
            check(service.GetProperty("state").GetString() == "downloaded" && service.GetProperty("ticketClosed").GetBoolean() && path.StartsWith("runtime-web-downloads/web-serviceData-", StringComparison.Ordinal) &&
                File.Exists(full) && File.ReadAllBytes(full).SequenceEqual(cpu.TicketBody) && sha == Convert.ToHexStringLower(SHA256.HashData(cpu.TicketBody)) && service.GetProperty("artifact").GetProperty("bytes").GetInt64() == cpu.TicketBody.Length,
                "Service data is downloaded, stored by its SHA-256 and the ticket is closed");
            check(cpu.TicketGets.Count == 1 && cpu.TicketGets.Single() == "GET /api/ticket?id=VGlja2V0LVNlcnZpY2U HTTP/1.1" && cpu.TicketHeaders.Single().Contains("X-Auth-Token: fixture-token-", StringComparison.OrdinalIgnoreCase) &&
                cpu.ClosedTickets.Single() == "VGlja2V0LVNlcnZpY2U" && !service.GetRawText().Contains("VGlja2V0", StringComparison.Ordinal),
                "The ticket is redeemed once on the pinned endpoint, closed with Api.CloseTicket and never returned");
            var again = J(await sessions.Download(a, "serviceData", 32, default));
            check(again.GetProperty("artifact").GetProperty("path").GetString() == path && Directory.GetFiles(Path.Combine(output, "runtime-web-downloads"), "*.bin").Length == 1, "The same bytes are stored once");

            // Profiling data takes no parameters; a service-data request needs a hardware ID and sends nothing without one.
            cpu.TicketBody = Encoding.UTF8.GetBytes("profiling-one");
            check(J(await sessions.Download(a, "profiling", null, default)).GetProperty("state").GetString() == "downloaded", "Profiling data is downloaded");
            int sent = cpu.Calls.Count;
            check((await Refused(() => sessions.Download(a, "serviceData", null, default)))?.Code == "RuntimeTicketSelectionInvalid" &&
                (await Refused(() => sessions.Download(a, "backup", null, default)))?.Code == "RuntimeTicketKindUnsupported" && cpu.Calls.Count == sent,
                "A bad ticket selection is refused before any request");

            // A method the CPU did not advertise returns unsupported with nothing sent; a missing permission refuses before the ticket request.
            bare.Grants = ["read_value"];
            var unsupported = J(await sessions.Download(b, "profiling", null, default));
            check(unsupported.GetProperty("state").GetString() == "unsupported" && !unsupported.GetProperty("dispatched").GetBoolean() && !bare.Calls.Contains("PlcProgram.DownloadProfilingData"),
                "An unadvertised download sends no ticket request");
            check((await Refused(() => sessions.Download(b, "serviceData", 1, default)))?.Code == "RuntimePermissionDenied" && !bare.Calls.Contains("Modules.DownloadServiceData"),
                "A missing download_service_data permission refuses before the ticket request");

            // A failed redemption still closes the ticket and stores nothing.
            int stored = Directory.GetFiles(Path.Combine(output, "runtime-web-downloads"), "*.bin").Length; int closes = cpu.ClosedTickets.Count;
            cpu.TicketMode = "error";
            var failed = J(await sessions.Download(a, "profiling", null, default));
            check(failed.GetProperty("state").GetString() == "failed" && failed.GetProperty("errorCode").GetString() == "RuntimeHttpFailure" && failed.GetProperty("ticketClosed").GetBoolean() &&
                cpu.ClosedTickets.Count == closes + 1 && Directory.GetFiles(Path.Combine(output, "runtime-web-downloads"), "*.bin").Length == stored && !Directory.GetFiles(Path.Combine(output, "runtime-web-downloads"), "*.tmp").Any(),
                "A failed download closes its ticket and leaves no file");
            cpu.TicketMode = "redirect";
            var redirected = J(await sessions.Download(a, "profiling", null, default));
            check(redirected.GetProperty("errorCode").GetString() == "RuntimeHttpFailure" && redirected.GetProperty("ticketClosed").GetBoolean(), "A redirect on the ticket endpoint is refused, not followed");
            cpu.TicketMode = "ok";

            // Retention: eleven different downloads leave at most eight files, the newest among them.
            string last = "";
            for (int i = 0; i < 11; i++)
            {
                cpu.TicketBody = Encoding.UTF8.GetBytes("retention-" + i);
                last = J(await sessions.Download(a, "profiling", null, default)).GetProperty("artifact").GetProperty("path").GetString()!;
                await Task.Delay(20); // distinct modification times
            }
            check(Directory.GetFiles(Path.Combine(output, "runtime-web-downloads"), "*.bin").Length <= RuntimeWebDownloadsMaximumFiles && File.Exists(Path.Combine(output, last)), "Downloads keep at most eight files and the newest survives");

            // Backup: only in an observed STOP; the CPU is never switched; the ticket is closed.
            cpu.Mode = "run"; cpu.TicketBody = Encoding.UTF8.GetBytes("backup-one"); int tickets = cpu.Calls.Count(c => c == "Plc.CreateBackup");
            var running = J(await sessions.Backup(a, default));
            check(running.GetProperty("state").GetString() == "preconditionMismatch" && !running.GetProperty("dispatched").GetBoolean() && cpu.Calls.Count(c => c == "Plc.CreateBackup") == tickets &&
                !cpu.Calls.Contains("Plc.RequestChangeOperatingMode"), "A backup in RUN sends no ticket request and never changes the mode");
            cpu.Mode = "stop";
            var backup = J(await sessions.Backup(a, default));
            check(backup.GetProperty("state").GetString() == "downloaded" && backup.GetProperty("ticketClosed").GetBoolean() && cpu.TicketGets.Last().Contains("VGlja2V0LUJhY2t1cA%3D%3D", StringComparison.Ordinal) &&
                backup.GetProperty("artifact").GetProperty("path").GetString()!.StartsWith("runtime-web-downloads/web-backup-", StringComparison.Ordinal), "A backup in STOP is downloaded, its padded ticket is escaped in the URL and closed");
            check((await Refused(() => sessions.Backup(b, default)))?.Code == "RuntimePermissionDenied" && !bare.Calls.Contains("Plc.CreateBackup"), "A missing backup_plc permission refuses before anything is sent");

            // Alarm acknowledgement: only a currently not_acknowledged alarm, sent once, read back.
            int acks = cpu.Calls.Count(c => c == "Alarms.Acknowledge");
            var acknowledged = J(await sessions.AcknowledgeAlarm(a, "en-US", "121651651651", default));
            check(acknowledged.GetProperty("state").GetString() == "acknowledged" && acknowledged.GetProperty("readbackMatches").GetBoolean() && cpu.Calls.Count(c => c == "Alarms.Acknowledge") == acks + 1,
                "An unacknowledged alarm is acknowledged once and read back");
            var already = J(await sessions.AcknowledgeAlarm(a, "en-US", "121651651651", default));
            check(already.GetProperty("state").GetString() == "alreadyAcknowledged" && !already.GetProperty("dispatched").GetBoolean() && cpu.Calls.Count(c => c == "Alarms.Acknowledge") == acks + 1,
                "An acknowledged alarm is not acknowledged again");
            check((await Refused(() => sessions.AcknowledgeAlarm(a, "en-US", "1\", \"x\":2", default)))?.Code == "RuntimeAlarmIdInvalid" &&
                (await Refused(() => sessions.AcknowledgeAlarm(b, "en-US", "12", default)))?.Code == "RuntimePermissionDenied" && !bare.Calls.Contains("Alarms.Acknowledge"),
                "A bad alarm id or a missing acknowledge_alarms permission sends no acknowledgement");

            // The client enforces the cap while reading, whatever Content-Length says, and leaves no file.
            var context = WebRuntimeProtocol.Context(cpu.Endpoint, Id(), Id(), cpu.Pin);
            using var client = new WebRuntimeClient(context);
            await client.ProbeAsync(default); await client.LoginAsync(context.ConnectionId, "reader", "local", Encoding.UTF8.GetBytes("\"pw\""), default);
            cpu.TicketBody = new byte[100_000];
            string temp = Path.Combine(output, Id() + ".tmp");
            cpu.TicketMode = "noLength";
            check((await Refused(() => client.DownloadTicketAsync("VGlja2V0", 4096, temp, default)))?.Code == "RuntimeDownloadLimit" && !File.Exists(temp), "A body with no Content-Length is cut at the cap and deleted");
            cpu.TicketMode = "ok";
            check((await Refused(() => client.DownloadTicketAsync("VGlja2V0", 4096, temp, default)))?.Code == "RuntimeDownloadLimit" && !File.Exists(temp), "A Content-Length above the cap is refused");
            var okDownload = await client.DownloadTicketAsync("VGlja2V0", 200_000, temp, default);
            check(okDownload.Bytes == 100_000 && File.ReadAllBytes(temp).Length == 100_000 && okDownload.Sha256 == Convert.ToHexStringLower(SHA256.HashData(cpu.TicketBody)), "A body within the cap is hashed and written whole");
            File.Delete(temp);
            check((await Refused(() => client.DownloadTicketAsync("a&b", 4096, temp, default)))?.Code == "RuntimeResponseInvalid", "A ticket that is not URL-safe is never put in a URL");
        }
        finally { Directory.Delete(vaultRoot, true); }
    }

    private const int RuntimeWebDownloadsMaximumFiles = 8; // RuntimeWebDownloads.MaximumFiles (internal to the Host)
}
