using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using Object = UnityEngine.Object;

namespace Hidano.AvatarSetupTool.Editor
{
    /// <summary>キャプチャ実行の結果。</summary>
    public sealed class CaptureResult
    {
        public bool Success;
        public bool Canceled;
        public string Error;
        public string OutputDirectory;

        public static CaptureResult Fail(string error) => new CaptureResult { Error = error };

        public static CaptureResult Cancel() => new CaptureResult { Canceled = true, Error = "キャンセルされました。" };
    }

    /// <summary>
    /// リトライ後も黒フレームだった描画失敗。原因究明用の診断情報 (構図/方向・出力解像度・
    /// SSAA 倍率・タイル位置と総数・レンダサイズ・環境値) をメッセージに含める。
    /// <see cref="ModelCaptureService.Capture"/> が捕捉して <see cref="CaptureResult.Fail"/> へ
    /// 変換するため、呼び出し元へは公開契約 (Result) の失敗として届く。
    /// 送出は PNG 保存前のため、黒い PNG がディスクに書かれることはない。
    /// </summary>
    internal sealed class CaptureRenderFailedException : Exception
    {
        public CaptureRenderFailedException(string message)
            : base(message)
        {
        }
    }

    /// <summary>
    /// モデルを 8 方向 × 撮影範囲 (全身 / 顔アップ / 両方) の PNG としてキャプチャし、
    /// 設定に応じてターンテーブル動画 (MP4 / GIF / ProRes 422) も生成するロジック層。
    /// UI (EditorWindow・ダイアログ・プログレスバー) には依存せず、
    /// <see cref="Capture"/> を直接呼べば CLI (-executeMethod 等) からも実行できる。
    ///
    /// - 撮影対象: Project の FBX / Prefab、または Hierarchy 上の GameObject (編集中の状態を複製して撮影)
    /// - 対象内の Avatar 付き Animator をすべて撮影。見つからなければエラーを返す
    /// - カメラは並行投影。背景はグレー一色に、1m 間隔の主線 + 10cm 間隔の細線のグリッドを描画
    /// - 全身は高さ基準の固定構図で、横に広いモデルは画像のアスペクト比を横に広げて収める
    /// - 実行前に GPU テクスチャ上限とメモリ使用量を見積もり、超過する場合は撮影せずエラーを返す
    /// </summary>
    public static class ModelCaptureService
    {
        private const int VideoFrameRate = 30;
        private const int GifFrameDelayCentiseconds = 200;

        /// <summary>
        /// H.264 の実装上の解像度上限。規格上は Level 6 で 8192x4320 まで定義されているが、
        /// Unity の MediaEncoder が使う Windows の Media Foundation エンコーダは
        /// Level 5.2 相当 (4096x2304、約 940 万ピクセル) までしか受け付けない。
        /// </summary>
        internal const int H264MaxDimension = 4096;
        internal const long H264MaxPixels = 4096L * 2304L;
        private const float FullBodyMargin = 1.05f;
        private const float MaxFullBodyAspect = 8f; // 幅の暴走防止
        private const float FacePaddingRatio = 0.1f;
        private const float FaceFallbackHeightRatio = 0.15f;
        internal const int SuperSampleFactor = 2; // PNG 解像度に対する動画の縮小率 (ボックス平均 = SSAA)

        private static readonly Color32 BackgroundColor = new Color32(184, 184, 184, 255);
        private static readonly Color32 SubLineColor = new Color32(144, 144, 144, 255);
        private static readonly Color32 MainLineColor = new Color32(96, 96, 96, 255);
        private const float SubLineSpacing = 0.1f; // 10cm 間隔の細線。10 本ごと (1m) に主線
        private const int SubLinesPerMainLine = 10;

        // 10cm 線は破線にして 1m の実線の主線と区別する。周期をグリッド間隔の約数にすることで
        // 破線の点が必ず 1cm の倍数の位置 (= 線どうしの交点を含む) に来るようにする
        private const float SubLineDashPeriod = 0.01f;
        private const float SubLineDashLength = 0.005f;

        /// <summary>
        /// カメラは -Z 側に固定し、モデル側を Y 回転させて 8 方向を撮る。
        /// yaw=180 でモデルの正面がカメラを向く。
        /// 名前は正面を 0 とした度数表記 (正面から左向きへ 45 度刻み)。
        /// 3 桁ゼロ埋めなので、ファイル名順に並べたとき回転順になる (GIF のフレーム順も同じ)。
        /// </summary>
        private static readonly (string Name, float Yaw)[] Directions =
        {
            ("000", 180f),
            ("045", -135f),
            ("090", -90f),
            ("135", -45f),
            ("180", 0f),
            ("225", 45f),
            ("270", 90f),
            ("315", 135f),
        };

        /// <summary>
        /// ターゲット 1 体分の固定構図。8 方向の静止画とターンテーブル動画のすべてで共用する。
        /// </summary>
        internal readonly struct ViewSpec
        {
            public ViewSpec(Vector3 center, float orthoSize, float depthExtent, int animWidth, int animHeight)
            {
                Center = center;
                OrthoSize = orthoSize;
                DepthExtent = depthExtent;
                AnimWidth = animWidth;
                AnimHeight = animHeight;
            }

            public Vector3 Center { get; }
            public float OrthoSize { get; }
            public float DepthExtent { get; }
            public int AnimWidth { get; }
            public int AnimHeight { get; }
            public int RenderWidth => AnimWidth * SuperSampleFactor;
            public int RenderHeight => AnimHeight * SuperSampleFactor;
        }

