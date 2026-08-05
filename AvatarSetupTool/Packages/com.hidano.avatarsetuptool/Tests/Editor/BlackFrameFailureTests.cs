using System;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Hidano.AvatarSetupTool.Editor.Tests
{
    /// <summary>
    /// 黒フレーム失敗経路の統合テスト (EditMode で GPU 描画を直接使用)。
    /// テスト用フック (<see cref="ModelCaptureService.TileReadbackHook"/>) でタイル読み戻しを
    /// 全黒化し、リトライがちょうど 1 回行われること・再失敗時の
    /// <see cref="CaptureRenderFailedException"/> 送出・失敗時に PNG ファイルが
    /// 生成されないことを検証する。
    ///
    /// シーンは空 (背景の不透明グレーのみ) で足りる。正常な読み戻しは必ず非黒になるため、
    /// フックの差し替えだけで失敗・復帰の両経路を決定的に再現できる。
    /// </summary>
    public class BlackFrameFailureTests
    {
        private PreviewRenderUtility preview;

        [OneTimeSetUp]
        public void SetUpScene()
        {
            preview = new PreviewRenderUtility();
            ModelCaptureService.SetupCameraAndLights(preview);

            // SRP では最初のレンダリングが空 (= 全黒) になることがあるため、本番 Capture と
            // 同じく捨て描画する。これを省くと復帰テストがウォームアップ起因の黒で不安定になる
            ModelCaptureService.RenderStill(
                preview, MakeView(128, 128), TileLayout.Compute(128, 128, 2, TileSideLimits.SafeTileSide),
                null, "warmup");
        }

        [OneTimeTearDown]
        public void TearDownScene()
        {
            preview?.Cleanup();
        }

        [TearDown]
        public void ResetHook()
        {
            ModelCaptureService.TileReadbackHook = null;
        }

        [Test]
        public void RenderStill_BlackAfterRetry_RendersExactlyTwiceAndThrows()
        {
            var renderCount = 0;
            ModelCaptureService.TileReadbackHook = pixels =>
            {
                renderCount++;
                return Blacken(pixels);
            };

            var view = MakeView(128, 128);
            var layout = TileLayout.Compute(128, 128, 2, TileSideLimits.SafeTileSide);
            Assert.That(layout.IsSinglePass, Is.True, "前提: 1 タイルなので描画回数 = フック呼び出し回数");

            LogAssert.Expect(LogType.Warning, new Regex("黒フレームを検出したためタイルを再描画します"));
            var exception = Assert.Throws<CaptureRenderFailedException>(
                () => ModelCaptureService.RenderStill(preview, view, layout, null, "test full 000"));

            Assert.That(renderCount, Is.EqualTo(2), "初回 + リトライちょうど 1 回で打ち切られること");
            StringAssert.Contains("test full 000", exception.Message, "診断メッセージに構図/方向の識別子を含む");
            StringAssert.Contains("128x128", exception.Message, "診断メッセージに出力解像度を含む");
        }

        [Test]
        public void RenderStill_BlackOnlyOnFirstAttempt_RecoversByRetry()
        {
            var renderCount = 0;
            ModelCaptureService.TileReadbackHook = pixels =>
            {
                renderCount++;
                return renderCount == 1 ? Blacken(pixels) : pixels;
            };

            var view = MakeView(128, 128);
            var layout = TileLayout.Compute(128, 128, 2, TileSideLimits.SafeTileSide);

            LogAssert.Expect(LogType.Warning, new Regex("黒フレームを検出したためタイルを再描画します"));
            var result = ModelCaptureService.RenderStill(preview, view, layout, null, "test full 000");

            Assert.That(renderCount, Is.EqualTo(2), "リトライ 1 回で復帰すること");
            Assert.That(ModelCaptureService.IsAllBlack(result), Is.False, "復帰後は正常な描画結果を返すこと");
        }

        [Test]
        public void CaptureShot_RenderFailure_DoesNotCreatePngFile()
        {
            ModelCaptureService.TileReadbackHook = Blacken;

            var outputDir = Path.Combine(
                Path.GetTempPath(), "AvatarSetupToolTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(outputDir);
            try
            {
                var filePath = Path.Combine(outputDir, "black_frame.png");

                LogAssert.Expect(LogType.Warning, new Regex("黒フレームを検出したためタイルを再描画します"));
                Assert.Throws<CaptureRenderFailedException>(
                    () => ModelCaptureService.CaptureShot(
                        preview, MakeView(128, 128), filePath, makeGifFrame: false,
                        checkCancel: null, shotLabel: "test full 000"));

                Assert.That(File.Exists(filePath), Is.False, "失敗時に黒い PNG が保存されないこと");
                Assert.That(Directory.GetFiles(outputDir), Is.Empty, "失敗時にファイルを一切残さないこと");
            }
            finally
            {
                Directory.Delete(outputDir, recursive: true);
            }
        }

        /// <summary>
        /// レンダ解像度から ViewSpec を作る。ViewSpec はアニメ解像度 × SuperSampleFactor が
        /// レンダ解像度になる契約のため、逆算して渡す (レンダ解像度は偶数であること)。
        /// </summary>
        private static ModelCaptureService.ViewSpec MakeView(int renderWidth, int renderHeight)
        {
            return new ModelCaptureService.ViewSpec(
                Vector3.zero, 1f, 2f,
                renderWidth / ModelCaptureService.SuperSampleFactor,
                renderHeight / ModelCaptureService.SuperSampleFactor);
        }

        /// <summary>描画失敗 (TDR / 確保失敗) 相当の読み戻し結果 (RGB 全黒、アルファ不透明) を作る。</summary>
        private static Color32[] Blacken(Color32[] pixels)
        {
            for (var i = 0; i < pixels.Length; i++)
            {
                pixels[i] = new Color32(0, 0, 0, 255);
            }

            return pixels;
        }
    }
}
