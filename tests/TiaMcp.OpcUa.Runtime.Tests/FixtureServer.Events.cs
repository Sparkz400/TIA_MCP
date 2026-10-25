using Opc.Ua;

namespace TiaMcp.OpcUa.Runtime.Tests;

internal sealed partial class FixtureServer
{
    internal void EmitEvent(int number,ushort severity=700)
    {
        fixtureNodes!.Emit(number,severity);
        Audit.Enqueue(new {operation="FixtureEventEmitted",number,severity,at=DateTimeOffset.UtcNow});
    }
    private sealed partial class FixtureNodes
    {
        private FolderState? eventSource;
        internal void Emit(int number,ushort severity)
        {
            lock(Lock)
            {
                var e=new DeviceFailureEventState(null);
                e.Initialize(SystemContext,eventSource,(EventSeverity)severity,new LocalizedText("de-DE","Übertemperatur <script>alert(1)</script> "+number));
                e.EventId.Value=System.Text.Encoding.UTF8.GetBytes("fixture-event-"+number);
                e.Time.Value=SourceTime.AddMilliseconds(number);
                e.ReceiveTime.Value=ServerTime.AddMilliseconds(number);
                eventSource!.ReportEvent(SystemContext,e);
            }
        }
    }
    private void AlterEvent(NotificationMessage message)
    {
        foreach(var list in message.NotificationData.Select(x=>x.Body).OfType<EventNotificationList>())
        foreach(var item in list.Events)
        {
            if(Mode=="eventBadHandle")item.ClientHandle=uint.MaxValue;
            if(Mode=="eventBadCount")item.EventFields.RemoveAt(item.EventFields.Count-1);
            if(Mode=="eventBadType")item.EventFields[7]=new Variant("700");
            if(Mode=="eventBadIdentity")item.EventFields[0]=Variant.Null;
            if(Mode=="eventNullMessage")item.EventFields[6]=new Variant(new LocalizedText("de-DE",(string)null!));
            if(Mode=="eventUnavailable") {item.EventFields[6]=new Variant(new StatusCode(StatusCodes.BadUserAccessDenied));item.EventFields[4]=Variant.Null;}
            if(Mode=="eventSecretEcho")item.EventFields[6]=new Variant(new LocalizedText("fixture-only-secret"));
        }
    }
}
