using NUnit.Framework;
using UnityEngine;

namespace Hidano.AvatarSetupTool.Editor.Tests
{
    /// <summary>
    /// 現行のボックス平均縮小 (DownscaleInto / Downscale) の挙動固定テスト。
    /// 丸め式 (samples/2 加算の四捨五入)・アルファ 255 固定・タイル分割合成の同値性・
    /// topDown 反転を固定し、後続の RGB24 経路置換における同値性比較の基準にする。
    /// </summary>
    public class DownscaleTests
    {
        /// <summary>決定的な擬似乱数ピクセル列 (LCG)。テストの再現性のため Unity の Random は使わない。</summary>
        private static Color32[] MakePixels(int count, uint seed)
        {
            var pixels = new Color32[count];
            var state = seed;
            for (var i = 0; i < count; i++)
            {
                state = state * 1664525u + 1013904223u;
                pixels[i] = new Color32(
                    (byte)(state >> 24), (byte)(state >> 16), (byte)(state >> 8), (byte)state);
            }

            return pixels;
        }

        /// <summary>現行実装と独立に書いたボックス平均の参照実装 (samples/2 加算の四捨五入)。</summary>
        private static Color32 ReferenceAverage(Color32[] source, int sourceWidth, int factor, int x, int y)
        {
            var samples = factor * factor;
            int r = 0, g = 0, b = 0;
            for (var dy = 0; dy < factor; dy++)
            {
                for (var dx = 0; dx < factor; dx++)
                {
                    var p = source[(y * factor + dy) * sourceWidth + x * factor + dx];
                    r += p.r;
                    g += p.g;
                    b += p.b;
                }
            }

            return new Color32(
                (byte)((r + samples / 2) / samples),
                (byte)((g + samples / 2) / samples),
                (byte)((b + samples / 2) / samples),
                255);
        }

        private static void AssertPixelsEqual(Color32[] expected, Color32[] actual)
        {
            Assert.That(actual.Length, Is.EqualTo(expected.Length));
            for (var i = 0; i < expected.Length; i++)
            {
                Assert.That(actual[i].r, Is.EqualTo(expected[i].r), $"r at {i}");
                Assert.That(actual[i].g, Is.EqualTo(expected[i].g), $"g at {i}");
                Assert.That(actual[i].b, Is.EqualTo(expected[i].b), $"b at {i}");
                Assert.That(actual[i].a, Is.EqualTo(expected[i].a), $"a at {i}");
            }
        }

        [Test]
        public void DownscaleInto_RoundsWithSamplesHalfBias()
        {
            // factor=2 (samples=4): (sum + 2) / 4 の切り捨て = 0.5 以上で切り上げ
            var cases = new (byte[] values, byte expected)[]
            {
                (new byte[] { 0, 0, 0, 0 }, 0),     // sum 0   -> 0
                (new byte[] { 0, 0, 0, 1 }, 0),     // sum 1   -> (1+2)/4 = 0
                (new byte[] { 0, 0, 1, 1 }, 1),     // sum 2   -> (2+2)/4 = 1 (半分は切り上げ)
                (new byte[] { 0, 1, 1, 1 }, 1),     // sum 3   -> (3+2)/4 = 1
                (new byte[] { 1, 1, 1, 1 }, 1),     // sum 4   -> 1
                (new byte[] { 255, 255, 255, 253 }, 255), // sum 1018 -> (1018+2)/4 = 255
                (new byte[] { 255, 255, 255, 255 }, 255),
            };

            foreach (var (values, expected) in cases)
            {
                var source = new Color32[4];
                for (var i = 0; i < 4; i++)
                {
                    source[i] = new Color32(values[i], values[i], values[i], 255);
                }

                var dest = new Color32[1];
                ModelCaptureService.DownscaleInto(source, 2, 2, dest, 1, 0, 0, 1, 1);

                Assert.That(dest[0].r, Is.EqualTo(expected), $"input sum case {string.Join(",", values)}");
                Assert.That(dest[0].g, Is.EqualTo(expected));
                Assert.That(dest[0].b, Is.EqualTo(expected));
            }
        }

