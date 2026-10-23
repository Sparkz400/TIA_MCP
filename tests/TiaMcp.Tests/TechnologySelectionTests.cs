using Newtonsoft.Json.Linq;
using Siemens.Engineering.SW.TechnologicalObjects;
using TiaMcp.Openness.V21;
using WorkerError = TiaMcp.Openness.V21.BridgeException;

namespace TiaMcp.Tests
{
    internal static class TechnologySelectionTests
    {
        private const string Root = "device/PLC/plc/technology";
        private static TechnologicalInstanceDB Item(string name = "Axis") => new([]) { Name = name };
        private static TechnologicalInstanceDBGroup Group(params TechnologicalInstanceDB[] items) => new() { TechnologicalObjects = items };
        private static JObject Name(string name = "Axis") => new() { ["name"] = name };
        private static JObject Path(string suffix = "Axis") => new() { ["technologyPath"] = Root + "/" + suffix };
        private static string? Find(TechnologicalInstanceDBGroup group, JObject request, bool required = true)
            => OpennessSession.FindTechnologyForTest(group,Root,request,required);
        private static async Task Refuse(string code, Action action)
        { var error = await Check.ThrowsAsync<WorkerError>(() => { action(); return Task.CompletedTask; }); Check.Equal(code,error.Code); }

        internal static async Task ExactSelection()
        {
            var a = Item(); var b = Item(); var sub = Group(b); sub.Name = "G/日本%";
            var root = Group(a); root.Groups = [sub];
            string nested = "groups/G%2F%E6%97%A5%E6%9C%AC%25/Axis";
            await Refuse("AmbiguousTarget",() => Find(root,Name("axis")));
            Check.Equal(Root + "/Axis",Find(root,Path()));
            Check.Equal(Root + "/" + nested,Find(root,Path(nested)));
            await Refuse("TechnologyObjectNotFound",() => Find(root,Path("axis")));
            var both = Path(); both["name"] = "axis";
            await Refuse("TargetMismatch",() => Find(root,both));
            both["name"] = "Axis"; both["technologyPath"] = Root + "/missing";
            await Refuse("TechnologyObjectNotFound",() => Find(root,both));
            Check.Equal<string?>(null,Find(root,both,false));
            root.Groups = []; Check.Equal(Root + "/Axis",Find(root,Name("axis")));
            root.TechnologicalObjects = [Item("slash/%日本")];
            Check.Equal(Root + "/slash%2F%25%E6%97%A5%E6%9C%AC",Find(root,Path("slash%2F%25%E6%97%A5%E6%9C%AC")));
            root.TechnologicalObjects = [a,b]; await Refuse("AmbiguousTarget",() => Find(root,Path()));
        }

        internal static async Task InvalidSelectors()
        {
            var root = Group(Item());
            await Refuse("InvalidRequest",() => Find(root,new()));
            foreach (string key in new[] { "name", "technologyPath" })
            foreach (JToken value in new JToken[] { JValue.CreateNull(), new JValue(1), new JArray(), new JObject(), new JValue(""), new JValue(" "), new JValue(new string('x',key == "name" ? 129 : 4097)) })
                await Refuse("InvalidRequest",() => Find(root,new JObject { [key] = value }));
            root.TechnologicalObjects = ThrowOnScan();
            foreach (string path in new[] { Root, Root + "Other/Axis", "device/other/plc/technology/Axis", Root.ToUpperInvariant() + "/Axis" })
                await Refuse("TargetMismatch",() => Find(root,new JObject { ["technologyPath"] = path }));
        }

        internal static async Task BoundIntent()
        {
            var a = Item(); var sub = Group(a); sub.Name = "A";
            var root = Group(); root.Groups = [sub]; var payload = Name("axis");
            OpennessSession.BindTechnologyForTest(root,Root,payload);
            Check.Equal(Root + "/groups/A/Axis",(string?)payload["technologyPath"]);
            Check.Equal("Axis",(string?)payload["name"]);
            root.TechnologicalObjects = [Item()];
            Check.Equal(Root + "/groups/A/Axis",Find(root,payload));
            sub.Name = "Moved";
            await Refuse("TechnologyObjectNotFound",() => Find(root,payload));
            sub.Name = "A"; a.Name = "Renamed";
            await Refuse("TechnologyObjectNotFound",() => Find(root,payload));
        }

