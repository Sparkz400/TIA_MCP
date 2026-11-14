using TiaMcp.Openness.V21;

namespace TiaMcp.Tests;

internal static class PhysicalDownloadRulesTests
{
    internal static Task AnswersOnlyFromAcknowledgements()
    {
        string? Pick(string type, bool stop, bool reinit, bool start, out string? refusal)
        { var value = PhysicalDownloadRules.Selection(type, stop, reinit, start, out string r); refusal = r; return value; }
        Check.Equal("ConsistentDownload", Pick("ConsistentBlocksDownload", false, false, false, out _));
        Check.Equal("KeepOnlineUserManagementData", Pick("UserManagementDownload", false, false, false, out _));
        Check.Equal<string?>(null, Pick("StopModules", false, false, false, out var stopRefusal));
        Check.Equal("StopNotAcknowledged", stopRefusal);
        Check.Equal("StopAll", Pick("StopModules", true, false, false, out _));
        // Keeping actual values is the default; reinitializing needs both acknowledgements.
        Check.Equal("KeepActualValues", Pick("DataBlockReinitializationOrKeepActualValues", true, false, false, out _));
        Check.Equal("KeepActualValues", Pick("DataBlockReinitializationOrKeepActualValues", false, true, false, out _));
        Check.Equal("StopPlcAndReinitialize", Pick("DataBlockReinitializationOrKeepActualValues", true, true, false, out _));
        Check.Equal<string?>(null, Pick("DataBlockReinitialization", false, true, false, out var reinitRefusal));
        Check.Equal("ReinitializeNotAcknowledged", reinitRefusal);
        Check.Equal("NoAction", Pick("StartModules", true, true, false, out _));
        Check.Equal("StartModule", Pick("StartModules", false, false, true, out _));
        Check.Equal<string?>(null, Pick("PlcMasterSecretPassword", true, true, true, out var password));
        Check.Equal("PhysicalAuthenticationRequired", password);
        Check.Equal<string?>(null, Pick("SomethingNew", true, true, true, out var unknown));
        Check.Equal("UnsupportedDownloadConfiguration", unknown);
        return Task.CompletedTask;
    }
}
