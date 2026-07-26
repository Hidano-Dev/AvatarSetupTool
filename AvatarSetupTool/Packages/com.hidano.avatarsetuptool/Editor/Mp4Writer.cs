using System;
using UnityEditor.Media;
using UnityEngine;

namespace Hidano.AvatarSetupTool.Editor
{
    /// <summary>
    /// UnityEditor.Media.MediaEncoder で H.264 の MP4 を書き出す薄いラッパー。
    /// エディタの編集モード内で完結し、Play モードや外部ツール(ffmpeg 等)は不要。
    /// フレームは AddFrame へ 1 枚ずつ渡して逐次エンコードする(全フレームをメモリに溜めない)。
    /// ピクセルは Texture2D.SetPixels32 と同じボトムアップの行順で渡すこと。
    /// </summary>
    internal sealed class Mp4Writer : IDisposable
    {
        private readonly MediaEncoder encoder;
        private readonly Texture2D frameTexture;

        public Mp4Writer(string filePath, int width, int height, int frameRate)
        {
            var attributes = new VideoTrackEncoderAttributes(new H264EncoderAttributes
            {
                gopSize = (uint)frameRate,
                numConsecutiveBFrames = 2,
                profile = UnityEditor.VideoEncodingProfile.H264High,
            })
            {
                frameRate = new MediaRational(frameRate),
                width = (uint)width,
                height = (uint)height,
            };
            encoder = new MediaEncoder(filePath, attributes);
            frameTexture = new Texture2D(width, height, TextureFormat.RGBA32, false);
        }

        public void AddFrame(Color32[] pixels)
        {
            frameTexture.SetPixels32(pixels);
            frameTexture.Apply();
            encoder.AddFrame(frameTexture);
        }

        public void Dispose()
        {
            encoder.Dispose();
            UnityEngine.Object.DestroyImmediate(frameTexture);
        }
    }
}
