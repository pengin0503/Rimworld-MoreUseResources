using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Threading;

namespace MoreUseResources.Core
{
    public enum TextureAssetKind { Unknown, Png, Dds }
    public enum TextureAssetState { Valid, Malformed, Skipped }

    /// <summary>
    /// Read-only analysis of a mod's on-disk artwork. Rgba8EquivalentBytes is a
    /// base-level, uncompressed RGBA8 comparison figure, never measured GPU allocation.
    /// </summary>
    public readonly struct TextureAssetReport
    {
        public readonly TextureAssetKind Kind;
        public readonly TextureAssetState State;
        public readonly int Width;
        public readonly int Height;
        public readonly long Rgba8EquivalentBytes;
        public readonly long ValidatedBytes;

        internal TextureAssetReport(TextureAssetKind kind, TextureAssetState state,
            int width = 0, int height = 0, long validatedBytes = 0)
        {
            Kind = kind;
            State = state;
            Width = state == TextureAssetState.Valid ? width : 0;
            Height = state == TextureAssetState.Valid ? height : 0;
            Rgba8EquivalentBytes = state == TextureAssetState.Valid ? (long)width * height * 4 : 0;
            ValidatedBytes = state == TextureAssetState.Valid ? validatedBytes : 0;
        }
    }

    /// <summary>
    /// Streams PNG chunks and checks their CRC32. For DDS only the fixed 128-byte header
    /// is validated; compressed texture data is not decoded or verified.
    /// Input is untrusted mod content: limits and cancellation apply before allocations.
    /// </summary>
    public static class TextureAssetInspector
    {
        public const long MaxImageBytes = 128L * 1024 * 1024;
        private const int MaxDimension = 16384;
        private const int MaxPngChunks = 65536;
        private const int IoBufferSize = 64 * 1024;
        private static readonly byte[] PngSignature = { 137, 80, 78, 71, 13, 10, 26, 10 };
        private static readonly uint[] CrcTable = CreateCrcTable();

        public static TextureAssetReport Inspect(string path, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            string extension = Path.GetExtension(path);
            TextureAssetKind kind = extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
                ? TextureAssetKind.Png
                : extension.Equals(".dds", StringComparison.OrdinalIgnoreCase) ? TextureAssetKind.Dds : TextureAssetKind.Unknown;
            if (kind == TextureAssetKind.Unknown)
                return new TextureAssetReport(kind, TextureAssetState.Skipped);

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                IoBufferSize, FileOptions.SequentialScan);
            if (stream.Length > MaxImageBytes)
                return new TextureAssetReport(kind, TextureAssetState.Skipped);
            return kind == TextureAssetKind.Png ? InspectPng(stream, token) : InspectDds(stream, token);
        }