        /// <summary>
        /// キャプチャを実行する。progress には表示用テキストと 0〜1 の進捗率が渡され、
        /// true を返すとキャンセル要求として撮影を中断する (書きかけの動画は削除される)。
        /// </summary>
        public static CaptureResult Capture(
            GameObject source, CaptureSettings settings, Func<string, float, bool> progress = null)
        {
            if (source == null)
            {
                return CaptureResult.Fail("撮影対象が指定されていません。");
            }

            if (string.IsNullOrEmpty(settings.outputRoot))
            {
                return CaptureResult.Fail("出力先フォルダが指定されていません。");
            }

            var animHeight = settings.NormalizedImageSize / SuperSampleFactor;
            var preview = new PreviewRenderUtility();
            try
            {
                SetupCameraAndLights(preview);

                var instance = InstantiateSource(preview, source, out var modelName);
                if (instance == null)
                {
                    return CaptureResult.Fail($"モデルを読み込めませんでした: {source.name}");
                }

                instance.transform.position = Vector3.zero;

                var targets = instance.GetComponentsInChildren<Animator>(true)
                    .Where(animator => animator.avatar != null)
                    .ToArray();
                if (targets.Length == 0)
                {
                    return CaptureResult.Fail(
                        $"Avatar が設定された Animator が見つかりませんでした: {modelName}");
                }

                // 事前チェック: 全ターゲットの構図を先に確定し、GPU 上限とメモリ見積もりを検証する
                var views = new (ViewSpec Full, ViewSpec Face)[targets.Length];
                for (var i = 0; i < targets.Length; i++)
                {
                    views[i] = ComputeViews(targets[i], targets[i].gameObject, animHeight);
                }

                var memoryError = ValidateMemory(views, settings);
                if (memoryError != null)
                {
                    return CaptureResult.Fail(memoryError);
                }

                var outputDir = UniqueOutputDirectory(settings.outputRoot, CaptureFileName.Sanitize(modelName));
                Directory.CreateDirectory(outputDir);

                var stopwatch = System.Diagnostics.Stopwatch.StartNew();

                var allRenderers = instance.GetComponentsInChildren<Renderer>(true);

                // SRP では最初のレンダリングが空になることがあるため、1 回捨てレンダリングする
                Warmup(preview, CalculateBounds(instance));

                var timestamp = DateTime.Now;

                // デバッグ情報は撮影前にターゲット分まとめて収集し、md ファイルとして先に書き出す
                // (キャンセルしても残った PNG と突き合わせられる)。各 PNG の iTXt にも同じ内容が入る
                string[] debugTexts = null;
                if (settings.includeDebugInfo)
                {
                    debugTexts = CaptureDebugInfo.CollectAndWriteMarkdown(
                        Path.Combine(outputDir, "debug_info.md"), source, modelName, targets, timestamp,
                        settings);
                }

                var bothViews = settings.viewMode == CaptureViewMode.Both;
                var stillPattern = EffectivePattern(
                    settings.fileNamePattern, targets.Length > 1, forStill: true, bothViews: bothViews);
                var videoPattern = EffectivePattern(
                    settings.fileNamePattern, targets.Length > 1, forStill: false, bothViews: bothViews);

                var isVideo = settings.format == CaptureOutputFormat.Mp4
                    || settings.format == CaptureOutputFormat.ProRes422;
                var usedNames = new HashSet<string>();
                var videoFrameCount = Mathf.RoundToInt(VideoFrameRate * settings.SecondsPerRotation);
                var stepsPerTarget = (Directions.Length + (isVideo ? videoFrameCount : 0)) * settings.ViewCount;
                var total = targets.Length * stepsPerTarget;
                var step = 0;
                for (var t = 0; t < targets.Length; t++)
                {
                    var animator = targets[t];
                    var target = animator.gameObject;
                    var captureName = MakeUniqueName(CaptureFileName.Sanitize(target.name), usedNames);

                    // 対象の Animator 配下だけを描画し、他の Animator と混ざらないようにする
                    foreach (var renderer in allRenderers)
                    {
                        renderer.enabled = renderer.transform.IsChildOf(target.transform);
                    }

                    var (fullView, faceView) = views[t];

                    string ResolveName(string pattern, string direction, string viewLabel, int width, int height)
                        => CaptureFileName.Resolve(
                            pattern, modelName, captureName, direction, viewLabel,
                            width, height, timestamp, settings.take);

                    var debugText = debugTexts?[t];

                    using (var backdrop = new GridBackdrop(preview, fullView, faceView))
                    {
                        var fullGifFrames = new List<Color32[]>(Directions.Length);
                        var faceGifFrames = new List<Color32[]>(Directions.Length);
                        foreach (var (dirName, yaw) in Directions)
                        {
                            target.transform.rotation = Quaternion.Euler(0f, yaw, 0f);

                            var progressText = $"{captureName}: {dirName}";
                            var progressRatio = step / (float)total;
                            if (CancelRequested(progress, progressText, progressRatio))
                            {
                                return CaptureResult.Cancel();
                            }

                            // タイルループ内のキャンセル判定用。直前と同一のテキスト・進捗値で
                            // 問い合わせるため、進捗表示のコールバック契約は変えずに応答性だけ高める
                            bool CheckCancel() => CancelRequested(progress, progressText, progressRatio);

                            var makeGifFrame = settings.format == CaptureOutputFormat.Gif;

                            if (settings.CaptureFull)
                            {
                                backdrop.Show(showFull: true);
                                var fullName = ResolveName(stillPattern, dirName, "full",
                                    fullView.RenderWidth, fullView.RenderHeight);
                                var fullFrame = CaptureShot(preview, fullView,
                                    Path.Combine(outputDir, fullName + ".png"), makeGifFrame,
                                    CheckCancel, $"{captureName} full {dirName}", debugText);
                                step++;
                                if (makeGifFrame)
                                {
                                    fullGifFrames.Add(fullFrame);
                                }
                            }

                            if (settings.CaptureFace)
                            {
                                backdrop.Show(showFull: false);
                                var faceName = ResolveName(stillPattern, dirName, "BS",
                                    faceView.RenderWidth, faceView.RenderHeight);
                                var faceFrame = CaptureShot(preview, faceView,
                                    Path.Combine(outputDir, faceName + ".png"), makeGifFrame,
                                    CheckCancel, $"{captureName} BS {dirName}", debugText);
                                step++;
                                if (makeGifFrame)
                                {
                                    faceGifFrames.Add(faceFrame);
                                }
                            }
                        }

                        if (settings.format == CaptureOutputFormat.Gif)
                        {
                            if (CancelRequested(progress, $"{captureName}: GIF を書き出し中", step / (float)total))
                            {
                                return CaptureResult.Cancel();
                            }

                            if (settings.CaptureFull)
                            {
                                GifWriter.Write(
                                    Path.Combine(outputDir, ResolveName(videoPattern, null, "full",
                                        fullView.AnimWidth, fullView.AnimHeight) + ".gif"),
                                    fullGifFrames, fullView.AnimWidth, fullView.AnimHeight,
                                    GifFrameDelayCentiseconds);
                            }

                            if (settings.CaptureFace)
                            {
                                GifWriter.Write(
                                    Path.Combine(outputDir, ResolveName(videoPattern, null, "BS",
                                        faceView.AnimWidth, faceView.AnimHeight) + ".gif"),
                                    faceGifFrames, faceView.AnimWidth, faceView.AnimHeight,
                                    GifFrameDelayCentiseconds);
                            }
                        }
                        else if (isVideo)
                        {
                            var extension = settings.format == CaptureOutputFormat.Mp4 ? ".mp4" : ".mov";
                            var fullPath = settings.CaptureFull
                                ? Path.Combine(outputDir, ResolveName(videoPattern, null, "full",
                                    fullView.AnimWidth, fullView.AnimHeight) + extension)
                                : null;
                            var facePath = settings.CaptureFace
                                ? Path.Combine(outputDir, ResolveName(videoPattern, null, "BS",
                                    faceView.AnimWidth, faceView.AnimHeight) + extension)
                                : null;
                            var canceled = CaptureTurntableVideos(
                                preview, target, backdrop, fullView, faceView, settings.format,
                                fullPath, facePath, captureName, videoFrameCount, total, progress, ref step);
                            if (canceled)
                            {
                                return CaptureResult.Cancel();
                            }
                        }
                    }
                }

                var pngCount = targets.Length * Directions.Length * settings.ViewCount;
                var videoCount = targets.Length * settings.ViewCount;
                var summary = settings.format switch
                {
                    CaptureOutputFormat.Mp4 => $"PNG {pngCount} 枚と MP4 {videoCount} 本",
                    CaptureOutputFormat.Gif => $"PNG {pngCount} 枚と GIF {videoCount} 本",
                    CaptureOutputFormat.ProRes422 => $"PNG {pngCount} 枚と ProRes MOV {videoCount} 本",
                    _ => $"PNG {pngCount} 枚",
                };
                Debug.Log($"[AvatarSetupTool] {modelName}: {summary}を保存しました: {outputDir}");
                UpdateTimeCalibration(stopwatch.Elapsed.TotalSeconds, EstimateSecondsForViews(views, settings));
                return new CaptureResult { Success = true, OutputDirectory = outputDir };
            }
            catch (OperationCanceledException)
            {
                // タイルループ内でのキャンセル検出。PNG 保存前に脱出しているため書きかけのファイルは残らない
                return CaptureResult.Cancel();
            }
            catch (CaptureRenderFailedException e)
            {
                // 描画失敗 (リトライ後も黒フレーム)。PNG 保存前に送出されるため黒い PNG は残らない。
                // UI はダイアログ (result.Error)、CLI はエラーログと戻り値の双方で失敗を判別できる
                Debug.LogError($"[AvatarSetupTool] {e.Message}");
                return CaptureResult.Fail(e.Message);
            }
            finally
            {
                preview.Cleanup();
            }
        }

        /// <summary>
        /// UI 表示用のメモリ使用量見積もり (バイト)。モデルのアスペクト比が判明する前の
        /// 概算のため正方形を仮定する。実行時には実際の構図で再検証される。
        /// GIF は全フレームを保持してから書き出すため、フレームを 1 枚ずつ逐次エンコードする
        /// MP4 / ProRes より見積もりが大きくなる (これは仕様)。
        /// </summary>
        public static long EstimateRequiredBytes(int imageSize, CaptureOutputFormat format, CaptureViewMode viewMode)
        {
            return EstimateRequiredBytes(
                imageSize, format, viewMode, SystemInfo.maxTextureSize, SystemInfo.graphicsMemorySize);
        }

