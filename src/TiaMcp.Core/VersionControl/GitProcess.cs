using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace TiaMcp.Core.VersionControl;

internal static partial class GitSafety
{
    internal static readonly UTF8Encoding Utf8 = new(false, true);
    internal static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    internal static string HashFile(string path) { using var stream = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(stream)); }
    internal static string ObjectId(string value)
    {
        if (!Regex.IsMatch(value, "^[0-9a-f]{40}$", RegexOptions.CultureInvariant))
            throw new BridgeException("GitInvalidObjectId", "An exact SHA-1 Git object ID is required; symbolic revisions are not accepted.");
        return value;
    }
    internal static string Branch(string value)
    {
        if (value.Length is < 1 or > 180 || !Regex.IsMatch(value, "^[A-Za-z0-9][A-Za-z0-9._/-]*$", RegexOptions.CultureInvariant) ||
            value.Contains("..") || value.Contains("//") || value.EndsWith('/') ||
            value.Split('/').Any(x => x.StartsWith('.') || x.EndsWith('.') || x.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)))
            throw new BridgeException("GitInvalidReference", "Use a bounded literal local branch name.");
        return "refs/heads/" + value;
    }
    internal static string PathRoot(string path, bool exists)
    {
        if (!Path.IsPathFullyQualified(path)) throw new BridgeException("GitUnsafePath", "An absolute resource path is required.");
        if (OperatingSystem.IsWindows() && path.StartsWith("\\\\", StringComparison.Ordinal))
            throw new BridgeException("GitUnsafePath", "The local Git profile does not accept UNC or device namespace paths.");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (full == Path.GetPathRoot(full)) throw new BridgeException("GitUnsafePath", "A filesystem root is not an owned Git resource.");
        foreach (var segment in full[Path.GetPathRoot(full)!.Length..].Split(Path.DirectorySeparatorChar))
            VciGuard.Path(segment, 255);
        for (var current = full; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if (File.Exists(current) || Directory.Exists(current))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new BridgeException("GitUnsafePath", "Reparse points and symbolic links are not supported.");
            }
        if (exists && !Directory.Exists(full)) throw new BridgeException("GitRepositoryMissing", "The exact repository directory does not exist.");
        return full;
    }
    internal static void RequireProcessDirectory(string path)
    {
        if (OperatingSystem.IsWindows() && path.Length >= 260)
            throw new BridgeException("GitWorkingDirectoryTooLong", "Windows Git requires a working/control directory shorter than 260 UTF-16 characters. Select a shorter workspace root; contained file and object paths may be longer.");
    }
    internal static void RequireRepositoryDirectory(string path)
    {
        RequireProcessDirectory(path);
        // Git checks the metadata directory before loading core.longpaths.
        if (OperatingSystem.IsWindows() && Path.Combine(path, ".git", "objects").Length >= 260)
            throw new BridgeException("GitWorkingDirectoryTooLong", "Windows Git requires the repository's .git/objects directory below 260 UTF-16 characters during startup. Select a shorter repository root; contained file and object paths may be longer.");
    }
    internal static void RelativePath(string path)
    {
        VciGuard.Path(path, 255);
        if (path.Split('/').Any(x => x.Equals(".git", StringComparison.OrdinalIgnoreCase) || Regex.IsMatch(x, "^git~[0-9]+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)))
            throw new BridgeException("GitUnsafePath", "Git metadata cannot be selected as package content.");
    }
    internal static string BlobObjectId(byte[] bytes)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        hash.AppendData(Encoding.ASCII.GetBytes("blob " + bytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\0"));
        hash.AppendData(bytes);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
    internal static void NoLinks(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new BridgeException("GitUnsafePath", "Reparse points and symbolic links are not supported.");
    }
    internal static string Decode(byte[] bytes)
    {
        try { return Utf8.GetString(bytes); }
        catch (DecoderFallbackException ex) { throw new BridgeException("GitInvalidOutput", "Git returned invalid UTF-8 metadata.", ex); }
    }
}

internal sealed record GitProcessResult(int ExitCode, byte[] Bytes, string Error)
{
    internal string Text => GitSafety.Decode(Bytes);
}

/// <summary>Internal only: callers cannot submit commands, arguments, executable overrides or environment variables.</summary>
internal sealed class GitProcess
{
    private readonly string executable;
    private readonly string control;
    private readonly GitRunnerLimits limits;
    internal string ExecutableSha256 { get; }
    internal GitProcess(string executable, string control, GitRunnerLimits limits)
    {
        this.executable = GitSafety.PathRoot(executable, false);
        if (!File.Exists(this.executable) || !Path.GetFileName(this.executable).Equals(OperatingSystem.IsWindows() ? "git.exe" : "git", StringComparison.OrdinalIgnoreCase))
            throw new BridgeException("GitExecutableRejected", "Supply the explicitly allowlisted, absolute Git executable path.");
        this.control = control; this.limits = limits;
        ExecutableSha256 = GitSafety.HashFile(this.executable);
    }

    internal async Task<GitProcessResult> RunAsync(string directory, IEnumerable<string> arguments,
        CancellationToken cancellationToken, byte[]? input = null, IReadOnlyDictionary<string, string>? environment = null,
        bool allowFailure = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        GitSafety.RequireProcessDirectory(directory);
        GitSafety.NoLinks(executable);
        if (ExecutableSha256 != GitSafety.HashFile(executable)) throw new BridgeException("GitExecutableChanged", "The allowlisted Git executable changed.");
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.Environment.Clear();
        foreach (var name in new[] { "SystemRoot", "WINDIR" })
            if (Environment.GetEnvironmentVariable(name) is { } value) start.Environment[name] = value;
        start.Environment["PATH"] = Path.GetDirectoryName(executable)! + Path.PathSeparator + Environment.SystemDirectory;
        foreach (var name in new[] { "HOME", "USERPROFILE", "XDG_CONFIG_HOME", "TEMP", "TMP" }) start.Environment[name] = control;
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        start.Environment["GIT_CONFIG_SYSTEM"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        start.Environment["GIT_ATTR_NOSYSTEM"] = "1";
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GCM_INTERACTIVE"] = "Never";
        start.Environment["GIT_NO_REPLACE_OBJECTS"] = "1";
        start.Environment["GIT_LITERAL_PATHSPECS"] = "1";
        start.Environment["LC_ALL"] = "C";
        if (environment is not null) foreach (var item in environment) start.Environment[item.Key] = item.Value;
        foreach (var argument in new[] { "--no-pager", "--no-optional-locks", "-c", "core.hooksPath=" + Path.Combine(control, "empty"),
            "-c", "core.fsmonitor=false", "-c", "core.untrackedCache=false", "-c", "gc.auto=0", "-c", "maintenance.auto=false",
            "-c", "core.attributesFile=" + Path.Combine(control, "empty-attributes"), "-c", "core.excludesFile=" + Path.Combine(control, "empty-excludes"),
            "-c", "core.autocrlf=false", "-c", "core.safecrlf=false", "-c", "core.quotePath=false", "-c", "diff.autoRefreshIndex=false", "-c", "color.ui=false",
            "-c", "credential.helper=", "-c", "protocol.allow=never", "-c", "commit.gpgSign=false", "-c", "tag.gpgSign=false" })
            start.ArgumentList.Add(argument);
        if (OperatingSystem.IsWindows())
        {
            start.ArgumentList.Add("-c"); start.ArgumentList.Add("core.longpaths=true");
        }
        // Never let failed discovery select an enclosing, unrelated repository.
        start.ArgumentList.Add("--git-dir=.git"); start.ArgumentList.Add("--work-tree=.");
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        using var timeout = new CancellationTokenSource(limits.TimeoutMilliseconds);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        process.Start();
        using var registration = linked.Token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
        async Task<byte[]> Drain(Stream stream, int maximum)
        {
            using var result = new MemoryStream();
            var buffer = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(buffer, linked.Token)) != 0)
            {
                if (result.Length + count > maximum)
                {
                    try { process.Kill(true); } catch (InvalidOperationException) { }
                    throw new BridgeException("GitOutputLimitExceeded", "Git output exceeded its configured bound; process tree stopped.");
                }
                result.Write(buffer, 0, count);
            }
            return result.ToArray();
        }
        var stdout = Drain(process.StandardOutput.BaseStream, limits.MaxOutputBytes);
        var stderr = Drain(process.StandardError.BaseStream, Math.Min(limits.MaxOutputBytes, 65536));
        async Task Send()
        {
            try { if (input is not null) await process.StandardInput.BaseStream.WriteAsync(input, linked.Token); }
            finally { process.StandardInput.Close(); }
        }
        try
        {
            await Task.WhenAll(stdout, stderr, Send(), process.WaitForExitAsync(linked.Token));
            linked.Token.ThrowIfCancellationRequested();
            var result = new GitProcessResult(process.ExitCode, await stdout, GitSafety.Decode(await stderr));
            if (!allowFailure && result.ExitCode != 0)
                throw new BridgeException("GitCommandFailed", "A controlled local Git operation failed (exit " + result.ExitCode + "): " + result.Error);
            return result;
        }
        catch (Exception ex) when (ex is OperationCanceledException || linked.IsCancellationRequested)
        {
            throw new BridgeException(cancellationToken.IsCancellationRequested ? "GitCancelled" : "GitTimedOut",
                "Controlled Git process was stopped; mutating dispatch requires reconciliation.", ex);
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None);
        }
    }
}
