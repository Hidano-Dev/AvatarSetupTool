using System;
using System.Text;
using NUnit.Framework;
using UnityEngine;

namespace Hidano.AvatarSetupTool.Editor.Tests
{
    /// <summary>
    /// PngMetadata.WithText の挙動固定テスト。iTXt チャンクの挿入位置 (IHDR 直後)・
    /// CRC・UTF-8 本文のラウンドトリップを固定し、エンコード経路置換後も
    /// 同一仕様のメタデータが付与されることを検証する基準にする。
    /// </summary>
    public class PngMetadataTests
    {
        /// <summary>シグネチャ 8 bytes + IHDR チャンク 25 bytes。iTXt はこの直後に挿入される。</summary>
        private const int InsertOffset = 33;

        private static byte[] EncodeSamplePng(out Color32[] pixels)
        {
            var texture = new Texture2D(4, 3, TextureFormat.RGB24, false);
            pixels = new Color32[12];
            for (var i = 0; i < pixels.Length; i++)
            {
                pixels[i] = new Color32((byte)(i * 20), (byte)(255 - i * 20), (byte)(i * 7), 255);
            }

            texture.SetPixels32(pixels);
            var png = texture.EncodeToPNG();
            UnityEngine.Object.DestroyImmediate(texture);
            return png;
        }

        private static uint ReadBigEndian(byte[] buffer, int offset)
        {
            return ((uint)buffer[offset] << 24) | ((uint)buffer[offset + 1] << 16)
                | ((uint)buffer[offset + 2] << 8) | buffer[offset + 3];
        }

        /// <summary>実装と独立に書いた PNG 仕様の CRC-32 (多項式 0xEDB88320)。</summary>
        private static uint ReferenceCrc32(byte[] buffer, int offset, int count)
        {
            var crc = 0xFFFFFFFFu;
            for (var i = 0; i < count; i++)
            {
                crc ^= buffer[offset + i];
                for (var k = 0; k < 8; k++)
                {
                    crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
                }
            }

            return crc ^ 0xFFFFFFFFu;
        }

        [Test]
        public void ReferenceCrc32_MatchesKnownTestVector()
        {
            // CRC-32 の標準検証ベクトル: "123456789" -> 0xCBF43926
            var data = Encoding.ASCII.GetBytes("123456789");
            Assert.That(ReferenceCrc32(data, 0, data.Length), Is.EqualTo(0xCBF43926u));
        }

        [Test]
        public void WithText_InsertsItxtChunkImmediatelyAfterIhdr()
        {
            var png = EncodeSamplePng(out _);
            var result = PngMetadata.WithText(png, "Comment", "hello");

            // IHDR までは無変更
            for (var i = 0; i < InsertOffset; i++)
            {
                Assert.That(result[i], Is.EqualTo(png[i]), $"byte at {i}");
            }

            // 直後にチャンクタイプ "iTXt"
            Assert.That(result[InsertOffset + 4], Is.EqualTo((byte)'i'));
            Assert.That(result[InsertOffset + 5], Is.EqualTo((byte)'T'));
            Assert.That(result[InsertOffset + 6], Is.EqualTo((byte)'X'));
            Assert.That(result[InsertOffset + 7], Is.EqualTo((byte)'t'));

            // チャンクの後ろは元の PNG の残り全体がそのまま続く
            var chunkLength = (int)ReadBigEndian(result, InsertOffset);
            var chunkTotal = 4 + 4 + chunkLength + 4;
            Assert.That(result.Length, Is.EqualTo(png.Length + chunkTotal));
            for (var i = InsertOffset; i < png.Length; i++)
            {
                Assert.That(result[i + chunkTotal], Is.EqualTo(png[i]), $"tail byte at {i}");
            }
        }