        /// <summary>
        /// 環境値 (maxTextureSize / graphicsMemoryMb) を引数で受ける実体。
        /// タイルサイズと適用倍率は実行時と同じレイアウト計算 (<see cref="TileLayout"/>) から取り、
        /// 見積もりと実行を一致させる。
        /// </summary>
        internal static long EstimateRequiredBytes(
            int imageSize, CaptureOutputFormat format, CaptureViewMode viewMode,
            int maxTextureSize, int graphicsMemoryMb)
        {
            var viewCount = viewMode == CaptureViewMode.Both ? 2 : 1;
            var renderPixels = (long)imageSize * imageSize;
            var layout = TileLayout.Compute(
                imageSize, imageSize, TileLayout.PreferredFactor(imageSize, imageSize),
                TileSideLimits.Compute(maxTextureSize, graphicsMemoryMb));
            var bytes = StillPeakBytes(renderPixels, layout);
            var animPixels = renderPixels / (SuperSampleFactor * SuperSampleFactor);
            if (format == CaptureOutputFormat.Gif)
            {
                // 構図ごとに 8 フレームを保持し、量子化でさらに数フレーム分使う
                bytes += animPixels * 4 * (Directions.Length + 2) * viewCount;
            }
            else if (format == CaptureOutputFormat.Mp4 || format == CaptureOutputFormat.ProRes422)
            {
                // 逐次エンコードのため縮小済みフレーム 1 枚分のバッファのみ
                bytes += animPixels * 4 * viewCount;
            }

            return bytes;
        }

        /// <summary>
        /// 静止画 1 枚のピークメモリ (バイト)。新エンコード経路の実バッファ構成に基づく:
        /// RGB24 合成バッファ (3 bytes/px) + PNG エンコード出力と iTXt コピー
        /// (圧縮後サイズの安全側概算で 1 byte/px × 2)、加えてタイル読み戻し
        /// (GetPixels32 配列 + Texture2D の CPU 側コピーで 4 bytes/px × 2)。
        /// </summary>
        private static long StillPeakBytes(long renderPixels, TileLayout layout)
        {
            return renderPixels * (3 + 1 * 2) + layout.MaxTileRenderPixels * 4 * 2;
        }

        /// <summary>メモリ見積もりに対して許容する上限 (実装メモリの半分)。</summary>
        public static long MemoryBudgetBytes => (long)SystemInfo.systemMemorySize * 1024 * 1024 / 2;

        /// <summary>
        /// 正方形構図を仮定しても H.264 の上限を確実に超える解像度設定かどうか (UI の事前判定用)。
        /// 横に広いモデルで幅だけ超えるケースは実行前チェック (<see cref="Capture"/> 内) が検出する。
        /// </summary>
        public static bool ExceedsH264Limit(int normalizedImageSize)
        {
            long videoSize = normalizedImageSize / SuperSampleFactor;
            return videoSize > H264MaxDimension || videoSize * videoSize > H264MaxPixels;
        }

        /// <summary>
        /// 出力ファイル合計サイズの概算 (バイト)。ディスク空き容量チェック用の目安で、
        /// ターゲット 1 体・正方形構図を仮定する (横に広いモデルや複数ターゲットでは増える)。
        /// 係数は実測 (グリッド背景 + テストモデル、1024px) の 1.5〜2 倍程度の安全率。
        /// </summary>
        public static long EstimateOutputBytes(CaptureSettings settings)
        {
            var size = (long)settings.NormalizedImageSize;
            var renderPixels = size * size;
            var animPixels = renderPixels / (SuperSampleFactor * SuperSampleFactor);
            var viewCount = settings.ViewCount;

            // PNG: 実測 0.15 bytes/px 程度 → 0.25 bytes/px で見積もる
            var bytes = renderPixels / 4 * Directions.Length * viewCount;

            var frames = (long)Mathf.RoundToInt(VideoFrameRate * settings.SecondsPerRotation);
            switch (settings.format)
            {
                case CaptureOutputFormat.Gif:
                    // 実測 0.16 bytes/px/フレーム程度 → 0.25 bytes/px
                    bytes += animPixels / 4 * Directions.Length * viewCount;
                    break;
                case CaptureOutputFormat.Mp4:
                    // MediaEncoder (High) のビットレートは解像度基準でコンテンツ依存が小さい。
                    // 実測 0.04 bit/px/フレーム程度 → 0.1 bit/px
                    bytes += animPixels * frames / 80 * viewCount;
                    break;
                case CaptureOutputFormat.ProRes422:
                    // 固定品質 (qScale 2) の実測 2.3 bit/px/フレーム程度 → 3 bit/px
                    bytes += animPixels * 3 / 8 * frames * viewCount;
                    break;
            }

            return bytes;
        }

        // 実測ベースのスループット (px/秒)。8K 静止画の実測 (Ryzen 9 9900X) で較正。
        // 静止画は新経路 (8K・SSAA x2・4096px タイル 4x4) の実測で、タイル切替・読み戻し・
        // RGB24 縮小合成のオーバーヘッドを含む。PNG は EncodeArrayToPNG 経路の実測
        private const double StillRenderRate = 90e6; // SSAA タイル描画 + 読み戻し + RGB24 縮小合成
        private const double PngEncodeRate = 26e6; // EncodeArrayToPNG + iTXt 挿入 + 書き込み
        private const double VideoRenderRate = 90e6; // 動画フレームの描画 + 読み戻し + 縮小
        private const double GifEncodeRate = 14e6; // GIF 量子化 + エンコード
        private const double Mp4EncodeRate = 50e6; // H.264 エンコード
        private const double ProResEncodeRate = 5e6; // ProRes エンコード

        private const string TimeCalibrationPrefsKey = "Hidano.AvatarSetupTool.ModelCapture.TimeCalibration";

        /// <summary>
        /// 実測時間 ÷ モデル推定時間の平滑値。撮影が成功するたびに更新され、
        /// 環境 (CPU/GPU/モデルの重さ) の差を見積もりへ反映する。
        /// </summary>
        internal static float TimeCalibrationFactor
        {
            get => Mathf.Clamp(EditorPrefs.GetFloat(TimeCalibrationPrefsKey, 1f), 0.25f, 4f);
            private set => EditorPrefs.SetFloat(TimeCalibrationPrefsKey, Mathf.Clamp(value, 0.25f, 4f));
        }

        /// <summary>
        /// 撮影にかかる時間の概算 (秒)。実測スループットに基づく推定へ、過去の撮影の
        /// 実測から得た較正係数 (<see cref="TimeCalibrationFactor"/>) を掛けて返す。
        /// ターゲット 1 体・正方形構図を仮定する。
        /// </summary>
        public static double EstimateCaptureSeconds(CaptureSettings settings)
        {
            var animSize = settings.NormalizedImageSize / SuperSampleFactor;
            var view = new ViewSpec(Vector3.zero, 1f, 0f, animSize, animSize);
            return EstimateSecondsForViews(new[] { (view, view) }, settings) * TimeCalibrationFactor;
        }

        /// <summary>
        /// 実際の構図での較正前の時間推定 (秒)。<see cref="EstimateCaptureSeconds"/> と
        /// 撮影後の較正 (<see cref="UpdateTimeCalibration"/>) が同じモデルを共有する。
        /// </summary>
        private static double EstimateSecondsForViews(
            (ViewSpec Full, ViewSpec Face)[] views, CaptureSettings settings)
        {
            var frames = Mathf.RoundToInt(VideoFrameRate * settings.SecondsPerRotation);
            var seconds = 0.0;
            foreach (var (full, face) in views)
            {
                foreach (var (view, capture) in new[]
                {
                    (full, settings.CaptureFull),
                    (face, settings.CaptureFace),
                })
                {
                    if (!capture)
                    {
                        continue;
                    }

                    var renderPixels = (double)view.RenderWidth * view.RenderHeight;
                    var stillFactor = StillLayout(view).Factor;
                    var animPixels = (double)view.AnimWidth * view.AnimHeight;
                    seconds += Directions.Length
                        * (renderPixels * stillFactor * stillFactor / StillRenderRate
                            + renderPixels / PngEncodeRate);
                    switch (settings.format)
                    {
                        case CaptureOutputFormat.Gif:
                            seconds += Directions.Length * animPixels / GifEncodeRate;
                            break;
                        case CaptureOutputFormat.Mp4:
                            seconds += frames * (renderPixels / VideoRenderRate + animPixels / Mp4EncodeRate);
                            break;
                        case CaptureOutputFormat.ProRes422:
                            seconds += frames * (renderPixels / VideoRenderRate + animPixels / ProResEncodeRate);
                            break;
                    }
                }
            }

            return seconds;
        }

