using System;
using System.IO;
using System.Text.RegularExpressions;

namespace Hidano.AvatarSetupTool.Editor
{
    /// <summary>
    /// 出力ファイル名のワイルドカード (Unity Recorder と同様の &lt;Token&gt; 形式) を解決する。
    /// </summary>
    public static class CaptureFileName
    {
        /// <summary>使用可能なワイルドカードと説明。UI の挿入メニューもこの順で並ぶ。</summary>
        public static readonly (string Token, string Description)[] Wildcards =
        {
            ("<Model>", "アセット / GameObject 名"),
            ("<Target>", "Animator オブジェクト名"),
            ("<Direction>", "方向 (01_front など。動画では空)"),
            ("<View>", "full / face"),
            ("<Resolution>", "出力解像度 (幅x高さ)"),
            ("<Date>", "日付 (yyyy-MM-dd)"),
            ("<Time>", "時刻 (HH-mm-ss)"),
            ("<Take>", "テイク番号 (001〜)"),
        };

        /// <summary>
        /// パターン内のワイルドカードを実際の値へ置換し、ファイル名として安全な文字列を返す
        /// (拡張子は含まない)。動画では direction に null を渡すと &lt;Direction&gt; が空になり、
        /// 連続した区切り文字は 1 つに畳まれる。
        /// </summary>
        public static string Resolve(
            string pattern, string model, string target, string direction, string view,
            int width, int height, DateTime timestamp, int take)
        {
            var name = pattern
                .Replace("<Model>", model)
                .Replace("<Target>", target)
                .Replace("<Direction>", direction ?? string.Empty)
                .Replace("<View>", view)
                .Replace("<Resolution>", $"{width}x{height}")
                .Replace("<Date>", timestamp.ToString("yyyy-MM-dd"))
                .Replace("<Time>", timestamp.ToString("HH-mm-ss"))
                .Replace("<Take>", take.ToString("000"));

            name = Regex.Replace(name, "_{2,}", "_").Trim('_', ' ');
            return Sanitize(string.IsNullOrEmpty(name) ? "capture" : name);
        }

        /// <summary>ファイル名に使えない文字を '_' へ置換する。</summary>
        public static string Sanitize(string name)
        {
            foreach (var invalid in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(invalid, '_');
            }

            return name;
        }
    }
}
