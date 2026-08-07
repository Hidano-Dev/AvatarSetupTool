using System;
using System.IO;
using System.Text;

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
        private const int JpegScanLimit = 1024 * 1024;
        private const int ExrScanLimit = 64 * 1024;
        private const int HdrScanLimit = 8 * 1024;
        private const int TiffEntryLimit = 512;

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
                        case ".jpg": case ".jpeg": success = TryReadJpeg(reader, out width, out height); break;
                        case ".tif": case ".tiff": success = TryReadTiff(stream, out width, out height); break;
                        case ".exr": success = TryReadExr(stream, out width, out height); break;
                        case ".hdr": success = TryReadHdr(stream, out width, out height); break;
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

        private static bool TryReadJpeg(BinaryReader reader, out int width, out int height)
        {
            width = height = 0;
            if (reader.ReadByte() != 0xff || reader.ReadByte() != 0xd8) return false;
            var stream = reader.BaseStream;
            while (stream.Position < Math.Min(stream.Length, JpegScanLimit))
            {
                var markerPrefix = reader.ReadByte();
                if (markerPrefix != 0xff) continue;
                byte marker;
                do { marker = reader.ReadByte(); } while (marker == 0xff);
                if (marker == 0xd9 || marker == 0xda) return false;
                if (marker >= 0xd0 && marker <= 0xd7 || marker == 0x01) continue;
                var length = ReadBigEndian16(reader.ReadBytes(2), 0);
                if (length < 2 || stream.Position + length - 2 > stream.Length) return false;
                if (marker >= 0xc0 && marker <= 0xcf && marker != 0xc4 && marker != 0xc8 && marker != 0xcc)
                {
                    if (length < 7) return false;
                    reader.ReadByte();
                    height = ReadBigEndian16(reader.ReadBytes(2), 0);
                    width = ReadBigEndian16(reader.ReadBytes(2), 0);
                    return width > 0 && height > 0;
                }
                stream.Seek(length - 2, SeekOrigin.Current);
            }
            return false;
        }

        private static bool TryReadTiff(Stream stream, out int width, out int height)
        {
            width = height = 0;
            var header = new BinaryReader(stream);
            var byteOrder = header.ReadBytes(2);
            if (byteOrder.Length < 2 || (byteOrder[0] != 'I' && byteOrder[0] != 'M') || byteOrder[0] != byteOrder[1]) return false;
            var littleEndian = byteOrder[0] == 'I';
            if (ReadU16(header, littleEndian) != 42) return false;
            var ifdOffset = ReadU32(header, littleEndian);
            if (ifdOffset > stream.Length - 2) return false;
            stream.Position = ifdOffset;
            var count = ReadU16(header, littleEndian);
            if (count > TiffEntryLimit || stream.Position + count * 12L > stream.Length) return false;
            for (var i = 0; i < count; i++)
            {
                var tag = ReadU16(header, littleEndian);
                var type = ReadU16(header, littleEndian);
                var valueCount = ReadU32(header, littleEndian);
                var valueOffset = stream.Position;
                var valueSize = type == 3 ? 2UL : type == 4 ? 4UL : 0UL;
                if ((tag == 256 || tag == 257) && valueSize != 0 && valueCount == 1)
                {
                    ulong value;
                    if (valueSize <= 4)
                    {
                        value = type == 3 ? ReadU16(header, littleEndian) : ReadU32(header, littleEndian);
                        stream.Position = valueOffset + 4;
                    }
                    else return false;
                    if (tag == 256) width = ToDimension(value); else height = ToDimension(value);
                }
                else stream.Position = valueOffset + 4;
            }
            return width > 0 && height > 0;
        }

        private static bool TryReadExr(Stream stream, out int width, out int height)
        {
            width = height = 0;
            var reader = new BinaryReader(stream);
            if (ReadLittleEndian32(reader.ReadBytes(4), 0) != 0x01312f76) return false;
            reader.ReadBytes(4);
            while (stream.Position < Math.Min(stream.Length, ExrScanLimit))
            {
                var name = ReadNullTerminated(reader, ExrScanLimit);
                if (name == null) return false;
                if (name.Length == 0) return false;
                var type = ReadNullTerminated(reader, ExrScanLimit);
                if (type == null) return false;
                var size = ReadLittleEndian32(reader.ReadBytes(4), 0);
                if (size < 0 || stream.Position + size > stream.Length || stream.Position + size > ExrScanLimit) return false;
                if (name == "dataWindow" && type == "box2i" && size >= 16)
                {
                    var xMin = reader.ReadInt32(); var yMin = reader.ReadInt32();
                    var xMax = reader.ReadInt32(); var yMax = reader.ReadInt32();
                    width = ToDimension((ulong)xMax - (ulong)xMin + 1); height = ToDimension((ulong)yMax - (ulong)yMin + 1);
                    return width > 0 && height > 0;
                }
                stream.Seek(size, SeekOrigin.Current);
            }
            return false;
        }

        private static bool TryReadHdr(Stream stream, out int width, out int height)
        {
            width = height = 0;
            var bytes = new byte[HdrScanLimit];
            var length = stream.Read(bytes, 0, bytes.Length);
            var text = Encoding.ASCII.GetString(bytes, 0, length);
            if (!text.StartsWith("#?RADIANCE", StringComparison.Ordinal) && !text.StartsWith("#?RGBE", StringComparison.Ordinal)) return false;
            var lines = text.Replace("\r", string.Empty).Split('\n');
            foreach (var line in lines)
            {
                var parts = line.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 4 || (parts[0] != "+Y" && parts[0] != "-Y") || (parts[2] != "+X" && parts[2] != "-X")) continue;
                if (int.TryParse(parts[1], out var y) && int.TryParse(parts[3], out var x))
                {
                    width = x; height = y; return width > 0 && height > 0;
                }
            }
            return false;
        }

        private static int ToDimension(ulong value) => value > 0 && value <= MaxDimension ? (int)value : 0;
        private static ushort ReadU16(BinaryReader reader, bool little) { var b = reader.ReadBytes(2); return (ushort)(little ? b[0] | b[1] << 8 : b[0] << 8 | b[1]); }
        private static uint ReadU32(BinaryReader reader, bool little) { var b = reader.ReadBytes(4); return little ? (uint)(b[0] | b[1] << 8 | b[2] << 16 | b[3] << 24) : (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]); }
        private static string ReadNullTerminated(BinaryReader reader, int limit)
        {
            var bytes = new System.Collections.Generic.List<byte>();
            while (reader.BaseStream.Position < Math.Min(reader.BaseStream.Length, limit)) { var b = reader.ReadByte(); if (b == 0) return Encoding.ASCII.GetString(bytes.ToArray()); bytes.Add(b); }
            return null;
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
