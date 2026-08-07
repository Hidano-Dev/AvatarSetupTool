using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Hidano.AvatarSetupTool.Editor.Tests
{
    public class TextureMaxSizeValidatorTests
    {
        private const string AssetPath = "Assets/TextureMaxSizeValidatorTest.png";
        private string absolutePath;

        [SetUp]
        public void SetUp()
        {
            absolutePath = Path.Combine(Application.dataPath, "TextureMaxSizeValidatorTest.png");
            AssetDatabase.DeleteAsset(AssetPath);
            File.WriteAllBytes(absolutePath, BuildPngHeader(4096, 4096));
            AssetDatabase.ImportAsset(AssetPath, ImportAssetOptions.ForceUpdate);
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
        public void ApplyFixes_ChangesOnlyMaxTextureSizeAndReportsSuccess()
        {
            var importer = AssetImporter.GetAtPath(AssetPath) as TextureImporter;
            Assert.That(importer, Is.Not.Null);

            var originalReadable = importer.isReadable;
            var originalType = importer.textureType;
            importer.maxTextureSize = 2048;
            importer.SaveAndReimport();

            var issue = new TextureMaxSizeIssue(AssetPath, 4096, 4096, 2048, 4096);
            var result = TextureMaxSizeValidator.ApplyFixes(
                new List<TextureMaxSizeIssue> { issue }, null);

            var updated = AssetImporter.GetAtPath(AssetPath) as TextureImporter;
            Assert.That(result.FixedCount, Is.EqualTo(1));
            Assert.That(result.FailedCount, Is.EqualTo(0));
            Assert.That(result.Cancelled, Is.False);
            Assert.That(updated.maxTextureSize, Is.EqualTo(4096));
            Assert.That(updated.isReadable, Is.EqualTo(originalReadable));
            Assert.That(updated.textureType, Is.EqualTo(originalType));
        }

        [Test]
        public void ApplyFixes_CancellationStopsAfterCurrentAsset()
        {
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("\\[TextureMaxSize\\].*does-not-exist\\.png"));
            var result = TextureMaxSizeValidator.ApplyFixes(
                new List<TextureMaxSizeIssue>
                {
                    new TextureMaxSizeIssue("Assets/does-not-exist.png", 1, 1, 2048, 4096),
                    new TextureMaxSizeIssue("Assets/does-not-exist-2.png", 1, 1, 2048, 4096)
                },
                (current, total, path) => false);

            Assert.That(result.Cancelled, Is.True);
            Assert.That(result.FailedCount, Is.EqualTo(1));
            Assert.That(result.FixedCount, Is.EqualTo(0));
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
                Object.DestroyImmediate(texture);
            }
        }
    }
}
