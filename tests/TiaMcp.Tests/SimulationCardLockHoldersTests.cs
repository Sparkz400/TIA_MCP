using TiaMcp.Simulation;

namespace TiaMcp.Tests;

internal static class SimulationCardLockHoldersTests
{
    // The card-staging refusal of 23 October (0x80070005) is what any open file below the card causes, even one opened with
    // delete sharing; the probe names the holding process.
    internal static Task OpenFileBelowCardRefusesMoveAndIsNamed()
    {
        string root = Path.Combine(Path.GetTempPath(), "tiamcp-card-" + Guid.NewGuid().ToString("N"));
        string card = Path.Combine(root, "SIMATIC_MC"), file = Path.Combine(card, "SIMATIC.S7S", "OMSSTORE");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, new byte[16]);
        try
        {
            using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                var refused = Check.ThrowsAsync<IOException>(() => { Directory.Move(card, card + "-staged"); return Task.CompletedTask; }).Result;
                Check.Equal(unchecked((int)0x80070005), refused.HResult);
                var holders = SimulationCardLockHolders.Find(card, out string? error);
                Check.Equal(null, error);
                Check.Equal(true, holders.Any(holder => holder.ProcessId == Environment.ProcessId));
            }
            Check.Equal(0, SimulationCardLockHolders.Find(card, out string? none).Length);
            Check.Equal(null, none);
            Directory.Move(card, card + "-staged");
            Check.Equal(0, SimulationCardLockHolders.Find(Path.Combine(root, "missing"), out string? missing).Length);
            Check.Equal(true, missing!.StartsWith("System.IO.DirectoryNotFoundException", StringComparison.Ordinal));
        }
        finally { Directory.Delete(root, true); }
        return Task.CompletedTask;
    }
}
