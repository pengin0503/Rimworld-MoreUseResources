using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace MoreUseResources.Benchmarks.Data;

/// <summary>What the generator wrote, so tests and the report can check the mod's findings against it.</summary>
/// <summary>
/// What the generator planted. <see cref="MalformedXmlAfterDefName"/> counts the malformed files whose
/// syntax error comes after the first Def's defName (part of <see cref="MalformedXml"/>); each of them
/// holds one Def with a defName that a scanner stopping early would count.
/// </summary>
public sealed record GeneratedTruth(int Mods, int XmlFiles, int MalformedXml, int Definitions, int CollidingDefinitions,
    int ValidPng, int MalformedPng, int Dds, int Sounds, int DuplicateFiles, long DuplicateBytes, long PackBytes, long TotalBytes,
    int MalformedXmlAfterDefName = 0);

/// <summary>
/// Writes a deterministic stand-in for a RimWorld install with mods, used when no real assets are
/// given (GitHub Actions, first try on a VM). It has the folder layout the mod discovers (Defs,
/// Patches, Languages, Textures, Sounds, loadFolders.xml, Assets.zip) and the content features the
/// mod inspects: Def names with cross-mod collisions, malformed XML, PNGs with valid and broken CRCs,
/// DDS headers and byte-identical duplicates. PNG data uses stored deflate blocks so the bytes do not
/// depend on the zlib build. It is input data for the real code, not a benchmark of its own.
/// </summary>
public static class DatasetGenerator
{
    public const int FormatVersion = 2;
    private const string MarkerName = ".mur-bench-generated.json";

    public static string Describe(int targetMiB, int seed) => "generated v" + FormatVersion + " target=" + targetMiB + "MiB seed=" + seed;

    /// <summary>Generates into <paramref name="root"/> unless a marker with the same parameters is already there.</summary>
    public static GeneratedTruth EnsureGenerated(string root, int targetMiB, int seed)
    {
        string marker = Path.Combine(root, MarkerName);
        if (File.Exists(marker))
        {
            try
            {
                var existing = JsonSerializer.Deserialize<Marker>(File.ReadAllText(marker));
                if (existing != null && existing.Version == FormatVersion && existing.TargetMiB == targetMiB && existing.Seed == seed)
                    return existing.Truth;
            }
            catch (JsonException) { }
        }
        if (Directory.Exists(root))
        {
            // Only a directory that this generator owns may be replaced.
            if (Directory.EnumerateFileSystemEntries(root).Any() && !File.Exists(marker))
                throw new IOException("Refusing to generate into a non-empty directory without a generator marker: " + root);
            Directory.Delete(root, recursive: true);
        }
        Directory.CreateDirectory(root);
        GeneratedTruth truth = Generate(root, targetMiB, seed);
        File.WriteAllText(marker, JsonSerializer.Serialize(new Marker(FormatVersion, targetMiB, seed, truth)));
        return truth;
    }

    private sealed record Marker(int Version, int TargetMiB, int Seed, GeneratedTruth Truth);

    /// <summary>The truth and description of a directory this generator wrote, or null.</summary>
    public static (GeneratedTruth Truth, string Description)? ReadMarker(string root)
    {
        string marker = Path.Combine(root, MarkerName);
        if (!File.Exists(marker)) return null;
        try
        {
            var m = JsonSerializer.Deserialize<Marker>(File.ReadAllText(marker));
            return m != null && m.Version == FormatVersion ? (m.Truth, Describe(m.TargetMiB, m.Seed)) : null;
        }
        catch (JsonException) { return null; }
    }

