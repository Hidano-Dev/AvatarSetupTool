using System;
using System.IO;

namespace Hidano.AvatarSetupTool.Editor
{
    internal readonly struct TextureDimensions
    {
        public TextureDimensions(int width, int height)
        {
            Width = width;
            Height = height;
        }

        public int Width { get; }
        public int Height { get; }
        public int LongerSide => Math.Max(Width, Height);
    }

    internal static class TextureHeaderReader
    {
        private const int MaxDimension = 1000000;

        public static bool IsSupportedExtension(string path)
        {
            try
            {
                switch (Path.GetExtension(path ?? string.Empty).ToLowerInvariant())
                {
                    case ".png": case ".jpg": case ".jpeg": case ".tga": case ".psd": case ".psb":
                    case ".bmp": case ".gif": case ".tif": case ".tiff": case ".exr": case ".hdr":
                        return true;
                    default:
                        return false;
                }
            }
            catch
            {
                return false;
            }
        }

        public static bool TryRead(string fullPath, out TextureDimensions dimensions)
        {
            dimensions = default(TextureDimensions);
            try
            {
                if (!IsSupportedExtension(fullPath)) return false;

                using (var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new BinaryReader(stream))
                {
                    var extension = Path.GetExtension(fullPath).ToLowerInvariant();
                    int width;
                    int height;
                    bool success;
                    switch (extension)
                    {
                        case ".png": success = TryReadPng(reader, out width, out height); break;
                        case ".tga": success = TryReadTga(reader, out width, out height); break;
                        case ".psd": case ".psb": success = TryReadPsd(reader, out width, out height); break;
                        case ".bmp": success = TryReadBmp(reader, out width, out height); break;
                        case ".gif": success = TryReadGif(reader, out width, out height); break;
                        default: width = 0; height = 0; success = false; break;
                    }

                    if (!success || !IsValidDimension(width) || !IsValidDimension(height)) return false;
                    dimensions = new TextureDimensions(width, height);
                    return true;
                }
            }
            catch
            {
                dimensions = default(TextureDimensions);
                return false;
            }
        }

        private static bool TryReadPng(BinaryReader reader, out int width, out int height)
        {
            width = height = 0;
            var header = reader.ReadBytes(33);
            if (header.Length < 33 || header[0] != 0x89 || header[1] != 'P' || header[2] != 'N' || header[3] != 'G'
                || header[4] != 0x0D || header[5] != 0x0A || header[6] != 0x1A || header[7] != 0x0A
                || header[12] != 'I' || header[13] != 'H' || header[14] != 'D' || header[15] != 'R'
                || ReadBigEndian32(header, 8) != 13) return false;
            width = ReadPositiveBigEndian32(header, 16);
            height = ReadPositiveBigEndian32(header, 20);
            return width > 0 && height > 0;
        }

        private static bool TryReadTga(BinaryReader reader, out int width, out int height)
        {
            width = height = 0;
            var header = reader.ReadBytes(18);
            if (header.Length < 18) return false;
            width = ReadLittleEndian16(header, 12);
            height = ReadLittleEndian16(header, 14);
            return width > 0 && height > 0;
        }

        private static bool TryReadPsd(BinaryReader reader, out int width, out int height)
        {
            width = height = 0;
            var header = reader.ReadBytes(22);
            if (header.Length < 22 || header[0] != '8' || header[1] != 'B' || header[2] != 'P' || header[3] != 'S'
                || (ReadBigEndian16(header, 4) != 1 && ReadBigEndian16(header, 4) != 2)) return false;
            height = ReadPositiveBigEndian32(header, 14);
            width = ReadPositiveBigEndian32(header, 18);
            return width > 0 && height > 0;
        }

        private static bool TryReadBmp(BinaryReader reader, out int width, out int height)
        {
            width = height = 0;
            var header = reader.ReadBytes(26);
            if (header.Length < 26 || header[0] != 'B' || header[1] != 'M') return false;
            var size = ReadLittleEndian32(header, 14);
            if (size == 12)
            {
                width = ReadLittleEndian16(header, 18);
                height = ReadLittleEndian16(header, 20);
                return width > 0 && height > 0;
            }

            if (size < 40) return false;
            var signedWidth = ReadLittleEndianSigned32(header, 18);
            var signedHeight = ReadLittleEndianSigned32(header, 22);
            if (signedWidth == int.MinValue || signedHeight == int.MinValue) return false;
            width = Math.Abs(signedWidth);
            height = Math.Abs(signedHeight);
            return width > 0 && height > 0;
        }

        private static bool TryReadGif(BinaryReader reader, out int width, out int height)
        {
            width = height = 0;
            var header = reader.ReadBytes(10);
            if (header.Length < 10 || header[0] != 'G' || header[1] != 'I' || header[2] != 'F'
                || (header[3] != '8') || (header[4] != '7' && header[4] != '9') || header[5] != 'a') return false;
            width = ReadLittleEndian16(header, 6);
            height = ReadLittleEndian16(header, 8);
            return width > 0 && height > 0;
        }

        private static bool IsValidDimension(int value) => value > 0 && value <= MaxDimension;
        private static int ReadLittleEndian16(byte[] data, int offset) => data[offset] | data[offset + 1] << 8;
        private static int ReadBigEndian16(byte[] data, int offset) => data[offset] << 8 | data[offset + 1];
        private static int ReadLittleEndian32(byte[] data, int offset) => data[offset] | data[offset + 1] << 8 | data[offset + 2] << 16 | data[offset + 3] << 24;
        private static int ReadLittleEndianSigned32(byte[] data, int offset) => ReadLittleEndian32(data, offset);
        private static int ReadPositiveBigEndian32(byte[] data, int offset)
        {
            var value = ReadBigEndian32(data, offset);
            return value > 0 && value <= MaxDimension ? (int)value : 0;
        }
        private static uint ReadBigEndian32(byte[] data, int offset) => (uint)data[offset] << 24 | (uint)data[offset + 1] << 16 | (uint)data[offset + 2] << 8 | data[offset + 3];
    }
}
