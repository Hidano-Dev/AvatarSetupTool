using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Hidano.AvatarSetupTool.Editor
{
    /// <summary>
    /// Project ウィンドウで選択した FBX / Prefab を 8 方向 × (全身 / 顔アップ) の
    /// 計 16 枚の PNG としてキャプチャし、あわせてターンテーブル動画
    /// (全身 / 顔アップの 2 本) を生成する。
    /// 動画はデフォルトで滑らかに 1 回転する MP4 (H.264、Play モード不要の
    /// エディタ内エンコード)。「(GIF)」付きメニューでは従来どおり
    /// 8 方向を 2 秒間隔で繋いだループ GIF になる。
    /// Avatar が設定された Animator を撮影対象とし、複数ある場合は
    /// オブジェクト名ごとにすべて撮影する。見つからない場合はダイアログで警告する。
    /// カメラは並行投影・背景は白単色。
    /// 出力先: 実行時にフォルダ選択ダイアログで指定する。
    /// 初期値はマイピクチャ、以降は前回選択したフォルダを記憶する。
    /// </summary>
    public static class FbxModelCaptureTool
    {
        private const string MenuPath = "Assets/Avatar Setup Tool/Capture Model Images";
        private const string GifMenuPath = "Assets/Avatar Setup Tool/Capture Model Images (GIF)";
        private const string OutputDirPrefsKey = "Hidano.AvatarSetupTool.FbxModelCaptureTool.OutputDir";
        private const int ImageSize = 2048;
        private const int AnimationImageSize = 1024; // GIF/MP4 共通。ImageSize の約数にすること(縮小がボックス平均のため)
        private const int GifFrameDelayCentiseconds = 200;
        private const int VideoFrameRate = 30;
        private const float VideoSecondsPerRotation = 6f;
        private const float FullBodyMargin = 1.05f;
        private const float FacePaddingRatio = 0.1f;
        private const float FaceFallbackHeightRatio = 0.15f;

        private enum AnimationFormat
        {
            Mp4,
            Gif,
        }

        private static int VideoFrameCount => Mathf.RoundToInt(VideoFrameRate * VideoSecondsPerRotation);

        /// <summary>
        /// カメラは -Z 側に固定し、モデル側を Y 回転させて 8 方向を撮る。
        /// yaw=180 でモデルの正面がカメラを向く。
        /// 名前の番号は、ファイル名順に並べたとき正面から左向きへ
        /// 回転していく順序になるように振っている(GIF のフレーム順も同じ)。
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

        [MenuItem(MenuPath)]
        private static void CaptureAsMp4()
        {
            Capture(AnimationFormat.Mp4);
        }

        [MenuItem(GifMenuPath)]
        private static void CaptureAsGif()
        {
            Capture(AnimationFormat.Gif);
        }

        private static void Capture(AnimationFormat format)
        {
            var outputRoot = SelectOutputRoot();
            if (string.IsNullOrEmpty(outputRoot))
            {
                return;
            }

            var assetsWithoutAvatar = new List<string>();
            foreach (var obj in Selection.objects)
            {
                var path = AssetDatabase.GetAssetPath(obj);
                if (IsCapturableAsset(path) && !CaptureAsset(path, outputRoot, format))
                {
                    assetsWithoutAvatar.Add(Path.GetFileName(path));
                }
            }

            if (assetsWithoutAvatar.Count > 0)
            {
                EditorUtility.DisplayDialog(
                    "Capture Model Images",
                    "Avatar が設定された Animator が見つからなかったため、以下のアセットはスキップしました:\n\n"
                    + string.Join("\n", assetsWithoutAvatar),
                    "OK");
            }
        }

        [MenuItem(MenuPath, true)]
        [MenuItem(GifMenuPath, true)]
        private static bool ValidateCapture()
        {
            foreach (var obj in Selection.objects)
            {
                if (IsCapturableAsset(AssetDatabase.GetAssetPath(obj)))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsCapturableAsset(string path)
        {
            return !string.IsNullOrEmpty(path)
                && (path.EndsWith(".fbx", System.StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith(".prefab", System.StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// 出力先フォルダをダイアログで選択させる。
        /// 初期表示は前回選択したフォルダ(EditorPrefs)、無ければマイピクチャ。
        /// キャンセル時は null を返す。
        /// </summary>
        private static string SelectOutputRoot()
        {
            var lastDir = EditorPrefs.GetString(OutputDirPrefsKey, string.Empty);
            if (string.IsNullOrEmpty(lastDir) || !Directory.Exists(lastDir))
            {
                lastDir = GetDefaultOutputRoot();
            }

            var selected = EditorUtility.OpenFolderPanel("キャプチャの出力先を選択", lastDir, string.Empty);
            if (string.IsNullOrEmpty(selected))
            {
                return null;
            }

            EditorPrefs.SetString(OutputDirPrefsKey, selected);
            return selected;
        }

        /// <summary>
        /// マイピクチャの実パスを OS に問い合わせて返す。
        /// ユーザーがフォルダを移動している場合も追従する。取得できなければプロジェクトルート。
        /// </summary>
        private static string GetDefaultOutputRoot()
        {
            var pictures = System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyPictures);
            return string.IsNullOrEmpty(pictures)
                ? Path.GetDirectoryName(Application.dataPath)
                : pictures;
        }

        /// <summary>
        /// アセット内の Avatar 付き Animator をすべて撮影する。
        /// Avatar 付き Animator が 1 つも見つからなければ false を返す(撮影は行わない)。
        /// </summary>
        private static bool CaptureAsset(string assetPath, string outputRoot, AnimationFormat format)
        {
            var prefab = AssetDatabase.LoadMainAssetAtPath(assetPath) as GameObject;
            if (prefab == null)
            {
                Debug.LogError($"[AvatarSetupTool] モデルを読み込めませんでした: {assetPath}");
                return true;
            }

            var preview = new PreviewRenderUtility();
            try
            {
                SetupCameraAndLights(preview);

                var instance = preview.InstantiatePrefabInScene(prefab);
                instance.transform.position = Vector3.zero;

                var targets = instance.GetComponentsInChildren<Animator>(true)
                    .Where(animator => animator.avatar != null)
                    .ToArray();
                if (targets.Length == 0)
                {
                    return false;
                }

                var assetName = Path.GetFileNameWithoutExtension(assetPath);
                var outputDir = Path.Combine(outputRoot, assetName);
                Directory.CreateDirectory(outputDir);

                var allRenderers = instance.GetComponentsInChildren<Renderer>(true);

                // SRP では最初のレンダリングが空になることがあるため、1 回捨てレンダリングする
                Warmup(preview, CalculateBounds(instance));

                var usedNames = new HashSet<string>();
                var stepsPerTarget = Directions.Length * 2
                    + (format == AnimationFormat.Mp4 ? VideoFrameCount * 2 : 0);
                var total = targets.Length * stepsPerTarget;
                var step = 0;
                foreach (var animator in targets)
                {
                    var target = animator.gameObject;
                    var captureName = MakeUniqueName(SanitizeFileName(target.name), usedNames);

                    // 対象の Animator 配下だけを描画し、他の Animator と混ざらないようにする
                    foreach (var renderer in allRenderers)
                    {
                        renderer.enabled = renderer.transform.IsChildOf(target.transform);
                    }

                    var fullGifFrames = new List<Color32[]>(Directions.Length);
                    var faceGifFrames = new List<Color32[]>(Directions.Length);
                    foreach (var (dirName, yaw) in Directions)
                    {
                        target.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
                        var bounds = CalculateBounds(target);

                        EditorUtility.DisplayProgressBar(
                            "Capture Model Images", $"{captureName}: {dirName}", step / (float)total);

                        var makeGifFrame = format == AnimationFormat.Gif;
                        var fullSize = Mathf.Max(bounds.extents.y, bounds.extents.x) * FullBodyMargin;
                        var fullFrame = CaptureShot(preview, bounds.center, fullSize, bounds.extents.z,
                            Path.Combine(outputDir, $"{captureName}_{dirName}_full.png"), makeGifFrame);
                        step++;

                        var (faceCenter, faceSize) = GetFaceView(animator, bounds);
                        var faceFrame = CaptureShot(preview, faceCenter, faceSize, bounds.extents.z,
                            Path.Combine(outputDir, $"{captureName}_{dirName}_face.png"), makeGifFrame);
                        step++;

                        if (makeGifFrame)
                        {
                            fullGifFrames.Add(fullFrame);
                            faceGifFrames.Add(faceFrame);
                        }
                    }

                    if (format == AnimationFormat.Gif)
                    {
                        EditorUtility.DisplayProgressBar(
                            "Capture Model Images", $"{captureName}: GIF を書き出し中", step / (float)total);
                        GifWriter.Write(Path.Combine(outputDir, $"{captureName}_full.gif"),
                            fullGifFrames, AnimationImageSize, AnimationImageSize, GifFrameDelayCentiseconds);
                        GifWriter.Write(Path.Combine(outputDir, $"{captureName}_face.gif"),
                            faceGifFrames, AnimationImageSize, AnimationImageSize, GifFrameDelayCentiseconds);
                    }
                    else
                    {
                        CaptureTurntableVideos(
                            preview, animator, target, outputDir, captureName, total, ref step);
                    }
                }

                var animationLabel = format == AnimationFormat.Gif ? "GIF" : "MP4";
                var pngCount = targets.Length * Directions.Length * 2;
                Debug.Log(
                    $"[AvatarSetupTool] {assetName}: PNG {pngCount} 枚と {animationLabel} {targets.Length * 2} 本を保存しました: {outputDir}");
                EditorUtility.RevealInFinder(outputDir);
                return true;
            }
            finally
            {
                preview.Cleanup();
                EditorUtility.ClearProgressBar();
            }
        }

        private static string SanitizeFileName(string name)
        {
            foreach (var invalid in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(invalid, '_');
            }

            return name;
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
            camera.backgroundColor = Color.white;

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
        /// 顔アップの構図を求める。
        /// 首(Neck)ジョイントを画像中心に置き、頭頂のメッシュ(バウンズ上端)が
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

            // 中心(首)から画像上端までは orthoSize。頭頂の上に画像高さの
            // FacePaddingRatio (= 2 * orthoSize * FacePaddingRatio) の余白を確保する。
            var neckToTop = Mathf.Max(bounds.max.y - neckPosition.y, bounds.size.y * 0.05f);
            var orthoSize = neckToTop / (1f - FacePaddingRatio * 2f);
            return (neckPosition, orthoSize);
        }

        private static void Warmup(PreviewRenderUtility preview, Bounds bounds)
        {
            var texture = RenderView(preview, bounds.center, bounds.extents.magnitude, bounds.extents.z, ImageSize);
            Object.DestroyImmediate(texture);
        }

        /// <summary>
        /// モデルを連続回転させながら全身 / 顔アップのターンテーブル MP4 を書き出す。
        /// フレームごとにバウンズを取り直すとズームが揺れて見えるため、
        /// 1 周分を包含する固定構図(回転軸中心・水平は外接円半径)で撮影する。
        /// </summary>
        private static void CaptureTurntableVideos(
            PreviewRenderUtility preview, Animator animator, GameObject target,
            string outputDir, string captureName, int total, ref int step)
        {
            var frameCount = VideoFrameCount;
            target.transform.rotation = Quaternion.Euler(0f, Directions[0].Yaw, 0f);
            var bounds = CalculateBounds(target);
            var axis = target.transform.position;
            var radius = HorizontalRadiusAroundAxis(bounds, axis);

            var fullCenter = new Vector3(axis.x, bounds.center.y, axis.z);
            var fullSize = Mathf.Max(bounds.extents.y, radius) * FullBodyMargin;
            var (faceViewCenter, faceSize) = GetFaceView(animator, bounds);
            var faceCenter = new Vector3(axis.x, faceViewCenter.y, axis.z);

            using (var fullWriter = new Mp4Writer(Path.Combine(outputDir, $"{captureName}_full.mp4"),
                AnimationImageSize, AnimationImageSize, VideoFrameRate))
            using (var faceWriter = new Mp4Writer(Path.Combine(outputDir, $"{captureName}_face.mp4"),
                AnimationImageSize, AnimationImageSize, VideoFrameRate))
            {
                for (var i = 0; i < frameCount; i++)
                {
                    EditorUtility.DisplayProgressBar(
                        "Capture Model Images", $"{captureName}: MP4 {i + 1}/{frameCount}", step / (float)total);

                    // PNG/GIF と同じく、正面から左向きへ回転する向き(ヨー角の増加方向)
                    var yaw = Directions[0].Yaw + 360f * i / frameCount;
                    target.transform.rotation = Quaternion.Euler(0f, yaw, 0f);

                    AddVideoFrame(preview, fullWriter, fullCenter, fullSize, radius);
                    step++;
                    AddVideoFrame(preview, faceWriter, faceCenter, faceSize, radius);
                    step++;
                }
            }
        }

        private static void AddVideoFrame(
            PreviewRenderUtility preview, Mp4Writer writer, Vector3 center, float orthoSize, float depthExtent)
        {
            var texture = RenderView(preview, center, orthoSize, depthExtent, ImageSize);
            writer.AddFrame(Downscale(texture, topDown: false));
            Object.DestroyImmediate(texture);
        }

        /// <summary>
        /// Y 軸回転でバウンズが水平方向に掃く範囲の半径(回転軸からの最大距離)。
        /// 回転中のどの向きでもモデルが画面と近クリップ面に収まるサイズの根拠になる。
        /// </summary>
        private static float HorizontalRadiusAroundAxis(Bounds bounds, Vector3 axis)
        {
            var dx = Mathf.Max(Mathf.Abs(bounds.min.x - axis.x), Mathf.Abs(bounds.max.x - axis.x));
            var dz = Mathf.Max(Mathf.Abs(bounds.min.z - axis.z), Mathf.Abs(bounds.max.z - axis.z));
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        /// <summary>
        /// 1 方向分を ImageSize で描画して PNG に保存し、makeGifFrame が真なら
        /// 同じ描画結果を GIF 用に縮小したフレームを返す(描画は 1 回で共用する)。
        /// </summary>
        private static Color32[] CaptureShot(
            PreviewRenderUtility preview, Vector3 center, float orthoSize, float depthExtent,
            string filePath, bool makeGifFrame)
        {
            var texture = RenderView(preview, center, orthoSize, depthExtent, ImageSize);
            File.WriteAllBytes(filePath, texture.EncodeToPNG());
            var gifFrame = makeGifFrame ? Downscale(texture, topDown: true) : null;
            Object.DestroyImmediate(texture);
            return gifFrame;
        }

        /// <summary>
        /// キャプチャ画像をアニメーション解像度へボックス平均で縮小する(スーパーサンプリングを兼ねる)。
        /// GetPixels32 は下端の行から始まる(ボトムアップ)。GIF はトップダウンの行順が
        /// 必要なため topDown 指定で上下反転し、MP4 (SetPixels32) はそのままの行順で返す。
        /// </summary>
        private static Color32[] Downscale(Texture2D texture, bool topDown)
        {
            const int factor = ImageSize / AnimationImageSize;
            const int samples = factor * factor;
            var source = texture.GetPixels32();
            var result = new Color32[AnimationImageSize * AnimationImageSize];
            for (var y = 0; y < AnimationImageSize; y++)
            {
                var destY = topDown ? AnimationImageSize - 1 - y : y;
                for (var x = 0; x < AnimationImageSize; x++)
                {
                    var r = 0;
                    var g = 0;
                    var b = 0;
                    for (var dy = 0; dy < factor; dy++)
                    {
                        var rowStart = (y * factor + dy) * ImageSize + x * factor;
                        for (var dx = 0; dx < factor; dx++)
                        {
                            var p = source[rowStart + dx];
                            r += p.r;
                            g += p.g;
                            b += p.b;
                        }
                    }

                    result[destY * AnimationImageSize + x] = new Color32(
                        (byte)((r + samples / 2) / samples),
                        (byte)((g + samples / 2) / samples),
                        (byte)((b + samples / 2) / samples),
                        255);
                }
            }

            return result;
        }

        private static Texture2D RenderView(
            PreviewRenderUtility preview, Vector3 center, float orthoSize, float depthExtent, int imageSize)
        {
            var camera = preview.camera;
            var distance = depthExtent + 1f;
            camera.orthographicSize = orthoSize;
            camera.transform.SetPositionAndRotation(center + Vector3.back * distance, Quaternion.identity);
            camera.nearClipPlane = 0.01f;
            camera.farClipPlane = distance + depthExtent + 1f;

            preview.BeginStaticPreview(new Rect(0f, 0f, imageSize, imageSize));
            preview.Render(true);
            return preview.EndStaticPreview();
        }
    }
}