        [Test]
        public void WithText_ChunkDataLayout_FollowsItxtSpec()
        {
            const string keyword = "Comment";
            const string text = "layout-check";
            var png = EncodeSamplePng(out _);
            var result = PngMetadata.WithText(png, keyword, text);

            var keywordBytes = Encoding.ASCII.GetBytes(keyword);
            var textBytes = Encoding.UTF8.GetBytes(text);
            var chunkLength = (int)ReadBigEndian(result, InsertOffset);
            Assert.That(chunkLength, Is.EqualTo(keywordBytes.Length + 5 + textBytes.Length));

            var dataOffset = InsertOffset + 8;
            for (var i = 0; i < keywordBytes.Length; i++)
            {
                Assert.That(result[dataOffset + i], Is.EqualTo(keywordBytes[i]), $"keyword byte {i}");
            }

            // keyword \0, 圧縮フラグ 0, 圧縮方式 0, 言語タグ \0, 翻訳キーワード \0
            for (var i = 0; i < 5; i++)
            {
                Assert.That(result[dataOffset + keywordBytes.Length + i], Is.EqualTo(0), $"separator byte {i}");
            }

            for (var i = 0; i < textBytes.Length; i++)
            {
                Assert.That(result[dataOffset + keywordBytes.Length + 5 + i], Is.EqualTo(textBytes[i]), $"text byte {i}");
            }
        }

        [Test]
        public void WithText_ChunkCrc_CoversTypeAndData()
        {
            var png = EncodeSamplePng(out _);
            var result = PngMetadata.WithText(png, "Comment", "crc-check 日本語");

            var chunkLength = (int)ReadBigEndian(result, InsertOffset);
            var storedCrc = ReadBigEndian(result, InsertOffset + 8 + chunkLength);
            var expectedCrc = ReferenceCrc32(result, InsertOffset + 4, 4 + chunkLength);

            Assert.That(storedCrc, Is.EqualTo(expectedCrc));
        }

        [Test]
        public void WithText_Utf8Body_RoundTrips()
        {
            const string keyword = "Comment";
            const string text = "解像度 8192×8192 / SSAA×2 のデバッグ情報 🎨 改行\nタブ\tも保持";
            var png = EncodeSamplePng(out _);
            var result = PngMetadata.WithText(png, keyword, text);

            var chunkLength = (int)ReadBigEndian(result, InsertOffset);
            var dataOffset = InsertOffset + 8;
            var keywordLength = Encoding.ASCII.GetBytes(keyword).Length;
            var textLength = chunkLength - keywordLength - 5;
            var decoded = Encoding.UTF8.GetString(result, dataOffset + keywordLength + 5, textLength);

            Assert.That(decoded, Is.EqualTo(text));
        }

        [Test]
        public void WithText_Output_RemainsLoadablePng_WithSamePixels()
        {
            var png = EncodeSamplePng(out var pixels);
            var result = PngMetadata.WithText(png, "Comment", "ラウンドトリップ検証");

            var loaded = new Texture2D(2, 2);
            var ok = loaded.LoadImage(result);
            Assert.That(ok, Is.True, "iTXt 挿入後も PNG としてデコードできる");
            Assert.That(loaded.width, Is.EqualTo(4));
            Assert.That(loaded.height, Is.EqualTo(3));

            var loadedPixels = loaded.GetPixels32();
            UnityEngine.Object.DestroyImmediate(loaded);
            for (var i = 0; i < pixels.Length; i++)
            {
                Assert.That(loadedPixels[i].r, Is.EqualTo(pixels[i].r), $"r at {i}");
                Assert.That(loadedPixels[i].g, Is.EqualTo(pixels[i].g), $"g at {i}");
                Assert.That(loadedPixels[i].b, Is.EqualTo(pixels[i].b), $"b at {i}");
            }
        }

        [Test]
        public void WithText_InvalidInput_ReturnsInputAsIs()
        {
            Assert.That(PngMetadata.WithText(null, "Comment", "x"), Is.Null);

            var tooShort = new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' };
            Assert.That(PngMetadata.WithText(tooShort, "Comment", "x"), Is.SameAs(tooShort));

            var wrongSignature = new byte[40];
            Assert.That(PngMetadata.WithText(wrongSignature, "Comment", "x"), Is.SameAs(wrongSignature));

            var png = EncodeSamplePng(out _);
            var brokenIhdr = (byte[])png.Clone();
            brokenIhdr[12] = (byte)'X';
            Assert.That(PngMetadata.WithText(brokenIhdr, "Comment", "x"), Is.SameAs(brokenIhdr));
        }
    }
}
