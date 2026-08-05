using System;
using NUnit.Framework;

namespace Hidano.AvatarSetupTool.Editor.Tests
{
    /// <summary>
    /// TileLayout / TileSideLimits の単体テスト。
    /// 境界ケース (8K の 4×4 分割・非正方形・剰余タイル・単一パス)、
    /// 縮退条件での SSAA 倍率降格と要求/適用倍率の報告、
    /// 被覆・非重複・境界整列・辺長上限遵守の不変条件を検証する。
    /// </summary>
    public class TileLayoutTests
    {
        [Test]
        public void Compute_8K_Ssaa2_Limit4096_Yields4x4()
        {
            // 8192px × SSAA2 / 上限 4096 → ブロック辺 2048 出力 px → 4×4 分割
            var layout = TileLayout.Compute(8192, 8192, preferredFactor: 2, tileSideLimit: 4096);

            Assert.That(layout.TilesX, Is.EqualTo(4));
            Assert.That(layout.TilesY, Is.EqualTo(4));
            Assert.That(layout.Factor, Is.EqualTo(2));
            Assert.That(layout.IsSinglePass, Is.False);

            for (var ty = 0; ty < layout.TilesY; ty++)
            {
                for (var tx = 0; tx < layout.TilesX; tx++)
                {
                    var rect = layout.GetTile(tx, ty);
                    Assert.That(rect.Width, Is.EqualTo(2048), $"width at ({tx},{ty})");
                    Assert.That(rect.Height, Is.EqualTo(2048), $"height at ({tx},{ty})");
                    Assert.That(rect.Width * layout.Factor, Is.LessThanOrEqualTo(4096));
                }
            }

            Assert.That(layout.MaxTileRenderPixels, Is.EqualTo(4096L * 4096L));
        }

        [Test]
        public void Compute_NonSquare_ProducesRemainderEdgeTiles()
        {
            // 7680×4320 × SSAA2 / 上限 4096 → ブロック辺 2048:
            // X = 3×2048 + 剰余 1536 → 4 列、Y = 2×2048 + 剰余 224 → 3 行
            var layout = TileLayout.Compute(7680, 4320, preferredFactor: 2, tileSideLimit: 4096);

            Assert.That(layout.TilesX, Is.EqualTo(4));
            Assert.That(layout.TilesY, Is.EqualTo(3));

            var interior = layout.GetTile(0, 0);
            Assert.That(interior.Width, Is.EqualTo(2048));
            Assert.That(interior.Height, Is.EqualTo(2048));

            var rightEdge = layout.GetTile(3, 0);
            Assert.That(rightEdge.X, Is.EqualTo(3 * 2048));
            Assert.That(rightEdge.Width, Is.EqualTo(7680 - 3 * 2048)); // 剰余 1536

            var topEdge = layout.GetTile(0, 2);
            Assert.That(topEdge.Y, Is.EqualTo(2 * 2048));
            Assert.That(topEdge.Height, Is.EqualTo(4320 - 2 * 2048)); // 剰余 224

            var corner = layout.GetTile(3, 2);
            Assert.That(corner.Width, Is.EqualTo(1536));
            Assert.That(corner.Height, Is.EqualTo(224));
        }

        [Test]
        public void Compute_OutputWithinLimit_IsSinglePass()
        {
            // 1024px × SSAA4 = レンダ 4096 = 上限ちょうど → 単一パス
            var layout = TileLayout.Compute(1024, 1024, preferredFactor: 4, tileSideLimit: 4096);

            Assert.That(layout.IsSinglePass, Is.True);
            Assert.That(layout.TilesX, Is.EqualTo(1));
            Assert.That(layout.TilesY, Is.EqualTo(1));
            Assert.That(layout.Factor, Is.EqualTo(4));

            var rect = layout.GetTile(0, 0);
            Assert.That(rect.X, Is.EqualTo(0));
            Assert.That(rect.Y, Is.EqualTo(0));
            Assert.That(rect.Width, Is.EqualTo(1024));
            Assert.That(rect.Height, Is.EqualTo(1024));
            Assert.That(layout.MaxTileRenderPixels, Is.EqualTo(4096L * 4096L));
        }

        [Test]
        public void Compute_OneOverBlockSide_SplitsIntoTwo()
        {
            // ブロック辺 2048 を 1px 超えたら 2 分割 (剰余タイルは 1px)
            var layout = TileLayout.Compute(2049, 2048, preferredFactor: 2, tileSideLimit: 4096);

            Assert.That(layout.TilesX, Is.EqualTo(2));
            Assert.That(layout.TilesY, Is.EqualTo(1));
            Assert.That(layout.IsSinglePass, Is.False);
            Assert.That(layout.GetTile(1, 0).Width, Is.EqualTo(1));
        }

