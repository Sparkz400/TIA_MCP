using System.Text.Json;
using System.Text.Json.Serialization;
using TiaMcp.Core;
using TiaMcp.Core.PhysicalRuntime;
using TiaMcp.Core.Security;
using TiaMcp.Core.Diagnostics;

namespace TiaMcp.OpcUa.Runtime;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OpcUaSubscriptionRequest(string AttemptId,
    [property: JsonConverter(typeof(OpcUaSubscriptionNodesConverter))] OpcUaRuntimeNode[] Nodes,
    int PublishingIntervalMilliseconds = 100, int SamplingIntervalMilliseconds = 100,
    uint QueueSize = 16, bool DiscardOldest = true, uint KeepAliveCount = 10, uint LifetimeCount = 30,
    int DurationSeconds = 10, int MaximumNotifications = 10000, long MaximumBytes = 64L << 20,
    int MaximumRepublish = 16,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DiagnosticBindingId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int MaximumReconnectAttempts = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OpcUaEventSelectionConverter))] OpcUaEventSelection? Events = null);

// Strictness is scoped to this new request; shared node contracts and existing
// manual-read/polling deserialization keep their established behavior.
internal sealed class OpcUaSubscriptionNodesConverter : JsonConverter<OpcUaRuntimeNode[]>
{
    public OpcUaSubscriptionNodesConverter() { }
    public override OpcUaRuntimeNode[]? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        JsonSerializer.Deserialize<OpcUaRuntimeNode[]>(ref reader,
            new JsonSerializerOptions(options) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow });
    public override void Write(Utf8JsonWriter writer, OpcUaRuntimeNode[] value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value, options);
}
public sealed record OpcUaSubscriptionPlan(string CaptureId, string HostId, DateTimeOffset CreatedUtc,
    OpcUaSubscriptionRequest Request, OpcUaRuntimeStatus Session);
public sealed record OpcUaMonitoredItem(uint ClientHandle, OpcUaRuntimeNode Node, string StatusCode,
    uint MonitoredItemId, double RevisedSamplingInterval, uint RevisedQueueSize, OpcUaRuntimeValue Metadata);
public sealed record OpcUaSubscriptionCreated(uint SubscriptionId, double RevisedPublishingInterval,
    uint RevisedLifetimeCount, uint RevisedKeepAliveCount, OpcUaMonitoredItem[] Items,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] OpcUaEventMonitoredItem? EventItem = null);
public sealed record OpcUaSubscriptionValue(uint ClientHandle, uint MonitoredItemId, bool Overflow, OpcUaRuntimeValue Value);
public sealed record OpcUaSubscriptionEvent(string Kind, uint SubscriptionId,
    uint? SequenceNumber = null, DateTimeOffset? PublishTime = null, bool KeepAlive = false,
    bool Duplicate = false, bool MoreNotifications = false, uint[]? AvailableSequenceNumbers = null,
    uint[]? AcknowledgedSequenceNumbers = null, string[]? AcknowledgementResults = null,
    OpcUaSubscriptionValue[]? Notifications = null, string[]? StatusChanges = null,
    uint? RequestedSequenceNumber = null, string? StatusCode = null,
    OpcUaSubscriptionCreated? Created = null, string? ErrorCode = null,
    uint? GapFrom = null, uint? GapTo = null, long MissingCount = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] OpcUaReconnectEvent? Reconnect = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] OpcUaEventNotification[]? Events = null);
public sealed record OpcUaSubscriptionRow(long Sequence, double ElapsedMilliseconds, DateTimeOffset ObservedUtc,
    OpcUaSubscriptionEvent Event);
public sealed record OpcUaSubscriptionProgress(string State, string? TerminalReason, double ElapsedMilliseconds,
    DateTimeOffset ObservedUtc, long Rows, long Notifications, long DuplicateMessages, long MissingSequences,
    bool RemoteCleanupConfirmed);

