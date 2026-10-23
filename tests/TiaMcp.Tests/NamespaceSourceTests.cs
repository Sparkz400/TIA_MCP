using TiaMcp.Openness.V21;
using EngineeringBridgeException = TiaMcp.Openness.V21.BridgeException;

namespace TiaMcp.Tests;

internal static class NamespaceSourceTests
{
    private const string Gate = "FUNCTION \"Gate\" : Bool BEGIN #Gate := TRUE; END_FUNCTION";
    private const string StateType = "TYPE \"State\" STRUCT Enabled : Bool; END_STRUCT; END_TYPE";

    internal static Task NamespaceSourcesPreserveExactScope()
    {
        Check.Equal("", EngineeringSources.DeclaredNamespace(Gate, false));
        Check.Equal("", EngineeringSources.DeclaredNamespace(StateType, true));
        string native = "NAMESPACE TiaMcp_UnitA\n" + Gate + "\nEND_NAMESPACE";
        Check.Equal("TiaMcp_UnitA", EngineeringSources.DeclaredNamespace(native, false));
        Check.Equal("Gate", EngineeringSources.DeclaredBlocks(native).Single());
        string dotted = "namespace \"Cell\".Unit_A\n" + StateType + "\nend_namespace;";
        Check.Equal("Cell.Unit_A", EngineeringSources.DeclaredNamespace(dotted, true));
        Check.Equal("State", EngineeringSources.DeclaredTypes(dotted).Single());
        string reopened = native + "; NAMESPACE TiaMcp_UnitA FUNCTION_BLOCK \"Latch\" BEGIN END_FUNCTION_BLOCK END_NAMESPACE";
        Check.Equal("TiaMcp_UnitA", EngineeringSources.DeclaredNamespace(reopened, false));
        Check.Equal("Gate,Latch", string.Join(",", EngineeringSources.DeclaredBlocks(reopened)));
        string qualifiedReferences = "NAMESPACE Cell.Unit FUNCTION \"Gate\" : Plant.Types.State VAR_INPUT Input : Plant.Types.State; END_VAR BEGIN #Gate := Plant.Shared.Value; END_FUNCTION END_NAMESPACE";
        Check.Equal("Cell.Unit", EngineeringSources.DeclaredNamespace(qualifiedReferences, false));
        Check.Equal("Gate", EngineeringSources.DeclaredBlocks(qualifiedReferences).Single());
        return Task.CompletedTask;
    }

    internal static async Task NamespaceEnvelopeCannotHideObjectScope()
    {
        string multiple = "NAMESPACE UnitA " + Gate + " FUNCTION \"AlsoCreated\" : Void BEGIN END_FUNCTION DATA_BLOCK \"Data\" BEGIN END_DATA_BLOCK END_NAMESPACE";
        Check.Equal("Gate,AlsoCreated,Data", string.Join(",", EngineeringSources.DeclaredBlocks(multiple)));
        Check.Equal("UnitA", EngineeringSources.DeclaredNamespace(multiple, false));
        foreach (string forbidden in new[] { "CONFIGURATION Hidden END_CONFIGURATION", "RESOURCE Hidden END_RESOURCE", "PLC_TAG_TABLE Hidden", "VAR_GLOBAL Hidden : Bool; END_VAR" })
        {
            await RejectBlocks("NAMESPACE UnitA " + forbidden + " " + Gate + " END_NAMESPACE");
            await RejectBlocks("NAMESPACE UnitA FUNCTION \"Visible\" : Void BEGIN " + forbidden + " END_FUNCTION END_NAMESPACE");
        }
        await RejectBlocks("NAMESPACE UnitA " + Gate + " " + StateType + " END_NAMESPACE");
        await RejectTypes("NAMESPACE UnitA " + StateType + " " + Gate + " END_NAMESPACE");
        await RejectBlocks("NAMESPACE UnitA " + Gate + " END_NAMESPACE " + Gate.Replace("Gate", "Other"));
        await RejectBlocks(Gate + " NAMESPACE UnitA " + Gate.Replace("Gate", "Other") + " END_NAMESPACE");
        await RejectBlocks("NAMESPACE UnitA " + Gate + " END_NAMESPACE NAMESPACE Other END_NAMESPACE");
        await RejectBlocks("NAMESPACE Other END_NAMESPACE NAMESPACE UnitA " + Gate + " END_NAMESPACE");
        await RejectTypes("NAMESPACE UnitA " + StateType + " END_NAMESPACE NAMESPACE Other END_NAMESPACE");
    }

