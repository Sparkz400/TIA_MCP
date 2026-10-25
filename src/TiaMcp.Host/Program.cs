using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TiaMcp.Core;
using TiaMcp.Host;

try
{
    if (args.Length > 0 && args[0] == "--credential-store") return CredentialCommand.Run(args[1..]);
    if (args.Length > 0 && args[0] == "--ua-identity-store") return UaClientIdentityCommand.Run(args[1..]);
    if (args.Length > 0 && args[0].StartsWith("--session-relay-", StringComparison.Ordinal))
        return await SessionRelay.RunAsync(args);
    var options = TiaMcp.Host.HostOptions.Parse(args);
    var workerArguments = new List<string>();
    if (options.SimulatorApiDirectory is not null) workerArguments.AddRange(["--sim-api-dir", options.SimulatorApiDirectory]);
    if (options.ComponentQualification) workerArguments.Add("--component-qualification");
    if (options.TestSuiteScopeProbe) workerArguments.Add("--testsuite-scope-probe");
    if (options.TranslationQualification) workerArguments.Add("--translation-qualification");
#if TESTSUITE_QUALIFICATION
    if (options.TestSuiteQualification) workerArguments.Add("--testsuite-qualification");
#endif
    var bridge = new BridgeClient(new BridgeOptions(options.WorkerPath, options.ApiDirectory, options.TimeoutSeconds,
        workerArguments, Disabled: options.NoNativeWorkers));
    await using var simulator = new SimulatorClient(options);
    await using (bridge)
    {
        if (options.Diagnose)
        {
            var info = await bridge.CallAsync("bridge_info");
            var discovery = await bridge.CallAsync("discover");
            Console.WriteLine(JsonSerializer.Serialize(new { bridge = info, discovery }, new JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
            builder.Logging.ClearProviders();
            builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
            builder.Services.AddSingleton(options);
            if (options.AddInHandoff) builder.Services.AddHostedService<AddInHandoffService>();
            builder.Services.AddSingleton<TiaMcp.Core.Inventory.InventoryStore>();
            builder.Services.AddSingleton<PhysicalRuntimeSessions>();
            builder.Services.AddSingleton<OpcUaRuntimeSessions>();
            builder.Services.AddSingleton<RuntimeCaptures>();
            builder.Services.AddSingleton<TiaMcp.Core.Inventory.IInventoryIdentityFactory, TiaMcp.Core.Inventory.SiemensWebIdentityFactory>();
            IBridgeClient native = options.WatchQualificationPoint is null ? bridge : new WatchQualificationBridge(bridge, options);
            builder.Services.AddSingleton<IBridgeClient>(new TiaMcp.Core.WatchTables.WatchTableDispatcher(native, options.ArtifactsDirectory,
                allowUnqualifiedDocumentEdits: options.WatchQualificationPoint is not null, allowComponentQualification: options.ComponentQualification));
            builder.Services.AddSingleton(simulator);
            var mcp = builder.Services.AddMcpServer(server => server.ServerInstructions = AuthenticationInstructions.Text +
                (options.NoNativeWorkers ? "\nThis Host has native workers disabled. Use supplied-document workflows; native engineering and simulator calls return NativeWorkersDisabled without launching a worker. Network inventory, runtime Web API and OPC UA tools remain separate and require an explicitly selected endpoint and authentication/trust prerequisites." : ""))
                .WithStdioServerTransport().WithTools<TiaTools>().WithTools<ArtifactTools>().WithTools<LibraryPackageTools>().WithTools<SimulatorTools>()
                .WithTools<TranslationTools>()
                .WithTools<TranslationNativeTools>()
                .WithTools<OpcUaTools>()
                .WithTools<VciTools>()
                .WithTools<GraphTools>()
    .WithTools<AutomationMlTools>()
                .WithTools<SoftwareUnitTools>()
                .WithTools<ConnectionTools>()
                .WithTools<CompositeEngineeringTools>()
                .WithTools<HmiComponentTools>()
                .WithTools<DbSnapshotTools>()
            .WithTools<SafetyEvidenceTools>()
                .WithTools<DriveProfileTools>()
                .WithTools<TechnologyProfileTools>()
                .WithTools<AccessTools>()
                .WithTools<RuntimeHandoffTools>()
                .WithTools<PublicCertificateTools>()
                .WithTools<WebApplicationTools>()
                .WithTools<DeviceInventoryTools>()
                .WithTools<PhysicalRuntimeTools>()
                .WithTools<OpcUaRuntimeTools>()
                .WithTools<OpcUaSubscriptionTools>()
                .WithTools<RuntimeCaptureTools>()
                .WithTools<AddInSelectionTools>()
                .WithTools<MultiuserTools>()
                .WithTools<TeamcenterTools>()
                .WithResources<ProjectExplorerResources>().WithRequestFilters(ProjectExplorerResources.Configure);
            if (options.WatchQualificationPoint is not null) mcp.WithTools<WatchQualificationTools>();
            if (options.WatchQualificationPoint is not null && options.ComponentQualification) mcp.WithTools<WatchAuthoringQualificationTools>();
            if (options.ComponentQualification) mcp.WithTools<ComponentQualificationTools>();
            if (options.TestSuiteScopeProbe) mcp.WithTools<TestSuiteScopeProbeTools>();
#if TESTSUITE_QUALIFICATION
            if (options.TestSuiteQualification) mcp.WithTools<TestSuiteQualificationTools>();
#endif
            if (options.TranslationQualification) mcp.WithTools<TranslationQualificationTools>();
            using var host = builder.Build();
            var handoffService = options.AddInHandoff ? host.Services.GetServices<IHostedService>().OfType<AddInHandoffService>().Single() : null;
            await host.RunAsync();
            if (handoffService?.Failed == true) return 1;
        }
    }
    await simulator.DisposeAsync();
    return bridge.WorkerShutdownSucceeded == true && simulator.Bridge.WorkerShutdownSucceeded == true ? 0 : 1;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception is BridgeException bridgeError ? $"{bridgeError.Code}: {bridgeError.Message}" : exception.ToString());
    return 1;
}