        [Test]
        public void DownscaleInto_ForcesAlphaTo255()
        {
            var source = new Color32[4];
            for (var i = 0; i < 4; i++)
            {
                source[i] = new Color32(10, 20, 30, 0); // 入力アルファ 0 でも出力は不透明
            }

            var dest = new Color32[1];
            ModelCaptureService.DownscaleInto(source, 2, 2, dest, 1, 0, 0, 1, 1);

            Assert.That(dest[0].a, Is.EqualTo(255));
        }

        [TestCase(2, 16, 12, 12345u)]
        [TestCase(4, 16, 16, 67890u)]
        [TestCase(1, 8, 8, 24680u)]
        public void DownscaleInto_MatchesReferenceImplementation(int factor, int sourceWidth, int sourceHeight, uint seed)
        {
            var source = MakePixels(sourceWidth * sourceHeight, seed);
            var destWidth = sourceWidth / factor;
            var destHeight = sourceHeight / factor;

            var actual = new Color32[destWidth * destHeight];
            ModelCaptureService.DownscaleInto(source, sourceWidth, factor,
                actual, destWidth, 0, 0, destWidth, destHeight);

            var expected = new Color32[destWidth * destHeight];
            for (var y = 0; y < destHeight; y++)
            {
                for (var x = 0; x < destWidth; x++)
                {
                    expected[y * destWidth + x] = ReferenceAverage(source, sourceWidth, factor, x, y);
                }
            }

            AssertPixelsEqual(expected, actual);
        }

        [Test]
        public void DownscaleInto_TiledComposition_MatchesSingleDownscale()
        {
            // RenderStill と同じ合成手順: タイル毎の部分ソースを destX/destY 指定で合成した結果が
            // 全域を一括縮小した結果と全画素一致する (置換後の同値性比較の基準)
            const int factor = 2;
            const int destWidth = 8;
            const int destHeight = 8;
            const int sourceWidth = destWidth * factor;
            const int sourceHeight = destHeight * factor;
            var source = MakePixels(sourceWidth * sourceHeight, 55555u);

            var single = new Color32[destWidth * destHeight];
            ModelCaptureService.DownscaleInto(source, sourceWidth, factor,
                single, destWidth, 0, 0, destWidth, destHeight);

            const int tilesX = 2;
            const int tilesY = 2;
            const int blockWidth = destWidth / tilesX;
            const int blockHeight = destHeight / tilesY;
            var tiled = new Color32[destWidth * destHeight];
            for (var ty = 0; ty < tilesY; ty++)
            {
                for (var tx = 0; tx < tilesX; tx++)
                {
                    // タイル描画で読み戻される部分ソース (行順は全域ソースと同じ向き) を切り出す
                    var tileSource = new Color32[blockWidth * factor * blockHeight * factor];
                    for (var row = 0; row < blockHeight * factor; row++)
                    {
                        var srcRow = ty * blockHeight * factor + row;
                        for (var col = 0; col < blockWidth * factor; col++)
                        {
                            tileSource[row * blockWidth * factor + col] =
                                source[srcRow * sourceWidth + tx * blockWidth * factor + col];
                        }
                    }

                    ModelCaptureService.DownscaleInto(tileSource, blockWidth * factor, factor,
                        tiled, destWidth, tx * blockWidth, ty * blockHeight, blockWidth, blockHeight);
                }
            }

            AssertPixelsEqual(single, tiled);
        }

        [Test]
        public void Downscale_BottomUp_KeepsRowOrder()
        {
            // 下半分 (ボトムアップ先頭側) を 40、上半分を 200 にした 4x4 を 2x2 へ縮小
            var source = new Color32[16];
            for (var i = 0; i < 16; i++)
            {
                var value = i < 8 ? (byte)40 : (byte)200;
                source[i] = new Color32(value, value, value, 255);
            }

            var result = ModelCaptureService.Downscale(source, 4, 2, 2, topDown: false);

            Assert.That(result[0].r, Is.EqualTo(40));  // 先頭行 = 下端のまま
            Assert.That(result[1].r, Is.EqualTo(40));
            Assert.That(result[2].r, Is.EqualTo(200));
            Assert.That(result[3].r, Is.EqualTo(200));
        }

