using System;
using System.Diagnostics;
using System.IO;
using NUnit.Framework;

namespace Hidano.AvatarSetupTool.Editor.Tests
{
    public class TextureHeaderReaderTests
    {
        private string directory;

        [SetUp] public void SetUp() => directory = Path.Combine(Path.GetTempPath(), "TextureHeaderReaderTests_" + Guid.NewGuid().ToString("N"));
        [TearDown] public void TearDown() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }

        [TestCase("png", 4096, 2048)]
        [TestCase("tga", 1024, 512)]
        [TestCase("psd", 8192, 4096)]
        [TestCase("psb", 8192, 4096)]
        [TestCase("bmp", 2048, 1024)]
        [TestCase("gif", 640, 480)]
        public void TryRead_FixedOffsetFormats_ReturnsDimensions(string extension, int expectedWidth, int expectedHeight)
        {
            var path = Write(extension, BuildHeader(extension, expectedWidth, expectedHeight));
            Assert.That(TextureHeaderReader.TryRead(path, out var dimensions), Is.True);
            Assert.That(dimensions.Width, Is.EqualTo(expectedWidth));
            Assert.That(dimensions.Height, Is.EqualTo(expectedHeight));
        }

        [TestCase("jpg", 4096, 2048)]
        [TestCase("jpeg", 4096, 2048)]
        [TestCase("tif", 3000, 2000)]
        [TestCase("tiff", 3000, 2000)]
        [TestCase("exr", 2048, 1024)]
        [TestCase("hdr", 1920, 1080)]
        public void TryRead_ScannedFormats_ReturnsDimensions(string extension, int expectedWidth, int expectedHeight)
        {
            var path = Write(extension, BuildScannedHeader(extension, expectedWidth, expectedHeight));
            Assert.That(TextureHeaderReader.TryRead(path, out var dimensions), Is.True);
            Assert.That(dimensions.Width, Is.EqualTo(expectedWidth));
            Assert.That(dimensions.Height, Is.EqualTo(expectedHeight));
        }

        [TestCase("PNG")][TestCase("jpg")][TestCase("jpeg")][TestCase("tga")][TestCase("psd")][TestCase("psb")]
        [TestCase("bmp")][TestCase("gif")][TestCase("tif")][TestCase("tiff")][TestCase("exr")][TestCase("hdr")]
        public void IsSupportedExtension_IsCaseInsensitive(string extension) => Assert.That(TextureHeaderReader.IsSupportedExtension("image." + extension), Is.True);

        [Test]
        public void TryRead_InvalidInputs_ReturnsFalseWithoutThrowing()
        {
            Assert.That(TextureHeaderReader.TryRead(Path.Combine(directory, "missing.png"), out _), Is.False);
            var path = Write("png", new byte[0]);
            Assert.That(TextureHeaderReader.TryRead(path, out _), Is.False);
            path = Write("png", BuildHeader("png", 0, 2));
            Assert.That(TextureHeaderReader.TryRead(path, out _), Is.False);
            path = Write("unknown", BuildHeader("png", 2, 2));
            Assert.That(TextureHeaderReader.TryRead(path, out _), Is.False);
        }

        [TestCase("png")]
        [TestCase("tga")]
        [TestCase("psd")]
        [TestCase("bmp")]
        [TestCase("gif")]
        [TestCase("jpg")]
        [TestCase("tif")]
        [TestCase("exr")]
        [TestCase("hdr")]
        public void TryRead_TruncatedHeader_ReturnsFalseWithoutThrowing(string extension)
        {
            var completeHeader = extension == "jpg" || extension == "tif" || extension == "exr" || extension == "hdr"
                ? BuildScannedHeader(extension, 128, 64)
                : BuildHeader(extension, 128, 64);
            var truncatedHeader = new byte[Math.Max(1, completeHeader.Length / 2)];
            Array.Copy(completeHeader, truncatedHeader, truncatedHeader.Length);
            var path = Write(extension, truncatedHeader);

            Assert.That(TextureHeaderReader.TryRead(path, out _), Is.False);
        }

