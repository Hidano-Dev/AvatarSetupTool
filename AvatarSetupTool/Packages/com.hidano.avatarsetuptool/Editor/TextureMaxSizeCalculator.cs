namespace Hidano.AvatarSetupTool.Editor
{
    /// <summary>
    /// テクスチャの実解像度から Unity の maxTextureSize を算出する純ロジック。
    /// </summary>
    internal static class TextureMaxSizeCalculator
    {
        /// <summary>maxTextureSize の選択肢 (昇順)。</summary>
        public static readonly int[] SizeOptions =
        {
            32, 64, 128, 256, 512, 1024, 2048, 4096, 8192, 16384
        };

        /// <summary>
        /// 幅・高さの長辺を包含する最小の選択肢を返す (上下限クランプ込み)。
        /// </summary>
        public static int Calculate(int width, int height)
        {
            var longestSide = width > height ? width : height;

            for (var i = 0; i < SizeOptions.Length; i++)
            {
                if (longestSide <= SizeOptions[i])
                {
                    return SizeOptions[i];
                }
            }

            return SizeOptions[SizeOptions.Length - 1];
        }
    }
}