        [Test]
        public void Downscale_TopDown_FlipsRows()
        {
            var source = new Color32[16];
            for (var i = 0; i < 16; i++)
            {
                var value = i < 8 ? (byte)40 : (byte)200;
                source[i] = new Color32(value, value, value, 255);
            }

            var result = ModelCaptureService.Downscale(source, 4, 2, 2, topDown: true);

            Assert.That(result[0].r, Is.EqualTo(200)); // 先頭行 = 上端 (GIF 用の行順)
            Assert.That(result[1].r, Is.EqualTo(200));
            Assert.That(result[2].r, Is.EqualTo(40));
            Assert.That(result[3].r, Is.EqualTo(40));
        }

        // ---- 4.2: RGB24 合成バッファ向け縮小合成ヘルパ ----
        // 合成バッファはボトムアップ行順 (EncodeArrayToPNG の行順が PngEncodeTests で
        // ボトムアップと確定したことに追随)。タイル読み戻し (ボトムアップ) からの合成は
        // 行反転なし、GIF 用 top-down への反転は DownscaleRgb24ToColor32 の 1 箇所のみ。

        /// <summary>Color32 ピクセル列をアルファを落とした RGB24 バッファへ変換する (行順は不変)。</summary>
        private static byte[] ToRgb24(Color32[] pixels)
        {
            var buffer = new byte[pixels.Length * 3];
            for (var i = 0; i < pixels.Length; i++)
            {
                buffer[i * 3] = pixels[i].r;
                buffer[i * 3 + 1] = pixels[i].g;
                buffer[i * 3 + 2] = pixels[i].b;
            }

            return buffer;
        }

        private static void AssertRgb24EqualsColor32(Color32[] expected, byte[] actual)
        {
            Assert.That(actual.Length, Is.EqualTo(expected.Length * 3));
            for (var i = 0; i < expected.Length; i++)
            {
                Assert.That(actual[i * 3], Is.EqualTo(expected[i].r), $"r at {i}");
                Assert.That(actual[i * 3 + 1], Is.EqualTo(expected[i].g), $"g at {i}");
                Assert.That(actual[i * 3 + 2], Is.EqualTo(expected[i].b), $"b at {i}");
            }
        }

        [TestCase(2, 16, 12, 12345u)]
        [TestCase(4, 16, 16, 67890u)]
        [TestCase(1, 8, 8, 24680u)]
        public void DownscaleIntoRgb24_MatchesColor32Baseline(int factor, int sourceWidth, int sourceHeight, uint seed)
        {
            // 丸め式が現行 DownscaleInto (1.2 の基準テスト対象) と同一であることを、
            // 同一入力の全画素チャネル一致で検証する
            var source = MakePixels(sourceWidth * sourceHeight, seed);
            var destWidth = sourceWidth / factor;
            var destHeight = sourceHeight / factor;

            var baseline = new Color32[destWidth * destHeight];
            ModelCaptureService.DownscaleInto(source, sourceWidth, factor,
                baseline, destWidth, 0, 0, destWidth, destHeight);

            var actual = new byte[destWidth * destHeight * 3];
            ModelCaptureService.DownscaleIntoRgb24(source, sourceWidth, factor,
                actual, destWidth, 0, 0, destWidth, destHeight);

            AssertRgb24EqualsColor32(baseline, actual);
        }

        [Test]
        public void DownscaleIntoRgb24_TiledComposition_MatchesSingleDownscale()
        {
            // 剰余タイルを含む非一様分割 (幅 4+4+2、高さ 4+2) の合成が全域一括縮小と
            // 全画素一致し、かつ現行 DownscaleInto の基準結果とも一致する
            const int factor = 2;
            const int destWidth = 10;
            const int destHeight = 6;
            const int sourceWidth = destWidth * factor;
            const int sourceHeight = destHeight * factor;
            var source = MakePixels(sourceWidth * sourceHeight, 13579u);

            var single = new byte[destWidth * destHeight * 3];
            ModelCaptureService.DownscaleIntoRgb24(source, sourceWidth, factor,
                single, destWidth, 0, 0, destWidth, destHeight);

            var baseline = new Color32[destWidth * destHeight];
            ModelCaptureService.DownscaleInto(source, sourceWidth, factor,
                baseline, destWidth, 0, 0, destWidth, destHeight);
            AssertRgb24EqualsColor32(baseline, single);

            var blockXs = new[] { (0, 4), (4, 4), (8, 2) };
            var blockYs = new[] { (0, 4), (4, 2) };
            var tiled = new byte[destWidth * destHeight * 3];
            foreach (var (destY, blockHeight) in blockYs)
            {
                foreach (var (destX, blockWidth) in blockXs)
                {
                    // タイル描画で読み戻される部分ソース (行順は全域ソースと同じ向き) を切り出す
                    var tileSource = new Color32[blockWidth * factor * blockHeight * factor];
                    for (var row = 0; row < blockHeight * factor; row++)
                    {
                        var srcRow = destY * factor + row;
                        for (var col = 0; col < blockWidth * factor; col++)
                        {
                            tileSource[row * blockWidth * factor + col] =
                                source[srcRow * sourceWidth + destX * factor + col];
                        }
                    }

                    ModelCaptureService.DownscaleIntoRgb24(tileSource, blockWidth * factor, factor,
                        tiled, destWidth, destX, destY, blockWidth, blockHeight);
                }
            }

            Assert.That(tiled, Is.EqualTo(single), "タイル分割合成が全域一括縮小と一致しない");
        }