internal static class OpcUaSubscriptionContracts
{
    internal static void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool condition) { if (!condition) throw OpcUaRuntimeClient.Error("UaSubscriptionInvalid"); }
    internal static void Validate(OpcUaSubscriptionRequest r)
    {
        Require(r is not null && ProtectedCredentialGuard.IsId(r.AttemptId) && r.Nodes is { Length: <= 64 } && (r.Nodes.Length>0 || r.Events is not null));
        if(r.Events is { } events) {OpcUaRuntimeClient.ValidateNode(events.Notifier);Require(events.MinimumSeverity<=1000);}
        Require(r.DiagnosticBindingId is null || r.Nodes.Length>0);
        DiagnosticUaBindings.ValidateId(r.DiagnosticBindingId);
        Require(r.MaximumReconnectAttempts is >= 0 and <= 3);
        foreach (var node in r.Nodes) OpcUaRuntimeClient.ValidateNode(node);
        Require(r.Nodes.Distinct().Count() == r.Nodes.Length && r.PublishingIntervalMilliseconds is >= 100 and <= 1000 &&
            r.SamplingIntervalMilliseconds is >= 0 and <= 60000 && r.QueueSize is >= 1 and <= 1024 &&
            r.KeepAliveCount is >= 1 and <= 10 && r.LifetimeCount >= 3*r.KeepAliveCount && r.LifetimeCount <= 1000 &&
            r.DurationSeconds is >= 1 and <= 3600 && r.MaximumNotifications is >= 1 and <= 100000 &&
            r.MaximumBytes is >= (1L<<20) and <= (64L<<20) && r.MaximumRepublish is >= 0 and <= 64);
    }
    internal static void ValidatePlan(OpcUaSubscriptionPlan p)
    {
        Validate(p.Request);
        Require(ProtectedCredentialGuard.IsId(p.CaptureId) && ProtectedCredentialGuard.IsId(p.HostId) &&
            p.Session.AttemptId == p.Request.AttemptId && p.Session.HostId == p.HostId && p.Session.State == "connected" &&
            p.Session.PeerVerified && p.Session.ErrorCode is null && p.Session.OperationErrorCode is null);
        OpcUaRuntimeSessionStore.Validate(p.Session.Selection);
    }
    internal static void ValidateValue(OpcUaSubscriptionPlan p, OpcUaRuntimeValue value)
    {
        Require(p.Request.Nodes.Contains(value.Node));
        // Reuse the established strict value, status, type, timestamp and canonical JSON checks.
        var request = new OpcUaCaptureRequest(p.Request.AttemptId,[value.Node],100,p.Request.DurationSeconds);
        var plan = new RuntimeCapturePlan(p.CaptureId,p.HostId,p.CreatedUtc,OpcUaCaptureContracts.StorageRequest(request),[],UaContext:new(request,p.Session));
        string json = JsonSerializer.Serialize(new OpcUaRuntimeReadResult(request.AttemptId,p.CreatedUtc,p.CreatedUtc,[value]),BridgeProtocol.Json);
        OpcUaCaptureContracts.ValidateRead(plan,new(0,"read",OpcUaCaptureContracts.SelectionId(value.Node),0,0,0,p.CreatedUtc,json,null,0));
    }
    internal static uint Status(string? text)
    {
        Require(text is { Length:10 } && text.StartsWith("0x",StringComparison.Ordinal) && uint.TryParse(text.AsSpan(2),System.Globalization.NumberStyles.HexNumber,System.Globalization.CultureInfo.InvariantCulture,out _));
        uint result=uint.Parse(text!.AsSpan(2),System.Globalization.NumberStyles.HexNumber,System.Globalization.CultureInfo.InvariantCulture);
        Require(text==$"0x{result:x8}"); return result;
    }
    internal static void ValidateRow(OpcUaSubscriptionPlan p, OpcUaSubscriptionRow r)
    {
        Require(r.Sequence >= 0 && double.IsFinite(r.ElapsedMilliseconds) && r.ElapsedMilliseconds>=0);
        var e=r.Event; Require(e is not null && e.Kind is "created" or "publish" or "republish" or "gap" or "deleted" or "failure" or "reconnect");
        if(e.Kind=="reconnect") {Require(e.Reconnect is not null && e.SubscriptionId>0);OpcUaReconnectHistory.Validate(e.Reconnect,p.Request.MaximumReconnectAttempts);}
        else Require(e.Reconnect is null);
        if(e.StatusCode is not null) Status(e.StatusCode);
        Require(e.AvailableSequenceNumbers is null || e.AvailableSequenceNumbers.Length<=4096 && e.AvailableSequenceNumbers.All(x=>x>0));
        Require(e.AcknowledgedSequenceNumbers is null || e.AcknowledgedSequenceNumbers.Length<=65 && e.AcknowledgedSequenceNumbers.All(x=>x>0));
        Require((e.AcknowledgementResults?.Length??0)==(e.AcknowledgedSequenceNumbers?.Length??0));
        foreach(var status in e.AcknowledgementResults??[]) Status(status);
        Require(e.StatusChanges is null || e.StatusChanges.Length<=4096);
        foreach(var status in e.StatusChanges??[]) Status(status);
        Require((e.Notifications?.Length??0)<=4096);
        OpcUaEventContracts.Validate(p,e);
        foreach(var n in e.Notifications??[]) { Require(n.ClientHandle>=1 && n.ClientHandle<=p.Request.Nodes.Length && n.MonitoredItemId>0 && n.Value.Node==p.Request.Nodes[n.ClientHandle-1]); ValidateValue(p,n.Value); Require(n.Overflow==((Status(n.Value.StatusCode)&0x00000480)==0x00000480)); }
        bool message=e.Kind is "publish" or "republish";
        if(!message)Require(e.SequenceNumber is null && e.PublishTime is null && !e.KeepAlive && !e.Duplicate && !e.MoreNotifications &&
            e.AvailableSequenceNumbers is null && e.AcknowledgedSequenceNumbers is null && e.AcknowledgementResults is null && e.Notifications is null && e.StatusChanges is null);
        if(e.Kind=="created")
        {
            var c=e.Created; Require(c is not null && c.SubscriptionId>0 && c.SubscriptionId==e.SubscriptionId &&
                double.IsFinite(c.RevisedPublishingInterval) && c.RevisedPublishingInterval>0 && c.RevisedKeepAliveCount>0 &&
                c.RevisedLifetimeCount>=3UL*c.RevisedKeepAliveCount && c.Items.Length==p.Request.Nodes.Length);
            var successfulIds=new HashSet<uint>();
            for(int i=0;i<c.Items.Length;i++) {var item=c.Items[i]; Require(item.ClientHandle==i+1 && item.Node==p.Request.Nodes[i] && item.Metadata.Node==item.Node &&
                double.IsFinite(item.RevisedSamplingInterval) && item.RevisedSamplingInterval>=0 &&
                ((Status(item.StatusCode)&0x80000000)!=0 ? item.MonitoredItemId==0 : item.MonitoredItemId>0 && item.RevisedQueueSize>0 && successfulIds.Add(item.MonitoredItemId))); ValidateValue(p,item.Metadata); }
        }
        else Require(e.Created is null);
        if(message)Require(e.SubscriptionId>0 && e.SequenceNumber>0 && e.PublishTime is not null && e.Notifications is not null && e.StatusChanges is not null &&
            e.Notifications.Length+(e.Events?.Length??0)<=1 && (!e.KeepAlive || e.Notifications.Length==0 && (e.Events?.Length??0)==0 && e.StatusChanges.Length==0 && !e.Duplicate));
        if(e.Kind=="publish")Require(e.AvailableSequenceNumbers is not null && e.AcknowledgedSequenceNumbers is not null && e.AcknowledgementResults is not null && e.RequestedSequenceNumber is null);
        if(e.Kind=="republish")Require(e.RequestedSequenceNumber==e.SequenceNumber && !e.KeepAlive && !e.MoreNotifications &&
            e.AvailableSequenceNumbers is null && e.AcknowledgedSequenceNumbers is null && e.AcknowledgementResults is null);
        if(e.Kind=="gap")Require(e.GapFrom>0 && e.GapTo>0 && e.MissingCount>0 &&
            (ulong)e.MissingCount==((ulong)e.GapTo.Value+uint.MaxValue-e.GapFrom.Value)%uint.MaxValue+1);
        else Require(e.GapFrom is null && e.GapTo is null && e.MissingCount==0);
        if(e.Kind=="failure")
        {
            Require(e.ErrorCode is { Length: >=1 and <=80 } && e.ErrorCode.StartsWith("Ua",StringComparison.Ordinal) && !e.ErrorCode.Any(char.IsControl));
            if(e.ErrorCode=="UaRepublishUnavailable")Require(e.RequestedSequenceNumber>0 && e.StatusCode=="0x807b0000");
            else Require(e.RequestedSequenceNumber is null && e.StatusCode is null);
        }
        else {Require(e.ErrorCode is null);if(e.Kind!="republish")Require(e.RequestedSequenceNumber is null);}
        if(e.Kind=="deleted")Require(e.StatusCode is not null && e.SubscriptionId>0);
        else if(e.Kind!="failure")Require(e.StatusCode is null);
    }
    internal static bool AllItemsFailed(OpcUaSubscriptionCreated c) => c.Items.All(x=>(Status(x.StatusCode)&0x80000000)!=0) &&
        (c.EventItem is null || (Status(c.EventItem.StatusCode)&0x80000000)!=0);
    internal static long Count(OpcUaSubscriptionEvent e) => e.Duplicate ? 0 : (e.Notifications?.Length??0)+(e.Events?.Length??0);
}