    public static GeneratedTruth Generate(string root, int targetMiB, int seed)
    {
        var random = new Random(seed);
        long target = Math.Max(8, targetMiB) * 1048576L;
        int mods = Math.Clamp(targetMiB / 6, 6, 60);
        var c = new Counter();

        string game = Path.Combine(root, "Game");
        string core = Path.Combine(game, "Data", "Core");
        WriteAbout(core, "ludeon.rimworld", "Core");
        // Official content: textures live in Assets.zip, only XML is loose (as in 1.7).
        long packTarget = Math.Max(4L << 20, target / 5);
        WritePack(Path.Combine(game, "Assets.zip"), packTarget, random, c);
        var coreDefs = new List<string>();
        for (int i = 0; i < 12; i++) WriteDefs(Path.Combine(core, "Defs", "ThingDefs_Misc", "Core_" + i + ".xml"), "Core", i, random, c, coreDefs, null);

        long textureBudget = target * 55 / 100 / mods;
        long soundBudget = target * 8 / 100 / mods;
        var sharedTextures = new List<(string Rel, byte[] Bytes)>();
        for (int m = 0; m < mods; m++)
        {
            string name = "GenMod" + m.ToString("D2", CultureInfo.InvariantCulture);
            string mod = Path.Combine(root, "Mods", name);
            WriteAbout(mod, "bench.generated." + name.ToLowerInvariant(), name);
            bool versioned = m % 3 == 0;
            string content = versioned ? Path.Combine(mod, Dataset.GameVersionFolder) : mod;
            if (versioned)
                File.WriteAllText(Path.Combine(mod, "loadFolders.xml"),
                    "<loadFolders>\n  <v1.7>\n    <li>/</li>\n    <li>1.7</li>\n  </v1.7>\n</loadFolders>\n");
            int defFiles = 3 + random.Next(6);
            for (int f = 0; f < defFiles; f++)
            {
                string path = Path.Combine(content, "Defs", "ThingDefs", name + "_" + f + ".xml");
                // A few mods redefine Core names; this is what the mod reports as a definition collision.
                WriteDefs(path, name, f, random, c, null, m % 4 == 1 && f == 0 ? coreDefs : null);
            }
            if (m % 7 == 3)
            {
                // Cut off after the first Def's defName, as a truncated download would be.
                WriteText(Path.Combine(content, "Defs", "Broken_" + m + ".xml"), "<Defs>\n  <ThingDef>\n    <defName>Broken" + m + "</defName>\n  <ThingDef>\n", c);
                c.MalformedXml++;
                c.MalformedXmlAfterDefName++;
                c.XmlFiles++;
            }
            if (m % 7 == 5)
            {
                // Cut off before any defName.
                WriteText(Path.Combine(content, "Defs", "Truncated_" + m + ".xml"), "<Defs>\n  <ThingDef>\n    <label>cut", c);
                c.MalformedXml++;
                c.XmlFiles++;
            }
            WriteText(Path.Combine(content, "Patches", name + ".xml"),
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<Patch>\n  <Operation Class=\"PatchOperationAdd\">\n    <xpath>/Defs/ThingDef[defName=\"Core_Item_0_0\"]/statBases</xpath>\n    <value><Mass>" + m + "</Mass></value>\n  </Operation>\n</Patch>\n", c);
            c.XmlFiles++;
            var keyed = new StringBuilder("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<LanguageData>\n");
            for (int k = 0; k < 40; k++) keyed.Append("  <").Append(name).Append("_Key").Append(k).Append(">Text ").Append(k).Append(" of ").Append(name).Append("</").Append(name).Append("_Key").Append(k).Append(">\n");
            keyed.Append("</LanguageData>\n");
            WriteText(Path.Combine(mod, "Languages", "English", "Keyed", name + ".xml"), keyed.ToString(), c);
            c.XmlFiles++;

            long written = 0;
            int t = 0;
            while (written < textureBudget)
            {
                int edge = 16 << random.Next(6); // 16..512
                int width = edge, height = random.Next(4) == 0 ? edge / 2 : edge;
                byte[] png = Png(width, height, random);
                bool broken = random.Next(100) == 0;
                if (broken)
                {
                    png[png.Length - 20] ^= 0x5A; // inside the IDAT data: the stored CRC no longer matches
                    c.MalformedPng++;
                }
                else c.ValidPng++;
                string rel = Path.Combine("Things", "Item", name + "_" + t + ".png");
                WriteBytes(Path.Combine(mod, "Textures", rel), png, c);
                if (!broken && random.Next(25) == 0) sharedTextures.Add((rel, png));
                written += png.Length;
                t++;
            }
            if (m % 5 == 2)
            {
                byte[] dds = Dds(256, 256, random);
                WriteBytes(Path.Combine(mod, "Textures", "UI", name + "_icon.dds"), dds, c);
                c.Dds++;
            }
            long sound = 0;
            int s = 0;
            while (sound < soundBudget)
            {
                byte[] wav = Wav(random.Next(48_000, 400_000), random);
                WriteBytes(Path.Combine(mod, "Sounds", name + "_" + s++ + ".wav"), wav, c);
                c.Sounds++;
                sound += wav.Length;
            }
            // Byte-identical copies of other mods' textures, as when several mods ship the same library.
            if (sharedTextures.Count > 0 && m % 2 == 1)
            {
                var (rel, bytes) = sharedTextures[random.Next(sharedTextures.Count)];
                WriteBytes(Path.Combine(mod, "Textures", "Shared", Path.GetFileName(rel)), bytes, c);
                c.ValidPng++;
                c.DuplicateFiles++;
                c.DuplicateBytes += bytes.Length;
            }
        }
        return new GeneratedTruth(mods + 1, c.XmlFiles, c.MalformedXml, c.Definitions, c.Collisions, c.ValidPng, c.MalformedPng,
            c.Dds, c.Sounds, c.DuplicateFiles, c.DuplicateBytes, c.PackBytes, c.TotalBytes, c.MalformedXmlAfterDefName);
    }

