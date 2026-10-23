using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using TiaMcp.Indexing;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        partial void RegisterTypedProjectModelProviders(List<IProjectModelProvider> providers)
            => providers.Add(new TypedExplorerPreviewProvider(this));

        private sealed class TypedExplorerPreviewProvider : IProjectModelProvider
        {
            private readonly OpennessSession _session;
            internal TypedExplorerPreviewProvider(OpennessSession session) { _session = session; }
            public string ProviderId => ProjectModelTypedPreview.ProviderId;
            public bool Supports(JObject node, string facet) => facet == "typedLadFbdPreview" && node.Value<string>("kind") == "block" &&
                (node["metadata"]?.Value<string>("programmingLanguage") == "LAD" || node["metadata"]?.Value<string>("programmingLanguage") == "FBD");
            public JObject Inspect(ProjectModelProviderRequest request)
            {
                if (request.Node["access"]?.Value<string>("state") == "protected")
                    return Unavailable(request, "protected", "Know-how protection prevents native graphical content inspection.");
                try
                {
                    var args = _session.ModelInspectionArguments(request.Reference.Value<string>("snapshotId"), request.Node,
                        new JObject { ["artifactDirectory"] = request.ArtifactDirectory, ["maxItems"] = request.MaxItems });
                    return ProjectModelTypedPreview.Adapt(request, _session.ReadTypedGraphical(args));
                }
                catch (BridgeException error) { return Unavailable(request, "unavailable", error.Code + ": " + error.Message); }
            }
            private static JObject Unavailable(ProjectModelProviderRequest request, string status, string reason)
            {
                var result = ProjectModelProviders.Describe(request.Reference, request.Facet);
                result["provider"] = ProjectModelTypedPreview.ProviderId; result["status"] = status; result["reason"] = reason;
                result["coverage"] = new JArray(new JObject { ["provider"] = ProjectModelTypedPreview.ProviderId, ["scope"] = request.Reference["objectPath"],
                    ["status"] = status, ["visited"] = 0, ["returned"] = 0, ["warnings"] = new JArray(reason), ["continuationAvailable"] = false });
                return result;
            }
        }
    }
}
