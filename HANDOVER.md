# HANDOVER

セッション日: 2026-07-26〜27 / パッケージ: com.hidano.avatarsetuptool 0.5.0 → **0.8.0** (未 publish。ユーザー指示によりまとまるまで 0.8.0 に据え置き)

## 今回やったこと

- **v0.8.0 追補**: 時間見積もりを 8K 実機ベンチで再較正 (SSAA 描画系 90→50 Mpx/s、PNG 35→50 Mpx/s。8K PNG 推定 80 秒→実測 2.5 分の乖離を修正、再現ベンチで 107.8 秒実測 = 新推定 107.4 秒)。さらに撮影実測から EditorPrefs へ較正係数を平滑保存し、次回見積もりへ反映 (実アバターの描画コスト差を吸収)
- **v0.8.0 追補**: グリッド破線を 2cm→1cm 周期に細分化、細線 164→144・主線 128→96 に濃色化
- **v0.8.0 追補**: 「デバッグ情報を記録」追加。出力フォルダへ debug_info.md を書き出し (画像内への描画は要望により廃止)、同内容を各 PNG の iTXt "Comment" にも埋め込む。内容: Source (アセットパス) / Prefab の git 直近コミット / Unity プロジェクト名 / Unity バージョン + RP / git ブランチ + origin URL / 撮影設定 (解像度・SSAA・形式・範囲) / ターゲットごとの元 FBX ヘッダ (エクスポート日時・ツール・元ファイルのフルパス) + SHA-256 + 規模統計 (三角形・ボーン・BlendShape・マテリアル・シェーダ一覧) / Captured (日時・ユーザー@PC・ツールバージョン)。SHA-256 は Get-FileHash と一致確認済み。元 FBX は配下全 Renderer のメッシュのアセットを数える多数決方式で特定 (Avatar 起点の逆探から変更)。新規: `FbxHeaderReader.cs` (バイナリ/ASCII FBX ヘッダのみの軽量パーサ、実測数 ms)、`CaptureDebugInfo.cs` (収集 + git 呼び出し + md 書き出し)、`PngMetadata.cs` (iTXt 挿入 + CRC32)。(非公開) の実 FBX (Maya 2022) の一時インポートで多数決特定 (meshes 8/9) まで検証済み

- **v0.6.0**: ProRes 422 エンコーダを純 C# で自前実装 (`ProResWriter.cs`、SMPTE RDD 36 + QuickTime MOV muxer)。dotnet + ffmpeg round-trip で PSNR 64〜69dB を検証済み
- **v0.6.0**: メニューを `Tools/Hidano/AvatarSetupTool/Model Capture` へ移動。回転速度ラベルの「/」分割修正。「撮影範囲」(全身のみ/顔のみ/全部) ドロップダウン追加、`<View>` をワイルドカード一覧から削除 (両方撮影時のみ自動補完)
- **v0.7.0**: グリッド線のリニア色空間バグ修正 (頂点カラーが sRGB 変換されず 1m 主線が消えていた。間隔は元から正確)。H.264 上限 (4096×2304) の事前チェック。同名フォルダは " (1)" 連番で回避。ディスク空き容量チェック + 確認ダイアログ
- **v0.8.0**: PNG に SSAA (≤2048px は 4 倍、以上は 2 倍、テクスチャ上限超えは正射影タイル分割)。キャンセルボタン (DisplayCancelableProgressBar、中断時は書きかけ動画を削除)。8K+MP4 は撮影ボタン無効化 + ProRes 切替ボタン。10cm 線を破線化 (2cm 周期)。出力サイズ・時間の目安表示 (実測キャリブレーション済み)
- 検証: Unity 内でテスト用ヒューマノイドを動的生成して実撮影・ピクセル実測 (グリッド間隔/破線/AA 階調/フォルダ連番/ProRes・MP4 回帰)

## 決定事項

- ProRes は自前エンコーダ (外部ツール・Recorder パッケージ非依存)。固定品質 qScale=2、フラット量子化行列、エンコーダ ID "ast0"、MOV は 4GB 上限で明示エラー
- 動画ライターは `IVideoFrameWriter` (Mp4Writer/ProResWriter、ボトムアップ行順) で抽象化
- 静止画 AA は SSAA + 正射影タイル分割。倍率は VRAM (graphicsMemorySize の半分) とテクスチャ上限でクランプ
- 見積もり係数は実測の 1.3〜1.7 倍の安全率 (PNG 0.25B/px、MP4 0.1bit/px、ProRes 3bit/px、時間はスループット定数)
- 破線は周期をグリッド間隔の約数 (2cm) にして交点を必ず点の中心に揃える
- `CaptureOutputFormat` は EditorPrefs に int 保存のため既存 enum 順序は変更禁止 (ProRes422 は末尾追加)