    private sealed class Counter
    {
        public int XmlFiles, MalformedXml, MalformedXmlAfterDefName, Definitions, Collisions, ValidPng, MalformedPng, Dds, Sounds, DuplicateFiles;
        public long DuplicateBytes, PackBytes, TotalBytes;
    }

    private static void WriteAbout(string root, string packageId, string name)
    {
        Directory.CreateDirectory(Path.Combine(root, "About"));
        File.WriteAllText(Path.Combine(root, "About", "About.xml"),
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<ModMetaData>\n  <name>" + name + "</name>\n  <packageId>" + packageId
            + "</packageId>\n  <supportedVersions><li>1.7</li></supportedVersions>\n</ModMetaData>\n");
    }

    private static void WriteDefs(string path, string mod, int file, Random random, Counter c, List<string>? record, List<string>? collideWith)
    {
        var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<Defs>\n");
        sb.Append("  <ThingDef Name=\"").Append(mod).Append("_Base").Append(file).Append("\" Abstract=\"True\">\n    <category>Item</category>\n  </ThingDef>\n");
        c.Definitions++; // abstract: counted as an element, not as a key
        int defs = 5 + random.Next(30);
        for (int d = 0; d < defs; d++)
        {
            string defName = collideWith != null && d < 3 && collideWith.Count > d ? collideWith[d] : mod + "_Item_" + file + "_" + d;
            if (collideWith != null && d < 3 && collideWith.Count > d) c.Collisions++;
            record?.Add(defName);
            sb.Append("  <ThingDef ParentName=\"").Append(mod).Append("_Base").Append(file).Append("\">\n")
              .Append("    <defName>").Append(defName).Append("</defName>\n")
              .Append("    <label>item ").Append(d).Append("</label>\n")
              .Append("    <description>Generated description ").Append(random.Next()).Append(" for benchmark input.</description>\n")
              .Append("    <statBases>\n      <MarketValue>").Append(random.Next(1, 500)).Append("</MarketValue>\n      <Mass>")
              .Append((random.Next(1, 100) / 10.0).ToString(CultureInfo.InvariantCulture)).Append("</Mass>\n    </statBases>\n")
              .Append("    <graphicData>\n      <texPath>Things/Item/").Append(mod).Append('_').Append(d).Append("</texPath>\n      <graphicClass>Graphic_Single</graphicClass>\n    </graphicData>\n")
              .Append("  </ThingDef>\n");
            c.Definitions++;
        }
        sb.Append("</Defs>\n");
        WriteText(path, sb.ToString(), c);
        c.XmlFiles++;
    }

    private static void WriteText(string path, string text, Counter c) => WriteBytes(path, Encoding.UTF8.GetBytes(text), c);

