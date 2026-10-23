using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;

internal static partial class Program
{
    // Calls the unchanged adapter dispatch from its exact built assembly. This
    // qualifies adapter observations, not Host/MCP transport or production writes.
    private static JObject ProbeConfiguredBridge(TiaPortal portal, string projectPath, JObject admission, string output)
    {
        var files = admission["bridgeFiles"] as JArray;
        if (files == null || files.Count == 0) throw new InvalidOperationException("Pinned bridge files required.");
        foreach (var file in files) RepairArtifact(file);
        string worker = RepairArtifact(admission["bridgeWorker"]);
        if (!files.Any(f => JToken.DeepEquals(f, admission["bridgeWorker"]))) throw new InvalidOperationException("Worker absent from pinned bundle.");
        var result = new JObject { ["completed"] = false, ["worker"] = admission["bridgeWorker"].DeepClone(),
            ["hostTransportQualified"] = false, ["productionMutationEnabled"] = false,
            ["wholeHmiPreservationQualified"] = false, ["inspections"] = new JArray() };
        Project project = null; IDisposable session = null;
        try
        {
            project = portal.Projects.Open(new FileInfo(projectPath));
            result["projectPath"] = project.Path.FullName;
            using (portal.ExclusiveAccess("TIA MCP: independent before capture"))
                result["before"] = new JArray(FindSoftware(project).OrderBy(s => s.Name, StringComparer.Ordinal).Select(s => Inspect(s, output, "bridge-before")));
            var assembly = Assembly.LoadFrom(worker);
            var type = assembly.GetType("TiaMcp.Openness.V21.OpennessSession", true);
            var constructor = type.GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(string), typeof(bool) }, null);
            session = (IDisposable)constructor.Invoke(new object[] { Api, false });
            var execute = type.GetMethod("Execute", BindingFlags.Instance | BindingFlags.NonPublic);
            Func<string, JObject, JObject> call = (operation, args) => (JObject)execute.Invoke(session, new object[] { operation, args });
            int processId = portal.GetCurrentProcess().Id;
            var attached = call("attach", new JObject { ["processId"] = processId });
            result["attached"] = attached;
            if (attached.Value<int?>("processId") != processId || attached.Value<bool?>("ownsPortalProcess") != false)
                throw new InvalidOperationException("Adapter attachment differs from exact owned helper process.");
            Func<JObject, JObject> inspect = request =>
            {
                var pages = new JArray(); var items = new JArray(); int offset = 0; string revision = null;
                while (true)
                {
                    var args = (JObject)request.DeepClone(); args["schemaVersion"] = 1; args["limit"] = 37;
                    args["offset"] = offset; args["maxBytes"] = 1048576;
                    if (revision != null) args["expectedMetadataRevision"] = revision;
                    var page = call("hmi_inspect", args); pages.Add(page);
                    foreach (var item in (JArray)page["items"]) items.Add(item.DeepClone());
                    if (page["nextOffset"] == null || page["nextOffset"].Type == JTokenType.Null)
                        return new JObject { ["items"] = items, ["lastPage"] = page.DeepClone(), ["pages"] = pages };
                    int next = page.Value<int>("nextOffset");
                    if (next <= offset || next > 50000) throw new InvalidOperationException("Adapter pagination did not progress within bounds.");
                    offset = next; revision = page.Value<string>("metadataRevision");
                }
            };
            var devices = inspect(new JObject { ["section"] = "devices", ["projectPath"] = projectPath });
            result["devices"] = devices;
            RepairSave(output, "bridge-progress", result);
            int sequence = 0;
            foreach (var device in (JArray)devices["items"])
            {
                var identity = (JObject)device["identity"];
                foreach (string section in new[] { "connections", "tags", "screens", "alarms", "scripts", "resources" })
                {
                    string exports = Path.Combine(output, "bridge-exports-" + (++sequence));
                    Directory.CreateDirectory(exports);
                    var observed = inspect(new JObject { ["section"] = section, ["target"] = identity.DeepClone(),
                        ["includeSource"] = true, ["artifactDirectory"] = exports });
                    ((JArray)result["inspections"]).Add(new JObject { ["target"] = identity.DeepClone(), ["section"] = section, ["result"] = observed });
                    RepairSave(output, "bridge-section-" + sequence, observed);
                }
            }
            session.Dispose(); session = null;
            using (portal.ExclusiveAccess("TIA MCP: independent after capture and compile"))
            {
                var owners = FindSoftware(project).OrderBy(s => s.Name, StringComparer.Ordinal).ToArray();
                result["after"] = new JArray(owners.Select(s => Inspect(s, output, "bridge-after")));
                result["nativeCompile"] = Compile(owners);
                if (!CompileSucceeded((JArray)result["nativeCompile"])) throw new InvalidOperationException("Native compile failed after adapter inspection.");
            }
            foreach (var file in files) RepairArtifact(file);
            result["completed"] = true;
        }
        catch (Exception error) { result["failure"] = Error(error is TargetInvocationException && error.InnerException != null ? error.InnerException : error); }
        finally
        {
            try { if (session != null) session.Dispose(); }
            catch (Exception error) { result["adapterDisposeError"] = Error(error); result["completed"] = false; }
            try { if (project != null) project.Close(); }
            catch (Exception error) { result["projectCloseError"] = Error(error); result["completed"] = false; }
            RepairSave(output, "bridge-result", result);
        }
        return result;
    }
}
