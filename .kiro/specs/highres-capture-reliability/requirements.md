# Requirements Document

## Project Description (Input)
モデルキャプチャ (ModelCaptureService) の 8K など高解像度撮影を低スペック環境でも確実・省メモリに書き出せるようにする改修。現状は SSAA×2 適用後のレンダサイズが GPU の maxTextureSize を超えたときのみタイル分割するため、8K 設定では 16384×16384 の単一 RenderTexture (約 3GB VRAM) を確保しにいき、確保失敗や TDR で黒い PNG が黙って書き出される。また読み戻しとエンコード経路で 8K 全面の Color32[] (256MB) + GetPixels32 の 1GB 配列 + Texture2D + EncodeToPNG の中間コピーが重なり CPU メモリのピークが 1.5〜2GB/枚に達し、GC で速度も劣化する。

改修内容:

(フェーズ1・確実性) タイル辺長の上限を maxTextureSize ではなく安全上限 (約4096px) と VRAM 予算から算出するよう TileCount/StillSuperSample を一般化し、8K+SSAA でも 4096px 級タイルの分割描画・合成にする (正射影なので合成結果は同一)。あわせて黒フレーム検出 (背景は必ずグレーのため全画素黒は異常) を追加し、検出時は 1 回リトライ、再失敗時はエラーとして報告し黙って黒 PNG を保存しない。VRAM 予算不足時に SSAA 倍率を 1 へ落とす既存動作はタイル分割で不要になるため見直す。

(フェーズ2・省メモリ) Texture2D+SetPixels32+EncodeToPNG を ImageConversion.EncodeArrayToPNG による RGB24 byte[] 直接エンコードへ置換して中間コピーを削減、または既存 PngMetadata の CRC32 を流用した行バンド単位のストリーミング PNG エンコーダを実装して 8K 全面バッファ自体を不要にする。メモリ見積もり (EstimateRequiredBytes/ValidateMemory) と時間見積もりの係数も新経路に合わせて更新する。

既存の出力仕様 (PNG 内容・ファイル名・iTXt メタデータ・GIF/MP4/ProRes 経路) は変えない。

対象ファイル: Packages/com.hidano.avatarsetuptool/Editor/ModelCaptureService.cs、PngMetadata.cs、CaptureSettings.cs

## Introduction
本仕様は、Unity エディタ拡張 AvatarSetupTool のモデルキャプチャ機能 (ModelCaptureService) における高解像度静止画キャプチャの信頼性向上と省メモリ化を対象とする。8K (PNG 高さ 8192px) など大サイズの静止画を、低 VRAM / 低 RAM 環境でも黒画像の黙殺的出力なしに確実に書き出せるようにし (フェーズ1)、CPU 側のピークメモリを削減して GC 起因の速度劣化を抑える (フェーズ2)。既存の出力仕様 (PNG のピクセル内容・ファイル名・iTXt メタデータ) と GIF/MP4/ProRes 経路、進捗表示・キャンセル動作は変更しない。

## Boundary Context
- **In scope**: 静止画 (PNG) キャプチャのレンダリング分割 (タイル辺長算出・合成)、SSAA 倍率決定ロジック、黒フレーム検出とリトライ・エラー報告、PNG エンコード経路の省メモリ化、メモリ/時間見積もり (EstimateRequiredBytes / ValidateMemory / 時間見積もり係数) の更新。対象ファイルは `Packages/com.hidano.avatarsetuptool/Editor/ModelCaptureService.cs`、`PngMetadata.cs`、`CaptureSettings.cs`。
- **Out of scope**: GIF/MP4/ProRes などの動画・アニメーション書き出し経路の変更、キャプチャ UI の刷新、カメラ設定・構図・背景色の変更、PNG 以外の静止画フォーマット追加。
- **Adjacent expectations**: CLI (`-executeMethod`) からの UI なし実行は従来どおり動作すること。PngMetadata による iTXt メタデータ付与の出力バイト仕様は維持されること。Unity Test Runner (EditMode) でのテスト実行が可能であること。

## Requirements

### Requirement 1: タイル分割レンダリングの一般化
**Objective:** キャプチャ機能の利用者として、8K などの高解像度静止画を GPU の VRAM 容量に依存せず撮影できるようにしたい。巨大な単一 RenderTexture の確保失敗や TDR による撮影失敗をなくすためである。

#### Acceptance Criteria
1. When 静止画のレンダリングを開始するとき, the ModelCaptureService shall タイル辺長を安全上限 (約 4096px) と VRAM 予算 (graphicsMemorySize に基づく) の両方から算出し、SSAA 適用後のレンダサイズを当該辺長以下のタイルへ分割して描画する
2. When SSAA 適用後のレンダサイズが算出されたタイル辺長以下であるとき, the ModelCaptureService shall タイル分割せず単一パスで描画する
3. The ModelCaptureService shall タイル数の決定において SSAA 倍率の約数などの制約に縛られず、任意の分割数でタイルの描画と合成を行える
4. When タイル分割で描画した結果を合成するとき, the ModelCaptureService shall 単一 RenderTexture で描画した場合と同一の合成結果を生成する (完全一致を目標とし、浮動小数点丸めに起因する各チャンネル ±1 階調以内の差のみ許容する)
5. The ModelCaptureService shall タイル 1 枚あたりの RenderTexture 確保サイズが maxTextureSize と算出したタイル辺長のいずれも超えないことを保証する

