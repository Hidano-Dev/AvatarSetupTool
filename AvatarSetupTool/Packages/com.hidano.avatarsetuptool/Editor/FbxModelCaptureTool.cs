using System.IO;
using UnityEditor;
using UnityEngine;

namespace Hidano.AvatarSetupTool.Editor
{
    /// <summary>
    /// Project ウィンドウで選択した FBX を 8 方向 × (全身 / 顔アップ) の
    /// 計 16 枚の PNG としてキャプチャする。
    /// カメラは並行投影・背景は白単色。
    /// 出力先: (Unity プロジェクトルート)/Captures/(FBX 名)/
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
            foreach (var obj in Selection.objects)
            {
                var path = AssetDatabase.GetAssetPath(obj);
                if (IsFbx(path))
                {
                    CaptureFbx(path);
                }
            }
        }

        [MenuItem(MenuPath, true)]
        private static bool ValidateCapture()
        {
            foreach (var obj in Selection.objects)
            {
                if (IsFbx(AssetDatabase.GetAssetPath(obj)))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsFbx(string path)
        {
            return !string.IsNullOrEmpty(path)
                && path.EndsWith(".fbx", System.StringComparison.OrdinalIgnoreCase);
        }

        private static void CaptureFbx(string assetPath)
        {
            var prefab = AssetDatabase.LoadMainAssetAtPath(assetPath) as GameObject;
            if (prefab == null)
            {
                Debug.LogError($"[AvatarSetupTool] モデルを読み込めませんでした: {assetPath}");
                return;
            }

            var fbxName = Path.GetFileNameWithoutExtension(assetPath);
            var outputDir = Path.Combine(GetProjectRootPath(), "Captures", fbxName);
            Directory.CreateDirectory(outputDir);

            var preview = new PreviewRenderUtility();
            try
            {
                SetupCameraAndLights(preview);

                var instance = preview.InstantiatePrefabInScene(prefab);
                instance.transform.position = Vector3.zero;

                // SRP では最初のレンダリングが空になることがあるため、1 回捨てレンダリングする
                Warmup(preview, CalculateBounds(instance));

                var shot = 0;
                foreach (var (dirName, yaw) in Directions)
                {
                    instance.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
                    var bounds = CalculateBounds(instance);

                    EditorUtility.DisplayProgressBar(
                        "Capture Model Images", $"{fbxName}: {dirName}", shot / (float)(Directions.Length * 2));

                    var fullSize = Mathf.Max(bounds.extents.y, bounds.extents.x) * FullBodyMargin;
                    RenderToFile(preview, bounds.center, fullSize, bounds.extents.z,
                        Path.Combine(outputDir, $"{fbxName}_{dirName}_full.png"));
                    shot++;

                    var (faceCenter, faceSize) = GetFaceView(instance, bounds);
                    RenderToFile(preview, faceCenter, faceSize, bounds.extents.z,
                        Path.Combine(outputDir, $"{fbxName}_{dirName}_face.png"));
                    shot++;
                }

                Debug.Log($"[AvatarSetupTool] {fbxName}: {shot} 枚のキャプチャを保存しました: {outputDir}");
                EditorUtility.RevealInFinder(outputDir);
            }
            finally
            {
                preview.Cleanup();
                EditorUtility.ClearProgressBar();
            }
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

        private static (Vector3 Center, float OrthoSize) GetFaceView(GameObject instance, Bounds bounds)
        {
            Transform head = null;
            var animator = instance.GetComponentInChildren<Animator>(true);
            if (animator != null && animator.isHuman)
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
                    $"[AvatarSetupTool] Head ボーンが取得できないため、バウンズ上部 {FaceFallbackHeightRatio:P0} を顔とみなします: {instance.name}");
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
