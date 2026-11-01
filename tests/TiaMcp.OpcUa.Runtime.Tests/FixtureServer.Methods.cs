using Opc.Ua;
using Opc.Ua.Server;
using TiaMcp.Core.PhysicalRuntime;

namespace TiaMcp.OpcUa.Runtime.Tests;

// A synthetic stand-in for an S7-1500 user-program server method (instance DB object with one Method) and one alarm condition.
internal sealed partial class FixtureServer
{
    internal const string PlcNamespace = "http://www.siemens.com/simatic-s7-opcua";
    internal static OpcUaRuntimeNode PlcNode(string name) => new(PlcNamespace, "string", name);
    internal int MethodCalls => Volatile.Read(ref fixtureNodes!.MethodCalls);
    internal int CallDelayMs { set => fixtureNodes!.CallDelayMs = value; }
    internal void SetAlarm(bool active, bool acknowledged) => fixtureNodes!.SetAlarm(active, acknowledged);
    private sealed partial class FixtureNodes
    {
        internal int MethodCalls, CallDelayMs;
        private FolderState? alarms;
        private AlarmConditionState? alarm;
        private void CreateMethodsAndAlarm(IList<IReference> objects)
        {
            ushort plc = NamespaceIndexes[1];
            BaseObjectState Instance(string name)
            {
                var instance = new BaseObjectState(null) { NodeId = new NodeId('"' + name + '"', plc), BrowseName = new QualifiedName(name, plc), DisplayName = name,
                    TypeDefinitionId = ObjectTypeIds.BaseObjectType, ReferenceTypeId = ReferenceTypeIds.Organizes };
                instance.AddReference(ReferenceTypeIds.Organizes, true, ObjectIds.ObjectsFolder);
                objects.Add(new NodeStateReference(ReferenceTypeIds.Organizes, false, instance.NodeId));
                return instance;
            }
            PropertyState<Argument[]> Arguments(MethodState method, string name, params (string Name, NodeId Type)[] arguments) => new(method) {
                NodeId = new NodeId(method.NodeId.Identifier + "." + name, plc), BrowseName = name, DisplayName = name, TypeDefinitionId = VariableTypeIds.PropertyType,
                ReferenceTypeId = ReferenceTypeIds.HasProperty, DataType = DataTypeIds.Argument, ValueRank = ValueRanks.OneDimension,
                Value = arguments.Select(a => new Argument { Name = a.Name, DataType = a.Type, ValueRank = ValueRanks.Scalar }).ToArray() };
            MethodState Method(BaseObjectState owner, string name, bool executable, (string, NodeId)[] inputs, (string, NodeId)[] outputs)
            {
                var method = new MethodState(owner) { NodeId = new NodeId(owner.NodeId.Identifier + "." + name, plc), BrowseName = new QualifiedName(name, plc),
                    DisplayName = name, ReferenceTypeId = ReferenceTypeIds.HasComponent, Executable = executable, UserExecutable = executable };
                method.InputArguments = Arguments(method, BrowseNames.InputArguments, inputs);
                method.OutputArguments = Arguments(method, BrowseNames.OutputArguments, outputs);
                owner.AddChild(method);
                return method;
            }
            // OutInt := InInt + 1 and OutBool := NOT InBool, like the qualification runner's FB; an Int overflow is the method's own Bad result.
            var server = Instance("ServerMethod_DB");
            Method(server, "Method", true, [("InInt", DataTypeIds.Int16), ("InBool", DataTypeIds.Boolean)], [("OutInt", DataTypeIds.Int16), ("OutBool", DataTypeIds.Boolean)])
                .OnCallMethod = (_, _, inputs, outputs) =>
                {
                    if (CallDelayMs > 0) Thread.Sleep(CallDelayMs);
                    Interlocked.Increment(ref MethodCalls);
                    if ((short)inputs[0] == short.MaxValue) return new ServiceResult(StatusCodes.BadOutOfRange);
                    outputs[0] = (short)((short)inputs[0] + 1); outputs[1] = !(bool)inputs[1];
                    return ServiceResult.Good;
                };
            // A scalar type with an array rank: refused like any non-scalar argument.
            Method(server, "Vector", true, [("Values", DataTypeIds.Int16)], []).InputArguments.Value[0].ValueRank = ValueRanks.OneDimension;
            var limits = Instance("Limits_DB");
            Method(limits, "Disabled", false, [("InInt", DataTypeIds.Int16)], []).OnCallMethod = (_, _, _, _) => { Interlocked.Increment(ref MethodCalls); return ServiceResult.Good; };
            Method(limits, "Dated", true, [("At", DataTypeIds.DateTime)], []).OnCallMethod = (_, _, _, _) => { Interlocked.Increment(ref MethodCalls); return ServiceResult.Good; };
            foreach (var instance in new[] { server, limits }) AddPredefinedNode(SystemContext, instance);

            alarms = new FolderState(null) { EventNotifier = EventNotifiers.SubscribeToEvents, NodeId = new NodeId("alarms", NamespaceIndex),
                BrowseName = new QualifiedName("alarms", NamespaceIndex), DisplayName = "Synthetic alarms", TypeDefinitionId = ObjectTypeIds.FolderType,
                ReferenceTypeId = ReferenceTypeIds.Organizes };
            alarms.AddReference(ReferenceTypeIds.Organizes, true, ObjectIds.ObjectsFolder);
            objects.Add(new NodeStateReference(ReferenceTypeIds.Organizes, false, alarms.NodeId));
            alarm = new AlarmConditionState(Server!.Telemetry, alarms);
            alarm.Create(SystemContext, new NodeId("alarms.Overtemperature", NamespaceIndex), new QualifiedName("Overtemperature", NamespaceIndex), "Overtemperature", true);
            alarm.SourceNode.Value = alarms.NodeId; alarm.SourceName.Value = "Synthetic alarms"; alarm.ConditionName.Value = "Overtemperature";
            alarm.EventType.Value = alarm.TypeDefinitionId; alarm.BranchId.Value = NodeId.Null;
            alarm.SetEnableState(SystemContext, true); alarm.SetAcknowledgedState(SystemContext, true); alarm.SetActiveState(SystemContext, false);
            alarm.SetSeverity(SystemContext, EventSeverity.High); alarm.Retain.Value = false;
            alarm.Message.Value = new LocalizedText("en-US", "Overtemperature <b>");
            alarms.AddChild(alarm);
            alarms.AddNotifier(SystemContext, ReferenceTypeIds.HasCondition, false, alarm);
            AddPredefinedNode(SystemContext, alarms);
            AddRootNotifier(alarms);
        }
        internal void SetAlarm(bool active, bool acknowledged)
        {
            lock (Lock)
            {
                alarm!.SetActiveState(SystemContext, active);
                alarm.SetAcknowledgedState(SystemContext, acknowledged);
                alarm.Retain.Value = active || !acknowledged;
                alarm.EventId.Value = Guid.NewGuid().ToByteArray();
                alarm.Time.Value = alarm.ReceiveTime.Value = DateTime.UtcNow;
                var snapshot = new InstanceStateSnapshot();
                snapshot.Initialize(SystemContext, alarm);
                alarms!.ReportEvent(SystemContext, snapshot);
            }
        }
    }
}
