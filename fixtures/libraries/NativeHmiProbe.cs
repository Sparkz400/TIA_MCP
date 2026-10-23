// Compiled by scripts/qualify_library_hmi.ps1 against the installed V21 PublicAPI.
// This executable never enumerates/attaches to existing TIA instances.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Web.Script.Serialization;
using Siemens.Engineering;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;

internal static class NativeHmiProbe
{
    private static string api, output, unifiedInternalConnection;
    private static readonly Dictionary<string, object> report = new Dictionary<string, object>();
    private static readonly List<object> stages = new List<object>();
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 3) { Console.Error.WriteLine("API_DIRECTORY FRESH_OUTPUT_DIRECTORY PROFILE_JSON"); return 2; }
        api = Path.GetFullPath(args[0]); output = Path.GetFullPath(args[1]);
        if (Directory.Exists(output)) { Console.Error.WriteLine("Choose a fresh output directory; evidence is never overwritten."); return 2; }
        Directory.CreateDirectory(output);
        AppDomain.CurrentDomain.AssemblyResolve += delegate(object sender, ResolveEventArgs e) {
            string path = Path.Combine(api, new AssemblyName(e.Name).Name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };
        report["schemaVersion"] = 1; report["startedAtUtc"] = DateTime.UtcNow.ToString("o");
        report["binary"] = FileIdentity(Assembly.GetExecutingAssembly().Location);
        report["vendorAssemblies"] = new[]{"Siemens.Engineering.Base.dll","Siemens.Engineering.WinCC.dll","Siemens.Engineering.WinCCUnified.dll"}.Select(n=>FileIdentity(Path.Combine(api,n))).ToArray();
        report["stages"] = stages; report["runtimeVerified"] = false; report["visualVerified"] = false;
        report["scope"] = "New owned TIA process and fresh project only; no existing project or process attachment.";
        try {
            var profile = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(args[2]));
            report["profile"] = profile;
            report["profileSource"] = ContentIdentity(Path.GetFullPath(args[2]));
            Run(profile); report["completed"] = true; Save(); return 0;
        } catch (Exception e) { report["completed"] = false; report["failure"] = e.ToString(); Save(); Console.Error.WriteLine(e.Message); return 1; }
    }
    private static object FileIdentity(string path) {
        using(var sha=SHA256.Create()) using(var stream=File.OpenRead(path))
            return new { path, sha256=BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","").ToLowerInvariant(), assemblyVersion=AssemblyName.GetAssemblyName(path).Version.ToString() };
    }
    private static object ContentIdentity(string path) {
        using(var sha=SHA256.Create()) using(var stream=File.OpenRead(path))
            return new { path, length=stream.Length, sha256=BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","").ToLowerInvariant() };
    }
    private static void Save() { File.WriteAllText(Path.Combine(output, "qualification.json"), new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 }.Serialize(report)); }
    private static void Stage(string name, Action operation) {
        var stage = new Dictionary<string, object> { {"name", name}, {"status", "invoking"}, {"startedAtUtc", DateTime.UtcNow.ToString("o")} };
        stages.Add(stage); Save(); Console.WriteLine("START " + name);
        try { operation(); stage["status"] = "passed"; } catch(Exception ex) { stage["status"] = "failed"; stage["failure"] = ex.ToString(); throw; }
        finally { stage["finishedAtUtc"] = DateTime.UtcNow.ToString("o"); Save(); }
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Run(Dictionary<string, object> profile) {
        TiaPortal portal = null; Project project = null;
        try {
            Stage("start-owned-tia", delegate { portal = new TiaPortal(TiaPortalMode.WithoutUserInterface); });
            Stage("create-owned-project", delegate { project = portal.Projects.Create(new DirectoryInfo(output), "LibraryHmiProbe"); });
            Stage("catalog", delegate {
                var entries = new List<object>();
                foreach (string filter in new[]{"TP700", "Unified", "RT Advanced"})
                    foreach (var entry in portal.HardwareCatalog.Find(filter).Take(40))
                        entries.Add(new { filter, entry.TypeIdentifier, entry.TypeName, version=entry.Version == null ? null : entry.Version.ToString(), entry.ArticleNumber });
                report["catalog"] = entries;
            });
            if (profile.ContainsKey("typeIdentifier")) {
                string family = Convert.ToString(profile["family"]); string typeId = Convert.ToString(profile["typeIdentifier"]);
                if (family != "classic" && family != "unified") throw new ArgumentException("Explicit family classic or unified required.");
                Device source=null, consumer=null;
                Stage("create-explicit-hmi-devices", delegate {
                    string filter=typeId; int colon=filter.IndexOf(':'); if(colon>=0) filter=filter.Substring(colon+1); int slash=filter.IndexOf('/'); if(slash>=0) filter=filter.Substring(0,slash);
                    if (!portal.HardwareCatalog.Find(filter).Any(e => e.TypeIdentifier == typeId)) throw new InvalidOperationException("Explicit identifier was not returned by the installed catalog.");
                    source = project.Devices.CreateWithItem(typeId, "HmiSource", "HmiSourceStation");
                    consumer = project.Devices.CreateWithItem(typeId, "HmiConsumer", "HmiConsumerStation");
                });
                if (family == "classic") Classic(project, source, consumer, profile); else Unified(project, source);
                Stage("save-project", delegate { project.Save(); });
                string path = project.Path.FullName;
                Stage("close-owned-project", delegate { project.Close(); project=null; });
                Stage("reopen-owned-project", delegate { project=portal.Projects.Open(new FileInfo(path)); });
                Stage("verify-reopened-fixture", delegate { VerifyReopened(project, family); });
                report["projectPath"] = path;
                Stage("archive-owned-project", delegate { project.Archive(new DirectoryInfo(output), "LibraryHmiProbe.zap21", ProjectArchivationMode.Compressed); });
            }
        } finally {
            if (portal != null) Stage("dispose-owned-portal-connection", delegate { portal.Dispose(); });
        }
    }
    private static IEnumerable<Software> SoftwareOf(Device device) {
        foreach(var item in device.DeviceItems) foreach(var software in SoftwareOf(item)) yield return software;
    }
    private static IEnumerable<Software> SoftwareOf(DeviceItem item) {
        var container=item.GetService<SoftwareContainer>(); if(container != null && container.Software != null) yield return container.Software;
        foreach(var child in item.DeviceItems) foreach(var software in SoftwareOf(child)) yield return software;
    }
    private static void Compile(IEngineeringServiceProvider provider) {
        var compiler=provider.GetService<ICompilable>();
        if(compiler==null) throw new InvalidOperationException("Native HMI compiler service unavailable.");
        var compiled=compiler.Compile();
        var messages=new List<object>(); bool truncated=false;
        CompileMessages(compiled.Messages,messages,0,ref truncated);
        report["compile"] = new { errors=compiled.ErrorCount, warnings=compiled.WarningCount, state=compiled.State.ToString(), messages, messagesTruncated=truncated };
        if(compiled.ErrorCount != 0) throw new InvalidOperationException("Native HMI compile failed; inspect fixture diagnostics.");
    }
    private static void CompileMessages(IEnumerable<CompilerResultMessage> source, List<object> target, int depth, ref bool truncated) {
        if(depth>64) { truncated=true; return; }
        foreach(var message in source) {
            if(target.Count>=2000) { truncated=true; return; }
            target.Add(new { description=message.Description, path=message.Path, state=message.State.ToString(), depth });
            CompileMessages(message.Messages,target,depth+1,ref truncated);
            if(truncated) return;
        }
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void VerifyReopened(Project project, string family) {
        var source=project.Devices.Single(d => d.Name=="HmiSourceStation");
        if(family=="classic") {
            var target=project.Devices.Single(d => d.Name=="HmiConsumerStation");
            var sourceHmi=SoftwareOf(source).OfType<Siemens.Engineering.Hmi.HmiTarget>().Single();
            var targetHmi=SoftwareOf(target).OfType<Siemens.Engineering.Hmi.HmiTarget>().Single();
            var name=sourceHmi.ScreenFolder.Screens.First().Name;
            var copied=targetHmi.ScreenFolder.Screens.Find(name);
            if(copied==null) throw new InvalidOperationException("Classic master-copy screen missing after fresh reopen.");
            copied.Export(new FileInfo(Path.Combine(output,"classic-reopened-screen.xml")),ExportOptions.WithDefaults);
        } else {
            var hmi=SoftwareOf(source).OfType<Siemens.Engineering.HmiUnified.HmiSoftware>().Single();
            var screen=hmi.Screens.Find("TiaMcpLibraryScreen"); var tag=hmi.Tags.Find("TiaMcpLibraryBool");
            if(screen==null || screen.Width!=800 || screen.Height!=480 || tag==null || tag.DataType!="Bool" || tag.Connection!=unifiedInternalConnection || !string.IsNullOrEmpty(tag.PlcTag))
                throw new InvalidOperationException("Unified screen/tag differs after fresh reopen.");
        }
        report["persistenceVerified"] = true;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Classic(Project project, Device sourceDevice, Device consumerDevice, Dictionary<string, object> profile) {
        var source=SoftwareOf(sourceDevice).OfType<Siemens.Engineering.Hmi.HmiTarget>().Single();
        var consumer=SoftwareOf(consumerDevice).OfType<Siemens.Engineering.Hmi.HmiTarget>().Single();
        report["nativeFamily"] = source.GetType().FullName;
        if(profile.ContainsKey("screenXml")) Stage("import-authored-classic-screen-fixture", delegate {
            string path=Path.GetFullPath(Convert.ToString(profile["screenXml"]));
            report["screenFixture"] = ContentIdentity(path); Save();
            source.ScreenFolder.Screens.Import(new FileInfo(path),ImportOptions.None);
        });
        var screen=source.ScreenFolder.Screens.FirstOrDefault();
        if(screen==null) throw new InvalidOperationException("Native Classic device has no default screen. Supply an authored screen fixture; no XML schema is fabricated.");
        Stage("export-native-classic-screen", delegate { screen.Export(new FileInfo(Path.Combine(output,"classic-source-screen.xml")),ExportOptions.WithDefaults); });
        Siemens.Engineering.Library.MasterCopies.MasterCopy master=null;
        Stage("author-classic-screen-master", delegate { master=project.ProjectLibrary.MasterCopyFolder.MasterCopies.Create(screen); });
        // Destination's generated default screen may collide. It is fixture-owned and its explicit deletion is recorded.
        Stage("remove-owned-default-collision", delegate { var existing=consumer.ScreenFolder.Screens.Find(screen.Name); if(existing!=null) existing.Delete(); });
        Stage("instantiate-classic-screen-master", delegate {
            var copied=consumer.ScreenFolder.Screens.CreateFrom(master);
            copied.Export(new FileInfo(Path.Combine(output,"classic-consumer-screen.xml")),ExportOptions.WithDefaults);
            if(copied.Name != screen.Name) throw new InvalidOperationException("Copied screen name mismatch.");
        });
        Stage("compile-classic-consumer", delegate { Compile(consumer); });
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Unified(Project project, Device sourceDevice) {
        var hmi=SoftwareOf(sourceDevice).OfType<Siemens.Engineering.HmiUnified.HmiSoftware>().Single();
        report["nativeFamily"] = hmi.GetType().FullName;
        Stage("create-unified-screen-and-tag", delegate {
            var screen=hmi.Screens.Create("TiaMcpLibraryScreen"); screen.Width=800; screen.Height=480;
            var table=hmi.TagTables.Create("TiaMcpLibraryTags"); var tag=table.Tags.Create("TiaMcpLibraryBool");
            unifiedInternalConnection=tag.Connection; tag.DataType="Bool";
            report["unifiedReadback"]=new { screen.Name, screen.Width, screen.Height, tagType=tag.DataType, tagName=tag.Name, connection=tag.Connection };
            Save();
            if(tag.DataType!="Bool" || tag.Connection!=unifiedInternalConnection || !string.IsNullOrEmpty(tag.PlcTag)
                || (!string.IsNullOrEmpty(tag.Connection) && hmi.Connections.Find(tag.Connection)!=null))
                throw new InvalidOperationException("Internal tag readback mismatch: type="+tag.DataType+", connection="+tag.Connection+", plcTag="+tag.PlcTag+", scope="+tag.Scope+", accessMode="+tag.AccessMode);
        });
        report["compileScope"] = "Owning Device: hardware and software; HmiSoftware does not expose IEngineeringServiceProvider.";
        Stage("compile-unified", delegate { Compile(sourceDevice); });
        report["faceplateQualification"] = "Not run: an authored released Unified faceplate/UDT library fixture is required.";
    }
}
