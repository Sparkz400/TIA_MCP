using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using TiaMcp.Core;

namespace TiaMcp.Tests;

internal static class JournalDirectoryPublicationTests
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    private static IOException Denied() => new("Injected rename denial", unchecked((int)0x80070005));

    internal static Task ActualWindowsHandle()
    {
        if (!OperatingSystem.IsWindows()) return Task.CompletedTask;
        using var f = new Fixture();
        using var handle = CreateFileW(f.Source, 0x80000000, 3, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        Check.Equal(false, handle.IsInvalid);
        int attempts = 0, waits = 0;
        PackageStorage.PublishDirectory(f.Source, f.Destination, (a, b) => { attempts++; Directory.Move(a, b); },
            _ => { waits++; handle.Dispose(); });
        Check.Equal(2, attempts); Check.Equal(1, waits);
        Check.Equal(false, Directory.Exists(f.Source));
        Check.Equal("complete journal", File.ReadAllText(Path.Combine(f.Destination, "record.json")));
        return Task.CompletedTask;
    }

    internal static async Task BoundsAndNoOverwrite()
    {
        using var f = new Fixture(); int attempts = 0, waits = 0;
        await Check.ThrowsAsync<IOException>(() => {
            PackageStorage.PublishDirectory(f.Source, f.Destination, (_, _) => { attempts++; throw Denied(); }, _ => waits++);
            return Task.CompletedTask;
        });
        Check.Equal(OperatingSystem.IsWindows() ? 10 : 1, attempts);
        Check.Equal(OperatingSystem.IsWindows() ? 9 : 0, waits);
        Check.Equal("complete journal", File.ReadAllText(Path.Combine(f.Source, "record.json")));
        foreach (int code in new[] { unchecked((int)0x80070002), unchecked((int)0x80070070), unchecked((int)0x800700b7) })
        {
            attempts = 0;
            await Check.ThrowsAsync<IOException>(() => {
                PackageStorage.PublishDirectory(f.Source, f.Destination, (_, _) => { attempts++; throw new IOException("Other failure", code); }, _ => throw new Exception("Unexpected retry"));
                return Task.CompletedTask;
            });
            Check.Equal(1, attempts);
        }
        Directory.CreateDirectory(f.Destination); File.WriteAllText(Path.Combine(f.Destination, "competitor.txt"), "preserved");
        attempts = 0;
        await Check.ThrowsAsync<IOException>(() => {
            PackageStorage.PublishDirectory(f.Source, f.Destination, (_, _) => { attempts++; throw Denied(); }, _ => throw new Exception("Existing destination retried"));
            return Task.CompletedTask;
        });
        Check.Equal(1, attempts); Check.Equal("preserved", File.ReadAllText(Path.Combine(f.Destination, "competitor.txt")));
        await Check.ThrowsAsync<BridgeException>(() => {
            PackageStorage.PublishDirectory(f.Source, Path.Combine(f.Root, "other", "destination"), (_, _) => throw new Exception("Cross-parent move dispatched"), _ => { });
            return Task.CompletedTask;
        });
        using var published = new Fixture(); attempts = 0;
        await Check.ThrowsAsync<IOException>(() => {
            PackageStorage.PublishDirectory(published.Source, published.Destination, (a, b) => { attempts++; Directory.Move(a, b); throw Denied(); }, _ => throw new Exception("Published move replayed"));
            return Task.CompletedTask;
        });
        Check.Equal(1, attempts); Check.Equal("complete journal", File.ReadAllText(Path.Combine(published.Destination, "record.json")));
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "journal-publication-" + Guid.NewGuid().ToString("N"));
        internal string Source => Path.Combine(Root, ".preparing-one");
        internal string Destination => Path.Combine(Root, "one");
        internal Fixture() { Directory.CreateDirectory(Source); File.WriteAllText(Path.Combine(Source, "record.json"), "complete journal"); }
        public void Dispose() { Directory.Delete(Root, true); }
    }
}
