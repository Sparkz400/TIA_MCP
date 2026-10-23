using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Opc.Ua;
using Opc.Ua.Server;
using TiaMcp.Core.PhysicalRuntime;

namespace TiaMcp.OpcUa.Runtime.Tests;

internal sealed partial class FixtureServer : StandardServer
{
    internal const string Namespace = "urn:tiamcp:qualification:synthetic";
    internal const string Application = "urn:tiamcp:qualification:server";
    internal static readonly DateTime SourceTime = new(2026, 10, 19, 12, 34, 56, DateTimeKind.Utc);
    internal static readonly DateTime ServerTime = SourceTime.AddMilliseconds(123);
    internal readonly ConcurrentQueue<object> Audit = new();
    internal string Mode = "normal";
    internal int ReadDelayMs;
    private int activeReads;
    internal int ActiveReads => Volatile.Read(ref activeReads);
    internal sealed class ReadBoundary
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal uint RequestHandle;
        internal DateTimeOffset EnteredAt, CompletedAt;
    }
    private ReadBoundary? nextValueRead;
    internal ReadBoundary HoldNextValueRead()
    {
        var boundary = new ReadBoundary();
        if (Interlocked.CompareExchange(ref nextValueRead,boundary,null) is not null)
            throw new InvalidOperationException("An original fixture read is already armed.");
        return boundary;
    }
    internal int NextDelayMs;
    internal readonly TaskCompletionSource NextEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal X509Certificate2? AdvertisedCertificate;
    internal int SessionCount => ServerInternal.SessionManager.GetSessions().Count;
    internal readonly TaskCompletionSource ReadEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal string Endpoint { get; private set; } = "";
    internal X509Certificate2 Certificate { get; private set; } = null!;
    internal OpcUaRuntimeSelection Selection => new(Endpoint, Application, Convert.ToHexStringLower(SHA256.HashData(Certificate.RawData)));
    internal static OpcUaRuntimeNode Node(string name) => new(Namespace, "string", name);
    internal static readonly Dictionary<string, (NodeId Type, object Value)> Values = new() {
        ["int64"] = (DataTypeIds.Int64, long.MinValue), ["uint64"] = (DataTypeIds.UInt64, ulong.MaxValue),
        ["double"] = (DataTypeIds.Double, 0.125d), ["bool"] = (DataTypeIds.Boolean, true),
        ["text"] = (DataTypeIds.String, "synthetic local value"), ["bad"] = (DataTypeIds.Int32, 7),
        ["uncertain"] = (DataTypeIds.Int32, 42), ["missingTimes"] = (DataTypeIds.Int32, 43),
        ["array"] = (DataTypeIds.Int32, new int[] { 1, 2, 3 }), ["denied"] = (DataTypeIds.Int32, 44),
        ["byteArray"] = (DataTypeIds.Byte, new byte[] { 1, 2, 3 }), ["byteString"] = (DataTypeIds.ByteString, new byte[] { 1, 2, 3 }),
        ["minimumDate"] = (DataTypeIds.DateTime, DateTime.MinValue),
        ["structure"] = (DataTypeIds.Argument, new ExtensionObject(new Argument { Name = "synthetic" }))
    };

    internal async Task StartFixture(int port, string directory, string mode = "normal")
    {
        Mode = mode; Endpoint = $"opc.tcp://127.0.0.1:{port}/";
        var builder = CertificateFactory.CreateCertificate(Application, "Local UA qualification", "CN=Local UA qualification",
            mode == "wrongHost" ? ["wrong.invalid"] : ["localhost", "127.0.0.1", System.Net.Dns.GetHostName()]);
        if (mode == "expired") builder.SetNotBefore(DateTime.UtcNow.AddDays(-20)).SetNotAfter(DateTime.UtcNow.AddDays(-10));
        Certificate = builder.SetRSAKeySize(2048).CreateForRSA();
        var configuration = new ApplicationConfiguration {
            ApplicationName = "Local UA qualification", ApplicationUri = Application, ProductUri = "urn:tiamcp:qualification",
            ApplicationType = ApplicationType.Server,
            SecurityConfiguration = new SecurityConfiguration {
                ApplicationCertificate = new CertificateIdentifier(Certificate), AutoAcceptUntrustedCertificates = false,
                RejectSHA1SignedCertificates = true, MinimumCertificateKeySize = 2048,
                TrustedPeerCertificates = new CertificateTrustList { StoreType = CertificateStoreType.Directory, StorePath = Path.Combine(directory, "trusted") },
                TrustedIssuerCertificates = new CertificateTrustList { StoreType = CertificateStoreType.Directory, StorePath = Path.Combine(directory, "issuers") },
                RejectedCertificateStore = new CertificateTrustList { StoreType = CertificateStoreType.Directory, StorePath = Path.Combine(directory, "rejected") }
            },
            TransportQuotas = new TransportQuotas { OperationTimeout = 10_000, MaxMessageSize = 1_048_576, MaxStringLength = 16_384,
                MaxByteStringLength = 65_536, MaxArrayLength = 4096 },
            ServerConfiguration = new ServerConfiguration {
                BaseAddresses = [Endpoint], SecurityPolicies = [
                    new ServerSecurityPolicy { SecurityMode = MessageSecurityMode.SignAndEncrypt, SecurityPolicyUri = SecurityPolicies.Basic256Sha256 }],
                UserTokenPolicies = [
                    new UserTokenPolicy { PolicyId = "anonymous", TokenType = UserTokenType.Anonymous },
                    new UserTokenPolicy { PolicyId = "username", TokenType = UserTokenType.UserName, SecurityPolicyUri = SecurityPolicies.Basic256Sha256 }],
                MaxSessionCount = 16, MinSessionTimeout = 1000, MaxSessionTimeout = 60_000,
                MaxBrowseContinuationPoints = 16,
                OperationLimits = new OperationLimits { MaxNodesPerBrowse = 32, MaxNodesPerRead = 256 }
            }
        };
        await configuration.ValidateAsync(ApplicationType.Server);
        // Test server only: accept the generated client certificates, never part of production trust.
        configuration.CertificateValidator.CertificateValidation += (_, e) =>
            e.Accept = e.Error.StatusCode == StatusCodes.BadCertificateUntrusted;
        await StartAsync(configuration);
        var listeners = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Where(x => x.Port == port).ToArray();
        if (listeners.Length == 0 || listeners.Any(x => !System.Net.IPAddress.IsLoopback(x.Address)))
        {
            await StopFixture();
            throw new InvalidOperationException("Fixture did not bind exclusively to loopback.");
        }
    }
    protected override MasterNodeManager CreateMasterNodeManager(IServerInternal server, ApplicationConfiguration configuration) =>
        new(server, configuration, null, new FixtureNodes(server, configuration));
    protected override void OnServerStarted(IServerInternal server)
    {
        base.OnServerStarted(server);
        server.SessionManager.ImpersonateUser += (_, e) => {
            if (e.NewIdentity is UserNameIdentityToken token)
            {
                if (token.UserName != "reader" || System.Text.Encoding.UTF8.GetString(token.DecryptedPassword) != "fixture-only-secret")
                    e.IdentityValidationError = new ServiceResult(StatusCodes.BadUserAccessDenied);
                else e.Identity = new UserIdentity(token);
            }
            if (Mode == "denyLogin") e.IdentityValidationError = new ServiceResult(StatusCodes.BadUserAccessDenied);
        };
    }
    protected override OperationContext ValidateRequest(SecureChannelContext channel, RequestHeader header, RequestType type)
    {
        Audit.Enqueue(new { operation = type.ToString(), at = DateTimeOffset.UtcNow, requestHandle = header.RequestHandle });
        if (Mode == "stale" && type == RequestType.Read) throw new ServiceResultException(StatusCodes.BadSessionIdInvalid);
        return base.ValidateRequest(channel, header, type);
    }
    public override async Task<GetEndpointsResponse> GetEndpointsAsync(SecureChannelContext channel, RequestHeader header,
        string endpointUrl, StringCollection locales, StringCollection profiles, CancellationToken ct)
    {
        Audit.Enqueue(new { operation = "GetEndpoints", at = DateTimeOffset.UtcNow });
        var result = await base.GetEndpointsAsync(channel, header, endpointUrl, locales, profiles, ct);
        if (Mode == "wrongApplication")
            foreach (var endpoint in result.Endpoints) endpoint.Server.ApplicationUri = "urn:wrong:application";
        if (AdvertisedCertificate is not null)
            foreach (var endpoint in result.Endpoints) endpoint.ServerCertificate = AdvertisedCertificate.RawData;
        return result;
    }
    public override Task<BrowseResponse> BrowseAsync(SecureChannelContext channel, RequestHeader header, ViewDescription view,
        uint maximum, BrowseDescriptionCollection nodes, CancellationToken ct) =>
        base.BrowseAsync(channel, header, view, Math.Min(maximum, 3), nodes, ct);
    public override async Task<BrowseNextResponse> BrowseNextAsync(SecureChannelContext channel, RequestHeader header,
        bool releaseContinuationPoints, ByteStringCollection continuationPoints, CancellationToken ct)
    {
        Audit.Enqueue(new { operation = "Continuation", release = releaseContinuationPoints, count = continuationPoints.Count,
            at = DateTimeOffset.UtcNow });
        if (releaseContinuationPoints && Mode == "releaseDenied") throw new ServiceResultException(StatusCodes.BadNoContinuationPoints);
        if (!releaseContinuationPoints && NextDelayMs > 0) { NextEntered.TrySetResult(); await Task.Delay(NextDelayMs, ct); }
        return await base.BrowseNextAsync(channel, header, releaseContinuationPoints, continuationPoints, ct);
    }
    public override async Task<ReadResponse> ReadAsync(SecureChannelContext channel, RequestHeader header, double age,
        TimestampsToReturn timestamps, ReadValueIdCollection nodes, CancellationToken ct)
    {
        Interlocked.Increment(ref activeReads);
        ReadBoundary? boundary = null;
        try
        {
        if (nodes.Any(x => x.NodeId.NamespaceIndex > 1 && x.AttributeId == Attributes.Value))
            boundary = Interlocked.Exchange(ref nextValueRead,null);
        if (boundary is not null)
        {
            boundary.RequestHandle = header.RequestHandle; boundary.EnteredAt = DateTimeOffset.UtcNow;
            boundary.Entered.TrySetResult();
            // Model an already accepted remote request whose completion is independent of
            // client cancellation. The exact test-owned request has a bounded release.
            await boundary.Release.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        if (ReadDelayMs > 0 && nodes.Any(x => x.NodeId.NamespaceIndex > 1))
        {
            ReadEntered.TrySetResult();
            await Task.Delay(ReadDelayMs, ct);
        }
        var result = await base.ReadAsync(channel, header, age, timestamps, nodes, ct);
        for (int i = 0; i < nodes.Count; i++)
        {
            var request = nodes[i];
            if (Mode == "changedNamespace" && request.NodeId == VariableIds.Server_NamespaceArray)
                result.Results[i].Value = new[] { Opc.Ua.Namespaces.OpcUa, Application, "urn:changed:namespace" };
            if (Mode == "changedStart" && request.NodeId == VariableIds.Server_ServerStatus_StartTime)
                result.Results[i].Value = DateTime.UtcNow.AddDays(-5);
            if (request.NodeId.NamespaceIndex <= 1 || request.AttributeId != Attributes.Value) continue;
            string name = Convert.ToString(request.NodeId.Identifier)!;
            if (!Values.ContainsKey(name)) continue;
            var value = result.Results[i];
            if (name == "byteArray") value.WrappedValue = new Variant(new byte[] { 1, 2, 3 }, new TypeInfo(BuiltInType.Byte, ValueRanks.OneDimension));
            if (Mode == "secretEcho" && name == "text") value.Value = "prefix fixture-only-secret suffix";
            value.SourceTimestamp = name == "missingTimes" ? DateTime.MinValue : SourceTime;
            value.ServerTimestamp = name == "missingTimes" ? DateTime.MinValue : ServerTime;
            value.SourcePicoseconds = name == "missingTimes" ? (ushort)0 : (ushort)12;
            value.ServerPicoseconds = name == "missingTimes" ? (ushort)0 : (ushort)34;
            if (name == "bad") value.StatusCode = StatusCodes.BadSensorFailure;
            if (name == "uncertain") value.StatusCode = StatusCodes.UncertainInitialValue;
            if (name == "denied") value.StatusCode = StatusCodes.BadUserAccessDenied;
        }
        return result;
        }
        finally
        {
            Interlocked.Decrement(ref activeReads);
            if (boundary is not null)
            {
                boundary.CompletedAt = DateTimeOffset.UtcNow;
                boundary.Completed.TrySetResult();
            }
        }
    }
    internal async Task StopFixture()
    {
        await StopAsync(); Dispose(); Certificate.Dispose(); AdvertisedCertificate?.Dispose();
    }
    private sealed class FixtureNodes(IServerInternal server, ApplicationConfiguration configuration)
        : CustomNodeManager2(server, configuration, Namespace)
    {
        public override void CreateAddressSpace(IDictionary<NodeId, IList<IReference>> external)
        {
            var folder = new FolderState(null) { NodeId = new NodeId("fixture", NamespaceIndex), BrowseName = new QualifiedName("fixture", NamespaceIndex),
                DisplayName = "Synthetic qualification", TypeDefinitionId = ObjectTypeIds.FolderType, ReferenceTypeId = ReferenceTypeIds.Organizes };
            folder.AddReference(ReferenceTypeIds.Organizes, true, ObjectIds.ObjectsFolder);
            if (!external.TryGetValue(ObjectIds.ObjectsFolder, out var refs)) external[ObjectIds.ObjectsFolder] = refs = new List<IReference>();
            refs.Add(new NodeStateReference(ReferenceTypeIds.Organizes, false, folder.NodeId));
            foreach (var pair in Values)
            {
                var variable = new BaseDataVariableState(folder) { NodeId = new NodeId(pair.Key, NamespaceIndex),
                    BrowseName = new QualifiedName(pair.Key, NamespaceIndex), DisplayName = pair.Key,
                    TypeDefinitionId = VariableTypeIds.BaseDataVariableType, ReferenceTypeId = ReferenceTypeIds.Organizes,
                    DataType = pair.Value.Type, ValueRank = pair.Value.Value is Array && pair.Value.Type != DataTypeIds.ByteString ? ValueRanks.OneDimension : ValueRanks.Scalar,
                    ArrayDimensions = pair.Value.Value is Array a && pair.Value.Type != DataTypeIds.ByteString ? new ReadOnlyList<uint>(new[] { (uint)a.Length }) : null,
                    Value = pair.Value.Value, AccessLevel = AccessLevels.CurrentRead, UserAccessLevel = AccessLevels.CurrentRead,
                    StatusCode = StatusCodes.Good, Timestamp = SourceTime };
                folder.AddChild(variable);
            }
            AddPredefinedNode(SystemContext, folder);
        }
    }
}