        [TestCase(2, 8, 12, 97531u)]
        [TestCase(1, 6, 4, 86420u)]
        public void DownscaleRgb24ToColor32_MatchesCurrentGifDownscale(
            int factor, int destWidth, int destHeight, uint seed)
        {
            // GIF フレームの現行同値性: 同一画像に対し現行 Downscale(topDown: true) と
            // 全画素一致する (行反転位置の移動で画素値が変わらないことの検証)
            var sourceWidth = destWidth * factor;
            var sourceHeight = destHeight * factor;
            var sourcePixels = MakePixels(sourceWidth * sourceHeight, seed);

            var expected = ModelCaptureService.Downscale(
                sourcePixels, sourceWidth, destWidth, destHeight, topDown: true);

            var actual = ModelCaptureService.DownscaleRgb24ToColor32(
                ToRgb24(sourcePixels), sourceWidth, sourceHeight, destWidth, destHeight);

            AssertPixelsEqual(expected, actual);
        }

        [Test]
        public void DownscaleRgb24ToColor32_FlipsRowsToTopDown()
        {
            // 下半分 (ボトムアップ先頭側) を 40、上半分を 200 にした 4x4 RGB24 を 2x2 へ縮小
            var source = new byte[4 * 4 * 3];
            for (var i = 0; i < 16; i++)
            {
                var value = i < 8 ? (byte)40 : (byte)200;
                source[i * 3] = value;
                source[i * 3 + 1] = value;
                source[i * 3 + 2] = value;
            }

            var result = ModelCaptureService.DownscaleRgb24ToColor32(source, 4, 4, 2, 2);

            Assert.That(result[0].r, Is.EqualTo(200)); // 先頭行 = 上端 (GIF 用の行順)
            Assert.That(result[1].r, Is.EqualTo(200));
            Assert.That(result[2].r, Is.EqualTo(40));
            Assert.That(result[3].r, Is.EqualTo(40));
            Assert.That(result[0].a, Is.EqualTo(255));
        }

        [Test]
        public void Downscale_TopDown_MatchesReferenceOnRandomInput()
        {
            const int destWidth = 4;
            const int destHeight = 6;
            const int factor = 2;
            const int sourceWidth = destWidth * factor;
            var source = MakePixels(sourceWidth * destHeight * factor, 97531u);

            var actual = ModelCaptureService.Downscale(source, sourceWidth, destWidth, destHeight, topDown: true);

            for (var y = 0; y < destHeight; y++)
            {
                for (var x = 0; x < destWidth; x++)
                {
                    // topDown 出力の行 y はボトムアップ縮小結果の行 (destHeight-1-y)
                    var expected = ReferenceAverage(source, sourceWidth, factor, x, destHeight - 1 - y);
                    var p = actual[y * destWidth + x];
                    Assert.That(p.r, Is.EqualTo(expected.r), $"r at ({x},{y})");
                    Assert.That(p.g, Is.EqualTo(expected.g), $"g at ({x},{y})");
                    Assert.That(p.b, Is.EqualTo(expected.b), $"b at ({x},{y})");
                    Assert.That(p.a, Is.EqualTo(255));
                }
            }
        }
    }
}
