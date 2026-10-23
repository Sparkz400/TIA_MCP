using System;
using System.IO;
using System.Runtime.CompilerServices;
using Newtonsoft.Json.Linq;
using TiaMcp.TestSuite.V21;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        private readonly bool _testSuiteScopeProbe;
        private bool _testSuiteScopeProbeAttempted;
        private JObject TestSuiteScopeProbe(JObject args)
        {
            if (!_testSuiteScopeProbe) throw new BridgeException("TestSuiteProfileUnqualified", "The scope probe is unavailable on normal workers.");
            string action = args.Value<string>("action");
            if (action != "prepare" && action != "read") throw new BridgeException("InvalidRequest", "Only prepare/read scope observations are supported.");
            EnsureAttached();
            if (!_ownsPortal || _scratchProject == null || _portal.Projects.Count != 1 || _scratchProject.IsModified ||
                !string.Equals(_scratchProject.Path.FullName, args.Value<string>("projectPath"), StringComparison.Ordinal))
                throw new BridgeException("ProjectIsolationRequired", "The exact newly created saved scratch is required.");
            EnsureProjectOffline(_scratchProject);
            if (action == "prepare" && _testSuiteScopeProbeAttempted)
                throw new BridgeException("RecoveryRequired", "Scope fixture preparation cannot be replayed.");
            if (action == "read" && !_testSuiteScopeProbeAttempted)
                throw new BridgeException("InvalidRequest", "Prepare the owned fixture first.");
            string directory = Path.Combine(ArtifactDirectory(args), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            JObject result;
            using (_portal.ExclusiveAccess("TIA MCP: owned scratch scope evidence; no execution"))
            {
                if (action == "prepare") _testSuiteScopeProbeAttempted = true;
                result = TestSuiteScopeProbeNative(action, directory);
            }
            if (action == "prepare") ShowTestSuiteScopeEditor();
            result["processId"] = _processId; result["sessionEpoch"] = _testSuiteEpoch;
            result["workerSha256"] = ChangeFileHash(typeof(OpennessSession).Assembly.Location);
            result["artifactDirectory"] = directory;
            return result;
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        private JObject TestSuiteScopeProbeNative(string action, string directory)
        {
            var plc = SelectPlc(new JObject { ["projectPath"] = _scratchProject.Path.FullName });
            return StyleScopeEditorProbe.Observe(_scratchProject, plc.Software, directory, action == "prepare");
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void ShowTestSuiteScopeEditor() { StyleScopeEditorProbe.Show(_scratchProject); }
    }
}
