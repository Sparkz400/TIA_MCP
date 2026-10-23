using System.Diagnostics;
using System.Security.Cryptography;
using TiaMcp.Core;

namespace TiaMcp.Tests;

internal static class ManifestPublicationTests
{
    internal static Task SuccessfulPublication()
    {
        foreach (bool exists in new[] { false, true })
        {
            using var f = new Fixture(exists);
            PackageStorage.PublishFile(f.Source, f.Target);
            Check.Equal(false, File.Exists(f.Source)); Check.Equal("next", File.ReadAllText(f.Target));
            PackageStorage.Save(f.Target, new { value = "saved" });
            Check.Equal("saved", PackageStorage.Load<System.Text.Json.JsonElement>(f.Target).GetProperty("value").GetString());
        }
        return Task.CompletedTask;
    }

    internal static Task ActualWindowsLocks()
    {
        if (!OperatingSystem.IsWindows()) return Task.CompletedTask;
        foreach (var (locked, exists) in new[] { ("source", false), ("source", true), ("target", true) })
        {
            using var f = new Fixture(exists); int attempts = 0, waits = 0;
            using var handle = new FileStream(locked == "source" ? f.Source : f.Target, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            PackageStorage.PublishFile(f.Source, f.Target, (source, target) => { attempts++; File.Move(source, target, true); },
                milliseconds => { Check.Equal(25, milliseconds); waits++; handle.Dispose(); });
            Check.Equal(2, attempts); Check.Equal(1, waits);
            Check.Equal(false, File.Exists(f.Source)); Check.Equal("next", File.ReadAllText(f.Target));
        }
        return Task.CompletedTask;
    }

    internal static Task BoundedErrors()
    {
        foreach (var error in new Exception[] { new UnauthorizedAccessException("denied"),
            new IOException("denied", unchecked((int)0x80070005)), new IOException("sharing", unchecked((int)0x80070020)),
            new IOException("lock", unchecked((int)0x80070021)), new IOException("disk", unchecked((int)0x80070070)),
            new IOException("unknown"), new InvalidOperationException("unknown"), new OperationCanceledException() })
        {
            using var f = new Fixture(); int attempts = 0, waits = 0;
            var caught = Catch(() => PackageStorage.PublishFile(f.Source, f.Target, (_, _) => { attempts++; throw error; },
                ms => { Check.Equal(25, ms); waits++; }));
            bool retry = OperatingSystem.IsWindows() && error.HResult is unchecked((int)0x80070005) or unchecked((int)0x80070020) or unchecked((int)0x80070021);
            Check.Equal(true, ReferenceEquals(error, caught)); Check.Equal(retry ? 10 : 1, attempts); Check.Equal(retry ? 9 : 0, waits);
            f.Unchanged();
        }
        if (OperatingSystem.IsWindows())
        {
            using var f = new Fixture(); File.SetAttributes(f.Target, FileAttributes.ReadOnly);
            try { Check.Equal(true, Catch(() => PackageStorage.PublishFile(f.Source, f.Target)) is UnauthorizedAccessException); f.Unchanged(); }
            finally { File.SetAttributes(f.Target, FileAttributes.Normal); }
        }
        return Task.CompletedTask;
    }

    internal static Task DriftAndUnknownOutcomes()
    {
        foreach (string defect in new[] { "source", "target", "created", "removed-target", "removed-source", "directory", "completed", "interrupt", "post-tamper", "returned-without-move" })
        {
            using var f = new Fixture(defect != "created"); int attempts = 0, waits = 0; Exception? fixtureFailure = null;
            var original = new UnauthorizedAccessException("locked");
            var interrupted = new OperationCanceledException();
            var caught = Catch(() => PackageStorage.PublishFile(f.Source, f.Target, (source, target) => {
                attempts++;
                try
                {
                    if (defect == "source") File.WriteAllText(source, "changed");
                    if (defect is "target" or "created") File.WriteAllText(target, "foreign");
                    if (defect == "removed-target") File.Delete(target);
                    if (defect == "removed-source") File.Delete(source);
                    if (defect == "directory") { File.Delete(target); Directory.CreateDirectory(target); File.WriteAllText(Path.Combine(target, "peer"), "foreign"); }
                    if (defect is "completed" or "post-tamper") File.Move(source, target, true);
                    if (defect == "post-tamper") { File.WriteAllText(target, "foreign"); return; }
                    if (defect == "returned-without-move") return;
                }
                catch (Exception error) { fixtureFailure = error; throw; }
                throw original;
            }, _ => { waits++; throw interrupted; }));
            if (fixtureFailure is not null) throw new InvalidOperationException($"Manifest fixture {defect} failed to establish its intended state; attempts={attempts}, waits={waits}.", fixtureFailure);
            Check.Equal(1, attempts); Check.Equal(defect == "interrupt" && OperatingSystem.IsWindows() ? 1 : 0, waits);
            if (defect == "completed") { Check.Equal(true, ReferenceEquals(original, caught)); Check.Equal("next", File.ReadAllText(f.Target)); }
            if (defect is "target" or "created" or "post-tamper") Check.Equal("foreign", File.ReadAllText(f.Target));
            if (defect == "source") Check.Equal("changed", File.ReadAllText(f.Source));
            if (defect == "directory") Check.Equal("foreign", File.ReadAllText(Path.Combine(f.Target, "peer")));
            if (defect == "removed-source") Check.Equal(false, File.Exists(f.Source));
            if (defect == "removed-target") Check.Equal(false, File.Exists(f.Target));
            if (defect is "interrupt" or "returned-without-move") f.Unchanged();
            if (defect is "post-tamper" or "returned-without-move") Check.Equal("PackageChanged", ((BridgeException)caught).Code);
            if (defect == "interrupt") Check.Equal(true, ReferenceEquals(OperatingSystem.IsWindows() ? interrupted : original, caught));
        }
        return Task.CompletedTask;
    }

    internal static Task UnsafePathsAndLinks()
    {
        using var f = new Fixture();
        Check.Equal("PackageInvalid", ((BridgeException)Catch(() => PackageStorage.PublishFile(f.Source, f.Source))).Code); f.Unchanged();
        Check.Equal("PackageInvalid", ((BridgeException)Catch(() => PackageStorage.PublishFile(f.Source, Path.Combine(f.Root, "other", "target")))).Code); f.Unchanged();
        string missing = Path.Combine(f.Pair, "absent");
        Check.Equal(true, Catch(() => PackageStorage.PublishFile(missing, f.Target)) is FileNotFoundException); f.Unchanged();
        string link = Path.Combine(f.Root, "alias"), peer = Path.Combine(f.Root, "peer"); Directory.CreateDirectory(peer);
        File.WriteAllText(Path.Combine(peer, "target"), "foreign");
        if (OperatingSystem.IsWindows())
        {
            var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string value in new[] { "/c", "mklink", "/J", link, peer }) start.ArgumentList.Add(value);
            using var child = Process.Start(start)!; child.WaitForExit(); Check.Equal(0, child.ExitCode);
        }
        else Directory.CreateSymbolicLink(link, peer);
        try
        {
            Check.Equal("PackageInvalid", ((BridgeException)Catch(() => PackageStorage.PublishFile(Path.Combine(link, "target"), Path.Combine(link, "next")))).Code);
            if (OperatingSystem.IsWindows())
            {
                int attempts = 0;
                var error = Catch(() => PackageStorage.PublishFile(f.Source, f.Target, (_, _) => { attempts++; throw new UnauthorizedAccessException(); }, _ => {
                    File.Delete(f.Target);
                    // Replace the target's parent with a junction only in this isolated fixture.
                    File.Move(f.Source, Path.Combine(f.Root, "retained")); Directory.Delete(f.Pair);
                    var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
                    foreach (string value in new[] { "/c", "mklink", "/J", f.Pair, peer }) start.ArgumentList.Add(value);
                    using var child = Process.Start(start)!; child.WaitForExit(); Check.Equal(0, child.ExitCode);
                }));
                Check.Equal("PackageInvalid", ((BridgeException)error).Code); Check.Equal(1, attempts);
                Directory.Delete(f.Pair); Directory.CreateDirectory(f.Pair);
            }
            Check.Equal("foreign", File.ReadAllText(Path.Combine(peer, "target")));
        }
        finally
        {
            if (Directory.Exists(f.Pair) && (File.GetAttributes(f.Pair) & FileAttributes.ReparsePoint) != 0) Directory.Delete(f.Pair);
            Directory.Delete(link);
        }
        return Task.CompletedTask;
    }

