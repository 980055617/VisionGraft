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
        Vector3 toHead = head != null ? (head.position - center).normalized : -basis.forward;
        if (toHead == Vector3.zero)
        {
            toHead = -basis.forward;
        }

        Vector3 right = basis.right;
        Vector3 up = basis.up;
        GetScreenSizeMeters(basis, out _, out float screenHeightMeters, out _);
        float halfScreenH = Mathf.Abs(screenHeightMeters) * 0.5f;
        float halfBarH = Mathf.Abs(ControlsBarSizeMeters.y) * 0.5f;
        float downFromCenter = halfScreenH + ControlsBarGapMeters + halfBarH - ControlsBarOffsetMeters.y;
        runtimeControlsRoot.transform.position =
            center
            + right * ControlsBarOffsetMeters.x
            - up * downFromCenter
            + toHead * ControlsBarForwardOffsetMeters;
        runtimeControlsRoot.transform.rotation = basis.rotation;
        ApplyRuntimeControlsSizing();

        Canvas canvas = GetRuntimeControlsCanvas();
        if (canvas != null)
        {
            canvas.worldCamera = GetViewCamera();
        }

        UpdateRuntimeSettingsPlacement();
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
        Vector3 toHead = head != null ? (head.position - basis.position).normalized : -basis.forward;
        if (toHead == Vector3.zero)
        {
            toHead = -basis.forward;
        }

        float basisWidth = ControlsBarSizeMeters.x;
        if (baseScreen != null)
        {
            GetScreenSizeMeters(baseScreen, out float screenWidthMeters, out _, out _);
            basisWidth = Mathf.Abs(screenWidthMeters);
        }

        float halfBarW = Mathf.Abs(basisWidth) * 0.5f;
        float halfPanelW = Mathf.Abs(SettingsPanelSizeMeters.x) * 0.5f;
        float rightFromBar = halfBarW + SettingsPanelGapMeters + halfPanelW + SettingsPanelOffsetMeters.x;
        float forwardFromScreen = Mathf.Max(SettingsPanelForwardOffsetMeters, 0.06f);
        runtimeSettingsRoot.transform.position =
            basis.position
            + basis.right * rightFromBar
            + basis.up * SettingsPanelOffsetMeters.y
            + toHead * forwardFromScreen;
        Quaternion panelRotation = basis.rotation;
        if (head != null)
        {
            Vector3 up = basis.up.sqrMagnitude > 0.000001f ? basis.up.normalized : Vector3.up;
            Vector3 look = head.position - runtimeSettingsRoot.transform.position;
            look = Vector3.ProjectOnPlane(look, up);
            if (look.sqrMagnitude > 0.000001f)
            {
                panelRotation = Quaternion.LookRotation(look.normalized, up);
            }
            // World-space canvas forward is opposite in this project setup.
            panelRotation = Quaternion.AngleAxis(180f, up) * panelRotation;
        }
        runtimeSettingsRoot.transform.rotation = panelRotation;

        Canvas canvas = runtimeSettingsRoot.GetComponent<Canvas>();
        if (canvas != null)
        {
            canvas.worldCamera = GetViewCamera();
        }

        RectTransform rect = runtimeSettingsRoot.GetComponent<RectTransform>();
        if (rect != null)
        {
            Vector2 size = rect.sizeDelta;
            if (size.x <= 0f || size.y <= 0f)
            {
                size = new Vector2(RuntimeSettingsDefaultCanvasWidth, RuntimeSettingsDefaultCanvasHeight);
                rect.sizeDelta = size;
            }

            rect.localScale = new Vector3(
                SettingsPanelSizeMeters.x / size.x,
                SettingsPanelSizeMeters.y / size.y,
                1f);
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
            rect.sizeDelta = size;
        }

        rect.localScale = new Vector3(
            ControlsBarSizeMeters.x / size.x,
            ControlsBarSizeMeters.y / size.y,
            1f);
    }



    private void OnRuntimeTrackPrevClicked()
    {
        PauseForManualRotationEdit();
        StepSelectedManualRotationTrack(-1);
        UpdateRuntimeTrackRotationUiState();
    }



    private void OnRuntimeTrackNextClicked()
    {
        PauseForManualRotationEdit();
        StepSelectedManualRotationTrack(1);
        UpdateRuntimeTrackRotationUiState();
    }



    private void OnRuntimeTrackYawResetClicked()
    {
        if (!TryGetSelectedManualRotationTrack(out uint trackId))
        {
            return;
        }

        PauseForManualRotationEdit();
        SetManualYawOffsetDegForTrack(trackId, 0f);
        UpdateRuntimeTrackRotationUiState();
    }



    private void OnRuntimeTrackYawSliderChanged(float value)
    {
        if (suppressRuntimeTrackYawCallback)
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
        enableInteractiveMotion = !enableInteractiveMotion;
        if (!enableInteractiveMotion)
        {
            StopAllInteractiveMotion();
        }
        UpdateRuntimeInteractiveMotionUiState();
    }



    private void UpdateRuntimeInteractiveMotionUiState()
    {
        if (runtimeInteractiveMotionValueText == null)
        {
            return;
        }

        runtimeInteractiveMotionValueText.text = enableInteractiveMotion ? "ON" : "OFF";
    }



    private void UpdateRuntimeTrackRotationUiState()
    {
        if (runtimeTrackSelectionText == null && runtimeTrackYawSlider == null && runtimeTrackYawValueText == null &&
            runtimeTrackFrontGuideText == null && runtimeTrackKeyInfoText == null)
        {
            return;
        }

        EnsureSelectedManualRotationTrack();

        if (!TryGetSelectedManualRotationTrack(out uint trackId))
        {
            if (runtimeTrackSelectionText != null)
            {
                runtimeTrackSelectionText.text = "none";
            }
            if (runtimeTrackYawValueText != null)
            {
                runtimeTrackYawValueText.text = "0.0 deg";
            }
            if (runtimeTrackYawSlider != null)
            {
                suppressRuntimeTrackYawCallback = true;
                runtimeTrackYawSlider.SetValueWithoutNotify(0f);
                suppressRuntimeTrackYawCallback = false;
                runtimeTrackYawSlider.interactable = false;
            }
            if (runtimeTrackFrontGuideText != null)
            {
                runtimeTrackFrontGuideText.text = "Arrow above head = FRONT  |  +:left  -:right";
            }
            if (runtimeTrackKeyInfoText != null)
            {
                runtimeTrackKeyInfoText.text = "Keys:0  Frame:0";
            }
            return;
        }

        int keyCount = GetManualYawKeyCountForTrack(trackId);
        bool hasKeyAtCurrent = HasManualYawKeyAtCurrentFrame(trackId);
        int frame = GetCurrentFrameIndex();
        float yaw = GetManualYawOffsetDegForTrack(trackId);
        if (runtimeTrackSelectionText != null)
        {
            runtimeTrackSelectionText.text = trackId.ToString();
        }
        if (runtimeTrackYawValueText != null)
        {
            runtimeTrackYawValueText.text = yaw.ToString("F1") + " deg";
        }
        if (runtimeTrackYawSlider != null)
        {
            runtimeTrackYawSlider.interactable = true;
            suppressRuntimeTrackYawCallback = true;
            runtimeTrackYawSlider.SetValueWithoutNotify(yaw);
            suppressRuntimeTrackYawCallback = false;
        }
        if (runtimeTrackFrontGuideText != null)
        {
            runtimeTrackFrontGuideText.text = "Arrow above head = FRONT  |  +:left  -:right";
        }
        if (runtimeTrackKeyInfoText != null)
        {
            runtimeTrackKeyInfoText.text = "Keys:" + keyCount + "  Frame:" + frame + (hasKeyAtCurrent ? " [key]" : " [interp]");
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
            runtimeScreenDistanceSlider.SetValueWithoutNotify(clamped);
            suppressRuntimeScreenDistanceCallback = false;
        }

        UpdateRuntimeScreenDistanceText(clamped);
    }



    private void PauseForManualRotationEdit()
    {
        if (vp == null || !vp.isPlaying)
        {
            return;
        }

        vp.Pause();
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
                runtimeProgressSlider.onValueChanged.RemoveListener(OnRuntimeProgressSliderChanged);
                runtimeProgressSlider.onValueChanged.AddListener(OnRuntimeProgressSliderChanged);
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
            runtimeProgressSlider.SetValueWithoutNotify(0f);
            suppressRuntimeProgressCallback = false;
            if (runtimeProgressText != null)
            {
                runtimeProgressText.text = "00:00 / 00:00";
            }
            return;
        }

        long vpFrameCount = vp.frameCount > 0 ? (long)vp.frameCount : 0L;
        long vpFrame = vp.frame >= 0 ? vp.frame : -1L;
        float fpsFallback = metaHeader.fps > 0.001f ? metaHeader.fps : (manifest != null && manifest.fps > 0.001f ? manifest.fps : 0f);
        int totalFramesMeta = metaHeader.numFrames > 0 ? (int)metaHeader.numFrames : (manifest != null && manifest.num_frames > 0 ? manifest.num_frames : 0);
        long totalFrames = vpFrameCount > 1 ? vpFrameCount : (totalFramesMeta > 1 ? totalFramesMeta : 0L);
        int currentFrame = vpFrame >= 0
            ? (int)vpFrame
            : (fpsFallback > 0.001f ? Mathf.Max(0, Mathf.FloorToInt((float)vp.time * fpsFallback)) : GetCurrentFrameIndex());

        double totalDuration = vp.length;
        if (totalDuration <= 0.0001d && fpsFallback > 0.001f && totalFrames > 0)
        {
            totalDuration = totalFrames / fpsFallback;
        }

        float normalized = 0f;
        if (totalDuration > 0.0001d)
        {
            normalized = Mathf.Clamp01((float)(vp.time / totalDuration));
        }
        else if (totalFrames > 1)
        {
            normalized = Mathf.Clamp01((float)currentFrame / (float)(totalFrames - 1));
        }

        suppressRuntimeProgressCallback = true;
        runtimeProgressSlider.SetValueWithoutNotify(normalized);
        suppressRuntimeProgressCallback = false;

        if (runtimeProgressText != null)
        {
            float fps = metaHeader.fps > 0.001f
                ? metaHeader.fps
                : (manifest != null && manifest.fps > 0.001f
                    ? manifest.fps
                    : (vp.frameRate > 0.001f ? (float)vp.frameRate : 0f));
            float curSec = (float)vp.time;
            float totalSec = (float)totalDuration;
            if (totalSec <= 0.0001f && fps > 0.001f && totalFrames > 0)
            {
                int frameForTime = vpFrame >= 0 ? (int)vpFrame : currentFrame;
                curSec = frameForTime / fps;
                totalSec = (float)totalFrames / fps;
            }
            runtimeProgressText.text = FormatClock(curSec) + " / " + FormatClock(totalSec);
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
        long totalFrames = totalFramesVp > 1 ? totalFramesVp : (totalFramesMeta > 1 ? totalFramesMeta : 0L);
        float fpsFallback = metaHeader.fps > 0.001f ? metaHeader.fps : (manifest != null && manifest.fps > 0.001f ? manifest.fps : (vp.frameRate > 0.001f ? (float)vp.frameRate : 0f));
        double totalDuration = vp.length;
        if (totalDuration <= 0.0001d && fpsFallback > 0.001f && totalFrames > 0)
        {
            totalDuration = totalFrames / fpsFallback;
        }

        if (totalDuration > 0.0001d)
        {
            double t = normalized * totalDuration;
            if (vp.canSetTime)
            {
                vp.time = t;
            }
            else if (totalFrames > 1)
            {
                long targetFrame = (long)Mathf.Round(normalized * (totalFrames - 1));
                targetFrame = System.Math.Max(0L, System.Math.Min(targetFrame, totalFrames - 1L));
                vp.frame = targetFrame;
            }
        }
        else if (totalFrames > 1)
        {
            long targetFrame = (long)Mathf.Round(normalized * (totalFrames - 1));
            targetFrame = System.Math.Max(0L, System.Math.Min(targetFrame, totalFrames - 1L));
            vp.frame = targetFrame;
        }

        UpdateRuntimeProgressUi();
    }



    private static string FormatClock(float sec)
    {
        if (sec < 0f || float.IsNaN(sec) || float.IsInfinity(sec))
        {
            sec = 0f;
        }

        int total = Mathf.FloorToInt(sec);
        int m = total / 60;
        int s = total % 60;
        return m.ToString("00") + ":" + s.ToString("00");
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

        if (IsPauseHotkeyPressed())
        {
            TogglePausePlayback();
            return;
        }

        if (TryReadPrimaryButtonPressed(out bool pressed))
        {
            if (pressed && !prevPrimaryButtonPressed)
            {
                TogglePausePlayback();
            }
            prevPrimaryButtonPressed = pressed;
        }
        else
        {
            prevPrimaryButtonPressed = false;
        }
    }



    private bool IsPauseHotkeyPressed()
    {
#if ENABLE_INPUT_SYSTEM
        UnityEngine.InputSystem.Keyboard kb = UnityEngine.InputSystem.Keyboard.current;
        if (kb != null &&
            ((kb.spaceKey != null && kb.spaceKey.wasPressedThisFrame) ||
             (kb.pKey != null && kb.pKey.wasPressedThisFrame)))
        {
            return true;
        }
#endif

#if ENABLE_LEGACY_INPUT_MANAGER
        return Input.GetKeyDown(KeyCode.Space) || Input.GetKeyDown(KeyCode.P);
#else
        return false;
#endif
    }



    private bool TryReadPrimaryButtonPressed(out bool pressed)
    {
        pressed = false;
        xrInputDevices.Clear();
        InputDevices.GetDevices(xrInputDevices);
        bool hasAny = false;
        for (int i = 0; i < xrInputDevices.Count; i++)
        {
            InputDevice device = xrInputDevices[i];
            if (!device.isValid)
            {
                continue;
            }

            if (device.TryGetFeatureValue(CommonUsages.primaryButton, out bool value))
            {
                hasAny = true;
                pressed |= value;
            }
        }

        return hasAny;
    }



    public void TogglePausePlayback()
    {
        if (vp == null)
        {
            return;
        }

        if (vp.isPlaying)
        {
            vp.Pause();
        }
        else
        {
            vp.Play();
        }

        UpdatePauseButtonLabel();
    }



    private void UpdatePauseButtonLabel()
    {
        if (runtimePauseButtonText == null)
        {
            return;
        }

        runtimePauseButtonText.text = (vp != null && vp.isPlaying) ? "Pause" : "Resume";
    }


}
