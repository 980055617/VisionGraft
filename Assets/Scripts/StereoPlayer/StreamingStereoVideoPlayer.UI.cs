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
    private const float RuntimeSettingsDefaultCanvasHeight = 520f;
    private const float RuntimeModelPickerDefaultCanvasWidth = 980f;
    private const float RuntimeModelPickerDefaultCanvasHeight = 660f;
    private const int RuntimeModelPickerEntriesPerPage = 6;

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
    private Text runtimeTrackSelectionText;
    private Slider runtimeTrackYawSlider;
    private Text runtimeTrackYawValueText;
    private Text runtimeTrackFrontGuideText;
    private Text runtimeTrackKeyInfoText;
    private Text runtimeInteractiveMotionValueText;
    private Text runtimeModelPickerTitleText;
    private Text runtimeModelPickerStatusText;
    private Text runtimeModelPickerPageText;
    private Button runtimeModelPickerPrevButton;
    private Button runtimeModelPickerNextButton;
    private readonly List<Button> runtimeModelPickerEntryButtons = new List<Button>();
    private readonly List<GameObject> runtimeModelPickerPreviewInstances = new List<GameObject>();
    private bool runtimeSettingsOpen;
    private bool runtimeModelPickerOpen;
    private bool runtimeFovxInitialized;
    private bool suppressRuntimeProgressCallback;
    private bool suppressRuntimeScreenDistanceCallback;
    private bool suppressRuntimeTrackYawCallback;
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
            SceneObjectWriter.ApplyActive(runtimeSettingsRoot, runtimeSettingsOpen);
            SetScreenColliderBlockForRuntimePanels();
            UpdateRuntimeSettingsPlacement();
            UpdateRuntimeScreenDistanceUiState();
            UpdateRuntimeTrackRotationUiState();
            UpdateRuntimeInteractiveMotionUiState();
        }

        if (runtimeModelPickerRoot != null)
        {
            SceneObjectWriter.ApplyActive(runtimeModelPickerRoot, runtimeModelPickerOpen);
            SetScreenColliderBlockForRuntimePanels();
            UpdateRuntimeModelPickerPlacement();
            UpdateRuntimeModelPickerUiState();
        }
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
            UnbindRuntimeSlider(runtimeTrackYawSlider, OnRuntimeTrackYawSliderChanged);
            UnbindRuntimeButton(FindButton(runtimeSettingsRoot, "trackprev"), OnRuntimeTrackPrevClicked);
            UnbindRuntimeButton(FindButton(runtimeSettingsRoot, "tracknext"), OnRuntimeTrackNextClicked);
            UnbindRuntimeButton(FindButton(runtimeSettingsRoot, "trackyawreset"), OnRuntimeTrackYawResetClicked);
            UnbindRuntimeButton(FindButton(runtimeSettingsRoot, "interactivemotiontoggle"), OnRuntimeInteractiveMotionToggleClicked);
        }

        if (runtimeModelPickerRoot != null)
        {
            UnbindRuntimeModelPickerButtons();
        }
    }

}
