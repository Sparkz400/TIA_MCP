using Newtonsoft.Json.Linq;
using Siemens.Engineering.Compare;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        // Offline only: PlcSoftware.CompareToOnline is deliberately not exposed.
        private JObject PlcCompare(JObject args)
        {
            var plc = SelectPlc(args);
            string targetPlcPath = args.Value<string>("targetPlcPath"), libraryId = args.Value<string>("libraryId");
            if (string.IsNullOrWhiteSpace(targetPlcPath) == string.IsNullOrWhiteSpace(libraryId))
                throw new BridgeException("InvalidRequest", "Provide exactly one compare target: targetPlcPath in the same project or libraryId from library_list/library_open.");
            ISoftwareCompareTarget target;
            var targetInfo = new JObject();
            if (string.IsNullOrWhiteSpace(libraryId))
            {
                var other = SelectPlc(new JObject { ["projectName"] = plc.Project.Name, ["plcPath"] = targetPlcPath });
                if (other.Path == plc.Path) throw new BridgeException("InvalidRequest", "Compare the PLC with a different PLC.");
                target = other.Software;
                targetInfo["plcPath"] = other.Path;
            }
            else
            {
                target = ResolveLibrary(ChangeText(args, "libraryId", 64)).Native as ISoftwareCompareTarget
                    ?? throw new BridgeException("TargetIncompatible", "Native library is not a software compare target.");
                targetInfo["libraryId"] = libraryId;
            }
            int max = Limit(args, 500);
            var budget = new ScanBudget(max);
            var root = plc.Software.CompareTo(target).RootElement;
            var result = Envelope();
            result["project"] = ProjectInfo(plc.Project);
            result["plcPath"] = plc.Path;
            result["target"] = targetInfo;
            result["root"] = root != null && budget.Take() ? CompareNode(root, budget, 0) : null;
            result["completeness"] = new JObject { ["complete"] = !budget.Truncated, ["truncated"] = budget.Truncated, ["maxItems"] = max, ["visitedItems"] = budget.Used,
                ["scope"] = "Native offline PlcSoftware.CompareTo result tree (left = selected PLC, right = target).",
                ["note"] = "Names and states are Siemens comparison output, not object identities. No online compare, synchronization or runtime equivalence is implied." };
            return result;
        }

        // The caller has already taken this element's budget slot.
        private static JObject CompareNode(CompareResultElement element, ScanBudget budget, int depth)
        {
            var children = new JArray();
            var node = new JObject { ["left"] = element.LeftName, ["right"] = element.RightName, ["state"] = element.ComparisonResult.ToString(),
                ["detail"] = element.DetailedInformation, ["children"] = children };
            foreach (var child in element.Elements)
            {
                if (depth >= 64 || !budget.Take()) { budget.Truncated = true; break; }
                children.Add(CompareNode(child, budget, depth + 1));
                if (budget.Truncated) break;
            }
            return node;
        }
    }
}
