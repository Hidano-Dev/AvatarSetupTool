using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Hidano.AvatarSetupTool.Editor
{
    /// <summary>
    /// Project ウィンドウで選択した FBX / Prefab を 8 方向 × (全身 / 顔アップ) の
    /// 計 16 枚の PNG としてキャプチャし、あわせて全方向を 2 秒間隔で繋いだ
    /// ループ GIF (全身 / 顔アップの 2 本) を生成する。
    /// Avatar が設定された Animator を撮影対象とし、複数ある場合は
    /// オブジェクト名ごとにすべて撮影する。見つからない場合はダイアログで警告する。
    /// カメラは並行投影・背景は白単色。
    /// 出力先: 実行時にフォルダ選択ダイアログで指定する。
    /// 初期値はマイピクチャ、以降は前回選択したフォルダを記憶する。
    /// </summary>
    public static class FbxModelCaptureTool
    {
        private const string MenuPath = "Assets/Avatar Setup Tool/Capture Model Images";
        private const string OutputDirPrefsKey = "Hidano.AvatarSetupTool.FbxModelCaptureTool.OutputDir";
        private const int ImageSize = 2048;
        private const int GifImageSize = 1024; // ImageSize の約数にすること(縮小がボックス平均のため)
        private const int GifFrameDelayCentiseconds = 200;
        private const float FullBodyMargin = 1.05f;
        private const float FacePaddingRatio = 0.1f;
        private const float FaceFallbackHeightRatio = 0.15f;

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
        private static void Capture()
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
                if (IsCapturableAsset(path) && !CaptureAsset(path, outputRoot))
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
        private static bool CaptureAsset(string assetPath, string outputRoot)
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
                var total = targets.Length * Directions.Length * 2;
                var shot = 0;
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
                            "Capture Model Images", $"{captureName}: {dirName}", shot / (float)total);

                        var fullSize = Mathf.Max(bounds.extents.y, bounds.extents.x) * FullBodyMargin;
                        fullGifFrames.Add(CaptureShot(preview, bounds.center, fullSize, bounds.extents.z,
                            Path.Combine(outputDir, $"{captureName}_{dirName}_full.png")));
                        shot++;

                        var (faceCenter, faceSize) = GetFaceView(animator, bounds);
                        faceGifFrames.Add(CaptureShot(preview, faceCenter, faceSize, bounds.extents.z,
                            Path.Combine(outputDir, $"{captureName}_{dirName}_face.png")));
                        shot++;
                    }

                    EditorUtility.DisplayProgressBar(
                        "Capture Model Images", $"{captureName}: GIF を書き出し中", shot / (float)total);
                    GifWriter.Write(Path.Combine(outputDir, $"{captureName}_full.gif"),
                        fullGifFrames, GifImageSize, GifImageSize, GifFrameDelayCentiseconds);
                    GifWriter.Write(Path.Combine(outputDir, $"{captureName}_face.gif"),
                        faceGifFrames, GifImageSize, GifImageSize, GifFrameDelayCentiseconds);
                }

                Debug.Log(
                    $"[AvatarSetupTool] {assetName}: PNG {shot} 枚と GIF {targets.Length * 2} 本を保存しました: {outputDir}");
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
        /// 1 方向分を ImageSize で描画して PNG に保存し、同じ描画結果を
        /// GIF 用に縮小したフレームを返す(描画は 1 回で共用する)。
        /// </summary>
        private static Color32[] CaptureShot(
            PreviewRenderUtility preview, Vector3 center, float orthoSize, float depthExtent, string filePath)
        {
            var texture = RenderView(preview, center, orthoSize, depthExtent, ImageSize);
            File.WriteAllBytes(filePath, texture.EncodeToPNG());
            var gifFrame = DownscaleForGif(texture);
            Object.DestroyImmediate(texture);
            return gifFrame;
        }

        /// <summary>
        /// キャプチャ画像を GIF 解像度へボックス平均で縮小する(スーパーサンプリングを兼ねる)。
        /// GetPixels32 は下端の行から始まるため、GIF の行順(トップダウン)への
        /// 上下反転もここで行う。
        /// </summary>
        private static Color32[] DownscaleForGif(Texture2D texture)
        {
            const int factor = ImageSize / GifImageSize;
            const int samples = factor * factor;
            var source = texture.GetPixels32();
            var result = new Color32[GifImageSize * GifImageSize];
            for (var y = 0; y < GifImageSize; y++)
            {
                for (var x = 0; x < GifImageSize; x++)
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

                    result[(GifImageSize - 1 - y) * GifImageSize + x] = new Color32(
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
