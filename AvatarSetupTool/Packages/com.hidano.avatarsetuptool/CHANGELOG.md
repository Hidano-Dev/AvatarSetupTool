# Changelog

All notable changes to this package will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.4.0] - 2026-07-26

### Added

- `FbxModelCaptureTool` にターンテーブル MP4 出力を追加し、デフォルトの動画形式に変更: モデルを 30fps・6 秒/周で滑らかに 1 回転させた H.264 MP4 (全身 / 顔アップの 2 本) を生成。エンコードは `UnityEditor.Media.MediaEncoder` によるエディタ内完結で、Play モードや ffmpeg 等の外部ツールは不要。回転中のズーム揺れを防ぐため、1 周分を包含する固定構図(回転軸中心・水平は外接円半径)で撮影
- 従来の GIF 出力用にメニュー「Capture Model Images (GIF)」を追加(既存の「Capture Model Images」は MP4 出力になる)

### Changed

- `FbxModelCaptureTool` の GIF 用縮小処理を MP4 と共用の `Downscale` に一般化(行順の上下反転を引数で切り替え)

## [0.3.1] - 2026-07-26

### Changed

- `FbxModelCaptureTool` の GIF 画質を改善: 量子化に Floyd–Steinberg ディザリングを導入してバンディングを低減し、解像度を 512px から 1024px に変更。GIF 用の再レンダリングをやめ、PNG 用の 2048px 描画をボックス平均で縮小(2× スーパーサンプリング)して共用するように変更

## [0.3.0] - 2026-07-25

### Added

- `FbxModelCaptureTool` に GIF アニメーション出力を追加: 8 方向のキャプチャを 2 秒間隔で繋いだ無限ループ GIF (全身 / 顔アップの 2 本) をモデルごとに生成。外部ツール不要の純 C# GIF89a エンコーダ `GifWriter` を同梱

### Changed

- `FbxModelCaptureTool` の出力ファイル名の方向名に番号プレフィックスを追加 (`01_front` 〜 `08_front_right`)。名前順で並べると正面から左向きに回転していく順序になるよう撮影順も変更

## [0.2.1] - 2026-07-24

### Fixed

- `FbxModelCaptureTool` のキャプチャが暗くなる問題を修正: 環境光を白(Color)に変更し、Directional Light を白・Intensity 1・カメラ正面からの照射に統一。補助ライトは無効化

## [0.2.0] - 2026-07-24

### Changed

- `FbxModelCaptureTool` のキャプチャ解像度を 1024px から 2048px に変更
- `FbxModelCaptureTool` の顔アップ構図を変更: 首(Neck)ジョイントを画像中心に置き、頭頂のメッシュが画像高さの 10% の余白に収まるようサイズを自動調整
- `FbxModelCaptureTool` の出力先を実行時のフォルダ選択ダイアログに変更。初期値はマイピクチャ(OS から実パスを取得)、前回選択したフォルダを EditorPrefs に記録して次回の初期表示に使用

## [0.1.2] - 2026-07-20

### Changed

- `FbxModelCaptureTool` を FBX に加えて Prefab にも対応
- `FbxModelCaptureTool` の撮影対象を「Avatar が設定された Animator」に変更し、複数ある場合はオブジェクト名ごとにすべて撮影するように変更。見つからない場合はダイアログで警告

## [0.1.1] - 2026-07-18

### Added

- FBX の初回インポート時に Mesh の Read/Write Enable と Humanoid リグを自動で有効化する `FbxImportSettingsPostprocessor` を追加
- Project ウィンドウの右クリックメニューから、FBX を 8 方向 × (全身 / 顔アップ) の計 16 枚の PNG(並行投影・白背景)としてキャプチャする `FbxModelCaptureTool` を追加

## [0.1.0] - 2026-07-09

### Added

- FBX インポート時に全 SkinnedMeshRenderer の Update When Offscreen を自動で有効化する `FbxSkinnedMeshPostprocessor` を追加
