using TiaMcp.Openness.V21;

namespace TiaMcp.Tests;

internal static class TechnologyBlockMembershipTests
{
    private sealed class Native(int id, string name)
    {
        public override string ToString() => name;
        public override bool Equals(object? other) => other is Native value && value.Id == id;
        public override int GetHashCode() => id;
        private int Id => id;
    }

    internal static Task ExactNativeIdentity()
    {
        var membership = new TechnologyBlockMembership();
        membership.AddTechnology(new Native(1, "Encoder"), "/PLC/technology/Encoder");
        Check.Equal<string?>(null, membership.AddBlock(new Native(2, "Encoder"), "/PLC/blocks/peer"));
        Check.Equal("/PLC/technology/Encoder", membership.AddBlock(new Native(1, "different proxy"), "/PLC/blocks/native"));
        var coverage = membership.Coverage(false);
        Check.Equal(2, coverage.Value<int>("blockObjects")); Check.Equal(1, coverage.Value<int>("mappedBlocks"));
        Check.Equal(true, coverage.Value<bool>("scopeComplete")); Check.Equal(false, coverage.Value<bool>("atomicSnapshot"));
        return Task.CompletedTask;
    }

    internal static Task AmbiguityAndBounds()
    {
        void Refuse(Action action)
        {
            try { action(); throw new Exception("Ambiguous native membership was accepted"); }
            catch (BridgeException error) { Check.Equal("TraversalIncomplete", error.Code); }
        }
        foreach (string defect in new[] { "technologyObject", "technologyPath", "blockObject", "blockPath" })
        {
            var membership = new TechnologyBlockMembership();
            membership.AddTechnology(new Native(1, "A"), "/technology/A");
            membership.AddBlock(new Native(2, "B"), "/blocks/B");
            if (defect == "technologyObject") Refuse(() => membership.AddTechnology(new Native(1, "alias"), "/technology/C"));
            if (defect == "technologyPath") Refuse(() => membership.AddTechnology(new Native(3, "C"), "/technology/A"));
            if (defect == "blockObject") Refuse(() => membership.AddBlock(new Native(2, "alias"), "/blocks/C"));
            if (defect == "blockPath") Refuse(() => membership.AddBlock(new Native(3, "C"), "/blocks/B"));
        }
        var bounded = new TechnologyBlockMembership();
        Refuse(() => bounded.AddTechnology(null!, "/missing")); Refuse(() => bounded.AddBlock(new object(), ""));
        for (int i = 0; i < 50000; i++) { bounded.AddTechnology(new Native(i, "T"), "/technology/" + i); bounded.AddBlock(new Native(i, "T"), "/blocks/" + i); }
        Refuse(() => bounded.AddTechnology(new Native(50000, "extra"), "/technology/extra"));
        Refuse(() => bounded.AddBlock(new Native(50000, "extra"), "/blocks/extra"));
        Check.Equal(false, bounded.Coverage(true).Value<bool>("scopeComplete"));
        return Task.CompletedTask;
    }
}
