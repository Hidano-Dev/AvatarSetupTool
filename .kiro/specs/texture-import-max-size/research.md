# Research & Design Decisions

## Summary
- **Feature**: `texture-import-max-size`
- **Discovery Scope**: Extension (既存 Editor アセンブリへの機能追加、ライト版ディスカバリ)
- **Key Findings**:
  - 既存の `FbxImportSettingsPostprocessor` が「`importSettingsMissing` による初回インポート判定 + AssetPostprocessor」の流儀を確立しており、テクスチャ側も同一パターンで実装できる
  - `TextureImporter.maxTextureSize` は Default (既定) プラットフォーム設定のみに作用するため、「プラットフォーム別オーバーライドは変更しない」という要件境界と API の作用範囲が一致する
  - 対象 9 フォーマットはいずれもヘッダ近傍 (PNG/JPEG/TGA/PSD/BMP/GIF/EXR/HDR) または IFD へのシーク (TIFF) のみで解像度を取得でき、ピクセルデコードは不要。TGA のみマジックバイトが無いため拡張子ベースのフォーマット判別が必須

## Research Log

### 既存コードベースの拡張ポイント分析
- **Context**: 実装先アセンブリの流儀 (初回インポート判定・軽量バイナリ読み取り・テスト構成) を踏襲するため
- **Sources Consulted**:
  - `AvatarSetupTool/Packages/com.hidano.avatarsetuptool/Editor/FbxImportSettingsPostprocessor.cs`
  - `AvatarSetupTool/Packages/com.hidano.avatarsetuptool/Editor/FbxHeaderReader.cs`
  - `AvatarSetupTool/Packages/com.hidano.avatarsetuptool/Editor/ModelCaptureWindow.cs` (MenuItem 命名)
  - `Editor/Hidano.AvatarSetupTool.Editor.asmdef`, `Tests/Editor/Hidano.AvatarSetupTool.Editor.Tests.asmdef`, `Editor/AssemblyInfo.cs`
- **Findings**:
  - `FbxImportSettingsPostprocessor` は `sealed class` + `AssetPostprocessor`。`importer.importSettingsMissing` が true (.meta 未生成 = 初回インポート) のときだけ設定を書き、既存アセットには触れない
  - `FbxHeaderReader` は `internal static` クラス。`FileStream(FileMode.Open, FileAccess.Read, FileShare.ReadWrite)` の読み取り専用アクセスで、全例外を握りつぶして「取れた分だけ返す」設計。ヘッダ以降 (Objects ノード以降) は読まない
  - MenuItem は `"Assets/Avatar Setup Tool/Capture Model Images..."` (priority 1000) + validate 関数のペアという既存命名・構成がある
  - Editor asmdef は `includePlatforms: ["Editor"]`、`AssemblyInfo.cs` で `InternalsVisibleTo("Hidano.AvatarSetupTool.Editor.Tests")` 済み。internal クラスをそのまま EditMode テストから参照できる
  - テストは `Tests/Editor/` に NUnit EditMode テストとして配置。`PngMetadataTests` のように「実装と独立な参照実装/固定バイト列で仕様を固定する」書き方が主流
- **Implications**: 新規コンポーネントはすべて `Hidano.AvatarSetupTool.Editor` 名前空間の internal (Postprocessor のみ Unity 要件上 class として public 可視性不要のため sealed class)、読み取りユーティリティは `FbxHeaderReader` と同じ防御的スタイルで設計する。新しい外部依存は不要。

