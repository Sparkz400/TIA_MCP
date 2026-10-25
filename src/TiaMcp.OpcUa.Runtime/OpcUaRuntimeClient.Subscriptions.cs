using Opc.Ua;
using TiaMcp.Core.PhysicalRuntime;

namespace TiaMcp.OpcUa.Runtime;

internal sealed partial class OpcUaRuntimeClient
{
    private uint subscriptionId;
    private OpcUaMonitoredItem[] monitoredItems = [];
    private OpcUaEventMonitoredItem? eventItem;
    internal async Task<OpcUaSubscriptionCreated> CreateSubscription(string attemptId, OpcUaSubscriptionRequest request, CancellationToken ct)
    {
        if(subscriptionId!=0) throw Error("UaSessionAcquisitionBusy");
        var metadata=request.Nodes.Length>0 ? (await Read(attemptId,request.Nodes,ct)).Items.ToArray() : [];
        byte eventNotifier=0;
        if(request.Events is { } selected)
        {
            await VerifyIdentity(ct);
            var capability=await Session.ReadAsync(null,0,TimestampsToReturn.Neither,[new ReadValueId{NodeId=Resolve(selected.Notifier),AttributeId=Attributes.EventNotifier}],ct);
            if(capability.Results.Count!=1 || !StatusCode.IsGood(capability.Results[0].StatusCode) || capability.Results[0].Value is not byte flags || (flags&EventNotifiers.SubscribeToEvents)==0)
                throw Error("UaEventNotifierUnavailable");
            eventNotifier=flags;
        }
        var created=await Session.CreateSubscriptionAsync(null,request.PublishingIntervalMilliseconds,
            request.LifetimeCount,request.KeepAliveCount,1,true,0,ct);
        subscriptionId=created.SubscriptionId;
        if(subscriptionId==0) throw Error("UaResponseInvalid");
        // No Subscription is registered with Session: the stack starts no background Publish engine.
        var requests=new MonitoredItemCreateRequestCollection();
        for(int i=0;i<request.Nodes.Length;i++) requests.Add(new MonitoredItemCreateRequest {
            ItemToMonitor=new ReadValueId {NodeId=Resolve(request.Nodes[i]),AttributeId=Attributes.Value},
            MonitoringMode=MonitoringMode.Reporting,
            RequestedParameters=new MonitoringParameters { ClientHandle=(uint)i+1,
                SamplingInterval=request.SamplingIntervalMilliseconds,QueueSize=request.QueueSize,
                DiscardOldest=request.DiscardOldest,Filter=null }
        });
        if(request.Events is { } events)requests.Add(new MonitoredItemCreateRequest {
            ItemToMonitor=new ReadValueId{NodeId=Resolve(events.Notifier),AttributeId=Attributes.EventNotifier},MonitoringMode=MonitoringMode.Reporting,
            RequestedParameters=new MonitoringParameters{ClientHandle=(uint)request.Nodes.Length+1,SamplingInterval=0,
                QueueSize=request.QueueSize,DiscardOldest=request.DiscardOldest,Filter=new ExtensionObject(EventFilter(events))} });
        var response=await Session.CreateMonitoredItemsAsync(null,subscriptionId,TimestampsToReturn.Both,requests,ct);
        if(response.Results.Count!=requests.Count) throw Error("UaResponseInvalid");
        monitoredItems=response.Results.Take(request.Nodes.Length).Select((r,i)=>new OpcUaMonitoredItem((uint)i+1,request.Nodes[i],$"0x{r.StatusCode.Code:x8}",
            r.MonitoredItemId,r.RevisedSamplingInterval,r.RevisedQueueSize,metadata[i])).ToArray();
        if(request.Events is not null)
        {
            var r=response.Results[^1];string[]? statuses=null;
            if(r.FilterResult?.Body is EventFilterResult filter)
            {
                if(filter.SelectClauseResults.Count!=0 && filter.SelectClauseResults.Count!=OpcUaEventContracts.Fields.Length ||
                    filter.WhereClauseResult.ElementResults.Any(e=>!StatusCode.IsGood(e.StatusCode) || e.OperandStatusCodes.Any(s=>!StatusCode.IsGood(s))))throw Error("UaEventFilterRejected");
                if(filter.SelectClauseResults.Count>0)statuses=filter.SelectClauseResults.Select(s=>$"0x{s.Code:x8}").ToArray();
            }
            else if(r.FilterResult?.Body is not null)throw Error("UaResponseInvalid");
            eventItem=new((uint)request.Nodes.Length+1,request.Events.Notifier,$"0x{r.StatusCode.Code:x8}",r.MonitoredItemId,r.RevisedSamplingInterval,r.RevisedQueueSize,statuses,eventNotifier);
        }
        var result=new OpcUaSubscriptionCreated(subscriptionId,created.RevisedPublishingInterval,created.RevisedLifetimeCount,created.RevisedMaxKeepAliveCount,monitoredItems,eventItem);
        CheckOutput(result); return result;
    }
    internal async Task<OpcUaSubscriptionEvent> Publish(uint[] acknowledgements,CancellationToken ct)
    {
        await VerifyIdentity(ct);
        var acks=new SubscriptionAcknowledgementCollection(acknowledgements.Select(x=>new SubscriptionAcknowledgement {SubscriptionId=subscriptionId,SequenceNumber=x}));
        var response=await Session.PublishAsync(new RequestHeader {TimeoutHint=TimeoutMs-1000},acks,ct);
        if(response.SubscriptionId!=subscriptionId || response.Results.Count!=acks.Count || response.AvailableSequenceNumbers.Count>4096) throw Error("UaResponseInvalid");
        var result=Message("publish",response.NotificationMessage) with { MoreNotifications=response.MoreNotifications,
            AvailableSequenceNumbers=response.AvailableSequenceNumbers.ToArray(),AcknowledgedSequenceNumbers=acknowledgements,
            AcknowledgementResults=response.Results.Select(x=>$"0x{x.Code:x8}").ToArray() };
        CheckOutput(result); return result;
    }
    internal async Task<OpcUaSubscriptionEvent> Republish(uint sequence,CancellationToken ct)
    {
        await VerifyIdentity(ct);
        try
        {
            var response=await Session.RepublishAsync(null,subscriptionId,sequence,ct);
            if(response.NotificationMessage.SequenceNumber!=sequence)throw Error("UaResponseInvalid");
            var result=Message("republish",response.NotificationMessage) with { RequestedSequenceNumber=sequence };
            CheckOutput(result); return result;
        }
        catch(ServiceResultException e) when(e.StatusCode==StatusCodes.BadMessageNotAvailable)
        { return new("failure",subscriptionId,RequestedSequenceNumber:sequence,StatusCode:$"0x{e.StatusCode:x8}",ErrorCode:"UaRepublishUnavailable"); }
    }
    private OpcUaSubscriptionEvent Message(string kind,NotificationMessage message)
    {
        if(message.SequenceNumber==0 || message.NotificationData.Count>4096) throw Error("UaResponseInvalid");
        var values=new List<OpcUaSubscriptionValue>(); var statuses=new List<string>();var events=new List<OpcUaEventNotification>();
        foreach(var extension in message.NotificationData)
        {
            if(extension.Body is DataChangeNotification changes)
            {
                if(changes.MonitoredItems.Count+values.Count>1)throw Error("UaResponseLimit");
                foreach(var n in changes.MonitoredItems)
                {
                    var item=monitoredItems.SingleOrDefault(x=>x.ClientHandle==n.ClientHandle);
                    if(item is null || item.MonitoredItemId==0 || (OpcUaSubscriptionContracts.Status(item.StatusCode)&0x80000000)!=0)throw Error("UaResponseInvalid");
                    var m=item.Metadata;
                    var type=m.DataType is null ? new DataValue(StatusCodes.BadNotReadable) : new DataValue(new Variant(Resolve(m.DataType)));
                    var rank=m.ValueRank is null ? new DataValue(StatusCodes.BadNotReadable) : new DataValue(new Variant(m.ValueRank.Value));
                    var dimensions=new DataValue(new Variant(m.ArrayDimensions));
                    var value=DecodeValue(item.Node,n.Value,type,rank,dimensions);
                    values.Add(new(item.ClientHandle,item.MonitoredItemId,(n.Value.StatusCode.Code&0x00000480)==0x00000480,value));
                }
            }
            else if(extension.Body is EventNotificationList notifications)
            {
                if(eventItem is null || (OpcUaSubscriptionContracts.Status(eventItem.StatusCode)&0x80000000)!=0 || notifications.Events.Count+events.Count>1)throw Error("UaSubscriptionNotificationUnsupported");
                foreach(var notification in notifications.Events)
                {
                    if(notification.ClientHandle!=eventItem.ClientHandle || notification.EventFields.Count!=OpcUaEventContracts.Fields.Length)throw Error("UaResponseInvalid");
                    var fields=notification.EventFields.Select((field,i)=>DecodeEventField(field,i)).ToArray();
                    events.Add(new(eventItem.ClientHandle,eventItem.MonitoredItemId,OpcUaEventContracts.IsOverflow(fields[1].Node),fields));
                }
            }
            else if(extension.Body is StatusChangeNotification status) statuses.Add($"0x{status.Status.Code:x8}");
            else throw Error("UaSubscriptionNotificationUnsupported");
        }
        if(values.Count+events.Count>1)throw Error("UaResponseLimit");
        return new(kind,subscriptionId,message.SequenceNumber,Time(message.PublishTime),message.NotificationData.Count==0,
            Notifications:values.ToArray(),StatusChanges:statuses.ToArray(),Events:events.Count>0?events.ToArray():null);
    }
    internal async Task<OpcUaSubscriptionEvent?> DeleteSubscription(CancellationToken ct)
    {
        if(subscriptionId==0)return null;
        uint captured=subscriptionId;
        // Cleanup uses the exact originally captured session even after a failed/cancelled Publish.
        if(session is not {Connected:true})return new("failure",captured,ErrorCode:"UaSubscriptionCleanupUnknown");
        try
        {
            var result=await session.DeleteSubscriptionsAsync(null,[captured],ct);
            if(result.Results.Count!=1)throw Error("UaResponseInvalid");
            uint code=result.Results[0].Code;
            if(StatusCode.IsGood(code)) {subscriptionId=0;monitoredItems=[];eventItem=null;}
            return new("deleted",captured,StatusCode:$"0x{code:x8}");
        }
        catch {return new("failure",captured,ErrorCode:"UaSubscriptionCleanupUnknown");}
    }
}
