#nullable disable
using System;
using System.IO;

namespace TiaMcp.Simulation
{
    public static class SimulationCardMoveRetry
    {
        public const int RetryAdmissionBudgetMs = 15000;
        public const int MaximumMoveCalls = 61;

        // Pure orchestration: only the supplied filesystem move is retried. Native
        // identity reads are guards; password/state calls are outside this method.
        public static void Run(Action validateBefore, Action move, Action validateAfter,
            Func<bool> reconcile, Action<string, int, long, Exception> checkpoint, Func<long> clock, Action<int> delay)
        {
            const int budget = RetryAdmissionBudgetMs, maximumAttempts = MaximumMoveCalls;
            int attempts = 0;
            long? retryStart = null;
            Exception lastMoveError = null;
            Func<long> elapsed = () => retryStart.HasValue ? Math.Max(0L, clock() - retryStart.Value) : 0L;
            while (true)
            {
                validateBefore();
                if (retryStart.HasValue && (elapsed() >= budget || attempts >= maximumAttempts))
                {
                    checkpoint("retryAdmissionExpired", attempts, elapsed(), lastMoveError);
                    throw lastMoveError;
                }
                int attempt = attempts + 1;
                checkpoint("beforeMove", attempt, elapsed(), null);
                // Check again after flushing durable intent; no delayed retry is
                // admitted once the budget expires, even if a guard/flush was slow.
                if (retryStart.HasValue && elapsed() >= budget)
                {
                    checkpoint("retryAdmissionExpired", attempts, elapsed(), lastMoveError);
                    throw lastMoveError;
                }
                attempts = attempt;
                try { move(); }
                catch (Exception exception)
                {
                    if (!retryStart.HasValue) retryStart = clock();
                    lastMoveError = exception;
                    checkpoint("moveThrew", attempts, elapsed(), exception);
                    bool completed = reconcile();
                    if (!RetryableMoveException(exception)) throw;
                    if (completed)
                    {
                        checkpoint("completedAfterExceptionVerified", attempts, elapsed(), exception);
                        return;
                    }
                    long remaining = budget - elapsed();
                    if (remaining <= 0 || attempts >= maximumAttempts)
                    {
                        checkpoint("retryAdmissionExpired", attempts, elapsed(), exception);
                        throw;
                    }
                    int pause = (int)Math.Min(250L, remaining);
                    checkpoint("retryScheduled", attempts, elapsed(), exception);
                    delay(pause);
                    continue;
                }
                validateAfter();
                checkpoint("moveReturnedAndVerified", attempts, elapsed(), null);
                return;
            }
        }

        private static bool RetryableMoveException(Exception exception)
        {
            if (!(exception is IOException) && !(exception is UnauthorizedAccessException)) return false;
            return exception.HResult == unchecked((int)0x80070005) ||
                exception.HResult == unchecked((int)0x80070020) || exception.HResult == unchecked((int)0x80070021);
        }

    }
}