        internal static async Task TraversalBounds()
        {
            var root = Group(); int visited = 0;
            IEnumerable<TechnologicalInstanceDB> Rows(int count)
            { for (int i=0;i<count;i++) { visited++; yield return Item(i == 0 ? "Axis" : "N"+i); } }
            root.TechnologicalObjects = Rows(50000);
            Check.Equal(Root + "/Axis",Find(root,Name())); Check.Equal(50000,visited);
            visited = 0; root.TechnologicalObjects = Rows(50002);
            await Refuse("TraversalLimit",() => Find(root,Name())); Check.Equal(50001,visited);
            await Refuse("TraversalLimit",() => Find(root,Name("absent"),false));
            await Refuse("TraversalLimit",() => OpennessSession.TechnologyFingerprintForTest(root,Root));
            root.TechnologicalObjects = Rows(49999); root.Groups = [Group(Item("Last"))];
            await Refuse("TraversalLimit",() => Find(root,Name()));
            root = Group(); var tail = root;
            for (int i=0;i<64;i++) { var next = Group(); next.Name = "G"; tail.Groups = [next]; tail = next; }
            tail.TechnologicalObjects = [Item()]; Check.Equal(true,Find(root,Name())!.EndsWith("/Axis",StringComparison.Ordinal));
            tail.Groups = [Group()]; await Refuse("TraversalLimit",() => Find(root,Name()));
        }

        internal static async Task DeleteVerification()
        {
            foreach (string mode in new[] { "success", "noop", "replacement", "nativeThrow", "readThrow" })
            {
                var selected = Item(); var peer = Item("Peer"); var rows = new List<TechnologicalInstanceDB> { selected,peer }; var root = Group(); root.TechnologicalObjects = rows;
                selected.OnDelete = () => {
                    if (mode == "nativeThrow") throw new InvalidOperationException("native delete failed");
                    if (mode != "noop") rows.Remove(selected);
                    if (mode == "replacement") rows.Add(Item());
                    if (mode == "readThrow") root.TechnologicalObjects = ThrowOnScan();
                };
                Action delete = () => OpennessSession.DeleteTechnologyForTest(root,Root,Path());
                if (mode is "nativeThrow" or "readThrow") await Check.ThrowsAsync<InvalidOperationException>(() => { delete(); return Task.CompletedTask; });
                else if (mode != "success") await Refuse("MutationVerificationFailed",delete);
                else { delete(); Check.Equal<string?>(null,Find(root,Path(),false)); }
                Check.Equal(1,selected.Deletes); Check.Equal(0,peer.Deletes);
            }
            var a = Item(); var b = Item(); var sub = Group(b); sub.Name = "Nested"; var top = Group(a); top.Groups = [sub];
            b.OnDelete = () => sub.TechnologicalObjects = [];
            OpennessSession.DeleteTechnologyForTest(top,Root,Path("groups/Nested/Axis")); Check.Equal(0,a.Deletes); Check.Equal(1,b.Deletes);
        }

        internal static Task FingerprintIdentity()
        {
            var item = Item(); var sub = Group(item); sub.Name = "A"; var root = Group(); root.Groups = [sub];
            var before = OpennessSession.TechnologyFingerprintForTest(root,Root);
            Check.Equal(Root + "/groups/A/Axis",(string?)before[0]!["path"]);
            Check.Equal("TO_PositioningAxis",(string?)before[0]!["typeIdentifier"]);
            Check.Equal("8.0",(string?)before[0]!["version"]); Check.Equal(1,(int)before[0]!["number"]!);
            Check.Equal(item.ModifiedDate.ToString("O"),(string?)before[0]!["modified"]);
            sub.Name = "B"; var after = OpennessSession.TechnologyFingerprintForTest(root,Root);
            Check.Equal(false,JToken.DeepEquals(before,after));
            after[0]!["path"] = before[0]!["path"]!.DeepClone();
            after[0]!["ownerPath"] = before[0]!["ownerPath"]!.DeepClone(); Check.Equal(true,JToken.DeepEquals(before,after));
            return Task.CompletedTask;
        }

        private static IEnumerable<TechnologicalInstanceDB> ThrowOnScan()
        { yield return Fail(); }
        private static TechnologicalInstanceDB Fail() => throw new InvalidOperationException("native enumeration failed");
    }
}

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        internal static string? FindTechnologyForTest(TechnologicalInstanceDBGroup group,string root,JObject payload,bool required) => FindTechnologyEntry(group,root,payload,required)?.Path;
        internal static void BindTechnologyForTest(TechnologicalInstanceDBGroup group,string root,JObject payload) => BindTechnologySelection(group,root,payload);
        internal static void DeleteTechnologyForTest(TechnologicalInstanceDBGroup group,string root,JObject payload) => DeleteTechnologySelection(group,root,payload);
        internal static JArray TechnologyFingerprintForTest(TechnologicalInstanceDBGroup group,string root) => TechnologyIdentityFingerprint(group,root);
    }
}
