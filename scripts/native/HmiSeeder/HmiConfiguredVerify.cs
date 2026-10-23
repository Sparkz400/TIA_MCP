using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;

// Qualification only: exact completed fixture, existing saved reopen and fresh
// archive retrieval. No production apply, runtime, download or automatic retry.
internal static partial class Program
{
    private static int VerifyConfigured(string admissionPath)
    {
        var admission = JObject.Parse(File.ReadAllText(admissionPath));
        var plan = ValidateConfiguredVerifyAdmission(admission, Assembly.GetExecutingAssembly().Location);
        if (admission["bridgeWorker"] != null)
        {
            if (plan.Phase != "reopen-retrieve") throw new InvalidOperationException("Bridge inspection requires a fresh archive copy.");
            RepairArtifact(admission["bridgeWorker"]);
            foreach (var file in (JArray)admission["bridgeFiles"]) RepairArtifact(file);
        }
        string output = plan.Output;
        if (!CreateDirectoryW(output, IntPtr.Zero))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Fresh configured-verification output could not be claimed.");
        RepairSave(output, "admission", admission);
        var result = new JObject { ["completed"] = false, ["phase"] = plan.Phase,
            ["configuredResult"] = admission["configuredResult"].DeepClone(), ["sourceArchive"] = admission["archive"].DeepClone(),
            ["projectPath"] = plan.Project, ["runtimeVerified"] = false, ["visualVerified"] = false,
            ["wholeHmiPreservationQualified"] = false, ["profileEnabled"] = false, ["automaticReplay"] = false,
            ["scope"] = "Captured owned HMI fixture state after fresh-process reopen and independent archive retrieval" };
        TiaPortal portal = null; Project project = null; Process process = null;
        try
        {
            RepairSave(output, "start-intent", new JObject { ["atUtc"] = DateTime.UtcNow.ToString("O"), ["mode"] = "WithUserInterface" });
            portal = new TiaPortal(TiaPortalMode.WithUserInterface);
            process = Process.GetProcessById(portal.GetCurrentProcess().Id);
            IntPtr retained = process.Handle;
            RepairSave(output, "start-return", new JObject { ["processId"] = process.Id,
                ["startedAtUtc"] = process.StartTime.ToUniversalTime().ToString("O"), ["executable"] = process.MainModule.FileName,
                ["exactHandleRetained"] = retained != IntPtr.Zero, ["ownsPortalProcess"] = true });
            result["processId"] = process.Id;
            using (portal.ExclusiveAccess("TIA MCP: qualify saved owned HMI state"))
            {
                RepairSave(output, "open-intent", new JObject { ["projectPath"] = plan.Project });
                project = portal.Projects.Open(new FileInfo(plan.Project));
                if (!string.Equals(project.Path.FullName, plan.Project, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Reopened project identity differs from the exact admission.");
                var owners = FindSoftware(project).OrderBy(sw => sw.Name, StringComparer.Ordinal).ToArray();
                if (!plan.Owners.SequenceEqual(owners.Select(sw => sw.Name))) throw new InvalidOperationException("Exact HMI owner inventory changed on reopen.");
                result["reopened"] = new JArray(owners.Select(sw => Inspect(sw, output, "reopened")));
                result["reopenedCompile"] = Compile(owners);
                RepairSave(output, "reopened", new JObject { ["native"] = result["reopened"].DeepClone(), ["compile"] = result["reopenedCompile"].DeepClone() });
                if (!CompileSucceeded((JArray)result["reopenedCompile"])) throw new InvalidOperationException("Fresh reopened HMI compilation failed.");
                project.Close(); project = null;
                if (plan.Phase == "reopen-retrieve")
                {
                    string retrieved = Path.Combine(output, "recovered");
                    if (!CreateDirectoryW(retrieved, IntPtr.Zero)) throw new InvalidOperationException("Recovery destination is not fresh.");
                    RepairSave(output, "retrieve-intent", new JObject { ["archive"] = admission["archive"].DeepClone(), ["destination"] = retrieved });
                    project = portal.Projects.Retrieve(new FileInfo(plan.Archive), new DirectoryInfo(retrieved));
                    result["recoveredProjectPath"] = project.Path.FullName;
                    if (!project.Path.FullName.StartsWith(retrieved + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Recovered project is outside the exact new destination.");
                    owners = FindSoftware(project).OrderBy(sw => sw.Name, StringComparer.Ordinal).ToArray();
                    if (!plan.Owners.SequenceEqual(owners.Select(sw => sw.Name))) throw new InvalidOperationException("Exact HMI owner inventory changed on retrieval.");
                    result["recovered"] = new JArray(owners.Select(sw => Inspect(sw, output, "recovered")));
                    result["recoveredCompile"] = Compile(owners);
                    RepairSave(output, "recovered", new JObject { ["native"] = result["recovered"].DeepClone(), ["compile"] = result["recoveredCompile"].DeepClone(),
                        ["projectPath"] = project.Path.FullName });
                    if (!CompileSucceeded((JArray)result["recoveredCompile"])) throw new InvalidOperationException("Recovered HMI compilation failed.");
                    project.Save(); project.Close(); project = null;
                }
            }
            if (admission["bridgeWorker"] != null)
            {
                result["bridge"] = ProbeConfiguredBridge(portal, result.Value<string>("recoveredProjectPath"), admission, output);
                if (result["bridge"].Value<bool?>("completed") != true) throw new InvalidOperationException("Adapter observation failed; see retained bridge-result.");
            }
            result["loadedHelperSiemensAssemblies"] = new JArray(AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => a.GetName().Name.StartsWith("Siemens.Engineering", StringComparison.Ordinal))
                .OrderBy(a => a.FullName, StringComparer.Ordinal).Select(a => new JObject {
                    ["identity"] = a.FullName, ["location"] = a.Location, ["sha256"] = Hash(a.Location) }));
            RepairSave(output, "loaded-helper-sdk", result["loadedHelperSiemensAssemblies"]);
            result["completed"] = true;
        }
        catch (Exception error) { result["failure"] = Error(error); }
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
                bool exited = process.WaitForExit(45000); result["retainedHandleExitVerified"] = exited;
                if (exited) result["nativeExitCode"] = process.ExitCode;
                process.Dispose();
            }
            result["sourceArchiveUnchanged"] = Hash(plan.Archive) == admission["archive"].Value<string>("sha256");
            result["configuredResultUnchanged"] = Hash(plan.ConfiguredResult) == admission["configuredResult"].Value<string>("sha256");
            if (result.Value<bool?>("retainedHandleExitVerified") != true || result.Value<bool?>("sourceArchiveUnchanged") != true ||
                result.Value<bool?>("configuredResultUnchanged") != true || result["closeError"] != null || result["nativeDisposeError"] != null || result["portalDisposeError"] != null)
                result["completed"] = false;
            RepairSave(output, "result", result);
        }
        return result.Value<bool?>("completed") == true ? 0 : 1;
    }
}
