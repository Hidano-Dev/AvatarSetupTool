using System;
using UnityEngine;

namespace Hidano.AvatarSetupTool.Editor
{
    /// <summary>キャプチャの出力形式。</summary>
    public enum CaptureOutputFormat
    {
        ImagesOnly,
        Mp4,
        Gif,
    }

    /// <summary>ターンテーブル動画の回転速度(1 周にかける秒数)。</summary>
    public enum RotationSpeedPreset
    {
        Seconds5,
        Seconds10,
        Seconds20,
        Custom,
    }

    /// <summary>
    /// モデルキャプチャの設定。UI (<see cref="ModelCaptureWindow"/>) とロジック
    /// (<see cref="ModelCaptureService"/>) の間で受け渡す純粋なデータで、
    /// 将来 CLI から実行する場合もこのクラスを組み立てて
    /// <see cref="ModelCaptureService.Capture"/> へ渡せばよい。
    /// </summary>
    [Serializable]
    public sealed class CaptureSettings
    {
        public const int MinImageSize = 256;
        public const int MaxImageSize = 8192;
        public const string DefaultFileNamePattern = "<Target>_<Direction>_<View>";

        public CaptureOutputFormat format = CaptureOutputFormat.ImagesOnly;

        /// <summary>PNG の高さ (px)。幅はモデルのアスペクト比に応じて自動で広がる。</summary>
        public int imageSize = 2048;

        public RotationSpeedPreset rotationSpeed = RotationSpeedPreset.Seconds10;

        /// <summary><see cref="rotationSpeed"/> が Custom のときの 1 周あたりの秒数。</summary>
        public float customSecondsPerRotation = 10f;

        /// <summary>出力ファイル名のパターン。<see cref="CaptureFileName.Wildcards"/> を使用可能。</summary>
        public string fileNamePattern = DefaultFileNamePattern;

        public string outputRoot = string.Empty;

        /// <summary>ファイル名の &lt;Take&gt; に使う連番。撮影が成功するたびに +1 される。</summary>
        public int take = 1;

        public float SecondsPerRotation => rotationSpeed switch
        {
            RotationSpeedPreset.Seconds5 => 5f,
            RotationSpeedPreset.Seconds10 => 10f,
            RotationSpeedPreset.Seconds20 => 20f,
            _ => Mathf.Clamp(customSecondsPerRotation, 1f, 300f),
        };

        /// <summary>
        /// <see cref="imageSize"/> を範囲内かつ 4 の倍数に丸めた値。
        /// GIF/MP4 は PNG の 1/2 解像度で、H.264 が偶数解像度を要求するため 4 の倍数に揃える。
        /// </summary>
        public int NormalizedImageSize => Mathf.Clamp((imageSize + 2) / 4 * 4, MinImageSize, MaxImageSize);
    }
}
