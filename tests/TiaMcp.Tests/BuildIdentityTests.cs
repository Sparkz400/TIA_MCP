using System.Text.Json.Nodes;
using TiaMcp.Host;

namespace TiaMcp.Tests;

internal static class BuildIdentityTests
{
    // host_build_identity reads the commit a bundle was packaged from out of its manifest; an older manifest has none.
    internal static Task BundleReportsItsSourceCommit()
    {
        string root = Path.Combine(Path.GetTempPath(), "tia-mcp-build-identity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string manifest = Path.Combine(root, "manifest.json");
            File.WriteAllText(manifest, "﻿{\"schemaVersion\":1,\"productVersion\":\"0.9.0\",\"sourceCommit\":\"1671b2a4aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"sourceTreeDirty\":false,\"files\":[]}");
            var bundle = BuildIdentity.Bundle(root, manifest);
            Check.Equal("1671b2a4aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", bundle["sourceCommit"]?.GetValue<string>());
            Check.Equal(false, bundle["sourceTreeDirty"]?.GetValue<bool>());
            Check.Equal("0.9.0", bundle["productVersion"]?.GetValue<string>());
            Check.Equal(null, bundle["sourceCommitNote"]);
            Check.Equal(64, bundle["manifestSha256"]?.GetValue<string>().Length);

            File.WriteAllText(manifest, "{\"schemaVersion\":1,\"files\":[]}");
            bundle = BuildIdentity.Bundle(root, manifest);
            Check.Equal(null, bundle["sourceCommit"]);
            Check.Equal(true, bundle["sourceCommitNote"] is JsonValue);

            File.WriteAllText(manifest, "not json");
            Check.Equal(true, BuildIdentity.Bundle(root, manifest)["manifestReadError"] is JsonValue);
        }
        finally { Directory.Delete(root, true); }
        return Task.CompletedTask;
    }
}
