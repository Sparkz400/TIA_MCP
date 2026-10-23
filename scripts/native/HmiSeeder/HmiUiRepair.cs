using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;

// A separate fixture-preparation entry point. The native editor configures the
// reviewed prerequisites; this helper never sets passwords or disables security.
// A coordinator must retain exact process handles and the resource-store window.
internal static partial class Program
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool CreateDirectoryW(string path, IntPtr securityAttributes);

    private static void RepairSave(string output, string name, JToken value)
    {
        using (var stream = new FileStream(Path.Combine(output, name + ".json"), FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        using (var writer = new StreamWriter(stream))
        { writer.Write(value.ToString(Formatting.Indented)); writer.Flush(); stream.Flush(true); }
    }

    private static JObject RepairReference(string path) => new JObject { ["path"] = path, ["sha256"] = Hash(path) };

    private static int RepairUi(string admissionPath)
    {
        var admission = JObject.Parse(File.ReadAllText(admissionPath));
        var plan = ValidateRepairUiAdmission(admission, Assembly.GetExecutingAssembly().Location);
        var profile = plan.Profile;
        string archive = plan.Archive, sourcePath = plan.SourcePath, output = plan.Output, reviewPath = plan.ReviewPath;
        string[] owners = plan.Owners;
        int minutes = plan.Minutes;
        TextListFixture = profile.Value<string>("resourceFixture") == "documented-bilingual-text-list";
        // Atomic fresh-workspace claim: never silently reuse a directory created
        // after the admission check. Parent is the already-held artifact root.
        if (!CreateDirectoryW(output, IntPtr.Zero))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Fresh owned fixture output could not be claimed.");
        RepairSave(output, "admission", admission);
        var result = new JObject { ["completed"] = false, ["profile"] = profile["profile"], ["sourceArchive"] = admission["archive"].DeepClone(),
            ["sourceReport"] = admission["sourceReport"].DeepClone(), ["review"] = admission["review"].DeepClone(),
            ["runtimeVerified"] = false, ["visualVerified"] = false, ["bridgeRecoveryVerified"] = false, ["profileEnabled"] = false,
            ["automaticReplay"] = false, ["scope"] = "Native editor prerequisite repair in a new independently retrieved owned copy" };
        TiaPortal portal = null; Project project = null; Process process = null;
        Exception failure = null;
        try
        {
            RepairSave(output, "start-intent", new JObject { ["atUtc"] = DateTime.UtcNow.ToString("O"), ["mode"] = "WithUserInterface" });
            portal = new TiaPortal(TiaPortalMode.WithUserInterface);
            process = Process.GetProcessById(portal.GetCurrentProcess().Id);
            IntPtr retainedHandle = process.Handle;
            result["processId"] = process.Id; result["processStartedAtUtc"] = process.StartTime.ToUniversalTime().ToString("O");
            RepairSave(output, "start-return", new JObject { ["processId"] = process.Id, ["startedAtUtc"] = result["processStartedAtUtc"],
                ["executable"] = process.MainModule.FileName, ["exactHandleRetained"] = retainedHandle != IntPtr.Zero, ["ownsPortalProcess"] = true });
            string retrieved = Path.Combine(output, "p"); Directory.CreateDirectory(retrieved);
            RepairSave(output, "retrieve-intent", new JObject { ["archive"] = admission["archive"].DeepClone(), ["destination"] = retrieved });
            project = portal.Projects.Retrieve(new FileInfo(archive), new DirectoryInfo(retrieved));
            result["projectPath"] = project.Path.FullName;
            RepairSave(output, "retrieve-return", new JObject { ["projectPath"] = project.Path.FullName, ["processId"] = process.Id });
            var nativeOwners = FindSoftware(project).ToArray();
            if (!owners.SequenceEqual(nativeOwners.Select(sw => sw.Name).OrderBy(n => n, StringComparer.Ordinal)))
                throw new InvalidOperationException("Retrieved HMI owner inventory differs from the reviewed fixture.");
            result["before"] = new JArray(nativeOwners.Select(sw => Inspect(sw, output, "before")));
            result["compileBefore"] = Compile(nativeOwners);
            RepairSave(output, "before", new JObject { ["native"] = result["before"].DeepClone(), ["compile"] = result["compileBefore"].DeepClone() });
            string commandPath = Path.Combine(output, "coordinator-command.json");
            RepairSave(output, "ui-ready", new JObject { ["processId"] = process.Id, ["projectPath"] = project.Path.FullName,
                ["owners"] = new JArray(owners), ["reviewSha256"] = Hash(reviewPath), ["commandPath"] = commandPath,
                ["requiredCommand"] = "capture-saved-close", ["abortCommand"] = "abort-close",
                ["instruction"] = "Perform only reviewed prerequisite edits and Save in the owned native editor. Never go online or execute scripts." });
            DateTime until = DateTime.UtcNow.AddMinutes(minutes);
            while (!File.Exists(commandPath) && DateTime.UtcNow < until) Thread.Sleep(250);
            if (!File.Exists(commandPath)) throw new TimeoutException("No explicit UI capture command; no automatic save or retry.");
            var command = JObject.Parse(File.ReadAllText(commandPath));
            if (!RepairUiCaptureRequested(command, Hash(reviewPath), process.Id, project.Path.FullName))
            {
                result["abortedBeforeCapture"] = true;
                throw new OperationCanceledException("Explicit bound abort: close the owned editor without capture, save or archive.");
            }
            using (portal.ExclusiveAccess("TIA MCP: inspect saved HMI prerequisite repair"))
            {
                if (project.IsModified) throw new InvalidOperationException("UI Save is required before capture. The helper does not save an unreviewed UI state.");
                result["after"] = new JArray(nativeOwners.Select(sw => Inspect(sw, output, "after")));
                result["compileAfter"] = Compile(nativeOwners);
                RepairSave(output, "after", new JObject { ["native"] = result["after"].DeepClone(), ["compile"] = result["compileAfter"].DeepClone() });
                if (!CompileSucceeded((JArray)result["compileAfter"])) throw new InvalidOperationException("Complete native compiler diagnostics still contain errors or incomplete evidence.");
                RepairSave(output, "save-intent", new JObject { ["purpose"] = "Persist successful native compile metadata after the explicit saved UI capture", ["projectPath"] = project.Path.FullName });
                project.Save();
                RepairSave(output, "save-return", new JObject { ["projectModified"] = project.IsModified });
                RepairSave(output, "archive-intent", new JObject { ["directory"] = output, ["name"] = "configured.zap21" });
                project.Archive(new DirectoryInfo(output), "configured.zap21", ProjectArchivationMode.Compressed);
                result["archive"] = RepairReference(Path.Combine(output, "configured.zap21"));
                RepairSave(output, "archive-return", result["archive"]);
            }
            project.Close(); project = null;
            result["savedConfiguredArchiveCaptured"] = true;
            result["freshProcessReopenRequired"] = true;
            result["completed"] = true;
        }
        catch (Exception error) { failure = error; result["failure"] = Error(error); }
        finally
        {
            if (project != null) try { project.Close(); } catch (Exception error) { result["closeError"] = Error(error); }
            if (portal != null)
            {
                try { portal.GetCurrentProcess().Dispose(); result["explicitOwnedSdkDispose"] = true; }
                catch (Exception error) { result["nativeDisposeError"] = Error(error); }
                try { portal.Dispose(); } catch (Exception error) { result["portalDisposeError"] = Error(error); }
            }
            if (process != null)
            {
                bool exited = process.WaitForExit(45000);
                result["retainedHandleExitVerified"] = exited;
                if (exited) result["nativeExitCode"] = process.ExitCode;
                process.Dispose();
            }
            result["sourceArchiveUnchanged"] = Hash(archive) == admission["archive"].Value<string>("sha256");
            result["sourceReportUnchanged"] = Hash(sourcePath) == admission["sourceReport"].Value<string>("sha256");
            if (result.Value<bool?>("retainedHandleExitVerified") != true || result.Value<bool?>("sourceArchiveUnchanged") != true ||
                result.Value<bool?>("sourceReportUnchanged") != true || result["closeError"] != null || result["nativeDisposeError"] != null || result["portalDisposeError"] != null)
                result["completed"] = false;
            RepairSave(output, "result", result);
        }
        Console.WriteLine(new JObject { ["completed"] = result["completed"], ["result"] = Path.Combine(output, "result.json"),
            ["profileEnabled"] = false, ["freshProcessReopenRequired"] = true }.ToString(Formatting.None));
        return failure == null && result.Value<bool?>("completed") == true ? 0 : 1;
    }
}