        [Test]
        public void TryRead_DoesNotModifyFileContentOrTimestamp()
        {
            var path = Write("png", BuildHeader("png", 4096, 2048));
            var expectedContent = File.ReadAllBytes(path);
            var expectedTimestamp = DateTime.UtcNow.AddMinutes(-5);
            File.SetLastWriteTimeUtc(path, expectedTimestamp);

            Assert.That(TextureHeaderReader.TryRead(path, out var dimensions), Is.True);
            Assert.That(dimensions.Width, Is.EqualTo(4096));
            Assert.That(dimensions.Height, Is.EqualTo(2048));
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(expectedContent));
            Assert.That(File.GetLastWriteTimeUtc(path), Is.EqualTo(expectedTimestamp));
        }

        [Test]
        public void TryRead_LargeBodyReadsHeaderOnly()
        {
            var header = BuildHeader("png", 4096, 2048);
            var bytes = new byte[8 * 1024 * 1024];
            Array.Copy(header, bytes, header.Length);
            var path = Write("png", bytes);
            var stopwatch = Stopwatch.StartNew();

            Assert.That(TextureHeaderReader.TryRead(path, out var dimensions), Is.True);

            stopwatch.Stop();
            Assert.That(dimensions.Width, Is.EqualTo(4096));
            Assert.That(dimensions.Height, Is.EqualTo(2048));
            Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(1)));
        }

        private string Write(string extension, byte[] bytes)
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "image." + extension);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        private static byte[] BuildHeader(string extension, int width, int height)
        {
            var bytes = new byte[extension == "png" ? 33 : extension == "gif" ? 10 : extension == "tga" ? 18 : extension == "bmp" ? 26 : 22];
            switch (extension)
            {
                case "png": Array.Copy(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }, bytes, 8); Array.Copy(new byte[] { 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52 }, 0, bytes, 8, 8); PutBe(bytes, 16, width); PutBe(bytes, 20, height); break;
                case "tga": PutLe(bytes, 12, width); PutLe(bytes, 14, height); break;
                case "psd": case "psb": bytes[0] = (byte)'8'; bytes[1] = (byte)'B'; bytes[2] = (byte)'P'; bytes[3] = (byte)'S'; PutBe(bytes, 4, extension == "psb" ? 2 : 1, 2); PutBe(bytes, 14, height); PutBe(bytes, 18, width); break;
                case "bmp": bytes[0] = (byte)'B'; bytes[1] = (byte)'M'; PutLe(bytes, 14, 40); PutLe(bytes, 18, width); PutLe(bytes, 22, height); break;
                case "gif": Array.Copy(new byte[] { (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a' }, bytes, 6); PutLe16(bytes, 6, width); PutLe16(bytes, 8, height); break;
            }
            return bytes;
        }

        private static byte[] BuildScannedHeader(string extension, int width, int height)
        {
            if (extension == "jpg" || extension == "jpeg")
            {
                return new byte[] { 0xff, 0xd8, 0xff, 0xc0, 0, 11, 8, (byte)(height >> 8), (byte)height,
                    (byte)(width >> 8), (byte)width, 1, 1, 0x11, 0 };
            }

            if (extension == "tif" || extension == "tiff")
            {
                var bytes = new byte[8 + 2 + 24 + 4];
                bytes[0] = (byte)'I'; bytes[1] = (byte)'I'; bytes[2] = 42; PutLe(bytes, 4, 8);
                PutLe16(bytes, 8, 2);
                PutLe16(bytes, 10, 256); PutLe16(bytes, 12, 4); PutLe(bytes, 14, 1); PutLe(bytes, 18, width);
                PutLe16(bytes, 22, 257); PutLe16(bytes, 24, 4); PutLe(bytes, 26, 1); PutLe(bytes, 30, height);
                return bytes;
            }

            if (extension == "exr")
            {
                using (var stream = new MemoryStream())
                using (var writer = new BinaryWriter(stream))
                {
                    writer.Write(new byte[] { 0x76, 0x2f, 0x31, 0x01, 2, 0, 0, 0 });
                    writer.Write(System.Text.Encoding.ASCII.GetBytes("dataWindow\0box2i\0"));
                    writer.Write(16); writer.Write(0); writer.Write(0); writer.Write(width - 1); writer.Write(height - 1);
                    writer.Write((byte)0); return stream.ToArray();
                }
            }

            return System.Text.Encoding.ASCII.GetBytes("#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n\n-Y " + height + " +X " + width + "\n");
        }

        private static void PutLe16(byte[] b, int o, int v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); }
        private static void PutLe(byte[] b, int o, int v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24); }
        private static void PutBe(byte[] b, int o, int v, int size = 4) { for (var i = 0; i < size; i++) b[o + i] = (byte)(v >> (8 * (size - i - 1))); }
    }
}
