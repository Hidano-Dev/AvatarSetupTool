using System;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Hidano.AvatarSetupTool.Editor.Tests
{
    public class TextureImportSettingsPostprocessorIntegrationTests
    {
        private const string AssetPath = "Assets/TextureImportSettingsPostprocessorIntegrationTest.png";
        private string absolutePath;

        [SetUp]
        public void SetUp()
        {
            absolutePath = Path.Combine(Application.dataPath, "TextureImportSettingsPostprocessorIntegrationTest.png");
            AssetDatabase.DeleteAsset(AssetPath);
            if (File.Exists(absolutePath)) File.Delete(absolutePath);
            if (File.Exists(absolutePath + ".meta")) File.Delete(absolutePath + ".meta");
            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
        }

        [TearDown]
        public void TearDown()
        {
            AssetDatabase.DeleteAsset(AssetPath);
            if (File.Exists(absolutePath)) File.Delete(absolutePath);
            if (File.Exists(absolutePath + ".meta")) File.Delete(absolutePath + ".meta");
            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
        }

        [Test]
        public void FirstImport_4096PngWithoutMeta_SetsMaxTextureSizeTo4096()
        {
            File.WriteAllBytes(absolutePath, BuildPngHeader(4096, 4096));

            AssetDatabase.ImportAsset(AssetPath, ImportAssetOptions.ForceUpdate);

            var importer = AssetImporter.GetAtPath(AssetPath) as TextureImporter;
            Assert.That(importer, Is.Not.Null);
            Assert.That(importer.maxTextureSize, Is.EqualTo(4096));
        }

        [Test]
        public void Reimport_ExistingTexture_PreservesMaxTextureSize()
        {
            File.WriteAllBytes(absolutePath, BuildPngHeader(4096, 4096));
            AssetDatabase.ImportAsset(AssetPath, ImportAssetOptions.ForceUpdate);
            var importer = (TextureImporter)AssetImporter.GetAtPath(AssetPath);
            Assert.That(importer.maxTextureSize, Is.EqualTo(4096));

            importer.maxTextureSize = 2048;
            importer.SaveAndReimport();

            var reimported = (TextureImporter)AssetImporter.GetAtPath(AssetPath);
            Assert.That(reimported.maxTextureSize, Is.EqualTo(2048));
        }

        [Test]
        public void FirstImport_InvalidPng_DoesNotChangeDefaultSettings()
        {
            File.WriteAllBytes(absolutePath, new byte[] { 0x89, 0x50, 0x4e, 0x47 });
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("Could not create asset from .*File could not be read"));

            AssetDatabase.ImportAsset(AssetPath, ImportAssetOptions.ForceUpdate);

            var importer = AssetImporter.GetAtPath(AssetPath) as TextureImporter;
            Assert.That(importer, Is.Not.Null);
            Assert.That(importer.maxTextureSize, Is.EqualTo(2048));
        }

        private static byte[] BuildPngHeader(int width, int height)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            try
            {
                return texture.EncodeToPNG();
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }
    }
}
