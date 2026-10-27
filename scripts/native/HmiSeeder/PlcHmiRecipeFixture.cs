using System;
using Version = System.Version;
using System.Collections;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.Library;
using Siemens.Engineering.Library.Types;
using Siemens.Engineering.SW;

// Independent fixture authoring for the combined PLC/HMI recipe probe (`plchmi`). The production
// bridge only prepares plans against this project; it never authors any of this content.
internal static partial class Program
{
    private static bool PlcHmiFixture;

    // PLC_A: released MotorStatus 1.0.0 use + MotorDB.status + two Unified tags bound to its fields.
    // Library: MotorDrive 1.0.0 (FB using MotorStatus 1.0.0) and MotorStatus 1.0.1 (speed Real -> LReal).
    // PLC_B: Bool tags for a combined install plan; its temporary 1.0.1 test instance is removed.
    private static JObject SeedPlcHmiRecipe(Project project, Software hmiA, string connectionA, string directory)
    {
        var result = new JObject { ["status"] = "failed", ["authoredBy"] = "HmiSeeder raw Openness; not the production bridge" };
        try
        {
            var plcs = project.Devices.SelectMany(d => Items(d.DeviceItems)).Select(i => i.GetService<SoftwareContainer>()?.Software as PlcSoftware).Where(p => p != null).OrderBy(p => p.Name).ToArray();
            if (plcs.Length != 2) throw new InvalidOperationException("The plchmi fixture requires exactly the two seeded PLCs.");
            PlcSoftware a = plcs[0], b = plcs[1];
            string v1 = Directory.CreateDirectory(Path.Combine(directory, "plchmi-v1")).FullName, v2 = Directory.CreateDirectory(Path.Combine(directory, "plchmi-v2")).FullName, fb = Directory.CreateDirectory(Path.Combine(directory, "plchmi-fb")).FullName;
            File.WriteAllText(Path.Combine(v1, "MotorStatus.udt"), "TYPE \"MotorStatus\"\nVERSION : 0.1\n   STRUCT\n      speed : Real;\n      running : Bool;\n   END_STRUCT;\nEND_TYPE\n");
            File.WriteAllText(Path.Combine(v2, "MotorStatus.udt"), "TYPE \"MotorStatus\"\nVERSION : 0.1\n   STRUCT\n      speed : LReal;\n      running : Bool;\n   END_STRUCT;\nEND_TYPE\n");
            File.WriteAllText(Path.Combine(fb, "MotorDrive.scl"), "FUNCTION_BLOCK \"MotorDrive\"\n{ S7_Optimized_Access := 'TRUE' }\nVERSION : 0.1\n   VAR_INPUT\n      Enable : Bool;\n   END_VAR\n   VAR_OUTPUT\n      Active : Bool;\n   END_VAR\n   VAR\n      status : \"MotorStatus\";\n   END_VAR\nBEGIN\n   #Active := #Enable;\n   #status.running := #Enable;\nEND_FUNCTION_BLOCK\n");
            var types = project.ProjectLibrary.TypeFolder.Types;
            var udt = types.CreateFromDocuments(new DirectoryInfo(v1), "MotorStatus", a.TypeGroup, LibraryImportOptions.None).CreatedType;
            var udtV1 = Release(udt, new Version(1, 0, 0));
            result["motorStatus"] = new JObject { ["typeGuid"] = udt.Guid.ToString("D"), ["v1"] = VersionRow(udtV1) }; Persist();
            string scl = Path.Combine(directory, "plchmi-db.scl");
            File.WriteAllText(scl, "DATA_BLOCK \"MotorDB\"\n{ S7_Optimized_Access := 'TRUE' }\nVERSION : 0.1\n   VAR\n      status : \"MotorStatus\";\n   END_VAR\nBEGIN\nEND_DATA_BLOCK\n");
            a.ExternalSourceGroup.ExternalSources.CreateFromFile("PlcHmiSource", scl).GenerateBlocksFromSource();
            var drive = types.CreateFromDocuments(new DirectoryInfo(fb), "MotorDrive", a.BlockGroup, LibraryImportOptions.None).CreatedType;
            result["motorDrive"] = new JObject { ["typeGuid"] = drive.Guid.ToString("D"), ["v1"] = VersionRow(Release(drive, new Version(1, 0, 0))) }; Persist();
            foreach (var spec in new[] { new[] { "MotorSpeed", "MotorDB.status.speed" }, new[] { "MotorRunning", "MotorDB.status.running" } })
            {
                object tag = Call(Get(hmiA, "Tags"), "Create", spec[0]);
                Set(tag, "Connection", connectionA); Set(tag, "PlcTag", spec[1]);
                result[spec[0]] = Attributes((IEngineeringObject)tag);
            }
            object screen = ((IEnumerable)Get(hmiA, "Screens")).Cast<object>().First();
            object items = Get(screen, "ScreenItems");
            Type rectangle = hmiA.GetType().Assembly.GetType("Siemens.Engineering.HmiUnified.UI.Shapes.HmiRectangle", true);
            object item = items.GetType().GetMethods().Single(m => m.Name == "Create" && m.IsGenericMethodDefinition && m.GetParameters().Length == 1).MakeGenericMethod(rectangle).Invoke(items, new object[] { "MotorSpeedConsumer" });
            object dynamics = Get(item, "Dynamizations");
            Type tagDynamics = hmiA.GetType().Assembly.GetType("Siemens.Engineering.HmiUnified.UI.Dynamization.TagDynamization", true);
            object dyn = dynamics.GetType().GetMethods().Single(m => m.Name == "Create" && m.IsGenericMethodDefinition && m.GetParameters().Length == 1).MakeGenericMethod(tagDynamics).Invoke(dynamics, new object[] { "Left" });
            Set(dyn, "Tag", "MotorSpeed");
            result["consumer"] = new JObject { ["screen"] = ((IEngineeringObject)screen).GetAttribute("Name")?.ToString(), ["item"] = "MotorSpeedConsumer", ["property"] = "Left", ["tag"] = "MotorSpeed" };
            // The field-changing version gets its test instance in PLC_B, so PLC_A keeps its 1.0.0 use.
            var transfer = udt.Versions.CreateFromDocuments(new DirectoryInfo(v2), "MotorStatus", b.TypeGroup, CreateOptions.None, LibraryImportOptions.None);
            result["motorStatus"]["v2"] = VersionRow(Release(udt, new Version(1, 0, 1)));
            result["motorStatus"]["v2Transfer"] = transfer.TransferResultState.ToString();
            b.TypeGroup.Types.Find("MotorStatus")?.Delete();
            var table = b.TagTableGroup.TagTables.Find("QualificationTags");
            table.Tags.Create("MotorEnable", "Bool", "%M10.0"); table.Tags.Create("MotorActive", "Bool", "%M10.1");
            result["installTarget"] = new JObject { ["plc"] = b.Name, ["tags"] = new JArray("MotorEnable", "MotorActive"), ["motorStatusRemoved"] = b.TypeGroup.Types.Find("MotorStatus") == null };
            result["plcA"] = a.Name; result["plcB"] = b.Name; result["hmiA"] = hmiA.Name; result["connectionA"] = connectionA;
            result["status"] = "created";
        }
        catch (Exception ex) { result["error"] = Error(ex); }
        return result;
    }

    private static LibraryTypeVersion Release(LibraryType type, Version number)
    {
        var version = type.Versions.Single(v => v.State == LibraryTypeVersionState.InWork);
        version.Release(CreateOrReleaseDependenciesMode.DoNotAutomaticallyCreateOrReleaseDependencies, number, "TIA MCP qualification", "Combined PLC/HMI recipe fixture");
        if (version.State != LibraryTypeVersionState.Committed || version.VersionNumber != number) throw new InvalidOperationException("Native release differs for " + type.Name);
        return version;
    }

    private static JObject VersionRow(LibraryTypeVersion version) => new JObject { ["versionGuid"] = version.Guid.ToString("D"), ["version"] = version.VersionNumber.ToString() };
}
