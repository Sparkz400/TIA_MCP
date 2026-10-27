using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Opc.Ua;
using Opc.Ua.Client;
using TiaMcp.Core;
using TiaMcp.Core.PhysicalRuntime;

namespace TiaMcp.OpcUa.Runtime;

/// <summary>One owned, pinned, encrypted UA session. Recovery never creates a replacement session.</summary>
internal sealed partial class OpcUaRuntimeClient : IAsyncDisposable
{
    internal const int TimeoutMs = 10_000;
    internal const int MaxMessageBytes = 1_048_576;
    internal const string Policy = SecurityPolicies.Basic256Sha256;
    private readonly OpcUaRuntimeSelection selection;
    private readonly ApplicationConfiguration configuration;
    private ISession? session;
    private string[]? namespaces;
    private string[]? serverUris;
    private DateTime startTime;
    private byte[]? passwordBytes;
    internal bool PeerVerified { get; private set; }
    internal bool SessionDispatched { get; private set; }
    internal bool Unusable { get; private set; }
    internal bool CanReconnect { get; private set; }
    internal DateTimeOffset LastUsed { get; private set; } = DateTimeOffset.UtcNow;

    internal OpcUaRuntimeClient(OpcUaRuntimeSelection selection, X509Certificate2 clientCertificate, string clientUri, string pkiPath)
    {
        this.selection = selection;
        configuration = new ApplicationConfiguration {
            ApplicationName = "TIA MCP read-only UA", ApplicationUri = clientUri, ApplicationType = ApplicationType.Client,
            ProductUri = "urn:tiamcp:opcua:runtime",
            SecurityConfiguration = new SecurityConfiguration {
                ApplicationCertificate = new CertificateIdentifier(clientCertificate),
                AutoAcceptUntrustedCertificates = false, RejectSHA1SignedCertificates = true,
                MinimumCertificateKeySize = 2048, RejectUnknownRevocationStatus = false,
                TrustedPeerCertificates = new CertificateTrustList { StoreType = CertificateStoreType.Directory, StorePath = Path.Combine(pkiPath, "trusted") },
                TrustedIssuerCertificates = new CertificateTrustList { StoreType = CertificateStoreType.Directory, StorePath = Path.Combine(pkiPath, "issuers") },
                RejectedCertificateStore = new CertificateTrustList { StoreType = CertificateStoreType.Directory, StorePath = Path.Combine(pkiPath, "rejected") }
            },
            TransportQuotas = new TransportQuotas { OperationTimeout = TimeoutMs, MaxMessageSize = MaxMessageBytes,
                MaxStringLength = 16_384, MaxByteStringLength = 65_536, MaxArrayLength = 4096, MaxEncodingNestingLevels = 32,
                ChannelLifetime = 60_000, SecurityTokenLifetime = 60_000 },
            ClientConfiguration = new ClientConfiguration { DefaultSessionTimeout = 60_000, MinSubscriptionLifetime = 0 }
        };
    }

