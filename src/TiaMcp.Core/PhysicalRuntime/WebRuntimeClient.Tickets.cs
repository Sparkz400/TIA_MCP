using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;

namespace TiaMcp.Core.PhysicalRuntime;

// Ticket-based downloads and the alarm acknowledgement of the Siemens S7-1500 Web API (Web server function manual 11/2023, A5E03484625-AK:
// 5.3 Ticket mechanism p. 164, Api.CloseTicket p. 170, Alarms.Acknowledge p. 221). Not verified against a real PLC or PLCSIM.
public sealed partial class WebRuntimeClient
{
    private static readonly TimeSpan DownloadDeadline = TimeSpan.FromMinutes(10);

    /// <summary>Requests one ticket (service data, profiling data or backup). Nothing is sent unless the session advertised the method and
    /// Api.CloseTicket: a ticket that cannot be closed would block every user (service data) or RUN (backup). Sent once on a fresh connection, never retried.</summary>
    public async Task<(string Status, int? ProviderErrorCode, string? Ticket)> RequestTicketAsync(string kind, long? hwid, CancellationToken cancellationToken)
    {
        Active(); var (method, _) = WebRuntimeProtocol.TicketKind(kind);
        var body = WebRuntimeProtocol.TicketRequest(Id(), kind, hwid); string id = body.GetProperty("id").GetString()!;
        if (!Methods.Contains(method) || !Methods.Contains("Api.CloseTicket")) return ("unsupported", null, null);
        DropReadConnection();
        return await Exchange(body, bytes => WebRuntimeProtocol.TicketResult(bytes, id), cancellationToken);
    }

    /// <summary>Api.CloseTicket for a ticket this session created; true only when the CPU answered true. Sent even when the download failed.</summary>
    public async Task<bool> CloseTicketAsync(string ticket, CancellationToken cancellationToken)
    {
        if (token is null) return false;
        var body = WebRuntimeProtocol.CloseTicket(Id(), ticket); string id = body.GetProperty("id").GetString()!;
        DropReadConnection();
        return await Exchange(body, bytes =>
        {
            using var response = WebRuntimeProtocol.Response(bytes, id);
            return response.RootElement.TryGetProperty("result", out var result) && result.ValueKind == System.Text.Json.JsonValueKind.True;
        }, cancellationToken);
    }

    /// <summary>The state of one alarm (Alarms.Browse with alarm_id) in the given language; "unsupported" without a request when not advertised.</summary>
    public async Task<(string Status, int? ProviderErrorCode, string? Acknowledgement)> AlarmStateAsync(string language, string alarmId, CancellationToken cancellationToken)
    {
        Active(); var body = WebRuntimeProtocol.AlarmById(Id(), language, alarmId); string id = body.GetProperty("id").GetString()!;
        if (!Methods.Contains("Alarms.Browse")) return ("unsupported", null, null);
        return await Exchange(body, bytes => WebRuntimeProtocol.AlarmState(bytes, id, alarmId), cancellationToken, keepAlive: true);
    }

    /// <summary>Redeems the ticket once at GET /api/ticket?id= on this session's pinned endpoint (no redirects, a fresh connection) and streams the body
    /// to <paramref name="temporaryPath"/> (created new), hashing as it goes. The cap is enforced while reading, whatever Content-Length claims; any failure deletes the file.</summary>
    public async Task<(long Bytes, string Sha256)> DownloadTicketAsync(string ticket, long maximumBytes, string temporaryPath, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this); Active(); WebRuntimeProtocol.Ticket(ticket);
        // The ticket stays out of every message and result; the URL is built only from the pinned endpoint and the validated ticket.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(DownloadDeadline);
        bool done = false;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Context.Endpoint + "/api/ticket?id=" + Uri.EscapeDataString(ticket))
            { Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
            request.Headers.ConnectionClose = true;
            request.Headers.Add("X-Auth-Token", token!);
            DropReadConnection();
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (!PeerVerified) throw Error("RuntimeTlsUnverified");
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw Error("RuntimeAuthenticationDenied");
            // Redirects are off, so a 3xx is a refusal; the Siemens manual allows 200 for a download.
            if (response.StatusCode != HttpStatusCode.OK) throw Error("RuntimeHttpFailure");
            if (response.Content.Headers.ContentLength > maximumBytes) throw Error("RuntimeDownloadLimit");
            using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long total = 0; byte[] buffer = new byte[81920];
            await using (var file = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                int read;
                while ((read = await stream.ReadAsync(buffer, deadline.Token).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > maximumBytes) throw Error("RuntimeDownloadLimit");
                    hash.AppendData(buffer, 0, read); await file.WriteAsync(buffer.AsMemory(0, read), deadline.Token).ConfigureAwait(false);
                }
                await file.FlushAsync(deadline.Token).ConfigureAwait(false);
            }
            if (total == 0) throw Error("RuntimeResponseInvalid");
            lastUse = Stopwatch.GetTimestamp(); done = true;
            return (total, Convert.ToHexStringLower(hash.GetHashAndReset()));
        }
        catch (OperationCanceledException) { throw Error(cancellationToken.IsCancellationRequested ? "RuntimeCancelled" : "RuntimeTimeout"); }
        catch (HttpRequestException e) { throw Error(Tls(e) ? "RuntimeTlsUnverified" : "RuntimeTransportFailure"); }
        catch (IOException) { throw Error("RuntimeTransportFailure"); }
        finally { if (!done) PackageStorage.TryDelete(temporaryPath); }
    }
}