        private static TextureAssetReport InspectDds(FileStream stream, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            const TextureAssetKind kind = TextureAssetKind.Dds;
            if (stream.Length < 128) return new TextureAssetReport(kind, TextureAssetState.Malformed);
            Span<byte> header = stackalloc byte[128];
            stream.ReadExactly(header);
            if (header[0] != 'D' || header[1] != 'D' || header[2] != 'S' || header[3] != ' '
                || BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(4, 4)) != 124
                || BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(76, 4)) != 32)
                return new TextureAssetReport(kind, TextureAssetState.Malformed);
            int height = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(12, 4));
            int width = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(16, 4));
            if (!ValidGeometry(width, height))
                return new TextureAssetReport(kind, TextureAssetState.Malformed);
            return new TextureAssetReport(kind, TextureAssetState.Valid, width, height, 128);
        }

        private static TextureAssetReport InspectPng(FileStream stream, CancellationToken token)
        {
            const TextureAssetKind kind = TextureAssetKind.Png;
            if (stream.Length < 57) return new TextureAssetReport(kind, TextureAssetState.Malformed);
            Span<byte> signature = stackalloc byte[8];
            stream.ReadExactly(signature);
            if (!signature.SequenceEqual(PngSignature))
                return new TextureAssetReport(kind, TextureAssetState.Malformed);
            int width = 0, height = 0;
            bool sawHeader = false, sawImageData = false;
            byte[] buffer = ArrayPool<byte>.Shared.Rent(IoBufferSize);
            try
            {
                for (int chunkCount = 0; chunkCount < MaxPngChunks; chunkCount++)
                {
                    token.ThrowIfCancellationRequested();
                    if (stream.Length - stream.Position < 12)
                        return new TextureAssetReport(kind, TextureAssetState.Malformed);
                    Span<byte> chunk = stackalloc byte[8];
                    stream.ReadExactly(chunk);
                    uint length = BinaryPrimitives.ReadUInt32BigEndian(chunk.Slice(0, 4));
                    // Ignore arbitrary chunk metadata, but never let an untrusted length
                    // force a large allocation or seek outside this file.
                    if (length > stream.Length - stream.Position - 4)
                        return new TextureAssetReport(kind, TextureAssetState.Malformed);
                    bool isHeader = chunk.Slice(4, 4).SequenceEqual("IHDR"u8);
                    bool isData = chunk.Slice(4, 4).SequenceEqual("IDAT"u8);
                    bool isEnd = chunk.Slice(4, 4).SequenceEqual("IEND"u8);
                    if (chunkCount == 0 && (!isHeader || length != 13))
                        return new TextureAssetReport(kind, TextureAssetState.Malformed);
                    if ((isHeader && (sawHeader || length != 13)) || (isEnd && length != 0))
                        return new TextureAssetReport(kind, TextureAssetState.Malformed);

                    uint crc = 0xffffffff;
                    crc = UpdateCrc(crc, chunk.Slice(4, 4));
                    uint remaining = length;
                    Span<byte> ihdr = stackalloc byte[13];
                    bool firstRead = true;
                    while (remaining > 0)
                    {
                        token.ThrowIfCancellationRequested();
                        int n = (int)Math.Min(remaining, (uint)buffer.Length);
                        stream.ReadExactly(buffer.AsSpan(0, n));
                        if (isHeader && firstRead)
                        {
                            buffer.AsSpan(0, 13).CopyTo(ihdr);
                            firstRead = false;
                        }
                        crc = UpdateCrc(crc, buffer.AsSpan(0, n));
                        remaining -= (uint)n;
                    }
                    Span<byte> stored = stackalloc byte[4];
                    stream.ReadExactly(stored);
                    if (BinaryPrimitives.ReadUInt32BigEndian(stored) != ~crc)
                        return new TextureAssetReport(kind, TextureAssetState.Malformed);

                    if (isHeader)
                    {
                        sawHeader = true;
                        width = (int)BinaryPrimitives.ReadUInt32BigEndian(ihdr.Slice(0, 4));
                        height = (int)BinaryPrimitives.ReadUInt32BigEndian(ihdr.Slice(4, 4));
                        if (!ValidGeometry(width, height) || ihdr[10] != 0 || ihdr[11] != 0 || ihdr[12] > 1
                            || ihdr[8] is not (1 or 2 or 4 or 8 or 16) || ihdr[9] is not (0 or 2 or 3 or 4 or 6))
                            return new TextureAssetReport(kind, TextureAssetState.Malformed);
                    }
                    else if (isData)
                    {
                        if (!sawHeader) return new TextureAssetReport(kind, TextureAssetState.Malformed);
                        sawImageData = true;
                    }
                    else if (isEnd)
                    {
                        if (!sawHeader || !sawImageData || stream.Position != stream.Length)
                            return new TextureAssetReport(kind, TextureAssetState.Malformed);
                        return new TextureAssetReport(kind, TextureAssetState.Valid, width, height, stream.Length);
                    }
                }
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
            return new TextureAssetReport(kind, TextureAssetState.Malformed);
        }

        private static bool ValidGeometry(int width, int height) =>
            width > 0 && height > 0 && width <= MaxDimension && height <= MaxDimension;

        private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> bytes)
        {
            foreach (byte value in bytes)
                crc = CrcTable[(int)((crc ^ value) & 255)] ^ (crc >> 8);
            return crc;
        }

        private static uint[] CreateCrcTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint v = n;
                for (int bit = 0; bit < 8; bit++)
                    v = (v & 1) == 0 ? v >> 1 : 0xedb88320u ^ (v >> 1);
                table[n] = v;
            }
            return table;
        }
    }
}
