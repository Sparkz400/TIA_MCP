using Opc.Ua;
using TiaMcp.Core;
using TiaMcp.Core.PhysicalRuntime;

namespace TiaMcp.OpcUa.Runtime;

internal sealed partial class OpcUaRuntimeClient
{
    /// <summary>The S7-1500 server's own namespace; user-program server methods (OPC_UA_ServerMethodPre/Post instances) live here.</summary>
    internal const string PlcNamespace = "http://www.siemens.com/simatic-s7-opcua";
    private static readonly BuiltInType[] CallTypes = [BuiltInType.Boolean, BuiltInType.SByte, BuiltInType.Byte, BuiltInType.Int16, BuiltInType.UInt16,
        BuiltInType.Int32, BuiltInType.UInt32, BuiltInType.Int64, BuiltInType.UInt64, BuiltInType.Float, BuiltInType.Double, BuiltInType.String];

    /// <summary>Gated owned-PLCSIM method call: only a Method component of a PLC-namespace object, with scalar inputs typed against its
    /// InputArguments. Refusals before the Call service keep the session usable; a lost response leaves it remoteSessionUnknown.</summary>
    internal async Task<object> Call(string attemptId, OpcUaRuntimeNode objectNode, OpcUaRuntimeNode methodNode,
        IReadOnlyList<OpcUaRuntimeCallArgument> inputs, CancellationToken ct)
    {
        await VerifyIdentity(ct);
        object Refused(string code, object? method = null)
        {
            var refused = new { attemptId, state = "refused", dispatched = false, refusal = code, objectNode, methodNode, method };
            CheckOutput(refused); // Argument names come from the server.
            return refused;
        }
        if (objectNode.NamespaceUri != PlcNamespace || methodNode.NamespaceUri != PlcNamespace) return Refused("UaMethodNotServerMethod");
        NodeId objectId = Resolve(objectNode), methodId = Resolve(methodNode);
        var methods = await Children(objectId, ReferenceTypeIds.HasComponent, NodeClass.Method, ct);
        if (methods is null) return Refused("UaMethodSetLimit");
        if (!methods.Any(r => r.NodeId.ServerIndex == 0 && !r.NodeId.IsAbsolute && (NodeId)r.NodeId == methodId)) return Refused("UaMethodNotServerMethod");
        var properties = await Children(methodId, ReferenceTypeIds.HasProperty, NodeClass.Variable, ct);
        if (properties is null) return Refused("UaMethodSetLimit");
        NodeId? Property(string name) => properties.Where(r => r.BrowseName == new QualifiedName(name, 0) && r.NodeId.ServerIndex == 0 && !r.NodeId.IsAbsolute)
            .Select(r => (NodeId)r.NodeId).SingleOrDefault();
        NodeId? inputProperty = Property("InputArguments"), outputProperty = Property("OutputArguments");
        var read = await Session.ReadAsync(null, 0, TimestampsToReturn.Neither, [
            new ReadValueId { NodeId = methodId, AttributeId = Attributes.Executable }, new ReadValueId { NodeId = methodId, AttributeId = Attributes.UserExecutable },
            new ReadValueId { NodeId = inputProperty ?? methodId, AttributeId = inputProperty is null ? Attributes.NodeClass : Attributes.Value },
            new ReadValueId { NodeId = outputProperty ?? methodId, AttributeId = outputProperty is null ? Attributes.NodeClass : Attributes.Value }], ct);
        if (read.Results.Count != 4) throw Error("UaResponseInvalid");
        var declared = new[] { inputProperty is null ? [] : Arguments(read.Results[2]), outputProperty is null ? [] : Arguments(read.Results[3]) };
        bool executable = read.Results[0].Value is true, userExecutable = read.Results[1].Value is true;
        var method = new { executable, userExecutable,
            inputArguments = declared[0]?.Select(Describe).ToArray(), outputArguments = declared[1]?.Select(Describe).ToArray() };
        if (declared[0] is null || declared[1] is null) return Refused("UaMethodArgumentsUnreadable", method);
        if (!executable || !userExecutable) return Refused("UaMethodNotExecutable", method);
        if (declared[0]!.Any(a => a.ValueRank != ValueRanks.Scalar || !CallTypes.Contains(TypeInfo.GetBuiltInType(a.DataType)))) return Refused("UaMethodArgumentUnsupported", method);
        if (declared[0]!.Length != inputs.Count) return Refused("UaMethodArgumentInvalid", method);
        var arguments = new VariantCollection();
        try
        {
            for (int i = 0; i < inputs.Count; i++) arguments.Add(WriteVariant(TypeInfo.GetBuiltInType(declared[0]![i].DataType).ToString(), inputs[i].Encoding, inputs[i].Value));
        }
        catch (BridgeException e) when (e.Code == "UaWriteValueInvalid") { return Refused("UaMethodArgumentInvalid", method); }
        var response = await Session.CallAsync(null, [new CallMethodRequest { ObjectId = objectId, MethodId = methodId, InputArguments = arguments }], ct);
        if (response.Results.Count != 1) throw Error("UaResponseInvalid");
        var called = response.Results[0];
        if (called.InputArgumentResults.Count is not 0 && called.InputArgumentResults.Count != inputs.Count || called.OutputArguments.Count > 64) throw Error("UaResponseInvalid");
        var outputs = called.OutputArguments.Select((value, i) => {
            var (encoding, data, unsupported) = Scalar(value.Value);
            return new { name = i < declared[1]!.Length ? declared[1]![i].Name : null, builtInType = value.TypeInfo?.BuiltInType.ToString(),
                encoding = unsupported is null ? encoding : null, value = unsupported is null ? data : null, errorCode = unsupported };
        }).ToArray();
        var result = new { attemptId, state = StatusCode.IsGood(called.StatusCode) ? "called" : "methodFailed", dispatched = true, objectNode, methodNode, method,
            statusCode = $"0x{called.StatusCode.Code:x8}", inputArgumentResults = called.InputArgumentResults.Select(s => $"0x{s.Code:x8}").ToArray(), outputs,
            note = "One Call service request; nothing is retried. The method's effect on the program is not read back." };
        CheckOutput(result);
        return result;
    }

