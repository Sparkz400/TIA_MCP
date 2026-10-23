using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;
using TiaMcp.Openness.V21;

internal static class ContentArtifactTests
{
    private const int Limit = 4 * 1024 * 1024;
    internal static void Run(Action<string, Action> test)
    {
        foreach (Encoding encoding in new Encoding[] { new UTF8Encoding(false), new UTF8Encoding(true),
            new UnicodeEncoding(false, true), new UnicodeEncoding(true, true),
            new UTF32Encoding(false, true), new UTF32Encoding(true, true) })
            test("content artifact hashes original bytes before decoding " + encoding.WebName + "/BOM=" + encoding.GetPreamble().Length, () => {
                const string text = "<Document>Grüße 温度 🙂\r\n</Document>";
                byte[] bytes = encoding.GetPreamble().Concat(encoding.GetBytes(text)).ToArray();
                WithFile(bytes, path => Equal(text, ReadFile(path, Hash(bytes))));
            });
        var malformed = new Dictionary<string, byte[]> {
            ["UTF8 continuation"] = new byte[] { 0x80 },
            ["UTF8 overlong"] = new byte[] { 0xc0, 0xaf },
            ["UTF8 truncated"] = new byte[] { 0xe2, 0x82 },
            ["UTF8 surrogate"] = new byte[] { 0xed, 0xa0, 0x80 },
            ["UTF8 beyond Unicode"] = new byte[] { 0xf4, 0x90, 0x80, 0x80 },
            ["UTF8 BOM invalid tail"] = new byte[] { 0xef, 0xbb, 0xbf, 0xff },
            ["UTF16 LE odd tail"] = new byte[] { 0xff, 0xfe, 0x41 },
            ["UTF16 BE odd tail"] = new byte[] { 0xfe, 0xff, 0x41 },
            ["UTF16 LE high surrogate"] = new byte[] { 0xff, 0xfe, 0x00, 0xd8 },
            ["UTF16 BE low surrogate"] = new byte[] { 0xfe, 0xff, 0xdc, 0x00 },
            ["UTF32 LE truncated"] = new byte[] { 0xff, 0xfe, 0, 0, 0x41, 0, 0 },
            ["UTF32 BE truncated"] = new byte[] { 0, 0, 0xfe, 0xff, 0, 0, 0x41 },
            ["UTF32 LE surrogate"] = new byte[] { 0xff, 0xfe, 0, 0, 0, 0xd8, 0, 0 },
            ["UTF32 BE beyond Unicode"] = new byte[] { 0, 0, 0xfe, 0xff, 0, 0x11, 0, 0 }
        };
        foreach (var item in malformed)
            test("content artifact rejects hash-matching malformed " + item.Key, () =>
                WithFile(item.Value, path => Reject(() => ReadFile(path, Hash(item.Value)))));
        test("content artifact cannot collapse distinct damaged comments into identical native XML", () => {
            byte[] prefix = Encoding.UTF8.GetBytes("<Document><!--"), suffix = Encoding.UTF8.GetBytes("--></Document>");
            byte[] first = prefix.Concat(new byte[] { 0x80 }).Concat(suffix).ToArray();
            byte[] second = prefix.Concat(new byte[] { 0x81 }).Concat(suffix).ToArray();
            if (Hash(first) == Hash(second)) throw new Exception("Different original hashes are required.");
            WithFile(first, a => WithFile(second, b => {
                try
                {
                    string left = ReadFile(a, Hash(first)), right = ReadFile(b, Hash(second));
                    EngineeringSources.RequireNativePreservation(EngineeringSources.ReadXml(left), EngineeringSources.ReadXml(right));
                }
                catch (PlcComponentException error) when (error.Code == "ComponentReadbackMismatch") { return; }
                throw new Exception("Distinct malformed raw inputs were accepted as preserved native XML.");
            }));
        });
        test("content artifact preserves a genuine replacement character", () => {
            byte[] bytes = Encoding.UTF8.GetBytes("<Document>\ufffd</Document>");
            WithFile(bytes, path => Equal("<Document>\ufffd</Document>", ReadFile(path, Hash(bytes))));
        });
        test("content artifact does not decode bytes from a second stream pass", () => {
            byte[] bytes = Encoding.UTF8.GetBytes("<Document>original</Document>");
            using var source = new Fragmented(bytes, chunk: 3);
            Equal("<Document>original</Document>", PlcComponentContentContract.ReadArtifactText(source, Hash(bytes)));
            Equal(1, source.EndReads); Equal(bytes.Length, source.Consumed); Equal(false, source.WasDisposed);
        });
        test("content artifact wrong hash refuses unchanged decoded text", () => {
            byte[] bytes = Encoding.UTF8.GetBytes("<Document>original</Document>");
            WithFile(bytes, path => Reject(() => ReadFile(path, new string('0', 64))));
        });
        test("content artifact exact four MiB remains readable", () => {
            byte[] bytes = Enumerable.Repeat((byte)'A', Limit).ToArray();
            WithFile(bytes, path => Equal(new string('A', Limit), ReadFile(path, Hash(bytes))));
        });
        test("content artifact oversized file refuses even with matching hash", () => {
            byte[] bytes = new byte[Limit + 1];
            WithFile(bytes, path => Reject(() => ReadFile(path, Hash(bytes))));
        });
        test("content artifact byte bound holds without trustworthy length or seeking", () => {
            byte[] bytes = new byte[Limit + 8192];
            using var source = new Fragmented(bytes, chunk: 1337);
            Reject(() => PlcComponentContentContract.ReadArtifactText(source, Hash(bytes)));
            Equal(Limit + 1, source.Consumed); Equal(0, source.EndReads);
        });
        test("content artifact hash includes unexpected trailing bytes", () => {
            byte[] original = Encoding.UTF8.GetBytes("<Document/>");
            using var source = new Fragmented(original.Concat(new byte[] { (byte)' ' }).ToArray(), chunk: 2);
            Reject(() => PlcComponentContentContract.ReadArtifactText(source, Hash(original)));
        });
        test("content artifact interrupted source never returns a partial snapshot", () => {
            using var source = new Fragmented(new byte[128], chunk: 7, failAfter: 14);
            try { PlcComponentContentContract.ReadArtifactText(source, Hash(new byte[14])); }
            catch (IOException) { Equal(14, source.Consumed); return; }
            throw new Exception("Partial read was accepted.");
        });
        test("content artifact malformed descriptor refuses before missing file read", () => {
            string missing = Path.Combine(Path.GetTempPath(), "absent-component-" + Guid.NewGuid().ToString("N"));
            Reject(() => ReadFile(missing, new string('a', 64), new string('b', 64)));
        });
    }