    internal static async Task NamespaceMalformedAndQualifiedDeclarationsRejected()
    {
        foreach (string malformed in new[]
        {
            "NAMESPACE", "NAMESPACE UnitA " + Gate, "END_NAMESPACE " + Gate,
            "NAMESPACE UnitA NAMESPACE Nested " + Gate + " END_NAMESPACE END_NAMESPACE",
            "NAMESPACE UnitA FUNCTION \"Gate\" : Bool BEGIN END_NAMESPACE END_FUNCTION",
            "FUNCTION \"Gate\" : Bool BEGIN NAMESPACE UnitA END_NAMESPACE END_FUNCTION",
            "NAMESPACE .UnitA " + Gate + " END_NAMESPACE", "NAMESPACE UnitA..Part " + Gate + " END_NAMESPACE",
            "NAMESPACE UnitA. \"\" " + Gate + " END_NAMESPACE", "NAMESPACE UnitA.",
            "NAMESPACE \"Unit A\" " + Gate + " END_NAMESPACE", "NAMESPACE \"Unit$NName\" " + Gate + " END_NAMESPACE",
            "NAMESPACE \"\" " + Gate + " END_NAMESPACE", "NAMESPACE UnitA END_NAMESPACE",
            "NAMESPACE UnitA " + Gate + " END_NAMESPACE END_NAMESPACE",
            "NAMESPACE UnitA " + Gate + " END_NAMESPACE NAMESPACE UnitB " + Gate.Replace("Gate", "Other") + " END_NAMESPACE",
            "NAMESPACE UnitA " + Gate + " " + Gate.Replace("Gate", "gate") + " END_NAMESPACE",
            "FUNCTION UnitA.Hidden : Bool BEGIN END_FUNCTION",
            "FUNCTION \"UnitA\".\"Hidden\" : Bool BEGIN END_FUNCTION",
            "NAMESPACE UnitA FUNCTION_BLOCK Other.Hidden BEGIN END_FUNCTION_BLOCK END_NAMESPACE"
        }) await RejectBlocks(malformed);
        await RejectTypes("NAMESPACE UnitA TYPE Other.Hidden STRUCT Flag : Bool; END_STRUCT; END_TYPE END_NAMESPACE");
        await RejectTypes("NAMESPACE UnitA " + StateType);
    }

    internal static Task NamespaceKeywordsInCommentsAndLiteralsRemainContent()
    {
        const string source = """
            // NAMESPACE Wrong CONFIGURATION Hidden END_NAMESPACE
            NAMESPACE Actual.Unit
            (* nested (* NAMESPACE Nested END_NAMESPACE *) metadata *)
            FUNCTION "END_NAMESPACE" : Void
            BEGIN
              #Text := 'END_NAMESPACE NAMESPACE Hidden FUNCTION "Invisible" END_FUNCTION';
              #Escaped := 'It$'s NAMESPACE Other';
              #Doubled := 'It''s END_NAMESPACE';
              #"NAMESPACE" := #"END_NAMESPACE";
            END_FUNCTION
            END_NAMESPACE
            """;
        Check.Equal("Actual.Unit", EngineeringSources.DeclaredNamespace(source, false));
        Check.Equal("END_NAMESPACE", EngineeringSources.DeclaredBlocks(source).Single());
        return Task.CompletedTask;
    }

    private static async Task RejectBlocks(string source)
    {
        await Check.ThrowsAsync<EngineeringBridgeException>(() => Task.FromResult(EngineeringSources.DeclaredBlocks(source)));
        await Check.ThrowsAsync<EngineeringBridgeException>(() => Task.FromResult(EngineeringSources.DeclaredNamespace(source, false)));
    }

    private static async Task RejectTypes(string source)
    {
        await Check.ThrowsAsync<EngineeringBridgeException>(() => Task.FromResult(EngineeringSources.DeclaredTypes(source)));
        await Check.ThrowsAsync<EngineeringBridgeException>(() => Task.FromResult(EngineeringSources.DeclaredNamespace(source, true)));
    }
}