    private static object Describe(Argument a) => new { name = a.Name, builtInType = TypeInfo.GetBuiltInType(a.DataType).ToString(), valueRank = a.ValueRank };

    // A declared argument list, or null when it is not a bounded Argument array.
    private static Argument[]? Arguments(DataValue value) =>
        StatusCode.IsGood(value.StatusCode) && value.Value is ExtensionObject[] { Length: <= 64 } list && list.All(e => e?.Body is Argument { Name.Length: <= 256 })
            ? list.Select(e => (Argument)e.Body).ToArray() : null;

    // One bounded browse page; null when the server has more (its continuation is released first).
    private async Task<ReferenceDescriptionCollection?> Children(NodeId node, NodeId referenceType, NodeClass nodeClass, CancellationToken ct)
    {
        var response = await Session.BrowseAsync(null, null, 64, [new BrowseDescription { NodeId = node, BrowseDirection = BrowseDirection.Forward,
            ReferenceTypeId = referenceType, IncludeSubtypes = true, NodeClassMask = (uint)nodeClass, ResultMask = (uint)BrowseResultMask.All }], ct);
        if (response.Results.Count != 1 || response.Results[0].References.Count > 64) throw Error("UaResponseInvalid");
        var page = response.Results[0];
        if (page.ContinuationPoint is { Length: > 0 })
        {
            var release = await Session.BrowseNextAsync(null, true, [page.ContinuationPoint], ct);
            if (release.Results.Count != 1 || !StatusCode.IsGood(release.Results[0].StatusCode)) throw Error("UaContinuationCleanupUnknown");
            return null;
        }
        return StatusCode.IsBad(page.StatusCode) ? [] : page.References;
    }

