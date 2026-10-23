using Newtonsoft.Json.Linq;
using TiaMcp.Openness.V21;

internal static class TextBoundaryTests
{
    internal static void Run(Action<string, Action> test)
    {
        var fields = new (string Name, Func<Fixture, JObject> Owner, string Key, string Valid, int Maximum)[] {
            ("instance name", f => (JObject)f.Payload["integration"]["instanceDb"], "name", "Motor_1", 80),
            ("standalone caller", f => { f.Standalone(); return (JObject)f.Payload["integration"]["caller"]; }, "name", "MotorCaller", 80),
            ("mapping parameter", f => (JObject)f.Payload["integration"]["mappings"][0], "parameter", "Enable", 128),
            ("native network ID", f => (JObject)f.Payload["integration"]["caller"], "insertAfterNetworkId", "A09f", 32)
        };
        foreach (var field in fields)
            test("component preflight consumes complete " + field.Name, () => {
                var f = new Fixture(); var owner = field.Owner(f);
                foreach (string valid in new[] { field.Valid, new string('A', field.Maximum) })
                {
                    owner[field.Key] = valid; string original = f.Payload.ToString();
                    PlcLibraryIntegrationPlanner.ValidatePayload(f.Payload);
                    if (original != f.Payload.ToString()) throw new Exception("Validation rewrote the payload.");
                }
                foreach (string invalid in new[] { field.Valid + "\n", field.Valid + "\r", field.Valid + "\r\n",
                    field.Valid + "\u2028", field.Valid + "\u2029", field.Valid + "\0", field.Valid + " ",
                    "\n" + field.Valid, "A\nB", new string('A', field.Maximum + 1) })
                {
                    owner[field.Key] = invalid; string original = f.Payload.ToString();
                    Refuse("InvalidIntegrationRecipe", () => PlcLibraryIntegrationPlanner.ValidatePayload(f.Payload));
                    if (original != f.Payload.ToString()) throw new Exception("Refusal rewrote the payload.");
                }
            });
        test("component native content digests consume all characters", () => {
            foreach (string suffix in new[] { "\n", "\r", "\r\n", "\u2028", "\u2029", "\0", " " })
            {
                var f = new Fixture(); f.Versions[0]["contentSha256"] = new string('a', 64) + suffix;
                Refuse("DependencyUnresolved", () => f.Build());
            }
            var valid = new Fixture(); _ = valid.Build();
        });
        test("component simple-name boundary does not restrict native tag symbols", () => {
            var f = new Fixture(); f.Inventory["tags"][0]["name"] = "Motor input with spaces";
            var plan = f.Build(); var payload = (JObject)plan["steps"][1]["prepareArguments"]["payload"];
            TypedGraphicalPayload.Validate(payload);
            if ((string)payload["model"]["operands"][0]["components"][0] != "Motor input with spaces")
                throw new Exception("Native symbolic component was changed.");
        });
    }

    private static void Refuse(string code, Action action)
    {
        try { action(); }
        catch (PlcIntegrationPlanException error) when (error.Code == code) { return; }
        throw new Exception("Expected " + code + " for the complete input.");
    }
}