### TextureImporter API の作用範囲確認
- **Context**: maxTextureSize 設定が要件の境界 (Default 設定のみ変更、オーバーライド不変) と一致するか確認
- **Sources Consulted**:
  - [TextureImporter.maxTextureSize (Unity Scripting API)](https://docs.unity3d.com/ScriptReference/TextureImporter-maxTextureSize.html)
  - [AssetPostprocessor.OnPreprocessTexture (Unity Scripting API)](https://docs.unity3d.com/ScriptReference/AssetPostprocessor.OnPreprocessTexture.html)
- **Findings**:
  - `TextureImporter.maxTextureSize` は Default プラットフォーム設定にのみ作用し、プラットフォーム別オーバーライド (`TextureImporterPlatformSettings`) には影響しない
  - `OnPreprocessTexture()` 内で `assetImporter as TextureImporter` に設定を書くのが公式パターン。インポート確定前に呼ばれるため再インポートループは発生しない
  - Inspector の Max Size 選択肢は 32/64/128/256/512/1024/2048/4096/8192/16384、既定値は 2048 (要件の選択肢リストと一致)
- **Implications**: Requirement 4.2 (maxTextureSize 以外を変更しない) は「`maxTextureSize` プロパティ以外に書き込まない」ことで API レベルで保証できる。オーバーライド保護のための追加コードは不要。

### 画像フォーマット別ヘッダ解像度の取得方法
- **Context**: Requirement 1.2 の 9 フォーマット (PNG・JPEG・TGA・PSD・BMP・GIF・TIFF・EXR・HDR) をヘッダ近傍のみで読む方式の確定
- **Sources Consulted**: 各フォーマット仕様 (PNG ISO/IEC 15948, JPEG ISO/IEC 10918 マーカー構造, TGA 2.0, Adobe PSD File Format, BMP BITMAPINFOHEADER, GIF89a, TIFF 6.0, OpenEXR File Layout, Radiance HDR) の既知構造
- **Findings**: 全フォーマットの解像度フィールド位置を確定 (design.md の Supporting References にフォーマット表として記載)。要点:
  - 固定オフセット読みで済むもの: PNG (IHDR)、TGA、PSD/PSB、BMP、GIF
  - 走査が必要なもの: JPEG (SOFn マーカーまで走査)、EXR (属性リスト内の `dataWindow` 走査)、HDR (テキスト解像度行の走査)
  - シークが必要なもの: TIFF (ヘッダの IFD オフセットへシークし tag 256/257 を読む。IFD がファイル末尾にある場合もあるがピクセルデコードは不要で、読み取り量は数 KB に収まる)
  - TGA には先頭マジックバイトが存在しないため、フォーマット判別は拡張子ベースにするしかない (Unity 自体も拡張子でインポータを決めているため整合する)
  - PSD の "8BPS" シグネチャは PSB (version 2) と共通のため、PSB もほぼ無償で対応可能
- **Implications**: 「拡張子でパーサを選択 → 各パーサがマジック/構造を検証 → 不一致・破損は解像度不明」という 2 段構えにする。走査系パーサには読み取り上限 (バイト数・エントリ数) を設けて壊れたファイルでも高速に諦める。

### 一括検証・修正の Unity エディタ API 構成
- **Context**: Requirement 3 (右クリックメニュー・列挙・確認・進捗・キャンセル・報告) の実現手段
- **Sources Consulted**: 既存 `ModelCaptureWindow.cs` の MenuItem 実装、UnityEditor API (AssetDatabase / EditorUtility / Selection) の既知仕様
- **Findings**:
  - 選択の取得: `Selection.assetGUIDs` → `AssetDatabase.GUIDToAssetPath`。フォルダは `AssetDatabase.IsValidFolder` で判別し `AssetDatabase.FindAssets("t:Texture2D", folders)` でサブフォルダ込み列挙、ファイルは `AssetImporter.GetAtPath` が `TextureImporter` かで判別。複数選択の重複は HashSet で除去
  - 進捗 + キャンセル: `EditorUtility.DisplayCancelableProgressBar` / `ClearProgressBar`
  - 確認・報告: `EditorUtility.DisplayDialog` (件数提示) + Console ログ (個別内容)
  - 修正適用: `TextureImporter.maxTextureSize` 書き換え + `SaveAndReimport()`。多数アセットは `AssetDatabase.StartAssetEditing` / `StopAssetEditing` (try/finally) で囲みインポートをバッチ化
- **Implications**: UI (メニュー・ダイアログ・進捗バー) と検証ロジックを分離し、ロジック側は進捗コールバック (bool 返却でキャンセル) を受け取る形にすれば EditMode テストから UI なしで検証できる。

## Architecture Pattern Evaluation

| Option | Description | Strengths | Risks / Limitations | Notes |
|--------|-------------|-----------|---------------------|-------|
| 純ロジック層 + Unity 統合層の 2 層分離 (採用) | Reader/Calculator を UnityEditor UI 非依存の internal static に置き、Postprocessor と ContextMenu が薄く呼ぶ | FbxHeaderReader の既存流儀と一致、EditMode テスト容易、責務境界明確 | ファイル数が増える (5 ファイル) | 採用。5.3 のテスト可能性要件を最短で満たす |
| Postprocessor 内に全ロジックを内包 | 1 ファイル完結 | ファイル数最小 | メニュー機能とロジック共有不可、テスト困難 | 不採用 |
| Unity の Texture2D 読み込みで解像度取得 | `AssetDatabase.LoadAssetAtPath<Texture2D>` の width/height を使う | パーサ不要 | インポート後の (maxTextureSize 適用済み) 解像度しか取れず、初回インポート前には使えない。1.1/1.3 に違反 | 不採用。ファイル実データの読み取りが必須 |

## Design Decisions

### Decision: 初回インポート判定は `importSettingsMissing` を踏襲
- **Context**: Requirement 2.6 (.meta が存在する場合は変更しない)
- **Alternatives Considered**:
  1. `File.Exists(assetPath + ".meta")` を直接確認
  2. `AssetImporter.importSettingsMissing` プロパティ
- **Selected Approach**: `importSettingsMissing` (既存 FbxImportSettingsPostprocessor と同一)
- **Rationale**: Unity 公式の初回インポート判定でありパッケージ内の既存流儀。ファイルシステム直接確認より Unity のインポートパイプラインと整合する
- **Trade-offs**: なし (両者は実質等価だが公式 API の方が堅牢)
- **Follow-up**: EditMode テストで .meta 有無による分岐を検証

### Decision: フォーマット判別は拡張子ディスパッチ + パーサ内構造検証
- **Context**: Requirement 1.2 / 1.4。TGA にはマジックバイトが無い
- **Alternatives Considered**:
  1. マジックバイトによる内容判別のみ
  2. 拡張子でパーサ選択し、各パーサが先頭構造を検証
- **Selected Approach**: 拡張子ディスパッチ + 構造検証の 2 段構え
- **Rationale**: TGA が内容判別不能。Unity 自体が拡張子でインポータを選ぶため、拡張子と中身が食い違うファイルは「壊れている」とみなして解像度不明に落とすのが要件 1.4 とも整合
- **Trade-offs**: 拡張子偽装ファイルは検出できないが、その場合 Unity のインポート自体も失敗するため実害なし
- **Follow-up**: 各パーサの破損データ検証をテストで固定

### Decision: 検証の適合判定は「最適値との完全一致」
- **Context**: Requirement 3.2 は「最適値と一致するか」を検証と定義。過大な maxTextureSize (例: 2048 テクスチャに 8192) は画質劣化しないが無駄設定
- **Alternatives Considered**:
  1. 最適値未満のみ問題視 (劣化のみ検出)
  2. 最適値と不一致なら過大・過小とも修正対象
- **Selected Approach**: 完全一致でない場合はすべて修正対象 (過大も過小も)
- **Rationale**: 要件文言に忠実。過大設定はプラットフォームによってはビルドサイズ・メモリを浪費するため是正価値がある。修正前に確認ダイアログを挟む (3.3) ため意図しない変更は防げる
- **Trade-offs**: 「意図的に小さくしている」アセットも検出されるが、確認ステップでユーザーが拒否できる
- **Follow-up**: 確認ダイアログとログに現在値→最適値を明示する

### Decision: 検証ロジックと UI の分離 (進捗コールバック注入)
- **Context**: Requirement 3.7 (進捗・キャンセル) と 5.3 (EditMode テスト可能) の両立
- **Alternatives Considered**:
  1. メニューハンドラに列挙・検証・ダイアログを一体実装
  2. `TextureMaxSizeValidator` (ロジック) と `TextureMaxSizeContextMenu` (UI) に分離し、進捗はデリゲートで注入
- **Selected Approach**: 分離 + デリゲート注入
- **Rationale**: `DisplayCancelableProgressBar` 等の UI API はテスト不能。デリゲート境界を切ることでロジック全体が EditMode テスト対象になる
- **Trade-offs**: 間接層が 1 つ増えるが、デリゲート 1 本で済む
- **Follow-up**: キャンセル時の部分完了レポート (検証済み件数・修正済み件数) の内容をテストで固定

## Risks & Mitigations
- TIFF の IFD がファイル末尾側にあるケースで「ヘッダ近傍のみ」の建前が崩れる — シークで読み取り量自体は数 KB に抑えられるため要件の趣旨 (ピクセルデコードなし・高速) は満たす。IFD エントリ数上限で破損ファイルを防御
- OnPreprocessTexture は全テクスチャインポートで発火し、他機能 (キャプチャ出力等) のアセット化にも波及し得る — 初回インポート (.meta なし) のみ、解像度取得成功時のみ変更、失敗時は Unity 既定動作という 3 重のガードで影響を限定 (2.6 / 2.7 / 4.4)
- 一括修正中の例外でプログレスバーが残留する — try/finally で `ClearProgressBar` と `StopAssetEditing` を必ず実行
- 大量アセット修正時の再インポート時間 — `StartAssetEditing` バッチ化 + キャンセル受付で緩和。検証フェーズはヘッダ読みのみのため高速

## References
- [TextureImporter.maxTextureSize — Unity Scripting API](https://docs.unity3d.com/ScriptReference/TextureImporter-maxTextureSize.html) — Default プラットフォーム設定のみに作用することの確認
- [AssetPostprocessor.OnPreprocessTexture — Unity Scripting API](https://docs.unity3d.com/ScriptReference/AssetPostprocessor.OnPreprocessTexture.html) — インポート前設定変更の公式パターン
- [Texture Import Settings — Unity Manual](https://docs.unity3d.com/Manual/class-TextureImporter.html) — Max Size 選択肢と既定値 2048 の確認
- 既存実装: `Editor/FbxImportSettingsPostprocessor.cs` / `Editor/FbxHeaderReader.cs` — 踏襲すべきパッケージ内流儀
