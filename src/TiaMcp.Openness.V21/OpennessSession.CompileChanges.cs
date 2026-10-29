using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.Compiler;

namespace TiaMcp.Openness.V21
{
    // Compile-only changes beside plc_compile: one device (station hardware and software), every device of the project, or
    // one HMI target. Like plc_compile they run through change_apply, so the project is saved and archived before the
    // compiler is dispatched; no other mutation runs and the scope is rechecked against the preview before dispatch.
    internal sealed partial class OpennessSession
    {
        private static bool IsCompileOnlyKind(string kind) => kind == "plc_compile" || kind == "device_compile" || kind == "project_compile" || kind == "hmi_compile";

        private void PrepareCompileOnly(EngineeringChange change, Project project, JObject preview)
        {
            var effect = CompileOnlyScope(change, project);
            change.ObjectNames = effect["targets"].Select(target => target.Value<string>()).ToArray();
            ((JArray)preview["effects"]).Add(effect);
            ((JArray)preview["warnings"]).Add("Compilation may change generated state. Apply saves the current project and creates a recovery archive before dispatch.");
        }

        private JObject CompileOnlyScope(EngineeringChange change, Project project)
        {
            var payload = change.Payload;
            string refusal = CompileOnlyRules.PayloadRefusal(change.Kind, payload);
            if (refusal != null) throw new BridgeException("InvalidRequest", refusal);
            if (change.Kind == "project_compile")
            {
                var entries = DeviceCompilation.Entries(project);
                if (entries.Count == 0) throw new BridgeException("UnsupportedCapability", "No project device compiler target exists.");
                return new JObject { ["action"] = "compile", ["scope"] = "project", ["targets"] = new JArray(entries.Select(entry => entry.Path)),
                    ["coverage"] = "Every device of the project (station hardware and software); devices are compiled in inventory order." };
            }
            if (change.Kind == "device_compile")
            {
                string path = ChangeText(payload, "devicePath", 4096);
                var matches = DeviceCompilation.Entries(project).Where(entry => entry.Path == path).ToArray();
                if (matches.Length != 1) throw new BridgeException("ObjectNotFound", "devicePath must name exactly one device of the selected project, as the device compiler inventory spells it.");
                if (matches[0].Item.GetService<ICompilable>() == null) throw new BridgeException("UnsupportedCapability", "The selected device exposes no compiler service.");
                return new JObject { ["action"] = "compile", ["scope"] = "device", ["devicePath"] = path, ["deviceName"] = matches[0].Item.Name, ["targets"] = new JArray(path),
                    ["coverage"] = "Device.GetService<ICompilable>: the station's hardware and software." };
            }
            var selected = ResolveHmiTarget(project, ChangeObject(payload, "target"));
            LibraryHmiCompiler(selected);
            var view = HmiTargetView(selected);
            return new JObject { ["action"] = "compile", ["scope"] = "hmi", ["target"] = view, ["targets"] = new JArray(view["compileScope"].DeepClone()),
                ["coverage"] = selected.Family == "unified" ? "Unified: the owning device's hardware and software." : "Classic: the HMI target's software." };
        }

        private JObject CompileOnly(EngineeringChange change, Project project)
        {
            var effect = CompileOnlyScope(change, project);
            if (!CompileOnlyRules.SameTargets(change.ObjectNames, effect["targets"].Select(target => target.Value<string>())))
                throw new BridgeException("CompileScopeChanged", "The compile targets differ from the reviewed preview.");
            change.Stages.Add("compiler_dispatching");
            WriteChangeJournal(change);
            if (change.Kind == "project_compile") return DeviceCompilation.Compile(project, null, change.ObjectNames);
            CompilerResult result;
            if (change.Kind == "device_compile") result = DeviceCompilation.Entries(project).Single(entry => entry.Path == change.ObjectNames[0]).Item.GetService<ICompilable>().Compile();
            else result = LibraryHmiCompiler(ResolveHmiTarget(project, ChangeObject(change.Payload, "target"))).Compile();
            return new JObject { ["errorCount"] = result.ErrorCount, ["warningCount"] = result.WarningCount, ["targets"] = new JArray(DeviceCompilation.Describe(result, change.ObjectNames[0])),
                ["scope"] = effect["coverage"].DeepClone(), ["runtimeVerified"] = false };
        }
    }
}
