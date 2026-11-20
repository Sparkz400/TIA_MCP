using TiaMcp.Model;
using TiaMcp.Openness.V21;

namespace TiaMcp.Tests
{
    internal static class BlockPathChainTests
    {
        // The chain is read out of paths ObjectIdentity.ObjectPath builds, escaped segments included.
        internal static Task GroupChainIsReadFromExactBlockPaths()
        {
            const string root = "/projects/P/devices/D/items/CPU/plc/CPU/blocks";
            static string Chain(string path) { var groups = BlockPathChain.Groups(path, root); return groups == null ? "null" : string.Join("|", groups); }
            Check.Equal("", Chain(root + "/Main"));
            Check.Equal("A|B%20C", Chain(root + "/groups/A/groups/B%20C/Motor"));
            Check.Equal("A", Chain(ObjectIdentity.ObjectPath(root + "/groups/A", "Pump", "Plant.Ns")));
            Check.Equal("", Chain(ObjectIdentity.ObjectPath(root, "Pump", "Plant.Ns")));
            Check.Equal("", Chain(root + "/groups"));
            Check.Equal("groups", Chain(root + "/groups/groups/groups"));
            Check.Equal("x", Chain(root + "/groups/x/groups"));
            Check.Equal("null", Chain(root + "/groups/A"));
            Check.Equal("null", Chain(root + "/systemGroups/Sys/Block"));
            Check.Equal("null", Chain(root + "/systemGroups/Sys/groups/G/Block"));
            Check.Equal("null", Chain("/projects/P/devices/D/items/CPU/plc/CPU/units/U/blocks/Main"));
            Check.Equal("null", Chain(root + "/"));
            Check.Equal("null", Chain(root + "/groups//Main"));
            Check.Equal("null", Chain(null!));
            Check.Equal("U", string.Join("|", BlockPathChain.Groups("/p/plc/CPU/units/U/blocks/groups/U/Main", "/p/plc/CPU/units/U/blocks")!));
            return Task.CompletedTask;
        }
    }
}