### Requirement 2: SSAA 画質の維持
**Objective:** キャプチャ機能の利用者として、低 VRAM 環境でも解像度設定に応じた SSAA 画質で撮影したい。環境差によって画質が黙って劣化することを避けるためである。

#### Acceptance Criteria
1. When 静止画の SSAA 倍率を決定するとき, the ModelCaptureService shall 解像度設定に応じた既定倍率 (2048px 以下は 4、それ超は 2) を適用する
2. While タイル分割によって単一タイルの VRAM 使用量が予算内に収まる, the ModelCaptureService shall VRAM 予算不足を理由に SSAA 倍率を 1 へ引き下げない
3. If タイル分割を適用しても最小構成のタイルが VRAM 予算に収まらない場合, then the ModelCaptureService shall SSAA 倍率の引き下げを行う前に警告をログへ出力し、実際に適用した倍率を利用者が確認できるようにする

### Requirement 3: 黒フレーム検出とリトライ
**Objective:** キャプチャ機能の利用者として、レンダリング失敗時に黒い PNG が黙って保存されるのではなく、失敗として通知されるようにしたい。撮影結果を目視確認せずとも失敗に気づけるようにするためである。

#### Acceptance Criteria
1. When 静止画 (タイルを含む) の読み戻しが完了したとき, the ModelCaptureService shall 全画素が黒であるか検査する (背景は常にグレー BackgroundColor(184,184,184) であるため、全画素黒は描画失敗とみなせる)
2. If 全画素黒のフレームを検出した場合, then the ModelCaptureService shall 当該フレームの描画を 1 回だけリトライする
3. If リトライ後も全画素黒のフレームを検出した場合, then the ModelCaptureService shall 当該キャプチャをエラーとして報告し、黒い PNG ファイルを保存しない
4. When 黒フレーム起因のエラーを報告するとき, the ModelCaptureService shall 失敗した解像度・タイル情報など原因究明に必要な情報をエラーメッセージに含める

### Requirement 4: 省メモリ PNG エンコード
**Objective:** キャプチャ機能の利用者として、8K 静止画の書き出し時に CPU メモリのピークを抑えたい。低 RAM 環境でのメモリ不足や GC による速度劣化を避けるためである。

#### Acceptance Criteria
1. The ModelCaptureService shall 静止画の PNG エンコードにおいて、Texture2D + SetPixels32 + EncodeToPNG による全面中間コピーを経由しない省メモリ経路 (RGB24 byte[] の直接エンコード、または行バンド単位のストリーミング PNG エンコード) を使用する
2. When 8K (高さ 8192px) の静止画を 1 枚書き出すとき, the ModelCaptureService shall キャプチャ処理全体の CPU 側ピークメモリを現行実装 (約 1.5〜2GB/枚) より削減する
3. Where 行バンド単位のストリーミング PNG エンコーダを採用する場合, the ModelCaptureService shall 画像全面分の中間バッファを確保せずに PNG を書き出す
4. When 省メモリ経路で PNG を書き出したとき, the ModelCaptureService shall 現行経路と同一のピクセル内容を持つ PNG を生成する
5. When 省メモリ経路で PNG を書き出したとき, the ModelCaptureService shall 現行と同一仕様の iTXt メタデータ (PngMetadata.WithText 相当) を付与する

### Requirement 5: メモリ・時間見積もりの更新
**Objective:** キャプチャ機能の利用者として、撮影前の必要メモリ検証と所要時間見積もりが新しい処理経路の実態に合っていてほしい。実際には撮影可能な設定が誤って拒否されたり、不正確な見積もりが表示されたりしないためである。

#### Acceptance Criteria
1. The ModelCaptureService shall EstimateRequiredBytes によるメモリ見積もりをタイル分割と省メモリエンコード経路の実際の使用量に基づいて算出する
2. When ValidateMemory が撮影可否を判定するとき, the ModelCaptureService shall 新経路の見積もり値を用いて判定し、新経路で撮影可能な設定を不足と誤判定しない
3. The ModelCaptureService shall 撮影所要時間の見積もり係数をタイル分割・新エンコード経路の処理時間に合わせて更新する

### Requirement 6: 既存出力仕様と実行環境の互換性維持
**Objective:** キャプチャ機能の利用者として、改修後も既存の出力ファイル仕様とワークフローがそのまま使えるようにしたい。後段ツールや運用スクリプトへの影響をなくすためである。

#### Acceptance Criteria
1. The ModelCaptureService shall 改修前後で PNG のピクセル内容・ファイル名規則・iTXt メタデータの仕様を変更しない
2. The ModelCaptureService shall GIF/MP4/ProRes の各書き出し経路の動作を変更しない
3. The ModelCaptureService shall 進捗表示およびキャンセル操作の既存動作を維持する (進捗コールバックの契約は不変のまま、タイル単位でのキャンセル判定追加による応答性向上は許容する)
4. When CLI (`-executeMethod`) から UI なしで実行されるとき, the ModelCaptureService shall エディタ UI に依存せずキャプチャ (黒フレーム検出・リトライ・エラー報告を含む) を完了する
5. If CLI 実行中にキャプチャがエラーとなった場合, then the ModelCaptureService shall 失敗をログおよび呼び出し元へ判別可能な形で報告する