    internal static Task UploadFailureAndRecovery()
    {
        if (!OperatingSystem.IsWindows()) return Task.CompletedTask;
        using var f = new Fixture(); var store = new ArtifactUploadStore(f.Root);
        byte[] data = [0, 255, 4, 3]; string hash = Convert.ToHexStringLower(SHA256.HashData(data));
        var upload = store.Begin("evidence.bin", data.Length, hash); store.Write(upload.UploadId, 0, Convert.ToBase64String(data));
        string directory = Path.Combine(f.Root, "library-intake", "uploads", upload.UploadId), manifest = Path.Combine(directory, "upload.json");
        byte[] original = File.ReadAllBytes(manifest);
        using (var held = new FileStream(manifest, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            Check.Equal(true, Catch(() => store.Commit(upload.UploadId)) is UnauthorizedAccessException);
        Check.Equal(true, original.AsSpan().SequenceEqual(File.ReadAllBytes(manifest)));
        Check.Equal("receiving", store.Status(upload.UploadId).State);
        string prepared = Directory.GetFiles(directory, "*.tmp").Single(); byte[] intended = File.ReadAllBytes(prepared);
        var committed = store.Commit(upload.UploadId); Check.Equal("committed", committed.State);
        Check.Equal(true, intended.AsSpan().SequenceEqual(File.ReadAllBytes(manifest)));
        Check.Equal(true, intended.AsSpan().SequenceEqual(File.ReadAllBytes(prepared)));
        Check.Equal(committed, store.Commit(upload.UploadId));
        Check.Equal(hash, PackageStorage.FileHash(store.ResolveCommitted(committed.ArtifactRef!).Path));
        Check.Equal(true, data.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(directory, "data.bin"))));
        return Task.CompletedTask;
    }