    internal async Task Connect(byte[]? passwordJson, CancellationToken ct)
    {
        await configuration.ValidateAsync(ApplicationType.Client, ct);
        configuration.CertificateValidator.CertificateValidation += (_, e) => {
            // A caller-supplied exact leaf pin is the trust root. Only the untrusted-root error may be waived.
            // Every other chain/time/usage/signature/domain error remains fatal.
            // The SDK presents each nested result separately; accepting this result does not accept inner errors.
            e.Accept = e.Error.StatusCode == StatusCodes.BadCertificateUntrusted && ValidPinnedLeaf(e.Certificate);
        };
        EndpointDescription[] matches;
        using (var discovery = await DiscoveryClient.CreateAsync(configuration, new Uri(selection.Endpoint),
            EndpointConfiguration.Create(configuration), ct: ct))
        {
            var endpoints = await discovery.GetEndpointsAsync(null, ct);
            if (endpoints.Count > 64) throw Error("UaResponseLimit");
            matches = endpoints.Where(e => e.EndpointUrl == selection.Endpoint &&
                e.Server.ApplicationUri == selection.ApplicationUri && e.SecurityMode == MessageSecurityMode.SignAndEncrypt &&
                e.SecurityPolicyUri == Policy && e.TransportProfileUri == Profiles.UaTcpTransport).ToArray();
        }
        if (matches.Length != 1) throw Error("UaEndpointIdentityMismatch");
        var selected = matches[0];
        using (var certificate = CertificateFactory.Create(selected.ServerCertificate))
        {
            if (!ValidPinnedLeaf(certificate)) throw Error("UaCertificateRejected");
            await configuration.CertificateValidator.ValidateAsync(certificate, ct);
            configuration.CertificateValidator.ValidateDomains(certificate, new ConfiguredEndpoint(null, selected));
        }
        var tokenType = selection.Authentication == "anonymous" ? UserTokenType.Anonymous : UserTokenType.UserName;
        var tokens = selected.UserIdentityTokens.Where(t => t.TokenType == tokenType &&
            (string.IsNullOrEmpty(t.SecurityPolicyUri) || t.SecurityPolicyUri == Policy)).ToArray();
        if (tokens.Length != 1) throw Error("UaAuthenticationProfileUnsupported");
        IUserIdentity identity;
        if (tokenType == UserTokenType.Anonymous) identity = new UserIdentity(new AnonymousIdentityToken { PolicyId = tokens[0].PolicyId });
        else
        {
            if (passwordJson is null || passwordJson.Length > 32_768) throw Error("UaCredentialInvalid");
            using var document = JsonDocument.Parse(passwordJson, new JsonDocumentOptions { MaxDepth = 1 });
            if (document.RootElement.ValueKind != JsonValueKind.String) throw Error("UaCredentialInvalid");
            passwordBytes = System.Text.Encoding.UTF8.GetBytes(document.RootElement.GetString()!);
            identity = new UserIdentity(selection.User, passwordBytes);
        }
        SessionDispatched = true;
        session = await new DefaultSessionFactory(null!).CreateAsync(configuration,
            new ConfiguredEndpoint(null, selected, EndpointConfiguration.Create(configuration)), false, true,
            "TIA MCP owned read-only", 60_000, identity, null, ct);
        PeerVerified = true;
        // No background polling/reconnect, automatic subscriptions or session transfer.
        session.KeepAliveInterval = Timeout.Infinite;
        session.TransferSubscriptionsOnReconnect = false;
        session.DeleteSubscriptionsOnClose = true;
        await VerifyIdentity(ct);
    }

    private bool ValidPinnedLeaf(X509Certificate2 certificate)
    {
        if (Convert.ToHexStringLower(SHA256.HashData(certificate.RawData)) != selection.CertificateSha256 ||
            DateTime.UtcNow < certificate.NotBefore.ToUniversalTime() || DateTime.UtcNow > certificate.NotAfter.ToUniversalTime() ||
            !X509Utils.GetApplicationUrisFromCertificate(certificate).SequenceEqual([selection.ApplicationUri])) return false;
        using var rsa = certificate.GetRSAPublicKey();
        if (rsa is null || rsa.KeySize < 2048) return false;
        var key = certificate.Extensions.OfType<X509KeyUsageExtension>().SingleOrDefault();
        var required = X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment | X509KeyUsageFlags.DataEncipherment;
        if (key is null || (key.KeyUsages & required) != required) return false;
        var extended = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().SingleOrDefault();
        return extended is not null && extended.EnhancedKeyUsages.Cast<Oid>().Any(o => o.Value == "1.3.6.1.5.5.7.3.1");
    }

