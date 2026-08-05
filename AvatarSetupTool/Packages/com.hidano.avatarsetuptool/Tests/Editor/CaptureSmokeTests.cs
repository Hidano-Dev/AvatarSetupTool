using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Hidano.AvatarSetupTool.Editor.Tests
{
    /// <summary>
    /// キャプチャ経路のスモークテスト (EditMode で GPU 描画を直接使用)。
    /// 小型テストモデルで <see cref="ModelCaptureService.Capture"/> を公開契約どおりに実行し、
    /// PNG が生成されること・iTXt (Comment) が読めること・成功結果が返ることを確認する。
    /// 出力内容の詳細 (ピクセル一致・タイル等価性・チャンク仕様) は各ユニットテストが担うため、
    /// ここでは新エンコード経路での end-to-end の成立のみを検証する。
    /// </summary>
    public class CaptureSmokeTests
    {
        [Test]
        public void Capture_SmallModel_ReturnsSuccessAndWritesReadablePngWithItxt()
        {
            var outputRoot = Path.Combine(
                Path.GetTempPath(), "AvatarSetupToolTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(outputRoot);
            GameObject source = null;
            Avatar avatar = null;
            try
            {
                source = BuildSmallTestModel(out avatar);
                var settings = new CaptureSettings
                {
                    format = CaptureOutputFormat.ImagesOnly,
                    viewMode = CaptureViewMode.FullOnly,
                    imageSize = CaptureSettings.MinImageSize,
                    outputRoot = outputRoot,
                    // 各 PNG へ iTXt (Comment) を埋め込む経路を通す
                    includeDebugInfo = true,
                };

                // 汎用アバターは Neck ボーンを持たないため、顔アップ構図のフォールバック警告が出る
                LogAssert.Expect(LogType.Warning, new Regex("Neck ボーンが取得できない"));

                var result = ModelCaptureService.Capture(source, settings);

                Assert.That(result.Error, Is.Null, "成功時はエラーメッセージを持たないこと");
                Assert.That(result.Canceled, Is.False, "キャンセルされていないこと");
                Assert.That(result.Success, Is.True, "成功結果 (CaptureResult.Success) が返ること");
                Assert.That(result.OutputDirectory, Does.StartWith(outputRoot));
                Assert.That(Directory.Exists(result.OutputDirectory), Is.True, "出力フォルダが作られること");
                Assert.That(
                    File.Exists(Path.Combine(result.OutputDirectory, "debug_info.md")),
                    Is.True, "includeDebugInfo で debug_info.md が書き出されること");

                var pngs = Directory.GetFiles(result.OutputDirectory, "*.png");
                Assert.That(pngs.Length, Is.EqualTo(8), "FullOnly では 8 方向分の PNG が生成されること");

                foreach (var path in pngs)
                {
                    var name = Path.GetFileName(path);
                    var bytes = File.ReadAllBytes(path);
                    AssertLoadablePng(bytes, name);

                    var comment = ReadItxtComment(bytes);
                    Assert.That(comment, Is.Not.Null.And.Not.Empty, $"iTXt (Comment) が読めること: {name}");
                    StringAssert.Contains("Captured:", comment, $"iTXt にデバッグ情報の本文が入っていること: {name}");
                }
            }
            finally
            {
                if (source != null)
                {
                    Object.DestroyImmediate(source);
                }

                if (avatar != null)
                {
                    Object.DestroyImmediate(avatar);
                }

                if (Directory.Exists(outputRoot))
                {
                    Directory.Delete(outputRoot, recursive: true);
                }
            }
        }

        /// <summary>
        /// 小型テストモデルを組む。Capture は「Avatar 付き Animator」を撮影対象として検出するため、
        /// 骨格を持たない箱モデルに実行時生成の汎用アバターを付けて条件を満たす。
        /// </summary>
        private static GameObject BuildSmallTestModel(out Avatar avatar)
        {
            var root = new GameObject("CaptureSmokeModel");
            var body = GameObject.CreatePrimitive(PrimitiveType.Cube);
            body.name = "Body";
            body.transform.SetParent(root.transform, false);
            body.transform.localScale = new Vector3(0.4f, 1f, 0.4f);
            body.transform.localPosition = new Vector3(0f, 0.5f, 0f);

            var animator = root.AddComponent<Animator>();
            avatar = AvatarBuilder.BuildGenericAvatar(root, string.Empty);
            avatar.name = "CaptureSmokeAvatar";
            animator.avatar = avatar;
            return root;
        }

        /// <summary>PNG としてデコードでき、高さが設定どおりであることを確認する。</summary>
        private static void AssertLoadablePng(byte[] bytes, string name)
        {
            var texture = new Texture2D(2, 2);
            try
            {
                Assert.That(texture.LoadImage(bytes), Is.True, $"PNG としてデコードできること: {name}");
                Assert.That(
                    texture.height, Is.EqualTo(CaptureSettings.MinImageSize),
                    $"出力高さが設定 (imageSize) どおりであること: {name}");
                Assert.That(texture.width, Is.GreaterThan(0), $"幅が正であること: {name}");
            }
            finally
            {
                Object.DestroyImmediate(texture);
            }
        }

        /// <summary>
        /// PNG のチャンク列を走査し、iTXt (keyword=Comment) の本文を UTF-8 で読み出す。
        /// 見つからなければ null (実装と独立した最小限のリーダー)。
        /// </summary>
        private static string ReadItxtComment(byte[] png)
        {
            // シグネチャ 8 bytes の直後から、[長さ 4][タイプ 4][データ][CRC 4] のチャンク列
            var offset = 8;
            while (offset + 12 <= png.Length)
            {
                var length = (int)(((uint)png[offset] << 24) | ((uint)png[offset + 1] << 16)
                    | ((uint)png[offset + 2] << 8) | png[offset + 3]);
                var type = Encoding.ASCII.GetString(png, offset + 4, 4);
                var dataStart = offset + 8;
                if (type == "iTXt")
                {
                    // keyword \0 圧縮フラグ 圧縮方式 言語タグ \0 翻訳キーワード \0 本文
                    var keywordEnd = Array.IndexOf(png, (byte)0, dataStart, length);
                    var keyword = Encoding.ASCII.GetString(png, dataStart, keywordEnd - dataStart);
                    if (keyword == "Comment")
                    {
                        var languageEnd = Array.IndexOf(png, (byte)0, keywordEnd + 3);
                        var translatedEnd = Array.IndexOf(png, (byte)0, languageEnd + 1);
                        var textStart = translatedEnd + 1;
                        return Encoding.UTF8.GetString(png, textStart, dataStart + length - textStart);
                    }
                }

                offset = dataStart + length + 4;
            }

            return null;
        }
    }
}
