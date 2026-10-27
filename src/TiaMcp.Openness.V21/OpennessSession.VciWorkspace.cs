using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using Siemens.Engineering.VersionControl;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        // Read side only: Workspace.ExportObject creates a mapping and Synchronize writes the user's
        // workspace, so neither belongs in a read-only tool (see docs/reference/vci-package-workflow.md).
        private JObject VciWorkspaceInspect(JObject args)
        {
            var project = SelectProject(args);
            var service = project.GetService<VersionControlInterface>() ?? throw new BridgeException("UnsupportedCapability", "The VCI service is not available for this project.");
            var budget = new ScanBudget(Limit(args, 500)); var warnings = new JArray(); var groups = new JArray(); var workspaces = new JArray();
            void Visit(WorkspaceGroup group, string parentPath, int depth)
            {
                if (depth > 32) { budget.Truncated = true; return; }
                if (!budget.Take()) return;
                string name = (group as WorkspaceSystemGroup)?.Name ?? (group as WorkspaceUserGroup)?.Name;
                string path = parentPath + "/" + Segment(name);
                groups.Add(new JObject { ["path"] = path, ["name"] = name, ["kind"] = group is WorkspaceSystemGroup ? "system" : "user" });
                foreach (var workspace in group.Workspaces.OrderBy(w => w.Name, StringComparer.Ordinal))
                {
                    if (!budget.Take()) return;
                    string workspacePath = path + "/" + Segment(workspace.Name);
                    var mappings = new JArray();
                    var row = new JObject { ["path"] = workspacePath, ["groupPath"] = path, ["name"] = workspace.Name, ["mappedObjects"] = mappings };
                    workspaces.Add(row);
                    try
                    {
                        row["rootPath"] = workspace.RootPath?.FullName; row["language"] = workspace.WorkspaceLanguage?.Name;
                        row["libraryPolicy"] = new JObject { ["globalLibraryPath"] = workspace.GlobalLibraryPath?.FullName, ["deleteUnusedTypeVersionFromLibrary"] = workspace.DeleteUnusedTypeVersionFromLibrary };
                    }
                    catch (Exception exception) { warnings.Add(InspectionWarning(workspacePath, exception)); }
                    foreach (var mapping in workspace.MappedObjects)
                    {
                        if (!budget.Take()) return;
                        var mapped = new JObject();
                        mappings.Add(mapped);
                        try
                        {
                            mapped["object"] = QualificationNativeIdentity(mapping.EngineeringObject);
                            mapped["directory"] = mapping.DirectoryPath?.FullName; mapped["fileName"] = mapping.FileNameWithoutExtension; mapped["format"] = mapping.FileFormat;
                            var status = mapping.GetStatus();
                            mapped["compareState"] = status.CompareState.ToString(); mapped["compareDetails"] = status.IndividualObjectCompareDetails.ToString();
                            mapped["childMappingState"] = mapping.GetChildStatus().MappingState.ToString();
                            var formats = new JArray(); mapped["supportedFormats"] = formats;
                            foreach (string format in workspace.GetSupportedFileFormats(mapping.EngineeringObject)) { if (!budget.Take()) return; formats.Add(format); }
                        }
                        catch (Exception exception) { warnings.Add(InspectionWarning(workspacePath + "/mapped/" + mappings.Count, exception)); }
                    }
                }
                foreach (var child in group.Groups.OrderBy(g => g.Name, StringComparer.Ordinal)) Visit(child, path, depth + 1);
            }
            Visit(service.WorkspaceGroup, "vci", 0);
            var result = Envelope();
            result["project"] = ProjectInfo(project);
            result["groups"] = groups;
            result["workspaces"] = workspaces;
            result["warnings"] = warnings;
            result["completeness"] = InspectionCoverage(budget, warnings, "VCI workspace groups, workspaces (root, language, library policy) and mapped objects with native compare/child mapping state and supported file formats.",
                "No workspace creation, mapping edit, export or synchronization", "Mapped-object identity is the native ancestor Name/Number chain, not a stable GUID",
                "Compare state is a native observation, not preservation or import qualification");
            return result;
        }
    }
}