    internal static Task SerializationIsNotRetried()
    {
        using var f = new Fixture(); var value = new FailedSerialization();
        Check.Equal(true, Catch(() => PackageStorage.Save(f.Target, value)) is InvalidOperationException);
        Check.Equal(1, value.Calls); f.Unchanged();
        Check.Equal(1, Directory.GetFiles(f.Pair, "target.*.tmp").Length);
        return Task.CompletedTask;
    }

    private static Exception Catch(Action action)
    { try { action(); } catch (Exception error) { return error; } throw new Exception("Expected publication failure."); }
    private sealed class FailedSerialization
    {
        internal int Calls;
        public string Value { get { Calls++; throw new InvalidOperationException("Serialization failed"); } }
    }
    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "manifest-publication-" + Guid.NewGuid().ToString("N"));
        internal string Pair => Path.Combine(Root, "pair");
        internal string Source => Path.Combine(Pair, "next.tmp");
        internal string Target => Path.Combine(Pair, "target");
        internal Fixture(bool target = true) { Directory.CreateDirectory(Pair); File.WriteAllText(Source, "next"); if (target) File.WriteAllText(Target, "prior"); }
        internal void Unchanged() { Check.Equal("next", File.ReadAllText(Source)); Check.Equal("prior", File.ReadAllText(Target)); }
        public void Dispose() { Directory.Delete(Root, true); }
    }
}
