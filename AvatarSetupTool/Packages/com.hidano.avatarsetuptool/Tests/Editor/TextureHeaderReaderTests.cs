using System;
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

        private static void PutLe16(byte[] b, int o, int v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); }
        private static void PutLe(byte[] b, int o, int v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24); }
        private static void PutBe(byte[] b, int o, int v, int size = 4) { for (var i = 0; i < size; i++) b[o + i] = (byte)(v >> (8 * (size - i - 1))); }
    }
}
