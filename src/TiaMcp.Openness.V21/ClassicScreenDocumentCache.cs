using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace TiaMcp.Model
{
    // Parsed immutable artifacts only, never native observations. The caller must
    // revalidate the retained file/path/hash on every read before consulting this cache.
    internal sealed class ClassicScreenDocumentCache
    {
        private readonly Dictionary<string, XDocument> documents = new Dictionary<string, XDocument>(StringComparer.Ordinal);
        private readonly Queue<string> order = new Queue<string>();
        private readonly Dictionary<string, int> sizes = new Dictionary<string, int>(StringComparer.Ordinal);
        private long characters;
        internal XDocument Read(string validatedHash, byte[] bytes)
        {
            if (string.IsNullOrEmpty(validatedHash)) throw new ArgumentException("A validated artifact hash is required.");
            if (bytes == null || bytes.Length > 64 * 1024 * 1024) throw new ArgumentException("Screen artifact exceeds its read bound.");
            using (var sha = SHA256.Create())
                if (!string.Equals(validatedHash, BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", ""), StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Screen artifact changed during its validated read.");
            if (!documents.TryGetValue(validatedHash, out var document))
            {
                string validatedXml = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');
                document = HmiPreservationRules.ReadXml(validatedXml);
                if (validatedXml.Length <= 16 * 1024 * 1024)
                {
                    // Evict the oldest documents until this one fits; a full cache keeps what a screen walk is still reusing.
                    while (order.Count > 0 && (documents.Count >= 128 || characters + validatedXml.Length > 16 * 1024 * 1024))
                    {
                        string oldest = order.Dequeue();
                        documents.Remove(oldest); characters -= sizes[oldest]; sizes.Remove(oldest);
                    }
                    documents.Add(validatedHash, document); order.Enqueue(validatedHash); sizes.Add(validatedHash, validatedXml.Length); characters += validatedXml.Length;
                }
            }
            return new XDocument(document); // Callers cannot poison later compositions.
        }
    }
}
