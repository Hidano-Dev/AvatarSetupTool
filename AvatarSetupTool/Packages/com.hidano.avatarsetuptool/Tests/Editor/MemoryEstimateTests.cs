using System;
using NUnit.Framework;

namespace Hidano.AvatarSetupTool.Editor.Tests
{
    /// <summary>
    /// メモリ見積もり (環境値を引数化した EstimateRequiredBytes の実体) の検証。
    /// 新エンコード経路の実バッファ構成に基づく見積もりが現行 (旧経路) の見積もり式を
    /// 超えないこと、8K がメモリ予算比較で誤拒否されないこと、GIF 蓄積分が現行式の
    /// まま維持されることを代表設定で確認する。
    /// </summary>
    public class MemoryEstimateTests
    {
        // 代表環境: maxTextureSize 16384 / VRAM 8GB (tileSideLimit は安全上限 4096 になる)
        private const int MaxTextureSize = 16384;
        private const int GraphicsMemoryMb = 8192;

        /// <summary>
        /// 旧経路 (Texture2D + EncodeToPNG) 時代の見積もり式。置換前の実装を比較基準として保持する:
        /// RT + 読み戻し Texture2D + GetPixels32 + PNG エンコードバッファ (4 bytes/px × 4)
        /// + タイル 1 枚分の読み戻し (辺長 = min(imageSize × SSAA, maxTextureSize))。
        /// </summary>
        private static long LegacyEstimate(int imageSize, CaptureOutputFormat format, CaptureViewMode viewMode)
        {
            var viewCount = viewMode == CaptureViewMode.Both ? 2 : 1;
            var renderPixels = (long)imageSize * imageSize;
            var stillFactor = imageSize <= 2048 ? 4 : 2;
            var tileSide = Math.Min((long)imageSize * stillFactor, MaxTextureSize);
            var bytes = renderPixels * 4 * 4 + tileSide * tileSide * 4 * 2;
            var animPixels = renderPixels
                / (ModelCaptureService.SuperSampleFactor * ModelCaptureService.SuperSampleFactor);
            if (format == CaptureOutputFormat.Gif)
            {
                // 8 方向 + 量子化 2 フレーム分 (現行実装の Directions.Length + 2 と同値)
                bytes += animPixels * 4 * (8 + 2) * viewCount;
            }
            else if (format == CaptureOutputFormat.Mp4 || format == CaptureOutputFormat.ProRes422)
            {
                bytes += animPixels * 4 * viewCount;
            }

            return bytes;
        }

        [TestCase(8192, CaptureOutputFormat.Gif, CaptureViewMode.Both)]
        [TestCase(8192, CaptureOutputFormat.ImagesOnly, CaptureViewMode.Both)]
        [TestCase(8192, CaptureOutputFormat.ProRes422, CaptureViewMode.Both)]
        [TestCase(4096, CaptureOutputFormat.Mp4, CaptureViewMode.Both)]
        [TestCase(2048, CaptureOutputFormat.Gif, CaptureViewMode.FullOnly)]
        [TestCase(1024, CaptureOutputFormat.ImagesOnly, CaptureViewMode.FullOnly)]
        public void NewEstimate_RepresentativeSettings_NotLargerThanLegacy(
            int imageSize, CaptureOutputFormat format, CaptureViewMode viewMode)
        {
            var estimate = ModelCaptureService.EstimateRequiredBytes(
                imageSize, format, viewMode, MaxTextureSize, GraphicsMemoryMb);

            Assert.That(estimate, Is.LessThanOrEqualTo(LegacyEstimate(imageSize, format, viewMode)));
        }

        [Test]
        public void Estimate_8K_BothGif_NotRejectedBy8GbRamBudget()
        {
            // 実装メモリ 8GB → 予算はその半分 (MemoryBudgetBytes と同じ算定)。
            // 旧式では 8K/Both/GIF が予算超過で誤拒否されていた環境で、新式は予算内に収まる
            var budget = 8192L * 1024 * 1024 / 2;
            var estimate = ModelCaptureService.EstimateRequiredBytes(
                8192, CaptureOutputFormat.Gif, CaptureViewMode.Both, MaxTextureSize, GraphicsMemoryMb);

            Assert.That(estimate, Is.LessThanOrEqualTo(budget), "新見積もりが 8K を誤拒否しないこと");
            Assert.That(LegacyEstimate(8192, CaptureOutputFormat.Gif, CaptureViewMode.Both),
                Is.GreaterThan(budget), "旧式では拒否されていた設定であること (テスト前提の確認)");
        }

        [Test]
        public void Estimate_Still_MatchesNewBufferComposition()
        {
            // 8K の静止画ピーク = RGB24 合成バッファ (3 bytes/px) + PNG 出力 + iTXt コピー
            // (1 byte/px × 2) + タイル読み戻し (4096px 級タイル × 4 bytes/px × 2)。
            // タイルサイズはレイアウト計算 (blockSide 2048 × 適用倍率 2 = 4096) と一致する
            var renderPixels = 8192L * 8192L;
            var expected = renderPixels * (3 + 1 * 2) + 4096L * 4096L * 4 * 2;

            Assert.That(
                ModelCaptureService.EstimateRequiredBytes(
                    8192, CaptureOutputFormat.ImagesOnly, CaptureViewMode.FullOnly,
                    MaxTextureSize, GraphicsMemoryMb),
                Is.EqualTo(expected));
        }

        [Test]
        public void Estimate_GifAccumulation_KeepsLegacyFormula()
        {
            // GIF 蓄積分 (ImagesOnly との差分) は現行式のまま維持される
            long GifPart(Func<int, CaptureOutputFormat, CaptureViewMode, long> estimate)
            {
                return estimate(8192, CaptureOutputFormat.Gif, CaptureViewMode.Both)
                    - estimate(8192, CaptureOutputFormat.ImagesOnly, CaptureViewMode.Both);
            }

            var newGifPart = GifPart((size, format, viewMode) =>
                ModelCaptureService.EstimateRequiredBytes(
                    size, format, viewMode, MaxTextureSize, GraphicsMemoryMb));

            Assert.That(newGifPart, Is.EqualTo(GifPart(LegacyEstimate)));
        }

        [Test]
        public void Estimate_SmallMaxTextureSize_ShrinksTileTerm()
        {
            // タイル読み戻し項が環境値由来のレイアウト (辺長上限) に追随することを確認する。
            // maxTextureSize 2048 では 4096px 級タイルが確保できないため見積もりが小さくなる
            var constrained = ModelCaptureService.EstimateRequiredBytes(
                8192, CaptureOutputFormat.ImagesOnly, CaptureViewMode.FullOnly,
                maxTextureSize: 2048, graphicsMemoryMb: GraphicsMemoryMb);
            var normal = ModelCaptureService.EstimateRequiredBytes(
                8192, CaptureOutputFormat.ImagesOnly, CaptureViewMode.FullOnly,
                MaxTextureSize, GraphicsMemoryMb);

            var renderPixels = 8192L * 8192L;
            Assert.That(constrained, Is.EqualTo(renderPixels * (3 + 1 * 2) + 2048L * 2048L * 4 * 2));
            Assert.That(constrained, Is.LessThan(normal));
        }
    }
}
