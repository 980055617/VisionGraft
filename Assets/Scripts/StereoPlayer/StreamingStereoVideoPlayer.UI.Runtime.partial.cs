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
            // 板の高さも Model / Settings と同じ換算で渡す。× screenDist / 2.0 だと高さが半分で
            // 計算され、バーの上端（約 1.1°）が画面の角度内かつ画面より奥に入る。今はその帯に
            // ウィジェットが無いので押せない操作は無いが、余裕がゼロだった（2026-09-25 の監査 F-3）。
            ResolvePanelSizeAtScreenPlane(ControlsBarSizeMeters, center, head, false),
            ScaleUiOffsetForDistance(ControlsBarGapMeters),
            ScaleUiOffsetForDistance(ControlsBarOffsetMeters),
            ControlsBarForwardOffsetMeters);
        Vector3 barPos = PinRuntimeUiDistance(pose.position);
        TransformWriter.ApplyPose(
            runtimeControlsRoot.transform, barPos, FaceRuntimeUiToView(barPos, pose.rotation));
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

        // 板の幅は Model パネルと同じ換算で渡す（**物理幅 × 画面の距離 ÷ パネルの距離**）。
        // ScaleUiOffsetForDistance は × screenDist / 2.0 なので、画面 1.0 m では実物の半分の幅で
        // 計算され、板の左 1/4 が画面の角度内（かつ画面より奥）に入っていた。そこはレイを画面に
        // 取られる（2026-09-25 の監査 F-2。Model パネルは既にこの換算に直してあった）。
        Vector2 settingsSizeAtScreen = ResolvePanelSizeAtScreenPlane(SettingsPanelSizeMeters, basis.position, head);
        RuntimeControlsPlacement.Pose pose = RuntimeControlsPlacement.ResolveSettingsPose(
            basis.position,
            basis.forward,
            basis.right,
            basis.up,
            basis.rotation,
            head != null,
            head != null ? head.position : Vector3.zero,
            basisWidth,
            settingsSizeAtScreen,
            ScaleUiOffsetForDistance(SettingsPanelGapMeters),
            ScaleUiOffsetForDistance(SettingsPanelOffsetMeters),
            SettingsPanelForwardOffsetMeters);
        Vector3 settingsPos = ApplyRuntimePanelDistanceOffset(PinRuntimeUiDistance(pose.position));

        // **画面より手前に引き寄せる。**換算だけでは横に大きく振った位置で被りが残るのと、
        // 掴んで前後に動かせる（±1 m）ので奥へ押し込むと画面の裏に入って自分では戻せなくなる
        // （同監査 F-7）。Model パネルと同じ処理。見かけの大きさが変わらないよう距離比で縮める。
        float settingsScale = 1f;
        if (pinRuntimeUiDistance && head != null && baseScreen != null)
        {
            float screenDistance = Vector3.Distance(head.position, basis.position);
            float maxDistance = screenDistance - ModelPickerInFrontOfScreenMarginMeters;
            Vector3 away = settingsPos - head.position;
            float distance = away.magnitude;
            if (maxDistance >= 0.25f && distance > maxDistance && distance > 0.0001f)
            {
                settingsPos = head.position + away.normalized * maxDistance;
                settingsScale = maxDistance / distance;
            }
        }

        TransformWriter.ApplyPose(
            runtimeSettingsRoot.transform, settingsPos, FaceRuntimeUiToView(settingsPos, pose.rotation));

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

            // settingsScale は「画面より手前へ引き寄せた分」。見かけの大きさを変えないよう距離比で縮める。
            TransformWriter.ApplyLocalScale(
                rect,
                new Vector3(
                    SettingsPanelSizeMeters.x * settingsScale / size.x,
                    SettingsPanelSizeMeters.y * settingsScale / size.y,
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
            $"yaw={ExperimentCsv.Format(removedYaw)} scale={ExperimentCsv.Format(removedScale)}");
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
        // 実験中は条件で固定（ボタン自体を作らないが、prefab 由来のボタンが残っていても効かないように）。
        if (isNormalMode || experimentLockInteractiveMotion)
        {
            return;
        }

        enableInteractiveMotion = !enableInteractiveMotion;
        if (!enableInteractiveMotion)
        {
            StopAllInteractiveMotion();
        }
        else
        {
            // OFF の間はスケジュールを進めないので、そのまま ON に戻すと期限切れの発火が次のフレームで
            // 起きる（「ON にしたらすぐ止まる」）。最短間隔ぶん先へ押す（2026-09-29 の 4 回目の監査）。
            DelayAllInteractiveMotionTriggersByMinimumInterval();
        }
        UpdateRuntimeInteractiveMotionUiState();
        ExperimentLog.Operation("motion_toggle", $"value={(enableInteractiveMotion ? 1 : 0)}");
    }



    private void UpdateRuntimeInteractiveMotionUiState()
    {
        Button motionToggle = FindButton(runtimeSettingsRoot, "interactivemotiontoggle");
        if (motionToggle != null)
        {
            UiComponentWriter.ApplyInteractable(motionToggle, !isNormalMode && !experimentLockInteractiveMotion);
        }

        if (runtimeInteractiveMotionValueText == null)
        {
            return;
        }

        // 値セルは 180 px × 36 px なので全角の「（固定）」を足すと 2 行に折れ、2 行目は Truncate で消える
        // （「OFF（固定」か「OFF」だけが見える。2026-09-29 の 3 回目の監査）。半角で 1 行に収める。
        string text = enableInteractiveMotion ? "ON" : "OFF";
        if (experimentLockInteractiveMotion)
        {
            // 空白を入れると "OFF (fixed)" が 182 px になり、180 px の値セルで 2 行に折れて
            // 2 行目が消える（"OFF" だけになる）。詰めれば 172 px で 1 行（2026-09-30 の 5 回目の監査）。
            text += "(fixed)";
        }
        UiComponentWriter.ApplyTextContent(runtimeInteractiveMotionValueText, text);
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



    // 戻り値: この呼び出しが実際に動画を止めたか（既に止まっていたら false）。
    // 掴んで回す側は、自分が止めたときだけ離したときに戻す（ResumeVideoAfterGrabRotate）。
    // cause: 何のために止めたか。解析で「掴みで止まった」と「パネル・編集で止まった」を分けられるように
    // 分けてある（2026-09-29 の 4 回目の監査。以前は全部 panel_or_edit）。
    private bool PauseForManualRotationEdit(string cause = "panel_or_edit")
    {
        RuntimePlaybackController.Command command = RuntimePlaybackController.ResolvePauseForEditCommand(
            vp != null,
            vp != null && vp.isPlaying);
        if (command == RuntimePlaybackController.Command.None)
        {
            return false;
        }

        RuntimePlaybackController.Apply(vp, command);
        UpdatePauseButtonLabel();

        // **止めたものには持ち主を持たせる。** Model パネルを開いている間の編集操作（回転リセット・Del・
        // 大きさ・キー送り・「表示しない」・対象の切り替え）で止めたぶんは、パネルを閉じるときに戻す。
        // 以前はこの 7 経路に戻す担当が誰も居らず、被験者が A を押すまで静止画のままになりえた
        // （2026-09-30 の 5 回目の監査）。掴み（cause=grab）は自前で戻すので触らない。
        if (runtimeModelPickerOpen && cause != "grab")
        {
            modelPickerWasPlayingBeforeOpen = true;
        }

        // **自動で止めたことも記録する。**手動の pause / resume しか残っていなかったので、
        // operations.csv だけでは「動画が止まっていた合計時間」が出せなかった（2026-09-25 の監査 M-4）。
        // action 名を分けてあるので、チュートリアルの段階検出（pause / resume を見る）は誤進行しない。
        ExperimentLog.Operation("pause_auto", $"cause={cause}");
        return true;
    }



    private void RefreshRuntimeSettingsPerFrame()
    {
        // 対象ごとの編集はモデル編集タブへ移ったので、そちらが開いている間も更新する。
        // 現在フレームが動けばキー情報も前後送りの飛び先も変わる。
        // 対象の解決が先。逆にすると、フレームが変わって track が切り替わったとき
        // 編集タブの値が 1 フレーム古い track のものになる。
        //
        // **パネルの中身も毎フレーム更新する。**
        // UpdateRuntimeModelPickerUiState は EnsureRuntimeControls と操作イベントからしか
        // 呼ばれていなかったが、EnsureRuntimeControls は OnPrepared から 1 回だけだ。
        // そのためシークバーを動かしてフレームが変わっても、写っている track が
        // 入れ替わったことがパネルに反映されなかった（2026-09-04 実機報告）。
        // 一覧の再生成は previewBuilt* のキャッシュで押さえてあるので毎回呼んでも重くない。
        //
        // UpdateRuntimePanelDrag を EnsureRuntimeControls に置いて動かなかったのと同じ罠。
        if (runtimeModelPickerOpen)
        {
            UpdateRuntimeModelPickerUiState();
        }

        if (runtimeModelPickerOpen && runtimeModelPickerTab == ModelPickerTabEdit)
        {
            UpdateRuntimeTrackRotationUiState();
        }

        // 向きのガイドは「いま編集している」ときだけ出す。
        // 被験者実験では Settings は Motion / Screen Dist の操作にしか使わないので、Settings を開いただけでは
        // 出さない（モデル編集タブのときだけ）。出すと置換ありの練習 3/3（Settings を開かせる）で必ず
        // モデルの頭上に赤い棒と球が現れ、被験者がモデルの一部と受け取る（2026-09-29 の 3 回目の監査）。
        // 自由視聴のときは従来どおり Settings でも出す。
        UpdateManualYawGuide(
            (runtimeSettingsOpen && !startedAsExperimentTrial) ||
            (runtimeModelPickerOpen && runtimeModelPickerTab == ModelPickerTabEdit));

        if (!runtimeSettingsOpen)
        {
            return;
        }

        UpdateRuntimeScreenDistanceUiState();
        UpdateRuntimeInteractiveMotionUiState();
    }



    // 進捗バーに「掛んでいるか」を知らせる仕掛けを付ける。
    // prefab 経路とフォールバック経路の両方から呼ばれるので、ここで重複を防ぐ。
    private void EnsureProgressDragNotifier(Slider slider)
    {
        if (slider == null)
        {
            return;
        }

        runtimeProgressDragNotifier = slider.GetComponent<RuntimeSliderDragNotifier>();
        if (runtimeProgressDragNotifier == null)
        {
            runtimeProgressDragNotifier = slider.gameObject.AddComponent<RuntimeSliderDragNotifier>();
        }

        runtimeProgressDragNotifier.onDragChanged = OnRuntimeProgressDragChanged;
    }


    private void UpdateRuntimeProgressUi()
    {
        if (runtimeProgressSlider == null && runtimeControlsRoot != null)
        {
            runtimeProgressSlider = FindSlider(runtimeControlsRoot, "progressslider");
            if (runtimeProgressSlider != null)
            {
                BindRuntimeSlider(runtimeProgressSlider, OnRuntimeProgressSliderChanged);
                EnsureProgressDragNotifier(runtimeProgressSlider);
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

        // **掴んでいる間はつまみを触らない。**
        // ここは毎フレーム走るので、上書きするとドラッグしたそばから
        // 現在の再生位置に戻され、動かしている様子がまったく見えない
        // （2026-09-04 の指摘）。掴んでいる間は指の位置がそのまま残るようにする。
        bool dragging = runtimeProgressDragNotifier != null && runtimeProgressDragNotifier.IsDragging;
        if (!dragging)
        {
            suppressRuntimeProgressCallback = true;
            UiComponentWriter.ApplySliderValueWithoutNotify(runtimeProgressSlider, progress.normalized);
            suppressRuntimeProgressCallback = false;
        }

        if (runtimeProgressText != null)
        {
            // 掴んでいる間は「飛ぼうとしている位置」を出す。離すまで映像は動かないので、
            // 数字が出ていないとどこへ行くのか分からない。
            UiComponentWriter.ApplyTextContent(
                runtimeProgressText,
                dragging ? ResolveSeekPreviewClockText(runtimeProgressSlider.value) : progress.clockText);
        }
    }



    // 掴んでいる間の scrub（つまみに映像を追従させる）の状態。
    // 2026-09-11 実機「ドラッグ中に動画が関係なく動いている。普通はつまみに合わせて動く」。
    private bool runtimeProgressDragWasPlaying;
    private float runtimeProgressLastScrubTime = -1f;
    private float runtimeProgressLastScrubValue = -1f;
    // 毎フレーム飛ばすとデコーダが flush され続けて離したあと映像が出るまで待たされる（2026-09-01）ので、
    // 0.15 秒に 1 回・つまみが 0.4% 以上動いたときだけ飛ばす。一時停止した上で飛ばすので、
    // 追従が遅れても再生が勝手に進むことはない。
    private const float ProgressScrubMinIntervalSeconds = 0.15f;
    private const float ProgressScrubMinDelta = 0.004f;

    private void OnRuntimeProgressSliderChanged(float normalized)
    {
        if (suppressRuntimeProgressCallback || vp == null)
        {
            return;
        }

        if (runtimeProgressDragNotifier != null && runtimeProgressDragNotifier.IsDragging)
        {
            // 掴んでいる間は間引いて飛ばし、映像をつまみに追従させる。操作ログには残さない
            // （離したときの 1 回だけ `seek` として残す）。
            float now = Time.unscaledTime;
            if (now - runtimeProgressLastScrubTime >= ProgressScrubMinIntervalSeconds &&
                Mathf.Abs(normalized - runtimeProgressLastScrubValue) >= ProgressScrubMinDelta)
            {
                runtimeProgressLastScrubTime = now;
                runtimeProgressLastScrubValue = normalized;
                SeekToNormalizedPosition(normalized, false);
            }

            return;
        }

        SeekToNormalizedPosition(normalized, true);
    }


    // 掴んだとき: 再生中なら止める（映像がつまみと無関係に進まないように）。
    // 離したとき: その位置へ飛ばし、掴む前に再生中だったなら再開する。
    // ここでの一時停止・再開は被験者の操作ではないので、TogglePausePlayback を通さず
    // 操作ログ（pause / resume）にも残さない。チュートリアルの A ボタンの段階を誤って進めないため。
    private void OnRuntimeProgressDragChanged(bool dragging)
    {
        if (vp == null || runtimeProgressSlider == null)
        {
            return;
        }

        if (dragging)
        {
            runtimeProgressDragWasPlaying = vp.isPlaying;
            runtimeProgressLastScrubTime = -1f;
            runtimeProgressLastScrubValue = -1f;
            if (runtimeProgressDragWasPlaying)
            {
                RuntimePlaybackController.Apply(vp, RuntimePlaybackController.Command.Pause);
                // バーの Pause / Resume の表示も合わせる。以前はここで止めても戻しても表示が変わらず、
                // 掴んでいる間は「Pause」（実際は停止中）、モーションから再開を預かって離した後は
                // 「Resume」（実際は再生中）のまま次の操作まで残った（2026-09-29 の 3 回目の監査）。
                UpdatePauseButtonLabel();
                // 記録にも残す。残さないと operations.csv だけで再生状態を追ったときに、つまみを掴んでいた
                // 間の停止が見えず、預けた再開（resume_auto cause=random_motion_end）が対無しで現れる
                // （2026-09-29 の 4 回目の監査）。action 名が pause / resume ではないので練習の段階は進まない。
                ExperimentLog.Operation("pause_auto", "cause=seek_drag");
            }

            return;
        }

        SeekToNormalizedPosition(runtimeProgressSlider.value, true);
        if (runtimeProgressDragWasPlaying)
        {
            runtimeProgressDragWasPlaying = false;
            // Random モーションが動画を止めている最中なら再開しない（止める権利はモーション側にある）。
            // モーションが終わるときに代わりに戻す（DeferVideoResumeToRandomMotionEnd）。
            if (!TryDeferVideoResumeToRandomMotionEnd())
            {
                RuntimePlaybackController.Apply(vp, RuntimePlaybackController.Command.Play);
                UpdatePauseButtonLabel();
                ExperimentLog.Operation("resume_auto", "cause=seek_drag");
                // 再生に戻したら編集パネルは畳む（2026-08-28 の規則）。ここだけ抜けていて、
                // Model パネルが開いたまま動画が進んだ（2026-09-30 の 5 回目の監査）。
                CloseEditPanelsForResume();
            }
        }
    }


    // 掴んでいる間に出す「飛び先」の時刻。進捗バーと同じ換算を使う。
    private string ResolveSeekPreviewClockText(float normalized)
    {
        float fpsFallback = ResolveSeekFpsForKeyNavigation();
        double totalDuration = ResolveTotalPlaybackDuration(fpsFallback);
        float target = (float)(Mathf.Clamp01(normalized) * totalDuration);
        return RuntimePlaybackTimeline.FormatClock(target) + " / " +
               RuntimePlaybackTimeline.FormatClock((float)totalDuration);
    }


    private double ResolveTotalPlaybackDuration(float fpsFallback)
    {
        long totalFramesVp = vp.frameCount > 0 ? (long)vp.frameCount : 0L;
        int totalFramesMeta = metaHeader.numFrames > 0 ? (int)metaHeader.numFrames : (manifest != null && manifest.num_frames > 0 ? manifest.num_frames : 0);
        int manifestFrames = manifest != null ? manifest.num_frames : 0;
        long totalFrames = RuntimePlaybackTimeline.ResolveTotalFrames(totalFramesVp, totalFramesMeta, manifestFrames);
        return RuntimePlaybackTimeline.ResolveTotalDuration(vp.length, fpsFallback, totalFrames);
    }


    // logOperation: 操作ログに `seek` を残すか。掴んでいる間の scrub は残さない（離したときの 1 回だけ）。
    private void SeekToNormalizedPosition(float normalized, bool logOperation = true)
    {
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

        if (logOperation)
        {
            ExperimentLog.Operation("seek", ExperimentCsv.Format(normalized));
        }
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

        // 読み込み中（url が空・Prepare 前）は何もしない。url の無い VideoPlayer に Play を掛けると
        // errorReceived → FailBundleLoad で試行が「読み込み失敗」になりうる（2026-09-29 の監査）。
        if (!vp.isPrepared)
        {
            Debug.Log("[Playback] toggle ignored: まだ Prepare が終わっていない");
            return;
        }

        // 「色々押したら止まって再開しなくなった」の再現待ち（2026-08-28）。
        // どの状態で押されたかが分からないと原因を絞れないので、押すたびに出す。
        Debug.Log(
            $"[Playback] toggle: playing={vp.isPlaying} prepared={vp.isPrepared} " +
            $"frame={vp.frame} len={vp.frameCount} url={(string.IsNullOrEmpty(vp.url) ? "(empty)" : "set")} " +
            $"picker={runtimeModelPickerOpen} settings={runtimeSettingsOpen} normalMode={isNormalMode}");

        bool wasPlaying = vp.isPlaying;
        RuntimePlaybackController.Apply(
            vp,
            RuntimePlaybackController.ResolveToggleCommand(wasPlaying));

        ExperimentLog.Operation(vp.isPlaying ? "resume" : "pause");

        // Random モーションが止めていた動画を被験者が A で戻したら、止めた所有権はモーションから被験者に移る。
        // 移さないと、モーションの終わりに「自分が止めた」と思って再開しに来て、その間に被験者が改めて A で
        // 止めた停止まで解いてしまう（2026-09-29 の 3 回目の監査）。video_pause_end もここで対にする。
        if (!wasPlaying && vp.isPlaying)
        {
            ReleaseRandomMotionVideoPauseOwnership("manual_resume");
        }

        // 掴み・つまみで止めたぶんも同じ。被験者が自分で触った時点で再生状態の持ち主は被験者になる。
        // 手放さないと「掴んで止める → A で戻す → A で止める → トリガーを離す」で、最後の停止が
        // 勝手に解けた（2026-09-29 の 4 回目の監査。自分で入れた戻し処理の穴）。
        // **つまみ側も同じ**（2026-09-30 の 5 回目の監査）: トリガーでつまみを掴んだまま親指で A を
        // 押す操作は片手でできるので、単眼・ステレオでも起きる。Model パネルの旗は
        // CloseEditPanelsForResume が閉じるときに消費するので触らない。
        grabRotatePausedVideo = false;
        runtimeProgressDragWasPlaying = false;

        // 再生に戻したら編集用のパネルは畳む。モデル変更も向き調整も
        // 一時停止して行う操作なので、再生中に開いたままだと視界を塞ぐだけ
        // （2026-08-28 の指摘）。
        if (vp.isPlaying)
        {
            CloseEditPanelsForResume();
        }

        UpdatePauseButtonLabel();
    }



    // 試行・練習を閉じるときに ExperimentController が呼ぶ。開いたままのパネルと掴みを閉じて、
    // `panel_open` / `pause_auto` に対の行を出させる。呼ばないと、パネルを開いたまま「視聴を終了」を
    // 押した試行の記録が「最後まで開きっぱなし・止まりっぱなし」に見えた（2026-09-30 の 5 回目の監査）。
    // 動画はアンロードまでの 1 秒ほど再生に戻るが、モーションの停止（StopAllInteractiveMotionForExperimentEnd）
    // でも同じことが起きるので新しい事象ではない。
    public void CloseRuntimePanelsForExperimentEnd()
    {
        // 沈静待ちの screen_dist を先に書き切る（Sink が外れる前に）。
        FlushPendingExperimentScreenDistLog(true);
        EndGrabRotate("試行の終わり");
        // つまみを掴んだままシーンが壊れるときに、解体中の OnDisable が再生を戻さないようにする。
        runtimeProgressDragWasPlaying = false;
        CloseEditPanelsForResume();
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
