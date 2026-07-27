using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Hidano.AvatarSetupTool.Editor
{
    /// <summary>
    /// キャプチャ画像へ焼き込むデバッグ情報 (モデルの出所) を収集する。
    /// - 撮影対象のアセットパス (シーン上のオブジェクトは元プレハブをたどる)
    /// - 対象が参照する元 FBX のヘッダメタデータ (いつ・どのツール・どのファイルからエクスポートされたか)
    /// - Prefab の場合は git の直近コミット (日時・作者・メッセージ)
    /// 取れない項目は黙って省略し、収集の失敗で撮影自体を止めない。
    /// </summary>
    internal static class CaptureDebugInfo
    {
        private const int MaxValueLength = 120;

        /// <summary>撮影対象そのもの (アセットパス + Prefab なら git コミット) の情報行。</summary>
        public static List<string> CollectSourceLines(GameObject source)
        {
            var lines = new List<string>();
            try
            {
                var path = FindAssetPath(source);
                if (string.IsNullOrEmpty(path))
                {
                    lines.Add("Source: (scene object)");
                    return lines;
                }

                lines.Add("Source: " + path);
                if (path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                {
                    var commit = GitLastCommit(Path.GetFullPath(path));
                    lines.Add("Prefab commit: " + (commit ?? "(no git history)"));
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[AvatarSetupTool] デバッグ情報 (Source) の収集に失敗しました: {e.Message}");
            }

            return lines;
        }

        /// <summary>ターゲットが参照する元 FBX をたどり、そのヘッダメタデータの情報行を返す。</summary>
        public static List<string> CollectFbxLines(Animator animator)
        {
            var lines = new List<string>();
            try
            {
                var fbxPath = FindModelAssetPath(animator);
                if (fbxPath == null)
                {
                    return lines;
                }

                lines.Add("FBX: " + fbxPath);
                var meta = FbxHeaderReader.Read(Path.GetFullPath(fbxPath));

                var exported = FormatTimeStamp(meta) ?? Get(meta, "CreationTime");
                var app = JoinNonEmpty(" ",
                    Get(meta, "Original|ApplicationVendor"),
                    Get(meta, "Original|ApplicationName"),
                    Get(meta, "Original|ApplicationVersion"));
                if (exported != null || app != null)
                {
                    lines.Add("FBX exported: " + JoinNonEmpty(" ",
                        exported, app == null ? null : $"({app})"));
                }

                // エクスポート元のファイル / プロジェクトのフルパス。
                // ここに含まれるユーザー名などが「誰がどの PC から」の手がかりになる
                var sourceFile = Get(meta, "Original|FileName")
                    ?? Get(meta, "Original|ApplicationNativeFile")
                    ?? Get(meta, "Original|ApplicationActiveProject");
                if (sourceFile != null)
                {
                    lines.Add("FBX source: " + Truncate(sourceFile));
                }

                var creator = Get(meta, "Creator");
                if (creator != null)
                {
                    lines.Add("FBX creator: " + Truncate(creator));
                }

                var lastSaved = Get(meta, "LastSaved|DateTime_GMT");
                if (lastSaved != null && lastSaved != Get(meta, "Original|DateTime_GMT"))
                {
                    lines.Add("FBX last saved: " + lastSaved + " (GMT)");
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[AvatarSetupTool] デバッグ情報 (FBX) の収集に失敗しました: {e.Message}");
            }

            return lines;
        }

        /// <summary>撮影そのものの情報行 (いつ・誰が・どの PC で・どのバージョンで撮ったか)。</summary>
        public static string CapturedLine(DateTime timestamp)
        {
            var version = UnityEditor.PackageManager.PackageInfo
                .FindForAssembly(typeof(CaptureDebugInfo).Assembly)?.version;
            return $"Captured: {timestamp:yyyy-MM-dd HH:mm:ss}"
                + $" by {Environment.UserName}@{Environment.MachineName}"
                + $" (AvatarSetupTool {version ?? "?"})";
        }

        /// <summary>
        /// 撮影対象のアセットパス。シーン上のオブジェクトはプレハブ元 (最内の原本) をたどる。
        /// </summary>
        private static string FindAssetPath(GameObject source)
        {
            if (EditorUtility.IsPersistent(source))
            {
                return AssetDatabase.GetAssetPath(source);
            }

            var original = PrefabUtility.GetCorrespondingObjectFromOriginalSource(source);
            return original == null ? null : AssetDatabase.GetAssetPath(original);
        }

        /// <summary>
        /// ターゲットが参照するモデルアセット (FBX 等、ModelImporter で読まれたもの) のパス。
        /// Avatar → SkinnedMeshRenderer のメッシュ → MeshFilter のメッシュの順にたどる。
        /// </summary>
        private static string FindModelAssetPath(Animator animator)
        {
            var candidates = new List<UnityEngine.Object>();
            if (animator.avatar != null)
            {
                candidates.Add(animator.avatar);
            }

            foreach (var renderer in animator.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (renderer.sharedMesh != null)
                {
                    candidates.Add(renderer.sharedMesh);
                }
            }

            foreach (var filter in animator.GetComponentsInChildren<MeshFilter>(true))
            {
                if (filter.sharedMesh != null)
                {
                    candidates.Add(filter.sharedMesh);
                }
            }

            foreach (var candidate in candidates)
            {
                var path = AssetDatabase.GetAssetPath(candidate);
                if (!string.IsNullOrEmpty(path) && AssetImporter.GetAtPath(path) is ModelImporter)
                {
                    return path;
                }
            }

            return null;
        }

        /// <summary>
        /// アセットの直近コミットを "日時  ハッシュ  メッセージ  (作者)" 形式で返す。
        /// 未コミットの変更があれば [+uncommitted] を付ける。git が無い / リポジトリ外なら null。
        /// </summary>
        private static string GitLastCommit(string fullPath)
        {
            try
            {
                var directory = Path.GetDirectoryName(fullPath);
                var fileName = Path.GetFileName(fullPath);
                var log = RunGit(directory, $"log -1 --format=%ci%x09%h%x09%an%x09%s -- \"{fileName}\"");
                if (string.IsNullOrWhiteSpace(log))
                {
                    return null;
                }

                var parts = log.Trim().Split('\t');
                var commit = parts.Length >= 4
                    ? $"{parts[0]}  {parts[1]}  {Truncate(parts[3])}  ({parts[2]})"
                    : log.Trim();

                var status = RunGit(directory, $"status --porcelain -- \"{fileName}\"");
                if (!string.IsNullOrWhiteSpace(status))
                {
                    commit += "  [+uncommitted]";
                }

                return commit;
            }
            catch (Exception)
            {
                return null; // git が PATH に無いなど
            }
        }

        private static string RunGit(string workingDirectory, string arguments)
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo("git", arguments)
            {
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
            };
            using (var process = System.Diagnostics.Process.Start(startInfo))
            {
                var output = process.StandardOutput.ReadToEnd();
                process.StandardError.ReadToEnd();
                if (!process.WaitForExit(5000))
                {
                    process.Kill();
                    return null;
                }

                return process.ExitCode == 0 ? output : null;
            }
        }

        /// <summary>バイナリ FBX の CreationTimeStamp (エクスポート元のローカル時刻) を整形する。</summary>
        private static string FormatTimeStamp(Dictionary<string, string> meta)
        {
            if (!meta.TryGetValue("TimeStamp/Year", out var year)
                || !meta.TryGetValue("TimeStamp/Month", out var month)
                || !meta.TryGetValue("TimeStamp/Day", out var day))
            {
                return null;
            }

            var time = meta.TryGetValue("TimeStamp/Hour", out var hour)
                && meta.TryGetValue("TimeStamp/Minute", out var minute)
                ? $" {int.Parse(hour):D2}:{int.Parse(minute):D2}"
                    + (meta.TryGetValue("TimeStamp/Second", out var second)
                        ? $":{int.Parse(second):D2}"
                        : string.Empty)
                : string.Empty;
            return $"{int.Parse(year):D4}-{int.Parse(month):D2}-{int.Parse(day):D2}{time}";
        }

        private static string Get(Dictionary<string, string> meta, string key)
        {
            return meta.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
                ? value
                : null;
        }

        private static string JoinNonEmpty(string separator, params string[] values)
        {
            var parts = new List<string>();
            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    parts.Add(value);
                }
            }

            return parts.Count == 0 ? null : string.Join(separator, parts);
        }

        private static string Truncate(string value)
        {
            return value.Length <= MaxValueLength ? value : value.Substring(0, MaxValueLength) + "…";
        }
    }
}
