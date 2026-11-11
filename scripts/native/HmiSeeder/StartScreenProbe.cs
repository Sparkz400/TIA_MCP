using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.Library.MasterCopies;
using Siemens.Engineering.SW;

// Probe: which route, if any, gives a newly created Classic HMI a start screen without the TIA editor.
// New scratch project in an owned headless Portal only. Every attempt and its native answer is recorded;
// nothing here is a supported fixture route until a report shows an error-free compile.
internal static partial class Program
{
    private static readonly string[] StartScreenNames = { "StartScreen", "StartScreenName", "Startscreen", "RootScreen", "StartPicture", "InitialScreen" };

    private static int StartScreenProbe(string output, string catalogFilter)
    {
        Output = Path.GetFullPath(output);
        if (Directory.Exists(Output) || File.Exists(Output)) throw new InvalidOperationException("Choose a new output directory; existing evidence is never overwritten.");
        Directory.CreateDirectory(Output);
        Report = new JObject { ["schemaVersion"] = 1, ["completed"] = false, ["probe"] = "classic-start-screen-routes", ["catalogFilter"] = catalogFilter,
            ["scope"] = "New scratch project in an owned headless Portal; no existing project is opened", ["startedUtc"] = DateTime.UtcNow.ToString("O") };
        Persist();
        using (var portal = new TiaPortal(TiaPortalMode.WithoutUserInterface))
        {
            Report["processId"] = portal.GetCurrentProcess().Id; Persist();
            Project project = null;
            try
            {
                project = portal.Projects.Create(new DirectoryInfo(Output), "Ssp_" + Guid.NewGuid().ToString("N").Substring(0, 8));
                var selected = portal.HardwareCatalog.Find(catalogFilter).Where(item => string.Equals(item.TypeName, catalogFilter, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(item => item.Version).ThenBy(item => item.TypeIdentifier, StringComparer.Ordinal).First();
                Report["typeIdentifier"] = selected.TypeIdentifier; Persist();
                var devices = new[] { "HmiDevice_A", "HmiDevice_B" }.Select(name => project.Devices.CreateWithItem(selected.TypeIdentifier, null, name)).ToArray();
                Func<Device, Software> classic = device => Items(device.DeviceItems).Select(item => item.GetService<SoftwareContainer>()?.Software)
                    .FirstOrDefault(sw => sw != null && sw.GetType().FullName.StartsWith("Siemens.Engineering.Hmi.", StringComparison.Ordinal));
                for (int i = 0; i < devices.Length; i++) if (classic(devices[i]) == null) Report["plug" + i] = PlugHmiApplication(devices[i], selected.TypeIdentifier, i + 1);
                Software a = classic(devices[0]), b = classic(devices[1]);
                if (a == null || b == null) throw new InvalidOperationException("The created devices expose no Classic HMI software.");

                // 1. What the public self-description offers on every object of the first device.
                var described = new JArray(); Report["selfDescription"] = described;
                foreach (IEngineeringObject obj in new IEngineeringObject[] { devices[0], a }.Concat(Items(devices[0].DeviceItems)))
                    described.Add(new JObject { ["type"] = obj.GetType().FullName, ["name"] = Text(() => obj.GetAttribute("Name")),
                        ["attributes"] = new JArray(obj.GetAttributeInfos().Select(info => info.Name + ":" + info.AccessMode)),
                        ["services"] = new JArray((obj as IEngineeringServiceProvider)?.GetServiceInfos().Select(info => info.Type.FullName) ?? Enumerable.Empty<string>()) });
                Report["contract"] = Contract(a); Persist();

                // 2. Baseline: two imported screens, no start screen.
                Report["seedA"] = Seed(a, Output);
                Report["compileA_seeded"] = Compile(new[] { a }); Persist();

                // 3. Names the meta-model might answer although GetAttributeInfos does not list them.
                var blind = new JArray(); Report["blindAttributes"] = blind;
                foreach (IEngineeringObject obj in new IEngineeringObject[] { a, devices[0] }.Concat(Items(devices[0].DeviceItems)))
                    foreach (string name in StartScreenNames)
                    {
                        var row = new JObject { ["type"] = obj.GetType().FullName, ["object"] = Text(() => obj.GetAttribute("Name")), ["attribute"] = name }; blind.Add(row);
                        try { row["read"] = Convert.ToString(obj.GetAttribute(name)); } catch (Exception ex) { row["readError"] = Error(ex)["message"]; }
                        try { obj.SetAttribute(name, "Overview_A"); row["written"] = true; } catch (Exception ex) { row["writeError"] = Error(ex)["message"]; }
                    }
                Report["compileA_afterBlind"] = Compile(new[] { a }); Persist();

                // 4. Paste instead of import: a master copy of a screen created into an HMI that has no screen yet.
                var paste = new JObject(); Report["masterCopyPaste"] = paste;
                try
                {
                    object screensA = Get(Get(a, "ScreenFolder"), "Screens"), screensB = Get(Get(b, "ScreenFolder"), "Screens");
                    var screen = Call(screensA, "Find", "Overview_A") as IMasterCopySource ?? throw new InvalidOperationException("The imported screen is no master copy source.");
                    var master = project.ProjectLibrary.MasterCopyFolder.MasterCopies.Create(screen);
                    paste["masterCopy"] = master.Name;
                    var createFrom = screensB.GetType().GetMethods().FirstOrDefault(m => m.Name == "CreateFrom" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(MasterCopy))
                        ?? throw new InvalidOperationException("The screen composition has no CreateFrom(MasterCopy).");
                    paste["created"] = Convert.ToString(Get(createFrom.Invoke(screensB, new object[] { master }), "Name"));
                }
                catch (Exception ex) { paste["error"] = Error(ex); }
                Report["compileB_afterPaste"] = Compile(new[] { b }); Persist();

                project.Save(); Report["projectPath"] = project.Path.FullName; Report["completed"] = true;
            }
            finally { try { project?.Close(); } catch { } Report["finishedUtc"] = DateTime.UtcNow.ToString("O"); Persist(); }
        }
        Console.WriteLine(Path.Combine(Output, "native-report.json"));
        return 0;
    }

    private static string Text(Func<object> read) { try { return Convert.ToString(read()); } catch (Exception ex) { return "<" + ex.GetType().Name + ">"; } }

    // The client object is a thin proxy; record what its non-public contract is, so a later step knows what could be asked of it.
    private static JObject Contract(object engineeringObject)
    {
        var result = new JObject();
        try
        {
            var field = engineeringObject.GetType().GetField("m_InternalAccess", BindingFlags.Instance | BindingFlags.NonPublic);
            object access = field?.GetValue(engineeringObject);
            result["accessType"] = access?.GetType().FullName;
            object contract = access?.GetType().GetInterfaces().SelectMany(i => i.GetMethods()).FirstOrDefault(m => m.Name == "GetBoxedContract")?.Invoke(access, null)
                ?? engineeringObject.GetType().GetInterfaces().SelectMany(i => i.GetMethods()).FirstOrDefault(m => m.Name == "GetBoxedContract")?.Invoke(engineeringObject, null);
            result["contractType"] = contract?.GetType().FullName;
            result["contractInterfaces"] = new JArray(contract?.GetType().GetInterfaces().Select(i => i.FullName) ?? Enumerable.Empty<string>());
            result["contractMethods"] = new JArray((contract?.GetType().GetInterfaces().SelectMany(i => i.GetMethods()) ?? Enumerable.Empty<MethodInfo>())
                .Select(m => m.DeclaringType.Name + "." + m.Name + "(" + string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name)) + ")").Distinct().Take(200));
        }
        catch (Exception ex) { result["error"] = Error(ex); }
        return result;
    }
}
