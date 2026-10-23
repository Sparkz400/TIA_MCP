using System.Buffers;
using System.Text.Json;

namespace TiaMcp.Core;

/// <summary>
/// One process-wide parsed inspection manifest, admitted only after comparing every
/// current file byte. Never trusts timestamps, sizes alone, or cached package state.
/// The retained model is private to inspection; mutable page fields must be copied.
/// </summary>
internal static class PackageInspectionManifestCache
{
    private const int MaximumCachedBytes = 8 << 20;
    private static readonly object Gate = new();
    private static Entry? _entry;
    private sealed record Entry(string Path, byte[] Bytes, PackageManifest Manifest);

    internal static PackageManifest Load(string path)
    {
        PackageStorage.EnsureNoLinks(path);
        try
        {
            lock (Gate)
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length > 64L << 20) throw new BridgeException("PackageInvalid", "Intake manifest exceeds its read limit.");
                if (stream.Length > MaximumCachedBytes)
                {
                    _entry = null;
                    return JsonSerializer.Deserialize<PackageManifest>(stream, BridgeProtocol.Json)
                        ?? throw new BridgeException("PackageInvalid", "Empty intake manifest.");
                }
                if (_entry is { } cached && string.Equals(cached.Path, path, StringComparison.Ordinal)
                    && stream.Length == cached.Bytes.Length && SameBytes(stream, cached.Bytes))
                    return cached.Manifest;

                _entry = null;
                stream.Position = 0;
                var bytes = new byte[checked((int)stream.Length)];
                stream.ReadExactly(bytes);
                if (stream.ReadByte() != -1) throw new BridgeException("PackageChanged", "Intake manifest changed while being read.");
                // Stream-based JSON deserialization accepts a UTF-8 BOM; retain that behavior.
                ReadOnlySpan<byte> json = bytes;
                if (json.StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) json = json[3..];
                var manifest = JsonSerializer.Deserialize<PackageManifest>(json, BridgeProtocol.Json)
                    ?? throw new BridgeException("PackageInvalid", "Empty intake manifest.");
                _entry = new Entry(path, bytes, manifest);
                return manifest;
            }
        }
        catch (JsonException error) { throw new BridgeException("PackageInvalid", "Intake manifest is corrupt.", error); }
        catch (FileNotFoundException error) { throw new BridgeException("IntakeNotFound", "The requested intake record does not exist.", error); }
        catch (DirectoryNotFoundException error) { throw new BridgeException("IntakeNotFound", "The requested intake record does not exist.", error); }
        catch (EndOfStreamException error) { throw new BridgeException("PackageChanged", "Intake manifest changed while being read.", error); }
    }

    private static bool SameBytes(Stream stream, byte[] expected)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            int offset = 0;
            while (offset < expected.Length)
            {
                int read = stream.Read(buffer, 0, Math.Min(buffer.Length, expected.Length - offset));
                if (read == 0 || !buffer.AsSpan(0, read).SequenceEqual(expected.AsSpan(offset, read))) return false;
                offset += read;
            }
            return stream.ReadByte() == -1;
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }
}
