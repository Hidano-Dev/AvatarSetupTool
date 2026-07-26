# HANDOVER

## 今回やったこと

- `FbxModelCaptureTool` の出力ファイル名の方向名に番号プレフィックスを追加(`01_front` 〜 `08_front_right`)。名前順 = 正面から左向きに回転する順序。撮影順・GIF フレーム順も同じに変更
- GIF アニメーション出力を追加(0.3.0): 8 方向 × 2 秒間隔の無限ループ GIF を全身/顔アップの 2 本生成(`モデル名_full.gif` / `モデル名_face.gif`)
- 純 C# GIF89a エンコーダ `GifWriter.cs` を新規実装(外部ツール・ffmpeg 不要)。メディアンカット量子化 + LZW 圧縮
- GIF 画質改善(0.3.1): Floyd–Steinberg ディザリング導入、512px→1024px、GIF 用再レンダリング廃止(PNG 用 2048px 描画をボックス平均縮小 = 2× SSAA)
- バージョン 0.3.1 に更新。CHANGELOG を 0.3.0/0.3.1 に分割

## 決定事項

- GIF は ffmpeg 同梱ではなく自前エンコーダで実現(容量ゼロ増)
- 番号はファイル名の完全な先頭ではなくモデル名の後ろ(`モデル名_01_front_full.png`)。複数 Animator 時のグループ化維持のため
- 回転順は「正面 → front_left → left → back_left → back → back_right → right → front_right」(モデルが左を向いていく)
- 今回の画質改善はパッチ(0.3.1)。新機能・API 変更なしのため
- GIF サイズが大きすぎる場合は `GifImageSize`(現在 1024、ImageSize の約数必須)を下げる

## 捨てた選択肢と理由

- **ffmpeg ポータブル版の同梱**: 数十 MB の容量増。GIF89a は自前実装で十分だった
- **Unity Recorder 依存**: Editor 拡張パッケージには重い依存。GIF 対応も不確実
- **APNG 出力**: フルカラーで画質は最良だが、Windows エクスプローラーで動かない。ユーザーに提案したが今回は不採用(GIF 改善で対応)
- **GIF 用の低解像度別レンダリング**(初期実装): PNG 用 2048px 描画の縮小共用に変更。描画回数半減 + SSAA 効果

## ハマりどころ

- **uloop CLI が全コマンド失敗**: ディスパッチャ(beta.22)の自己更新が CLI_UPDATE_REQUIRED の無限ループ。回避 = `%LOCALAPPDATA%\uloop\versions\3.0.0-beta.45\windows-amd64\uloop-project-runner.exe` を直接呼ぶ。beta.57 はプロトコル 4 でこのプロジェクトのパッケージ(プロトコル 3)と不一致。**beta.45 を使う**(メモリにも記録済み)
- `uloop launch` はランナー非対応 → Unity 直接起動(`D:\UnityEditors\6000.3.19f1\Editor\Unity.exe -projectPath ...`)。起動完了はパイプ `\\.\pipe\uloop-UnityCliLoop-cfa18d2cc7ff7806` の出現で判定
- 別プロジェクトの Unity(OscSurface / (非公開プロジェクト))が起動中でも AvatarSetupTool のパイプは開かない(パイプ名はプロジェクト固有)。プロセス名だけで判断しない
- ユーザーが他プロジェクトで Unity 作業中の場合は `-batchmode -nographics -quit -logFile` でコンパイル確認する(GUI エディタを増やさない)
- `Texture2D.GetPixels32` は下端行始まり。GIF はトップダウンなので反転必須(DownscaleForGif 内で実施)

## 学び

- GIF89a は仕様が単純(256 色パレット + LZW)で、量子化込みでも約 450 行の純 C# で実装可能
- GIF の見た目品質はディザリングの有無が支配的。フラット色(トゥーンのベタ塗り・白背景)は誤差 0 でディザノイズが乗らないため、アニメ調モデルとの相性が良い
- GifWriter は UnityEngine 依存が Color32/Mathf だけなので、シム 2 つで .NET 単体テスト可能(scratchpad の giftest プロジェクト方式)。System.Drawing でデコード検証できる
- LZW のコード幅拡張タイミングは「emit 後、次コード割り当て前に nextCode == 1<<codeSize なら +1」(ppmtogif 方式)で GDI+ デコード互換を確認済み

## 次にやること

1. **[高] 実モデルでの動作確認**(ユーザー作業): 右クリック → Capture Model Images で PNG 16 枚 + GIF 2 本の出力、GIF の画質・ファイルサイズを目視確認。未実施
2. [中] 0.3.1 のコミット(ユーザーは自分でコミットする習慣。package.json と CHANGELOG.md が未コミット)
3. [低] GIF サイズが大きすぎたら `GifImageSize` 調整を検討

## 関連ファイル

- `AvatarSetupTool/Packages/com.hidano.avatarsetuptool/Editor/FbxModelCaptureTool.cs` — 撮影順・番号付け・GIF フレーム生成(CaptureShot / DownscaleForGif)
- `AvatarSetupTool/Packages/com.hidano.avatarsetuptool/Editor/GifWriter.cs` — GIF89a エンコーダ(新規、meta の GUID はランダム生成済み)
- `AvatarSetupTool/Packages/com.hidano.avatarsetuptool/CHANGELOG.md`
- `AvatarSetupTool/Packages/com.hidano.avatarsetuptool/package.json` — 0.3.1
- 検証ハーネス: scratchpad `giftest/`(セッション終了で消える。UnityShim.cs + Program.cs 方式は再現容易)
