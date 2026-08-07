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
        private const string RootPath = "Assets/TextureMaxSizeServiceIntegrationTests";
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
            AssetDatabase.DeleteAsset(RootPath);
            if (File.Exists(absolutePath)) File.Delete(absolutePath);
            if (File.Exists(absolutePath + ".meta")) File.Delete(absolutePath + ".meta");
            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
        }

        [Test]
        public void CollectTextureAssetPaths_FolderFileMixedAndDuplicate_ReturnsDistinctTextures()
        {
            var folderTexture = CreateTexture("folder.png", 4096, 1024);
            var nestedTexture = CreateTexture("Nested/nested.png", 512, 256);
            var fileTexture = CreateTexture("file.png", 256, 128);
            var nonTexture = CreateTextAsset("notes.txt");

            var result = TextureMaxSizeValidator.CollectTextureAssetPaths(
                new[] { RootPath, folderTexture, fileTexture, nonTexture, RootPath });

            Assert.That(result, Is.EquivalentTo(new[] { folderTexture, nestedTexture, fileTexture }));
            Assert.That(result, Has.Count.EqualTo(3));
        }

        [Test]
        public void CollectTextureAssetPaths_FolderWithoutTextures_ReturnsEmptyList()
        {
            var nonTexture = CreateTextAsset("only.txt");

            var result = TextureMaxSizeValidator.CollectTextureAssetPaths(new[] { RootPath });

            Assert.That(nonTexture, Does.StartWith(RootPath));
            Assert.That(result, Is.Empty);
        }

        [Test]
        public void ValidateAndApplyFixes_UpdatesOnlyMaxTextureSizeAndReportsCounts()
        {
            var assetPath = CreateTexture("needs-fix.png", 4096, 2048);
            var importer = (TextureImporter)AssetImporter.GetAtPath(assetPath);
            importer.textureType = TextureImporterType.Sprite;
            importer.isReadable = true;
            importer.maxTextureSize = 2048;
            importer.SaveAndReimport();

            var report = TextureMaxSizeValidator.Validate(new[] { assetPath }, null);

            Assert.That(report.ScannedCount, Is.EqualTo(1));
            Assert.That(report.SkippedCount, Is.EqualTo(0));
            Assert.That(report.Cancelled, Is.False);
            Assert.That(report.Issues, Has.Count.EqualTo(1));
            Assert.That(report.Issues[0].OptimalMaxSize, Is.EqualTo(4096));

            var result = TextureMaxSizeValidator.ApplyFixes(report.Issues, null);
            var updated = (TextureImporter)AssetImporter.GetAtPath(assetPath);

            Assert.That(result.FixedCount, Is.EqualTo(1));
            Assert.That(result.FailedCount, Is.EqualTo(0));
            Assert.That(result.Cancelled, Is.False);
            Assert.That(updated.maxTextureSize, Is.EqualTo(4096));
            Assert.That(updated.textureType, Is.EqualTo(TextureImporterType.Sprite));
            Assert.That(updated.isReadable, Is.True);
        }

        [Test]
        public void Validate_CorruptAssetSkipsItAndContinuesWithAccurateCounts()
        {
            var validPath = CreateTexture("valid.png", 4096, 4096);
            SetMaxTextureSize(validPath, 2048);
            var corruptPath = Path.Combine(RootPath, "corrupt.png").Replace('\\', '/');
            var corruptAbsolutePath = Path.Combine(Application.dataPath, corruptPath.Substring("Assets/".Length));
            File.WriteAllBytes(corruptAbsolutePath, new byte[] { 0x89, 0x50, 0x4e, 0x47 });
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("Could not create asset from .*corrupt\\.png.*File could not be read"));
            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);

            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("\\[TextureMaxSize\\].*corrupt\\.png"));
            var report = TextureMaxSizeValidator.Validate(new[] { corruptPath, validPath }, null);

            Assert.That(report.ScannedCount, Is.EqualTo(2));
            Assert.That(report.SkippedCount, Is.EqualTo(1));
            Assert.That(report.Issues, Has.Count.EqualTo(1));
            Assert.That(report.Cancelled, Is.False);
        }

        [Test]
        public void Validate_ProgressCancellationReturnsPartialCancelledReport()
        {
            var firstPath = CreateTexture("first.png", 4096, 4096);
            var secondPath = CreateTexture("second.png", 4096, 4096);
            SetMaxTextureSize(firstPath, 2048);
            SetMaxTextureSize(secondPath, 2048);
            var progressCalls = 0;

            var report = TextureMaxSizeValidator.Validate(
                new[] { firstPath, secondPath },
                (current, total, path) =>
                {
                    progressCalls++;
                    return false;
                });

            Assert.That(progressCalls, Is.EqualTo(1));
            Assert.That(report.ScannedCount, Is.EqualTo(1));
            Assert.That(report.Issues, Has.Count.EqualTo(1));
            Assert.That(report.Cancelled, Is.True);
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

        private static string CreateTexture(string relativePath, int width, int height)
        {
            var assetPath = (RootPath + "/" + relativePath).Replace('\\', '/');
            var absolute = Path.Combine(Application.dataPath, assetPath.Substring("Assets/".Length));
            Directory.CreateDirectory(Path.GetDirectoryName(absolute));
            File.WriteAllBytes(absolute, BuildPngHeader(width, height));
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
            return assetPath;
        }

        private static string CreateTextAsset(string relativePath)
        {
            var assetPath = (RootPath + "/" + relativePath).Replace('\\', '/');
            var absolute = Path.Combine(Application.dataPath, assetPath.Substring("Assets/".Length));
            Directory.CreateDirectory(Path.GetDirectoryName(absolute));
            File.WriteAllText(absolute, "not a texture");
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
            return assetPath;
        }

        private static void SetMaxTextureSize(string assetPath, int maxTextureSize)
        {
            var importer = (TextureImporter)AssetImporter.GetAtPath(assetPath);
            importer.maxTextureSize = maxTextureSize;
            importer.SaveAndReimport();
        }
    }
}
