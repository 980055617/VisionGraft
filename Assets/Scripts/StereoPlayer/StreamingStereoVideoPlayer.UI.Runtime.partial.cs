using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;
using UnityEngine.XR;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{

    private void UpdateRuntimeControlsPlacement()
    {
        if (runtimeControlsRoot == null || !enableRuntimeControls)
        {
            return;
        }

        Transform basis = leftScreen != null ? leftScreen : rightScreen;
        if (basis == null)
        {
            return;
        }

        Vector3 center = basis.position;
        if (leftScreen != null && rightScreen != null)
        {
            center = (leftScreen.position + rightScreen.position) * 0.5f;
        }

        Transform head = GetViewOrHeadTransform();
        GetScreenSizeMeters(basis, out _, out float screenHeightMeters, out _);
        RuntimeControlsPlacement.Pose pose = RuntimeControlsPlacement.ResolveBarPose(
            center,
            basis.forward,
            basis.right,
            basis.up,
            basis.rotation,
            head != null,
            head != null ? head.position : Vector3.zero,
            screenHeightMeters,
            ControlsBarSizeMeters,
            ControlsBarGapMeters,
            ControlsBarOffsetMeters,
            ControlsBarForwardOffsetMeters);
        TransformWriter.ApplyPose(runtimeControlsRoot.transform, pose.position, pose.rotation);
        ApplyRuntimeControlsSizing();

        Canvas canvas = GetRuntimeControlsCanvas();
        if (canvas != null)
        {
            UiComponentWriter.ApplyWorldCamera(canvas, GetViewCamera());
        }

        UpdateRuntimeSettingsPlacement();
        UpdateRuntimeModelPickerPlacement();
    }



    private void UpdateRuntimeSettingsPlacement()
    {
        if (runtimeSettingsRoot == null || !enableRuntimeControls || !runtimeSettingsOpen)
        {
            return;
        }

        if (runtimeSettingsPlacementLockDepth > 0)
        {
            return;
        }

        // Place the settings panel to the right of the video screen and slightly in front.
        Transform baseScreen = rightScreen != null ? rightScreen : leftScreen;
        Transform basis = baseScreen != null
            ? baseScreen
            : (runtimeControlsRoot != null ? runtimeControlsRoot.transform : null);
        if (basis == null)
        {
            return;
        }

        Transform head = GetViewOrHeadTransform();
        float basisWidth = ControlsBarSizeMeters.x;
        if (baseScreen != null)
        {
            GetScreenSizeMeters(baseScreen, out float screenWidthMeters, out _, out _);
            basisWidth = Mathf.Abs(screenWidthMeters);
        }

        RuntimeControlsPlacement.Pose pose = RuntimeControlsPlacement.ResolveSettingsPose(
            basis.position,
            basis.forward,
            basis.right,
            basis.up,
            basis.rotation,
            head != null,
            head != null ? head.position : Vector3.zero,
            basisWidth,
            SettingsPanelSizeMeters,
            SettingsPanelGapMeters,
            SettingsPanelOffsetMeters,
            SettingsPanelForwardOffsetMeters);
        TransformWriter.ApplyPose(runtimeSettingsRoot.transform, pose.position, pose.rotation);

        Canvas canvas = runtimeSettingsRoot.GetComponent<Canvas>();
        if (canvas != null)
        {
            UiComponentWriter.ApplyWorldCamera(canvas, GetViewCamera());
        }

        RectTransform rect = runtimeSettingsRoot.GetComponent<RectTransform>();
        if (rect != null)
        {
            Vector2 size = rect.sizeDelta;
            if (size.x <= 0f || size.y <= 0f)
            {
                size = new Vector2(RuntimeSettingsDefaultCanvasWidth, RuntimeSettingsDefaultCanvasHeight);
                TransformWriter.ApplySizeDelta(rect, size);
            }

            TransformWriter.ApplyLocalScale(
                rect,
                new Vector3(
                    SettingsPanelSizeMeters.x / size.x,
                    SettingsPanelSizeMeters.y / size.y,
                    1f));
        }
    }



    private void PlaceScreensWithoutMovingSettings()
    {
        runtimeSettingsPlacementLockDepth++;
        try
        {
            PlaceScreens();
        }
        finally
        {
            runtimeSettingsPlacementLockDepth = Mathf.Max(0, runtimeSettingsPlacementLockDepth - 1);
        }
    }



    private Canvas GetRuntimeControlsCanvas()
    {
        if (runtimeControlsRoot == null)
        {
            return null;
        }

        Canvas canvas = runtimeControlsRoot.GetComponent<Canvas>();
        if (canvas != null)
        {
            return canvas;
        }

        return runtimeControlsRoot.GetComponentInChildren<Canvas>(true);
    }



    private void ApplyRuntimeControlsSizing()
    {
        Canvas canvas = GetRuntimeControlsCanvas();
        if (canvas == null)
        {
            return;
        }

        RectTransform rect = canvas.GetComponent<RectTransform>();
        if (rect == null)
        {
            return;
        }

        Vector2 size = rect.sizeDelta;
        if (size.x <= 0f || size.y <= 0f)
        {
            size = new Vector2(RuntimeControlsDefaultCanvasWidth, RuntimeControlsDefaultCanvasHeight);
            TransformWriter.ApplySizeDelta(rect, size);
        }
        else if (size.y < RuntimeControlsDefaultCanvasHeight)
        {
            size = new Vector2(Mathf.Max(size.x, RuntimeControlsDefaultCanvasWidth), RuntimeControlsDefaultCanvasHeight);
            TransformWriter.ApplySizeDelta(rect, size);
        }

        TransformWriter.ApplyLocalScale(
            rect,
            new Vector3(
                ControlsBarSizeMeters.x / size.x,
                ControlsBarSizeMeters.y / size.y,
                1f));
    }



    private void OnRuntimeTrackPrevClicked()
    {
        if (isNormalMode)
        {
            return;
        }

        PauseForManualRotationEdit();
        StepSelectedManualRotationTrack(-1);
        UpdateRuntimeTrackRotationUiState();
    }



    private void OnRuntimeTrackNextClicked()
    {
        if (isNormalMode)
        {
            return;
        }

        PauseForManualRotationEdit();
        StepSelectedManualRotationTrack(1);
        UpdateRuntimeTrackRotationUiState();
    }



    private void OnRuntimeTrackYawResetClicked()
    {
        if (isNormalMode || !TryGetSelectedManualRotationTrack(out uint trackId))
        {
            return;
        }

        PauseForManualRotationEdit();
        SetManualYawOffsetDegForTrack(trackId, 0f);
        UpdateRuntimeTrackRotationUiState();
    }



    private void OnRuntimeTrackYawSliderChanged(float value)
    {
        if (suppressRuntimeTrackYawCallback || isNormalMode)
        {
            return;
        }

        if (!TryGetSelectedManualRotationTrack(out uint trackId))
        {
            return;
        }

        PauseForManualRotationEdit();
        SetManualYawOffsetDegForTrack(trackId, value);
        UpdateRuntimeTrackRotationUiState();
    }



    private void OnRuntimeInteractiveMotionToggleClicked()
    {
        if (isNormalMode)
        {
            return;
        }

        enableInteractiveMotion = !enableInteractiveMotion;
        if (!enableInteractiveMotion)
        {
            StopAllInteractiveMotion();
        }
        UpdateRuntimeInteractiveMotionUiState();
    }



    private void UpdateRuntimeInteractiveMotionUiState()
    {
        Button motionToggle = FindButton(runtimeSettingsRoot, "interactivemotiontoggle");
        if (motionToggle != null)
        {
            UiComponentWriter.ApplyInteractable(motionToggle, !isNormalMode);
        }

        if (runtimeInteractiveMotionValueText == null)
        {
            return;
        }

        UiComponentWriter.ApplyTextContent(runtimeInteractiveMotionValueText, enableInteractiveMotion ? "ON" : "OFF");
    }



    private void UpdateRuntimeModeUiState()
    {
        Button modeButton = FindButton(runtimeControlsRoot, "mode");
        if (modeButton != null)
        {
            UiComponentWriter.ApplyInteractable(modeButton, hasNormalModeVideo);
        }

        if (runtimeModeButtonText != null)
        {
            UiComponentWriter.ApplyTextContent(runtimeModeButtonText, "Display");
        }

        UpdateRuntimeTrackRotationUiState();
        UpdateRuntimeInteractiveMotionUiState();
    }



    private void UpdateRuntimeTrackRotationUiState()
    {
        Button trackPrevButton = FindButton(runtimeSettingsRoot, "trackprev");
        if (trackPrevButton != null)
        {
            UiComponentWriter.ApplyInteractable(trackPrevButton, !isNormalMode);
        }

        Button trackNextButton = FindButton(runtimeSettingsRoot, "tracknext");
        if (trackNextButton != null)
        {
            UiComponentWriter.ApplyInteractable(trackNextButton, !isNormalMode);
        }

        Button trackYawResetButton = FindButton(runtimeSettingsRoot, "trackyawreset");
        if (trackYawResetButton != null)
        {
            UiComponentWriter.ApplyInteractable(trackYawResetButton, !isNormalMode);
        }

        if (runtimeTrackSelectionText == null && runtimeTrackYawSlider == null && runtimeTrackYawValueText == null &&
            runtimeTrackFrontGuideText == null && runtimeTrackKeyInfoText == null)
        {
            return;
        }

        if (isNormalMode)
        {
            if (runtimeTrackYawSlider != null)
            {
                UiComponentWriter.ApplyInteractable(runtimeTrackYawSlider, false);
            }
            return;
        }

        EnsureSelectedManualRotationTrack();

        if (!TryGetSelectedManualRotationTrack(out uint trackId))
        {
            if (runtimeTrackSelectionText != null)
            {
                UiComponentWriter.ApplyTextContent(runtimeTrackSelectionText, "none");
            }
            if (runtimeTrackYawValueText != null)
            {
                UiComponentWriter.ApplyTextContent(runtimeTrackYawValueText, "0.0 deg");
            }
            if (runtimeTrackYawSlider != null)
            {
                suppressRuntimeTrackYawCallback = true;
                UiComponentWriter.ApplySliderValueWithoutNotify(runtimeTrackYawSlider, 0f);
                suppressRuntimeTrackYawCallback = false;
                UiComponentWriter.ApplyInteractable(runtimeTrackYawSlider, false);
            }
            if (runtimeTrackFrontGuideText != null)
            {
                UiComponentWriter.ApplyTextContent(runtimeTrackFrontGuideText, "Arrow above head = FRONT  |  +:left  -:right");
            }
            if (runtimeTrackKeyInfoText != null)
            {
                UiComponentWriter.ApplyTextContent(runtimeTrackKeyInfoText, "Keys:0  Frame:0");
            }
            return;
        }

        int keyCount = GetManualYawKeyCountForTrack(trackId);
        bool hasKeyAtCurrent = HasManualYawKeyAtCurrentFrame(trackId);
        int frame = GetCurrentPlaybackFrame();
        float yaw = GetManualYawOffsetDegForTrack(trackId);
        if (runtimeTrackSelectionText != null)
        {
            UiComponentWriter.ApplyTextContent(runtimeTrackSelectionText, trackId.ToString());
        }
        if (runtimeTrackYawValueText != null)
        {
            UiComponentWriter.ApplyTextContent(runtimeTrackYawValueText, yaw.ToString("F1") + " deg");
        }
        if (runtimeTrackYawSlider != null)
        {
            UiComponentWriter.ApplyInteractable(runtimeTrackYawSlider, true);
            suppressRuntimeTrackYawCallback = true;
            UiComponentWriter.ApplySliderValueWithoutNotify(runtimeTrackYawSlider, yaw);
            suppressRuntimeTrackYawCallback = false;
        }
        if (runtimeTrackFrontGuideText != null)
        {
            UiComponentWriter.ApplyTextContent(runtimeTrackFrontGuideText, "Arrow above head = FRONT  |  +:left  -:right");
        }
        if (runtimeTrackKeyInfoText != null)
        {
            UiComponentWriter.ApplyTextContent(runtimeTrackKeyInfoText, "Keys:" + keyCount + "  Frame:" + frame + (hasKeyAtCurrent ? " [key]" : " [interp]"));
        }
    }



    private void UpdateRuntimeScreenDistanceUiState()
    {
        if (runtimeScreenDistanceSlider == null && runtimeScreenDistanceValueText == null)
        {
            return;
        }

        float clamped = ClampRuntimeScreenDistance(screenDistanceMeters);
        if (!Mathf.Approximately(screenDistanceMeters, clamped))
        {
            screenDistanceMeters = clamped;
        }

        if (runtimeScreenDistanceSlider != null)
        {
            UpdateRuntimeScreenDistanceSliderRange();
            suppressRuntimeScreenDistanceCallback = true;
            UiComponentWriter.ApplySliderValueWithoutNotify(runtimeScreenDistanceSlider, clamped);
            suppressRuntimeScreenDistanceCallback = false;
        }

        UpdateRuntimeScreenDistanceText(clamped);
    }



    private void PauseForManualRotationEdit()
    {
        RuntimePlaybackController.Command command = RuntimePlaybackController.ResolvePauseForEditCommand(
            vp != null,
            vp != null && vp.isPlaying);
        if (command == RuntimePlaybackController.Command.None)
        {
            return;
        }

        RuntimePlaybackController.Apply(vp, command);
        UpdatePauseButtonLabel();
    }



    private void RefreshRuntimeSettingsPerFrame()
    {
        if (!runtimeSettingsOpen)
        {
            UpdateManualYawGuide(false);
            return;
        }

        UpdateRuntimeScreenDistanceUiState();
        UpdateRuntimeTrackRotationUiState();
        UpdateRuntimeInteractiveMotionUiState();
        UpdateManualYawGuide(true);
    }



    private void UpdateRuntimeProgressUi()
    {
        if (runtimeProgressSlider == null && runtimeControlsRoot != null)
        {
            runtimeProgressSlider = FindSlider(runtimeControlsRoot, "progressslider");
            if (runtimeProgressSlider != null)
            {
                BindRuntimeSlider(runtimeProgressSlider, OnRuntimeProgressSliderChanged);
            }
            runtimeProgressText = FindText(runtimeControlsRoot, "progresstext");
        }

        if (runtimeProgressSlider == null)
        {
            return;
        }

        if (vp == null)
        {
            suppressRuntimeProgressCallback = true;
            UiComponentWriter.ApplySliderValueWithoutNotify(runtimeProgressSlider, 0f);
            suppressRuntimeProgressCallback = false;
            if (runtimeProgressText != null)
            {
                UiComponentWriter.ApplyTextContent(runtimeProgressText, "00:00 / 00:00");
            }
            return;
        }

        long vpFrameCount = vp.frameCount > 0 ? (long)vp.frameCount : 0L;
        long vpFrame = vp.frame >= 0 ? vp.frame : -1L;
        float manifestFps = manifest != null ? manifest.fps : 0f;
        int totalFramesMeta = metaHeader.numFrames > 0 ? (int)metaHeader.numFrames : (manifest != null && manifest.num_frames > 0 ? manifest.num_frames : 0);
        int manifestFrames = manifest != null ? manifest.num_frames : 0;
        RuntimeProgressDisplay.State progress = RuntimeProgressDisplay.Resolve(
            vpFrameCount,
            vpFrame,
            vp.time,
            vp.length,
            vp.frameRate,
            metaHeader.fps,
            manifestFps,
            totalFramesMeta,
            manifestFrames,
            GetCurrentPlaybackFrame());

        suppressRuntimeProgressCallback = true;
        UiComponentWriter.ApplySliderValueWithoutNotify(runtimeProgressSlider, progress.normalized);
        suppressRuntimeProgressCallback = false;

        if (runtimeProgressText != null)
        {
            UiComponentWriter.ApplyTextContent(runtimeProgressText, progress.clockText);
        }
    }



    private void OnRuntimeProgressSliderChanged(float normalized)
    {
        if (suppressRuntimeProgressCallback || vp == null)
        {
            return;
        }

        normalized = Mathf.Clamp01(normalized);
        long totalFramesVp = vp.frameCount > 0 ? (long)vp.frameCount : 0L;
        int totalFramesMeta = metaHeader.numFrames > 0 ? (int)metaHeader.numFrames : (manifest != null && manifest.num_frames > 0 ? manifest.num_frames : 0);
        int manifestFrames = manifest != null ? manifest.num_frames : 0;
        long totalFrames = RuntimePlaybackTimeline.ResolveTotalFrames(totalFramesVp, totalFramesMeta, manifestFrames);
        float manifestFps = manifest != null ? manifest.fps : 0f;
        float fpsFallback = RuntimePlaybackTimeline.ResolveSeekFps(metaHeader.fps, manifestFps, vp.frameRate);
        double totalDuration = RuntimePlaybackTimeline.ResolveTotalDuration(vp.length, fpsFallback, totalFrames);

        RuntimePlaybackTimeline.SeekTarget target = RuntimePlaybackTimeline.ResolveSeekTarget(
            normalized,
            totalDuration,
            vp.canSetTime,
            totalFrames);
        RuntimePlaybackController.ApplySeekTarget(vp, target);

        UpdateRuntimeProgressUi();
    }



    private static Font GetRuntimeUiFont()
    {
        try
        {
            Font legacy = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (legacy != null)
            {
                return legacy;
            }
        }
        catch
        {
        }

        try
        {
            return Resources.GetBuiltinResource<Font>("Arial.ttf");
        }
        catch
        {
            return null;
        }
    }



    private void HandleRuntimePauseInput()
    {
        if (!EnablePauseHotkey)
        {
            return;
        }

        bool hotkeyPressed = IsPauseHotkeyPressed();
        bool hasPrimaryButton = TryReadPrimaryButtonPressed(out bool pressed);
        RuntimePauseInput.Decision decision = RuntimePauseInput.Resolve(
            hotkeyPressed,
            hasPrimaryButton,
            pressed,
            prevPrimaryButtonPressed);

        prevPrimaryButtonPressed = decision.previousPrimaryButtonPressed;
        if (decision.togglePause)
        {
            TogglePausePlayback();
        }
    }



    private bool IsPauseHotkeyPressed()
    {
        return RuntimePauseInputReader.IsPauseHotkeyPressed();
    }



    private bool TryReadPrimaryButtonPressed(out bool pressed)
    {
        return RuntimePauseInputReader.TryReadPrimaryButtonPressed(xrInputDevices, out pressed);
    }



    public void TogglePausePlayback()
    {
        if (vp == null)
        {
            return;
        }

        RuntimePlaybackController.Apply(
            vp,
            RuntimePlaybackController.ResolveToggleCommand(vp.isPlaying));

        UpdatePauseButtonLabel();
    }



    private void UpdatePauseButtonLabel()
    {
        if (runtimePauseButtonText == null)
        {
            return;
        }

        UiComponentWriter.ApplyTextContent(runtimePauseButtonText, (vp != null && vp.isPlaying) ? "Pause" : "Resume");
    }


}
