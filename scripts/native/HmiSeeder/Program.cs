using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.HW.CommunicationConnections;
using Siemens.Engineering.SW;
using Siemens.Engineering.Umac;

// Independent fixture author, deliberately separate from the adapter under test.
// It can only create new scratch projects. No existing process attach, arbitrary
// project open, physical download, runtime or project-script execution exists.
internal static partial class Program
{
    private static string Api = InstalledApiDirectory();
    // Same lookup as the worker: Siemens registers the installed V21 API; C:\Program Files is only the setup default.
    private static string InstalledApiDirectory()
    {
        using (var hklm = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64))
        using (var key = hklm.OpenSubKey(@"SOFTWARE\Siemens\Automation\Openness\21.0\PublicAPI\21.0.0.0\net48"))
        {
            var basePath = key?.GetValue("Siemens.Engineering.Base") as string;
            return string.IsNullOrWhiteSpace(basePath) ? @"C:\Program Files\Siemens\Automation\Portal V21\PublicAPI\V21\net48" : Path.GetDirectoryName(basePath);
        }
    }
    private static string Output;
    private static JObject Report;
    private static bool PositiveFixture;
    private static bool TextListFixture;
    private static readonly string[] TextListInputNames = { "QualificationTextLists.hmi.yml", "QualificationTextLists.TextLibrary.hmi.yml" };
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
        try { return Run(args); }
        catch (Exception ex)
        {
            var error = Error(ex); if (Report != null) { Report["failure"] = error; Report["completed"] = false; Persist(); }
            Console.WriteLine(error.ToString(Formatting.None)); return 2;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Run(string[] args)
    {
        if (args.Length == 3 && args[0] == "visual") return Visual(args[1], args[2]);
        if (args.Length == 2 && args[0] == "repair-ui") return RepairUi(args[1]);
        if (args.Length == 2 && args[0] == "verify-configured") return VerifyConfigured(args[1]);
        if (args.Length < 2 || args.Length > 5 || args[0] != "qualify") throw new ArgumentException("HmiSeeder.exe qualify <NEW-output-directory> [comma-separated-profile-ids] [positive|negative] [textlists]");
        string mode = args.Length >= 4 ? args[3] : "negative";
        if (mode != "positive" && mode != "negative") throw new ArgumentException("Fixture mode must be positive or negative.");
        PositiveFixture = mode == "positive";
        TextListFixture = args.Length == 5;
        if (TextListFixture && args[4] != "textlists") throw new ArgumentException("The only documented optional resource fixture is textlists.");
        var requested = args.Length >= 3 ? args[2].Split(',') : null;
        var knownProfiles = new[] { "classic-comfort", "classic-rt-advanced", "classic-basic", "classic-rt-professional", "unified-comfort", "unified-pc" };
        if (requested != null && (requested.Length != requested.Distinct().Count() || requested.Any(id => !knownProfiles.Contains(id))))
            throw new ArgumentException("Select distinct known exact HMI profile IDs before starting an owned portal.");
        if (TextListFixture && (requested == null || requested.Any(id => !id.StartsWith("unified-", StringComparison.Ordinal))))
            throw new ArgumentException("The paired text-list fixture requires explicitly selected Unified profiles.");
        if (TextListFixture) foreach (string name in TextListInputNames)
            if (!File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fixtures", name))) throw new FileNotFoundException("The paired documented text-list fixture is incomplete.", name);
        Output = Path.GetFullPath(args[1]);
        if (Directory.Exists(Output) || File.Exists(Output)) throw new InvalidOperationException("Choose a new output directory; existing evidence is never overwritten.");
        for (var parent = new DirectoryInfo(Path.GetDirectoryName(Output)); parent != null; parent = parent.Parent)
            if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Output ancestry cannot contain reparse points.");
        Directory.CreateDirectory(Output);
        Report = new JObject { ["schemaVersion"] = 1, ["completed"] = false, ["startedUtc"] = DateTime.UtcNow.ToString("O"),
            ["scope"] = "Independent newly-created offline HMI fixtures; no existing user project, scripts or deployment",
            ["seederSha256"] = Hash(Assembly.GetExecutingAssembly().Location), ["profiles"] = new JArray(), ["runtimeVerified"] = false,
            ["fixtureMode"] = mode, ["deliberateInvalidReferences"] = !PositiveFixture,
            ["resourceFixture"] = TextListFixture ? "documented-bilingual-text-list" : "none",
            ["assembly"] = typeof(TiaPortal).Assembly.FullName, ["cleanup"] = "pending" };
        Persist();
        RecordProcesses(null, "before-owned-start");
        using (var portal = new TiaPortal(TiaPortalMode.WithoutUserInterface))
        {
            Report["processId"] = portal.GetCurrentProcess().Id; Report["ownsPortalProcess"] = true; Persist();
            RecordProcesses(portal, "owned-start");
            var profiles = new[] {
                new[] { "classic-comfort", "TP700 Comfort" }, new[] { "classic-rt-advanced", "WinCC RT Advanced" },
                new[] { "classic-basic", "KTP700 Basic PN" }, new[] { "classic-rt-professional", "WinCC RT Professional" },
                new[] { "unified-comfort", "MTP700 Unified Comfort" }, new[] { "unified-pc", "WinCC Unified PC RT" }
            };
            foreach (var profile in profiles)
            {
                if (requested != null && !requested.Contains(profile[0])) continue;
                var row = new JObject { ["profile"] = profile[0], ["catalogFilter"] = profile[1], ["status"] = "unverified",
                    ["fixtureMode"] = mode, ["deliberateInvalidReferences"] = !PositiveFixture,
                    ["resourceFixture"] = TextListFixture ? "documented-bilingual-text-list" : "none",
                    ["nativeQualification"] = "unverified", ["runtimeVerified"] = false, ["visualVerified"] = false, ["gates"] = new JObject() };
                ((JArray)Report["profiles"]).Add(row); Persist();
                Project project = null;
                try
                {
                    string name = "TiaMcpHmi_" + profile[0].Replace('-', '_') + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                    string directory = Path.Combine(Output, profile[0]); Directory.CreateDirectory(directory);
                    project = portal.Projects.Create(new DirectoryInfo(directory), name); row["projectPath"] = project.Path.FullName; Persist();
                    RecordProcesses(portal, profile[0] + ":created");
                    if (TextListFixture) { row["resourceLanguages"] = ConfigureResourceLanguages(project); Persist(); }
                    var matches = portal.HardwareCatalog.Find(profile[1]);
                    row["catalogMatches"] = new JArray(matches.Take(200).Select(item => new JObject {
                        ["typeIdentifier"] = item.TypeIdentifier, ["typeName"] = item.TypeName, ["articleNumber"] = item.ArticleNumber,
                        ["version"] = item.Version == null ? null : item.Version.ToString(), ["catalogPath"] = item.CatalogPath }));
                    row["catalogTotal"] = matches.Count; Persist();
                    if (matches.Count == 0) { row["status"] = "blocked"; row["reason"] = "No installed catalog target matches this exact family/device filter."; continue; }
                    // Only catalog-observed IDs are used. Every selected identity is retained.
                    var exact = matches.Where(item => string.Equals(item.TypeName, profile[1], StringComparison.OrdinalIgnoreCase)).ToList();
                    if (exact.Count == 0) { row["status"] = "blocked"; row["reason"] = "Catalog keyword matches exist but none has the exact requested device/runtime type name; a related device is not substituted."; continue; }
                    var selected = exact.OrderByDescending(item => item.Version).ThenBy(item => item.TypeIdentifier, StringComparer.Ordinal).First();
                    row["selectedTarget"] = new JObject { ["typeIdentifier"] = selected.TypeIdentifier, ["version"] = selected.Version == null ? null : selected.Version.ToString(), ["typeName"] = selected.TypeName, ["catalogPath"] = selected.CatalogPath };
                    Persist();
                    var first = project.Devices.CreateWithItem(selected.TypeIdentifier, null, "HmiDevice_A");
                    var second = project.Devices.CreateWithItem(selected.TypeIdentifier, null, "HmiDevice_B");
                    var software = new List<Software>(); var devices = new JArray(); row["devices"] = devices;
                    foreach (var device in new[] { first, second })
                    {
                        var description = new JObject { ["name"] = device.Name, ["items"] = new JArray() }; devices.Add(description);
                        foreach (var item in Items(device.DeviceItems))
                        {
                            var native = new JObject { ["name"] = item.Name, ["typeIdentifier"] = item.TypeIdentifier }; ((JArray)description["items"]).Add(native);
                            try
                            {
                                var sw = item.GetService<SoftwareContainer>()?.Software;
                                native["softwareType"] = sw?.GetType().FullName;
                                if (sw != null && (sw.GetType().FullName.StartsWith("Siemens.Engineering.Hmi.") || sw.GetType().FullName.StartsWith("Siemens.Engineering.HmiUnified."))) software.Add(sw);
                                native["softwareNull"] = sw == null;
                            }
                            catch (Exception ex) { native["softwareError"] = Error(ex); }
                        }
                    }
                    Persist();
                    if (software.Count != 2) { row["status"] = "blocked"; row["reason"] = "Hardware created but two HMI software objects are inaccessible; inspect softwareNull/softwareError for installation/license evidence."; continue; }
                    row["softwareTypes"] = new JArray(software.Select(sw => sw.GetType().FullName));
                    row["fixtureAuthoring"] = new JArray(software.Select(sw => Seed(sw, directory)));
                    row["connectionFixture"] = SeedConnections(portal, project, new[] { first, second }, directory);
                    // Native dynamics are authored only after their external tags exist.
                    row["dynamicsFixture"] = new JArray(software.Where(sw => sw.GetType().FullName.StartsWith("Siemens.Engineering.HmiUnified.")).Select(sw =>
                        new JObject { ["software"] = sw.Name, ["dynamics"] = SeedDynamics(sw) }));
                    row["runtimePrerequisites"] = new JArray(software.Select(ConfigureRuntimePrerequisites));
                    row["nativeBefore"] = new JArray(software.Select(sw => Inspect(sw, directory, "baseline")));
                    row["nativeRepeated"] = new JArray(software.Select(sw => Inspect(sw, directory, "repeated")));
                    row["compileBeforeTrial"] = Compile(software);
                    row["scalarTrial"] = ScalarTrial(software[0], directory);
                    row["compile"] = Compile(software);
                    RecordProcesses(portal, profile[0] + ":compiled");
                    project.Save(); row["saveVerified"] = true;
                    string projectPath = project.Path.FullName;
                    project.Archive(new DirectoryInfo(directory), "baseline.zap21", ProjectArchivationMode.Compressed);
                    string archive = Path.Combine(directory, "baseline.zap21"); row["archive"] = new JObject { ["path"] = archive, ["sha256"] = Hash(archive) };
                    project.Close(); project = null;
                    RecordProcesses(portal, profile[0] + ":closed-before-reopen");
                    project = portal.Projects.Open(new FileInfo(projectPath));
                    RecordProcesses(portal, profile[0] + ":reopened");
                    row["reopenVerified"] = true;
                    row["nativeReopened"] = new JArray(FindSoftware(project).Select(sw => Inspect(sw, directory, "reopened")));
                    row["compileReopened"] = Compile(FindSoftware(project));
                    project.Close(); project = null;
                    string recovery = Path.Combine(directory, "recovered"); Directory.CreateDirectory(recovery);
                    project = portal.Projects.Retrieve(new FileInfo(archive), new DirectoryInfo(recovery));
                    RecordProcesses(portal, profile[0] + ":retrieved");
                    row["recoveredProjectPath"] = project.Path.FullName;
                    row["nativeRecovered"] = new JArray(FindSoftware(project).Select(sw => Inspect(sw, directory, "recovered")));
                    row["compileRecovered"] = Compile(FindSoftware(project));
                    if (PositiveFixture && CompileSucceeded((JArray)row["compileRecovered"]))
                        row["derivedPositiveBaseline"] = DerivePositiveBaseline(portal, ref project, directory, profile[0]);
                    row["archiveRetrievalVerified"] = true;
                    row["status"] = "observed"; row["nativeQualification"] = "requires-bridge-comparison";
                    row["reason"] = "Native fixture lifecycle recorded; compare independent inventories through exact bridge binaries before qualifying inspection. Mutations, visual inspection and runtime are separate gates.";
                }
                catch (Exception ex) { row["error"] = Error(ex); string type = (string)row["error"]["type"]; string message = (string)row["error"]["message"]; row["status"] = type.IndexOf("License", StringComparison.OrdinalIgnoreCase) >= 0 || message.IndexOf("not installed", StringComparison.OrdinalIgnoreCase) >= 0 ? "blocked" : "failed"; }
                finally { if (project != null) { try { project.Close(); } catch (Exception ex) { row["closeError"] = Error(ex); } } Persist(); }
            }
            portal.GetCurrentProcess().Dispose();
        }
        RecordProcesses(null, "after-owned-dispose");
        Report["cleanup"] = "owned-portal-disposed"; Report["completed"] = true; Persist();
        EmitDerivedReport();
        Console.WriteLine(new JObject { ["report"] = Path.Combine(Output, "native-report.json"), ["profiles"] = new JArray(((JArray)Report["profiles"]).OfType<JObject>().Select(row => new JObject { ["profile"] = row["profile"], ["status"] = row["status"], ["reason"] = row["reason"] })) }.ToString(Formatting.None));
        return ((JArray)Report["profiles"]).OfType<JObject>().Any(row => row.Value<string>("status") == "failed") ? 1 : ((JArray)Report["profiles"]).OfType<JObject>().Any(row => row.Value<string>("status") == "blocked") ? 2 : 0;
    }

    private static JObject Seed(Software sw, string directory)
    {
        var result = new JObject { ["software"] = sw.Name, ["steps"] = new JArray() };
        if (!sw.GetType().FullName.StartsWith("Siemens.Engineering.HmiUnified."))
        {
            result["status"] = "native-defaults-plus-documented-self-description";
            // Repeating Overview_A across roles checks that the adapter keeps role paths distinct.
            foreach (string compositionPath in new[] { "ScreenFolder.Screens", "ScreenTemplateFolder.ScreenTemplates", "ScreenPopupFolder.ScreenPopups", "TagFolder.TagTables" })
            {
                var step = new JObject { ["composition"] = compositionPath }; ((JArray)result["steps"]).Add(step);
                try
                {
                    object composition = sw; foreach (string part in compositionPath.Split('.')) composition = Get(composition, part);
                    var native = (IEngineeringComposition)composition; var infos = native.GetCreationInfos();
                    step["creationInfos"] = new JArray(infos.Select(info => new JObject { ["type"] = info.Type.FullName, ["parameters"] = new JArray(info.ParameterInfos.Select(parameter => new JObject { ["name"] = parameter.Name, ["mandatory"] = parameter.IsMandatory })) }));
                    var eligible = infos.FirstOrDefault(info => info.ParameterInfos.All(parameter => !parameter.IsMandatory || parameter.Name.Equals("Name", StringComparison.OrdinalIgnoreCase)));
                    if (eligible == null) { step["status"] = "unsupported"; continue; }
                    string nameParameter = eligible.ParameterInfos.Single(parameter => parameter.Name.Equals("Name", StringComparison.OrdinalIgnoreCase)).Name;
                    var created = native.Create(eligible.Type, new[] { new KeyValuePair<string, object>(nameParameter, compositionPath.StartsWith("Screen") ? "Overview_A" : "QualificationTags") });
                    step["createdType"] = created.GetType().FullName;
                }
                catch (Exception ex) { step["error"] = Error(ex); }
            }
            // Independent authored inputs use the documented Classic SimaticML
            // object/attribute structure. Only a successful native import and
            // subsequent native export establishes the accepted fixture shape.
            foreach (string screenName in new[] { "Overview_A", "Overview_B" })
            {
                var step = new JObject { ["authoredScreenInput"] = screenName }; ((JArray)result["steps"]).Add(step);
                try
                {
                    string file = Path.Combine(directory, "input_" + sw.Name + "_" + screenName + ".xml");
                    File.WriteAllText(file, "<?xml version=\"1.0\" encoding=\"utf-8\"?><Document><Engineering version=\"V21\"/><Hmi.Screen.Screen ID=\"0\"><AttributeList><Name>" + screenName + "</Name><BackColor>204, 204, 204</BackColor></AttributeList><ObjectList><Hmi.Screen.ScreenLayer ID=\"1\" CompositionName=\"Layers\"><AttributeList><Name>Qualification</Name><Index>0</Index></AttributeList><ObjectList><Hmi.Screen.Rectangle ID=\"2\" CompositionName=\"ScreenItems\"><AttributeList><ObjectName>RepeatedItem</ObjectName><Left>30</Left><Top>40</Top><Width>120</Width><Height>60</Height></AttributeList></Hmi.Screen.Rectangle></ObjectList></Hmi.Screen.ScreenLayer></ObjectList></Hmi.Screen.Screen></Document>");
                    step["input"] = file; step["inputSha256"] = Hash(file);
                    object composition = Get(Get(sw, "ScreenFolder"), "Screens");
                    object imported = composition.GetType().GetMethod("Import", new[] { typeof(FileInfo), typeof(ImportOptions) }).Invoke(composition, new object[] { new FileInfo(file), ImportOptions.None });
                    step["nativeImportAccepted"] = true;
                }
                catch (Exception ex) { step["error"] = Error(ex); }
            }
            foreach (var role in new[] { new[] { "ScreenTemplateFolder", "ScreenTemplates", "Hmi.Screen.ScreenTemplate", "" }, new[] { "ScreenPopupFolder", "ScreenPopups", "Hmi.Screen.ScreenPopup", "<Width>320</Width><Height>240</Height>" } })
            {
                var step = new JObject { ["authoredScreenInput"] = "Overview_A", ["role"] = role[0] }; ((JArray)result["steps"]).Add(step);
                try
                {
                    string file = Path.Combine(directory, "input_" + sw.Name + "_" + role[1] + ".xml");
                    File.WriteAllText(file, "<?xml version=\"1.0\" encoding=\"utf-8\"?><Document><Engineering version=\"V21\"/><" + role[2] + " ID=\"0\"><AttributeList><Name>Overview_A</Name>" + role[3] + "</AttributeList><ObjectList><Hmi.Screen.ScreenLayer ID=\"1\" CompositionName=\"Layers\"><AttributeList><Name>Qualification</Name><Index>0</Index></AttributeList><ObjectList><Hmi.Screen.Rectangle ID=\"2\" CompositionName=\"ScreenItems\"><AttributeList><ObjectName>RepeatedItem</ObjectName><Left>10</Left><Top>20</Top><Width>80</Width><Height>40</Height></AttributeList></Hmi.Screen.Rectangle></ObjectList></Hmi.Screen.ScreenLayer></ObjectList></" + role[2] + "></Document>");
                    step["input"] = file; step["inputSha256"] = Hash(file);
                    object composition = Get(Get(sw, role[0]), role[1]);
                    composition.GetType().GetMethod("Import", new[] { typeof(FileInfo), typeof(ImportOptions) }).Invoke(composition, new object[] { new FileInfo(file), ImportOptions.None });
                    step["nativeImportAccepted"] = true;
                }
                catch (Exception ex) { step["error"] = Error(ex); }
            }
            try
            {
                string file = Path.Combine(directory, "input_" + sw.Name + "_tags.xml");
                File.WriteAllText(file, "<?xml version=\"1.0\" encoding=\"utf-8\"?><Document><Engineering version=\"V21\"/><Hmi.Tag.TagTable ID=\"0\"><AttributeList><Name>QualificationTags</Name></AttributeList><ObjectList><Hmi.Tag.Tag ID=\"1\" CompositionName=\"Tags\"><AttributeList><Name>SharedValue</Name></AttributeList><LinkList><DataType TargetID=\"@OpenLink\"><Name>Int</Name></DataType></LinkList></Hmi.Tag.Tag></ObjectList></Hmi.Tag.TagTable></Document>");
                object composition = Get(Get(sw, "TagFolder"), "TagTables");
                composition.GetType().GetMethod("Import", new[] { typeof(FileInfo), typeof(ImportOptions) }).Invoke(composition, new object[] { new FileInfo(file), ImportOptions.None });
                result["tagTableImportAccepted"] = true;
                if (PositiveFixture) { result["classicBindingFixture"] = "unverified: requires independently native-accepted Classic binding template"; return result; }
                // A separate native-accepted screen with an explicit internal tag link.
                string boundFile = Path.Combine(directory, "input_" + sw.Name + "_bound.xml");
                File.WriteAllText(boundFile, "<?xml version=\"1.0\" encoding=\"utf-8\"?><Document><Engineering version=\"V21\"/><Hmi.Screen.Screen ID=\"0\"><AttributeList><Name>BindingScreen</Name></AttributeList><ObjectList><Hmi.Screen.ScreenLayer ID=\"1\" CompositionName=\"Layers\"><AttributeList><Name>Qualification</Name><Index>0</Index></AttributeList><ObjectList><Hmi.Screen.IOField ID=\"2\" CompositionName=\"ScreenItems\"><AttributeList><ObjectName>BoundValue</ObjectName><Left>30</Left><Top>40</Top><Width>120</Width><Height>60</Height></AttributeList><LinkList><ProcessTag TargetID=\"@OpenLink\"><Name>SharedValue</Name></ProcessTag></LinkList></Hmi.Screen.IOField></ObjectList></Hmi.Screen.ScreenLayer></ObjectList></Hmi.Screen.Screen></Document>");
                object screenComposition = Get(Get(sw, "ScreenFolder"), "Screens");
                screenComposition.GetType().GetMethod("Import", new[] { typeof(FileInfo), typeof(ImportOptions) }).Invoke(screenComposition, new object[] { new FileInfo(boundFile), ImportOptions.None });
                result["boundScreenImportAccepted"] = true;
            }
            catch (Exception ex) { result["tagTableImportError"] = Error(ex); }
            return result;
        }
        foreach (string screenName in new[] { "Overview_A", "Overview_B" })
        {
            var step = new JObject { ["screen"] = screenName }; ((JArray)result["steps"]).Add(step);
            try
            {
                object screens = Get(sw, "Screens"); object screen = Call(screens, "Create", screenName);
                object items = Get(screen, "ScreenItems");
                Type rectangleType = sw.GetType().Assembly.GetType("Siemens.Engineering.HmiUnified.UI.Shapes.HmiRectangle", true);
                var create = items.GetType().GetMethods().Single(method => method.Name == "Create" && method.IsGenericMethodDefinition && method.GetParameters().Length == 1);
                var item = create.MakeGenericMethod(rectangleType).Invoke(items, new object[] { "RepeatedItem" });
                step["createdType"] = item.GetType().FullName; step["duplicateNameValidAcrossScreens"] = true;
            }
            catch (Exception ex) { step["error"] = Error(ex); }
        }
        try { object tag = Call(Get(sw, "Tags"), "Create", "SharedValue"); Set(tag, "DataType", "Int"); result["sameTagNameAcrossDevices"] = "created"; }
        catch (Exception ex) { result["tagError"] = Error(ex); }
        try { Set(Get(sw, "RuntimeSettings"), "StartScreen", "Overview_A"); result["startScreen"] = Get(Get(sw, "RuntimeSettings"), "StartScreen").ToString(); }
        catch (Exception ex) { result["startScreenError"] = Error(ex); }
        result["richContents"] = SeedRichContents(sw, directory);
        return result;
    }

    private static JObject ConfigureResourceLanguages(Project project)
    {
        var settings = project.LanguageSettings;
        var result = new JObject { ["scope"] = "Active project languages of this newly created disposable fixture only",
            ["before"] = new JArray(settings.ActiveLanguages.Select(language => language.Culture.Name)) };
        foreach (string name in new[] { "en-US", "de-DE" })
        {
            var language = settings.Languages.Find(CultureInfo.GetCultureInfo(name));
            if (language == null) throw new InvalidOperationException("Fixture text language is not supported: " + name);
            if (!settings.ActiveLanguages.Contains(language)) settings.ActiveLanguages.Add(language);
        }
        result["after"] = new JArray(settings.ActiveLanguages.Select(language => language.Culture.Name));
        if (!new[] { "en-US", "de-DE" }.All(name => settings.ActiveLanguages.Find(CultureInfo.GetCultureInfo(name)) != null))
            throw new InvalidOperationException("Both documented text-list fixture languages must be active before native import.");
        result["status"] = "observed";
        return result;
    }

    private static JObject SeedTextList(Software sw, string directory)
    {
        var result = new JObject { ["status"] = "unverified", ["scriptsExecuted"] = false,
            ["sourceGrammar"] = "https://docs.tia.siemens.cloud/r/en-us/v21/functions-for-accessing-the-data-of-an-hmi-unified-device/hmisoftware/text-lists/export-of-text-lists" };
        try
        {
            string inputDirectory = Path.Combine(directory, "text-list-input_" + sw.Name); Directory.CreateDirectory(inputDirectory);
            var inputs = new JArray(); result["inputs"] = inputs;
            foreach (string name in TextListInputNames)
            {
                string source = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fixtures", name), destination = Path.Combine(inputDirectory, name);
                File.Copy(source, destination, false);
                inputs.Add(new JObject { ["path"] = destination, ["sha256"] = Hash(destination) });
            }
            object composition = Get(sw, "HmiTextLists");
            var import = composition.GetType().GetMethod("Import", new[] { typeof(DirectoryInfo), typeof(string) });
            if (import == null) throw new InvalidOperationException("Documented paired text-list import is not exposed by this installed target.");
            bool accepted = (bool)import.Invoke(composition, new object[] { new DirectoryInfo(inputDirectory), TextListInputNames[0] });
            result["nativeImportAccepted"] = accepted;
            var lists = ((IEnumerable)composition).Cast<IEngineeringObject>().Where(item => (string)item.GetAttribute("Name") == "QualificationStates").ToArray();
            result["matchingNativeListCount"] = lists.Length;
            result["readback"] = new JArray(lists.Select(Attributes));
            var exported = ExportTextLists(sw, directory, "authored"); result["nativeExport"] = exported;
            if (!accepted || lists.Length != 1 || exported.Value<bool?>("fixtureTextsPresent") != true)
                throw new InvalidOperationException("Paired native import/readback/export did not establish the named list and both languages' text.");
            result["status"] = "observed";
        }
        catch (Exception ex) { result["status"] = "failed"; result["error"] = Error(ex); }
        return result;
    }

    private static JObject ExportTextLists(Software sw, string directory, string stage)
    {
        var result = new JObject { ["status"] = "unverified", ["scope"] = "Native text-list collection plus paired text-library export; formatted text and unrelated resources remain outside this fixture" };
        try
        {
            string output = Path.Combine(directory, "text-list-export_" + stage + "_" + sw.Name); Directory.CreateDirectory(output);
            object composition = Get(sw, "HmiTextLists");
            var export = composition.GetType().GetMethod("Export", new[] { typeof(DirectoryInfo), typeof(string) });
            if (export == null) throw new InvalidOperationException("Documented paired text-list export is not exposed by this installed target.");
            var files = ((IEnumerable)export.Invoke(composition, new object[] { new DirectoryInfo(output), "QualificationTextLists" })).Cast<FileInfo>().ToArray();
            var contents = new JArray(); var artifacts = new JArray(); result["files"] = contents; result["export"] = artifacts;
            foreach (FileInfo file in files.OrderBy(item => item.Name, StringComparer.Ordinal))
            {
                if (!file.FullName.StartsWith(output + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || file.Length > 2097152)
                    throw new InvalidOperationException("Text-list native export exceeds its owned path/byte bound.");
                string hash = Hash(file.FullName);
                artifacts.Add(new JObject { ["path"] = file.FullName, ["sha256"] = hash });
                contents.Add(new JObject { ["name"] = file.Name, ["sha256"] = hash, ["text"] = File.ReadAllText(file.FullName) });
            }
            string combined = string.Join("\n", contents.OfType<JObject>().Select(item => item.Value<string>("text")));
            result["fixtureTextsPresent"] = files.Length == 2 && new[] { "QualificationStates", "en-US", "de-DE", "Qualification off", "Qualifikation aus", "Qualification ready", "Qualifikation bereit" }.All(combined.Contains);
            result["status"] = "observed";
        }
        catch (Exception ex) { result["status"] = "failed"; result["error"] = Error(ex); }
        return result;
    }

    private static JObject SeedRichContents(Software sw, string directory)
    {
        var result = new JObject { ["scriptExecuted"] = false, ["compositionCreation"] = new JArray() };
        foreach (string name in new[] { "Scripts", "HmiTextLists", "HmiGraphicLists" })
        {
            var row = new JObject { ["composition"] = name }; ((JArray)result["compositionCreation"]).Add(row);
            try
            {
                var composition = (IEngineeringComposition)Get(sw, name);
                var infos = composition.GetCreationInfos();
                row["creationInfos"] = new JArray(infos.Select(info => new JObject { ["type"] = info.Type.FullName, ["parameters"] = new JArray(info.ParameterInfos.Select(p => new JObject { ["name"] = p.Name, ["mandatory"] = p.IsMandatory })) }));
                var eligible = infos.FirstOrDefault(info => info.ParameterInfos.Any(p => p.Name.Equals("Name", StringComparison.OrdinalIgnoreCase)) && info.ParameterInfos.All(p => !p.IsMandatory || p.Name.Equals("Name", StringComparison.OrdinalIgnoreCase)));
                if (eligible == null) { row["status"] = "creation-not-exposed"; row["reason"] = "Installed typed API exposes import, not Create; native creation self-description exposes no eligible name-only constructor. Requires vendor paired source format and fixture template."; continue; }
                string parameter = eligible.ParameterInfos.Single(p => p.Name.Equals("Name", StringComparison.OrdinalIgnoreCase)).Name;
                var created = composition.Create(eligible.Type, new[] { new KeyValuePair<string, object>(parameter, "Qualification" + name) });
                row["created"] = Attributes(created); row["nativeType"] = created.GetType().FullName;
            }
            catch (Exception ex) { row["error"] = Error(ex); }
        }
        if (TextListFixture) result["textListFixture"] = SeedTextList(sw, directory);
        try
        {
            object alarm = Call(Get(sw, "DiscreteAlarms"), "Create", "QualificationAlarm");
            Set(alarm, "RaisedStateTag", "SharedValue"); Set(alarm, "RaisedStateTagBitNumber", (uint)0);
            result["discreteAlarm"] = Attributes((IEngineeringObject)alarm);
        }
        catch (Exception ex) { result["alarmError"] = Error(ex); }
        try
        {
            object screen = ((IEnumerable)Get(sw, "Screens")).Cast<object>().First();
            object events = Get(screen, "EventHandlers");
            var create = events.GetType().GetMethods().Single(m => m.Name == "Create" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.IsEnum);
            Type eventType = create.GetParameters()[0].ParameterType;
            result["screenEventTypes"] = new JArray(Enum.GetNames(eventType));
            string eventName = Enum.GetNames(eventType).First(n => n == "Loaded");
            object handler = create.Invoke(events, new[] { Enum.Parse(eventType, eventName) });
            object script = Get(handler, "Script");
            Set(script, "ScriptCode", "const observed = Tags(\"SharedValue\").Read(); // fixture source only; never executed");
            Set(script, "GlobalDefinitionAreaScriptCode", "const qualificationMarker = 21;");
            result["screenEvent"] = new JObject { ["event"] = eventName, ["scriptCode"] = Get(script, "ScriptCode").ToString(), ["globalCode"] = Get(script, "GlobalDefinitionAreaScriptCode").ToString(), ["executed"] = false };
        }
        catch (Exception ex) { result["screenEventError"] = Error(ex); }
        return result;
    }

    private static JObject SeedConnections(TiaPortal portal, Project project, Device[] hmis, string directory)
    {
        var result = new JObject { ["plcs"] = new JArray(), ["links"] = new JArray() };
        try
        {
            var selected = portal.HardwareCatalog.Find("CPU 1511-1 PN").Where(item => item.TypeName == "CPU 1511-1 PN").OrderByDescending(item => item.Version).First();
            result["plcTypeIdentifier"] = selected.TypeIdentifier;
            for (int index = 0; index < 2; index++)
            {
                var plcDevice = project.Devices.CreateWithItem(selected.TypeIdentifier, "PLC_" + (index == 0 ? "A" : "B"), "PlcDevice_" + index);
                var plcItem = Items(plcDevice.DeviceItems).Single(item => item.GetService<SoftwareContainer>()?.Software is PlcSoftware);
                var plc = (PlcSoftware)plcItem.GetService<SoftwareContainer>().Software;
                JObject security = ConfigureNewFixtureCpu(plcItem, directory);
                security["runtimeUser"] = ConfigureNewFixtureRuntimeUser(project, plcDevice, directory);
                var table = plc.TagTableGroup.TagTables.Create("QualificationTags");
                table.Tags.Create("SharedSignal", "Int", "%MW0");
                var plcRow = new JObject { ["device"] = plcDevice.Name, ["software"] = plc.Name, ["runtimeItem"] = plcItem.Name,
                    ["tag"] = new JObject { ["table"] = table.Name, ["name"] = "SharedSignal", ["type"] = "Int", ["address"] = "%MW0" } };
                ((JArray)result["plcs"]).Add(plcRow);
                plcRow["configurationProtection"] = security;
                string scl = Path.Combine(directory, "input_" + plc.Name + ".scl");
                File.WriteAllText(scl, "DATA_BLOCK \"QualificationDB\"\n{ S7_Optimized_Access := 'TRUE' }\nVERSION : 0.1\n VAR\n Value : Int;\n END_VAR\nBEGIN\nEND_DATA_BLOCK\n");
                try { plc.ExternalSourceGroup.ExternalSources.CreateFromFile("QualificationSource", scl).GenerateBlocksFromSource(); plcRow["dbCreated"] = true; }
                catch (Exception ex) { plcRow["dbError"] = Error(ex); }
                var hmiItem = Items(hmis[index].DeviceItems).Single(item => { var sw = item.GetService<SoftwareContainer>()?.Software; return sw != null && sw.GetType().FullName.StartsWith("Siemens.Engineering.Hmi"); });
                var hmi = hmiItem.GetService<SoftwareContainer>().Software;
                var link = new JObject { ["hmiSoftware"] = hmi.Name, ["plcSoftware"] = plc.Name }; ((JArray)result["links"]).Add(link);
                try
                {
                    var localNodes = Items(hmis[index].DeviceItems).SelectMany(item => item.GetService<NetworkInterface>()?.Nodes ?? Enumerable.Empty<Node>()).ToList();
                    if (localNodes.Count == 0)
                    {
                        // A newly created Unified PC runtime has no Ethernet
                        // module. Discover an installed generic PC interface
                        // and use the public native compatibility check before
                        // adding it to this independently owned fixture only.
                        link["interfacePrerequisite"] = AddPcInterface(portal, hmis[index], index);
                        localNodes = Items(hmis[index].DeviceItems).SelectMany(item => item.GetService<NetworkInterface>()?.Nodes ?? Enumerable.Empty<Node>()).ToList();
                    }
                    if (localNodes.Count == 0) throw new InvalidOperationException("Owned HMI fixture exposes no native network node after documented PC-interface compatibility probes; see interfacePrerequisite.");
                    var localNode = localNodes[0];
                    var partnerNode = Items(plcDevice.DeviceItems).SelectMany(item => item.GetService<NetworkInterface>()?.Nodes ?? Enumerable.Empty<Node>()).First();
                    var subnet = localNode.CreateAndConnectToSubnet("QualificationSubnet_" + index);
                    partnerNode.ConnectToSubnet(subnet);
                    var management = hmiItem.GetService<CommunicationManagement>();
                    if (management == null) throw new InvalidOperationException("Native HMI runtime DeviceItem exposes no CommunicationManagement.");
                    var connection = management.Connections.Create<HmiConnection>(localNode, plcItem, partnerNode);
                    link["isValid"] = connection.IsValid; link["localConnectionName"] = connection.LocalConnectionName;
                    link["exactLocalTargetEquals"] = Equals(connection.LocalTarget, hmiItem); link["exactPartnerTargetEquals"] = Equals(connection.PartnerTarget, plcItem);
                    link["localTarget"] = connection.LocalTarget.Name; link["partnerTarget"] = connection.PartnerTarget.Name;
                    if (hmi.GetType().FullName.StartsWith("Siemens.Engineering.HmiUnified."))
                    {
                        foreach (var spec in new[] { new[] { "LinkedSignal", "SharedSignal" }, new[] { "LinkedDbValue", "QualificationDB.Value" } })
                        {
                            object tag = Call(Get(hmi, "Tags"), "Create", spec[0]);
                            Set(tag, "Connection", connection.LocalConnectionName); Set(tag, "PlcTag", spec[1]);
                            // The integrated symbolic PlcTag setter derives DataType/PlcName.
                            // Native rejects later DataType writes because that field is disabled.
                            link[spec[0]] = Attributes((IEngineeringObject)tag);
                        }
                    }
                }
                catch (Exception ex) { link["error"] = Error(ex); }
                var hardwareCompile = plcDevice.GetService<ICompilable>().Compile();
                plcRow["hardwareCompile"] = new JObject { ["errorCount"] = hardwareCompile.ErrorCount, ["warningCount"] = hardwareCompile.WarningCount, ["messages"] = Messages(hardwareCompile.Messages) };
                plcRow["compile"] = Compile(new[] { (Software)plc });
            }
        }
        catch (Exception ex) { result["error"] = Error(ex); }
        return result;
    }

    private static JObject DerivePositiveBaseline(TiaPortal portal, ref Project project, string directory, string profile)
    {
        // Independently test a separate retrieved candidate only if both compiler
        // summary and the complete message tree are clear of errors.
        var evidence = new JObject { ["profile"] = profile, ["fixtureMode"] = "positive", ["deliberateInvalidReferences"] = false,
            ["status"] = "unverified", ["scope"] = "Separate compile-ready candidate derived from this run's recovered owned archive; original compile results remain unchanged",
            ["projectPath"] = project.Path.FullName, ["nativeBefore"] = new JArray(FindSoftware(project).Select(sw => Inspect(sw, directory, "derived-baseline"))) };
        try
        {
            evidence["compileBeforeTrial"] = Compile(FindSoftware(project));
            evidence["compile"] = evidence["compileBeforeTrial"].DeepClone();
            evidence["compileObservationAlias"] = "No scalar trial occurs in the derived copy; compile and compileBeforeTrial refer to the same native baseline call.";
            if (!CompileSucceeded((JArray)evidence["compileBeforeTrial"])) { evidence["status"] = "failed"; return evidence; }
            project.Save();
            string path = project.Path.FullName;
            project.Archive(new DirectoryInfo(directory), "derived-positive.zap21", ProjectArchivationMode.Compressed);
            string archive = Path.Combine(directory, "derived-positive.zap21");
            evidence["archive"] = new JObject { ["path"] = archive, ["sha256"] = Hash(archive) };
            project.Close(); project = null;
            project = portal.Projects.Open(new FileInfo(path));
            RecordProcesses(portal, profile + ":derived-reopened");
            evidence["nativeReopened"] = new JArray(FindSoftware(project).Select(sw => Inspect(sw, directory, "derived-reopened")));
            evidence["compileReopened"] = Compile(FindSoftware(project)); evidence["reopenVerified"] = true;
            project.Close(); project = null;
            string recovered = Path.Combine(directory, "derived-recovered"); Directory.CreateDirectory(recovered);
            project = portal.Projects.Retrieve(new FileInfo(archive), new DirectoryInfo(recovered));
            RecordProcesses(portal, profile + ":derived-retrieved");
            evidence["recoveredProjectPath"] = project.Path.FullName;
            evidence["nativeRecovered"] = new JArray(FindSoftware(project).Select(sw => Inspect(sw, directory, "derived-recovered")));
            evidence["compileRecovered"] = Compile(FindSoftware(project)); evidence["archiveRetrievalVerified"] = true;
            evidence["status"] = "observed";
        }
        catch (Exception ex) { evidence["status"] = "failed"; evidence["error"] = Error(ex); }
        return evidence;
    }

    private static bool CompileMessagesSucceeded(JToken messages) => messages is JArray rows && rows.All(row =>
        row is JObject && row.Value<bool?>("diagnosticCaptureIncomplete") != true &&
        new[] { "Success", "Information", "Warning" }.Contains(row.Value<string>("state")) && CompileMessagesSucceeded(row["children"]));

    private static bool CompileSucceeded(JArray results) => results.Count != 0 && results.All(token => token is JObject result &&
        result["error"] == null && result.Value<bool?>("messagesComplete") == true && result["errorCount"]?.Type == JTokenType.Integer && result.Value<int?>("errorCount") == 0 &&
        (result.Value<string>("state") == "Success" || result.Value<string>("state") == "Information" || result.Value<string>("state") == "Warning") && CompileMessagesSucceeded(result["messages"]));

    private static void EmitDerivedReport()
    {
        var profiles = new JArray();
        foreach (var original in ((JArray)Report["profiles"]).OfType<JObject>())
        {
            if (!(original["derivedPositiveBaseline"] is JObject candidate)) continue;
            var copy = (JObject)candidate.DeepClone();
            copy["selectedTarget"] = original["selectedTarget"].DeepClone();
            copy["sourceArchive"] = original["archive"].DeepClone();
            // Expected authoring relationships are retained with their origin,
            // while all derived current-object readbacks are independently read.
            foreach (string field in new[] { "connectionFixture", "fixtureAuthoring", "dynamicsFixture", "resourceFixture" })
                if (original[field] != null) copy[field] = original[field].DeepClone();
            copy["authoringEvidenceSource"] = "Original owned fixture authoring in sourceNativeReport; derived nativeBefore/nativeReopened/nativeRecovered are fresh native readbacks";
            copy["runtimeVerified"] = false; copy["visualVerified"] = false;
            profiles.Add(copy);
        }
        if (profiles.Count == 0) return;
        string source = Path.Combine(Output, "native-report.json");
        var derived = new JObject { ["schemaVersion"] = 1, ["completed"] = true, ["profiles"] = profiles,
            ["sourceNativeReport"] = new JObject { ["path"] = source, ["sha256"] = Hash(source) },
            ["seederSha256"] = Report["seederSha256"].DeepClone(), ["runtimeVerified"] = false,
            ["scope"] = "Independent additional baseline stages derived through public native archive lifecycle; source failures are not reassessed or replaced" };
        using (var writer = new StreamWriter(new FileStream(Path.Combine(Output, "derived-native-report.json"), FileMode.CreateNew, FileAccess.Write)))
            writer.Write(derived.ToString(Formatting.Indented));
    }

    private static void RecordProcesses(TiaPortal portal, string stage)
    {
        var observation = new JObject { ["stage"] = stage, ["observedUtc"] = DateTime.UtcNow.ToString("O"), ["osPortalProcesses"] = new JArray(),
            ["scope"] = "API process identity plus read-only OS observations; an OS-only PID is not assumed owned or eligible for termination" };
        if (portal != null)
            try { observation["apiProcessId"] = portal.GetCurrentProcess().Id; }
            catch (Exception ex) { observation["apiProcessError"] = Error(ex); }
        foreach (var process in System.Diagnostics.Process.GetProcessesByName("Siemens.Automation.Portal"))
            using (process)
            {
                var entry = new JObject { ["processId"] = process.Id };
                try { entry["startTimeUtc"] = process.StartTime.ToUniversalTime().ToString("O"); } catch (Exception ex) { entry["metadataError"] = Error(ex); }
                ((JArray)observation["osPortalProcesses"]).Add(entry);
            }
        if (!(Report["processObservations"] is JArray)) Report["processObservations"] = new JArray();
        ((JArray)Report["processObservations"]).Add(observation); Persist();
    }

    private static JObject ConfigureNewFixtureRuntimeUser(Project project, Device newlyCreatedDevice, string directory)
    {
        var evidence = new JObject { ["scope"] = "new seeder-owned offline CPU only", ["anonymousAccessChanged"] = false,
            ["existingProtectionDisabled"] = false, ["secretReturned"] = false, ["status"] = "unverified" };
        byte[] random = new byte[24], clear = null, encrypted = null; char[] characters = null;
        try
        {
            var device = newlyCreatedDevice.GetService<UmacDevice>();
            if (device == null) { evidence["status"] = "unavailable"; return evidence; }
            var umac = project.GetService<UmacConfigurator>() ?? throw new InvalidOperationException("New fixture project exposes no UmacConfigurator.");
            var candidates = device.AvailableDeviceFunctionRights.Where(right => right.Identifier == "ProtectionLevelFullAccess" ||
                string.Equals((right.Identifier ?? "").Split('.', '/', ':').Last(), "FullAccess", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (candidates.Length != 1) throw new InvalidOperationException("Unique native standard FullAccess identifier is required; no user or role was created.");
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(random);
            characters = new char[36]; characters[0] = 'T'; characters[1] = 'm'; characters[2] = '9'; characters[3] = '!';
            Convert.ToBase64CharArray(random, 0, random.Length, characters, 4);
            clear = Encoding.UTF8.GetBytes(characters);
            encrypted = ProtectedData.Protect(clear, Encoding.UTF8.GetBytes("HmiSeeder.RuntimeUser.v1"), DataProtectionScope.CurrentUser);
            string credential = Path.Combine(directory, "fixture-runtime-" + Guid.NewGuid().ToString("N") + ".dpapi");
            using (var stream = new FileStream(credential, FileMode.CreateNew, FileAccess.Write, FileShare.None)) stream.Write(encrypted, 0, encrypted.Length);
            evidence["credentialPath"] = credential; evidence["storage"] = "Windows DPAPI CurrentUser"; evidence["entropy"] = "HmiSeeder.RuntimeUser.v1";
            string suffix = Guid.NewGuid().ToString("N").Substring(0, 12);
            evidence["username"] = "HmiFixture_" + suffix; evidence["roleName"] = "HmiFixtureRole_" + suffix;
            ProjectUser user;
            using (var password = new SecureString())
            {
                foreach (char character in characters) password.AppendChar(character);
                password.MakeReadOnly(); user = umac.ProjectUsers.Create((string)evidence["username"], password);
            }
            evidence["status"] = "user-created"; user.ProjectUserAuthenticationType = AuthenticationType.Password;
            var role = umac.CustomRoles.Create((string)evidence["roleName"], "One newly created disposable HMI fixture CPU only");
            role.AssignDeviceFunctionRight(device, candidates[0]); user.Roles.Add(role);
            if (!user.IsActive) user.Activate();
            if (!user.IsActive || !user.Roles.Any(assigned => assigned.Identifier == role.Identifier) ||
                !role.GetAssignedDeviceFunctionRights(device).Any(right => right.Identifier == candidates[0].Identifier))
                throw new InvalidOperationException("New fixture runtime user/right readback differs.");
            evidence["functionRightIdentifier"] = candidates[0].Identifier; evidence["roleIdentifier"] = role.Identifier;
            evidence["isActive"] = user.IsActive; evidence["status"] = "configured";
        }
        catch (Exception ex) { evidence["status"] = "failed"; evidence["error"] = Error(ex); }
        finally
        {
            Array.Clear(random, 0, random.Length); if (characters != null) Array.Clear(characters, 0, characters.Length);
            if (clear != null) Array.Clear(clear, 0, clear.Length); if (encrypted != null) Array.Clear(encrypted, 0, encrypted.Length);
        }
        return evidence;
    }

    private static JObject ConfigureNewFixtureCpu(DeviceItem newlyCreatedCpu, string directory)
    {
        // Only called immediately after this seeder creates the CPU. No existing
        // CPU or protected user project enters this credential path.
        var evidence = new JObject { ["scope"] = "new seeder-owned offline CPU", ["secretReturned"] = false };
        var service = newlyCreatedCpu.GetService<PlcMasterSecretConfigurator>();
        if (service == null) { evidence["status"] = "unavailable"; return evidence; }
        evidence["before"] = service.MasterSecretConfiguration.ToString();
        if (service.MasterSecretConfiguration.ToString().StartsWith("WithPassword", StringComparison.Ordinal))
        { evidence["status"] = "existing-protection-retained"; return evidence; }
        byte[] random = new byte[24], clear = null, encrypted = null;
        char[] characters = null;
        try
        {
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(random);
            characters = new char[36]; characters[0] = 'T'; characters[1] = 'm'; characters[2] = '9'; characters[3] = '!';
            Convert.ToBase64CharArray(random, 0, random.Length, characters, 4);
            clear = Encoding.UTF8.GetBytes(characters);
            encrypted = ProtectedData.Protect(clear, Encoding.UTF8.GetBytes("HmiSeeder.CpuSecret.v1"), DataProtectionScope.CurrentUser);
            string credential = Path.Combine(directory, "fixture-cpu-" + Guid.NewGuid().ToString("N") + ".dpapi");
            using (var stream = new FileStream(credential, FileMode.CreateNew, FileAccess.Write, FileShare.None)) stream.Write(encrypted, 0, encrypted.Length);
            evidence["credentialPath"] = credential; evidence["storage"] = "Windows DPAPI CurrentUser";
            evidence["entropy"] = "HmiSeeder.CpuSecret.v1";
            using (var secret = new SecureString())
            {
                foreach (char character in characters) secret.AppendChar(character);
                secret.MakeReadOnly(); service.Protect(secret);
            }
            evidence["after"] = service.MasterSecretConfiguration.ToString(); evidence["status"] = "configured";
        }
        catch (Exception ex) { evidence["status"] = "failed"; evidence["error"] = Error(ex); }
        finally
        {
            Array.Clear(random, 0, random.Length);
            if (characters != null) Array.Clear(characters, 0, characters.Length);
            if (clear != null) Array.Clear(clear, 0, clear.Length);
            if (encrypted != null) Array.Clear(encrypted, 0, encrypted.Length);
        }
        return evidence;
    }

    private static JObject ConfigureRuntimePrerequisites(Software software)
    {
        var evidence = new JObject { ["software"] = software.Name, ["startScreen"] = "Overview_A", ["secretValuesRead"] = false };
        try
        {
            object runtime = software.GetType().FullName.StartsWith("Siemens.Engineering.HmiUnified.") ? Get(software, "RuntimeSettings") : software;
            var native = (IEngineeringObject)runtime;
            var attributes = native.GetAttributeInfos();
            evidence["nativeType"] = runtime.GetType().FullName;
            evidence["attributeCapabilities"] = new JArray(attributes.Select(attribute => new JObject { ["name"] = attribute.Name, ["access"] = attribute.AccessMode.ToString() }));
            var property = PublicProperty(runtime, "StartScreen");
            if (property != null && property.CanWrite) Set(runtime, "StartScreen", "Overview_A");
            else if (attributes.Any(attribute => attribute.Name == "StartScreen" && (attribute.AccessMode & EngineeringAttributeAccessMode.Write) != 0))
                native.SetAttribute("StartScreen", "Overview_A");
            else
            {
                evidence["status"] = "blocked";
                evidence["reason"] = "No public StartScreen setter in this exact native target self-description. Supply a natively configured fixture template; no private project payload is changed.";
                return evidence;
            }
            evidence["startScreenReadback"] = property != null ? Get(runtime, "StartScreen").ToString() : native.GetAttribute("StartScreen")?.ToString();
            evidence["status"] = (string)evidence["startScreenReadback"] == "Overview_A" ? "start-screen-configured" : "failed";
            evidence["remainingPrerequisites"] = "The native compiler independently validates runtime security defaults. No password, transfer-security or certificate setting is guessed or disabled.";
        }
        catch (Exception ex) { evidence["status"] = "failed"; evidence["error"] = Error(ex); }
        return evidence;
    }

    private static JObject AddPcInterface(TiaPortal portal, Device device, int index)
    {
        var evidence = new JObject { ["device"] = device.Name, ["beforeItems"] = new JArray(Items(device.DeviceItems).Select(item => new JObject { ["name"] = item.Name, ["typeIdentifier"] = item.TypeIdentifier })), ["catalogQueries"] = new JArray(), ["compatibility"] = new JArray() };
        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        foreach (string query in new[] { "IE General", "IE Allgemein" })
        {
            var matches = portal.HardwareCatalog.Find(query);
            ((JArray)evidence["catalogQueries"]).Add(new JObject { ["query"] = query, ["total"] = matches.Count,
                ["matches"] = new JArray(matches.Take(40).Select(item => new JObject { ["typeIdentifier"] = item.TypeIdentifier, ["typeName"] = item.TypeName, ["catalogPath"] = item.CatalogPath })) });
            foreach (var match in matches.Where(item => string.Equals(item.TypeName, "IE General", StringComparison.OrdinalIgnoreCase) || string.Equals(item.TypeName, "IE Allgemein", StringComparison.OrdinalIgnoreCase)).Take(16))
            {
                if (!identifiers.Add(match.TypeIdentifier)) continue;
                foreach (HardwareObject owner in new[] { (HardwareObject)device }.Concat(Items(device.DeviceItems)))
                {
                    for (int slot = 1; slot <= 32; slot++)
                    {
                        string name = "QualificationEthernet_" + index;
                        bool allowed = owner.CanPlugNew(match.TypeIdentifier, name, slot);
                        if (!allowed) continue;
                        var attempt = new JObject { ["owner"] = owner.Name, ["typeIdentifier"] = match.TypeIdentifier, ["slot"] = slot, ["canPlugNew"] = true };
                        ((JArray)evidence["compatibility"]).Add(attempt);
                        var created = owner.PlugNew(match.TypeIdentifier, name, slot);
                        attempt["createdName"] = created.Name; attempt["createdTypeIdentifier"] = created.TypeIdentifier;
                        evidence["afterNetworkNodes"] = new JArray(Items(device.DeviceItems).SelectMany(item => item.GetService<NetworkInterface>()?.Nodes ?? Enumerable.Empty<Node>()).Select(node => node.Name));
                        return evidence;
                    }
                }
            }
        }
        evidence["status"] = "no-native-compatible-generic-PC-interface-found";
        return evidence;
    }

    private static JArray SeedDynamics(Software sw)
    {
        var result = new JArray();
        object screen = ((IEnumerable)Get(sw, "Screens")).Cast<object>().First();
        foreach (string mode in PositiveFixture ? new[] { "Direct" } : new[] { "Direct", "Indirect", "Broken", "Formula" })
        {
            var row = new JObject { ["mode"] = mode }; result.Add(row);
            try
            {
                object items = Get(screen, "ScreenItems");
                Type rectangle = sw.GetType().Assembly.GetType("Siemens.Engineering.HmiUnified.UI.Shapes.HmiRectangle", true);
                object item = items.GetType().GetMethods().Single(method => method.Name == "Create" && method.IsGenericMethodDefinition && method.GetParameters().Length == 1).MakeGenericMethod(rectangle).Invoke(items, new object[] { mode + "Reference" });
                object dynamics = Get(item, "Dynamizations");
                Type tagDynamics = sw.GetType().Assembly.GetType("Siemens.Engineering.HmiUnified.UI.Dynamization.TagDynamization", true);
                object dyn = dynamics.GetType().GetMethods().Single(method => method.Name == "Create" && method.IsGenericMethodDefinition && method.GetParameters().Length == 1).MakeGenericMethod(tagDynamics).Invoke(dynamics, new object[] { "Left" });
                Set(dyn, "Tag", mode == "Broken" ? "MissingTag_Qualification" : mode == "Direct" ? "LinkedSignal" : "SharedValue");
                Set(dyn, "UseIndirectAddressing", mode == "Indirect");
                if (mode == "Formula") { object converter = Get(dyn, "ValueConverter"); Set(converter, "IsFormulaSelected", true); Set(converter, "Formula", "'SharedValue' + 2"); row["converter"] = Attributes((IEngineeringObject)converter); }
                row["nativeReadback"] = Attributes((IEngineeringObject)dyn); row["created"] = true;
            }
            catch (Exception ex) { row["error"] = Error(ex); }
        }
        return result;
    }

    private static JObject ScalarTrial(Software sw, string directory)
    {
        var result = new JObject { ["operation"] = "Classic standalone screen BackColor", ["qualified"] = false };
        if (!sw.GetType().FullName.StartsWith("Siemens.Engineering.Hmi.")) { result["status"] = "unverified"; result["reason"] = "Unified screen preservation requires complete events/dynamics export or readback."; return result; }
        object composition = Get(Get(sw, "ScreenFolder"), "Screens");
        var screen = ((IEnumerable)composition).Cast<IEngineeringObject>().FirstOrDefault();
        if (screen == null) { result["status"] = "blocked"; result["reason"] = "Native fixture has no screen."; return result; }
        string beforeFile = Path.Combine(directory, "scalar-before.xml"), afterFile = Path.Combine(directory, "scalar-after.xml"), restoreFile = Path.Combine(directory, "scalar-restored.xml");
        var export = screen.GetType().GetMethod("Export", new[] { typeof(FileInfo), typeof(ExportOptions) });
        try
        {
            export.Invoke(screen, new object[] { new FileInfo(beforeFile), ExportOptions.WithDefaults });
            result["before"] = beforeFile;
            result["attributes"] = new JArray(screen.GetAttributeInfos().Select(attr => new JObject { ["name"] = attr.Name, ["access"] = attr.AccessMode.ToString() }));
            try { object value = screen.GetAttribute("BackColor"); result["attributeNativeType"] = value?.GetType().FullName; result["attributeValue"] = value?.ToString(); }
            catch (Exception ex) { result["getAttributeError"] = Error(ex); }
            var document = XDocument.Load(beforeFile); var color = document.Root.Element("Hmi.Screen.Screen").Element("AttributeList").Element("BackColor");
            if (color == null) throw new InvalidOperationException("Actual native screen export has no BackColor.");
            string previous = color.Value; color.Value = "224, 240, 255";
            string input = Path.Combine(directory, "scalar-input.xml"); document.Save(input);
            composition.GetType().GetMethod("Import", new[] { typeof(FileInfo), typeof(ImportOptions) }).Invoke(composition, new object[] { new FileInfo(input), ImportOptions.Override });
            screen = ((IEnumerable)composition).Cast<IEngineeringObject>().Single(item => (string)item.GetAttribute("Name") == (string)document.Root.Element("Hmi.Screen.Screen").Element("AttributeList").Element("Name"));
            export.Invoke(screen, new object[] { new FileInfo(afterFile), ExportOptions.WithDefaults }); result["after"] = afterFile;
            var after = XDocument.Load(afterFile); var afterColor = after.Root.Element("Hmi.Screen.Screen").Element("AttributeList").Element("BackColor");
            result["exactScalarReadback"] = afterColor.Value == "224, 240, 255";
            result["nativeChangedCompile"] = Compile(new[] { sw });
            color.Value = previous; afterColor.Value = previous;
            foreach (var created in document.Root.Elements("DocumentInfo").Elements("Created").ToArray()) created.Remove();
            foreach (var created in after.Root.Elements("DocumentInfo").Elements("Created").ToArray()) created.Remove();
            result["selectedScreenUnrelatedPayloadEqual"] = XNode.DeepEquals(document, after);
            composition.GetType().GetMethod("Import", new[] { typeof(FileInfo), typeof(ImportOptions) }).Invoke(composition, new object[] { new FileInfo(beforeFile), ImportOptions.Override });
            screen = ((IEnumerable)composition).Cast<IEngineeringObject>().Single(item => (string)item.GetAttribute("Name") == (string)document.Root.Element("Hmi.Screen.Screen").Element("AttributeList").Element("Name"));
            export.Invoke(screen, new object[] { new FileInfo(restoreFile), ExportOptions.WithDefaults }); result["restored"] = restoreFile;
            result["status"] = "observed"; result["reason"] = "Native import/readback/compile and restoring import exercised on standalone fixture; production prepare/apply, changed-state reopen, whole-HMI preservation and visual acceptance remain separate.";
        }
        catch (Exception ex) { result["status"] = "failed"; result["error"] = Error(ex); }
        return result;
    }

    private static JObject Inspect(Software sw, string directory, string stage)
    {
        var result = new JObject { ["name"] = sw.Name, ["type"] = sw.GetType().FullName, ["attributes"] = Attributes(sw), ["sections"] = new JObject() };
        string[] classic = { "Connections", "ScreenFolder", "ScreenPopupFolder", "ScreenSlideinFolder", "ScreenTemplateFolder", "ScreenGlobalElements", "ScreenOverview", "TagFolder", "VBScriptFolder", "TextLists", "GraphicLists" };
        string[] unified = { "Connections", "Tags", "TagTables", "Screens", "ScreenGroups", "DiscreteAlarms", "AnalogAlarms", "Scripts", "HmiTextLists", "HmiGraphicLists" };
        int budget = 2000;
        foreach (string section in sw.GetType().FullName.StartsWith("Siemens.Engineering.HmiUnified.") ? unified : classic)
        {
            // A present getter returning null is an absent singleton (e.g. no global screen), not an error.
            try { var info = PublicProperty(sw, section) ?? throw new InvalidOperationException("Unavailable property: " + section);
                ((JObject)result["sections"])[section] = Walk(info.GetValue(sw), directory, stage + "_" + sw.Name + "_" + section, 0, ref budget); }
            catch (Exception ex) { ((JObject)result["sections"])[section] = new JObject { ["error"] = Error(ex) }; }
        }
        if (TextListFixture && sw.GetType().FullName.StartsWith("Siemens.Engineering.HmiUnified.")) result["textListEvidence"] = ExportTextLists(sw, directory, stage);
        result["remainingBudget"] = budget; return result;
    }

    private static JToken Walk(object value, string directory, string path, int depth, ref int budget)
    {
        if (value == null) return JValue.CreateNull();
        if (depth > 12 || budget-- <= 0) return new JObject { ["truncated"] = true };
        if (value is IEnumerable enumerable && !(value is string))
        { var entries = new JArray(); foreach (var child in enumerable) { if (budget <= 0) { entries.Add(new JObject { ["truncated"] = true }); break; } entries.Add(Walk(child, directory, path + "_" + entries.Count, depth + 1, ref budget)); } return entries; }
        var row = new JObject { ["nativeType"] = value.GetType().FullName, ["attributes"] = Attributes(value as IEngineeringObject) };
        var export = value.GetType().GetMethod("Export", new[] { typeof(FileInfo), typeof(ExportOptions) });
        if (export != null)
        {
            string file = Path.Combine(directory, path + ".xml");
            try { export.Invoke(value, new object[] { new FileInfo(file), ExportOptions.WithDefaults }); row["export"] = new JObject { ["path"] = file, ["sha256"] = Hash(file) }; }
            catch (Exception ex) { row["exportError"] = Error(ex); }
        }
        foreach (string property in new[] { "Screens", "ScreenTemplates", "ScreenPopups", "ScreenSlideins", "ScreenItems", "Folders", "TagTables", "Tags", "VBScripts", "Dynamizations", "ValueConverter", "EventHandlers", "PropertyEventHandlers", "Script" })
            if (value.GetType().GetProperty(property) != null)
                try { row[property] = Walk(Get(value, property), directory, path + "_" + property, depth + 1, ref budget); }
                catch (Exception ex) { row[property] = new JObject { ["error"] = Error(ex) }; }
        return row;
    }

    private static JObject Attributes(IEngineeringObject obj)
    {
        var result = new JObject(); if (obj == null) return result;
        foreach (var attr in obj.GetAttributeInfos().Take(256))
        {
            if ((attr.AccessMode & EngineeringAttributeAccessMode.Read) == 0) continue;
            try { object value = obj.GetAttribute(attr.Name); if (value == null || value is string || value.GetType().IsPrimitive || value.GetType().IsEnum || value is decimal || value is Guid) result[attr.Name] = value == null ? JValue.CreateNull() : value.GetType().IsEnum ? new JValue(value.ToString()) : JToken.FromObject(value); else result[attr.Name] = new JObject { ["nativeType"] = value.GetType().FullName, ["notScalar"] = true }; }
            catch (Exception ex) { result[attr.Name] = new JObject { ["error"] = Error(ex) }; }
        }
        return result;
    }
    private static JArray Compile(IEnumerable<Software> software) => new JArray(software.Select(sw => {
        try { object target = sw; ICompilable compiler = null; for (int depth = 0; target != null && depth < 8 && compiler == null; depth++) { compiler = (target as IEngineeringServiceProvider)?.GetService<ICompilable>(); if (compiler == null) target = target.GetType().GetProperty("Parent")?.GetValue(target); } if (compiler == null) throw new InvalidOperationException("No public ICompilable service on software or exposed ancestors."); var result = compiler.Compile(); var messages = Messages(result.Messages); return new JObject { ["name"] = sw.Name, ["compileTargetType"] = target.GetType().FullName, ["errorCount"] = result.ErrorCount, ["warningCount"] = result.WarningCount, ["state"] = result.State.ToString(), ["messages"] = messages, ["messagesComplete"] = HmiCompilerDiagnostics.IsComplete(messages) }; }
        catch (Exception ex) { return new JObject { ["name"] = sw.Name, ["error"] = Error(ex) }; }
    }));
    private static JArray Messages(IEnumerable<CompilerResultMessage> messages) => HmiCompilerDiagnostics.Capture(messages,
        message => message.State.ToString(), message => message.Description, message => message.Path, message => message.Messages);

    private static int Visual(string archivePath, string outputPath)
    {
        string archive = Path.GetFullPath(archivePath), output = Path.GetFullPath(outputPath);
        string reportPath = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(archive)), "native-report.json");
        var source = JObject.Parse(File.ReadAllText(reportPath));
        var profile = ((JArray)source["profiles"]).OfType<JObject>().Single(row => string.Equals(row["archive"]?.Value<string>("path"), archive, StringComparison.OrdinalIgnoreCase));
        if (Hash(archive) != profile["archive"].Value<string>("sha256") || !Path.GetFileName(profile.Value<string>("projectPath")).StartsWith("TiaMcpHmi_", StringComparison.Ordinal)) throw new InvalidOperationException("Visual mode accepts only an unchanged independently generated fixture archive.");
        if (Directory.Exists(output) || File.Exists(output)) throw new InvalidOperationException("Visual output must be a fresh directory.");
        Directory.CreateDirectory(output);
        var receipt = new JObject { ["sourceArchive"] = archive, ["archiveSha256"] = Hash(archive), ["ownsPortalProcess"] = true, ["visualVerified"] = false, ["cleanup"] = "pending" };
        using (var portal = new TiaPortal(TiaPortalMode.WithUserInterface))
        {
            receipt["processId"] = portal.GetCurrentProcess().Id;
            var project = portal.Projects.Retrieve(new FileInfo(archive), new DirectoryInfo(output));
            receipt["projectPath"] = project.Path.FullName; receipt["compile"] = Compile(FindSoftware(project));
            File.WriteAllText(Path.Combine(output, "visual-receipt.json"), receipt.ToString(Formatting.Indented));
            Console.WriteLine(receipt.ToString(Formatting.None));
            var stop = DateTime.UtcNow.AddMinutes(15);
            while (!File.Exists(Path.Combine(output, "close-visual")) && DateTime.UtcNow < stop) System.Threading.Thread.Sleep(500);
            project.Close();
            portal.GetCurrentProcess().Dispose();
        }
        receipt["cleanup"] = "owned-portal-disposed";
        File.WriteAllText(Path.Combine(output, "visual-receipt.json"), receipt.ToString(Formatting.Indented));
        return 0;
    }
    private static IEnumerable<Software> FindSoftware(Project project) => project.Devices.Concat(project.UngroupedDevicesGroup.Devices).SelectMany(device => Items(device.DeviceItems)).Select(item => item.GetService<SoftwareContainer>()?.Software).Where(sw => sw != null && (sw.GetType().FullName.StartsWith("Siemens.Engineering.Hmi.") || sw.GetType().FullName.StartsWith("Siemens.Engineering.HmiUnified.")));
    private static IEnumerable<DeviceItem> Items(IEnumerable<DeviceItem> source) { foreach (var item in source) { yield return item; foreach (var child in Items(item.DeviceItems)) yield return child; } }
    private static PropertyInfo PublicProperty(object obj, string property) => obj.GetType().GetProperty(property) ?? obj.GetType().GetInterfaces().Select(type => type.GetProperty(property)).FirstOrDefault(info => info != null);
    private static object Get(object obj, string property) => PublicProperty(obj, property)?.GetValue(obj) ?? throw new InvalidOperationException("Unavailable property: " + property);
    private static object Call(object obj, string method, string argument) => obj.GetType().GetMethod(method, new[] { typeof(string) })?.Invoke(obj, new object[] { argument }) ?? throw new InvalidOperationException("Unavailable method: " + method);
    private static void Set(object obj, string property, object value) { var info = PublicProperty(obj, property) ?? throw new InvalidOperationException("Unavailable property: " + property); info.SetValue(obj, info.PropertyType.IsEnum && value is string text ? Enum.Parse(info.PropertyType, text) : value.GetType() != info.PropertyType && value is IConvertible ? Convert.ChangeType(value, info.PropertyType) : value); }
    private static JObject Error(Exception ex) { while (ex is TargetInvocationException && ex.InnerException != null) ex = ex.InnerException; return new JObject { ["type"] = ex.GetType().FullName, ["message"] = ex.Message, ["hresult"] = ex.HResult }; }
    private static string Hash(string path) { using (var sha = SHA256.Create()) using (var stream = File.OpenRead(path)) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(); }
    private static void Persist() { File.WriteAllText(Path.Combine(Output, "native-report.json"), Report.ToString(Formatting.Indented)); }
}
