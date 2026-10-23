using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TiaMcp.TestSuite.V21
{
    /// <summary>Retains a bounded native tree before transport; partial enumeration never becomes complete.</summary>
    public static class StyleRawCapture
    {
        public static byte[] Retain(string path, JObject result, Action<JArray> collect)
            => RetainBounded(path, result, collect, 64 * 1024 * 1024);

        internal static byte[] RetainBounded(string path, JObject result, Action<JArray> collect, int maximumBytes)
        {
            if (!Path.IsPathRooted(path)) throw new ArgumentException("Fresh absolute raw capture path required.");
            if (maximumBytes < 64 || maximumBytes > 64 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
            if (File.Exists(path + ".failure.json")) throw new IOException("An earlier raw capture failure receipt exists.");
            // Reserve the original artifact before enumeration. A collision cannot cause another
            // collection attempt, and an encoding/size failure leaves the exact serialized prefix.
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                var messages = new JArray(); result["messages"] = messages; result["collectionComplete"] = false;
                Exception collectionError = null, captureError = null;
                try { collect(messages); result["collectionComplete"] = true; }
                catch (Exception error) { collectionError = error; }
                try
                {
                    using (var bounded = new BoundedStream(file, maximumBytes))
                    using (var text = new StreamWriter(bounded, new UTF8Encoding(false, true), 4096, true))
                    using (var json = new JsonTextWriter(text) { Formatting = Formatting.None, CloseOutput = false, AutoCompleteOnClose = false })
                    { new JArray(result).WriteTo(json); json.Flush(); text.Flush(); }
                }
                catch (Exception error) { captureError = error; }
                file.Flush(true);
                if (collectionError != null || captureError != null)
                {
                    var error = collectionError ?? captureError;
                    Flush(path + ".failure.json", Encoding.UTF8.GetBytes(new JObject {
                        ["type"] = error.GetType().FullName, ["message"] = error.Message,
                        ["collectionComplete"] = false, ["captureComplete"] = false,
                        ["enumerationReturned"] = collectionError == null,
                        ["captureFailureType"] = captureError?.GetType().FullName,
                        ["captureFailure"] = captureError?.Message,
                        ["retainedBytes"] = file.Length, ["maximumBytes"] = maximumBytes,
                        ["automaticReplay"] = false }.ToString(Formatting.None)));
                    if (collectionError != null && captureError != null) throw new AggregateException(collectionError, captureError);
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
                }
            }
            return File.ReadAllBytes(path);
        }

        private sealed class BoundedStream : Stream
        {
            private readonly Stream output;
            private readonly long maximum;
            internal BoundedStream(Stream output, long maximum) { this.output = output; this.maximum = maximum; }
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => output.Length;
            public override long Position { get => output.Position; set => throw new NotSupportedException(); }
            public override void Write(byte[] buffer, int offset, int count)
            {
                int allowed = (int)Math.Min(count, Math.Max(0, maximum - output.Position));
                if (allowed != 0) output.Write(buffer, offset, allowed);
                if (allowed != count) throw new InvalidOperationException("TestResultLimit: raw capture reached its byte bound; the exact prefix is retained.");
            }
            public override void Flush() => output.Flush();
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }
        private static void Flush(string path, byte[] bytes)
        {
            if (!Path.IsPathRooted(path)) throw new ArgumentException("Fresh absolute raw capture path required.");
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
        }
    }
}
