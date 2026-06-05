using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    private const float RuntimeControlsDefaultCanvasWidth = 1000f;
    private const float RuntimeControlsDefaultCanvasHeight = 200f;
    private const float RuntimeSettingsDefaultCanvasWidth = 900f;
    private const float RuntimeSettingsDefaultCanvasHeight = 520f;

    private GameObject runtimeControlsRoot;
    private Text runtimePauseButtonText;
    private Text runtimeSettingsButtonText;
    private GameObject runtimeSettingsRoot;
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
    private bool runtimeSettingsOpen;
    private bool runtimeFovxInitialized;
    private bool suppressRuntimeProgressCallback;
    private bool suppressRuntimeScreenDistanceCallback;
    private bool suppressRuntimeTrackYawCallback;
    private int runtimeSettingsPlacementLockDepth;
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

        if (runtimeControlsRoot != null)
        {
            SceneObjectWriter.ApplyActive(runtimeControlsRoot, true);
            ApplyRuntimeControlsSizing();
            UpdateRuntimeControlsPlacement();
            UpdatePauseButtonLabel();
            UpdateSettingsButtonLabel();
            UpdateRuntimeProgressUi();
        }

        if (runtimeSettingsRoot != null)
        {
            SceneObjectWriter.ApplyActive(runtimeSettingsRoot, runtimeSettingsOpen);
            SetScreenColliderBlockForSettings(runtimeSettingsOpen);
            UpdateRuntimeSettingsPlacement();
            UpdateRuntimeScreenDistanceUiState();
            UpdateRuntimeTrackRotationUiState();
            UpdateRuntimeInteractiveMotionUiState();
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
    }

}
