using TiaMcp.Transport;

namespace TiaMcp.Tests;

internal static class WorkerPipeWriterTests
{
    private sealed class FailingFlushStream : MemoryStream
    {
        internal int Error;
        public override void Flush()
        {
            if (Error != 0) throw new IOException("Injected native pipe error", unchecked((int)0x80070000) | Error);
            base.Flush();
        }
    }

    internal static Task ClosedPeer()
    {
        foreach (int error in new[] { 109, 232, 233 })
        {
            using var stream = new FailingFlushStream();
            var writer = new WorkerPipeWriter(stream);
            writer.WriteLine("completed response");
            writer.PeerEofReceived = true;
            stream.Error = error;
            writer.Dispose();
        }
        return Task.CompletedTask;
    }

    internal static Task FailuresRemainVisible()
    {
        foreach (var scenario in new[] { (Eof: false, Error: 109), (Eof: true, Error: 5) })
        {
            using var stream = new FailingFlushStream();
            var writer = new WorkerPipeWriter(stream) { PeerEofReceived = scenario.Eof };
            stream.Error = scenario.Error;
            bool rejected = false;
            try { writer.Dispose(); } catch (IOException) { rejected = true; }
            if (!rejected) throw new Exception("A non-EOF or non-pipe failure was hidden.");
        }
        return Task.CompletedTask;
    }
}
