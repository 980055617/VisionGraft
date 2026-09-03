using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    private const float RuntimeControlsDefaultCanvasWidth = 1000f;
    // 2026-08-28: Home / Bundle の 3 行目を足したので広げた（範囲 -220..+220）。
    // 以前は 300 で、スライダー +94・ボタン -12 / -100 で埋まっていた。
    // 物理サイズ（ControlsBarSizeMeters）も同じ比率で広げないとボタンが縮む。
    private const float RuntimeControlsDefaultCanvasHeight = 440f;
    private const float RuntimeSettingsDefaultCanvasWidth = 900f;
    // Scale 行を足したぶん縦に伸ばした（520 → 640）。SettingsPanelSizeMeters.y も
    // 同じ比で 0.5 → 0.615 にしてあるので、文字の見かけの大きさは変わらない。
    // 640 → 340。対象ごとの編集をモデル編集タブへ移して Title / Motion / Screen Dist の
    // 3 行だけになったため（2026-09-04）。SettingsPanelSizeMeters も同じ比で縮めてある。
    private const float RuntimeSettingsDefaultCanvasHeight = 340f;
    private const float RuntimeModelPickerDefaultCanvasWidth = 980f;
    private const float RuntimeModelPickerDefaultCanvasHeight = 660f;
    private const int RuntimeModelPickerEntriesPerPage = 6;
    // ピッカーのプレビューをパネル面より手前に出す量（メートル）。
    // world space canvas の z は等倍なので、これはそのまま実寸。
    private const float PreviewForwardOffsetMeters = 0.01f;

    private GameObject runtimeControlsRoot;
    private Text runtimePauseButtonText;
    private Text runtimeSettingsButtonText;
    private Text runtimeModeButtonText;
    private Text runtimeModelPickerButtonText;
    private GameObject runtimeSettingsRoot;
    private GameObject runtimeModelPickerRoot;
    private Slider runtimeProgressSlider;
    private Text runtimeProgressText;
    private Slider runtimeFovxSlider;
    private Text runtimeFovxValueText;
    private Slider runtimeScreenDistanceSlider;
    private Text runtimeScreenDistanceValueText;
    // 対象ごとの編集はモデル編集タブが持つ（UI.ModelEdit.partial.cs）。
    // Yaw スライダーと矢印の説明は掛け替わって消えた（2026-09-03）。
    private Slider runtimeTrackScaleSlider;
    private Text runtimeTrackScaleValueText;
    private Text runtimeTrackKeyInfoText;
    private Text runtimeInteractiveMotionValueText;
    private Text runtimeModelPickerStatusText;
    private Text runtimeModelPickerPageText;
    private Button runtimeModelPickerPrevButton;
    private Button runtimeModelPickerNextButton;
    private readonly List<Button> runtimeModelPickerEntryButtons = new List<Button>();
    private readonly List<GameObject> runtimeModelPickerPreviewInstances = new List<GameObject>();
    // プレビューの素のスケール。レイが乗ったセルだけ拡大して、外れたらここへ戻す。
    private readonly List<Vector3> runtimeModelPickerPreviewBaseScales = new List<Vector3>();
    private int runtimeModelPickerHoverIndex = -1;
    // track を 1 つずつ送るのではなく、出ている ID を全部並べて直接押せるようにするボタン列。
    private readonly List<Button> runtimeModelPickerTargetButtons = new List<Button>();
    // 「この track にはモデルを置かない」のトグル。
    private Button runtimeModelPickerHideButton;

    // 脇に出るパネル（Settings / モデルピッカー）の前後位置。掴み代のドラッグで動かす。
    // + が遠ざける方向。Settings とピッカーで共有する（片方だけ動くと揃わない）。
    private float runtimePanelDistanceOffsetMeters;
    // 手前の限界。実機で「もっと前まで持ってきたい」との要望で広げた（2026-09-02）。
    // 実際にどこまで寄るかは ApplyRuntimePanelDistanceOffset の最低距離で頭打ちになる。
    private const float RuntimePanelDistanceOffsetMin = -1.0f;
    private const float RuntimePanelDistanceOffsetMax = 1.0f;
    // 掴み代を掴んでいる間だけ true。掴んだ瞬間のコントローラ位置を控えておく。
    private bool runtimePanelDragActive;
    private Vector3 runtimePanelDragStartPointer;
    private float runtimePanelDragStartOffset;
    private float runtimePanelDragLoggedOffset;
    private float runtimePanelDragLoggedAt;
    // コントローラを 1m 前後させたときに動く距離。1.0 なら手の動きとパネルが 1:1。
    // 実機で「もう少し感度を上げたい」との要望で 2.5 倍に（2026-09-02）。
    // 腕を前後に伸ばせる範囲は 0.5m 程度なので、1:1 では端まで届かなかった。
    private const float RuntimePanelDragGain = 2.5f;
    private bool runtimeSettingsOpen;
    private bool batchSettingsForcedOpen;
    private bool batchModelPickerForcedOpen;
    private bool runtimeModelPickerOpen;
    private bool runtimeFovxInitialized;
    private bool suppressRuntimeProgressCallback;
    private bool suppressRuntimeScreenDistanceCallback;
    private bool suppressRuntimeTrackScaleCallback;
    private int runtimeSettingsPlacementLockDepth;
    private int runtimeModelPickerPageIndex;
    private int runtimeModelPickerTrackId = -1;
    private readonly List<InputDevice> xrInputDevices = new List<InputDevice>();
    private void EnsureRuntimeControls()
    {
        if (!enableRuntimeControls)
        {
            if (runtimeControlsRoot != null)
            {
                SceneObjectWriter.ApplyActive(runtimeControlsRoot, false);
            }
            if (runtimeSettingsRoot != null)
            {
                SceneObjectWriter.ApplyActive(runtimeSettingsRoot, false);
            }
            if (runtimeModelPickerRoot != null)
            {
                SceneObjectWriter.ApplyActive(runtimeModelPickerRoot, false);
            }
            SetScreenColliderBlockForSettings(false);
            return;
        }

        if (runtimeControlsRoot == null)
        {
            runtimeControlsRoot = BuildRuntimeControlsUi();
        }
        if (runtimeSettingsRoot == null)
        {
            runtimeSettingsRoot = BuildRuntimeSettingsUi();
        }
        if (runtimeModelPickerRoot == null)
        {
            runtimeModelPickerRoot = BuildRuntimeModelPickerUi();
        }

        if (runtimeControlsRoot != null)
        {
            SceneObjectWriter.ApplyActive(runtimeControlsRoot, true);
            ApplyRuntimeControlsSizing();
            UpdateRuntimeControlsPlacement();
            UpdatePauseButtonLabel();
            UpdateSettingsButtonLabel();
            UpdateRuntimeModeUiState();
            UpdateModelPickerButtonLabel();
            UpdateRuntimeProgressUi();
        }

        if (runtimeSettingsRoot != null)
        {
            if (batchOpenSettingsOnStart && !batchSettingsForcedOpen)
            {
                batchSettingsForcedOpen = true;
                runtimeSettingsOpen = true;
            }

            SceneObjectWriter.ApplyActive(runtimeSettingsRoot, runtimeSettingsOpen);
            SetScreenColliderBlockForRuntimePanels();
            UpdateRuntimeSettingsPlacement();
            UpdateRuntimeScreenDistanceUiState();
            UpdateRuntimeTrackRotationUiState();
            UpdateRuntimeInteractiveMotionUiState();
        }

        if (runtimeModelPickerRoot != null)
        {
            // **実機と同じ経路で開く。** フラグを直接立てると ToggleRuntimeModelPickerPanel が
            // やっている対象の解決・一時停止を飛ばしてしまい、バッチでしか起きない状態になる。
            //
            // 一度きりにしない理由: 再生が再開すると CloseEditPanelsForResume が閉じるので、
            // バッチでは開いた状態を保てない（実機では人が開くので問題にならない）。
            // 検証用に、閉じられたら開き直す。
            if (batchOpenModelPickerOnStart && !runtimeModelPickerOpen && metaLoaded)
            {
                batchModelPickerForcedOpen = true;
                if (!string.IsNullOrEmpty(batchModelPickerTab) && batchModelPickerTab == "edit")
                {
                    SetRuntimeModelPickerTab(ModelPickerTabEdit);
                }

                ToggleRuntimeModelPickerPanel();
                runtimeModelPickerPageIndex = Mathf.Max(0, batchModelPickerPage);
            }

            SceneObjectWriter.ApplyActive(runtimeModelPickerRoot, runtimeModelPickerOpen);
            SetScreenColliderBlockForRuntimePanels();
            UpdateRuntimeModelPickerPlacement();
            UpdateRuntimeModelPickerUiState();
        }

        // **両方のブロックの後で呼ぶ。** 設定ブロックの中に置いていたときは
        // ピッカーが開く前に 1 回走って終わり、パネルの方が一度も出なかった。
        DumpPanelLayoutIfRequested();
    }

    private void UnbindRuntimeControls()
    {
        if (runtimeControlsRoot != null)
        {
            Button pauseButton = FindButton(runtimeControlsRoot, "pause");
            if (pauseButton == null)
            {
                pauseButton = runtimeControlsRoot.GetComponentInChildren<Button>(true);
            }
            UnbindRuntimeButton(pauseButton, TogglePausePlayback);
            UnbindRuntimeButton(FindButton(runtimeControlsRoot, "setting"), ToggleRuntimeSettingsPanel);
            UnbindRuntimeButton(FindButton(runtimeControlsRoot, "mode"), ToggleNormalMode);
            UnbindRuntimeButton(FindButton(runtimeControlsRoot, "prefabselect"), ToggleRuntimeModelPickerPanel);
            UnbindRuntimeSlider(FindSlider(runtimeControlsRoot, "progressslider"), OnRuntimeProgressSliderChanged);
        }

        if (runtimeSettingsRoot != null)
        {
            UnbindRuntimeSlider(runtimeFovxSlider, OnRuntimeFovxSliderChanged);
            UnbindRuntimeSlider(runtimeScreenDistanceSlider, OnRuntimeScreenDistanceSliderChanged);
            UnbindRuntimeButton(FindButton(runtimeSettingsRoot, "interactivemotiontoggle"), OnRuntimeInteractiveMotionToggleClicked);
        }

        if (runtimeModelPickerRoot != null)
        {
            UnbindRuntimeModelPickerButtons();
        }
    }

}
