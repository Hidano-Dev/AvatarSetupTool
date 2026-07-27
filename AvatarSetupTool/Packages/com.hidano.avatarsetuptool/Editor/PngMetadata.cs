using System;
using System.Text;

namespace Hidano.AvatarSetupTool.Editor
{
    /// <summary>
    /// PNG バイナリへ国際化テキストチャンク (iTXt) を挿入するユーティリティ。
    /// Texture2D.EncodeToPNG はメタデータを書けないため、IHDR 直後へ自前で差し込む。
    /// iTXt は UTF-8 なので日本語のデバッグ情報もそのまま格納できる。
    /// </summary>
    internal static class PngMetadata
    {
        /// <summary>
        /// keyword と text の iTXt チャンクを挿入した新しい PNG バイト列を返す。
        /// PNG として不正なバイト列だった場合は元をそのまま返す。
        /// </summary>
        public static byte[] WithText(byte[] png, string keyword, string text)
        {
            // シグネチャ 8 bytes + IHDR チャンク (length 4 + type 4 + data 13 + CRC 4)
            const int InsertOffset = 8 + 25;
            if (png == null || png.Length < InsertOffset
                || png[0] != 0x89 || png[1] != (byte)'P' || png[2] != (byte)'N' || png[3] != (byte)'G'
                || png[12] != (byte)'I' || png[13] != (byte)'H' || png[14] != (byte)'D' || png[15] != (byte)'R')
            {
                return png;
            }

            var keywordBytes = Encoding.ASCII.GetBytes(keyword);
            var textBytes = Encoding.UTF8.GetBytes(text);

            // keyword \0, 圧縮フラグ 0, 圧縮方式 0, 言語タグ \0, 翻訳キーワード \0, 本文
            var data = new byte[keywordBytes.Length + 5 + textBytes.Length];
            Array.Copy(keywordBytes, data, keywordBytes.Length);
            Array.Copy(textBytes, 0, data, keywordBytes.Length + 5, textBytes.Length);

            var chunk = new byte[4 + 4 + data.Length + 4];
            WriteBigEndian(chunk, 0, (uint)data.Length);
            chunk[4] = (byte)'i';
            chunk[5] = (byte)'T';
            chunk[6] = (byte)'X';
            chunk[7] = (byte)'t';
            Array.Copy(data, 0, chunk, 8, data.Length);
            WriteBigEndian(chunk, 8 + data.Length, Crc32(chunk, 4, 4 + data.Length));

            var result = new byte[png.Length + chunk.Length];
            Array.Copy(png, result, InsertOffset);
            Array.Copy(chunk, 0, result, InsertOffset, chunk.Length);
            Array.Copy(png, InsertOffset, result, InsertOffset + chunk.Length, png.Length - InsertOffset);
            return result;
        }

        private static void WriteBigEndian(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)(value >> 24);
            buffer[offset + 1] = (byte)(value >> 16);
            buffer[offset + 2] = (byte)(value >> 8);
            buffer[offset + 3] = (byte)value;
        }

        private static uint[] crcTable;

        /// <summary>PNG 仕様のチャンク CRC (CRC-32、多項式 0xEDB88320)。</summary>
        private static uint Crc32(byte[] buffer, int offset, int count)
        {
            if (crcTable == null)
            {
                crcTable = new uint[256];
                for (uint n = 0; n < 256; n++)
                {
                    var c = n;
                    for (var k = 0; k < 8; k++)
                    {
                        c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                    }

                    crcTable[n] = c;
                }
            }

            var crc = 0xFFFFFFFFu;
            for (var i = 0; i < count; i++)
            {
                crc = crcTable[(crc ^ buffer[offset + i]) & 0xFF] ^ (crc >> 8);
            }

            return crc ^ 0xFFFFFFFFu;
        }
    }
}
