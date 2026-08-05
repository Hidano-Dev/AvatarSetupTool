using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace Hidano.AvatarSetupTool.Editor.Tests
{
    /// <summary>
    /// タスク 6.2「8K 実機検証」の一時計測テスト。実測値を JSON
    /// (Temp/highres8k-verification.json) へ書き出し、検証レポート作成後にこのファイルは削除する
    /// (恒久のリグレッション検証は既存の単体・統合テストが担う)。
    ///
    /// 検証項目:
    /// 1. 8K / Both / ImagesOnly の実機撮影と CPU 側ピークメモリ (ベースライン比 1GB/枚 未満)
    /// 2. タイル継ぎ目: 実機 8K 描画を製品レイアウトと境界位置の異なるレイアウトで二重に行い、
    ///    製品レイアウトのタイル境界の行・列を境界を跨いで比較 (各チャンネル ±1 階調以内)。
    ///    補助として実出力 PNG のタイル境界の隣接行・列差分も記録する
    /// 3. 低 VRAM (graphicsMemorySize 擬似低値) での 8K 描画が SSAA 降格なしに完了
    /// 4. 撮影実測による時間見積もり較正係数の更新で見積もり誤差が収束
    /// </summary>
    public class Highres8KVerificationTests
    {
        private const long OneGiB = 1L << 30;
        private const int OutputSize = 8192;
        private const string CalibrationPrefsKey = "Hidano.AvatarSetupTool.ModelCapture.TimeCalibration";

        [Serializable]
        internal class Report
        {
            public string unityVersion;
            public string gpuName;
            public string graphicsApi;
            public string cpu;
            public int maxTextureSize;
            public int graphicsMemoryMb;
            public int systemMemoryMb;

            // 1. ピークメモリ (8K / Both / ImagesOnly)
            public bool captureSuccess;
            public int pngCount;
            public long outputTotalBytes;
            public double captureSeconds;
            public long baselinePrivateBytes;
            public long peakPrivateBytes;
            public long diffPrivateBytes;
            public long baselineWorkingSetBytes;
            public long peakWorkingSetBytes;
            public long diffWorkingSetBytes;
            public long baselineManagedBytes;
            public long peakManagedBytes;
            public long baselineUnityNativeBytes;
            public long peakUnityNativeBytes;
            public int processSamples;
            public int progressSamples;

            // 2. 継ぎ目 (レイアウト差し替え二重描画の比較 + 実 PNG の境界隣接差分)
            public int seamTilesXA;
            public int seamTilesYA;
            public int seamTilesXB;
            public int seamTilesYB;
            public int seamFactor;
            public int seamMaxDiffOverall;
            public int seamMaxDiffAtBoundaries;
            public double seamExactMatchPercent;
            public string pngSeamRows;
            public string pngSeamCols;

            // 3. 低 VRAM
            public int lowVramPseudoMb;
            public int lowVramTileSideLimit;
            public int lowVramFactor;
            public int lowVramRequestedFactor;
            public int lowVramTilesX;
            public int lowVramTilesY;
            public bool lowVramCompleted;

            // 4. 時間較正
            public float calibrationFactorBefore;
            public float calibrationFactorAfter;
            public double estimateBeforeSeconds;
            public double estimateAfterSeconds;
            public double errorBeforeSeconds;
            public double errorAfterSeconds;
        }

        [Test]
        public void Verify8K_RealMachine()
        {
            var report = new Report
            {
                unityVersion = Application.unityVersion,
                gpuName = SystemInfo.graphicsDeviceName,
                graphicsApi = SystemInfo.graphicsDeviceType.ToString(),
                cpu = SystemInfo.processorType,
                maxTextureSize = SystemInfo.maxTextureSize,
                graphicsMemoryMb = SystemInfo.graphicsMemorySize,
                systemMemoryMb = SystemInfo.systemMemorySize,
            };

            // 実撮影は較正係数 (EditorPrefs) を更新する。収束の実測値は記録しつつ、
            // 小型テストモデルの撮影は実アバターより軽く係数を歪めるため元の値へ戻す
            var originalCalibration = EditorPrefs.GetFloat(CalibrationPrefsKey, 1f);
            try
            {
                MeasureCaptureMemoryAndCalibration(report);
                MeasureSeamsAndLowVram(report);
            }
            finally
            {
                EditorPrefs.SetFloat(CalibrationPrefsKey, originalCalibration);
                WriteJson(report);
            }

            Assert.That(report.captureSuccess, Is.True, "8K / Both / ImagesOnly の実機撮影が成功すること");
            Assert.That(report.pngCount, Is.EqualTo(16), "Both では 8 方向 x 2 構図の PNG が生成されること");
            Assert.That(report.processSamples, Is.GreaterThan(50), "メモリサンプリングが撮影中に動作していたこと");
            Assert.That(report.diffPrivateBytes, Is.LessThan(OneGiB),
                "CPU 側ピークメモリ (プロセス Private Bytes のベースライン差分) が 1GB/枚 未満であること");
            Assert.That(report.seamMaxDiffOverall, Is.LessThanOrEqualTo(1),
                "タイル境界位置の異なる 8K 描画どうしが全画素 ±1 階調以内で一致すること");
            Assert.That(report.seamMaxDiffAtBoundaries, Is.LessThanOrEqualTo(1),
                "製品レイアウトのタイル境界の行・列 (境界を跨ぐ両側) が ±1 階調以内であること");
            Assert.That(report.lowVramRequestedFactor, Is.EqualTo(2), "8K の要求 SSAA 倍率は 2 であること");
            Assert.That(report.lowVramFactor, Is.EqualTo(2), "低 VRAM 擬似値でも SSAA 降格が起きないこと");
            Assert.That(report.lowVramCompleted, Is.True, "低 VRAM 擬似値のレイアウトで 8K 描画が完了すること");
            Assert.That(report.errorAfterSeconds, Is.LessThanOrEqualTo(report.errorBeforeSeconds * 1.1 + 2.0),
                "較正係数の更新後に時間見積もり誤差が収束 (悪化しない) こと");
        }

        /// <summary>
        /// 8K / Both / ImagesOnly を実機撮影し、撮影直前に GC したベースラインと撮影中ピークの
        /// 差分で CPU 側ピークメモリを測る。同期実行中は Profiler のフレームカウンタが更新されない
        /// ため、プロセスの Private Bytes / Working Set を背景スレッドで 15ms 間隔サンプリングし、
        /// 補助としてマネージヒープと Unity ネイティブ確保量を進捗コールバック時点で記録する。
        /// あわせて較正係数の更新前後の時間見積もりを記録する。
        /// </summary>
        private static void MeasureCaptureMemoryAndCalibration(Report report)
        {
            var outputRoot = Path.Combine(
                Path.GetTempPath(), "AvatarSetupTool8K_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(outputRoot);
            GameObject source = null;
            Avatar avatar = null;
            try
            {
                source = BuildSmallTestModel(out avatar);
                var settings = new CaptureSettings
                {
                    format = CaptureOutputFormat.ImagesOnly,
                    viewMode = CaptureViewMode.Both,
                    imageSize = CaptureSettings.MaxImageSize,
                    outputRoot = outputRoot,
                };

                report.calibrationFactorBefore = ModelCaptureService.TimeCalibrationFactor;
                report.estimateBeforeSeconds = ModelCaptureService.EstimateCaptureSeconds(settings);

                // ベースライン: 撮影直前に未使用アセットを解放して GC を発生させる
                EditorUtility.UnloadUnusedAssetsImmediate();
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                SampleProcess(out var basePrivate, out var baseWorkingSet);
                report.baselinePrivateBytes = basePrivate;
                report.baselineWorkingSetBytes = baseWorkingSet;
                report.baselineManagedBytes = GC.GetTotalMemory(false);
                report.baselineUnityNativeBytes = UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong();

                var peaks = new long[2];
                peaks[0] = report.baselineManagedBytes;
                peaks[1] = report.baselineUnityNativeBytes;
                var progressSamples = 0;

                // 汎用アバターは Neck ボーンを持たないため、顔アップ構図のフォールバック警告が出る
                LogAssert.Expect(LogType.Warning, new Regex("Neck ボーンが取得できない"));

                CaptureResult result;
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                using (var sampler = new ProcessMemorySampler())
                {
                    result = ModelCaptureService.Capture(source, settings, (text, ratio) =>
                    {
                        progressSamples++;
                        peaks[0] = Math.Max(peaks[0], GC.GetTotalMemory(false));
                        peaks[1] = Math.Max(
                            peaks[1], UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong());
                        return false;
                    });
                    stopwatch.Stop();
                    sampler.Stop();
                    report.peakPrivateBytes = Math.Max(sampler.PeakPrivateBytes, basePrivate);
                    report.peakWorkingSetBytes = Math.Max(sampler.PeakWorkingSetBytes, baseWorkingSet);
                    report.processSamples = sampler.Samples;
                }

                report.captureSuccess = result.Success;
                report.captureSeconds = stopwatch.Elapsed.TotalSeconds;
                report.diffPrivateBytes = report.peakPrivateBytes - report.baselinePrivateBytes;
                report.diffWorkingSetBytes = report.peakWorkingSetBytes - report.baselineWorkingSetBytes;
                report.peakManagedBytes = peaks[0];
                report.peakUnityNativeBytes = peaks[1];
                report.progressSamples = progressSamples;

                // 較正係数の更新 (UpdateTimeCalibration) 後の見積もりで誤差の収束を確認する
                report.calibrationFactorAfter = ModelCaptureService.TimeCalibrationFactor;
                report.estimateAfterSeconds = ModelCaptureService.EstimateCaptureSeconds(settings);
                report.errorBeforeSeconds = Math.Abs(report.captureSeconds - report.estimateBeforeSeconds);
                report.errorAfterSeconds = Math.Abs(report.captureSeconds - report.estimateAfterSeconds);

                if (result.Success)
                {
                    var pngs = Directory.GetFiles(result.OutputDirectory, "*.png");
                    report.pngCount = pngs.Length;
                    report.outputTotalBytes = pngs.Sum(path => new FileInfo(path).Length);
                    MeasurePngBoundaryDeltas(report, pngs);
                }
            }
            finally
            {
                if (source != null)
                {
                    Object.DestroyImmediate(source);
                }

                if (avatar != null)
                {
                    Object.DestroyImmediate(avatar);
                }

                if (Directory.Exists(outputRoot))
                {
                    Directory.Delete(outputRoot, recursive: true);
                }
            }
        }

        /// <summary>
        /// 実出力の 8K PNG (full 構図 1 枚) について、製品レイアウトのタイル境界に当たる
        /// 行・列の隣接画素差分 (平均絶対差) を、境界から離れた参照位置の同差分と並べて記録する。
        /// モデルのエッジや背景グリッドを含む実画像のため合否判定には使わず、
        /// 継ぎ目起因の異常な段差がないことの補助データとする (合否はレイアウト差し替え比較が担う)。
        /// </summary>
        private static void MeasurePngBoundaryDeltas(Report report, string[] pngs)
        {
            var path = pngs.FirstOrDefault(p => Path.GetFileName(p).Contains("_full_")) ?? pngs[0];
            var texture = new Texture2D(2, 2);
            try
            {
                if (!texture.LoadImage(File.ReadAllBytes(path)))
                {
                    report.pngSeamRows = "PNG のデコードに失敗";
                    return;
                }

                var width = texture.width;
                var height = texture.height;
                var pixels = texture.GetPixels32();
                var layout = TileLayout.Compute(
                    width, height, TileLayout.PreferredFactor(width, height),
                    TileSideLimits.Compute(SystemInfo.maxTextureSize, SystemInfo.graphicsMemorySize));

                var rows = new StringBuilder();
                for (var ty = 1; ty < layout.TilesY; ty++)
                {
                    var b = layout.GetTile(0, ty).Y;
                    rows.Append(rows.Length > 0 ? "; " : string.Empty).Append(
                        $"y={b}: 境界 {RowMeanAbsDiff(pixels, width, b - 1, b):F3}"
                        + $" / 参照- {RowMeanAbsDiff(pixels, width, b - 9, b - 8):F3}"
                        + $" / 参照+ {RowMeanAbsDiff(pixels, width, b + 7, b + 8):F3}");
                }

                var cols = new StringBuilder();
                for (var tx = 1; tx < layout.TilesX; tx++)
                {
                    var b = layout.GetTile(tx, 0).X;
                    cols.Append(cols.Length > 0 ? "; " : string.Empty).Append(
                        $"x={b}: 境界 {ColMeanAbsDiff(pixels, width, height, b - 1, b):F3}"
                        + $" / 参照- {ColMeanAbsDiff(pixels, width, height, b - 9, b - 8):F3}"
                        + $" / 参照+ {ColMeanAbsDiff(pixels, width, height, b + 7, b + 8):F3}");
                }

                report.pngSeamRows = $"{Path.GetFileName(path)} ({width}x{height}): {rows}";
                report.pngSeamCols = cols.ToString();
            }
            finally
            {
                Object.DestroyImmediate(texture);
            }
        }

        private static double RowMeanAbsDiff(Color32[] pixels, int width, int rowA, int rowB)
        {
            long sum = 0;
            for (var x = 0; x < width; x++)
            {
                var a = pixels[rowA * width + x];
                var b = pixels[rowB * width + x];
                sum += Math.Abs(a.r - b.r) + Math.Abs(a.g - b.g) + Math.Abs(a.b - b.b);
            }

            return sum / (width * 3.0);
        }

        private static double ColMeanAbsDiff(Color32[] pixels, int width, int height, int colA, int colB)
        {
            long sum = 0;
            for (var y = 0; y < height; y++)
            {
                var a = pixels[y * width + colA];
                var b = pixels[y * width + colB];
                sum += Math.Abs(a.r - b.r) + Math.Abs(a.g - b.g) + Math.Abs(a.b - b.b);
            }

            return sum / (height * 3.0);
        }

        /// <summary>
        /// 実機 GPU で 8K を製品レイアウト (4096px 上限) と境界位置の異なるレイアウト
        /// (上限 3800px、境界が互いに素) で二重描画し、製品レイアウトのタイル境界の行・列を
        /// 境界を跨いで比較する。片方の境界はもう片方ではタイル内部になるため、
        /// 継ぎ目起因の段差があれば必ず差分として現れる。シーンは等価性テストと同じ
        /// 連続ノイズ板 (幾何エッジのラスタライズ反転による偽陽性を避ける)。
        /// 続けて同一シーンで低 VRAM 擬似値レイアウトの完了を確認する。
        /// </summary>
        private static void MeasureSeamsAndLowVram(Report report)
        {
            var preview = new PreviewRenderUtility();
            var created = new List<Object>();
            try
            {
                ModelCaptureService.SetupCameraAndLights(preview);
                BuildNoiseScene(preview, created);

                // SRP では最初のレンダリングが空になることがあるため、本番 Capture と同じく捨て描画する
                ModelCaptureService.RenderStill(
                    preview, MakeView(128, 128),
                    TileLayout.Compute(128, 128, 2, TileSideLimits.SafeTileSide), null, "warmup");

                var view = MakeView(OutputSize, OutputSize);
                var factor = TileLayout.PreferredFactor(OutputSize, OutputSize);
                var layoutA = TileLayout.Compute(
                    OutputSize, OutputSize, factor,
                    TileSideLimits.Compute(SystemInfo.maxTextureSize, SystemInfo.graphicsMemorySize));
                var layoutB = TileLayout.Compute(OutputSize, OutputSize, factor, 3800);
                report.seamFactor = layoutA.Factor;
                report.seamTilesXA = layoutA.TilesX;
                report.seamTilesYA = layoutA.TilesY;
                report.seamTilesXB = layoutB.TilesX;
                report.seamTilesYB = layoutB.TilesY;

                // 前提: 両レイアウトの内部境界が一致しない (共有境界では段差が相殺し検出できない)
                var boundariesA = InnerBoundaries(layoutA);
                var boundariesB = InnerBoundaries(layoutB);
                Assert.That(boundariesA.Xs.Intersect(boundariesB.Xs), Is.Empty, "前提: X 境界が互いに素");
                Assert.That(boundariesA.Ys.Intersect(boundariesB.Ys), Is.Empty, "前提: Y 境界が互いに素");

                var a = ModelCaptureService.RenderStill(preview, view, layoutA, null, "seam-A");
                var b = ModelCaptureService.RenderStill(preview, view, layoutB, null, "seam-B");
                AssertSceneHasVariation(a);

                var exact = 0L;
                var maxDiff = 0;
                for (var i = 0; i < a.Length; i += 3)
                {
                    var dr = Math.Abs(a[i] - b[i]);
                    var dg = Math.Abs(a[i + 1] - b[i + 1]);
                    var db = Math.Abs(a[i + 2] - b[i + 2]);
                    if (dr == 0 && dg == 0 && db == 0)
                    {
                        exact++;
                    }

                    maxDiff = Math.Max(maxDiff, Math.Max(dr, Math.Max(dg, db)));
                }

                report.seamMaxDiffOverall = maxDiff;
                report.seamExactMatchPercent = exact * 300.0 / a.Length;

                // 製品レイアウトの境界を跨ぐ行・列 (両側 1px ずつ) に限定した最大差分
                var boundaryDiff = 0;
                foreach (var x in boundariesA.Xs)
                {
                    for (var y = 0; y < OutputSize; y++)
                    {
                        boundaryDiff = Math.Max(boundaryDiff, PixelDiff(a, b, (y * OutputSize + x - 1) * 3));
                        boundaryDiff = Math.Max(boundaryDiff, PixelDiff(a, b, (y * OutputSize + x) * 3));
                    }
                }

                foreach (var y in boundariesA.Ys)
                {
                    for (var x = 0; x < OutputSize; x++)
                    {
                        boundaryDiff = Math.Max(boundaryDiff, PixelDiff(a, b, ((y - 1) * OutputSize + x) * 3));
                        boundaryDiff = Math.Max(boundaryDiff, PixelDiff(a, b, (y * OutputSize + x) * 3));
                    }
                }

                report.seamMaxDiffAtBoundaries = boundaryDiff;
                a = null;
                b = null;

                // 低 VRAM: graphicsMemorySize の擬似低値 (256MB) でも VramFloorMb の下限クランプで
                // タイル辺長上限が維持され、SSAA 降格なしに 8K 描画が完了することを実機で確認する
                report.lowVramPseudoMb = 256;
                report.lowVramTileSideLimit = TileSideLimits.Compute(
                    SystemInfo.maxTextureSize, report.lowVramPseudoMb);
                var layoutLow = TileLayout.Compute(
                    OutputSize, OutputSize, factor, report.lowVramTileSideLimit);
                report.lowVramFactor = layoutLow.Factor;
                report.lowVramRequestedFactor = layoutLow.RequestedFactor;
                report.lowVramTilesX = layoutLow.TilesX;
                report.lowVramTilesY = layoutLow.TilesY;
                var low = ModelCaptureService.RenderStill(preview, view, layoutLow, null, "low-vram");
                report.lowVramCompleted =
                    low != null && low.Length == OutputSize * OutputSize * 3 && HasVariation(low);
            }
            finally
            {
                preview.Cleanup();
                foreach (var obj in created)
                {
                    if (obj != null)
                    {
                        Object.DestroyImmediate(obj);
                    }
                }
            }
        }

        private static int PixelDiff(byte[] a, byte[] b, int offset)
        {
            return Math.Max(
                Math.Abs(a[offset] - b[offset]),
                Math.Max(Math.Abs(a[offset + 1] - b[offset + 1]), Math.Abs(a[offset + 2] - b[offset + 2])));
        }

        /// <summary>レイアウトの内部タイル境界 (出力 px)。端 (0, サイズ) は含まない。</summary>
        private static (List<int> Xs, List<int> Ys) InnerBoundaries(TileLayout layout)
        {
            var xs = new List<int>();
            for (var tx = 1; tx < layout.TilesX; tx++)
            {
                xs.Add(layout.GetTile(tx, 0).X);
            }

            var ys = new List<int>();
            for (var ty = 1; ty < layout.TilesY; ty++)
            {
                ys.Add(layout.GetTile(0, ty).Y);
            }

            return (xs, ys);
        }

        private static void AssertSceneHasVariation(byte[] pixels)
        {
            Assert.That(HasVariation(pixels), Is.True, "前提: 描画結果が単色でシーンが描画されていません。");
        }

        private static bool HasVariation(byte[] pixels)
        {
            for (var i = 3; i + 2 < pixels.Length; i += 3)
            {
                if (pixels[i] != pixels[0] || pixels[i + 1] != pixels[1] || pixels[i + 2] != pixels[2])
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>プロセスの Private Bytes / Working Set を背景スレッドで 15ms 間隔サンプリングする。</summary>
        private sealed class ProcessMemorySampler : IDisposable
        {
            private readonly Thread thread;
            private volatile bool running = true;

            public long PeakPrivateBytes;
            public long PeakWorkingSetBytes;
            public int Samples;

            public ProcessMemorySampler()
            {
                thread = new Thread(Run) { IsBackground = true, Name = "Highres8KMemorySampler" };
                thread.Start();
            }

            private void Run()
            {
                while (running)
                {
                    SampleProcess(out var privateBytes, out var workingSet);
                    if (privateBytes > PeakPrivateBytes)
                    {
                        PeakPrivateBytes = privateBytes;
                    }

                    if (workingSet > PeakWorkingSetBytes)
                    {
                        PeakWorkingSetBytes = workingSet;
                    }

                    Samples++;
                    Thread.Sleep(15);
                }
            }

            public void Stop()
            {
                running = false;
                thread.Join(2000);
            }

            public void Dispose() => Stop();
        }

        private static void SampleProcess(out long privateBytes, out long workingSetBytes)
        {
            using (var process = System.Diagnostics.Process.GetCurrentProcess())
            {
                privateBytes = process.PrivateMemorySize64;
                workingSetBytes = process.WorkingSet64;
            }
        }

        /// <summary>
        /// 小型テストモデルを組む (CaptureSmokeTests と同じ)。Capture は「Avatar 付き Animator」を
        /// 撮影対象として検出するため、箱モデルに実行時生成の汎用アバターを付けて条件を満たす。
        /// </summary>
        private static GameObject BuildSmallTestModel(out Avatar avatar)
        {
            var root = new GameObject("Highres8KModel");
            var body = GameObject.CreatePrimitive(PrimitiveType.Cube);
            body.name = "Body";
            body.transform.SetParent(root.transform, false);
            body.transform.localScale = new Vector3(0.4f, 1f, 0.4f);
            body.transform.localPosition = new Vector3(0f, 0.5f, 0f);

            var animator = root.AddComponent<Animator>();
            avatar = AvatarBuilder.BuildGenericAvatar(root, string.Empty);
            avatar.name = "Highres8KAvatar";
            animator.avatar = avatar;
            return root;
        }

        /// <summary>
        /// レンダ解像度から ViewSpec を作る (TileRenderEquivalenceTests と同じ逆算)。
        /// </summary>
        private static ModelCaptureService.ViewSpec MakeView(int renderWidth, int renderHeight)
        {
            return new ModelCaptureService.ViewSpec(
                Vector3.zero, 1f, 2f,
                renderWidth / ModelCaptureService.SuperSampleFactor,
                renderHeight / ModelCaptureService.SuperSampleFactor);
        }

        /// <summary>
        /// 画面全体を覆う高周波ノイズ板 2 枚のシーン (TileRenderEquivalenceTests と同じ構成)。
        /// 幾何エッジを画面内に置かないことで、タイル毎の射影行列の浮動小数差による
        /// ラスタライザ被覆反転 (±1 階調超の偽陽性) を避け、継ぎ目の実バグだけを検出する。
        /// </summary>
        private static void BuildNoiseScene(PreviewRenderUtility preview, List<Object> created)
        {
            AddTexturedQuad(
                preview, created, new Vector3(0f, 0f, 1.5f), new Vector2(6f, 4f), 0f,
                MakeNoiseTexture(created, 256, 192, 12345u, opaque: true));
            AddTexturedQuad(
                preview, created, new Vector3(0.2f, -0.1f, 0.8f), new Vector2(10f, 10f), 20f,
                MakeNoiseTexture(created, 160, 160, 67890u, opaque: false));
        }

        private static Texture2D MakeNoiseTexture(
            List<Object> created, int width, int height, uint seed, bool opaque)
        {
            var pixels = new Color32[width * height];
            var state = seed;
            for (var i = 0; i < pixels.Length; i++)
            {
                state = state * 1664525u + 1013904223u;
                pixels[i] = new Color32(
                    (byte)(state >> 24), (byte)(state >> 16), (byte)(state >> 8),
                    opaque ? (byte)255 : (byte)(64 + (state & 0x7F)));
            }

            var texture = new Texture2D(width, height, TextureFormat.RGBA32, mipChain: false)
            {
                name = "Highres8KNoise",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                anisoLevel = 0,
                hideFlags = HideFlags.HideAndDontSave,
            };
            texture.SetPixels32(pixels);
            texture.Apply(updateMipmaps: false);
            created.Add(texture);
            return texture;
        }

        private static void AddTexturedQuad(
            PreviewRenderUtility preview, List<Object> created,
            Vector3 center, Vector2 size, float zRotationDegrees, Texture2D texture)
        {
            var halfW = size.x / 2f;
            var halfH = size.y / 2f;
            var mesh = new Mesh
            {
                name = "Highres8KQuad",
                vertices = new[]
                {
                    new Vector3(-halfW, -halfH, 0f),
                    new Vector3(-halfW, halfH, 0f),
                    new Vector3(halfW, halfH, 0f),
                    new Vector3(halfW, -halfH, 0f),
                },
                uv = new[]
                {
                    new Vector2(0f, 0f),
                    new Vector2(0f, 1f),
                    new Vector2(1f, 1f),
                    new Vector2(1f, 0f),
                },
                triangles = new[] { 0, 1, 2, 0, 2, 3 },
            };
            created.Add(mesh);

            var go = new GameObject("Highres8KQuad");
            go.transform.SetPositionAndRotation(center, Quaternion.Euler(0f, 0f, zRotationDegrees));
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            var material = new Material(Shader.Find("Sprites/Default"))
            {
                hideFlags = HideFlags.HideAndDontSave,
                mainTexture = texture,
            };
            created.Add(material);
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            preview.AddSingleGO(go);
        }

        private static void WriteJson(Report report)
        {
            var path = Path.GetFullPath(
                Path.Combine(Application.dataPath, "..", "Temp", "highres8k-verification.json"));
            File.WriteAllText(path, JsonUtility.ToJson(report, prettyPrint: true), Encoding.UTF8);
            TestContext.WriteLine($"実測レポートを書き出しました: {path}");
        }
    }
}
