using System.Text.Json;
using TiaMcp.Core;
using TiaMcp.Core.PhysicalRuntime;

namespace TiaMcp.OpcUa.Runtime;

/// <summary>Reconstruct internally consistent retained evidence independently of the journal's counters.</summary>
internal sealed class OpcUaSubscriptionTimeline(OpcUaSubscriptionPlan plan)
{
    private OpcUaSubscriptionCreated? created;
    private readonly HashSet<uint> seen=[];
    private readonly List<(uint From,long Count)> gaps=[];
    private HashSet<uint> committedSeen=[];
    private bool serverStatus;
    private OpcUaSubscriptionEvent? pendingGap;
    private uint expected=1;
    private int republish,recoveryProbes;
    private bool deleted,failed;
    private readonly OpcUaReconnectHistory reconnect=new(plan.Request.MaximumReconnectAttempts);
    private bool recovering;
    internal long Rows,Notifications,Duplicates,Missing;
    internal double Elapsed;
    internal bool Cleanup;
    private static void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool value)=>OpcUaSubscriptionContracts.Require(value);
    internal void BeginSegment()=>committedSeen=new(seen);
    internal void ValidateTerminal(OpcUaSubscriptionProgress p)
    {
        if(p.State!="ended")return;
        if(p.TerminalReason=="notificationLimit")Require(Notifications==plan.Request.MaximumNotifications);
        if(p.TerminalReason=="allItemsFailed")Require(created is not null && created.Items.All(x=>(OpcUaSubscriptionContracts.Status(x.StatusCode)&0x80000000)!=0) && Notifications==0);
        if(p.TerminalReason=="serverStatusChanged")Require(serverStatus);
        if(serverStatus)Require(p.TerminalReason is "serverStatusChanged" or "stopped" or "hostShutdown");
        if(p.TerminalReason=="sequenceGapLimit")Require(Missing>republish-recoveryProbes && republish==plan.Request.MaximumRepublish);
        if(p.TerminalReason=="durationLimit")Require(p.ElapsedMilliseconds>=plan.Request.DurationSeconds*1000d);
        if(p.TerminalReason=="sessionUnavailable")Require(failed);
    }
    internal void Apply(OpcUaSubscriptionRow row)
    {
        OpcUaSubscriptionContracts.ValidateRow(plan,row);
        Require(row.Sequence==Rows && row.ElapsedMilliseconds>=Elapsed && !deleted);
        var e=row.Event;
        if(e.Kind=="created") {Require(Rows==0 && created is null && !failed);created=e.Created;}
        else if(e.Kind=="reconnect")
        {
            Require(created is not null && e.SubscriptionId==created.SubscriptionId && !failed && !serverStatus && pendingGap is null);
            reconnect.Apply(e.Reconnect!);
            if(e.Reconnect!.State=="succeeded")recovering=true;
        }
        else if(e.Kind is "publish" or "republish" or "gap")
        {
            Require(created is not null && !failed && !serverStatus && reconnect.Healthy && !reconnect.Pending && e.SubscriptionId==created.SubscriptionId);
            foreach(var value in e.Notifications??[])
            {
                var item=created.Items[(int)value.ClientHandle-1];
                Require(value.MonitoredItemId==item.MonitoredItemId && (OpcUaSubscriptionContracts.Status(item.StatusCode)&0x80000000)==0 &&
                    value.Value.DataType==item.Metadata.DataType && value.Value.ValueRank==item.Metadata.ValueRank &&
                    JsonElement.DeepEquals(JsonSerializer.SerializeToElement(value.Value.ArrayDimensions),JsonSerializer.SerializeToElement(item.Metadata.ArrayDimensions)));
            }
            serverStatus|=e.StatusChanges is {Length:>0};
            if(e.Kind=="gap") {Require(pendingGap is null && e.GapFrom==expected);pendingGap=e;gaps.Add((e.GapFrom!.Value,e.MissingCount));}
            else if(e.Kind=="publish")
            {
                Require(!recovering);
                ulong delta=((ulong)e.SequenceNumber!.Value+uint.MaxValue-expected)%uint.MaxValue;
                bool ahead=delta<0x80000000UL;
                Require(ahead || !e.KeepAlive && (seen.Contains(e.SequenceNumber.Value) || InGap(e.SequenceNumber.Value)));
                Require(ahead && delta>0 ? pendingGap is not null && pendingGap.GapTo==OpcUaRuntimeSubscriptions.Previous(e.SequenceNumber.Value) && (ulong)pendingGap.MissingCount==delta : pendingGap is null);
                pendingGap=null;
                Require(e.AcknowledgedSequenceNumbers!.Distinct().Count()==e.AcknowledgedSequenceNumbers!.Length);
                foreach(uint ack in e.AcknowledgedSequenceNumbers)Require(committedSeen.Contains(ack));
                serverStatus|=e.StatusChanges is {Length:>0};
                if(!e.KeepAlive)Require(e.Duplicate==!seen.Add(e.SequenceNumber.Value));
                if(ahead)expected=e.KeepAlive?e.SequenceNumber.Value:OpcUaRuntimeSubscriptions.Next(e.SequenceNumber.Value);
            }
            else
            {
                if(recovering)
                {
                    Require(e.RequestedSequenceNumber==expected && pendingGap is {MissingCount:1} && pendingGap.GapFrom==expected && pendingGap.GapTo==expected);
                    pendingGap=null;expected=OpcUaRuntimeSubscriptions.Next(expected);
                }
                Require(pendingGap is null && ++republish<=plan.Request.MaximumRepublish && InGap(e.RequestedSequenceNumber!.Value));
                Require(e.Duplicate==!seen.Add(e.SequenceNumber!.Value));
            }
        }
        else if(e.Kind=="failure")
        {
            if(e.ErrorCode=="UaRepublishUnavailable")
            {
                Require(created is not null && reconnect.Healthy && !reconnect.Pending && e.SubscriptionId==created.SubscriptionId && ++republish<=plan.Request.MaximumRepublish &&
                    (recovering ? e.RequestedSequenceNumber==expected && pendingGap is null : InGap(e.RequestedSequenceNumber!.Value)));
                if(recovering){recoveryProbes++;recovering=false;}
            }
            else failed=true;
        }
        else if(e.Kind=="deleted")
        {
            Require(created is null ? failed : e.SubscriptionId==created.SubscriptionId);
            deleted=true;Cleanup=e.StatusCode=="0x00000000";
        }
        Rows++;Elapsed=row.ElapsedMilliseconds;Notifications+=OpcUaSubscriptionContracts.Count(e);Duplicates+=e.Duplicate?1:0;Missing+=e.MissingCount;
        Require(Notifications<=plan.Request.MaximumNotifications);
    }
    private bool InGap(uint sequence)=>gaps.Any(g=>((ulong)sequence+uint.MaxValue-g.From)%uint.MaxValue<(ulong)g.Count);
}
