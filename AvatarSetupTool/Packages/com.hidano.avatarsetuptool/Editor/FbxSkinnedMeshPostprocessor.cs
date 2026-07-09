using System;
using UnityEditor;
using UnityEngine;

namespace Hidano.AvatarSetupTool.Editor
{
    /// <summary>
    /// FBX インポート時に、モデル内の全 SkinnedMeshRenderer の
    /// Update When Offscreen を有効化する。
    /// </summary>
    public sealed class FbxSkinnedMeshPostprocessor : AssetPostprocessor
    {
        private void OnPostprocessModel(GameObject root)
        {
            if (!assetPath.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            foreach (var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                renderer.updateWhenOffscreen = true;
            }
        }
    }
}
