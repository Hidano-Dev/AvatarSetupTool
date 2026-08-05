using NUnit.Framework;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace Hidano.AvatarSetupTool.Editor.Tests
{
    /// <summary>
    /// ImageConversion.EncodeArrayToPNG による RGB24 直接エンコードのピクセル一致・行順検証
    /// (フェーズ 2 の入口ゲート)。確定した組合せ: GraphicsFormat.R8G8B8_SRGB (3 bytes/px) +
    /// ボトムアップ行順 (バッファ先頭行 = 画像最下段。GetPixels32 / SetPixels32 と同じ
    /// Unity テクスチャ行順)。design.md の top-down 推定は本テストの初回実行で反証され、
    /// マーカー検証によりボトムアップで確定した — 4.2 以降の合成バッファはこれに追随する。
    /// PNG バイト列自体は圧縮器差で現行経路と異なってよく、比較は常にデコード後のピクセル値で行う。
    /// </summary>
    public class PngEncodeTests
    {
        /// <summary>キャプチャ背景と同じ不透明グレー。</summary>
        private const byte Background = 184;

        /// <summary>
        /// 背景色で埋めた RGB24 ボトムアップバッファの中央に決定的な擬似乱数 (LCG) 領域を重ねる。
        /// テストの再現性のため Unity の Random は使わない。
        /// </summary>
        private static byte[] MakeRgb24Pattern(int width, int height, uint seed)
        {
            var buffer = new byte[width * height * 3];
            for (var i = 0; i < buffer.Length; i++)
            {
                buffer[i] = Background;
            }

            var state = seed;
            for (var y = height / 4; y < height * 3 / 4; y++)
            {
                for (var x = width / 4; x < width * 3 / 4; x++)
                {
                    var offset = (y * width + x) * 3;
                    state = state * 1664525u + 1013904223u;
                    buffer[offset] = (byte)(state >> 24);
                    buffer[offset + 1] = (byte)(state >> 16);
                    buffer[offset + 2] = (byte)(state >> 8);
                }
            }

            return buffer;
        }

        /// <summary>新経路: RGB24 ボトムアップバッファを EncodeArrayToPNG で直接 PNG 化する。</summary>
        private static byte[] EncodeDirect(byte[] rgbBottomUp, int width, int height)
        {
            return ImageConversion.EncodeArrayToPNG(
                rgbBottomUp, GraphicsFormat.R8G8B8_SRGB,
                (uint)width, (uint)height, (uint)(width * 3));
        }

        /// <summary>
        /// 現行経路: 同一画像を Texture2D(RGB24) + SetPixels32 + EncodeToPNG で PNG 化する。
        /// SetPixels32 もボトムアップ行順のため、行の入れ替えなしでバッファと対応する。
        /// </summary>
        private static byte[] EncodeViaTexture2D(byte[] rgbBottomUp, int width, int height)
        {
            var pixels = new Color32[width * height];
            for (var i = 0; i < pixels.Length; i++)
            {
                pixels[i] = new Color32(
                    rgbBottomUp[i * 3], rgbBottomUp[i * 3 + 1], rgbBottomUp[i * 3 + 2], 255);
            }

            var texture = new Texture2D(width, height, TextureFormat.RGB24, false);
            try
            {
                texture.SetPixels32(pixels);
                return texture.EncodeToPNG();
            }
            finally
            {
                Object.DestroyImmediate(texture);
            }
        }

        /// <summary>
        /// PNG をデコードしてボトムアップ行順の Color32[] を返す。LoadImage の結果 (GetPixels32)
        /// は「PNG 先頭行 = 画像最上段 = 配列末尾行」という Texture2D の確立した仕様に従うため、
        /// エンコード側の行順推定から独立した基準になる。
        /// </summary>
        private static Color32[] DecodeBottomUp(byte[] png, int expectedWidth, int expectedHeight)
        {
            var texture = new Texture2D(2, 2);
            try
            {
                Assert.That(texture.LoadImage(png), Is.True, "PNG のデコードに失敗");
                Assert.That(texture.width, Is.EqualTo(expectedWidth));
                Assert.That(texture.height, Is.EqualTo(expectedHeight));
                return texture.GetPixels32();
            }
            finally
            {
                Object.DestroyImmediate(texture);
            }
        }

        [TestCase(16, 12, 12345u)]
        [TestCase(7, 5, 67890u)]  // 非正方形かつ奇数辺 (行アライメントの取り違えを検出)
        [TestCase(1, 1, 24680u)]
        public void EncodeArrayToPng_MatchesTexture2DRoute_AfterDecode(int width, int height, uint seed)
        {
            var buffer = MakeRgb24Pattern(width, height, seed);

            var directPng = EncodeDirect(buffer, width, height);
            var legacyPng = EncodeViaTexture2D(buffer, width, height);
            Assert.That(directPng, Is.Not.Null.And.Not.Empty);
            Assert.That(legacyPng, Is.Not.Null.And.Not.Empty);

            var direct = DecodeBottomUp(directPng, width, height);
            var legacy = DecodeBottomUp(legacyPng, width, height);
            for (var i = 0; i < direct.Length; i++)
            {
                Assert.That(direct[i].r, Is.EqualTo(legacy[i].r), $"r at {i}");
                Assert.That(direct[i].g, Is.EqualTo(legacy[i].g), $"g at {i}");
                Assert.That(direct[i].b, Is.EqualTo(legacy[i].b), $"b at {i}");
            }
        }

        [Test]
        public void EncodeArrayToPng_RoundTrip_PreservesBufferExactly()
        {
            // R8G8B8_SRGB 指定で色空間変換が挟まらず、バッファのバイト値がそのまま
            // PNG のピクセル値になることを確認する (現行経路との一致の前提条件)
            const int width = 16;
            const int height = 12;
            var buffer = MakeRgb24Pattern(width, height, 55555u);

            var decoded = DecodeBottomUp(EncodeDirect(buffer, width, height), width, height);

            for (var i = 0; i < decoded.Length; i++)
            {
                Assert.That(decoded[i].r, Is.EqualTo(buffer[i * 3]), $"r at {i}");
                Assert.That(decoded[i].g, Is.EqualTo(buffer[i * 3 + 1]), $"g at {i}");
                Assert.That(decoded[i].b, Is.EqualTo(buffer[i * 3 + 2]), $"b at {i}");
            }
        }

        [Test]
        public void EncodeArrayToPng_RowOrder_IsBottomUp()
        {
            // 行順の確定検証: バッファ末尾行のマーカーが画像最上段 (PNG 先頭行) に現れる
            // = EncodeArrayToPNG はバッファをボトムアップで解釈する。初回実行 (top-down 仮定)
            // では先頭行マーカーが最上段に現れず反証されたため、この向きで確定した。
            // 四隅を異色にして上下・左右の取り違えも検出する
            const int width = 8;
            const int height = 6;
            var buffer = MakeRgb24Pattern(width, height, 97531u);

            void SetPixel(int x, int y, byte r, byte g, byte b)
            {
                var offset = (y * width + x) * 3;
                buffer[offset] = r;
                buffer[offset + 1] = g;
                buffer[offset + 2] = b;
            }

            SetPixel(0, height - 1, 255, 0, 0);         // バッファ末尾行左端 (赤) → 画像最上段
            SetPixel(width - 1, height - 1, 0, 255, 0); // バッファ末尾行右端 (緑)
            SetPixel(0, 0, 0, 0, 255);                  // バッファ先頭行左端 (青) → 画像最下段

            var decoded = DecodeBottomUp(EncodeDirect(buffer, width, height), width, height);

            // DecodeBottomUp の行順はバッファと同一なので、位置がそのまま一致すれば
            // 「バッファ末尾行 = PNG 先頭行 (画像最上段)」が成立している
            var topLeft = decoded[(height - 1) * width];
            Assert.That((topLeft.r, topLeft.g, topLeft.b), Is.EqualTo(((byte)255, (byte)0, (byte)0)),
                "バッファ末尾行が画像最上段に現れない (行順がボトムアップでない)");
            var topRight = decoded[(height - 1) * width + width - 1];
            Assert.That((topRight.r, topRight.g, topRight.b), Is.EqualTo(((byte)0, (byte)255, (byte)0)),
                "末尾行右端の水平位置が一致しない");
            var bottomLeft = decoded[0];
            Assert.That((bottomLeft.r, bottomLeft.g, bottomLeft.b), Is.EqualTo(((byte)0, (byte)0, (byte)255)),
                "バッファ先頭行が画像最下段に現れない");
        }
    }
}