    private async Task VerifyIdentity(CancellationToken ct)
    {
        var response = await Session.ReadAsync(null, 0, TimestampsToReturn.Neither, [
            new ReadValueId { NodeId = VariableIds.Server_NamespaceArray, AttributeId = Attributes.Value },
            new ReadValueId { NodeId = VariableIds.Server_ServerArray, AttributeId = Attributes.Value },
            new ReadValueId { NodeId = VariableIds.Server_ServerStatus_StartTime, AttributeId = Attributes.Value }], ct);
        if (response.Results.Count != 3 || response.Results.Any(v => !StatusCode.IsGood(v.StatusCode)) ||
            response.Results[0].Value is not string[] ns || ns.Length is < 1 or > 256 ||
            ns.Distinct(StringComparer.Ordinal).Count() != ns.Length || ns.Any(x => x.Length > 4096) ||
            response.Results[1].Value is not string[] servers || servers.Length is < 1 or > 256 ||
            servers[0] != selection.ApplicationUri || response.Results[2].Value is not DateTime started)
            throw Error("UaSessionIdentityMismatch");
        if (namespaces is not null && (!namespaces.SequenceEqual(ns) || !serverUris!.SequenceEqual(servers) || startTime != started))
            throw Error("UaSessionIdentityChanged");
        namespaces = ns; serverUris = servers; startTime = started; LastUsed = DateTimeOffset.UtcNow;
    }

    internal async Task<OpcUaRuntimeReadResult> Read(string attemptId, IReadOnlyList<OpcUaRuntimeNode> nodes, CancellationToken ct)
    {
        if (nodes is null || nodes.Count is < 1 or > 64) throw Error("UaRequestLimit");
        foreach (var node in nodes) ValidateNode(node);
        var started = DateTimeOffset.UtcNow;
        await VerifyIdentity(ct);
        var requests = new ReadValueIdCollection();
        foreach (var node in nodes)
        {
            var id = Resolve(node);
            foreach (var attribute in new[] { Attributes.Value, Attributes.DataType, Attributes.ValueRank, Attributes.ArrayDimensions })
                requests.Add(new ReadValueId { NodeId = id, AttributeId = attribute });
        }
        var response = await Session.ReadAsync(null, 0, TimestampsToReturn.Both, requests, ct);
        if (response.Results.Count != requests.Count) throw Error("UaResponseInvalid");
        var values = new List<OpcUaRuntimeValue>();
        for (int i = 0; i < nodes.Count; i++)
        {
            var value = response.Results[i * 4]; var type = response.Results[i * 4 + 1]; var rank = response.Results[i * 4 + 2];
            var dimensions = response.Results[i * 4 + 3];
            values.Add(DecodeValue(nodes[i],value,type,rank,dimensions));
        }
        var result = new OpcUaRuntimeReadResult(attemptId, started, DateTimeOffset.UtcNow, values);
        CheckOutput(result);
        return result;
    }

    private OpcUaRuntimeValue DecodeValue(OpcUaRuntimeNode node, DataValue value, DataValue type, DataValue rank, DataValue dimensions)
    {
            string quality = StatusCode.IsBad(value.StatusCode) ? "Bad" : StatusCode.IsUncertain(value.StatusCode) ? "Uncertain" : "Good";
            var typeNode = StatusCode.IsGood(type.StatusCode) && type.Value is NodeId dataType ? External(dataType) : null;
            int? valueRank = StatusCode.IsGood(rank.StatusCode) && rank.Value is int r ? r : null;
            var builtIn = value.WrappedValue.TypeInfo?.BuiltInType.ToString();
            var declaredDimensions = StatusCode.IsGood(dimensions.StatusCode) ? dimensions.Value as uint[] : null;
            bool array = value.WrappedValue.TypeInfo?.ValueRank is >= 0 || value.Value is Matrix;
            (string? encoding, object? data, string? unsupported) = array
                ? ArrayValue(value.WrappedValue, valueRank, declaredDimensions) : Scalar(value.Value);
            if (!array && valueRank is >= 0 && value.Value is not null) (encoding, data, unsupported) = (null, null, "UaArrayShapeMismatch");
            string outcome = quality == "Bad" ? "itemError" : typeNode is null || valueRank is null ? "metadataError" :
                unsupported is not null ? "unsupported" : "value";
            return new(node, outcome, builtIn, typeNode, valueRank,
                declaredDimensions,
                $"0x{value.StatusCode.Code:x8}", quality, Time(value.SourceTimestamp), Time(value.ServerTimestamp),
                value.SourcePicoseconds, value.ServerPicoseconds, outcome == "value" ? encoding : null,
                outcome == "value" ? data : null, outcome == "value" ? null : unsupported ?? (outcome == "itemError" ? "UaItemBad" : "UaMetadataUnavailable"));
    }