        [TestCase(4, 100, 1)]  // 100/4=25, 100/2=50 とも 64 未満 → 1 まで降格
        [TestCase(4, 200, 2)]  // 200/4=50 < 64、200/2=100 ≥ 64 → 2 で停止
        [TestCase(2, 127, 1)]  // 127/2=63 < 64 → 1 へ降格
        [TestCase(2, 128, 2)]  // 128/2=64 ちょうど → 降格しない
        public void Compute_DegenerateLimit_DowngradesFactorStepwise(
            int preferredFactor, int tileSideLimit, int expectedFactor)
        {
            var layout = TileLayout.Compute(512, 512, preferredFactor, tileSideLimit);

            Assert.That(layout.Factor, Is.EqualTo(expectedFactor));
            Assert.That(layout.RequestedFactor, Is.EqualTo(preferredFactor),
                "縮退時も要求倍率は警告ログ用に保持される");
        }

        [TestCase(8192, 8192, 2, 4096)]
        [TestCase(1024, 1024, 4, 4096)]
        [TestCase(2048, 2048, 4, 4096)]
        public void Compute_NormalEnvironment_KeepsPreferredFactor(
            int width, int height, int preferredFactor, int tileSideLimit)
        {
            var layout = TileLayout.Compute(width, height, preferredFactor, tileSideLimit);

            Assert.That(layout.Factor, Is.EqualTo(preferredFactor));
            Assert.That(layout.RequestedFactor, Is.EqualTo(preferredFactor));
        }

        [TestCase(8192, 8192, 2, 4096)]   // 8K 正方形 (均等 4×4)
        [TestCase(7680, 4320, 2, 4096)]   // 非正方形 + 両軸に剰余タイル
        [TestCase(8192, 2048, 2, 4096)]   // 横長 (Y は単一)
        [TestCase(513, 257, 2, 512)]      // 剰余 1px の極端ケース
        [TestCase(1024, 1024, 4, 4096)]   // 単一パス
        [TestCase(2049, 2048, 2, 4096)]   // ブロック辺 +1px
        [TestCase(512, 512, 4, 100)]      // 縮退 (倍率 1 へ降格)
        [TestCase(512, 512, 4, 200)]      // 縮退 (倍率 2 へ降格)
        [TestCase(64, 64, 1, 64)]         // 最小入力
        public void Compute_Invariants_CoverageAlignmentAndSideLimit(
            int width, int height, int preferredFactor, int tileSideLimit)
        {
            var layout = TileLayout.Compute(width, height, preferredFactor, tileSideLimit);

            // 被覆・非重複: 全タイル矩形で出力ピクセルを塗り、全画素がちょうど 1 回塗られる
            var coverage = new int[width * height];
            long maxRenderPixels = 0;
            for (var ty = 0; ty < layout.TilesY; ty++)
            {
                for (var tx = 0; tx < layout.TilesX; tx++)
                {
                    var rect = layout.GetTile(tx, ty);

                    Assert.That(rect.Width, Is.GreaterThan(0), $"width at ({tx},{ty})");
                    Assert.That(rect.Height, Is.GreaterThan(0), $"height at ({tx},{ty})");

                    // 辺長上限遵守: レンダサイズ (出力 px × Factor) が上限以下
                    Assert.That(rect.Width * layout.Factor, Is.LessThanOrEqualTo(tileSideLimit),
                        $"render width at ({tx},{ty})");
                    Assert.That(rect.Height * layout.Factor, Is.LessThanOrEqualTo(tileSideLimit),
                        $"render height at ({tx},{ty})");

                    // 境界整列: 矩形は出力ピクセル境界の整数座標で定義され、
                    // レンダ境界 (× Factor) も整数になる (整数矩形なので常に整列)
                    Assert.That(rect.X, Is.GreaterThanOrEqualTo(0));
                    Assert.That(rect.Y, Is.GreaterThanOrEqualTo(0));
                    Assert.That(rect.X + rect.Width, Is.LessThanOrEqualTo(width));
                    Assert.That(rect.Y + rect.Height, Is.LessThanOrEqualTo(height));

                    for (var y = rect.Y; y < rect.Y + rect.Height; y++)
                    {
                        for (var x = rect.X; x < rect.X + rect.Width; x++)
                        {
                            coverage[y * width + x]++;
                        }
                    }

                    maxRenderPixels = Math.Max(maxRenderPixels,
                        (long)rect.Width * layout.Factor * rect.Height * layout.Factor);
                }
            }

            for (var i = 0; i < coverage.Length; i++)
            {
                Assert.That(coverage[i], Is.EqualTo(1), $"pixel ({i % width},{i / width}) coverage");
            }

            // MaxTileRenderPixels は実タイルの最大レンダピクセル数と一致する
            Assert.That(layout.MaxTileRenderPixels, Is.EqualTo(maxRenderPixels));
        }

