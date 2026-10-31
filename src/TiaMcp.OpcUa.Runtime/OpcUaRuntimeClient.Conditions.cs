using System.Diagnostics;
using System.Globalization;
using Opc.Ua;

namespace TiaMcp.OpcUa.Runtime;

internal sealed partial class OpcUaRuntimeClient
{
    // Select clauses of the condition snapshot, in order; a typed field may also come back null or as a status.
    private static readonly (string Name, NodeId Type, string Path, Type Value)[] ConditionFields = [
        ("eventId", ObjectTypeIds.BaseEventType, "EventId", typeof(byte[])), ("eventType", ObjectTypeIds.BaseEventType, "EventType", typeof(NodeId)),
        ("sourceName", ObjectTypeIds.BaseEventType, "SourceName", typeof(string)), ("time", ObjectTypeIds.BaseEventType, "Time", typeof(DateTime)),
        ("message", ObjectTypeIds.BaseEventType, "Message", typeof(LocalizedText)), ("severity", ObjectTypeIds.BaseEventType, "Severity", typeof(ushort)),
        ("conditionId", ObjectTypeIds.ConditionType, "", typeof(NodeId)), ("retain", ObjectTypeIds.ConditionType, "Retain", typeof(bool)),
        ("activeState", ObjectTypeIds.AlarmConditionType, "ActiveState/Id", typeof(bool)), ("ackedState", ObjectTypeIds.AcknowledgeableConditionType, "AckedState/Id", typeof(bool))];

    /// <summary>Read-only Alarms and Conditions snapshot: one temporary subscription on the Server object, ConditionRefresh, bounded
    /// Publish for the window, then DeleteSubscriptions. No Acknowledge, Confirm, AddComment, enable or shelving.</summary>
    internal async Task<object> Conditions(string attemptId, int windowMilliseconds, int maximumEvents, CancellationToken ct)
    {
        if (subscriptionId != 0) throw Error("UaSessionAcquisitionBusy");
        await VerifyIdentity(ct);
        var notifier = await Session.ReadAsync(null, 0, TimestampsToReturn.Neither, [new ReadValueId { NodeId = ObjectIds.Server, AttributeId = Attributes.EventNotifier }], ct);
        if (notifier.Results.Count != 1 || notifier.Results[0].Value is not byte flags || (flags & EventNotifiers.SubscribeToEvents) == 0) throw Error("UaEventNotifierUnavailable");
        var created = await Session.CreateSubscriptionAsync(null, 500, 20, 2, 0, true, 0, ct);
        uint id = created.SubscriptionId;
        if (id == 0) throw Error("UaResponseInvalid");
        bool cleaned = false;
        object? result = null;
        try
        {
            var filter = new EventFilter { SelectClauses = new(ConditionFields.Select(f => new SimpleAttributeOperand { TypeDefinitionId = f.Type,
                AttributeId = f.Path.Length == 0 ? Attributes.NodeId : Attributes.Value,
                BrowsePath = new(f.Path.Length == 0 ? [] : f.Path.Split('/').Select(p => new QualifiedName(p, 0))) })) };
            var items = await Session.CreateMonitoredItemsAsync(null, id, TimestampsToReturn.Both, [new MonitoredItemCreateRequest {
                ItemToMonitor = new ReadValueId { NodeId = ObjectIds.Server, AttributeId = Attributes.EventNotifier }, MonitoringMode = MonitoringMode.Reporting,
                RequestedParameters = new MonitoringParameters { ClientHandle = 1, SamplingInterval = 0, QueueSize = (uint)maximumEvents + 2, DiscardOldest = false,
                    Filter = new ExtensionObject(filter) } }], ct);
            if (items.Results.Count != 1) throw Error("UaResponseInvalid");
            var item = items.Results[0];
            if (StatusCode.IsBad(item.StatusCode)) throw Error("UaEventFilterRejected");
            string[]? selectResults = item.FilterResult?.Body is EventFilterResult r && r.SelectClauseResults.Count == ConditionFields.Length
                ? r.SelectClauseResults.Select(s => $"0x{s.Code:x8}").ToArray() : null;
            var refresh = await Session.CallAsync(null, [new CallMethodRequest { ObjectId = ObjectTypeIds.ConditionType,
                MethodId = MethodIds.ConditionType_ConditionRefresh, InputArguments = [new Variant(id)] }], ct);
            if (refresh.Results.Count != 1) throw Error("UaResponseInvalid");
            var events = new List<Dictionary<string, object?>>();
            var statuses = new List<string>();
            var acknowledgements = new SubscriptionAcknowledgementCollection();
            bool truncated = false;
            int publishes = 0;
            for (var clock = Stopwatch.StartNew(); clock.ElapsedMilliseconds < windowMilliseconds && !truncated; publishes++)
            {
                var response = await Session.PublishAsync(new RequestHeader { TimeoutHint = TimeoutMs - 1000 }, acknowledgements, ct);
                if (response.SubscriptionId != id || response.NotificationMessage.NotificationData.Count > 64) throw Error("UaResponseInvalid");
                acknowledgements = response.NotificationMessage.NotificationData.Count == 0 ? [] :
                    [new SubscriptionAcknowledgement { SubscriptionId = id, SequenceNumber = response.NotificationMessage.SequenceNumber }];
                foreach (var body in response.NotificationMessage.NotificationData.Select(x => x.Body))
                {
                    if (body is StatusChangeNotification change) { statuses.Add($"0x{change.Status.Code:x8}"); continue; }
                    if (body is not EventNotificationList list) throw Error("UaSubscriptionNotificationUnsupported");
                    foreach (var e in list.Events)
                    {
                        if (e.ClientHandle != 1 || e.EventFields.Count != ConditionFields.Length) throw Error("UaResponseInvalid");
                        if (events.Count == maximumEvents) { truncated = true; break; }
                        events.Add(ConditionEvent(e.EventFields));
                    }
                }
            }
            result = new { attemptId, notifier = External(ObjectIds.Server), subscription = new { created.RevisedPublishingInterval, created.RevisedLifetimeCount,
                    created.RevisedMaxKeepAliveCount }, monitoredItem = new { statusCode = $"0x{item.StatusCode.Code:x8}", item.RevisedQueueSize, selectClauseResults = selectResults },
                refresh = new { statusCode = $"0x{refresh.Results[0].StatusCode.Code:x8}", startObserved = events.Any(x => (string?)x["kind"] == "refreshStart"),
                    endObserved = events.Any(x => (string?)x["kind"] == "refreshEnd") },
                events, truncated, overflow = events.Any(x => (string?)x["kind"] == "overflow"), statusChanges = statuses, publishes, windowMilliseconds,
                note = "Read-only snapshot: ConditionRefresh re-sends retained conditions to this temporary subscription only. No Acknowledge, Confirm, AddComment, enable or shelving." };
        }
        finally { cleaned = await DeleteTemporarySubscription(id); }
        if (!cleaned) throw Error("UaSubscriptionCleanupUnknown");
        CheckOutput(result!);
        return result!;
    }

