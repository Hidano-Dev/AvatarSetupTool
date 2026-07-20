using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Hidano.AvatarSetupTool.Editor
{
    /// <summary>
    /// Project ウィンドウで選択した FBX / Prefab を 8 方向 × (全身 / 顔アップ) の
    /// 計 16 枚の PNG としてキャプチャする。
    /// Avatar が設定された Animator を撮影対象とし、複数ある場合は
    /// オブジェクト名ごとにすべて撮影する。見つからない場合はダイアログで警告する。
    /// カメラは並行投影・背景は白単色。
    /// 出力先: (Unity プロジェクトルート)/Captures/(アセット名)/
    /// </summary>
    public static class FbxModelCaptureTool
    {
        private const string MenuPath = "Assets/Avatar Setup Tool/Capture Model Images";
        private const int ImageSize = 1024;
        private const float FullBodyMargin = 1.05f;
        private const float FaceMargin = 0.75f;
        private const float FaceFallbackHeightRatio = 0.15f;

        /// <summary>
        /// カメラは -Z 側に固定し、モデル側を Y 回転させて 8 方向を撮る。
        /// yaw=180 でモデルの正面がカメラを向く。
        /// </summary>
        private static readonly (string Name, float Yaw)[] Directions =
        {
            ("front", 180f),
            ("front_right", 135f),
            ("right", 90f),
            ("back_right", 45f),
            ("back", 0f),
            ("back_left", -45f),
            ("left", -90f),
            ("front_left", -135f),
        };

        [MenuItem(MenuPath)]
        private static void Capture()
        {
            var assetsWithoutAvatar = new List<string>();
            foreach (var obj in Selection.objects)
            {
                var path = AssetDatabase.GetAssetPath(obj);
                if (IsCapturableAsset(path) && !CaptureAsset(path))
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
        /// アセット内の Avatar 付き Animator をすべて撮影する。
        /// Avatar 付き Animator が 1 つも見つからなければ false を返す(撮影は行わない)。
        /// </summary>
        private static bool CaptureAsset(string assetPath)
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
                var outputDir = Path.Combine(GetProjectRootPath(), "Captures", assetName);
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

                    foreach (var (dirName, yaw) in Directions)
                    {
                        target.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
                        var bounds = CalculateBounds(target);

                        EditorUtility.DisplayProgressBar(
                            "Capture Model Images", $"{captureName}: {dirName}", shot / (float)total);

                        var fullSize = Mathf.Max(bounds.extents.y, bounds.extents.x) * FullBodyMargin;
                        RenderToFile(preview, bounds.center, fullSize, bounds.extents.z,
                            Path.Combine(outputDir, $"{captureName}_{dirName}_full.png"));
                        shot++;

                        var (faceCenter, faceSize) = GetFaceView(animator, bounds);
                        RenderToFile(preview, faceCenter, faceSize, bounds.extents.z,
                            Path.Combine(outputDir, $"{captureName}_{dirName}_face.png"));
                        shot++;
                    }
                }

                Debug.Log($"[AvatarSetupTool] {assetName}: {shot} 枚のキャプチャを保存しました: {outputDir}");
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

        private static string GetProjectRootPath()
        {
            return Path.GetDirectoryName(Application.dataPath);
        }

        private static void SetupCameraAndLights(PreviewRenderUtility preview)
        {
            var camera = preview.camera;
            camera.orthographic = true;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.white;

            // モデル側を回転させるため、ライトはカメラ基準で固定になる
            preview.lights[0].intensity = 1.2f;
            preview.lights[0].transform.rotation = Quaternion.Euler(30f, 20f, 0f);
            preview.lights[1].intensity = 0.8f;
            preview.ambientColor = new Color(0.4f, 0.4f, 0.4f, 1f);
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

        private static (Vector3 Center, float OrthoSize) GetFaceView(Animator animator, Bounds bounds)
        {
            Transform head = null;
            if (animator.isHuman)
            {
                head = animator.GetBoneTransform(HumanBodyBones.Head);
            }

            Vector3 headPosition;
            if (head != null)
            {
                headPosition = head.position;
            }
            else
            {
                Debug.LogWarning(
                    $"[AvatarSetupTool] Head ボーンが取得できないため、バウンズ上部 {FaceFallbackHeightRatio:P0} を顔とみなします: {animator.gameObject.name}");
                headPosition = new Vector3(
                    bounds.center.x,
                    bounds.max.y - bounds.size.y * FaceFallbackHeightRatio,
                    bounds.center.z);
            }

            var topY = bounds.max.y;
            var headToTop = Mathf.Max(topY - headPosition.y, bounds.size.y * 0.05f);
            var center = new Vector3(headPosition.x, (headPosition.y + topY) * 0.5f, headPosition.z);
            return (center, headToTop * FaceMargin);
        }

        private static void Warmup(PreviewRenderUtility preview, Bounds bounds)
        {
            var texture = RenderView(preview, bounds.center, bounds.extents.magnitude, bounds.extents.z);
            Object.DestroyImmediate(texture);
        }

        private static void RenderToFile(
            PreviewRenderUtility preview, Vector3 center, float orthoSize, float depthExtent, string filePath)
        {
            var texture = RenderView(preview, center, orthoSize, depthExtent);
            File.WriteAllBytes(filePath, texture.EncodeToPNG());
            Object.DestroyImmediate(texture);
        }

        private static Texture2D RenderView(
            PreviewRenderUtility preview, Vector3 center, float orthoSize, float depthExtent)
        {
            var camera = preview.camera;
            var distance = depthExtent + 1f;
            camera.orthographicSize = orthoSize;
            camera.transform.SetPositionAndRotation(center + Vector3.back * distance, Quaternion.identity);
            camera.nearClipPlane = 0.01f;
            camera.farClipPlane = distance + depthExtent + 1f;

            preview.BeginStaticPreview(new Rect(0f, 0f, ImageSize, ImageSize));
            preview.Render(true);
            return preview.EndStaticPreview();
        }
    }
}