        [Test]
        public void Compute_SameInput_IsDeterministic()
        {
            var a = TileLayout.Compute(7680, 4320, 2, 4096);
            var b = TileLayout.Compute(7680, 4320, 2, 4096);

            Assert.That(b.TilesX, Is.EqualTo(a.TilesX));
            Assert.That(b.TilesY, Is.EqualTo(a.TilesY));
            Assert.That(b.Factor, Is.EqualTo(a.Factor));
            for (var ty = 0; ty < a.TilesY; ty++)
            {
                for (var tx = 0; tx < a.TilesX; tx++)
                {
                    var ra = a.GetTile(tx, ty);
                    var rb = b.GetTile(tx, ty);
                    Assert.That(rb.X, Is.EqualTo(ra.X));
                    Assert.That(rb.Y, Is.EqualTo(ra.Y));
                    Assert.That(rb.Width, Is.EqualTo(ra.Width));
                    Assert.That(rb.Height, Is.EqualTo(ra.Height));
                }
            }
        }

        [TestCase(0, 512)]
        [TestCase(512, 0)]
        [TestCase(-1, 512)]
        public void Compute_NonPositiveOutput_Throws(int width, int height)
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => TileLayout.Compute(width, height, 2, 4096));
        }

        [TestCase(0)]
        [TestCase(3)]
        [TestCase(8)]
        public void Compute_InvalidPreferredFactor_Throws(int preferredFactor)
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => TileLayout.Compute(512, 512, preferredFactor, 4096));
        }

        [Test]
        public void Compute_TileSideLimitBelowMinBlockSide_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => TileLayout.Compute(512, 512, 2, TileSideLimits.MinBlockSide - 1));
        }

        [TestCase(-1, 0)]
        [TestCase(0, -1)]
        [TestCase(4, 0)]
        [TestCase(0, 4)]
        public void GetTile_OutOfRange_Throws(int tx, int ty)
        {
            var layout = TileLayout.Compute(8192, 8192, 2, 4096); // 4×4
            Assert.Throws<ArgumentOutOfRangeException>(() => layout.GetTile(tx, ty));
        }

        [TestCase(2048, 2048, 4)]  // 最長辺 2048 以下 → 4
        [TestCase(512, 2048, 4)]
        [TestCase(2049, 512, 2)]   // 最長辺 2048 超 → 2
        [TestCase(7680, 4320, 2)]
        public void PreferredFactor_FollowsLongestSideRule(int width, int height, int expected)
        {
            Assert.That(TileLayout.PreferredFactor(width, height), Is.EqualTo(expected));
        }

        [Test]
        public void TileSideLimits_NormalEnvironment_ReturnsSafeTileSide()
        {
            // 高スペック環境では安全上限 4096 が支配する
            Assert.That(TileSideLimits.Compute(maxTextureSize: 16384, graphicsMemoryMb: 24576),
                Is.EqualTo(TileSideLimits.SafeTileSide));
        }

        [Test]
        public void TileSideLimits_SmallMaxTextureSize_IsRespected()
        {
            Assert.That(TileSideLimits.Compute(maxTextureSize: 2048, graphicsMemoryMb: 8192),
                Is.EqualTo(2048));
        }

        [Test]
        public void TileSideLimits_UnderReportedVram_IsClampedByFloor()
        {
            // graphicsMemorySize の過小報告 (128MB) でも下限クランプ 1024MB により
            // VRAM 由来の辺長は安全上限を下回らない
            Assert.That(TileSideLimits.Compute(maxTextureSize: 16384, graphicsMemoryMb: 128),
                Is.EqualTo(TileSideLimits.SafeTileSide));
        }

        [Test]
        public void TileSideLimits_FloorValue_MatchesLowVramResult()
        {
            // 下限クランプにより VramFloorMb 以下の報告値はすべて同じ結果になる
            var atFloor = TileSideLimits.Compute(16384, TileSideLimits.VramFloorMb);
            var belowFloor = TileSideLimits.Compute(16384, 1);
            Assert.That(belowFloor, Is.EqualTo(atFloor));
        }
    }
}