        /// <summary>
        /// 撮影の実測時間で見積もり係数を較正する。短時間の撮影は
        /// ウォームアップ等の固定コストの比率が大きくノイズになるため無視する。
        /// </summary>
        private static void UpdateTimeCalibration(double actualSeconds, double predictedSeconds)
        {
            if (actualSeconds < 5.0 || predictedSeconds <= 0.0)
            {
                return;
            }

            var raw = (float)(actualSeconds / predictedSeconds);
            TimeCalibrationFactor = Mathf.Lerp(TimeCalibrationFactor, raw, 0.5f);
        }

        /// <summary>
        /// 出力先フォルダ名を決める。同名フォルダに中身がある場合は
        /// "名前 (1)"、"名前 (2)" … と連番を付けて別フォルダにする (上書き防止)。
        /// </summary>
        private static string UniqueOutputDirectory(string root, string name)
        {
            var dir = Path.Combine(root, name);
            for (var i = 1; Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any(); i++)
            {
                dir = Path.Combine(root, $"{name} ({i})");
            }

            return dir;
        }

        /// <summary>
        /// ワイルドカードの不足でファイル名が衝突しないよう、必要なトークンを補ったパターンを返す。
        /// &lt;View&gt; は全身と顔アップの両方を撮る場合のみ補完する (片方だけなら衝突しない)。
        /// </summary>
        internal static string EffectivePattern(string pattern, bool multipleTargets, bool forStill, bool bothViews)
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                pattern = CaptureSettings.DefaultFileNamePattern;
            }

            if (multipleTargets && !pattern.Contains("<Target>"))
            {
                pattern = "<Target>_" + pattern;
            }

            if (bothViews && !pattern.Contains("<View>"))
            {
                pattern += "_<View>";
            }

            if (forStill && !pattern.Contains("<Direction>"))
            {
                pattern += "_<Direction>";
            }

