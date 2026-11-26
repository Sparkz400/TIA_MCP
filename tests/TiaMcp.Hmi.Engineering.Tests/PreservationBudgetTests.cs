using TiaMcp.Model;

static class PreservationBudgetTests
{
    static void Assert(bool value) { if (!value) throw new Exception("Unexpected preservation-budget decision."); }

    internal static void Run(Action<string, Action> check)
    {
        const long MiB = 1024L * 1024;
        check("Project capture accepts the production failure at 102 exports and over 32 MiB", () => {
            Assert(HmiPreservationBudget.AllowsAddition(102, 32 * MiB, 640 * 1024));
            Assert(HmiPreservationBudget.AllowsAddition(102, 15 * MiB, 50 * MiB));
            Assert(HmiPreservationBudget.AllowsAddition(200, 128 * MiB, MiB));
        });
        check("Project capture includes the last file and byte but rejects truncation beyond either bound", () => {
            Assert(HmiPreservationBudget.AllowsAddition(2047, 511 * MiB, MiB));
            Assert(!HmiPreservationBudget.AllowsAddition(2048, 0, 1));
            Assert(!HmiPreservationBudget.AllowsAddition(1, 511 * MiB, MiB + 1));
            Assert(!HmiPreservationBudget.AllowsAddition(1, 512 * MiB, 1));
        });
        check("Individual native exports cannot exceed the shared 64 MiB parser bound", () => {
            Assert(HmiPreservationBudget.AllowsAddition(0, 0, 64 * MiB));
            Assert(!HmiPreservationBudget.AllowsAddition(0, 0, 64 * MiB + 1));
            Assert(HmiPreservationBudget.MaxArtifactBytes == HmiPreservationRules.MaxDocumentBytes);
        });
        check("The XML reader accepts a large native-size document and rejects an oversized UTF-8 document", () => {
            var xml = "<Document><Payload>" + new string('x', 50 * 1024 * 1024) + "</Payload></Document>";
            Assert(HmiPreservationRules.ReadXml(xml).Root.Element("Payload").Value.Length == 50 * 1024 * 1024);
            try {
                HmiPreservationRules.ReadXml("<Document>" + new string('\u00e4', 32 * 1024 * 1024) + "</Document>");
            }
            catch (ArgumentException) { return; }
            throw new Exception("UTF-8 byte limit must still refuse oversized documents.");
        });
        check("Invalid or overflowing budget inputs cannot authorize another export", () => {
            Assert(!HmiPreservationBudget.AllowsAddition(-1, 0, 0));
            Assert(!HmiPreservationBudget.AllowsAddition(0, -1, 0));
            Assert(!HmiPreservationBudget.AllowsAddition(0, 0, -1));
            Assert(!HmiPreservationBudget.AllowsAddition(1, long.MaxValue, 1));
            Assert(!HmiPreservationBudget.AllowsAddition(1, 1, long.MaxValue));
            Assert(!HmiPreservationBudget.AllowsAddition(int.MaxValue, 0, 1));
        });
    }
}