    private Dictionary<string, object?> ConditionEvent(VariantCollection fields)
    {
        var e = new Dictionary<string, object?>();
        for (int i = 0; i < ConditionFields.Length; i++)
        {
            var (name, _, _, type) = ConditionFields[i];
            object? value = fields[i].Value;
            if (value is not null && value is not StatusCode && value.GetType() != type) throw Error("UaEventFieldTypeInvalid");
            e[name] = value switch {
                null => (object?)null, StatusCode s => new { statusCode = $"0x{s.Code:x8}" }, byte[] b => Convert.ToBase64String(b),
                NodeId n => External(n) ?? throw Error("UaEventNamespaceUnavailable"),
                DateTime d => DateTime.SpecifyKind(d, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture),
                LocalizedText t => new { text = t.Text, locale = t.Locale }, _ => value };
        }
        var eventType = fields[1].Value as NodeId;
        e["kind"] = eventType == ObjectTypeIds.RefreshStartEventType ? "refreshStart" : eventType == ObjectTypeIds.RefreshEndEventType ? "refreshEnd" :
            eventType == ObjectTypeIds.EventQueueOverflowEventType ? "overflow" : fields[6].Value is NodeId ? "condition" : "event";
        return e;
    }

    private async Task<bool> DeleteTemporarySubscription(uint id)
    {
        using var deadline = new CancellationTokenSource(TimeoutMs);
        try
        {
            if (session is not { Connected: true } s) return false;
            var deleted = await s.DeleteSubscriptionsAsync(null, [id], deadline.Token);
            return deleted.Results.Count == 1 && StatusCode.IsGood(deleted.Results[0]);
        }
        catch { return false; }
    }
}
