using TiaMcp.Simulation;

namespace TiaMcp.Tests;

internal static class SimulationCardMoveRetryTests
{
    private static IOException Sharing() => new("Injected sharing denial", unchecked((int)0x80070020));

    internal static Task TransientLockAndReconciliation()
    {
        long now = 0; int moves = 0, after = 0, guards = 0;
        SimulationCardMoveRetry.Run(() => { guards++; now += 500; }, () => { if (++moves <= 5) throw Sharing(); },
            () => after++, () => false, (_, _, _, _) => { }, () => now, delay => now += delay);
        Check.Equal(6, moves); Check.Equal(6, guards); Check.Equal(1, after);
        Check.Equal(true, now > 2000 && now < SimulationCardMoveRetry.RetryAdmissionBudgetMs);
        moves = 0; after = 0;
        SimulationCardMoveRetry.Run(() => { }, () => { moves++; throw Sharing(); }, () => after++, () => true,
            (_, _, _, _) => { }, () => now, _ => throw new Exception("A reconciled move was replayed."));
        Check.Equal(1, moves); Check.Equal(0, after);
        return Task.CompletedTask;
    }

    internal static async Task BoundsAndNoNativeReplay()
    {
        long now = 0; int moves = 0;
        await Check.ThrowsAsync<IOException>(() => {
            SimulationCardMoveRetry.Run(() => { }, () => { moves++; throw Sharing(); }, () => { }, () => false,
                (_, _, _, _) => { }, () => now, delay => now += delay);
            return Task.CompletedTask;
        });
        Check.Equal(true, now <= SimulationCardMoveRetry.RetryAdmissionBudgetMs && moves <= SimulationCardMoveRetry.MaximumMoveCalls);
        moves = 0; now = 0;
        await Check.ThrowsAsync<IOException>(() => {
            SimulationCardMoveRetry.Run(() => { if (moves > 0) now += SimulationCardMoveRetry.RetryAdmissionBudgetMs; },
                () => { moves++; throw Sharing(); }, () => { }, () => false, (_, _, _, _) => { }, () => now, delay => now += delay);
            return Task.CompletedTask;
        });
        Check.Equal(1, moves);
        foreach (string failure in new[] { "guard", "checkpoint", "reconcile", "postcondition", "nonretryable" })
        {
            moves = 0;
            await Check.ThrowsAsync<InvalidOperationException>(() => {
                SimulationCardMoveRetry.Run(() => { if (failure == "guard") throw new InvalidOperationException(); },
                    () => { moves++; if (failure == "nonretryable") throw new InvalidOperationException(); if (failure != "postcondition") throw Sharing(); },
                    () => throw new InvalidOperationException(), () => { if (failure == "reconcile") throw new InvalidOperationException(); return false; },
                    (_, _, _, _) => { if (failure == "checkpoint") throw new InvalidOperationException(); },
                    () => now, _ => throw new Exception("A non-move operation was replayed."));
                return Task.CompletedTask;
            });
            Check.Equal(failure is "guard" or "checkpoint" ? 0 : 1, moves);
        }
    }
}
