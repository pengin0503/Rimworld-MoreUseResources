using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Xml;

namespace MoreUseResources.Core;

public sealed record PatchedXml(XmlDocument Document, int[] Origins);

public static class PatchedXmlStore
{
    private const int MaxBytes = 64 * 1024 * 1024;
    private const int MaxNodes = 250_000;
    private const int Format = 2;
    private const int HashSize = 32;

    public static void Save(string path, string key, XmlDocument document, int[] origins)
    {
        if (origins.Length > MaxNodes || document.ChildNodes.Count != 1
            || document.DocumentElement is not { Name: "Defs" } root || root.ChildNodes.Count != origins.Length)
            throw new InvalidDataException("Invalid cached XML provenance.");

        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
                writer.Write(Format);
                writer.Write(key);
                writer.Write(document.PreserveWhitespace);
                writer.Write(origins.Length);
                foreach (int origin in origins) writer.Write(origin);
                XmlDomCodec.Write(writer, root);
                writer.Flush();
                if (output.Length > MaxBytes - HashSize)
                    throw new InvalidDataException("Patched XML exceeds the cache limit.");
                output.Position = 0;
                writer.Write(SHA256.HashData(output));
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    public static PatchedXml? Load(string path, string key, int sourceCount)
    {
        try
        {
            using var input = File.OpenRead(path);
            long length = input.Length;
            if (length <= HashSize || length > MaxBytes) return null;
            byte[] payload = new byte[(int)length - HashSize];
            input.ReadExactly(payload);
            Span<byte> storedHash = stackalloc byte[HashSize];
            input.ReadExactly(storedHash);
            ModFileReads.Add(length);
            if (!storedHash.SequenceEqual(SHA256.HashData(payload))) return null;

            using var stream = new MemoryStream(payload, writable: false);
            using var reader = new BinaryReader(stream, Encoding.UTF8);
            if (reader.ReadInt32() != Format || reader.ReadString() != key) return null;
            bool preserveWhitespace = reader.ReadBoolean();
            int count = reader.ReadInt32();
            if (count < 0 || count > MaxNodes || count > (stream.Length - stream.Position) / sizeof(int)) return null;
            var origins = new int[count];
            for (int i = 0; i < count; i++)
            {
                int origin = reader.ReadInt32();
                if (origin < -1 || origin >= sourceCount) return null;
                origins[i] = origin;
            }
            var document = new XmlDocument { PreserveWhitespace = preserveWhitespace, XmlResolver = null };
            if (XmlDomCodec.Read(reader, document) is not XmlElement { Name: "Defs" } root
                || root.ChildNodes.Count != count || stream.Position != stream.Length) return null;
            document.AppendChild(root);
            return new PatchedXml(document, origins);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException
            or XmlException or ArgumentException or InvalidOperationException or InvalidDataException)
        {
            return null;
        }
    }
}
