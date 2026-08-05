using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Hidano.AvatarSetupTool.Editor.Tests
{
    /// <summary>
    /// タイル描画と単一パス描画の等価性統合テスト (EditMode で GPU 描画を直接使用)。
    /// 小サイズ (512px 級) で辺長上限を強制的に絞ったレイアウトを RenderStill へ注入し、
    /// 多タイル合成の結果が単一パス描画と全ピクセルで一致する (完全一致目標、
    /// 差がある場合も各チャンネル ±1 階調以内) ことを検証する。
    ///
    /// シーンは画面全体を覆う高周波テクスチャ板 (バイリニア補間 = 画面上で連続な画像) で構成し、
    /// 幾何エッジ (シルエット) を画面内に置かない。エッジはタイル毎の射影行列の浮動小数差が
    /// ラスタライザの被覆判定を反転させて ±1 階調を超える差を生む (GPU の原理上避けられない) 一方、
    /// 連続な画像はサブピクセルジッタが ±1 階調未満に収まるため、タイル矩形→カメラ矩形換算や
    /// 合成位置の実バグ (1px ズレ等) だけを高感度に検出できる。
    /// </summary>
    public class TileRenderEquivalenceTests
    {
        private PreviewRenderUtility preview;
        private readonly List<Object> created = new List<Object>();

        [OneTimeSetUp]
        public void SetUpScene()
        {
            preview = new PreviewRenderUtility();
            ModelCaptureService.SetupCameraAndLights(preview);
            BuildScene();

            // SRP では最初のレンダリングが空になることがあるため、本番 Capture と同じく捨て描画する
            var warmupView = MakeView(128, 128);
            ModelCaptureService.RenderStill(
                preview, warmupView, TileLayout.Compute(128, 128, 2, TileSideLimits.SafeTileSide),
                null, "warmup");
        }

        [OneTimeTearDown]
        public void TearDownScene()
        {
            preview?.Cleanup();
            foreach (var obj in created)
            {
                if (obj != null)
                {
                    Object.DestroyImmediate(obj);
                }
            }

            created.Clear();
        }

        // 必須ケース:
        // - 512x512 / 上限 300 (blockSide 150): 剰余タイル (62px) を含む不均等な 4x4 分割
        // - 640x384 / 上限 512 (blockSide 256): 非正方形構図かつ非 2 冪の 3x2 分割 (両軸に剰余タイル)
        // - 480x480 / 上限 320 (blockSide 160): 非 2 冪の 3x3 等分割 (剰余なし)
        [TestCase(512, 512, 300, 4, 4)]
        [TestCase(640, 384, 512, 3, 2)]
        [TestCase(480, 480, 320, 3, 3)]
        public void TiledRender_MatchesSinglePassWithinOneStep(
            int width, int height, int tileSideLimit, int expectedTilesX, int expectedTilesY)
        {
            const int factor = 2;
            var view = MakeView(width, height);

            var tiledLayout = TileLayout.Compute(width, height, factor, tileSideLimit);
            Assert.That(tiledLayout.TilesX, Is.EqualTo(expectedTilesX), "前提: X 分割数");
            Assert.That(tiledLayout.TilesY, Is.EqualTo(expectedTilesY), "前提: Y 分割数");
            Assert.That(tiledLayout.Factor, Is.EqualTo(factor), "前提: SSAA 倍率が縮退しない");

            var singleLayout = TileLayout.Compute(width, height, factor, TileSideLimits.SafeTileSide);
            Assert.That(singleLayout.IsSinglePass, Is.True, "前提: 基準側は単一パス");

            var single = ModelCaptureService.RenderStill(preview, view, singleLayout, null, "single");
            var tiled = ModelCaptureService.RenderStill(preview, view, tiledLayout, null, "tiled");

            AssertSceneHasVariation(single);
            AssertEquivalent(single, tiled, width, height);
        }

        /// <summary>
        /// レンダ解像度から ViewSpec を作る。ViewSpec はアニメ解像度 × SuperSampleFactor が
        /// レンダ解像度になる契約のため、逆算して渡す (レンダ解像度は偶数であること)。
        /// </summary>
        private static ModelCaptureService.ViewSpec MakeView(int renderWidth, int renderHeight)
        {
            return new ModelCaptureService.ViewSpec(
                Vector3.zero, 1f, 2f,
                renderWidth / ModelCaptureService.SuperSampleFactor,
                renderHeight / ModelCaptureService.SuperSampleFactor);
        }

        /// <summary>
        /// シーンが実際に描画されている (背景一色に潰れていない) ことの前提確認。
        /// これが失敗する場合は等価性比較自体が無意味 (空画像どうしの一致) になっている。
        /// pixels は RenderStill が返す RGB24 (3 bytes/px) 合成バッファ。
        /// </summary>
        private static void AssertSceneHasVariation(byte[] pixels)
        {
            for (var i = 3; i + 2 < pixels.Length; i += 3)
            {
                if (pixels[i] != pixels[0] || pixels[i + 1] != pixels[1] || pixels[i + 2] != pixels[2])
                {
                    return;
                }
            }

            Assert.Fail("前提: 描画結果が単色でシーンが描画されていません。");
        }

        /// <summary>
        /// 全ピクセルを比較し、各チャンネル ±1 階調を超える差を失敗として報告する。
        /// expected / actual は RenderStill が返す RGB24 (3 bytes/px) 合成バッファ。
        /// </summary>
        private static void AssertEquivalent(byte[] expected, byte[] actual, int width, int height)
        {
            Assert.That(actual.Length, Is.EqualTo(expected.Length));

            var mismatches = new List<string>();
            var exactMatches = 0;
            var pixelCount = expected.Length / 3;
            for (var i = 0; i < pixelCount; i++)
            {
                var offset = i * 3;
                var er = expected[offset];
                var eg = expected[offset + 1];
                var eb = expected[offset + 2];
                var ar = actual[offset];
                var ag = actual[offset + 1];
                var ab = actual[offset + 2];
                if (er == ar && eg == ag && eb == ab)
                {
                    exactMatches++;
                    continue;
                }

                if (Mathf.Abs(er - ar) > 1 || Mathf.Abs(eg - ag) > 1 || Mathf.Abs(eb - ab) > 1)
                {
                    if (mismatches.Count < 10)
                    {
                        mismatches.Add(
                            $"({i % width}, {i / width}): 単一 ({er},{eg},{eb})"
                            + $" vs タイル ({ar},{ag},{ab})");
                    }
                    else
                    {
                        mismatches.Add("...");
                        break;
                    }
                }
            }

            TestContext.WriteLine(
                $"{width}x{height}: 完全一致 {exactMatches}/{pixelCount} px"
                + $" ({exactMatches * 100.0 / pixelCount:F2}%)");
            Assert.That(mismatches, Is.Empty, "±1 階調を超える差のあるピクセル");
        }

        /// <summary>
        /// 画面全体 (最大 half-width 1.67 の全構図) をはみ出して覆う高周波テクスチャ板を
        /// 2 枚重ねてシーンを組む。手前の板は半透明かつ回転させ、ブレンドと非軸整列の
        /// UV 補間も比較対象に含める。どちらもエッジが画面外になるサイズにする。
        /// Sprites/Default (アンリット) なのでライティングに依存せず決定的に描ける。
        /// </summary>
        private void BuildScene()
        {
            AddTexturedQuad(
                new Vector3(0f, 0f, 1.5f), new Vector2(6f, 4f), 0f,
                MakeNoiseTexture(256, 192, 12345u, opaque: true));
            AddTexturedQuad(
                new Vector3(0.2f, -0.1f, 0.8f), new Vector2(10f, 10f), 20f,
                MakeNoiseTexture(160, 160, 67890u, opaque: false));
        }

        /// <summary>
        /// 決定的な擬似乱数 (LCG) のノイズテクスチャを作る。バイリニア補間で画面上の色が
        /// 位置に対して連続になり、かつテクセル密度が render px より粗いため、
        /// サブピクセルジッタによる色差が ±1 階調未満に収まる。
        /// opaque が偽の場合はアルファも 64..191 で変化させ、ブレンド経路も検証する。
        /// </summary>
        private Texture2D MakeNoiseTexture(int width, int height, uint seed, bool opaque)
        {
            var pixels = new Color32[width * height];
            var state = seed;
            for (var i = 0; i < pixels.Length; i++)
            {
                state = state * 1664525u + 1013904223u;
                pixels[i] = new Color32(
                    (byte)(state >> 24), (byte)(state >> 16), (byte)(state >> 8),
                    opaque ? (byte)255 : (byte)(64 + (state & 0x7F)));
            }

            var texture = new Texture2D(width, height, TextureFormat.RGBA32, mipChain: false)
            {
                name = "EquivalenceNoise",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                anisoLevel = 0,
                hideFlags = HideFlags.HideAndDontSave,
            };
            texture.SetPixels32(pixels);
            texture.Apply(updateMipmaps: false);
            created.Add(texture);
            return texture;
        }

        /// <summary>
        /// テクスチャ付きの板を 1 枚シーンへ追加する。頂点順と三角形分割は
        /// 本体の GridBackdrop.AddQuad と同じ (左下 → 左上 → 右上 → 右下)。
        /// </summary>
        private void AddTexturedQuad(Vector3 center, Vector2 size, float zRotationDegrees, Texture2D texture)
        {
            var halfW = size.x / 2f;
            var halfH = size.y / 2f;
            var mesh = new Mesh
            {
                name = "EquivalenceQuad",
                vertices = new[]
                {
                    new Vector3(-halfW, -halfH, 0f),
                    new Vector3(-halfW, halfH, 0f),
                    new Vector3(halfW, halfH, 0f),
                    new Vector3(halfW, -halfH, 0f),
                },
                uv = new[]
                {
                    new Vector2(0f, 0f),
                    new Vector2(0f, 1f),
                    new Vector2(1f, 1f),
                    new Vector2(1f, 0f),
                },
                triangles = new[] { 0, 1, 2, 0, 2, 3 },
            };
            created.Add(mesh);

            var go = new GameObject("EquivalenceQuad");
            go.transform.SetPositionAndRotation(center, Quaternion.Euler(0f, 0f, zRotationDegrees));
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            var material = new Material(Shader.Find("Sprites/Default"))
            {
                hideFlags = HideFlags.HideAndDontSave,
                mainTexture = texture,
            };
            created.Add(material);
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            preview.AddSingleGO(go);
        }
    }
}
