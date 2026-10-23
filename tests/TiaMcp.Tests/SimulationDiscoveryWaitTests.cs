using TiaMcp.Simulation;

namespace TiaMcp.Tests;

internal static class SimulationDiscoveryWaitTests
{
    internal static Task TransientEntriesAndStableAbsence()
    {
        long now = 0; int guards = 0;
        var snapshots = new Queue<bool>([false, true, false, true, true]);
        var evidence = new List<(int Attempt, bool Empty, int Consecutive)>();
        bool clear = SimulationDiscoveryWait.ObserveEmpty(() => guards++, () => snapshots.Dequeue(),
            (attempt, empty, consecutive, elapsed) => evidence.Add((attempt, empty, consecutive)),
            () => now, delay => now += delay);
        Check.Equal(true, clear); Check.Equal(10, guards); Check.Equal(5, evidence.Count);
        Check.Equal(0, evidence[2].Consecutive); Check.Equal(2, evidence[4].Consecutive);
        Check.Equal(2000L, now); Check.Equal(false, evidence[0].Empty);
        return Task.CompletedTask;
    }

    internal static Task PersistentDevicesAndLateSnapshots()
    {
        long now = 0; int queries = 0;
        bool clear = SimulationDiscoveryWait.ObserveEmpty(() => { }, () => { queries++; return false; },
            (_, _, _, _) => { }, () => now, delay => now += delay);
        Check.Equal(false, clear); Check.Equal(SimulationDiscoveryWait.BudgetMs, (int)now);
        Check.Equal(true, queries <= SimulationDiscoveryWait.MaximumQueries);
        now = 0; queries = 0;
        clear = SimulationDiscoveryWait.ObserveEmpty(() => { }, () => { queries++; now += 16000; return true; },
            (_, _, _, _) => { }, () => now, delay => now += delay);
        Check.Equal(false, clear); Check.Equal(2, queries);
        now = 0; queries = 0;
        clear = SimulationDiscoveryWait.ObserveEmpty(() => { }, () => { queries++; return false; },
            (_, _, _, _) => { }, () => now, _ => { });
        Check.Equal(false, clear); Check.Equal(SimulationDiscoveryWait.MaximumQueries, queries);
        return Task.CompletedTask;
    }

    internal static async Task GuardAndReadFailuresNeverRetry()
    {
        foreach (string failure in new[] { "before", "read", "after", "record" })
        {
            int guards = 0, queries = 0, waits = 0;
            await Check.ThrowsAsync<InvalidOperationException>(() => {
                SimulationDiscoveryWait.ObserveEmpty(() => {
                    guards++;
                    if (failure == "before" || (failure == "after" && guards == 2)) throw new InvalidOperationException();
                }, () => { queries++; if (failure == "read") throw new InvalidOperationException(); return true; },
                    (_, _, _, _) => { if (failure == "record") throw new InvalidOperationException(); },
                    () => 0, _ => waits++);
                return Task.CompletedTask;
            });
            Check.Equal(failure == "before" ? 0 : 1, queries); Check.Equal(0, waits);
        }
    }
}
