using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Hidano.AvatarSetupTool.Editor
{
    /// <summary>キャプチャ実行の結果。</summary>
    public sealed class CaptureResult
    {
        public bool Success;
        public string Error;
        public string OutputDirectory;

        public static CaptureResult Fail(string error) => new CaptureResult { Error = error };
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
        private static readonly Color32 SubLineColor = new Color32(164, 164, 164, 255);
        private static readonly Color32 MainLineColor = new Color32(128, 128, 128, 255);
        private const float SubLineAlpha = 0.6f; // 10cm 線は半透明にして 1m の主線と視覚的に区別する
        private const float SubLineSpacing = 0.1f; // 10cm 間隔の細線。10 本ごと (1m) に主線
        private const int SubLinesPerMainLine = 10;

        /// <summary>
        /// カメラは -Z 側に固定し、モデル側を Y 回転させて 8 方向を撮る。
        /// yaw=180 でモデルの正面がカメラを向く。
        /// 名前の番号は、ファイル名順に並べたとき正面から左向きへ
        /// 回転していく順序になるように振っている (GIF のフレーム順も同じ)。
        /// </summary>
        private static readonly (string Name, float Yaw)[] Directions =
        {
            ("01_front", 180f),
            ("02_front_left", -135f),
            ("03_left", -90f),
            ("04_back_left", -45f),
            ("05_back", 0f),
            ("06_back_right", 45f),
            ("07_right", 90f),
            ("08_front_right", 135f),
        };

        /// <summary>
        /// ターゲット 1 体分の固定構図。8 方向の静止画とターンテーブル動画のすべてで共用する。
        /// </summary>
        private readonly struct ViewSpec
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
        /// キャプチャを実行する。progress には表示用テキストと 0〜1 の進捗率が渡される。
        /// </summary>
        public static CaptureResult Capture(
            GameObject source, CaptureSettings settings, Action<string, float> progress = null)
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

                var allRenderers = instance.GetComponentsInChildren<Renderer>(true);

                // SRP では最初のレンダリングが空になることがあるため、1 回捨てレンダリングする
                Warmup(preview, CalculateBounds(instance));

                var timestamp = DateTime.Now;
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

                    using (var backdrop = new GridBackdrop(preview, fullView, faceView))
                    {
                        var fullGifFrames = new List<Color32[]>(Directions.Length);
                        var faceGifFrames = new List<Color32[]>(Directions.Length);
                        foreach (var (dirName, yaw) in Directions)
                        {
                            target.transform.rotation = Quaternion.Euler(0f, yaw, 0f);

                            progress?.Invoke($"{captureName}: {dirName}", step / (float)total);

                            var makeGifFrame = settings.format == CaptureOutputFormat.Gif;

                            if (settings.CaptureFull)
                            {
                                backdrop.Show(showFull: true);
                                var fullName = ResolveName(stillPattern, dirName, "full",
                                    fullView.RenderWidth, fullView.RenderHeight);
                                var fullFrame = CaptureShot(preview, fullView,
                                    Path.Combine(outputDir, fullName + ".png"), makeGifFrame);
                                step++;
                                if (makeGifFrame)
                                {
                                    fullGifFrames.Add(fullFrame);
                                }
                            }

                            if (settings.CaptureFace)
                            {
                                backdrop.Show(showFull: false);
                                var faceName = ResolveName(stillPattern, dirName, "face",
                                    faceView.RenderWidth, faceView.RenderHeight);
                                var faceFrame = CaptureShot(preview, faceView,
                                    Path.Combine(outputDir, faceName + ".png"), makeGifFrame);
                                step++;
                                if (makeGifFrame)
                                {
                                    faceGifFrames.Add(faceFrame);
                                }
                            }
                        }

                        if (settings.format == CaptureOutputFormat.Gif)
                        {
                            progress?.Invoke($"{captureName}: GIF を書き出し中", step / (float)total);
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
                                    Path.Combine(outputDir, ResolveName(videoPattern, null, "face",
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
                                ? Path.Combine(outputDir, ResolveName(videoPattern, null, "face",
                                    faceView.AnimWidth, faceView.AnimHeight) + extension)
                                : null;
                            CaptureTurntableVideos(
                                preview, target, backdrop, fullView, faceView, settings.format,
                                fullPath, facePath, captureName, videoFrameCount, total, progress, ref step);
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
                return new CaptureResult { Success = true, OutputDirectory = outputDir };
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
            var viewCount = viewMode == CaptureViewMode.Both ? 2 : 1;
            var renderPixels = (long)imageSize * imageSize;
            // RT + 読み戻し Texture2D + GetPixels32 + PNG エンコードバッファ
            var bytes = renderPixels * 4 * 4;
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

        /// <summary>メモリ見積もりに対して許容する上限 (実装メモリの半分)。</summary>
        public static long MemoryBudgetBytes => (long)SystemInfo.systemMemorySize * 1024 * 1024 / 2;

        /// <summary>
        /// 出力ファイル合計サイズの概算 (バイト)。ディスク空き容量チェック用の目安で、
        /// ターゲット 1 体・正方形構図を仮定する (横に広いモデルや複数ターゲットでは増える)。
        /// </summary>
        public static long EstimateOutputBytes(CaptureSettings settings)
        {
            var size = (long)settings.NormalizedImageSize;
            var renderPixels = size * size;
            var animPixels = renderPixels / (SuperSampleFactor * SuperSampleFactor);
            var viewCount = settings.ViewCount;

            // PNG: グリッド背景 + モデルでおおむね 0.5 bytes/px 程度
            var bytes = renderPixels / 2 * Directions.Length * viewCount;

            var frames = (long)Mathf.RoundToInt(VideoFrameRate * settings.SecondsPerRotation);
            switch (settings.format)
            {
                case CaptureOutputFormat.Gif:
                    bytes += animPixels / 2 * Directions.Length * viewCount;
                    break;
                case CaptureOutputFormat.Mp4:
                    // VideoBitrateMode.High ≒ 0.3 bit/px/フレーム程度
                    bytes += animPixels * frames / 25 * viewCount;
                    break;
                case CaptureOutputFormat.ProRes422:
                    // 固定品質 (qScale 2) の実測 + 余裕でおおむね 3 bit/px/フレーム
                    bytes += animPixels * 3 / 8 * frames * viewCount;
                    break;
            }

            return bytes;
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
                    peak = Math.Max(peak, renderPixels * 4 * 4);
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

        private static void SetupCameraAndLights(PreviewRenderUtility preview)
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
                    var color = GridLineColor(
                        isMainPass ? MainLineColor : SubLineColor, isMainPass ? 1f : SubLineAlpha);

                    // 縦線 (x = n * 0.1m)
                    for (var i = Mathf.CeilToInt(minX / SubLineSpacing);
                        i <= Mathf.FloorToInt(maxX / SubLineSpacing); i++)
                    {
                        if (IsMainLine(i) != isMainPass)
                        {
                            continue;
                        }

                        var x = i * SubLineSpacing;
                        AddQuad(vertices, triangles, colors, color, z,
                            x - width / 2f, minY, x + width / 2f, maxY);
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
                        AddQuad(vertices, triangles, colors, color, z,
                            minX, y - width / 2f, maxX, y + width / 2f);
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
            /// 頂点カラーを描画用に変換する。Sprites/Default は頂点カラーを色空間変換せずに
            /// そのまま使うため、リニア色空間では sRGB 値をリニアへ変換してから渡さないと
            /// 線が明るく化けてしまう (10cm 線が背景より明るくなり、1m の主線が背景と
            /// ほぼ同じ値になって消える)。また同シェーダは乗算済みアルファ
            /// (Blend One OneMinusSrcAlpha) なので RGB にアルファを掛けておく。
            /// </summary>
            private static Color32 GridLineColor(Color32 srgb, float alpha)
            {
                var color = (Color)srgb;
                if (QualitySettings.activeColorSpace == ColorSpace.Linear)
                {
                    color = color.linear;
                }

                return new Color(color.r * alpha, color.g * alpha, color.b * alpha, alpha);
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

        /// <summary>
        /// モデルを連続回転させながらターンテーブル動画 (MP4 / ProRes MOV) を書き出す。
        /// 構図は ComputeViews が返した固定構図をそのまま使う。パスが null の構図はスキップする。
        /// </summary>
        private static void CaptureTurntableVideos(
            PreviewRenderUtility preview, GameObject target, GridBackdrop backdrop,
            ViewSpec fullView, ViewSpec faceView, CaptureOutputFormat format, string fullPath, string facePath,
            string captureName, int frameCount, int total, Action<string, float> progress, ref int step)
        {
            var label = format == CaptureOutputFormat.Mp4 ? "MP4" : "ProRes";
            using (var fullWriter = fullPath == null ? null : CreateVideoWriter(format, fullPath, fullView))
            using (var faceWriter = facePath == null ? null : CreateVideoWriter(format, facePath, faceView))
            {
                for (var i = 0; i < frameCount; i++)
                {
                    progress?.Invoke($"{captureName}: {label} {i + 1}/{frameCount}", step / (float)total);

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
            writer.AddFrame(Downscale(texture, view.AnimWidth, view.AnimHeight, topDown: false));
            Object.DestroyImmediate(texture);
        }

        /// <summary>
        /// 1 方向分を高解像度で描画して PNG に保存し、makeGifFrame が真なら
        /// 同じ描画結果を GIF 用に縮小したフレームを返す (描画は 1 回で共用する)。
        /// </summary>
        private static Color32[] CaptureShot(
            PreviewRenderUtility preview, ViewSpec view, string filePath, bool makeGifFrame)
        {
            var texture = RenderView(preview, view);
            File.WriteAllBytes(filePath, texture.EncodeToPNG());
            var gifFrame = makeGifFrame ? Downscale(texture, view.AnimWidth, view.AnimHeight, topDown: true) : null;
            Object.DestroyImmediate(texture);
            return gifFrame;
        }

        /// <summary>
        /// キャプチャ画像をアニメーション解像度へボックス平均で縮小する (スーパーサンプリングを兼ねる)。
        /// GetPixels32 は下端の行から始まる (ボトムアップ)。GIF はトップダウンの行順が
        /// 必要なため topDown 指定で上下反転し、MP4 (SetPixels32) はそのままの行順で返す。
        /// </summary>
        private static Color32[] Downscale(Texture2D texture, int destWidth, int destHeight, bool topDown)
        {
            const int factor = SuperSampleFactor;
            const int samples = factor * factor;
            var sourceWidth = texture.width;
            var source = texture.GetPixels32();
            var result = new Color32[destWidth * destHeight];
            for (var y = 0; y < destHeight; y++)
            {
                var destY = topDown ? destHeight - 1 - y : y;
                for (var x = 0; x < destWidth; x++)
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

                    result[destY * destWidth + x] = new Color32(
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
