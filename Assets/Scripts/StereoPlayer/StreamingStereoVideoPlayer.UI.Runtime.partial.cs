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
        TransformWriter.ApplyPose(
            runtimeSettingsRoot.transform, ApplyRuntimePanelDistanceOffset(pose.position), pose.rotation);

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



    private void OnRuntimeTrackYawResetClicked()
    {
        if (isNormalMode || !TryGetSelectedManualRotationTrack(out uint trackId))
        {
            return;
        }

        PauseForManualRotationEdit();
        // 3 軸まとめて 0 に戻す。yaw だけ戻しても pitch / roll が残ると
        // 「Reset したのに傾いたまま」になる。
        SetManualRotationForTrack(trackId, 0f, 0f, 0f);
        UpdateRuntimeTrackRotationUiState();
        PersistManualYaw(trackId);
        // Else は bundle に向きの推定値が無く、ここでの調整が唯一の向きの決め手になる。
        // モデル変更と同じく交絡になり得るので prefab 名と同じ粒度で記録する
        // （Docs/experiment-flow.md「操作の統制」）。
        ExperimentLog.Operation("change_rotation", $"track={trackId} yaw=0 op=reset");
    }



    // 現在フレームの yaw / scale のキーを消す。両方まとめて消す。
    // 片方ずつにするとボタンが 2 つ増えて Track 行に入らないうえ、
    // 「このフレームの調整を取り消す」という意図では普通どちらも消したい。
    private void OnRuntimeTrackKeyDeleteClicked()
    {
        if (isNormalMode || !TryGetSelectedManualRotationTrack(out uint trackId))
        {
            return;
        }

        PauseForManualRotationEdit();
        bool removedYaw = RemoveManualYawKeyAtCurrentFrame(trackId);
        bool removedScale = RemoveManualScaleKeyAtCurrentFrame(trackId);
        if (!removedYaw && !removedScale)
        {
            return;
        }

        if (removedYaw)
        {
            PersistManualYaw(trackId);
        }
        if (removedScale)
        {
            PersistManualScale(trackId);
        }

        UpdateRuntimeTrackRotationUiState();
        ExperimentLog.Operation(
            "change_rotation",
            $"track={trackId} op=delete_key frame={GetCurrentPlaybackFrame()} " +
            $"yaw={removedYaw} scale={removedScale}");
    }


    private void OnRuntimeTrackScaleResetClicked()
    {
        if (isNormalMode || !TryGetSelectedManualRotationTrack(out uint trackId))
        {
            return;
        }

        PauseForManualRotationEdit();
        ResetManualScaleForTrack(trackId);
        UpdateRuntimeTrackRotationUiState();
        PersistManualScale(trackId);
        ExperimentLog.Operation("change_scale", $"track={trackId} scale=1 op=reset");
    }



    private void OnRuntimeTrackScaleSliderChanged(float value)
    {
        if (suppressRuntimeTrackScaleCallback || isNormalMode)
        {
            return;
        }

        if (!TryGetSelectedManualRotationTrack(out uint trackId))
        {
            return;
        }

        PauseForManualRotationEdit();
        SetManualScaleForTrack(trackId, value);
        UpdateRuntimeTrackRotationUiState();
        PersistManualScale(trackId);
        // 向きと同じく、Else の大きさはユーザーの調整が唯一の決め手になる。
        // 交絡になり得るのでモデル変更・回転と同じ粒度で記録する
        // （Docs/experiment-flow.md「操作の統制」）。
        ExperimentLog.Operation(
            "change_scale",
            $"track={trackId} scale={ExperimentCsv.Format(value)} frame={GetCurrentPlaybackFrame()}");
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



    // 編集タブの表示をいまの対象・いまのフレームに合わせる。
    //
    // 参照先は**モデル編集タブ**（Change パネル）。以前は Settings パネルの中を
    // FindButton(runtimeSettingsRoot, ...) で名前引きしていたが、対象ごとの編集は
    // そちらへ移した（2026-09-04）。いまは生成時に持った参照をそのまま使う。
    private void UpdateRuntimeTrackRotationUiState()
    {
        if (isNormalMode)
        {
            ApplyTrackEditControlsInteractable(false);
            if (runtimeTrackScaleSlider != null)
            {
                UiComponentWriter.ApplyInteractable(runtimeTrackScaleSlider, false);
            }
            runtimeTrackPrevKeyFrame = -1;
            runtimeTrackNextKeyFrame = -1;
            ApplyTrackKeyNavigationButtons();
            return;
        }

        EnsureSelectedManualRotationTrack();

        if (!TryGetSelectedManualRotationTrack(out uint trackId))
        {
            ApplyTrackEditControlsInteractable(false);
            if (runtimeTrackRotationValueText != null)
            {
                UiComponentWriter.ApplyTextContent(runtimeTrackRotationValueText, "対象がありません");
            }
            if (runtimeTrackScaleValueText != null)
            {
                UiComponentWriter.ApplyTextContent(runtimeTrackScaleValueText, "x1.00");
            }
            if (runtimeTrackScaleSlider != null)
            {
                suppressRuntimeTrackScaleCallback = true;
                UiComponentWriter.ApplySliderValueWithoutNotify(runtimeTrackScaleSlider, ManualScaleDefault);
                suppressRuntimeTrackScaleCallback = false;
                UiComponentWriter.ApplyInteractable(runtimeTrackScaleSlider, false);
            }
            if (runtimeTrackKeyInfoText != null)
            {
                UiComponentWriter.ApplyTextContent(runtimeTrackKeyInfoText, "キー 回転:0 大きさ:0");
            }
            runtimeTrackPrevKeyFrame = -1;
            runtimeTrackNextKeyFrame = -1;
            ApplyTrackKeyNavigationButtons();
            return;
        }

        int keyCount = GetManualYawKeyCountForTrack(trackId);
        int scaleKeyCount = GetManualScaleKeyCountForTrack(trackId);
        bool hasKeyAtCurrent = HasManualYawKeyAtCurrentFrame(trackId) || HasManualScaleKeyAtCurrentFrame(trackId);
        int frame = GetCurrentPlaybackFrame();
        float manualScale = GetManualScaleForTrack(trackId);
        GetManualRotationForTrack(trackId, out float yaw, out float pitch, out float roll);

        ApplyTrackEditControlsInteractable(true);

        // Del は「現在フレームにキーがあるとき」だけ押せる。押せるかどうかで
        // そのフレームがキーなのか補間なのかが分かる。
        if (runtimeTrackKeyDeleteButtonRef != null)
        {
            UiComponentWriter.ApplyInteractable(runtimeTrackKeyDeleteButtonRef, hasKeyAtCurrent);
        }

        if (runtimeTrackRotationValueText != null)
        {
            UiComponentWriter.ApplyTextContent(
                runtimeTrackRotationValueText,
                "回転  yaw " + yaw.ToString("F1") +
                "  pitch " + pitch.ToString("F1") +
                "  roll " + roll.ToString("F1"));
        }
        if (runtimeTrackScaleValueText != null)
        {
            UiComponentWriter.ApplyTextContent(runtimeTrackScaleValueText, "x" + manualScale.ToString("F2"));
        }
        if (runtimeTrackScaleSlider != null)
        {
            UiComponentWriter.ApplyInteractable(runtimeTrackScaleSlider, true);
            suppressRuntimeTrackScaleCallback = true;
            UiComponentWriter.ApplySliderValueWithoutNotify(runtimeTrackScaleSlider, manualScale);
            suppressRuntimeTrackScaleCallback = false;
        }
        if (runtimeTrackKeyInfoText != null)
        {
            UiComponentWriter.ApplyTextContent(
                runtimeTrackKeyInfoText,
                "キー 回転:" + keyCount + " 大きさ:" + scaleKeyCount +
                "   現在 " + frame + (hasKeyAtCurrent ? " [キー]" : " [補間]"));
        }

        RefreshTrackKeyNavigationTargets(trackId, frame);
        ApplyTrackKeyNavigationButtons();
    }


    private void ApplyTrackEditControlsInteractable(bool interactable)
    {
        if (runtimeTrackRotationResetButton != null)
        {
            UiComponentWriter.ApplyInteractable(runtimeTrackRotationResetButton, interactable);
        }
        if (runtimeTrackScaleResetButtonRef != null)
        {
            UiComponentWriter.ApplyInteractable(runtimeTrackScaleResetButtonRef, interactable);
        }
        // Del はここでは常に伏せる。「現在フレームにキーがあるか」を
        // 呼び出し側が見てから改めて押せるようにする。
        if (runtimeTrackKeyDeleteButtonRef != null)
        {
            UiComponentWriter.ApplyInteractable(runtimeTrackKeyDeleteButtonRef, false);
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
        // 対象ごとの編集はモデル編集タブへ移ったので、そちらが開いている間も更新する。
        // 現在フレームが動けばキー情報も前後送りの飛び先も変わる。
        bool editTabOpen = runtimeModelPickerOpen && runtimeModelPickerTab == ModelPickerTabEdit;
        if (editTabOpen)
        {
            UpdateRuntimeTrackRotationUiState();
        }

        // 向きのガイドは「いま編集している」ときだけ出す。
        UpdateManualYawGuide(runtimeSettingsOpen || editTabOpen);

        if (!runtimeSettingsOpen)
        {
            return;
        }

        UpdateRuntimeScreenDistanceUiState();
        UpdateRuntimeInteractiveMotionUiState();
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

        ExperimentLog.Operation("seek", ExperimentCsv.Format(normalized));
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
            Debug.LogWarning("[Playback] toggle ignored: VideoPlayer が無い");
            return;
        }

        // 「色々押したら止まって再開しなくなった」の再現待ち（2026-08-28）。
        // どの状態で押されたかが分からないと原因を絞れないので、押すたびに出す。
        Debug.Log(
            $"[Playback] toggle: playing={vp.isPlaying} prepared={vp.isPrepared} " +
            $"frame={vp.frame} len={vp.frameCount} url={(string.IsNullOrEmpty(vp.url) ? "(empty)" : "set")} " +
            $"picker={runtimeModelPickerOpen} settings={runtimeSettingsOpen} normalMode={isNormalMode}");

        RuntimePlaybackController.Apply(
            vp,
            RuntimePlaybackController.ResolveToggleCommand(vp.isPlaying));

        ExperimentLog.Operation(vp.isPlaying ? "resume" : "pause");

        // 再生に戻したら編集用のパネルは畳む。モデル変更も向き調整も
        // 一時停止して行う操作なので、再生中に開いたままだと視界を塞ぐだけ
        // （2026-08-28 の指摘）。
        if (vp.isPlaying)
        {
            CloseEditPanelsForResume();
        }

        UpdatePauseButtonLabel();
    }



    private void CloseEditPanelsForResume()
    {
        if (runtimeModelPickerOpen)
        {
            CloseRuntimeModelPickerPanel();
        }

        if (runtimeSettingsOpen)
        {
            ToggleRuntimeSettingsPanel();
        }
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