    internal async Task<OpcUaRuntimeBrowseResult> Browse(string attemptId, OpcUaRuntimeNode node, int maximum, CancellationToken ct)
    {
        ValidateNode(node);
        if (maximum is < 1 or > 256) throw Error("UaRequestLimit");
        await VerifyIdentity(ct);
        byte[]? continuation = null;
        var references = new List<OpcUaRuntimeReference>();
        bool truncated = false, released = false;
        string? operationFailure = null;
        try
        {
            var response = await Session.BrowseAsync(null, null, (uint)Math.Min(maximum, 64), [
                new BrowseDescription { NodeId = Resolve(node), BrowseDirection = BrowseDirection.Forward,
                    ReferenceTypeId = ReferenceTypeIds.HierarchicalReferences, IncludeSubtypes = true,
                    NodeClassMask = 0, ResultMask = (uint)BrowseResultMask.All }], ct);
            var results = response.Results;
            int pages = 0;
            while (true)
            {
                if (results.Count != 1) throw Error("UaResponseInvalid");
                var page = results[0];
                continuation = page.ContinuationPoint;
                if (StatusCode.IsBad(page.StatusCode)) throw Error("UaBrowseDenied");
                if (page.References.Count > 256) throw Error("UaResponseLimit");
                foreach (var item in page.References)
                {
                    if (references.Count == maximum) { truncated = true; break; }
                    var local = item.NodeId.ServerIndex == 0 && !item.NodeId.IsAbsolute ? External((NodeId)item.NodeId) : null;
                    references.Add(new(local, item.NodeClass.ToString(), item.BrowseName.Name,
                        item.BrowseName.NamespaceIndex < namespaces!.Length ? namespaces[item.BrowseName.NamespaceIndex] : null,
                        item.DisplayName.Text, item.IsForward, External(item.ReferenceTypeId),
                        item.TypeDefinition.ServerIndex == 0 && !item.TypeDefinition.IsAbsolute ? External((NodeId)item.TypeDefinition) : null,
                        local is null ? "externalOrUnknownNamespace" : null));
                }
                if (continuation is not { Length: > 0 }) break;
                if (continuation.Length > 65_536) throw Error("UaResponseLimit");
                if (references.Count >= maximum || ++pages >= 16) { truncated = true; break; }
                var next = await Session.BrowseNextAsync(null, false, [continuation], ct);
                continuation = null;
                results = next.Results;
            }
        }
        catch (Exception e)
        {
            operationFailure = ct.IsCancellationRequested || e is OperationCanceledException ? "UaCancelled" : e is BridgeException b ? b.Code : "UaBrowseFailed";
            throw;
        }
        finally
        {
            if (continuation is { Length: > 0 })
            {
                using var cleanup = new CancellationTokenSource(TimeoutMs);
                try
                {
                    var release = await Session.BrowseNextAsync(null, true, [continuation], cleanup.Token);
                    released = release.Results.Count == 1 && StatusCode.IsGood(release.Results[0].StatusCode);
                    if (!released) throw Error("UaContinuationCleanupUnknown");
                }
                catch { throw new UaContinuationCleanupFailure(operationFailure); }
            }
        }
        var result = new OpcUaRuntimeBrowseResult(attemptId, node, references, truncated, released);
        CheckOutput(result);
        return result;
    }

