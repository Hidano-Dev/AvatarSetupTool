# Research & Design Decisions

## Summary
- **Feature**: `highres-capture-reliability`
- **Discovery Scope**: Extension (既存 ModelCaptureService の改修。light discovery + 外部 API の個別調査)
- **Key Findings**:
  - `ImageConversion.EncodeArrayToPNG` は Unity 2020.1 以降で利用可能 (Unity 6000.x でも現行 API)。RGB 8bit フォーマットを渡せば 8bit RGB PNG を直接生成でき、Texture2D を経由しない。ただし行順 (top-down / bottom-up) は公式ドキュメントに明記がなく、実装時のピクセル同一性テストで確定させる必要がある
  - `SystemInfo.graphicsMemorySize` は統合 GPU / 共有メモリ環境で実際より小さい値 (dxdiag の約半分など) を返す既知の問題があり、VRAM 予算計算には下限クランプが必須
  - TDR (GPU タイムアウト) からのアプリレベルでの確実な復旧手段はない。最善策は「1 回の描画の GPU 負荷を小さく保って TDR 自体を回避する」ことであり、タイル辺長を約 4096px に制限する本改修の方針と一致する

## Research Log

### ImageConversion.EncodeArrayToPNG の仕様
- **Context**: フェーズ 2 (省メモリ) で Texture2D + SetPixels32 + EncodeToPNG を置換する候補 (Option 4-A) の API 契約確認。
- **Sources Consulted**:
  - [ImageConversion.EncodeArrayToPNG (Unity 6000.0)](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/ImageConversion.EncodeArrayToPNG.html)
  - [ImageConversion (Unity 6000.0)](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/ImageConversion.html)
  - [UnityCsReference ImageConversion.bindings.cs](https://github.com/Unity-Technologies/UnityCsReference/blob/master/Modules/ImageConversion/ScriptBindings/ImageConversion.bindings.cs)
  - [EncodeNativeArrayToPNG の内部動作に関する議論](https://discussions.unity.com/t/imageconversion-encodenativearraytopng-what-happens-under-the-hood/870417)
- **Findings**:
  - シグネチャ: `byte[] EncodeArrayToPNG(Array array, GraphicsFormat format, uint width, uint height, uint rowBytes = 0)`。`rowBytes = 0` で自動計算。
  - 非圧縮フォーマットのみ対応。8bit RGB フォーマット (`R8G8B8_SRGB` / `R8G8B8_UNorm`) を渡すと 8bit RGB (color type 2) の PNG が生成される。出力 PNG にガンマ補正やカラープロファイルのチャンクは書かれない (= バイト値がそのまま格納される)。
  - スレッドセーフと明記されている。
  - 行順は公式に未記載。コミュニティ報告 (AsyncGPUReadback のボトムアップデータをそのまま渡すと上下反転した PNG になる) から、エンコーダは配列先頭行を画像の最上段 (top-down) として扱うと推定される。ただし推定であるため実装時の検証が必須。
- **Implications**:
  - 合成バッファは RGB24 (3 bytes/px) の byte[] を top-down 行順で構築する。タイル読み戻し (GetPixels32 はボトムアップ) から合成する際に上下反転を行う。
  - `R8G8B8_SRGB` と `R8G8B8_UNorm` はどちらも 8bit 入力ではバイト値パススルーになる想定だが、design では既定を `R8G8B8_SRGB` とし、EncodeToPNG との**ピクセル完全一致テスト** (AC 4.4) を検証フックとして必ず用意する。テストが行順・フォーマット双方の推定を実装時に確定させる。

### SystemInfo.graphicsMemorySize の信頼性
- **Context**: タイル辺長の VRAM 予算計算に使う値の信頼性確認 (gap 分析の research 項目 4)。
- **Sources Consulted**:
  - [SystemInfo.graphicsMemorySize ドキュメント](https://docs.unity3d.com/ScriptReference/SystemInfo-graphicsMemorySize.html)
  - [共有メモリ環境で dxdiag の約半分を返す報告](https://forum.unity.com/threads/systeminfo-graphicsmemorysize-roughly-half-of-display-shared-memory-reported-by-dxdiag-exe.350489/)
  - [ユニファイドメモリ環境で過小報告される報告](https://discussions.unity.com/t/on-linux-with-unified-memory-with-vram-set-for-llm-best-practice-systeminfo-graphicsmemorysize-is-way-too-low-and-provokes-logspam/1732822)
- **Findings**:
  - 公式に「approximate (概算)」とされ、統合 GPU / 共有メモリ / ユニファイドメモリ環境では実際に利用可能な量より大幅に小さい値を返すことがある。
  - 過大報告よりも**過小報告**が典型的な故障モード。
- **Implications**:
  - VRAM 予算は `max(graphicsMemorySize, 1024MB)` の下限クランプを掛けてから計算する (Unity 6 エディタが動作する環境で実効グラフィックスメモリが 1GB 未満のケースは実質存在しない)。
  - タイル辺長には安全上限 4096px のハードキャップがあるため、VRAM 項が誤って小さすぎるタイルを強制することはクランプにより防がれ、誤って大きすぎるタイルを許すこともキャップにより防がれる。

### TDR (GPU タイムアウト) と PreviewRenderUtility の復旧
- **Context**: 黒フレームリトライ時に PreviewRenderUtility の再生成が必要か (gap 分析の research 項目 1)。
- **Sources Consulted**:
  - [Timeout Detection and Recovery (Wikipedia)](https://en.wikipedia.org/wiki/Timeout_Detection_and_Recovery)
  - [Unity Manual: Troubleshoot D3D12 GPU crashes on Windows](https://docs.unity3d.com/6000.5/Documentation/Manual/windows-troubleshoot-gpu-crash.html)
  - [D3D11 device reset/removed 関連スレッド](https://discussions.unity.com/t/failed-to-present-d3d11-swapchain-due-to-device-reset-removed-list-of-solutions/919068)
- **Findings**:
  - TDR は既定 2 秒の GPU 応答タイムアウトで発動し、ドライバがリセットされる。発動後の状態はドライバ・OS 依存で、アプリレベルで確実に復旧する API はない (Unity エディタ自身がデバイス再生成を試みるが保証なし)。
  - RenderTexture の内容はデバイスリセットで失われ、黒い結果になることがある。
  - 確実な対策は「1 回の GPU 送信を 2 秒以内に終わる規模に抑えて TDR を発動させない」こと。
- **Implications**:
  - **予防が主、リトライは従**。タイル辺長 4096px 上限 (16.7Mpx/枚、約 200MB) により、現行の 16384px 単一 RT (268Mpx、約 3GB) と比べ TDR / 確保失敗の発生自体を抑える。
  - リトライは同一 PreviewRenderUtility で同一タイルを 1 回再描画するだけとし、preview の再生成は行わない (再生成はモデル・グリッド・ライトの再構築を伴い複雑度に見合わない)。再失敗時は例外で失敗を伝搬し、黒 PNG を保存しない。

### 黒フレーム判定の厳密性
- **Context**: gap 分析の research 項目 5。誤検知 (正常フレームを黒と判定) の可能性検証。
- **Findings**:
  - カメラの clear color は常に BackgroundColor(184,184,184) の不透明グレーで、モデルの有無・構図・カラースペース (Linear/Gamma) にかかわらず、正常な読み戻し結果に RGB=(0,0,0) の全画素一致はあり得ない (Linear 変換されても 184 は 0 にならない)。
  - タイル分割後も各タイルの全面が背景色でクリアされるため、タイル単位の判定が成立する。
- **Implications**: 判定は「全画素で r==0 かつ g==0 かつ b==0 (アルファは無視)」の厳密比較とし、非黒画素を見つけた時点で打ち切る (正常系はほぼ先頭画素で終了、コスト無視可能)。

### テスト用 asmdef 構成
- **Context**: リポジトリにテストが 1 件もない (gap 分析)。internal メンバのテスト方法の確認。
- **Findings**:
  - UPM パッケージ規約では `Tests/Editor/` に EditMode テストを置く。埋め込みパッケージ (Packages/ 直下) のテストは manifest の `testables` なしで Test Runner に表示される。
  - asmdef 自体に InternalsVisibleTo の設定項目はなく、`AssemblyInfo.cs` に `[assembly: InternalsVisibleTo("...Tests")]` を書く方式が標準。
  - テスト asmdef は `UnityEngine.TestRunner` / `UnityEditor.TestRunner` 参照 + `nunit.framework.dll` precompiled 参照 + defineConstraints `UNITY_INCLUDE_TESTS` が定型。
- **Implications**: `Tests/Editor/Hidano.AvatarSetupTool.Editor.Tests.asmdef` を新設し、本体 Editor アセンブリに `AssemblyInfo.cs` を追加する。

## Architecture Pattern Evaluation

| Option | Description | Strengths | Risks / Limitations | Notes |
|--------|-------------|-----------|---------------------|-------|
| A: RenderStill を直接書き換え | 既存メソッド内でタイル計算も一緒に修正 | 差分最小 | タイル計算がテスト不能なまま残る | テストゼロの現状を改善できない |
| B: キャプチャパイプラインを別クラス群へ全面分離 | レンダラ・合成・エンコードを各クラス化 | 責務明確 | 出力仕様不変の制約に対しリスク・工数過大 | 過剰設計 |
| **C: ハイブリッド (採用)** | 純粋ロジック (タイルレイアウト・縮小合成・黒判定) を internal に抽出し、RenderStill / CaptureShot はインプレース修正 | 純粋ロジックが単体テスト可能、描画経路の差分は最小 | internal 公開が増える | gap 分析の推奨。InternalsVisibleTo で担保 |

## Design Decisions

### Decision: タイル辺長の決定式
- **Context**: 1.1, 1.5, 2.2, 2.3。maxTextureSize 基準では 8K+SSAA で 3GB 級 RT を確保しにいく。
- **Alternatives Considered**:
  1. maxTextureSize のみ (現行) — 8K で確保失敗・TDR
  2. VRAM 予算のみ — graphicsMemorySize が信頼できない
  3. 固定 4096px のみ — 低 VRAM 環境の保護がない
- **Selected Approach**: `TileSideLimit = min(SafeTileSide=4096, maxTextureSize, VRAM予算由来の辺長)`。VRAM 予算 = `max(graphicsMemorySize, 1024MB) × 1024² ÷ 2`、辺長 = `floor(sqrt(予算 ÷ 12 bytes/px))` (float16 カラー + 深度で約 12 bytes/px、現行 StillSuperSample と同じ係数)。
- **Rationale**: 4096px タイルは約 200MB/枚で Unity 6 エディタ動作環境なら確実に確保でき、TDR も回避できる。VRAM 項は下限クランプ済みなので過小報告に汚染されない。
- **Trade-offs**: 8K+SSAA×2 は 16 タイル (4×4) になり描画回数が増えるが、正射影のため結果は同一で、確保失敗リスクの排除を優先する。
- **Follow-up**: 実測でタイルオーバーヘッド込みの StillRenderRate を再較正する。

### Decision: 非一様タイルレイアウト (任意分割数)
- **Context**: 1.3, 1.4。現行 TileCount は SSAA 倍率の 2 冪約数に限定され、4096px 上限では分割数が足りない。
- **Alternatives Considered**:
  1. 2 冪約数の維持 + 上限緩和 — 8K×SSAA2 で 16384/4096=4 分割は偶然表現できるが、非正方形構図や端数で破綻
  2. 出力ピクセル空間で任意の等分割 + 端タイルは剰余 (採用)
- **Selected Approach**: 出力ピクセル空間でタイル矩形を定義する。`maxBlock = floor(TileSideLimit ÷ factor)` (出力 px)、`tiles = ceil(size ÷ maxBlock)`、最後のタイルのみ剰余サイズ。レンダサイズは `block × factor` で、タイル境界は常に「出力ピクセル × SSAA 倍率」境界に整列するため、ボックス平均縮小の結果は単一描画と画素単位で一致する。
- **Rationale**: 境界整列がピクセル同一性 (1.4) の必要十分条件。純粋な整数計算なので単体テストで全域検証できる。
- **Trade-offs**: タイルごとにカメラ矩形 (orthoSize・aspect・中心) を出力ピクセル基準で計算する非一様対応が必要 (現行は全タイル同形)。
- **Follow-up**: 小サイズでのタイル分割 vs 単一描画のピクセル一致を EditMode テストで検証する。

### Decision: 黒フレームの失敗伝搬は Result 契約を維持
- **Context**: 3.3, 3.4, 6.4, 6.5。UI (ダイアログ) と CLI (ログ + 戻り値) の両方で失敗を通知する必要がある。
- **Alternatives Considered**:
  1. 例外を Capture の外へ投げっぱなし — CLI 呼び出し元の既存契約 (CaptureResult) が壊れる
  2. RenderStill が内部例外を投げ、Capture が捕捉して CaptureResult.Fail へ変換 (採用)
- **Selected Approach**: internal 例外 `CaptureRenderFailedException` (解像度・SSAA 倍率・タイル情報を保持) を RenderStill が投げ、Capture が捕捉して `Debug.LogError` + `CaptureResult.Fail(message)` を返す。ModelCaptureWindow は既存の `result.Error` ダイアログ経路をそのまま使う。
- **Rationale**: 公開 API (`Capture` の戻り値契約) を変えずに UI/CLI 双方の要件を満たす。深いコールスタック (CaptureShot → RenderStill → タイルループ) からの脱出は例外が最も単純。
- **Trade-offs**: internal 例外型が 1 つ増えるのみ。

### Decision: PNG エンコードは Option 4-A (EncodeArrayToPNG + RGB24 byte[])
- **Context**: 4.1〜4.5。現行は Color32[] (256MB) + Texture2D + EncodeToPNG 中間コピーでピーク 1.5〜2GB。
- **Alternatives Considered**:
  1. 4-A: RGB24 byte[] 合成バッファ + `EncodeArrayToPNG` — ピーク約 0.3〜0.5GB。実装小
  2. 4-B: PngMetadata の CRC32 を流用した行バンドストリーミング PNG エンコーダ自作 — 全面バッファ自体が不要だが、DeflateStream の圧縮率・速度検証と PNG フォーマット実装の検証コストが大きい
- **Selected Approach**: 4-A を採用。合成先を `Color32[]` (4 bytes/px、ボトムアップ) から `byte[]` RGB24 (3 bytes/px、top-down) に変更し、タイル縮小書き込み時に行反転する。iTXt は既存 `PngMetadata.WithText` を無変更で適用する。4-B は 4-A で AC 4.2 (ピーク削減) を満たせなかった場合の予備フェーズとして設計上の席のみ確保する。
- **Rationale**: 4-A で 8K ピークは約 1/4〜1/5 になり要件を満たす。AC 4.3 は Where 句 (ストリーミングエンコーダ採用時のみ) の条件付き要件であり、4-A 採用時は発火しない。
- **Trade-offs**: 8K 全面の RGB24 バッファ (約 201MB) は残るが、要件の削減目標には十分。
- **Follow-up**: EncodeToPNG とのピクセル完全一致テストで行順と GraphicsFormat (`R8G8B8_SRGB`) の推定を確定する。不一致なら `R8G8B8_UNorm` / 行反転の組合せを切り替える (実装内定数の変更で吸収)。

### Decision: SSAA 倍率決定から VRAM 降格を撤去
- **Context**: 2.1, 2.2, 2.3。現行 StillSuperSample は VRAM 予算不足で倍率を黙って 1 に落とす。
- **Selected Approach**: 倍率は解像度既定 (≤2048px → 4、超 → 2) を常に採用する。タイル分割が VRAM 制約を吸収するため降格は原則不要。防衛的例外として `TileSideLimit ÷ factor < MinBlockSide (64px)` の場合のみ、警告ログ (適用倍率を明記) を出して倍率を下げる。
- **Rationale**: TileSideLimit は下限クランプにより実質 4096 なので降格は事実上発生しないが、AC 2.3 の契約 (降格前の警告と適用倍率の可視化) を仕様として固定する。

## Risks & Mitigations
- EncodeArrayToPNG の行順・フォーマット挙動が推定と異なる — ピクセル完全一致テストを phase 2 の入口に置き、不一致なら実装内の行順/フォーマット定数で吸収 (アーキテクチャに影響しない)
- タイル境界のレンダリング誤差 (ラスタライズの浮動小数点差) — タイル矩形を出力ピクセル整数境界で定義し、カメラ矩形をピクセル単位換算で構成。小サイズのタイル vs 単一描画一致テストで検証
- TDR がタイル縮小後も発生する環境 (極端に重いシェーダ等) — 黒フレーム検出 + リトライ + 明示的失敗報告で「黙って黒 PNG」だけは確実に排除
- graphicsMemorySize の過大報告 (誤って大きいタイルを許す) — SafeTileSide=4096 のハードキャップで上限を固定
- 見積もり係数の乖離 — TimeCalibrationFactor の平滑化 (既存機構) が残差を吸収。メモリ見積もりは新経路の実バッファ構成から再導出

## References
- [ImageConversion.EncodeArrayToPNG (Unity 6000.0)](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/ImageConversion.EncodeArrayToPNG.html) — 省メモリエンコードの中核 API
- [ImageConversion (Unity 6000.0)](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/ImageConversion.html) — Encode 系 API 一覧
- [UnityCsReference ImageConversion.bindings.cs](https://github.com/Unity-Technologies/UnityCsReference/blob/master/Modules/ImageConversion/ScriptBindings/ImageConversion.bindings.cs) — バインディング実装
- [SystemInfo.graphicsMemorySize](https://docs.unity3d.com/ScriptReference/SystemInfo-graphicsMemorySize.html) — 「approximate」の明記
- [graphicsMemorySize が共有メモリの約半分を返す報告](https://forum.unity.com/threads/systeminfo-graphicsmemorysize-roughly-half-of-display-shared-memory-reported-by-dxdiag-exe.350489/) — 下限クランプの根拠
- [Timeout Detection and Recovery](https://en.wikipedia.org/wiki/Timeout_Detection_and_Recovery) — TDR の既定 2 秒タイムアウト
- [Unity Manual: Troubleshoot GPU crashes on Windows](https://docs.unity3d.com/6000.5/Documentation/Manual/windows-troubleshoot-gpu-crash.html) — デバイスロスト時の挙動
