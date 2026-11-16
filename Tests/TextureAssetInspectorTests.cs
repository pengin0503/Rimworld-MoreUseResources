using System;
using System.IO;
using System.Threading;
using MoreUseResources.Core;
using Xunit;

namespace MoreUseResources.Tests
{
    public sealed class TextureAssetInspectorTests
    {
        private const string ValidPngBase64 =
            "iVBORw0KGgoAAAANSUhEUgAAAAIAAAADCAYAAAC56t6BAAAAC0lEQVR4nGNgwAkAABsAAco8Sg0AAAAASUVORK5CYII=";

        [Fact]
        public void Png_checks_real_chunks_and_reports_uncompressed_RGBA8_equivalent()
        {
            using var file = new TempAsset(".png", Convert.FromBase64String(ValidPngBase64));
            TextureAssetReport report = TextureAssetInspector.Inspect(file.Path, CancellationToken.None);
            Assert.Equal(TextureAssetState.Valid, report.State);
            Assert.Equal(TextureAssetKind.Png, report.Kind);
            Assert.Equal(2, report.Width);
            Assert.Equal(3, report.Height);
            Assert.Equal(24, report.Rgba8EquivalentBytes);
            Assert.Equal(new FileInfo(file.Path).Length, report.ValidatedBytes);
        }

        [Fact]
        public void Png_with_bad_CRC_or_missing_end_is_malformed()
        {
            byte[] broken = Convert.FromBase64String(ValidPngBase64);
            broken[20] ^= 1; // corrupt an IHDR byte but not its CRC
            using var crcFile = new TempAsset(".png", broken);
            Assert.Equal(TextureAssetState.Malformed, TextureAssetInspector.Inspect(crcFile.Path, CancellationToken.None).State);
            byte[] valid = Convert.FromBase64String(ValidPngBase64);
            Array.Resize(ref valid, valid.Length - 12); // no IEND
            using var truncated = new TempAsset(".png", valid);
            Assert.Equal(TextureAssetState.Malformed, TextureAssetInspector.Inspect(truncated.Path, CancellationToken.None).State);
        }

        [Fact]
        public void Dds_parses_metadata_without_claiming_to_decode_compressed_payloads()
        {
            byte[] dds = Dds(128, 64);
            using var file = new TempAsset(".dds", dds);
            TextureAssetReport result = TextureAssetInspector.Inspect(file.Path, CancellationToken.None);
            Assert.Equal(TextureAssetState.Valid, result.State);
            Assert.Equal(TextureAssetKind.Dds, result.Kind);
            Assert.Equal(128, result.Width);
            Assert.Equal(64, result.Height);
            Assert.Equal(32768, result.Rgba8EquivalentBytes);
            Assert.Equal(128, result.ValidatedBytes);
            dds[76] = 0; // invalid pixel format structure size
            using var broken = new TempAsset(".dds", dds);
            Assert.Equal(TextureAssetState.Malformed, TextureAssetInspector.Inspect(broken.Path, CancellationToken.None).State);
        }

        [Fact]
        public void Unsafe_or_unknown_assets_do_not_crash_or_decode()
        {
            using var empty = new TempAsset(".png", Array.Empty<byte>());
            Assert.Equal(TextureAssetState.Malformed, TextureAssetInspector.Inspect(empty.Path, CancellationToken.None).State);
            using var unknown = new TempAsset(".jpg", new byte[] { 1, 2, 3 });
            Assert.Equal(TextureAssetState.Skipped, TextureAssetInspector.Inspect(unknown.Path, CancellationToken.None).State);
            using var png = new TempAsset(".png", Convert.FromBase64String(ValidPngBase64));
            using var cancel = new CancellationTokenSource();
            cancel.Cancel();
            Assert.Throws<OperationCanceledException>(() => TextureAssetInspector.Inspect(png.Path, cancel.Token));
        }

        internal static byte[] Dds(int width, int height)
        {
            byte[] bytes = new byte[128];
            bytes[0] = (byte)'D'; bytes[1] = (byte)'D'; bytes[2] = (byte)'S'; bytes[3] = (byte)' ';
            BitConverter.GetBytes(124).CopyTo(bytes, 4);
            BitConverter.GetBytes(height).CopyTo(bytes, 12);
            BitConverter.GetBytes(width).CopyTo(bytes, 16);
            BitConverter.GetBytes(32).CopyTo(bytes, 76);
            return bytes;
        }

        internal sealed class TempAsset : IDisposable
        {
            public string Path { get; }
            public TempAsset(string extension, byte[] bytes)
            {
                Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                    "mur-image-" + Guid.NewGuid().ToString("N") + extension);
                File.WriteAllBytes(Path, bytes);
            }
            public void Dispose() => File.Delete(Path);
        }
    }
}
