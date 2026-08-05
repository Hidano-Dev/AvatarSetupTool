# Requirements Document

## Project Description (Input)
モデルキャプチャ (ModelCaptureService) の 8K など高解像度撮影を低スペック環境でも確実・省メモリに書き出せるようにする改修。現状は SSAA×2 適用後のレンダサイズが GPU の maxTextureSize を超えたときのみタイル分割するため、8K 設定では 16384×16384 の単一 RenderTexture (約 3GB VRAM) を確保しにいき、確保失敗や TDR で黒い PNG が黙って書き出される。また読み戻しとエンコード経路で 8K 全面の Color32[] (256MB) + GetPixels32 の 1GB 配列 + Texture2D + EncodeToPNG の中間コピーが重なり CPU メモリのピークが 1.5〜2GB/枚に達し、GC で速度も劣化する。

改修内容:

(フェーズ1・確実性) タイル辺長の上限を maxTextureSize ではなく安全上限 (約4096px) と VRAM 予算から算出するよう TileCount/StillSuperSample を一般化し、8K+SSAA でも 4096px 級タイルの分割描画・合成にする (正射影なので合成結果は同一)。あわせて黒フレーム検出 (背景は必ずグレーのため全画素黒は異常) を追加し、検出時は 1 回リトライ、再失敗時はエラーとして報告し黙って黒 PNG を保存しない。VRAM 予算不足時に SSAA 倍率を 1 へ落とす既存動作はタイル分割で不要になるため見直す。

(フェーズ2・省メモリ) Texture2D+SetPixels32+EncodeToPNG を ImageConversion.EncodeArrayToPNG による RGB24 byte[] 直接エンコードへ置換して中間コピーを削減、または既存 PngMetadata の CRC32 を流用した行バンド単位のストリーミング PNG エンコーダを実装して 8K 全面バッファ自体を不要にする。メモリ見積もり (EstimateRequiredBytes/ValidateMemory) と時間見積もりの係数も新経路に合わせて更新する。

既存の出力仕様 (PNG 内容・ファイル名・iTXt メタデータ・GIF/MP4/ProRes 経路) は変えない。

対象ファイル: Packages/com.hidano.avatarsetuptool/Editor/ModelCaptureService.cs、PngMetadata.cs、CaptureSettings.cs

## Requirements
<!-- Will be generated in /kiro-spec-requirements phase -->