    private ISession Session => session is { Connected: true } s && !Unusable ? s : throw Error("UaSessionUnavailable");
    internal void Invalidate(bool reconnectable = false) { Unusable = true; CanReconnect = reconnectable && session is not null; }
    /// <summary>Reactivates the original session. Only when the server reports that session gone while this client owns a
    /// subscription does it open one replacement session and call TransferSubscriptions; Transfer is null otherwise.</summary>
    internal async Task<(string? Transfer, string? Status)> Reconnect(CancellationToken ct)
    {
        if(!CanReconnect || session is null) throw Error("UaReconnectUnavailable");
        using var leaf=CertificateFactory.Create(session.ConfiguredEndpoint.Description.ServerCertificate);
        if(!ValidPinnedLeaf(leaf)) throw Error("UaCertificateRejected");
        await configuration.CertificateValidator.ValidateAsync(leaf,ct);
        configuration.CertificateValidator.ValidateDomains(leaf,session.ConfiguredEndpoint);
        var original=session.SessionId;
        try {await session.ReconnectAsync(null,null,ct);}
        catch(ServiceResultException e) when(subscriptionId!=0 && e.StatusCode is StatusCodes.BadSessionIdInvalid or StatusCodes.BadSessionClosed)
        {return await Transfer(ct);}
        session.KeepAliveInterval=Timeout.Infinite;
        if(session.SessionId!=original) throw Error("UaSessionIdentityChanged");
        Unusable=false;
        try {await VerifyIdentity(ct);CanReconnect=false;}
        catch {Unusable=true;throw;}
        return (null,null);
    }
    private async Task<(string?, string?)> Transfer(CancellationToken ct)
    {
        // Same pinned endpoint description, client certificate and user identity; the server checks the identity matches the owner.
        var old=session!;
        session=await new DefaultSessionFactory(null!).CreateAsync(configuration,old.ConfiguredEndpoint,false,true,
            "TIA MCP owned read-only",60_000,old.Identity,null,ct);
        session.KeepAliveInterval=Timeout.Infinite;
        session.TransferSubscriptionsOnReconnect=false;
        session.DeleteSubscriptionsOnClose=true;
        Unusable=false;
        try
        {
            // A restarted server has a new start time: refuse rather than adopt it.
            await VerifyIdentity(ct);
            string? refused;
            try
            {
                var response=await session.TransferSubscriptionsAsync(null,[subscriptionId],false,ct);
                if(response.Results.Count!=1) throw Error("UaResponseInvalid");
                refused=StatusCode.IsGood(response.Results[0].StatusCode) ? null : $"0x{response.Results[0].StatusCode.Code:x8}";
            }
            catch(ServiceResultException e) when(e.StatusCode is not (StatusCodes.BadConnectionClosed or StatusCodes.BadSecureChannelClosed or
                StatusCodes.BadCommunicationError or StatusCodes.BadNoCommunication or StatusCodes.BadNotConnected or StatusCodes.BadTimeout or
                StatusCodes.BadRequestTimeout or StatusCodes.BadRequestCancelledByClient))
            {refused=$"0x{e.StatusCode:x8}";}
            // A refused subscription is no longer owned here; the acquisition creates a new one with a new sequence.
            if(refused is not null) {subscriptionId=0;monitoredItems=[];eventItem=null;}
            CanReconnect=false;
            return (refused is null ? "transferred" : "refused", refused);
        }
        catch {Unusable=true;throw;}
        finally {try {old.Dispose();} catch { }}
    }
    internal async Task<bool> Close()
    {
        if (session is null) { ForgetPassword(); return !SessionDispatched; }
        using var deadline = new CancellationTokenSource(TimeoutMs);
        try
        {
            bool hadSession = session.Connected;
            return StatusCode.IsGood(await session.CloseAsync(TimeoutMs, true, deadline.Token)) && hadSession;
        }
        catch { return false; }
        finally { var old = session; session = null; Unusable = true; try { old.Dispose(); } catch { } ForgetPassword(); }
    }
    public async ValueTask DisposeAsync() { await Close(); }
    private void ForgetPassword() { if (passwordBytes is not null) CryptographicOperations.ZeroMemory(passwordBytes); passwordBytes = null; }
    private void CheckOutput(object value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length > MaxMessageBytes) throw Error("UaResponseLimit");
        if (passwordBytes is { Length: > 0 })
        {
            var escaped = JsonSerializer.SerializeToUtf8Bytes(System.Text.Encoding.UTF8.GetString(passwordBytes));
            if (bytes.AsSpan().IndexOf(passwordBytes) >= 0 || bytes.AsSpan().IndexOf(escaped.AsSpan(1, escaped.Length - 2)) >= 0)
                throw Error("UaSecretEchoRejected");
        }
    }
    private NodeId Resolve(OpcUaRuntimeNode node)
    {
        int index = Array.IndexOf(namespaces!, node.NamespaceUri);
        if (index < 0) throw Error("UaNamespaceUnavailable");
        return node.IdentifierType switch {
            "numeric" => new NodeId(uint.Parse(node.Identifier, CultureInfo.InvariantCulture), (ushort)index),
            "string" => new NodeId(node.Identifier, (ushort)index),
            "guid" => new NodeId(Guid.ParseExact(node.Identifier, "D"), (ushort)index),
            "opaque" => new NodeId(Convert.FromBase64String(node.Identifier), (ushort)index),
            _ => throw Error("UaNodeInvalid")
        };
    }
    private OpcUaRuntimeNode? External(NodeId id) => id.NamespaceIndex >= namespaces!.Length ? null :
        new(namespaces[id.NamespaceIndex], id.IdType.ToString().ToLowerInvariant() == "opaque" ? "opaque" :
            id.IdType.ToString().ToLowerInvariant(), id.Identifier is byte[] bytes ? Convert.ToBase64String(bytes) :
            Convert.ToString(id.Identifier, CultureInfo.InvariantCulture)!);
    internal static void ValidateNode(OpcUaRuntimeNode node)
    {
        if (node is null || string.IsNullOrEmpty(node.NamespaceUri) || node.NamespaceUri.Length > 4096 ||
            !Uri.TryCreate(node.NamespaceUri, UriKind.Absolute, out _) || node.Identifier is null || node.Identifier.Length > 4096)
            throw Error("UaNodeInvalid");
        bool valid = node.IdentifierType switch {
            "numeric" => uint.TryParse(node.Identifier, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n.ToString(CultureInfo.InvariantCulture) == node.Identifier,
            "string" => node.Identifier.Length > 0 && !node.Identifier.Any(char.IsControl),
            "guid" => Guid.TryParseExact(node.Identifier, "D", out var g) && g.ToString("D") == node.Identifier,
            "opaque" => ValidOpaque(node.Identifier), _ => false };
        if (!valid) throw Error("UaNodeInvalid");
    }
    private static bool ValidOpaque(string value) { try { return value.Length > 0 && Convert.ToBase64String(Convert.FromBase64String(value)) == value; } catch { return false; } }
    private static DateTimeOffset? Time(DateTime time) => time == DateTime.MinValue ? null : new DateTimeOffset(DateTime.SpecifyKind(time, DateTimeKind.Utc));
    private static (string?, object?, string?) Scalar(object? value) => value switch {
        null => ("null", null, null), bool b => ("boolean", b, null),
        sbyte or byte or short or ushort or int or uint or long or ulong => ("decimalInteger", Convert.ToString(value, CultureInfo.InvariantCulture), null),
        float f when float.IsFinite(f) => ("roundTripFloat", f.ToString("R", CultureInfo.InvariantCulture), null),
        double d when double.IsFinite(d) => ("roundTripDouble", d.ToString("R", CultureInfo.InvariantCulture), null),
        string s => ("string", s, null), DateTime d => ("utcDateTime", DateTime.SpecifyKind(d, DateTimeKind.Utc).ToString("O"), null),
        Guid g => ("guid", g.ToString("D"), null), Uuid g => ("guid", g.ToString(), null),
        byte[] b => ("base64", Convert.ToBase64String(b), null),
        Array => (null, null, "UaArrayUnsupported"), ExtensionObject => (null, null, "UaStructureUnsupported"),
        _ => (null, null, "UaValueFormUnsupported")
    };
    internal static BridgeException Error(string code) => new(code, "OPC UA operation refused or incomplete; no remote error text or credential returned.");
}

internal sealed class UaContinuationCleanupFailure(string? operationErrorCode) :
    BridgeException("UaContinuationCleanupUnknown", "The bounded owned continuation release could not be confirmed.")
{
    internal string? OperationErrorCode { get; } = operationErrorCode;
}
