# Requirements Document

## Project Description (Input)
テクスチャ画像アセットのインポート設定自動調整機能。追加されたテクスチャの実データをファイルパスから読み取り、実際の解像度に最適な Import 設定の最大サイズ (maxTextureSize) を自動調整する。主目的は 4K テクスチャが既定の 2048 で読み込まれて劣化するのを防ぐこと。インポート時の自動設定に加え、プロジェクトビューのファイルまたはフォルダを右クリックして、選択箇所以下の全テクスチャアセットに対して最大サイズが適切かを検証し、必要に応じて修正するメニュー機能も提供する。既存の FbxImportSettingsPostprocessor と同様に com.hidano.avatarsetuptool パッケージの Editor アセンブリに実装する。

## Requirements
<!-- Will be generated in /kiro-spec-requirements phase -->
