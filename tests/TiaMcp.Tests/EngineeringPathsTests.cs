using TiaMcp.Openness.V21;
using EngineeringBridgeException = TiaMcp.Openness.V21.BridgeException;

namespace TiaMcp.Tests;

internal static class EngineeringPathsTests
{
    // These are pure preflight checks: no directory is created. Use a fresh
    // synthetic path on the current volume so long runner TEMP paths cannot
    // consume the native boundary before the fixture adds its tested suffix.
    private static string PureFixtureRoot(string prefix) =>
        Path.Combine(Path.GetPathRoot(AppContext.BaseDirectory)!, prefix + "-" + Guid.NewGuid().ToString("N"));

    internal static async Task ArchiveStagingBoundaryIsPure()
    {
        string root = PureFixtureRoot("tia-archive");
        string name = new string('A', 143 - root.Length - 1) + ".zap21";
        EngineeringPaths.ValidateProjectArchivePath(root, name);
        var failure = await Check.ThrowsAsync<EngineeringBridgeException>(() => Task.Run(() => EngineeringPaths.ValidateProjectArchivePath(root, "A" + name)));
        Check.Equal("ArchivePathTooLong", failure.Code);
        Check.Equal(false, Directory.Exists(root) || File.Exists(root));
    }

    internal static async Task ArchivePathsRejectInvalidComponents()
    {
        string root = Path.Combine(Path.GetTempPath(), "tia-archive-validation");
        foreach (string name in new[] { "", " ", ".zap21", "..zap21", "...zap21", "name .zap21", "name..zap21", "../before.zap21", "one\\before.zap21", "name:stream.zap21", "before.zip", "bad\0.zap21" })
        {
            var failure = await Check.ThrowsAsync<EngineeringBridgeException>(() => Task.Run(() => EngineeringPaths.ValidateProjectArchivePath(root, name)));
            Check.Equal("InvalidRequest", failure.Code);
        }
        foreach (string directory in new[] { "", ".", "relative", "C:relative", "\\relative" })
        {
            var failure = await Check.ThrowsAsync<EngineeringBridgeException>(() => Task.Run(() => EngineeringPaths.ValidateProjectArchivePath(directory, "before.zap21")));
            Check.Equal("InvalidRequest", failure.Code);
        }
    }

    internal static async Task RecoveryArchiveUsesShortName()
    {
        string root = PureFixtureRoot("tia-short");
        root = Path.Combine(root, new string('D', 143 - root.Length - 1 - 1 - 34));
        string name = EngineeringPaths.NewRecoveryArchiveName(root);
        Check.Equal(143, Path.Combine(root, Path.GetFileNameWithoutExtension(name)).Length);
        Check.Equal(true, name.StartsWith("a_", StringComparison.Ordinal));
        Check.Equal(true, Guid.TryParseExact(name.Substring(2, 32), "N", out _));
        Check.Equal(".zap21", Path.GetExtension(name));
        var failure = await Check.ThrowsAsync<EngineeringBridgeException>(() => Task.FromResult(EngineeringPaths.NewRecoveryArchiveName(root + "D")));
        Check.Equal("ArchivePathTooLong", failure.Code);
        Check.Equal(false, Directory.Exists(root) || File.Exists(root));
    }

    internal static async Task LengthBoundaryIsPure()
    {
        // A fresh nonexistent location is only inspected: preflight must not create it.
        string root = PureFixtureRoot("tia-path");
        int nameLength = 143 - root.Length - 1 - "r_12345678".Length - 1;
        if (nameLength <= 0) throw new InvalidOperationException("Test temporary root is too long for the TIA boundary fixture.");
        string name = new('P', nameLength);
        string recovery = EngineeringPaths.NewRecoveryDirectory(root, name);
        Check.Equal(143, Path.Combine(recovery, name).Length);
        Check.Equal(root, Path.GetDirectoryName(recovery));
        Check.Equal(true, Path.GetFileName(recovery).StartsWith("r_", StringComparison.Ordinal));
        Check.Equal(10, Path.GetFileName(recovery).Length);
        var failure = await Check.ThrowsAsync<EngineeringBridgeException>(() => Task.FromResult(EngineeringPaths.NewRecoveryDirectory(root, name + "P")));
        Check.Equal("RecoveryPathTooLong", failure.Code);
        Check.Equal(false, Directory.Exists(root) || File.Exists(root));
    }

    internal static async Task InvalidPathsRejected()
    {
        string root = Path.Combine(Path.GetTempPath(), "tia-path-validation");
        foreach (string name in new[] { "", " ", ".", "..", "one/two", "one\\two", "name:stream", "bad\0name" })
        {
            var failure = await Check.ThrowsAsync<EngineeringBridgeException>(() => Task.FromResult(EngineeringPaths.NewRecoveryDirectory(root, name)));
            Check.Equal("InvalidRequest", failure.Code);
        }
        foreach (string relative in new[] { "", ".", "relative", "C:relative", "\\relative" })
        {
            var failure = await Check.ThrowsAsync<EngineeringBridgeException>(() => Task.FromResult(EngineeringPaths.NewRecoveryDirectory(relative, "Project")));
            Check.Equal("InvalidRequest", failure.Code);
        }
    }
}
