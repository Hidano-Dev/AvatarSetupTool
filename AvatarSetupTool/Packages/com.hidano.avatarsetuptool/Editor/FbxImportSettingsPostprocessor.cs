using System;
using UnityEditor;

namespace Hidano.AvatarSetupTool.Editor
{
    /// <summary>
    /// FBX の初回インポート時に、Mesh の Read/Write Enable と
    /// Humanoid リグを有効化し、Blend Shape Normals を Import に設定する。
    /// 既にインポート済み(.meta が存在する)FBX の設定は変更しない。
    /// </summary>
    public sealed class FbxImportSettingsPostprocessor : AssetPostprocessor
    {
        private void OnPreprocessModel()
        {
            if (!assetPath.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var importer = (ModelImporter)assetImporter;
            if (!importer.importSettingsMissing)
            {
                return;
            }

            importer.isReadable = true;
            importer.animationType = ModelImporterAnimationType.Human;
            importer.importBlendShapeNormals = ModelImporterNormals.Import;
        }
    }
}