    private static void WriteBytes(string path, byte[] bytes, Counter c)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        c.TotalBytes += bytes.Length;
    }

    private static void WritePack(string path, long bytes, Random random, Counter c)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var fixedTime = new DateTimeOffset(2026, 11, 18, 0, 0, 0, TimeSpan.Zero);
        using (var stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            long written = 0;
            int i = 0;
            byte[] buffer = new byte[1 << 20];
            while (written < bytes)
            {
                ZipArchiveEntry entry = zip.CreateEntry("Textures/Packed/tex_" + i++ + ".bin", CompressionLevel.NoCompression);
                entry.LastWriteTime = fixedTime;
                int size = (int)Math.Min(buffer.Length, Math.Max(65536, bytes - written));
                random.NextBytes(buffer.AsSpan(0, size));
                using Stream s = entry.Open();
                s.Write(buffer, 0, size);
                written += size;
            }
        }
        long length = new FileInfo(path).Length;
        c.PackBytes += length;
        c.TotalBytes += length;
    }

    /// <summary>An RGBA PNG whose IDAT is a zlib stream of stored blocks (valid, deterministic, uncompressed).</summary>
    public static byte[] Png(int width, int height, Random random)
    {
        int stride = width * 4 + 1;
        byte[] raw = new byte[stride * height];
        random.NextBytes(raw);
        for (int y = 0; y < height; y++) raw[y * stride] = 0; // filter type None
        using var ms = new MemoryStream();
        ms.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        byte[] ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(0), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)height);
        ihdr[8] = 8; ihdr[9] = 6; // 8-bit RGBA
        Chunk(ms, "IHDR", ihdr);
        Chunk(ms, "IDAT", ZlibStored(raw));
        Chunk(ms, "IEND", Array.Empty<byte>());
        return ms.ToArray();
    }

    public static byte[] Dds(int width, int height, Random random)
    {
        byte[] data = new byte[128 + width * height / 2];
        random.NextBytes(data);
        "DDS "u8.CopyTo(data);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 124);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(12), height);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(16), width);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(76), 32);
        return data;
    }

    private static byte[] Wav(int dataBytes, Random random)
    {
        byte[] wav = new byte[44 + dataBytes];
        random.NextBytes(wav);
        "RIFF"u8.CopyTo(wav);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(4), 36 + dataBytes);
        "WAVEfmt "u8.CopyTo(wav.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(22), 2);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(24), 44100);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(28), 44100 * 4);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(32), 4);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(34), 16);
        "data"u8.CopyTo(wav.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(40), dataBytes);
        return wav;
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(len, (uint)data.Length);
        s.Write(len);
        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(data);
        uint crc = Crc32.Update(Crc32.Update(0xffffffffu, typeBytes), data) ^ 0xffffffffu;
        BinaryPrimitives.WriteUInt32BigEndian(len, crc);
        s.Write(len);
    }

    private static byte[] ZlibStored(byte[] raw)
    {
        using var ms = new MemoryStream(raw.Length + raw.Length / 65535 * 5 + 16);
        ms.WriteByte(0x78);
        ms.WriteByte(0x01);
        int offset = 0;
        do
        {
            int n = Math.Min(65535, raw.Length - offset);
            bool last = offset + n >= raw.Length;
            ms.WriteByte(last ? (byte)1 : (byte)0);
            ms.WriteByte((byte)(n & 0xff));
            ms.WriteByte((byte)(n >> 8));
            ms.WriteByte((byte)(~n & 0xff));
            ms.WriteByte((byte)((~n >> 8) & 0xff));
            ms.Write(raw, offset, n);
            offset += n;
        } while (offset < raw.Length);
        uint a = 1, b = 0;
        foreach (byte x in raw)
        {
            a = (a + x) % 65521;
            b = (b + a) % 65521;
        }
        Span<byte> adler = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(adler, (b << 16) | a);
        ms.Write(adler);
        return ms.ToArray();
    }

    private static class Crc32
    {
        private static readonly uint[] Table = Create();

        public static uint Update(uint crc, ReadOnlySpan<byte> bytes)
        {
            foreach (byte value in bytes) crc = Table[(crc ^ value) & 255] ^ (crc >> 8);
            return crc;
        }

        private static uint[] Create()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint v = n;
                for (int k = 0; k < 8; k++) v = (v & 1) == 0 ? v >> 1 : 0xedb88320u ^ (v >> 1);
                table[n] = v;
            }
            return table;
        }
    }
}
