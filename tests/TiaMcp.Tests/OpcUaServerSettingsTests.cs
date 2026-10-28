namespace TiaMcp.Tests;

internal static class OpcUaServerSettingsTests
{
    internal static Task PinnedFieldsAndPayloadForms()
    {
        Check.Equal("serverEnabled,port,guestAccess,autoAcceptClientCertificates,runtimeLicense",
            string.Join(",", Core.OpcUaServerSettings.Settings.Select(s => s.Field)));
        Check.Equal("OpcUaServer", Core.OpcUaServerSettings.Find("serverEnabled")!.Attribute);
        Check.Equal("OpcUaPort", Core.OpcUaServerSettings.Find("port")!.Attribute);
        Check.Equal("OpcUaAutoAcceptAllClientCertificates", Core.OpcUaServerSettings.Find("autoAcceptClientCertificates")!.Attribute);
        Check.Equal(null, Core.OpcUaServerSettings.Find("guestAccess")!.Attribute);
        Check.Equal(true, Core.OpcUaServerSettings.Find("runtimeLicense")!.OnCpu);
        Check.Equal(null, Core.OpcUaServerSettings.Find("password"));
        Check.Equal((object)"SimaticOpcUaS71500Small", Core.OpcUaServerSettings.PayloadValue("runtimeLicense", 1UL));
        Check.Equal((object)true, Core.OpcUaServerSettings.PayloadValue("serverEnabled", true));
        Check.Equal(null, Core.OpcUaServerSettings.PayloadValue("runtimeLicense", 9UL));
        Check.Equal((object)2UL, Core.OpcUaServerSettings.NativeValue("runtimeLicense", "SimaticOpcUaS71500Medium", typeof(ulong)));
        Check.Equal((object)(ushort)4841, Core.OpcUaServerSettings.NativeValue("port", 4841L, typeof(ushort)));
        return Task.CompletedTask;
    }

    internal static Task RefusesBadValuesAndStaleExpectations()
    {
        Check.Equal(null, Core.OpcUaServerSettings.Refusal("serverEnabled", false, true, false));
        Check.Equal(null, Core.OpcUaServerSettings.Refusal("runtimeLicense", "NoLicense", "SimaticOpcUaS71500Small", 0UL));
        Check.Equal(null, Core.OpcUaServerSettings.Refusal("port", 4840L, 4841L, (ushort)4840));
        Check.Equal("InvalidRequest", Core.OpcUaServerSettings.Refusal("port", 4840L, 0L, (ushort)4840));
        Check.Equal("InvalidRequest", Core.OpcUaServerSettings.Refusal("port", 4840L, 65536L, (ushort)4840));
        Check.Equal("InvalidRequest", Core.OpcUaServerSettings.Refusal("serverEnabled", false, "true", false));
        Check.Equal("InvalidRequest", Core.OpcUaServerSettings.Refusal("runtimeLicense", "NoLicense", "Unlimited", 0UL));
        Check.Equal("ExpectedValueMismatch", Core.OpcUaServerSettings.Refusal("serverEnabled", true, false, false));
        Check.Equal("ExpectedValueMismatch", Core.OpcUaServerSettings.Refusal("runtimeLicense", "SimaticOpcUaS71500Small", "NoLicense", 0UL));
        Check.Equal("ExpectedValueMismatch", Core.OpcUaServerSettings.Refusal("port", 4841L, 4842L, (ushort)4840));
        // A native value outside the documented range never matches an expectation.
        Check.Equal("ExpectedValueMismatch", Core.OpcUaServerSettings.Refusal("runtimeLicense", "NoLicense", "NoLicense", 7UL));
        return Task.CompletedTask;
    }
}