## 捨てた選択肢と理由

- **MSAA RT への描画**: URP のプレビュー描画パスでは MSAA ターゲットに何も描画されない (真っ黒) ことを実測確認。`BeginStaticPreview` を経由しない自前 RT 描画は LightmapSettings 未初期化で **エディタごとクラッシュ**するため厳禁
- **SMAA/ポストプロセス AA**: cameraType=Game 偽装でのみ動作するが背景色が 184→161 に化けるため不採用
- **Unity Recorder パッケージの ProRes**: フレーム単位の公開 API がなく依存も増えるため不採用
- **UnityEditor.Media での ProRes**: UnityEditor.dll / MediaModule に ProRes 文字列なし (H.264/VP8 のみ) を確認済み
- **8K の MP4 出力**: H.264 規格上は Level 6 で可能だが Windows Media Foundation が 4096×2304 相当まで。対応せず ProRes へ誘導する UI にした

## ハマりどころ

- ProRes のスキャン順テーブルを最初誤っていた (index 19 以降)。ffmpeg に単一 DCT 係数パターンをエンコードさせてビットストリームから真のテーブルを実測して解決 (`ProResWriter.cs` の ProgressiveScan が正)
- uloop ディスパッチャは今も CLI_UPDATE_REQUIRED ループで全滅 → beta.45 ランナー直呼び (メモリ `uloop-dispatcher-workaround.md` 参照。execute-dynamic-code は `--code-file` 必須、数値リテラルの byte 引数は `(byte)255` 明示)
- 開発用 Unity は数回落ちた/閉じられた。`D:\UnityEditors\6000.3.19f1\Editor\Unity.exe -projectPath ...` で起動し、パイプ `\\.\pipe\uloop-UnityCliLoop-cfa18d2cc7ff7806` の出現を待つ
- ユーザーの (非公開) プロジェクト ((非公開プロジェクト)) が同時に開いていることがある。Unity プロセスはコマンドラインでプロジェクトを判別してから操作すること
- プレビュー RT は HDR float16 (R16G16B16A16_SFloat)。GetPixels 直読みはリニア値になる

## 学び

- PreviewRenderUtility + URP は「MSAA なし・ポストプロセスなし」が前提。AA は SSAA しか選択肢がない。正射影ならタイル分割描画が厳密に一致するのでテクスチャ上限を回避できる
- Sprites/Default の頂点カラーは色空間変換されない → リニア色空間では `Color.linear` 変換が必要
- MediaEncoder (High) のビットレートは解像度基準でコンテンツ依存が小さい → サイズ見積もりが決定的にできる
- ffmpeg を「リファレンスエンコーダ/デコーダ」にした相互検証は自前コーデック実装の強力な検証手段

## 次にやること

1. **(高) 0.8.0 の publish** (ユーザーが「まとまってから」と指示、タイミングは要確認) — ユーザーの (非公開) プロジェクトはレジストリ (npmjs, scope com.hidano) 経由参照のため、publish しないと修正が届かない
2. **(中) コミット** — 今回の変更は未コミット (ユーザー未依頼のため保留中)
3. (低) 実アバターでの 8K ProRes 実地確認 (時間目安 約 42 分/体。キャンセル可)
4. (低) ProRes の MOV 4GB 超え対応 (co64) は必要になったら

## 関連ファイル

- `AvatarSetupTool/Packages/com.hidano.avatarsetuptool/Editor/ProResWriter.cs` (新規: ProRes 422 エンコーダ + MOV muxer)
- `AvatarSetupTool/Packages/com.hidano.avatarsetuptool/Editor/ModelCaptureService.cs` (SSAA/タイル描画、破線グリッド、キャンセル、見積もり、H.264 検証、フォルダ連番)
- `AvatarSetupTool/Packages/com.hidano.avatarsetuptool/Editor/ModelCaptureWindow.cs` (撮影範囲、警告 2 段階、ボタン無効化、サイズ/時間表示、容量確認)
- `AvatarSetupTool/Packages/com.hidano.avatarsetuptool/Editor/CaptureSettings.cs` (ProRes422/CaptureViewMode 追加)
- `AvatarSetupTool/Packages/com.hidano.avatarsetuptool/Editor/CaptureFileName.cs` (`<View>` 一覧削除)
- `AvatarSetupTool/Packages/com.hidano.avatarsetuptool/Editor/Mp4Writer.cs` (IVideoFrameWriter)
- `AvatarSetupTool/Packages/com.hidano.avatarsetuptool/CHANGELOG.md` / `package.json` (0.8.0)
