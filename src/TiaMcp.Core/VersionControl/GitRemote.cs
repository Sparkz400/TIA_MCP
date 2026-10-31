namespace TiaMcp.Core.VersionControl;

/// <summary>
/// The one administrator-configured remote (Host --git-remote). MCP callers never choose a URL. A local bare repository
/// is the default; credential-free https needs --git-allow-network-remote. User info, queries and other transports are refused.
/// </summary>
public sealed class GitRemote
{
    /// <summary>The literal Git argument: a full local path or the exact https URL.</summary>
    public string Location { get; }
    /// <summary>The single Git protocol this remote enables (file or https); every other protocol stays disallowed.</summary>
    public string Protocol { get; }
    public bool IsLocal => Protocol == "file";
    private GitRemote(string location, string protocol) { Location = location; Protocol = protocol; }

    public static GitRemote Parse(string value, bool allowNetwork)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048 || value.Any(char.IsControl) || value.StartsWith('-'))
            throw new BridgeException("GitRemoteRefused", "Configure one bounded remote without control characters.");
        string? local = value.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
            ? Uri.TryCreate(value, UriKind.Absolute, out var file) && file.IsFile && file.Host.Length == 0 && file.Query.Length == 0 && file.Fragment.Length == 0 ? file.LocalPath : null
            : Path.IsPathFullyQualified(value) ? value : null;
        if (local is not null) return new(GitSafety.PathRoot(local, false), "file");
        if (value.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            throw new BridgeException("GitRemoteRefused", "A file remote must be a file:/// URL of a local bare repository.");
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.Host.Length == 0)
            throw new BridgeException("GitRemoteRefused", "Use an absolute local path or file:/// URL of a bare repository, or (with --git-allow-network-remote) an https:// URL.");
        if (!allowNetwork) throw new BridgeException("GitRemoteRefused", "A non-local remote requires the Host option --git-allow-network-remote.");
        if (uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new BridgeException("GitRemoteRefused", "Remote URLs never carry user names, credentials, queries or fragments.");
        return new(value, "https");
    }

    // receive-pack runs the remote repository's own hooks and configuration, outside the Host's pinned -c profile, so a local
    // remote must be a minimal bare repository: the runner's own config allowlist, no hooks, alternates or other indirection.
    // Callers check again immediately before dispatch; a writer of the remote after that point is outside the Host's control.
    internal void RequireSafeLocal()
    {
        if (!IsLocal) return;
        var root = GitSafety.PathRoot(Location, true);
        if (!File.Exists(Path.Combine(root, "HEAD")) || !Directory.Exists(Path.Combine(root, "objects")) || !Directory.Exists(Path.Combine(root, "refs")))
            throw new BridgeException("GitRemoteRefused", "The configured local remote must be a bare Git repository.");
        GitSafety.NoLinks(Path.Combine(root, "objects")); GitSafety.NoLinks(Path.Combine(root, "refs"));
        GitSafety.RequireNoIndirection(root);
        GitSafety.RequireMinimalConfig(root, true);
    }
}
