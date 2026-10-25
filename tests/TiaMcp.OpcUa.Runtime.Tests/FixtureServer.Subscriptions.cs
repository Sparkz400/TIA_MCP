using Opc.Ua;

namespace TiaMcp.OpcUa.Runtime.Tests;

internal sealed partial class FixtureServer
{
    private readonly Dictionary<uint,string> subscriptionHandles=new();
    private int publishOrdinal, activePublishes;
    internal int ActivePublishes=>Volatile.Read(ref activePublishes);
    private NotificationMessage? firstMessage;
    public override async Task<CreateSubscriptionResponse> CreateSubscriptionAsync(SecureChannelContext channel,RequestHeader header,
        double interval,uint lifetime,uint keepAlive,uint maximum,bool enabled,byte priority,CancellationToken ct)
    {
        publishOrdinal=0;firstMessage=null;subscriptionHandles.Clear();
        var response=await base.CreateSubscriptionAsync(channel,header,Math.Max(200,interval),Math.Max(60,lifetime),Math.Max(2,keepAlive),maximum,enabled,priority,ct);
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
            subscriptionHandles[item.RequestedParameters.ClientHandle]=Convert.ToString(item.ItemToMonitor.NodeId.Identifier)!;
            item.RequestedParameters.SamplingInterval=Math.Max(200,item.RequestedParameters.SamplingInterval);
            item.RequestedParameters.QueueSize=Math.Min(4,item.RequestedParameters.QueueSize);
        }
        var response=await base.CreateMonitoredItemsAsync(channel,header,subscription,timestamps,items,ct);
        foreach(var item in items.Where(i=>i.ItemToMonitor.AttributeId==Attributes.EventNotifier))
        {
            var filter=(EventFilter)item.RequestedParameters.Filter.Body;
            Audit.Enqueue(new {operation="FixtureEventFilter",node=item.ItemToMonitor.NodeId.ToString(),
                fields=filter.SelectClauses.Select(s=>new{type=s.TypeDefinitionId.ToString(),path=s.BrowsePath.Select(p=>p.ToString()).ToArray(),attribute=s.AttributeId}).ToArray(),
                operators=filter.WhereClause.Elements.Select(e=>e.FilterOperator.ToString()).ToArray(),
                minimumSeverity=((LiteralOperand)filter.WhereClause.Elements[0].FilterOperands[1].Body).Value.Value});
        }
        Audit.Enqueue(new {operation="ItemsCreated",subscription,items=response.Results.Select((r,i)=>new {clientHandle=items[i].RequestedParameters.ClientHandle,
            node=Convert.ToString(items[i].ItemToMonitor.NodeId.Identifier),status=$"0x{r.StatusCode.Code:x8}",id=r.MonitoredItemId,
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
        var response=await base.PublishAsync(channel,header,acks,ct);publishOrdinal++;
        if(DropPublishResponses>0)
        {
            Interlocked.Decrement(ref DropPublishResponses);
            Audit.Enqueue(new {operation="FixtureLostPublishResponse",sequence=response.NotificationMessage.SequenceNumber});
            throw new ServiceResultException(StatusCodes.BadConnectionClosed);
        }
        if(Mode is "subscriptionGap" or "subscriptionRepublishUnavailable" or "subscriptionRepublishStatus" && publishOrdinal==1 || Mode=="subscriptionGapAfterRecovery" && publishOrdinal==2)
        {
            Audit.Enqueue(new {operation="FixtureDroppedPublish",sequence=response.NotificationMessage.SequenceNumber});
            var acknowledgementResults=response.Results;
            response=await base.PublishAsync(channel,header,[],ct);
            response.Results=acknowledgementResults;
        }
        Normalize(response.NotificationMessage);
        if(Mode=="subscriptionDuplicate")
        {
            if(publishOrdinal==1)firstMessage=(NotificationMessage)response.NotificationMessage.Clone();
            if(publishOrdinal==2)response.NotificationMessage=(NotificationMessage)firstMessage!.Clone();
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
    public override async Task<RepublishResponse> RepublishAsync(SecureChannelContext channel,RequestHeader header,uint subscription,uint sequence,CancellationToken ct)
    {
        Audit.Enqueue(new {operation="RepublishRequested",subscription,sequence});
        if(Mode=="subscriptionGapAfterRecovery" && publishOrdinal==1 && sequence==2)throw new ServiceResultException(StatusCodes.BadMessageNotAvailable);
        if(Mode=="subscriptionRepublishUnavailable")throw new ServiceResultException(StatusCodes.BadMessageNotAvailable);
        var response=await base.RepublishAsync(channel,header,subscription,sequence,ct);Normalize(response.NotificationMessage);
        if(Mode=="subscriptionRepublishStatus")response.NotificationMessage.NotificationData=[new ExtensionObject(new StatusChangeNotification {Status=StatusCodes.BadTimeout})];
        return response;
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
