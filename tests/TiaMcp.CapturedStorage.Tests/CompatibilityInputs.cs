using System.Xml.Linq;
namespace TiaMcp.Openness.V21;
// Test input records retain concise independent mutation fixtures; all matching
// and construction delegate to the production pure helper linked by this project.
internal static class CapturedStorageTemplate
{
    internal const string ProfileId = "v21u2-component04-state-bool-fbd-global-v1";
    internal const string Cpu = "OrderNumber:6ES7 511-1AL03-0AB0/V4.1";
    internal const string FbName = "TiaMcpComponentGate";
    internal const string TypeName = "TiaMcpComponentState";
    internal const string FbTypeGuid = "f566f7e9-d5bc-415e-b552-4828e3b1027c";
    internal const string FbVersionGuid = "e129ccee-1eaa-456b-b2f7-1581fcc3b6b9";
    internal const string UdtTypeGuid = "35c9be87-892c-4ce6-a724-545ab1d41683";
    internal const string UdtVersionGuid = "eee41dd4-5137-48bb-a42b-d7fc64637eec";
    internal sealed record Artifact(byte[] Bytes, string Sha256);
    internal sealed record Profile(string Id, string CpuIdentifier, string NativeRelease, string NativeApiFileVersion, string Route, string CallerLanguage,
        Artifact ReferenceCallee, Artifact ReferenceType, Artifact InstanceDocument);
    // The adapter must obtain these fields from the exact prepared plan and
    // native FindInstances, not from user arguments or profile name matching.
    internal sealed record Binding(string PlannedPath, string NativePath, string ScopePath, string Namespace,
        string TypeGuid, string VersionGuid, string VersionState, bool DoNotUse, bool ScopeComplete, int NativeUseCount,
        string[] RequiredVersionGuids, Artifact Document);


    internal static XDocument Create(string name, int number, Profile p, string plc, string cpu, string release, string api, string route, string language, Binding fb, Binding udt) =>
        TypedGraphicalCapturedStorageTemplate.Create(name, number,
            new(p.Id, p.CpuIdentifier, p.NativeRelease, p.NativeApiFileVersion, p.Route, p.CallerLanguage, A(p.ReferenceCallee), A(p.ReferenceType), A(p.InstanceDocument)),
            plc, cpu, release, api, route, language, B(fb), B(udt));
    private static TypedGraphicalCapturedStorageTemplate.Artifact A(Artifact a) => new(a.Bytes, a.Sha256);
    private static TypedGraphicalCapturedStorageTemplate.Binding B(Binding b) => new(b.PlannedPath, b.NativePath, b.ScopePath, b.Namespace, b.TypeGuid, b.VersionGuid, b.VersionState, b.DoNotUse, b.ScopeComplete, b.NativeUseCount, b.RequiredVersionGuids, A(b.Document));
}
