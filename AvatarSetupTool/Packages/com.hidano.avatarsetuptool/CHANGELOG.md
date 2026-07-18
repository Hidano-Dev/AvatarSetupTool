# Changelog

All notable changes to this package will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- FBX の初回インポート時に Mesh の Read/Write Enable と Humanoid リグを自動で有効化する `FbxImportSettingsPostprocessor` を追加
- Project ウィンドウの右クリックメニューから、FBX を 8 方向 × (全身 / 顔アップ) の計 16 枚の PNG(並行投影・白背景)としてキャプチャする `FbxModelCaptureTool` を追加

## [0.1.0] - 2026-07-09

### Added

- FBX インポート時に全 SkinnedMeshRenderer の Update When Offscreen を自動で有効化する `FbxSkinnedMeshPostprocessor` を追加