    /// <summary>HistoryRead boundary probe, not a history service: the server's history capabilities, the node's history access
    /// attributes and one bounded HistoryReadRaw whose status (never its values) is returned. The product returns no history data.</summary>
    internal async Task<object> HistoryProbe(string attemptId, OpcUaRuntimeNode node, CancellationToken ct)
    {
        var capabilities = await Read(attemptId, [External(VariableIds.HistoryServerCapabilities_AccessHistoryDataCapability)!,
            External(VariableIds.HistoryServerCapabilities_AccessHistoryEventsCapability)!], ct);
        var id = Resolve(node);
        var attributes = await Session.ReadAsync(null, 0, TimestampsToReturn.Neither, [new ReadValueId { NodeId = id, AttributeId = Attributes.AccessLevel },
            new ReadValueId { NodeId = id, AttributeId = Attributes.UserAccessLevel }, new ReadValueId { NodeId = id, AttributeId = Attributes.Historizing }], ct);
        if (attributes.Results.Count != 3) throw Error("UaResponseInvalid");
        object Attribute(DataValue v) => new { statusCode = $"0x{v.StatusCode.Code:x8}", value = StatusCode.IsGood(v.StatusCode) ? v.Value is byte b ? (object)b : v.Value as bool? : null };
        var now = DateTime.UtcNow;
        var details = new ExtensionObject(new ReadRawModifiedDetails { IsReadModified = false, StartTime = now.AddHours(-1), EndTime = now, NumValuesPerNode = 1, ReturnBounds = false });
        string? serviceStatus = null, operationStatus = null;
        int? values = null;
        bool? released = null;
        try
        {
            var response = await Session.HistoryReadAsync(null, details, TimestampsToReturn.Both, false, [new HistoryReadValueId { NodeId = id }], ct);
            if (response.Results.Count != 1) throw Error("UaResponseInvalid");
            var item = response.Results[0];
            operationStatus = $"0x{item.StatusCode.Code:x8}";
            values = item.HistoryData?.Body is HistoryData data ? data.DataValues.Count : null;
            if (item.ContinuationPoint is { Length: > 0 })
            {
                var release = await Session.HistoryReadAsync(null, details, TimestampsToReturn.Both, true,
                    [new HistoryReadValueId { NodeId = id, ContinuationPoint = item.ContinuationPoint }], ct);
                released = release.Results.Count == 1 && StatusCode.IsGood(release.Results[0].StatusCode);
                if (released != true) throw Error("UaContinuationCleanupUnknown");
            }
        }
        // A service fault the server answered is the evidence; transport, session and timeout failures still end the session.
        catch (ServiceResultException e) when (e.StatusCode is not (StatusCodes.BadConnectionClosed or StatusCodes.BadSecureChannelClosed or
            StatusCodes.BadCommunicationError or StatusCodes.BadNoCommunication or StatusCodes.BadNotConnected or StatusCodes.BadTimeout or
            StatusCodes.BadRequestTimeout or StatusCodes.BadRequestCancelledByClient or StatusCodes.BadSessionIdInvalid or StatusCodes.BadSessionClosed or
            StatusCodes.BadSessionNotActivated or StatusCodes.BadEncodingLimitsExceeded or StatusCodes.BadDecodingError))
        { serviceStatus = $"0x{e.StatusCode:x8}"; }
        var result = new { attemptId, node, capabilities = capabilities.Items,
            nodeAttributes = new { accessLevel = Attribute(attributes.Results[0]), userAccessLevel = Attribute(attributes.Results[1]), historizing = Attribute(attributes.Results[2]) },
            historyReadRaw = new { serviceStatusCode = serviceStatus, statusCode = operationStatus, valueCount = values, continuationReleased = released },
            product = new { historyRead = "refused", code = "UaHistoryReadUnsupported" },
            note = "Boundary probe only: one bounded HistoryReadRaw (last hour, at most one value) whose values are never returned; the S7-1500 server profile has no historical-access facet." };
        CheckOutput(result);
        return result;
    }
}
