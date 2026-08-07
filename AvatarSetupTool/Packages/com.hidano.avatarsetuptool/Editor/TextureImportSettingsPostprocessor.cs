using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Hidano.AvatarSetupTool.Editor
{
    /// <summary>
    /// テクスチャの初回インポート時に、実解像度に基づく maxTextureSize を適用する。
    /// </summary>
    public sealed class TextureImportSettingsPostprocessor : AssetPostprocessor
    {
        private void OnPreprocessTexture()
        {
            try
            {
                var importer = (TextureImporter)assetImporter;
                if (!importer.importSettingsMissing)
                {
                    return;
                }

                var fullPath = Path.GetFullPath(assetPath);
                if (!TextureHeaderReader.TryRead(fullPath, out var dimensions))
                {
                    return;
                }

                importer.maxTextureSize = TextureMaxSizeCalculator.Calculate(
                    dimensions.Width,
                    dimensions.Height);
            }
            catch (Exception exception)
            {
                Debug.LogWarning(
                    $"[TextureMaxSize] テクスチャの maxTextureSize 自動調整に失敗しました: {assetPath}\n{exception}");
            }
        }
    }
}
