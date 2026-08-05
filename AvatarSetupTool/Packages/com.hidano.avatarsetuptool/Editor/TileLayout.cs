using System;

namespace Hidano.AvatarSetupTool.Editor
{
    /// <summary>
    /// 静止画のタイル分割レイアウト。出力解像度・SSAA 倍率・レンダ辺長上限から
    /// 任意分割数の非一様タイル (端タイルは剰余サイズ) を決定的に算出する。
    /// 純粋な整数計算のみで Unity API を呼ばず、環境値は引数で受ける。
    ///
    /// 不変条件:
    /// - 全タイルの矩形は出力ピクセル空間を重複・欠落なく被覆する
    /// - 各タイルのレンダサイズ (辺長 × Factor) は辺長上限以下
    /// - タイル境界は常に出力ピクセル境界 (= SSAA 倍率 × 出力 px のレンダ境界) に整列する
    /// </summary>
    internal readonly struct TileLayout
    {
        private readonly int outputWidth;
        private readonly int outputHeight;

        /// <summary>標準タイルの一辺 (出力 px)。端タイルのみこれより小さい剰余サイズになる。</summary>
        private readonly int blockSide;

        /// <summary>実際に適用する SSAA 倍率 (縮退時は requested より小さい)。</summary>
        public int Factor { get; }

        /// <summary>要求した SSAA 倍率 (警告ログ用)。</summary>
        public int RequestedFactor { get; }

        public int TilesX { get; }

        public int TilesY { get; }

        /// <summary>単一パスかどうか (TilesX == 1 &amp;&amp; TilesY == 1)。</summary>
        public bool IsSinglePass => TilesX == 1 && TilesY == 1;

        /// <summary>タイル 1 枚の最大レンダピクセル数 (メモリ見積もり用)。</summary>
        public long MaxTileRenderPixels
        {
            get
            {
                long tileWidth = Math.Min(blockSide, outputWidth) * Factor;
                long tileHeight = Math.Min(blockSide, outputHeight) * Factor;
                return tileWidth * tileHeight;
            }
        }

        private TileLayout(
            int outputWidth, int outputHeight, int blockSide,
            int factor, int requestedFactor, int tilesX, int tilesY)
        {
            this.outputWidth = outputWidth;
            this.outputHeight = outputHeight;
            this.blockSide = blockSide;
            Factor = factor;
            RequestedFactor = requestedFactor;
            TilesX = tilesX;
            TilesY = tilesY;
        }

        /// <summary>
        /// レイアウトを算出する。outputWidth/Height は出力 PNG のピクセルサイズ、
        /// preferredFactor は解像度既定の SSAA 倍率、tileSideLimit はレンダ辺長上限 (px)。
        /// 同一入力に対し常に同一結果を返す (決定的)。
        /// </summary>
        public static TileLayout Compute(
            int outputWidth, int outputHeight, int preferredFactor, int tileSideLimit)
        {
            if (outputWidth <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(outputWidth), outputWidth, "正の値が必要です。");
            }

            if (outputHeight <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(outputHeight), outputHeight, "正の値が必要です。");
            }

            if (preferredFactor != 1 && preferredFactor != 2 && preferredFactor != 4)
            {
                throw new ArgumentOutOfRangeException(nameof(preferredFactor), preferredFactor, "1 / 2 / 4 のいずれかが必要です。");
            }

            if (tileSideLimit < TileSideLimits.MinBlockSide)
            {
                throw new ArgumentOutOfRangeException(nameof(tileSideLimit), tileSideLimit,
                    $"{TileSideLimits.MinBlockSide} 以上が必要です。");
            }

            var factor = preferredFactor;

            // タイル 1 辺のレンダサイズ blockSide * factor が上限以下になる最大の出力 px 幅
            var blockSide = tileSideLimit / factor;
            var tilesX = (outputWidth + blockSide - 1) / blockSide;
            var tilesY = (outputHeight + blockSide - 1) / blockSide;
            return new TileLayout(
                outputWidth, outputHeight, blockSide, factor, preferredFactor, tilesX, tilesY);
        }

        /// <summary>タイル (tx, ty) の出力ピクセル空間での矩形。端タイルは剰余サイズ。</summary>
        public TileRect GetTile(int tx, int ty)
        {
            if (tx < 0 || tx >= TilesX)
            {
                throw new ArgumentOutOfRangeException(nameof(tx), tx, $"0 以上 {TilesX} 未満が必要です。");
            }

            if (ty < 0 || ty >= TilesY)
            {
                throw new ArgumentOutOfRangeException(nameof(ty), ty, $"0 以上 {TilesY} 未満が必要です。");
            }

            var x = tx * blockSide;
            var y = ty * blockSide;
            return new TileRect(
                x, y, Math.Min(blockSide, outputWidth - x), Math.Min(blockSide, outputHeight - y));
        }
    }

    /// <summary>出力ピクセル空間のタイル矩形。</summary>
    internal readonly struct TileRect
    {
        public int X { get; }
        public int Y { get; }
        public int Width { get; }
        public int Height { get; }

        public TileRect(int x, int y, int width, int height)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }
    }

    /// <summary>タイル 1 枚のレンダ辺長上限を環境値から算出する。</summary>
    internal static class TileSideLimits
    {
        /// <summary>TDR と確保失敗を避けるレンダ辺長の安全上限 (px)。</summary>
        internal const int SafeTileSide = 4096;

        /// <summary>graphicsMemorySize 過小報告対策の下限クランプ (MB)。</summary>
        internal const int VramFloorMb = 1024;

        /// <summary>タイルの最小ブロック辺 (出力 px)。これを下回る縮退時のみ SSAA 倍率を下げる。</summary>
        internal const int MinBlockSide = 64;

        /// <summary>
        /// min(SafeTileSide, maxTextureSize, VRAM 予算由来の辺長) を返す。
        /// VRAM 予算 = max(graphicsMemoryMb, VramFloorMb) / 2、
        /// 辺長 = sqrt(予算 / 12 bytes/px) (タイル 1 枚あたり float16 カラー + 深度でおよそ 12 bytes/px)。
        /// </summary>
        internal static int Compute(int maxTextureSize, int graphicsMemoryMb)
        {
            var budgetBytes = Math.Max(graphicsMemoryMb, VramFloorMb) * 1024L * 1024L / 2;
            var vramSide = (int)Math.Sqrt(budgetBytes / 12);
            return Math.Min(SafeTileSide, Math.Min(maxTextureSize, vramSide));
        }
    }
}
