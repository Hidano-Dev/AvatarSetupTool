using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Hidano.AvatarSetupTool.Editor
{
    /// <summary>進捗通知。false を返すとキャンセルする。</summary>
    internal delegate bool TextureMaxSizeProgress(int current, int total, string assetPath);

    /// <summary>maxTextureSize が最適値と一致しないテクスチャ 1 件分。</summary>
    internal readonly struct TextureMaxSizeIssue
    {
        public TextureMaxSizeIssue(string assetPath, int width, int height, int currentMaxSize, int optimalMaxSize)
        {
            AssetPath = assetPath;
            Width = width;
            Height = height;
            CurrentMaxSize = currentMaxSize;
            OptimalMaxSize = optimalMaxSize;
        }

        public string AssetPath { get; }
        public int Width { get; }
        public int Height { get; }
        public int CurrentMaxSize { get; }
        public int OptimalMaxSize { get; }
    }

    /// <summary>テクスチャ maxTextureSize の検証結果。</summary>
    internal sealed class TextureMaxSizeValidationReport
    {
        public TextureMaxSizeValidationReport(
            IReadOnlyList<TextureMaxSizeIssue> issues,
            int scannedCount,
            int skippedCount,
            bool cancelled)
        {
            Issues = issues;
            ScannedCount = scannedCount;
            SkippedCount = skippedCount;
            Cancelled = cancelled;
        }

        public IReadOnlyList<TextureMaxSizeIssue> Issues { get; }
        public int ScannedCount { get; }
        public int SkippedCount { get; }
        public bool Cancelled { get; }
    }

    /// <summary>
    /// 選択されたアセットの列挙と、既定プラットフォームの maxTextureSize 検証を行う。
    /// 検証ではインポート設定を変更しない。
    /// </summary>
    internal static class TextureMaxSizeValidator
    {
        public static IReadOnlyList<string> CollectTextureAssetPaths(
            IReadOnlyList<string> selectedAssetPaths)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (selectedAssetPaths == null)
            {
                return result;
            }

            foreach (var selectedPath in selectedAssetPaths)
            {
                if (string.IsNullOrEmpty(selectedPath))
                {
                    continue;
                }

                if (AssetDatabase.IsValidFolder(selectedPath))
                {
                    var guids = AssetDatabase.FindAssets("t:Texture2D", new[] { selectedPath });
                    foreach (var guid in guids)
                    {
                        AddIfTextureImporter(AssetDatabase.GUIDToAssetPath(guid), seen, result);
                    }
                    continue;
                }

                AddIfTextureImporter(selectedPath, seen, result);
            }

            return result;
        }

        public static TextureMaxSizeValidationReport Validate(
            IReadOnlyList<string> assetPaths,
            TextureMaxSizeProgress progress)
        {
            var issues = new List<TextureMaxSizeIssue>();
            var paths = Deduplicate(assetPaths);
            var scannedCount = 0;
            var skippedCount = 0;
            var cancelled = false;

            for (var i = 0; i < paths.Count; i++)
            {
                var assetPath = paths[i];
                scannedCount++;
                try
                {
                    var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
                    if (importer == null)
                    {
                        skippedCount++;
                        LogSkipped(assetPath, "TextureImporter を取得できませんでした");
                    }
                    else if (!TextureHeaderReader.TryRead(Path.GetFullPath(assetPath), out var dimensions))
                    {
                        skippedCount++;
                        LogSkipped(assetPath, "画像ヘッダーから解像度を取得できませんでした");
                    }
                    else
                    {
                        var optimalMaxSize = TextureMaxSizeCalculator.Calculate(
                            dimensions.Width,
                            dimensions.Height);
                        if (importer.maxTextureSize != optimalMaxSize)
                        {
                            issues.Add(new TextureMaxSizeIssue(
                                assetPath,
                                dimensions.Width,
                                dimensions.Height,
                                importer.maxTextureSize,
                                optimalMaxSize));
                        }
                    }
                }
                catch (Exception exception)
                {
                    skippedCount++;
                    LogSkipped(assetPath, exception.Message);
                }

                if (progress != null && !progress(i + 1, paths.Count, assetPath))
                {
                    cancelled = true;
                    break;
                }
            }

            return new TextureMaxSizeValidationReport(issues, scannedCount, skippedCount, cancelled);
        }

        private static void AddIfTextureImporter(
            string assetPath,
            HashSet<string> seen,
            List<string> result)
        {
            if (string.IsNullOrEmpty(assetPath) || !seen.Add(assetPath))
            {
                return;
            }

            if (AssetImporter.GetAtPath(assetPath) is TextureImporter)
            {
                result.Add(assetPath);
            }
            else
            {
                seen.Remove(assetPath);
            }
        }

        private static List<string> Deduplicate(IReadOnlyList<string> assetPaths)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (assetPaths == null)
            {
                return result;
            }

            foreach (var assetPath in assetPaths)
            {
                if (!string.IsNullOrEmpty(assetPath) && seen.Add(assetPath))
                {
                    result.Add(assetPath);
                }
            }
            return result;
        }

        private static void LogSkipped(string assetPath, string reason)
        {
            Debug.LogWarning($"[TextureMaxSize] {assetPath}: {reason}");
        }
    }
}
