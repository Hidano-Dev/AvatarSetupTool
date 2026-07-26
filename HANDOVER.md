# HANDOVER

## 今回やったこと

- スクショツールを EditorWindow ベースに改良(0.5.0)。UI とロジックを分離
  - `ModelCaptureWindow.cs`(新規): 設定 UI。EditorPrefs へ JSON で設定を永続化
  - `ModelCaptureService.cs`(`FbxModelCaptureTool.cs` を git mv、GUID 維持): UI 非依存ロジック。`Capture(GameObject, CaptureSettings, progress)` 公開で将来 CLI (-executeMethod) から呼べる
  - `CaptureSettings.cs`(新規): 純粋データ。`CaptureFileName.cs`(新規): ワイルドカード解決
- 撮影対象を ObjectField 指定に変更: FBX / Prefab に加え Hierarchy 上の GameObject(編集状態を Instantiate で複製、Camera/Light 無効化、lossyScale 維持)
- 解像度 256〜8192px(高さ基準、4 の倍数丸め)。プリセット + カスタム。実行前に maxTextureSize と推定メモリ(実装メモリの半分が上限)を検証し、超過時は撮影せず警告
- 背景を白 → グレー(RGB 184)+ グリッド(主線 1m / 細線 10cm、y=0 は主線)
- MP4 回転速度を選択式に: 5/10/20 秒 + カスタム 1〜300 秒。デフォルト 10 秒/周(従来 6 秒)
- ファイル名ワイルドカード(Recorder 風): `<Model> <Target> <Direction> <View> <Resolution> <Date> <Time> <Take>`。衝突するパターンにはトークン自動補完。Take は成功ごとに +1
- 右クリックメニュー 3 項目 → 「Capture Model Images...」1 項目に統合(ウィンドウを開いて対象セット)。`Window > Avatar Setup Tool > Model Capture` も追加
- batchmode コンパイル 2 回でエラー・警告ゼロを確認。未コミット

## 決定事項

- ロジック層はダイアログ・プログレスバー禁止。進捗は `Action<string, float>`、結果は `CaptureResult` で返す
- グリッドはテクスチャでなく頂点カラー付きメッシュ(細線→主線の順に追加、ZWrite Off の Sprites/Default で後勝ち描画)。8K でもメモリ増なし・線がクリスプ
- カメラ固定・モデル回転方式は維持 → グリッドは回転中も静止
- メモリ検証は実行時に全ターゲットの構図確定後に実施(アスペクト比が事前に不明のため)。UI 表示は正方形仮定の概算
- 回転速度は MP4 のみ。GIF は従来どおり 8 方向 × 2 秒固定
- 複数選択の一括撮影は廃止(ウィンドウは単一ターゲット)
- 設定は EditorPrefs キー `Hidano.AvatarSetupTool.ModelCapture.Settings` に EditorJsonUtility で保存

## 捨てた選択肢と理由

- **グリッドをフル解像度テクスチャで生成**: 16384px 幅で 1GB 級のメモリ消費。メッシュ方式なら数百クアッドで済む
- **グリッドをタイルテクスチャ + リピート**: 拡大時に線がぼける
- **キャプチャ後の CPU 合成でグリッド描画**: モデルと背景の分離が不確実(モデルに背景色が含まれると誤爆)
- **カスタムシェーダーでグリッド描画**: URP / BiRP 両対応の手間。Sprites/Default(頂点カラー・アンリット・URP 動作)で十分
- **kiro spec 化**: ユーザーが直接実装を指示しており、過去セッションも直接実装の実績。spec は使わず

## ハマりどころ

- `EditorGUILayout.Popup(GUIContent, int, string[])` オーバーロードは存在しない → GUIContent[] に変換して使う
- git mv した直後の Write は先に Read が必要(ツール制約)
- batchmode コンパイル中にソース編集すると反映が不確実 → 2 回目を実行して確認した
- AvatarSetupTool の uloop パイプは `uloop-UnityCliLoop-cfa18d2cc7ff7806`。今回開いていたパイプ `ba9f03c5bd80ba3e` は別プロジェクトのもの。パイプ名で判別する
- ユーザーが他プロジェクトで Unity 作業中 → GUI エディタは起動せず `-batchmode -nographics -quit -logFile` でコンパイル確認(前回からの引き継ぎ、有効だった)

## 学び

- `PreviewRenderUtility.AddSingleGO` でシーン上の GameObject の複製をプレビューシーンに入れられる(プレハブ以外も撮影可能)
- `Object.Instantiate` は親から外れるので lossyScale の引き継ぎが必要
- 並行投影 + カメラ固定なら、ワールド XY 平面のメッシュを奥に置くだけで画面座標に一致した背景グリッドになる
- Windows ではファイル名に `<` `>` が使えないため、未知トークンは Sanitize で自然に `_` へ潰れる

## 次にやること

1. **[高] 実モデルでの動作確認**(ユーザー作業): ウィンドウから PNG / MP4 / GIF を出力し、グリッドの見た目(色・太さ)、Hierarchy オブジェクト撮影、8K などの大解像度時の警告動作を目視確認。未実施
2. [中] 0.5.0 のコミット(ユーザーは自分でコミットする習慣。全変更が未コミット)
3. [低] グリッドの色・線幅の調整(`ModelCaptureService` の `BackgroundColor` / `SubLineColor` / `MainLineColor` / `CreateGridObject` 内の 3px / 1.5px)
4. [低] GIF にも回転速度(フレーム間隔)設定を適用するか検討(現状 2 秒固定)

## 関連ファイル

- `AvatarSetupTool/Packages/com.hidano.avatarsetuptool/Editor/ModelCaptureWindow.cs` — 設定 UI(新規)
- `AvatarSetupTool/Packages/com.hidano.avatarsetuptool/Editor/ModelCaptureService.cs` — 撮影ロジック(旧 FbxModelCaptureTool.cs)
- `AvatarSetupTool/Packages/com.hidano.avatarsetuptool/Editor/CaptureSettings.cs` — 設定データ(新規)
- `AvatarSetupTool/Packages/com.hidano.avatarsetuptool/Editor/CaptureFileName.cs` — ワイルドカード解決(新規)
- `AvatarSetupTool/Packages/com.hidano.avatarsetuptool/CHANGELOG.md` — 0.5.0 追記
- `AvatarSetupTool/Packages/com.hidano.avatarsetuptool/package.json` — 0.5.0
- コンパイルログ: scratchpad `compile.log` / `compile2.log`(セッション終了で消える)
