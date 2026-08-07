using System.Collections.Generic;
using UnityEditor;

namespace Hidano.AvatarSetupTool.Editor
{
    /// <summary>Project Browser からテクスチャの maxTextureSize を検証・修正するメニュー。</summary>
    internal static class TextureMaxSizeContextMenu
    {
        private const string MenuPath = "Assets/Avatar Setup Tool/Validate Texture Max Size";

        [MenuItem(MenuPath, false, 1001)]
        private static void ValidateAndFix()
        {
            var selectedPaths = GetSelectedAssetPaths();
            var texturePaths = TextureMaxSizeValidator.CollectTextureAssetPaths(selectedPaths);
            if (texturePaths.Count == 0)
            {
                EditorUtility.DisplayDialog("テクスチャ最大サイズ", "選択範囲に対象テクスチャがありません。設定は変更されませんでした。", "OK");
                return;
            }

            TextureMaxSizeValidationReport report;
            try
            {
                report = TextureMaxSizeValidator.Validate(texturePaths, Progress("テクスチャ最大サイズを検証中"));
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            foreach (var issue in report.Issues)
            {
                UnityEngine.Debug.Log($"[TextureMaxSize] {issue.AssetPath}: {issue.CurrentMaxSize} -> {issue.OptimalMaxSize} ({issue.Width}x{issue.Height})");
            }

            if (report.Issues.Count == 0 || report.Cancelled)
            {
                ShowResult(report, 0, 0, report.Cancelled);
                return;
            }

            if (!EditorUtility.DisplayDialog("テクスチャ最大サイズの修正", $"{report.Issues.Count} 件のテクスチャで maxTextureSize が最適値と一致しません。\n修正を適用しますか？", "修正する", "キャンセル"))
            {
                ShowResult(report, 0, 0, true);
                return;
            }

            TextureMaxSizeFixResult fixResult;
            try
            {
                fixResult = TextureMaxSizeValidator.ApplyFixes(report.Issues, Progress("テクスチャ最大サイズを修正中"));
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            ShowResult(report, fixResult.FixedCount, fixResult.FailedCount, fixResult.Cancelled);
        }

        [MenuItem(MenuPath, true)]
        private static bool ValidateMenu()
        {
            return Selection.assetGUIDs != null && Selection.assetGUIDs.Length > 0;
        }

        private static IReadOnlyList<string> GetSelectedAssetPaths()
        {
            var paths = new List<string>();
            foreach (var guid in Selection.assetGUIDs)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrEmpty(path))
                {
                    paths.Add(path);
                }
            }
            return paths;
        }

        private static TextureMaxSizeProgress Progress(string title)
        {
            return (current, total, assetPath) => !EditorUtility.DisplayCancelableProgressBar(
                title, $"{current}/{total}: {assetPath}", total == 0 ? 1f : (float)current / total);
        }

        private static void ShowResult(TextureMaxSizeValidationReport report, int fixedCount, int failedCount, bool cancelled)
        {
            var status = cancelled ? "\n処理はキャンセルされました。" : string.Empty;
            EditorUtility.DisplayDialog("テクスチャ最大サイズの結果", $"検証: {report.ScannedCount} 件\n修正: {fixedCount} 件\nスキップ: {report.SkippedCount + failedCount} 件{status}", "OK");
        }
    }
}
