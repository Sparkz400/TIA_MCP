using System.Globalization;
using Opc.Ua;
using Opc.Ua.Server;

namespace TiaMcp.OpcUa.Runtime.Tests;

internal sealed partial class FixtureServer
{
    private readonly Dictionary<uint,string> subscriptionHandles=new();
    private int publishOrdinal, dataOrdinal, duplicateOrdinal, activePublishes;
    internal int ActivePublishes=>Volatile.Read(ref activePublishes);
    private NotificationMessage? firstMessage;
    /// <summary>
    /// Forced timing for robustness runs, off by default: with TIAMCP_UA_FIXTURE_HOLD_DATA_MS set, every Publish a new
    /// subscription receives within that many milliseconds of its creation is answered, one publishing interval later, with a
    /// keep-alive, as a server answers whose first values or events are not queued yet (a loaded machine). No check may then
    /// depend on values or events arriving in the first publishing cycle.
    /// </summary>
    internal static readonly int HoldDataMs=int.TryParse(Environment.GetEnvironmentVariable("TIAMCP_UA_FIXTURE_HOLD_DATA_MS"),out int hold) && hold>0 ? hold : 0;
    private long subscriptionCreatedAt;
    private bool basePublished;
    private uint lostSequence;
    private int lostOrdinal;
    private uint currentSubscription;
    private double currentInterval;
    public override async Task<CreateSubscriptionResponse> CreateSubscriptionAsync(SecureChannelContext channel,RequestHeader header,
        double interval,uint lifetime,uint keepAlive,uint maximum,bool enabled,byte priority,CancellationToken ct)
    {
        publishOrdinal=0;dataOrdinal=0;duplicateOrdinal=0;basePublished=false;lostSequence=0;lostOrdinal=0;firstMessage=null;subscriptionHandles.Clear();
        var response=await base.CreateSubscriptionAsync(channel,header,Mode=="subscriptionLongKeepAlive"?1000:Math.Max(200,interval),Math.Max(60,lifetime),Math.Max(2,keepAlive),maximum,enabled,priority,ct);
        currentSubscription=response.SubscriptionId;currentInterval=response.RevisedPublishingInterval;subscriptionCreatedAt=System.Diagnostics.Stopwatch.GetTimestamp();
        Audit.Enqueue(new {operation="SubscriptionCreated",requestedInterval=interval,requestedLifetime=lifetime,requestedKeepAlive=keepAlive,
            maximumNotifications=maximum,subscriptionId=response.SubscriptionId,revisedInterval=response.RevisedPublishingInterval,
            revisedLifetime=response.RevisedLifetimeCount,revisedKeepAlive=response.RevisedMaxKeepAliveCount});
        return response;
    }
    public override async Task<CreateMonitoredItemsResponse> CreateMonitoredItemsAsync(SecureChannelContext channel,RequestHeader header,
        uint subscription,TimestampsToReturn timestamps,MonitoredItemCreateRequestCollection items,CancellationToken ct)
    {
        if(Mode=="subscriptionCreateItemsDenied")throw new ServiceResultException(StatusCodes.BadUserAccessDenied);
        foreach(var item in items)
        {
            subscriptionHandles[item.RequestedParameters.ClientHandle]=Convert.ToString(item.ItemToMonitor.NodeId.Identifier, CultureInfo.InvariantCulture)!;
            item.RequestedParameters.SamplingInterval=Math.Max(200,item.RequestedParameters.SamplingInterval);
            item.RequestedParameters.QueueSize=Math.Min(4,item.RequestedParameters.QueueSize);
        }
        var response=await base.CreateMonitoredItemsAsync(channel,header,subscription,timestamps,items,ct);
        foreach(var item in items.Where(i=>i.ItemToMonitor.AttributeId==Attributes.EventNotifier))
        {
            var filter=(EventFilter)item.RequestedParameters.Filter.Body;
            Audit.Enqueue(new {operation="FixtureEventFilter",node=item.ItemToMonitor.NodeId.ToString(),
                fields=filter.SelectClauses.Select(s=>new{type=s.TypeDefinitionId.ToString(),path=s.BrowsePath.Select(p=>p.ToString()).ToArray(),attribute=s.AttributeId}).ToArray(),
                operators=(filter.WhereClause?.Elements??[]).Select(e=>e.FilterOperator.ToString()).ToArray(),
                minimumSeverity=(filter.WhereClause?.Elements?.Count??0)==0?null:((LiteralOperand)filter.WhereClause.Elements[0].FilterOperands[1].Body).Value.Value});
        }
        Audit.Enqueue(new {operation="ItemsCreated",subscription,items=response.Results.Select((r,i)=>new {clientHandle=items[i].RequestedParameters.ClientHandle,
            node=Convert.ToString(items[i].ItemToMonitor.NodeId.Identifier, CultureInfo.InvariantCulture),status=$"0x{r.StatusCode.Code:x8}",id=r.MonitoredItemId,
            revisedSampling=r.RevisedSamplingInterval,revisedQueue=r.RevisedQueueSize}).ToArray()});
        return response;
    }
    public override async Task<PublishResponse> PublishAsync(SecureChannelContext channel,RequestHeader header,SubscriptionAcknowledgementCollection acks,CancellationToken ct)
    {
        Interlocked.Increment(ref activePublishes);
        Audit.Enqueue(new {operation="PublishEntered",requestHandle=header.RequestHandle,at=DateTimeOffset.UtcNow});
        try
        {
        if(Mode=="subscriptionPublishDenied")throw new ServiceResultException(StatusCodes.BadUserAccessDenied);
        // A held keep-alive goes through the same faults as one the server sends on its own.
        bool held=HoldDataMs>0 && !basePublished && acks.Count==0 && System.Diagnostics.Stopwatch.GetElapsedTime(subscriptionCreatedAt).TotalMilliseconds<HoldDataMs;
        var response=held?await HeldKeepAlive(channel,header,ct):await base.PublishAsync(channel,header,acks,ct);publishOrdinal++;basePublished|=!held;
        // Faults that drop or repeat "the first message" key on the first message that carries data. Whether a
        // keep-alive precedes it depends on whether the first values or events were queued before the
        // server's first publishing cycle, which is timing the test does not own (load delays it past 200 ms).
        bool data=response.NotificationMessage.NotificationData.Count>0;
        if(data)dataOrdinal++;
        bool firstData=data && dataOrdinal==1;
        // A lost response is a data message too: a lost keep-alive leaves nothing to Republish.
        if(DropPublishResponses>0 && data)
        {
            Interlocked.Decrement(ref DropPublishResponses);lostSequence=response.NotificationMessage.SequenceNumber;lostOrdinal=dataOrdinal;
            Audit.Enqueue(new {operation="FixtureLostPublishResponse",sequence=response.NotificationMessage.SequenceNumber});
            throw new ServiceResultException(StatusCodes.BadConnectionClosed);
        }
        // After a lost response, subscriptionGapAfterRecovery drops the first data message that follows the recovery.
        bool firstAfterRecovery=data && lostOrdinal!=0 && dataOrdinal==lostOrdinal+1;
        if(Mode is "subscriptionGap" or "subscriptionRepublishUnavailable" or "subscriptionRepublishStatus" && firstData || Mode=="subscriptionGapAfterRecovery" && firstAfterRecovery)
        {
            Audit.Enqueue(new {operation="FixtureDroppedPublish",sequence=response.NotificationMessage.SequenceNumber});
            var acknowledgementResults=response.Results;
            response=await base.PublishAsync(channel,header,[],ct);
            response.Results=acknowledgementResults;
        }
        Normalize(response.NotificationMessage);
        if(Mode=="subscriptionDuplicate")
        {
            if(firstData){firstMessage=(NotificationMessage)response.NotificationMessage.Clone();duplicateOrdinal=publishOrdinal+1;}
            else if(publishOrdinal==duplicateOrdinal)response.NotificationMessage=(NotificationMessage)firstMessage!.Clone();
        }
        if(Mode=="subscriptionOversize")
            foreach(var change in response.NotificationMessage.NotificationData.Select(x=>x.Body).OfType<DataChangeNotification>())
                if(change.MonitoredItems.Count>0)change.MonitoredItems.Add((MonitoredItemNotification)change.MonitoredItems[0].Clone());
        if(Mode=="subscriptionBadHandle")
            foreach(var change in response.NotificationMessage.NotificationData.Select(x=>x.Body).OfType<DataChangeNotification>())
                foreach(var item in change.MonitoredItems)item.ClientHandle=uint.MaxValue;
        if(Mode=="subscriptionStatus")response.NotificationMessage.NotificationData=[new ExtensionObject(new StatusChangeNotification {Status=StatusCodes.BadTimeout})];
        Audit.Enqueue(new {operation="PublishReturned",subscription=response.SubscriptionId,sequence=response.NotificationMessage.SequenceNumber,
            available=response.AvailableSequenceNumbers.ToArray(),keepAlive=response.NotificationMessage.NotificationData.Count==0,
            acknowledged=acks.Select(x=>x.SequenceNumber).ToArray(),results=response.Results.Select(x=>$"0x{x.Code:x8}").ToArray()});
        return response;
        }
        finally {Audit.Enqueue(new {operation="PublishCompleted",requestHandle=header.RequestHandle,at=DateTimeOffset.UtcNow});Interlocked.Decrement(ref activePublishes);}
    }
    /// <summary>The keep-alive of a subscription that has sent nothing yet: it carries the next sequence number, 1.</summary>
    private async Task<PublishResponse> HeldKeepAlive(SecureChannelContext channel,RequestHeader header,CancellationToken ct)
    {
        var context=ValidateRequest(channel,header,RequestType.Publish);
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(currentInterval),ct);
            return new PublishResponse {ResponseHeader=CreateResponse(header,StatusCodes.Good),SubscriptionId=currentSubscription,
                AvailableSequenceNumbers=[],MoreNotifications=false,Results=[],DiagnosticInfos=[],
                NotificationMessage=new NotificationMessage {SequenceNumber=1,PublishTime=DateTime.UtcNow,NotificationData=[]}};
        }
        finally {OnRequestComplete(context);}
    }
    public override async Task<RepublishResponse> RepublishAsync(SecureChannelContext channel,RequestHeader header,uint subscription,uint sequence,CancellationToken ct)
    {
        Audit.Enqueue(new {operation="RepublishRequested",subscription,sequence});
        // The recovery probe for the message after the lost one, before any further data message, finds nothing to retransmit.
        if(Mode=="subscriptionGapAfterRecovery" && lostOrdinal!=0 && dataOrdinal==lostOrdinal && sequence==lostSequence+1)throw new ServiceResultException(StatusCodes.BadMessageNotAvailable);
        if(Mode=="subscriptionRepublishUnavailable")throw new ServiceResultException(StatusCodes.BadMessageNotAvailable);
        var response=await base.RepublishAsync(channel,header,subscription,sequence,ct);Normalize(response.NotificationMessage);
        if(Mode=="subscriptionRepublishStatus")response.NotificationMessage.NotificationData=[new ExtensionObject(new StatusChangeNotification {Status=StatusCodes.BadTimeout})];
        return response;
    }
    public override Task<TransferSubscriptionsResponse> TransferSubscriptionsAsync(SecureChannelContext channel,RequestHeader header,UInt32Collection subscriptions,bool sendInitialValues,CancellationToken ct)
    {
        Audit.Enqueue(new {operation="TransferRequested",subscriptions=subscriptions.ToArray(),sendInitialValues});
        if(Mode=="transferRefused")throw new ServiceResultException(StatusCodes.BadServiceUnsupported);
        return base.TransferSubscriptionsAsync(channel,header,subscriptions,sendInitialValues,ct);
    }
    public override Task<DeleteSubscriptionsResponse> DeleteSubscriptionsAsync(SecureChannelContext channel,RequestHeader header,UInt32Collection subscriptions,CancellationToken ct)
    {
        Audit.Enqueue(new {operation="SubscriptionDeleteRequested",subscriptions=subscriptions.ToArray()});
        if(Mode=="subscriptionDeleteDenied")throw new ServiceResultException(StatusCodes.BadUserAccessDenied);
        return base.DeleteSubscriptionsAsync(channel,header,subscriptions,ct);
    }
    private void Normalize(NotificationMessage message)
    {
        AlterEvent(message);
        foreach(var changes in message.NotificationData.Select(x=>x.Body).OfType<DataChangeNotification>())
        foreach(var n in changes.MonitoredItems)
        {
            if(!subscriptionHandles.TryGetValue(n.ClientHandle,out string? name))continue;
            var v=n.Value;
            v.SourceTimestamp=name=="missingTimes"?DateTime.MinValue:SourceTime;
            v.ServerTimestamp=name=="missingTimes"?DateTime.MinValue:ServerTime;
            v.SourcePicoseconds=name=="missingTimes"?(ushort)0:(ushort)12;v.ServerPicoseconds=name=="missingTimes"?(ushort)0:(ushort)34;
            if(name=="bad")v.StatusCode=StatusCodes.BadSensorFailure;
            if(name=="badArray")v.StatusCode=StatusCodes.BadSensorFailure;
            if(name=="uncertainArray")v.StatusCode=StatusCodes.UncertainInitialValue;
            if(name=="uncertain")v.StatusCode=StatusCodes.UncertainInitialValue;
            if(name=="denied")v.StatusCode=StatusCodes.BadUserAccessDenied;
            if(name=="byteArray")v.WrappedValue=new Variant(new byte[]{1,2,3},new TypeInfo(BuiltInType.Byte,ValueRanks.OneDimension));
            if(name=="nullArray")v.WrappedValue=new Variant(null,new TypeInfo(BuiltInType.Int32,ValueRanks.OneDimension));
            if(Mode=="subscriptionOverflow")v.StatusCode=new StatusCode(v.StatusCode.Code|0x00000480u);
            if(Mode=="secretEcho" && name=="text")v.Value="prefix fixture-only-secret suffix";
        }
    }
}