    private static string ReadFile(string path, string expected, string descriptorHash = null)
    {
        var method = typeof(PlcComponentContentContract).GetMethod("Artifact", BindingFlags.Static | BindingFlags.NonPublic)!;
        try { return (string)method.Invoke(null, new object[] { new JObject { ["path"] = path, ["sha256"] = descriptorHash ?? expected }, expected })!; }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }
    private static void WithFile(byte[] bytes, Action<string> action)
    {
        string path = Path.Combine(Path.GetTempPath(), "tia-component-bytes-" + Guid.NewGuid().ToString("N") + ".xml");
        try { File.WriteAllBytes(path, bytes); action(path); }
        finally { File.Delete(path); }
    }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static void Equal<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}"); }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (PlcComponentException error) when (error.Code == "ComponentReadbackMismatch") { return; }
        throw new Exception("Expected component content refusal.");
    }
    private sealed class Fragmented(byte[] bytes, int chunk, int failAfter = int.MaxValue) : Stream
    {
        internal int Consumed, EndReads;
        internal bool WasDisposed;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (Consumed >= failAfter) throw new IOException("Synthetic interruption.");
            if (EndReads > 0) throw new Exception("The source was read again after its original EOF.");
            int take = Math.Min(count, Math.Min(chunk, bytes.Length - Consumed));
            if (take == 0) { EndReads++; return 0; }
            Array.Copy(bytes, Consumed, buffer, offset, take); Consumed += take; return take;
        }
        protected override void Dispose(bool disposing) { WasDisposed = true; base.Dispose(disposing); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
