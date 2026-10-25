using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Opc.Ua;
using TiaMcp.Core.PhysicalRuntime;

namespace TiaMcp.OpcUa.Runtime;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OpcUaEventSelection(OpcUaRuntimeNode Notifier, ushort MinimumSeverity = 0);
internal sealed class OpcUaEventSelectionConverter : JsonConverter<OpcUaEventSelection>
{
    public override OpcUaEventSelection? Read(ref Utf8JsonReader reader,Type type,JsonSerializerOptions options) =>
        JsonSerializer.Deserialize<OpcUaEventSelection>(ref reader,new JsonSerializerOptions(options){UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow});
    public override void Write(Utf8JsonWriter writer,OpcUaEventSelection value,JsonSerializerOptions options)=>JsonSerializer.Serialize(writer,value,options);
}
public sealed record OpcUaEventMonitoredItem(uint ClientHandle, OpcUaRuntimeNode Notifier, string StatusCode,
    uint MonitoredItemId, double RevisedSamplingInterval, uint RevisedQueueSize, string[]? SelectClauseResults, byte EventNotifier);
public sealed record OpcUaEventField(string Name, string Kind, string? Text, OpcUaRuntimeNode? Node, string? Locale, string? StatusCode);
public sealed record OpcUaEventNotification(uint ClientHandle, uint MonitoredItemId, bool Overflow, OpcUaEventField[] Fields);

internal static class OpcUaEventContracts
{
    internal static readonly string[] Fields=["EventId","EventType","SourceNode","SourceName","Time","ReceiveTime","Message","Severity"];
    private static void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool value)=>OpcUaSubscriptionContracts.Require(value);
    internal static bool IsOverflow(OpcUaRuntimeNode? type)=>type==new OpcUaRuntimeNode(Namespaces.OpcUa,"numeric",ObjectTypeIds.EventQueueOverflowEventType.Identifier.ToString()!);
    internal static void Validate(OpcUaSubscriptionPlan plan,OpcUaSubscriptionEvent e)
    {
        if(e.Kind=="created")
        {
            var item=e.Created?.EventItem;
            Require((item is null)==(plan.Request.Events is null));
            if(item is not null)
            {
                Require(item.Notifier==plan.Request.Events!.Notifier && item.ClientHandle==plan.Request.Nodes.Length+1 && (item.EventNotifier&1)!=0 &&
                    double.IsFinite(item.RevisedSamplingInterval) && item.RevisedSamplingInterval>=0 &&
                    ((OpcUaSubscriptionContracts.Status(item.StatusCode)&0x80000000)!=0 ? item.MonitoredItemId==0 :
                        item.MonitoredItemId>0 && item.RevisedQueueSize>0 && !e.Created!.Items.Any(x=>x.MonitoredItemId==item.MonitoredItemId)));
                Require(item.SelectClauseResults is null || item.SelectClauseResults.Length==Fields.Length);
                foreach(string status in item.SelectClauseResults??[])OpcUaSubscriptionContracts.Status(status);
            }
        }
        Require(e.Events is null || plan.Request.Events is not null && (e.Kind is "publish" or "republish") && e.Events.Length<=1);
        foreach(var notification in e.Events??[])
        {
            Require(notification.ClientHandle==plan.Request.Nodes.Length+1 && notification.MonitoredItemId>0 && notification.Fields.Length==Fields.Length);
            for(int i=0;i<Fields.Length;i++)ValidateField(notification.Fields[i],i);
            Require(notification.Overflow==IsOverflow(notification.Fields[1].Node));
        }
    }
    internal static void ValidateField(OpcUaEventField field,int index)
    {
        Require(field is not null && field.Name==Fields[index] && (field.Text is null || field.Text.Length<=65536) && (field.Locale is null || field.Locale.Length<=128));
        if(field.Kind!="value")
        {
            Require(index is not (0 or 1 or 5) && field.Text is null && field.Node is null && field.Locale is null);
            if(field.Kind=="status")Require((OpcUaSubscriptionContracts.Status(field.StatusCode)&0xc0000000)!=0);
            else Require(field.Kind=="null" && field.StatusCode is null);
            return;
        }
        Require(field.StatusCode is null && (index==6 || field.Locale is null));
        if(index is 1 or 2) {Require(field.Node is not null && field.Text is null);OpcUaRuntimeClient.ValidateNode(field.Node);return;}
        Require(field.Node is null && (field.Text is not null || index==6));
        if(index==0)
        {
            byte[] bytes;
            try {bytes=Convert.FromBase64String(field.Text!);}catch(FormatException){throw OpcUaRuntimeClient.Error("UaSubscriptionInvalid");}
            Require(bytes.Length is >=1 and <=4096 && Convert.ToBase64String(bytes)==field.Text);
        }
        if(index is 4 or 5)Require(DateTime.TryParseExact(field.Text,"O",CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out var date) && date.Kind==DateTimeKind.Utc && date.ToString("O")==field.Text);
        if(index==7)Require(ushort.TryParse(field.Text,NumberStyles.None,CultureInfo.InvariantCulture,out var severity) && severity.ToString(CultureInfo.InvariantCulture)==field.Text);
    }
}
