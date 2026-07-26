using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Hidano.AvatarSetupTool.Editor
{
    /// <summary>
    /// モデルキャプチャの設定 UI。ロジックは <see cref="ModelCaptureService"/> にあり、
    /// このクラスは設定の編集・保存 (EditorPrefs) と、実行時のダイアログ /
    /// プログレスバー表示のみを担当する。
    /// </summary>
    public sealed class ModelCaptureWindow : EditorWindow
    {
        private const string SettingsPrefsKey = "Hidano.AvatarSetupTool.ModelCapture.Settings";
        private static readonly int[] SizePresets = { 256, 512, 1024, 2048, 4096, 8192 };

        // Popup / GenericMenu は "/" をサブメニュー区切りとして扱うため、ラベルに "/" を使わないこと
        private static readonly string[] FormatLabels =
            { "画像のみ (PNG)", "PNG + MP4", "PNG + GIF", "PNG + ProRes 422 (MOV)" };
        private static readonly string[] RotationLabels =
            { "5 秒で 1 周", "10 秒で 1 周", "20 秒で 1 周", "カスタム" };
        private static readonly string[] ViewModeLabels = { "全身のみ", "顔のみ", "全部" };

        [SerializeField] private GameObject target;
        [SerializeField] private CaptureSettings settings = new CaptureSettings();
        [SerializeField] private bool customSize;
        private Vector2 scroll;

        [MenuItem("Tools/Hidano/AvatarSetupTool/Model Capture")]
        public static void Open()
        {
            GetWindow<ModelCaptureWindow>("Model Capture");
        }

        [MenuItem("Assets/Avatar Setup Tool/Capture Model Images...", false, 1000)]
        private static void OpenFromAssets()
        {
            var window = GetWindow<ModelCaptureWindow>("Model Capture");
            if (Selection.activeGameObject != null)
            {
                window.target = Selection.activeGameObject;
            }
        }

        [MenuItem("Assets/Avatar Setup Tool/Capture Model Images...", true)]
        private static bool ValidateOpenFromAssets()
        {
            return Selection.activeGameObject != null;
        }

        private void OnEnable()
        {
            var json = EditorPrefs.GetString(SettingsPrefsKey, string.Empty);
            if (!string.IsNullOrEmpty(json))
            {
                try
                {
                    EditorJsonUtility.FromJsonOverwrite(json, settings);
                }
                catch (Exception)
                {
                    settings = new CaptureSettings();
                }
            }

            if (string.IsNullOrEmpty(settings.outputRoot))
            {
                settings.outputRoot = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            }

            customSize = Array.IndexOf(SizePresets, settings.imageSize) < 0;
        }

        private void OnDisable()
        {
            SaveSettings();
        }

        private void SaveSettings()
        {
            EditorPrefs.SetString(SettingsPrefsKey, EditorJsonUtility.ToJson(settings));
        }

        private void OnGUI()
        {
            scroll = EditorGUILayout.BeginScrollView(scroll);
            EditorGUI.BeginChangeCheck();

            EditorGUILayout.Space();
            target = (GameObject)EditorGUILayout.ObjectField(
                new GUIContent("撮影対象", "Project の FBX / Prefab、または Hierarchy 上の GameObject"),
                target, typeof(GameObject), true);

            EditorGUILayout.Space();
            settings.format = (CaptureOutputFormat)EditorGUILayout.Popup(
                "出力形式", (int)settings.format, FormatLabels);

            settings.viewMode = (CaptureViewMode)EditorGUILayout.Popup(
                new GUIContent("撮影範囲", "全身と顔アップのどちらの構図を撮影するか"),
                (int)settings.viewMode, ViewModeLabels);

            DrawResolution();
            DrawH264LimitWarning();

            if (settings.format == CaptureOutputFormat.Mp4 || settings.format == CaptureOutputFormat.ProRes422)
            {
                DrawRotationSpeed();
            }

            DrawFileName();
            DrawOutputRoot();

            settings.take = Mathf.Max(1, EditorGUILayout.IntField(
                new GUIContent("テイク番号", "<Take> に使われる連番。撮影が成功すると +1 されます"),
                settings.take));

            if (EditorGUI.EndChangeCheck())
            {
                SaveSettings();
            }

            DrawMemoryEstimate();

            EditorGUILayout.Space();
            var error = ValidationError();
            using (new EditorGUI.DisabledScope(error != null))
            {
                if (GUILayout.Button("撮影開始", GUILayout.Height(32f)))
                {
                    Run();
                    GUIUtility.ExitGUI();
                }
            }

            if (error != null)
            {
                EditorGUILayout.HelpBox(error, MessageType.Info);
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawResolution()
        {
            var presetIndex = customSize ? -1 : Array.IndexOf(SizePresets, settings.imageSize);
            var options = SizePresets.Select(size => new GUIContent($"{size} px"))
                .Append(new GUIContent("カスタム")).ToArray();
            var selected = EditorGUILayout.Popup(
                new GUIContent("解像度 (高さ)", "PNG の高さ。横に広いモデルは幅が自動で拡張されます (最大 8 倍)"),
                presetIndex < 0 ? SizePresets.Length : presetIndex, options);
            if (selected < SizePresets.Length)
            {
                customSize = false;
                settings.imageSize = SizePresets[selected];
            }
            else
            {
                customSize = true;
                EditorGUI.indentLevel++;
                settings.imageSize = EditorGUILayout.DelayedIntField(
                    $"カスタム ({CaptureSettings.MinImageSize}〜{CaptureSettings.MaxImageSize})",
                    settings.imageSize);
                if (settings.imageSize != settings.NormalizedImageSize)
                {
                    EditorGUILayout.LabelField(" ", $"→ {settings.NormalizedImageSize} px に丸めて撮影します (4 の倍数)");
                }

                EditorGUI.indentLevel--;
            }
        }

        /// <summary>
        /// MP4 選択時、動画解像度が H.264 エンコーダの上限を確実に超える設定なら警告する。
        /// 横に広いモデルで幅だけ超えるケースは実行前チェック (Service 側) が検出する。
        /// </summary>
        private void DrawH264LimitWarning()
        {
            if (settings.format != CaptureOutputFormat.Mp4)
            {
                return;
            }

            var videoSize = (long)(settings.NormalizedImageSize / ModelCaptureService.SuperSampleFactor);
            if (videoSize <= ModelCaptureService.H264MaxDimension
                && videoSize * videoSize <= ModelCaptureService.H264MaxPixels)
            {
                return;
            }

            EditorGUILayout.HelpBox(
                $"この解像度では動画が {videoSize}px 四方以上になり、MP4 (H.264) エンコーダの上限"
                + " (4096x2304 相当) を超えるため撮影に失敗します。"
                + "ProRes 422 (MOV) に切り替えるか、解像度を下げてください。",
                MessageType.Warning);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("ProRes 422 (MOV) に切り替える", GUILayout.Width(220f)))
                {
                    settings.format = CaptureOutputFormat.ProRes422;
                    GUI.FocusControl(null);
                }
            }
        }

        private void DrawRotationSpeed()
        {
            settings.rotationSpeed = (RotationSpeedPreset)EditorGUILayout.Popup(
                "回転速度", (int)settings.rotationSpeed, RotationLabels);
            if (settings.rotationSpeed == RotationSpeedPreset.Custom)
            {
                EditorGUI.indentLevel++;
                settings.customSecondsPerRotation = EditorGUILayout.FloatField(
                    new GUIContent("1 周にかける秒数 (1〜300)"), settings.customSecondsPerRotation);
                EditorGUI.indentLevel--;
            }
        }

        private void DrawFileName()
        {
            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                settings.fileNamePattern = EditorGUILayout.TextField("ファイル名", settings.fileNamePattern);
                if (GUILayout.Button("+ ワイルドカード", GUILayout.Width(110f)))
                {
                    var menu = new GenericMenu();
                    foreach (var (token, description) in CaptureFileName.Wildcards)
                    {
                        menu.AddItem(new GUIContent($"{token}  —  {description}"), false, () =>
                        {
                            settings.fileNamePattern += token;
                            SaveSettings();
                            Repaint();
                        });
                    }

                    menu.ShowAsContext();
                }
            }

            // 実際の保存時と同じ補完 (<View> や <Direction> の自動付与) を通した例を表示する
            var effectivePattern = ModelCaptureService.EffectivePattern(
                settings.fileNamePattern, multipleTargets: false, forStill: true,
                bothViews: settings.viewMode == CaptureViewMode.Both);
            var viewLabel = settings.viewMode == CaptureViewMode.FaceOnly ? "face" : "full";
            var preview = CaptureFileName.Resolve(
                effectivePattern, "Model", "Target", "01_front", viewLabel,
                settings.NormalizedImageSize, settings.NormalizedImageSize, DateTime.Now, settings.take);
            EditorGUILayout.LabelField(" ", $"例: {preview}.png", EditorStyles.miniLabel);
        }

        private void DrawOutputRoot()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                settings.outputRoot = EditorGUILayout.TextField("出力先", settings.outputRoot);
                if (GUILayout.Button("...", GUILayout.Width(30f)))
                {
                    var selected = EditorUtility.OpenFolderPanel("キャプチャの出力先を選択", settings.outputRoot, string.Empty);
                    if (!string.IsNullOrEmpty(selected))
                    {
                        settings.outputRoot = selected;
                        SaveSettings();
                        GUI.FocusControl(null);
                    }
                }
            }
        }

        private void DrawMemoryEstimate()
        {
            EditorGUILayout.Space();
            var required = ModelCaptureService.EstimateRequiredBytes(
                settings.NormalizedImageSize, settings.format, settings.viewMode);
            var budget = ModelCaptureService.MemoryBudgetBytes;
            var requiredMb = required / (1024 * 1024);
            if (required > budget)
            {
                EditorGUILayout.HelpBox(
                    $"推定メモリ使用量 約 {requiredMb} MB が上限の目安 ({budget / (1024 * 1024)} MB) を超えるため、"
                    + "撮影は実行前のチェックで中断されます。解像度を下げてください。",
                    MessageType.Warning);
            }
            else
            {
                var note = settings.format == CaptureOutputFormat.Gif
                    ? " GIF は全フレームを保持してから書き出すため、1 フレームずつ逐次エンコードする MP4 や ProRes より多めになります。"
                    : string.Empty;
                EditorGUILayout.HelpBox(
                    $"推定メモリ使用量: 約 {requiredMb} MB (正方形想定の概算。横に広いモデルでは増加し、実行前に再チェックされます)。{note}",
                    MessageType.Info);
            }
        }

        private string ValidationError()
        {
            if (target == null)
            {
                return "撮影対象を指定してください。";
            }

            if (string.IsNullOrEmpty(settings.outputRoot))
            {
                return "出力先フォルダを指定してください。";
            }

            if (!Directory.Exists(settings.outputRoot))
            {
                return $"出力先フォルダが存在しません: {settings.outputRoot}";
            }

            return null;
        }

        private void Run()
        {
            if (!ConfirmDiskSpace())
            {
                return;
            }

            try
            {
                var result = ModelCaptureService.Capture(target, settings,
                    (text, ratio) => EditorUtility.DisplayProgressBar("Capture Model Images", text, ratio));
                if (!result.Success)
                {
                    EditorUtility.DisplayDialog("Capture Model Images", result.Error, "OK");
                    return;
                }

                settings.take++;
                SaveSettings();
                EditorUtility.RevealInFinder(result.OutputDirectory);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                EditorUtility.DisplayDialog("Capture Model Images", $"撮影に失敗しました。\n{e.Message}", "OK");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        /// <summary>
        /// 出力先ドライブの空き容量が推定出力サイズに対して不足しそうなら確認ダイアログを出す。
        /// 続行が選ばれたか、チェック不要 (十分な空き / 判定不能) なら true。
        /// </summary>
        private bool ConfirmDiskSpace()
        {
            long available;
            try
            {
                var driveRoot = Path.GetPathRoot(Path.GetFullPath(settings.outputRoot));
                if (string.IsNullOrEmpty(driveRoot))
                {
                    return true;
                }

                available = new DriveInfo(driveRoot).AvailableFreeSpace;
            }
            catch (Exception)
            {
                return true; // ネットワークパスなどで空き容量を判定できない場合はチェックしない
            }

            var estimated = ModelCaptureService.EstimateOutputBytes(settings);
            const long ReserveBytes = 256L * 1024 * 1024; // 出力以外のための最低限の余裕
            if (estimated + ReserveBytes <= available)
            {
                return true;
            }

            return EditorUtility.DisplayDialog(
                "Capture Model Images",
                "出力先ドライブの空き容量が不足する可能性があります。\n"
                + $"推定出力サイズ: 約 {FormatSize(estimated)} (ターゲット 1 体・正方形想定の概算)\n"
                + $"空き容量: {FormatSize(available)}\n\n続行しますか?",
                "続行", "中止");
        }

        private static string FormatSize(long bytes)
        {
            return bytes >= 1L << 30
                ? $"{bytes / (double)(1L << 30):F1} GB"
                : $"{bytes / (1L << 20)} MB";
        }
    }
}
