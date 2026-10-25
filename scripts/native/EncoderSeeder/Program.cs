using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.TechnologicalObjects;
using Siemens.Engineering.SW.TechnologicalObjects.Motion;
using TiaMcp.Openness.V21;

// Test-fixture preparation only. This is deliberately not a general MCP write
// route: it requires an exact worker-owned scratch authorization and preserves
// a project archive before configuring the hardware dependency.
internal static class Program
{
    private static readonly string Api = InstalledApiDirectory();

    // Siemens registers the installed V21 API location; C:\Program Files is only the setup default.
    private static string InstalledApiDirectory()
    {
        using (var hklm = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64))
        using (var key = hklm.OpenSubKey(@"SOFTWARE\Siemens\Automation\Openness\21.0\PublicAPI\21.0.0.0\net48"))
        {
            var basePath = key?.GetValue("Siemens.Engineering.Base") as string;
            return string.IsNullOrWhiteSpace(basePath) ? @"C:\Program Files\Siemens\Automation\Portal V21\PublicAPI\V21\net48" : Path.GetDirectoryName(basePath);
        }
    }

    [STAThread]
    private static int Main(string[] args)
    {
        AppDomain.CurrentDomain.AssemblyResolve += (sender, evt) =>
        {
            string name = new AssemblyName(evt.Name).Name;
            if (!name.StartsWith("Siemens.Engineering", StringComparison.Ordinal)) return null;
            string path = Path.Combine(Api, name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };
        try { Console.WriteLine(Run(args).ToString(Formatting.None)); return 0; }
        catch (Exception ex) { Console.WriteLine(new JObject { ["completed"] = false, ["error"] = ex.GetType().Name, ["message"] = ex.Message, ["stack"] = ex.StackTrace }.ToString(Formatting.None)); return 1; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static JObject Run(string[] args)
    {
        if (args.Length != 2 || (args[0] != "inspect" && args[0] != "connect"))
            throw new InvalidOperationException("Usage: EncoderSeeder.exe inspect|connect <authorization.json>");
        string root = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "../../../../../../artifacts"));
        string authPath = GuardPath(root, args[1]);
        var auth = JObject.Parse(File.ReadAllText(authPath));
        if (auth.Value<bool?>("ownsPortalProcess") != true || auth.Value<string>("creationTool") != "tia_create_scratch")
            throw new InvalidOperationException("A worker-owned tia_create_scratch authorization is required.");
        string projectPath = GuardPath(root, auth.Value<string>("projectPath"));
        if (!Path.GetFileName(projectPath).StartsWith("TiaMcpScratch_", StringComparison.Ordinal) || Path.GetExtension(projectPath) != ".ap21")
            throw new InvalidOperationException("Only an isolated TiaMcpScratch_ project is eligible.");
        int pid = auth.Value<int>("processId");
        var process = TiaPortal.GetProcesses().SingleOrDefault(candidate => candidate.Id == pid)
            ?? throw new InvalidOperationException("The exact owned portal process is absent.");
        using (var portal = process.Attach())
        {
            if (portal.Projects.Count != 1 || !string.Equals(portal.Projects[0].Path.FullName, projectPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The exact authorized scratch project must be the only open project.");
            var project = portal.Projects[0];
            using (portal.ExclusiveAccess("TIA MCP isolated encoder fixture dependency preparation"))
            {
                var expectedTargets = (auth["deviceCompilerTargets"] as JArray ?? throw new InvalidOperationException("An independent device compiler inventory is required.")).Values<string>().ToArray();
                DeviceCompilation.RequireTargets(project, expectedTargets);
                var all = DeviceCompilation.Items(project);
                var cpuEntry = all.Single(entry => entry.Path == auth.Value<string>("cpuHardwarePath"));
                var moduleEntry = all.Single(entry => entry.Path == auth.Value<string>("moduleHardwarePath"));
                var cpu = cpuEntry.Item; var module = moduleEntry.Item;
                var plc = cpu.GetService<SoftwareContainer>()?.Software as PlcSoftware;
                if (cpu.TypeIdentifier != auth.Value<string>("cpuTypeIdentifier") || plc == null ||
                    cpuEntry.Path + "/plc/" + Uri.EscapeDataString(plc.Name) != auth.Value<string>("plcPath"))
                    throw new InvalidOperationException("Exact authorized PLC hardware/software identity differs.");
                if (module.Name != auth.Value<string>("moduleName") || module.TypeIdentifier != auth.Value<string>("moduleTypeIdentifier"))
                    throw new InvalidOperationException("Exact authorized module differs from its catalog profile.");
                var moduleItems = all.Where(entry => entry.Path == moduleEntry.Path || entry.Path.StartsWith(moduleEntry.Path + "/items/", StringComparison.Ordinal)).Select(entry => entry.Item).ToArray();
                var result = new JObject { ["completed"] = true, ["mode"] = args[0], ["projectPath"] = projectPath,
                    ["processId"] = pid, ["plcPath"] = auth.Value<string>("plcPath"), ["cpuHardwarePath"] = cpuEntry.Path,
                    ["moduleHardwarePath"] = moduleEntry.Path, ["cpuTypeIdentifier"] = cpu.TypeIdentifier, ["moduleBefore"] = Module(module, moduleItems) };
                if (args[0] == "inspect") return result;
                string name = auth.Value<string>("technologyName");
                if (name != "TiaMcp_EncoderConnected_Fixture") throw new InvalidOperationException("Only the dedicated connected encoder fixture name is permitted.");
                if (plc.TechnologicalObjectGroup.TechnologicalObjects.Find(name) != null) throw new InvalidOperationException("Do not overwrite an existing encoder fixture.");
                string archiveName = "encoder-prerequisites-before-" + Guid.NewGuid().ToString("N");
                project.Save();
                project.Archive(new DirectoryInfo(Path.GetDirectoryName(authPath)), archiveName, ProjectArchivationMode.Compressed);
                result["checkpointArchive"] = Path.Combine(Path.GetDirectoryName(authPath), archiveName + ".zap21");
                result["checkpointReceipt"] = CheckpointEvidence.Capture(result.Value<string>("checkpointArchive"), authPath, projectPath, pid);
                try
                {
                    // Attribute names and values come from the preceding native
                    // inspection; enum values are parsed against the native type.
                    var writes = new JArray(); result["hardwareAttributeWrites"] = writes;
                    foreach (var change in (auth["hardwareAttributes"] as JArray ?? new JArray()).OfType<JObject>())
                    {
                        var item = moduleItems.Single(candidate => candidate.Name == change.Value<string>("itemName") &&
                            (change["channelNumber"] == null || candidate.Channels.Any(channel => channel.Number == change.Value<int>("channelNumber") && channel.Type.ToString() == change.Value<string>("channelType") && channel.IoType.ToString() == change.Value<string>("channelIoType"))));
                        IEngineeringObject target = item;
                        if (change["channelNumber"] != null)
                            target = item.Channels.Single(channel => channel.Number == change.Value<int>("channelNumber") && channel.Type.ToString() == change.Value<string>("channelType") && channel.IoType.ToString() == change.Value<string>("channelIoType"));
                        foreach (var write in SetAttributes(target, (JObject)change["attributes"])) writes.Add(write);
                    }
                    var partner = moduleItems.Single(item => item.Name == auth.Value<string>("channelItemName") && item.Channels.Any(channel => channel.Number == auth.Value<int>("channelNumber") && channel.Type == ChannelType.Technology && channel.IoType.ToString() == auth.Value<string>("channelIoType")));
                    var sensor = partner.Channels.Single(channel => channel.Number == auth.Value<int>("channelNumber") && channel.Type == ChannelType.Technology && channel.IoType.ToString() == auth.Value<string>("channelIoType"));
                    var technology = plc.TechnologicalObjectGroup.TechnologicalObjects.Create(name, "TO_ExternalEncoder", new Version("10.0"));
                    result["technologyBeforeConnection"] = ParameterDependencies(technology);
                    var connection = technology.GetService<EncoderHardwareConnectionProvider>().SensorInterface;
                    connection.Connect(sensor);
                    result["technologyAfterConnection"] = ParameterDependencies(technology);
                    result["compileScope"] = "All selected project devices, hardware and software";
                    result["compile"] = DeviceCompilation.Compile(project, expectedTargets: expectedTargets);
                    var afterItems = DeviceCompilation.Items(project).ToDictionary(entry => entry.Path, entry => entry.Item, StringComparer.Ordinal);
                    if (afterItems.Count != all.Count || all.Any(entry => !afterItems.TryGetValue(entry.Path, out var item) || !Equals(item, entry.Item)))
                        throw new InvalidOperationException("Hardware identities changed during encoder preparation.");
                    result["moduleAfter"] = Module(module, moduleItems);
                    result["connection"] = Connection(connection);
                    string export = Path.Combine(Path.GetDirectoryName(authPath), "encoder-connected-native.xml");
                    technology.Export(new FileInfo(export), ExportOptions.WithDefaults, DocumentInfoOptions.None);
                    result["nativeXmlPath"] = export;
                    result["completed"] = TiaMcp.Openness.V21.CompilationEvidenceRules.IsSuccessful((JObject)result["compile"]) && connection.IsConnected;
                    if (result.Value<bool>("completed")) project.Save();
                }
                catch (Exception ex)
                {
                    result["completed"] = false; result["error"] = ex.GetType().Name; result["message"] = ex.Message;
                    result["moduleAfter"] = Module(module, moduleItems);
                }
                return result;
            }
        }
    }

    private static JArray SetAttributes(IEngineeringObject target, JObject changes)
    {
        var readbacks = new JArray();
        foreach (var property in changes.Properties())
        {
            var info = target.GetAttributeInfos().Single(item => item.Name == property.Name);
            if ((info.AccessMode & EngineeringAttributeAccessMode.Write) == 0) throw new InvalidOperationException("Native hardware attribute is not writable: " + property.Name);
            object before = target.GetAttribute(property.Name);
            if (before == null) throw new InvalidOperationException("Cannot infer the native type of a null hardware attribute.");
            object requested = ((JValue)property.Value).Value;
            // Several V21 dynamic hardware attributes expose UInt64 despite a
            // public symbolic enum. Resolve that exact enum, never guess numbers.
            if (requested is string && !(before is string) && !before.GetType().IsEnum)
            {
                var symbolic = typeof(DeviceItem).Assembly.GetType("Siemens.Engineering.HW." + property.Name);
                if (symbolic?.IsEnum == true) requested = Enum.Parse(symbolic, (string)requested, false);
            }
            object value = before.GetType().IsEnum ? Enum.Parse(before.GetType(), property.Value.Value<string>(), false) : Convert.ChangeType(requested, before.GetType(), CultureInfo.InvariantCulture);
            target.SetAttribute(property.Name, value);
            if (!Equals(target.GetAttribute(property.Name), value)) throw new InvalidOperationException("Native hardware attribute readback differs: " + property.Name);
            readbacks.Add(new JObject { ["name"] = property.Name, ["requested"] = property.Value.DeepClone(),
                ["nativeType"] = before.GetType().FullName, ["before"] = JToken.FromObject(before), ["after"] = JToken.FromObject(value) });
        }
        return readbacks;
    }

    private static JObject Module(DeviceItem module, IEnumerable<DeviceItem> moduleItems) => new JObject { ["name"] = module.Name, ["typeIdentifier"] = module.TypeIdentifier,
        ["items"] = new JArray(moduleItems.Select(item => new JObject { ["name"] = item.Name, ["typeIdentifier"] = item.TypeIdentifier,
            ["attributes"] = Attributes(item), ["channels"] = new JArray(item.Channels.Select(channel => new JObject { ["number"] = channel.Number,
                ["type"] = channel.Type.ToString(), ["ioType"] = channel.IoType.ToString(), ["attributes"] = Attributes(channel) })) })) };

    private static JArray Attributes(IEngineeringObject target)
    {
        var rows = new JArray();
        foreach (var info in target.GetAttributeInfos())
        {
            var row = new JObject { ["name"] = info.Name, ["access"] = info.AccessMode.ToString() }; rows.Add(row);
            if ((info.AccessMode & EngineeringAttributeAccessMode.Read) == 0) continue;
            try
            {
                var value = target.GetAttribute(info.Name); row["type"] = value?.GetType().FullName;
                row["value"] = value == null ? JValue.CreateNull() : value.GetType().IsPrimitive || value is string || value is decimal ? JToken.FromObject(value) : new JValue(value.ToString());
                if (value != null && value.GetType().IsEnum) row["allowedEnumNames"] = new JArray(Enum.GetNames(value.GetType()));
            }
            catch (Exception ex) { row["error"] = ex.Message; }
        }
        return rows;
    }

    private static JObject Connection(AxisEncoderHardwareConnectionInterface value) => new JObject { ["isConnected"] = value.IsConnected,
        ["inputAddress"] = value.InputAddress, ["outputAddress"] = value.OutputAddress,
        ["inputModule"] = value.InputModule?.Name, ["outputModule"] = value.OutputModule?.Name,
        ["connectOption"] = value.ConnectOption.ToString(), ["channel"] = value.Channel == null ? null : new JObject {
            ["number"] = value.Channel.Number, ["type"] = value.Channel.Type.ToString(), ["ioType"] = value.Channel.IoType.ToString() },
        ["pathToDBMember"] = value.PathToDBMember, ["sensorIndexInActorTelegram"] = value.SensorIndexInActorTelegram };

    private static JArray ParameterDependencies(TechnologicalInstanceDB technology) => new JArray(
        new[] { "Sensor.Type", "Sensor.Interface.Number", "_Sensor.Interface.Telegram", "_Sensor.DataAdaptionOffline" }.Select(name =>
        {
            var parameter = technology.Parameters.Find(name);
            return new JObject { ["name"] = name, ["value"] = parameter.Value == null ? JValue.CreateNull() : JToken.FromObject(parameter.Value),
                ["type"] = parameter.Value?.GetType().FullName, ["valueAccess"] = parameter.GetAttributeInfos().Single(info => info.Name == "Value").AccessMode.ToString() };
        }));

    private static string GuardPath(string root, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)) throw new InvalidOperationException("An absolute fixture path is required.");
        string full = Path.GetFullPath(path);
        if (!full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Fixture paths must remain inside repository artifacts.");
        for (string cursor = full; !string.IsNullOrEmpty(cursor) && cursor.Length >= root.Length; cursor = Path.GetDirectoryName(cursor))
            if ((File.Exists(cursor) || Directory.Exists(cursor)) && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Reparse-point paths are not accepted.");
        return full;
    }
}
