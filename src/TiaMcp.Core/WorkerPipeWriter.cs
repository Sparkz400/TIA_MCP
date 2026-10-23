using System.IO;
using System.IO.Pipes;
using System.Text;

namespace TiaMcp.Transport
{
    // AutoFlush finishes each response before the next read. Windows can report
    // ERROR_BROKEN_PIPE again when StreamWriter.Dispose flushes after clean EOF.
    // Ignore only that redundant flush; truncated reads and failed writes remain errors.
    public sealed class WorkerPipeWriter : StreamWriter
    {
        private readonly Stream _underlying;
        public bool PeerEofReceived { get; set; }
        public WorkerPipeWriter(Stream stream) : base(stream, new UTF8Encoding(false), 4096, true)
        { _underlying = stream; AutoFlush = true; NewLine = "\n"; }

        protected override void Dispose(bool disposing)
        {
            try { base.Dispose(disposing); }
            catch (IOException error) when (disposing && PeerEofReceived &&
                (IsClosedPipe(error.HResult) || (_underlying is PipeStream pipe && !pipe.IsConnected))) { }
        }

        private static bool IsClosedPipe(int result)
        {
            int code = result & 0xffff;
            return code == 109 || code == 232 || code == 233;
        }
    }
}