            return pattern;
        }

        /// <summary>
        /// 撮影対象をプレビューシーンへ配置する。Project のアセット (FBX / Prefab) は
        /// プレハブとして、Hierarchy 上の GameObject は現在の編集状態を複製して配置する。
        /// </summary>
        private static GameObject InstantiateSource(
            PreviewRenderUtility preview, GameObject source, out string modelName)
        {
            if (EditorUtility.IsPersistent(source))
            {
                var path = AssetDatabase.GetAssetPath(source);
                modelName = Path.GetFileNameWithoutExtension(path);
                var prefab = AssetDatabase.LoadMainAssetAtPath(path) as GameObject;
                return prefab == null ? null : preview.InstantiatePrefabInScene(prefab);
            }

            modelName = source.name;
            var clone = Object.Instantiate(source);
            clone.name = source.name;
            clone.transform.rotation = Quaternion.identity;
            // 複製で親から外れるため、見た目のスケールを維持する
            clone.transform.localScale = source.transform.lossyScale;
            clone.SetActive(true);

            // プレビューシーンの描画に影響するコンポーネントは無効化する
            foreach (var camera in clone.GetComponentsInChildren<Camera>(true))
            {
                camera.enabled = false;
            }

            foreach (var light in clone.GetComponentsInChildren<Light>(true))
            {
                light.enabled = false;
            }

            preview.AddSingleGO(clone);
            return clone;
        }

        /// <summary>
        /// GPU のテクスチャ上限と、処理中に確保するメモリの見積もりを検証する。
        /// 問題があればエラーメッセージを、無ければ null を返す。
        /// </summary>
        private static string ValidateMemory((ViewSpec Full, ViewSpec Face)[] views, CaptureSettings settings)
        {
            var maxTextureSize = SystemInfo.maxTextureSize;
            long peak = 0;
            long gifAccumulated = 0;
            foreach (var (full, face) in views)
            {
                long animPixels = 0;
                foreach (var (view, capture) in new[]
                {
                    (full, settings.CaptureFull),
                    (face, settings.CaptureFace),
                })
                {
                    if (!capture)
                    {
                        continue;
                    }

                    if (view.RenderWidth > maxTextureSize || view.RenderHeight > maxTextureSize)
                    {
                        return $"レンダリング解像度 {view.RenderWidth}x{view.RenderHeight} が"
                            + $"この環境のテクスチャ上限 ({maxTextureSize}px) を超えています。"
                            + "解像度を下げてください (横に広いモデルは幅が自動拡張されるため、より小さい解像度が必要です)。";
                    }

                    var renderPixels = (long)view.RenderWidth * view.RenderHeight;
                    // 静止画ピークは新エンコード経路の実バッファ構成で算定する。
                    // タイルサイズ・適用倍率は実行時と同じレイアウト計算から取り、見積もりと実行を一致させる
                    peak = Math.Max(peak, StillPeakBytes(renderPixels, StillLayout(view)));
                    animPixels += (long)view.AnimWidth * view.AnimHeight;

                    if (settings.format == CaptureOutputFormat.Mp4
                        && (view.AnimWidth > H264MaxDimension || view.AnimHeight > H264MaxDimension
                            || (long)view.AnimWidth * view.AnimHeight > H264MaxPixels))
                    {
                        return $"MP4 (H.264) の動画解像度 {view.AnimWidth}x{view.AnimHeight} が"
                            + $"エンコーダの上限 ({H264MaxDimension}x2304 相当) を超えています。"
                            + "ProRes 422 (MOV) を使うか、解像度を下げてください"
                            + " (横に広いモデルは幅が自動拡張されるため、より小さい解像度が必要です)。";
                    }
                }

                if (settings.format == CaptureOutputFormat.Gif)
                {
                    gifAccumulated = Math.Max(gifAccumulated, animPixels * 4 * (Directions.Length + 2));
                }
            }

            var required = peak + gifAccumulated;
            var budget = MemoryBudgetBytes;
            if (required > budget)
            {
                return $"推定メモリ使用量 {required / (1024 * 1024)} MB が上限の目安"
                    + $" ({budget / (1024 * 1024)} MB = 実装メモリの半分) を超えています。解像度を下げてください。";
            }

            return null;
        }

        private static string MakeUniqueName(string name, HashSet<string> usedNames)
        {
            var unique = name;
            for (var i = 2; !usedNames.Add(unique); i++)
            {
                unique = $"{name}_{i}";
            }

            return unique;
        }

        internal static void SetupCameraAndLights(PreviewRenderUtility preview)
        {
            var camera = preview.camera;
            camera.orthographic = true;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = BackgroundColor;

            // モデル側を回転させるため、ライトはカメラ基準で固定になる。
            // カメラは常に +Z を向くので、rotation = identity で真正面からの照射になる
            preview.lights[0].color = Color.white;
            preview.lights[0].intensity = 1f;
            preview.lights[0].transform.rotation = Quaternion.identity;
            preview.lights[1].intensity = 0f;
            preview.ambientColor = Color.white;
        }

        private static Bounds CalculateBounds(GameObject instance)
        {
            var renderers = instance.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0)
            {
                return new Bounds(instance.transform.position, Vector3.one);
            }

            var bounds = renderers[0].bounds;
            for (var i = 1; i < renderers.Length; i++)
            {
                bounds.Encapsulate(renderers[i].bounds);
            }

            return bounds;
        }

        /// <summary>
        /// ターゲット 1 体分の撮影構図 (全身 / 顔アップ) を求める。
        /// 方向ごとにバウンズを取り直すと構図が揺れて GIF/MP4 のフレーム間で
        /// ジッターになるため、正面向きのバウンズと回転掃引半径から
        /// 全方向共通の固定構図を作る。
        /// 全身は高さ基準 (頭上・足元の余白が常に一定) で、横に広いモデルは
        /// 縮小せず画像のアスペクト比を横に広げて収める。高さの解像度は常に固定。
        /// </summary>
        private static (ViewSpec Full, ViewSpec Face) ComputeViews(
            Animator animator, GameObject target, int animHeight)
        {
            target.transform.rotation = Quaternion.Euler(0f, Directions[0].Yaw, 0f);
            var bounds = CalculateBounds(target);
            var axis = target.transform.position;
            var radius = HorizontalRadiusAroundAxis(bounds, axis);

            var fullOrtho = Mathf.Max(bounds.extents.y, 0.001f) * FullBodyMargin;
            var aspect = Mathf.Clamp(radius * FullBodyMargin / fullOrtho, 1f, MaxFullBodyAspect);
            var animWidth = ToEven(Mathf.RoundToInt(animHeight * aspect));
            var full = new ViewSpec(
                new Vector3(axis.x, bounds.center.y, axis.z), fullOrtho, radius,
                animWidth, animHeight);

            var (faceCenter, faceSize) = GetFaceView(animator, bounds);
            var face = new ViewSpec(
                new Vector3(axis.x, faceCenter.y, axis.z), faceSize, radius,
                animHeight, animHeight);

            return (full, face);
        }

        /// <summary>H.264 は解像度が偶数である必要があるため、最も近い偶数へ切り上げる。</summary>
        private static int ToEven(int value)
        {
            return (value + 1) / 2 * 2;
        }

        /// <summary>
        /// Y 軸回転でバウンズが水平方向に掃く範囲の半径 (回転軸からの最大距離)。
        /// 回転中のどの向きでもモデルが画面と近クリップ面に収まるサイズの根拠になる。
        /// </summary>
        private static float HorizontalRadiusAroundAxis(Bounds bounds, Vector3 axis)
        {
            var dx = Mathf.Max(Mathf.Abs(bounds.min.x - axis.x), Mathf.Abs(bounds.max.x - axis.x));
            var dz = Mathf.Max(Mathf.Abs(bounds.min.z - axis.z), Mathf.Abs(bounds.max.z - axis.z));
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        /// <summary>
        /// 顔アップの構図を求める。
        /// 首 (Neck) ジョイントを画像中心に置き、頭頂のメッシュ (バウンズ上端) が
        /// 画像上端から高さの <see cref="FacePaddingRatio"/> 分の余白に収まるサイズを返す。
        /// </summary>
        private static (Vector3 Center, float OrthoSize) GetFaceView(Animator animator, Bounds bounds)
        {
            Transform neck = null;
            if (animator.isHuman)
            {
                neck = animator.GetBoneTransform(HumanBodyBones.Neck);
                if (neck == null)
                {
                    neck = animator.GetBoneTransform(HumanBodyBones.Head);
                }
            }

            Vector3 neckPosition;
            if (neck != null)
            {
                neckPosition = neck.position;
            }
            else
            {
                Debug.LogWarning(
                    $"[AvatarSetupTool] Neck ボーンが取得できないため、バウンズ上部 {FaceFallbackHeightRatio:P0} を首位置とみなします: {animator.gameObject.name}");
                neckPosition = new Vector3(
                    bounds.center.x,
                    bounds.max.y - bounds.size.y * FaceFallbackHeightRatio,
                    bounds.center.z);
            }

            // 中心 (首) から画像上端までは orthoSize。頭頂の上に画像高さの
            // FacePaddingRatio (= 2 * orthoSize * FacePaddingRatio) の余白を確保する。
            var neckToTop = Mathf.Max(bounds.max.y - neckPosition.y, bounds.size.y * 0.05f);
            var orthoSize = neckToTop / (1f - FacePaddingRatio * 2f);
            return (neckPosition, orthoSize);
        }

        private static void Warmup(PreviewRenderUtility preview, Bounds bounds)
        {
            var texture = RenderView(
                preview, bounds.center, bounds.extents.magnitude, bounds.extents.z, 256, 256);
            Object.DestroyImmediate(texture);
        }

        /// <summary>
        /// 背景のグリッド (1m 間隔の主線 + 10cm 間隔の細線) を、構図ごとにメッシュとして生成する。
        /// カメラは並行投影かつ -Z 固定なので、ワールド XY 平面上の線をモデルの後方に
        /// 置くだけで、画面上でワールド座標に一致したグリッドになる。y=0 (床) は主線。
        /// モデル側が回転してもグリッドは静止したままになる。
        /// </summary>
        private sealed class GridBackdrop : IDisposable
        {
            private readonly GameObject full;
            private readonly GameObject face;

            public GridBackdrop(PreviewRenderUtility preview, ViewSpec fullView, ViewSpec faceView)
            {
                full = CreateGridObject(preview, fullView);
                face = CreateGridObject(preview, faceView);
            }

            public void Show(bool showFull)
            {
                full.SetActive(showFull);
                face.SetActive(!showFull);
            }

            public void Dispose()
            {
                Destroy(full);
                Destroy(face);
            }

            private static void Destroy(GameObject go)
            {
                if (go == null)
                {
                    return;
                }

                var filter = go.GetComponent<MeshFilter>();
                if (filter != null && filter.sharedMesh != null)
                {
                    Object.DestroyImmediate(filter.sharedMesh);
                }

                var renderer = go.GetComponent<MeshRenderer>();
                if (renderer != null && renderer.sharedMaterial != null)
                {
                    Object.DestroyImmediate(renderer.sharedMaterial);
                }

                Object.DestroyImmediate(go);
            }

            private static GameObject CreateGridObject(PreviewRenderUtility preview, ViewSpec view)
            {
                var halfHeight = view.OrthoSize;
                var halfWidth = view.OrthoSize * view.RenderWidth / view.RenderHeight;
                // モデルの回転掃引より奥、ファークリップ (DepthExtent + 1) より手前に置く
                var z = view.Center.z + view.DepthExtent + 0.5f;

                // 線の太さは画面ピクセル基準 (主線 3px / 細線 1.5px) をワールド単位へ換算する
                var worldPerPixel = 2f * halfHeight / view.RenderHeight;
                var mainWidth = worldPerPixel * 3f;
                var subWidth = worldPerPixel * 1.5f;

                var vertices = new List<Vector3>();
                var colors = new List<Color32>();
                var triangles = new List<int>();

                var minX = view.Center.x - halfWidth - mainWidth;
                var maxX = view.Center.x + halfWidth + mainWidth;
                var minY = view.Center.y - halfHeight - mainWidth;
                var maxY = view.Center.y + halfHeight + mainWidth;

                // 細線 → 主線の順に追加する。マテリアルは ZWrite Off の半透明キューなので
                // 後から追加した三角形が上に描かれ、交点では主線が勝つ
                foreach (var isMainPass in new[] { false, true })
                {
                    var width = isMainPass ? mainWidth : subWidth;
                    var color = GridLineColor(isMainPass ? MainLineColor : SubLineColor);

                    // 縦線 (x = n * 0.1m)。10cm の細線は破線
                    for (var i = Mathf.CeilToInt(minX / SubLineSpacing);
                        i <= Mathf.FloorToInt(maxX / SubLineSpacing); i++)
                    {
                        if (IsMainLine(i) != isMainPass)
                        {
                            continue;
                        }

                        var x = i * SubLineSpacing;
                        if (isMainPass)
                        {
                            AddQuad(vertices, triangles, colors, color, z,
                                x - width / 2f, minY, x + width / 2f, maxY);
                        }
                        else
                        {
                            AddDashes(vertices, triangles, colors, color, z,
                                vertical: true, x, width, minY, maxY);
                        }
                    }

                    // 横線 (y = n * 0.1m)。y=0 の床線も主線になる
                    for (var i = Mathf.CeilToInt(minY / SubLineSpacing);
                        i <= Mathf.FloorToInt(maxY / SubLineSpacing); i++)
                    {
                        if (IsMainLine(i) != isMainPass)
                        {
                            continue;
                        }

                        var y = i * SubLineSpacing;
                        if (isMainPass)
                        {
                            AddQuad(vertices, triangles, colors, color, z,
                                minX, y - width / 2f, maxX, y + width / 2f);
                        }
                        else
                        {
                            AddDashes(vertices, triangles, colors, color, z,
                                vertical: false, y, width, minX, maxX);
                        }
                    }
                }

                var mesh = new Mesh { name = "CaptureGrid" };
                mesh.SetVertices(vertices);
                mesh.SetColors(colors);
                mesh.SetTriangles(triangles, 0);

                var go = new GameObject("CaptureGrid");
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var renderer = go.AddComponent<MeshRenderer>();
                // 頂点カラーをそのまま描くアンリットとして Sprites/Default を使う (URP でも動作する)
                renderer.sharedMaterial = new Material(Shader.Find("Sprites/Default"))
                {
                    hideFlags = HideFlags.HideAndDontSave,
                };
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;

                preview.AddSingleGO(go);
                go.SetActive(false);
                return go;
            }

            private static bool IsMainLine(int index)
            {
                return index % SubLinesPerMainLine == 0;
            }

            /// <summary>
            /// 破線を追加する。点は座標 0 を基準に <see cref="SubLineDashPeriod"/> ごとに置かれるため、
            /// どの線でも点の位置がワールド座標で揃い、線どうしの交点は必ず点の中心になる。
            /// vertical が真なら linePos は x 座標で from..to は y の範囲、偽ならその逆。
            /// </summary>
            private static void AddDashes(
                List<Vector3> vertices, List<int> triangles, List<Color32> colors, Color32 color,
                float z, bool vertical, float linePos, float width, float from, float to)
            {
                for (var i = Mathf.CeilToInt((from - SubLineDashLength / 2f) / SubLineDashPeriod);
                    i <= Mathf.FloorToInt((to + SubLineDashLength / 2f) / SubLineDashPeriod); i++)
                {
                    var center = i * SubLineDashPeriod;
                    var start = Mathf.Max(from, center - SubLineDashLength / 2f);
                    var end = Mathf.Min(to, center + SubLineDashLength / 2f);
                    if (end <= start)
                    {
                        continue;
                    }

                    if (vertical)
                    {
                        AddQuad(vertices, triangles, colors, color, z,
                            linePos - width / 2f, start, linePos + width / 2f, end);
                    }
                    else
                    {
                        AddQuad(vertices, triangles, colors, color, z,
                            start, linePos - width / 2f, end, linePos + width / 2f);
                    }
                }
            }

            /// <summary>
            /// 頂点カラーを描画用に変換する。Sprites/Default は頂点カラーを色空間変換せずに
            /// そのまま使うため、リニア色空間では sRGB 値をリニアへ変換してから渡さないと
            /// 線が明るく化けてしまう (10cm 線が背景より明るくなり、1m の主線が背景と
            /// ほぼ同じ値になって消える)。
            /// </summary>
            private static Color32 GridLineColor(Color32 srgb)
            {
                var color = (Color)srgb;
                if (QualitySettings.activeColorSpace == ColorSpace.Linear)
                {
                    color = color.linear;
                }

                return color;
            }

            private static void AddQuad(
                List<Vector3> vertices, List<int> triangles, List<Color32> colors, Color32 color,
                float z, float minX, float minY, float maxX, float maxY)
            {
                var start = vertices.Count;
                vertices.Add(new Vector3(minX, minY, z));
                vertices.Add(new Vector3(minX, maxY, z));
                vertices.Add(new Vector3(maxX, maxY, z));
                vertices.Add(new Vector3(maxX, minY, z));
                for (var i = 0; i < 4; i++)
                {
                    colors.Add(color);
                }

                triangles.Add(start);
                triangles.Add(start + 1);
                triangles.Add(start + 2);
                triangles.Add(start);
                triangles.Add(start + 2);
                triangles.Add(start + 3);
            }
        }

        /// <summary>progress がキャンセルを要求したら true を返す (進捗表示を兼ねる)。</summary>
        private static bool CancelRequested(Func<string, float, bool> progress, string text, float ratio)
        {
            return progress != null && progress(text, ratio);
        }

        /// <summary>
        /// モデルを連続回転させながらターンテーブル動画 (MP4 / ProRes MOV) を書き出す。
        /// 構図は ComputeViews が返した固定構図をそのまま使う。パスが null の構図はスキップする。
        /// キャンセルされた場合は書きかけの動画ファイルを削除して true を返す。
        /// </summary>
        private static bool CaptureTurntableVideos(
            PreviewRenderUtility preview, GameObject target, GridBackdrop backdrop,
            ViewSpec fullView, ViewSpec faceView, CaptureOutputFormat format, string fullPath, string facePath,
            string captureName, int frameCount, int total, Func<string, float, bool> progress, ref int step)
        {
            var label = format == CaptureOutputFormat.Mp4 ? "MP4" : "ProRes";
            var canceled = false;
            using (var fullWriter = fullPath == null ? null : CreateVideoWriter(format, fullPath, fullView))
            using (var faceWriter = facePath == null ? null : CreateVideoWriter(format, facePath, faceView))
            {
                for (var i = 0; i < frameCount; i++)
                {
                    if (CancelRequested(progress, $"{captureName}: {label} {i + 1}/{frameCount}", step / (float)total))
                    {
                        canceled = true;
                        break;
                    }

                    // PNG/GIF と同じく、正面から左向きへ回転する向き (ヨー角の増加方向)
                    var yaw = Directions[0].Yaw + 360f * i / frameCount;
                    target.transform.rotation = Quaternion.Euler(0f, yaw, 0f);

                    if (fullWriter != null)
                    {
                        backdrop.Show(showFull: true);
                        AddVideoFrame(preview, fullWriter, fullView);
                        step++;
                    }

                    if (faceWriter != null)
                    {
                        backdrop.Show(showFull: false);
                        AddVideoFrame(preview, faceWriter, faceView);
                        step++;
                    }
                }
            }

            if (canceled)
            {
                DeleteIfExists(fullPath);
                DeleteIfExists(facePath);
            }

            return canceled;
        }

        private static void DeleteIfExists(string path)
        {
            if (path != null && File.Exists(path))
            {
                File.Delete(path);
            }
        }

        private static IVideoFrameWriter CreateVideoWriter(CaptureOutputFormat format, string path, ViewSpec view)
        {
            try
            {
                return format == CaptureOutputFormat.Mp4
                    ? (IVideoFrameWriter)new Mp4Writer(path, view.AnimWidth, view.AnimHeight, VideoFrameRate)
                    : new ProResWriter(path, view.AnimWidth, view.AnimHeight, VideoFrameRate);
            }
            catch (InvalidOperationException e)
            {
                // MediaEncoder は解像度超過などで初期化に失敗すると素の例外を投げる
                throw new InvalidOperationException(
                    $"動画エンコーダの初期化に失敗しました ({view.AnimWidth}x{view.AnimHeight})。"
                    + "H.264 の解像度上限を超えているか、出力先に書き込めない可能性があります。"
                    + "ProRes 422 (MOV) を使うか、解像度を下げてください。", e);
            }
        }

        private static void AddVideoFrame(PreviewRenderUtility preview, IVideoFrameWriter writer, ViewSpec view)
        {
            var texture = RenderView(preview, view);
            var pixels = texture.GetPixels32();
            Object.DestroyImmediate(texture);
            writer.AddFrame(Downscale(pixels, view.RenderWidth, view.AnimWidth, view.AnimHeight, topDown: false));
        }

        /// <summary>
        /// 1 方向分をスーパーサンプリング付きで描画して PNG に保存し、makeGifFrame が真なら
        /// 同じ描画結果を GIF 用に縮小したフレームを返す (描画は 1 回で共用する)。
        /// RGB24 合成バッファを <see cref="ImageConversion.EncodeArrayToPNG"/> で直接 PNG 化する
        /// (Texture2D の全面中間コピーを経由しない省メモリ経路)。エンコード結果が null/空の場合は
        /// 防衛的に検出し、黒フレームと同経路 (<see cref="CaptureRenderFailedException"/>) で
        /// 失敗報告して壊れたファイルを残さない。
        /// debugText があれば PNG の iTXt メタデータとしても埋め込む。
        /// checkCancel はタイルループのキャンセル判定として RenderStill へ渡す。
        /// shotLabel は描画失敗時の診断メッセージに使う構図/方向の識別子 (例 "Avatar full 045")。
        /// </summary>
        internal static Color32[] CaptureShot(
            PreviewRenderUtility preview, ViewSpec view, string filePath, bool makeGifFrame,
            Func<bool> checkCancel, string shotLabel, string debugText = null)
        {
            var pixels = RenderStill(preview, view, checkCancel, shotLabel);
            var bytes = ImageConversion.EncodeArrayToPNG(
                pixels, GraphicsFormat.R8G8B8_SRGB,
                (uint)view.RenderWidth, (uint)view.RenderHeight, (uint)(view.RenderWidth * 3));
            if (bytes == null || bytes.Length == 0)
            {
                throw new CaptureRenderFailedException(
                    $"PNG エンコードに失敗しました (EncodeArrayToPNG が空の結果を返しました): {shotLabel}、"
                    + $"出力解像度 {view.RenderWidth}x{view.RenderHeight}。");
            }

            if (!string.IsNullOrEmpty(debugText))
            {
                bytes = PngMetadata.WithText(bytes, "Comment", debugText);
            }

            File.WriteAllBytes(filePath, bytes);
            return makeGifFrame
                ? DownscaleRgb24ToColor32(pixels, view.RenderWidth, view.RenderHeight, view.AnimWidth, view.AnimHeight)
                : null;
        }

        /// <summary>
        /// 静止画のタイル分割レイアウト。環境値 (maxTextureSize / graphicsMemorySize) を注入して
        /// 算出し、描画 (<see cref="RenderStill"/>) と見積もり (ValidateMemory /
        /// EstimateSecondsForViews) が同一のレイアウト結果を参照する。
        /// SSAA 倍率は解像度既定 (最長辺 2048px 以下は 4、超は 2) で、VRAM 制約はタイル分割が
        /// 吸収するため VRAM を理由とする降格は行わない。
        /// </summary>
        private static TileLayout StillLayout(ViewSpec view)
        {
            return TileLayout.Compute(
                view.RenderWidth, view.RenderHeight,
                TileLayout.PreferredFactor(view.RenderWidth, view.RenderHeight),
                TileSideLimits.Compute(SystemInfo.maxTextureSize, SystemInfo.graphicsMemorySize));
        }

        /// <summary>
        /// 静止画 1 枚をスーパーサンプリング付きで描画し、出力解像度の RGB24 (3 bytes/px)・
        /// ボトムアップ行順の合成バッファを返す (EncodeArrayToPNG がそのまま受け取れる形式)。
        /// <see cref="TileLayout"/> の決定に従い、正射影カメラをずらしながら非一様タイル
        /// (端タイルは剰余サイズ) で分割描画する (正射影なので分割しても結果は一致する。
        /// 単一パスは 1×1 タイルとして同一フローで扱う)。タイル用テクスチャは各タイル処理後に
        /// 即破棄し、同時生存を 1 枚に保つ。
        /// checkCancel は各タイルの描画前に呼ばれ、true でキャンセル要求として
        /// OperationCanceledException を送出する (PNG 保存前に脱出するため書きかけのファイルは残らない)。
        /// タイル読み戻し直後 (合成前) に全画素黒を検査し、黒検出時は同一タイルを同一プレビューで
        /// 1 回だけ再描画・再検査する。再失敗時は診断情報付きの
        /// <see cref="CaptureRenderFailedException"/> を送出し、部分結果を返さない。
        /// </summary>
        private static byte[] RenderStill(
            PreviewRenderUtility preview, ViewSpec view, Func<bool> checkCancel, string shotLabel)
        {
            return RenderStill(preview, view, StillLayout(view), checkCancel, shotLabel);
        }

        /// <summary>
        /// テスト用フック。タイル読み戻し直後のピクセルをこの関数で差し替え、GPU の実失敗を
        /// 起こさずに黒フレーム失敗経路 (検出 → リトライ → 失敗伝搬) を再現する。
        /// 製品経路では常に null (呼び出しごとのオーバーヘッドは null チェックのみ)。
        /// </summary>
        internal static Func<Color32[], Color32[]> TileReadbackHook;

        /// <summary>
        /// レイアウトを引数で受ける実体。製品経路は <see cref="StillLayout"/> の結果を渡し、
        /// 等価性テストは辺長上限を強制的に絞ったレイアウトを注入して
        /// 多タイル描画と単一パス描画を同一フローで比較する。
        /// </summary>
        internal static byte[] RenderStill(
            PreviewRenderUtility preview, ViewSpec view, TileLayout layout,
            Func<bool> checkCancel, string shotLabel)
        {
            if (layout.Factor < layout.RequestedFactor)
            {
                Debug.LogWarning(
                    $"[AvatarSetupTool] SSAA 倍率を要求値 {layout.RequestedFactor} から {layout.Factor} に下げました"
                    + $" ({view.RenderWidth}x{view.RenderHeight}、レンダ辺長上限が極端に小さい環境のため)。");
            }

            var width = view.RenderWidth;
            var height = view.RenderHeight;
            var factor = layout.Factor;
            var result = new byte[width * height * 3];

            // タイル矩形 (出力 px) → カメラ矩形 (orthoSize・中心座標) の換算は double で計算し、
            // カメラへ設定する直前に float 化する。端の剰余タイルでもピクセル境界に正確に一致させる
            var worldPerPixel = 2.0 * view.OrthoSize / height;

            for (var ty = 0; ty < layout.TilesY; ty++)
            {
                for (var tx = 0; tx < layout.TilesX; tx++)
                {
                    if (checkCancel != null && checkCancel())
                    {
                        throw new OperationCanceledException();
                    }

                    var rect = layout.GetTile(tx, ty);
                    var tileOrtho = view.OrthoSize * (double)rect.Height / height;
                    var tileCenter = new Vector3(
                        (float)(view.Center.x + (rect.X + rect.Width * 0.5 - width * 0.5) * worldPerPixel),
                        (float)(view.Center.y + (rect.Y + rect.Height * 0.5 - height * 0.5) * worldPerPixel),
                        view.Center.z);

                    Color32[] RenderTilePixels()
                    {
                        var texture = RenderView(preview, tileCenter, (float)tileOrtho, view.DepthExtent,
                            rect.Width * factor, rect.Height * factor);
                        var tilePixels = texture.GetPixels32();
                        Object.DestroyImmediate(texture);
                        return TileReadbackHook == null ? tilePixels : TileReadbackHook(tilePixels);
                    }

                    // 背景は常に不透明グレーのため全画素黒は描画失敗 (TDR / 確保失敗など)。
                    // 合成前に検査し、同一タイルを同一プレビューで 1 回だけ再描画・再検査する
                    var pixels = RenderTilePixels();
                    if (IsAllBlack(pixels))
                    {
                        Debug.LogWarning(
                            $"[AvatarSetupTool] 黒フレームを検出したためタイルを再描画します: {shotLabel}"
                            + $" タイル ({tx}, {ty}) / {layout.TilesX}x{layout.TilesY}");
                        pixels = RenderTilePixels();
                        if (IsAllBlack(pixels))
                        {
                            throw new CaptureRenderFailedException(
                                $"レンダリングに失敗しました (リトライ後も全画素が黒のままです): {shotLabel}、"
                                + $"出力解像度 {width}x{height}、"
                                + $"SSAA 要求 x{layout.RequestedFactor} / 適用 x{factor}、"
                                + $"タイル ({tx}, {ty}) / {layout.TilesX}x{layout.TilesY}、"
                                + $"タイルレンダサイズ {rect.Width * factor}x{rect.Height * factor}、"
                                + $"maxTextureSize={SystemInfo.maxTextureSize}、"
                                + $"graphicsMemorySize={SystemInfo.graphicsMemorySize}MB。"
                                + "GPU ドライバの応答停止 (TDR) や VRAM 不足が原因の可能性があります。");
                        }
                    }

                    DownscaleIntoRgb24(pixels, rect.Width * factor, factor,
                        result, width, rect.X, rect.Y, rect.Width, rect.Height);
                }
            }

            return result;
        }

        /// <summary>
        /// 全画素が RGB=(0,0,0) なら true (アルファ無視)。描画失敗 (黒フレーム) の判定に使う。
        /// 背景は常に不透明グレーのため、正常フレームに全画素黒はあり得ない。
        /// 非黒画素の発見で早期終了する (正常系はほぼ先頭で終了する)。
        /// </summary>
        internal static bool IsAllBlack(Color32[] pixels)
        {
            for (var i = 0; i < pixels.Length; i++)
            {
                var p = pixels[i];
                if (p.r != 0 || p.g != 0 || p.b != 0)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// source (ボトムアップ行順) をボックス平均で 1/factor に縮小し、dest の指定位置へ書き込む。
        /// </summary>
        internal static void DownscaleInto(
            Color32[] source, int sourceWidth, int factor,
            Color32[] dest, int destStride, int destX, int destY, int blockWidth, int blockHeight)
        {
            var samples = factor * factor;
            for (var y = 0; y < blockHeight; y++)
            {
                for (var x = 0; x < blockWidth; x++)
                {
                    var r = 0;
                    var g = 0;
                    var b = 0;
                    for (var dy = 0; dy < factor; dy++)
                    {
                        var rowStart = (y * factor + dy) * sourceWidth + x * factor;
                        for (var dx = 0; dx < factor; dx++)
                        {
                            var p = source[rowStart + dx];
                            r += p.r;
                            g += p.g;
                            b += p.b;
                        }
                    }

                    dest[(destY + y) * destStride + destX + x] = new Color32(
                        (byte)((r + samples / 2) / samples),
                        (byte)((g + samples / 2) / samples),
                        (byte)((b + samples / 2) / samples),
                        255);
                }
            }
        }

        /// <summary>
        /// キャプチャ画像をアニメーション解像度へボックス平均で縮小する (スーパーサンプリングを兼ねる)。
        /// source は下端の行から始まる (ボトムアップ)。GIF はトップダウンの行順が
        /// 必要なため topDown 指定で上下反転し、MP4 (SetPixels32) はそのままの行順で返す。
        /// </summary>
        internal static Color32[] Downscale(
            Color32[] source, int sourceWidth, int destWidth, int destHeight, bool topDown)
        {
            var factor = sourceWidth / destWidth;
            var result = new Color32[destWidth * destHeight];
            DownscaleInto(source, sourceWidth, factor, result, destWidth, 0, 0, destWidth, destHeight);
            if (topDown)
            {
                var row = new Color32[destWidth];
                for (var y = 0; y < destHeight / 2; y++)
                {
                    var top = y * destWidth;
                    var bottom = (destHeight - 1 - y) * destWidth;
                    Array.Copy(result, top, row, 0, destWidth);
                    Array.Copy(result, bottom, result, top, destWidth);
                    Array.Copy(row, 0, result, bottom, destWidth);
                }
            }

            return result;
        }

        /// <summary>
        /// source (Color32[]、ボトムアップ行順のタイル読み戻し) をボックス平均で 1/factor に縮小し、
        /// dest (RGB24・3 bytes/px の静止画合成バッファ) の出力矩形
        /// (destX, destY, blockWidth, blockHeight) へ書き込む。destStride は dest の行幅 (px)。
        /// 丸めは <see cref="DownscaleInto"/> と同一 (samples/2 加算の四捨五入) で、
        /// タイル分割合成の結果は全域を一括縮小した結果とチャネル値が一致する。
        /// EncodeArrayToPNG はバッファをボトムアップ行順で解釈する (PngEncodeTests で確定。
        /// design.md の top-down 推定は反証済み) ため、合成バッファもボトムアップとし、
        /// 読み戻しと同じ行順のまま行反転なしで書き込む。
        /// </summary>
        internal static void DownscaleIntoRgb24(
            Color32[] source, int sourceWidth, int factor,
            byte[] dest, int destStride, int destX, int destY, int blockWidth, int blockHeight)
        {
            var samples = factor * factor;
            for (var y = 0; y < blockHeight; y++)
            {
                for (var x = 0; x < blockWidth; x++)
                {
                    var r = 0;
                    var g = 0;
                    var b = 0;
                    for (var dy = 0; dy < factor; dy++)
                    {
                        var rowStart = (y * factor + dy) * sourceWidth + x * factor;
                        for (var dx = 0; dx < factor; dx++)
                        {
                            var p = source[rowStart + dx];
                            r += p.r;
                            g += p.g;
                            b += p.b;
                        }
                    }

                    var offset = ((destY + y) * destStride + destX + x) * 3;
                    dest[offset] = (byte)((r + samples / 2) / samples);
                    dest[offset + 1] = (byte)((g + samples / 2) / samples);
                    dest[offset + 2] = (byte)((b + samples / 2) / samples);
                }
            }
        }

        /// <summary>
        /// RGB24 (3 bytes/px)・ボトムアップ行順の静止画合成バッファをアニメーション解像度の
        /// Color32[] (top-down 行順、GIF 用) へボックス平均で縮小する。
        /// 丸めは <see cref="DownscaleInto"/> と同一、アルファは 255 固定で、
        /// 現行の Downscale(topDown: true) と同一の画素値になる (GIF フレーム不変の根拠)。
        /// GIF が要求する top-down への行反転はこの縮小 1 箇所でのみ行う。
        /// 前提: sourceHeight == destHeight * (sourceWidth / destWidth)。
        /// </summary>
        internal static Color32[] DownscaleRgb24ToColor32(
            byte[] source, int sourceWidth, int sourceHeight, int destWidth, int destHeight)
        {
            var factor = sourceWidth / destWidth;
            var samples = factor * factor;
            var result = new Color32[destWidth * destHeight];
            for (var y = 0; y < destHeight; y++)
            {
                // 出力行 y (top-down) はボトムアップなソースの上から y 番目のブロック行に対応する
                var sourceBlockRow = sourceHeight - (y + 1) * factor;
                for (var x = 0; x < destWidth; x++)
                {
                    var r = 0;
                    var g = 0;
                    var b = 0;
                    for (var dy = 0; dy < factor; dy++)
                    {
                        var rowStart = ((sourceBlockRow + dy) * sourceWidth + x * factor) * 3;
                        for (var dx = 0; dx < factor; dx++)
                        {
                            r += source[rowStart + dx * 3];
                            g += source[rowStart + dx * 3 + 1];
                            b += source[rowStart + dx * 3 + 2];
                        }
                    }

                    result[y * destWidth + x] = new Color32(
                        (byte)((r + samples / 2) / samples),
                        (byte)((g + samples / 2) / samples),
                        (byte)((b + samples / 2) / samples),
                        255);
                }
            }

            return result;
        }

        private static Texture2D RenderView(PreviewRenderUtility preview, ViewSpec view)
        {
            return RenderView(
                preview, view.Center, view.OrthoSize, view.DepthExtent, view.RenderWidth, view.RenderHeight);
        }

        private static Texture2D RenderView(
            PreviewRenderUtility preview, Vector3 center, float orthoSize, float depthExtent,
            int imageWidth, int imageHeight)
        {
            var camera = preview.camera;
            var distance = depthExtent + 1f;
            camera.orthographicSize = orthoSize;
            camera.aspect = imageWidth / (float)imageHeight;
            camera.transform.SetPositionAndRotation(center + Vector3.back * distance, Quaternion.identity);
            camera.nearClipPlane = 0.01f;
            camera.farClipPlane = distance + depthExtent + 1f;

            preview.BeginStaticPreview(new Rect(0f, 0f, imageWidth, imageHeight));
            preview.Render(true);
            return preview.EndStaticPreview();
        }
    }
}
