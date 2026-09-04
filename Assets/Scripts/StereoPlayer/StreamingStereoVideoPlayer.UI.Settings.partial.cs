using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;
using UnityEngine.XR;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{

    private GameObject BuildRuntimeSettingsUi()
    {
        EnsureEventSystem();
        InitializeRuntimeFovxIfNeeded();

        GameObject settingsRootObj;
        if (runtimeControlsPrefab != null)
        {
            settingsRootObj = RuntimeUiRootFactory.Create("RuntimeSettingsPanel", runtimeControlsPrefab);
        }
        else
        {
            settingsRootObj = RuntimeUiRootFactory.Create("RuntimeSettingsPanel", null);
            Canvas canvas = RuntimeCanvasComponentFactory.EnsureCanvas(settingsRootObj);
            UiComponentWriter.ApplyWorldSpaceCamera(canvas, GetViewCamera());
            RuntimeCanvasComponentFactory.EnsureGraphicRaycaster(settingsRootObj, false);
        }
        EnsureCanvasRaycasters(settingsRootObj);

        Canvas rootCanvas = settingsRootObj.GetComponent<Canvas>();
        if (rootCanvas == null)
        {
            rootCanvas = settingsRootObj.GetComponentInChildren<Canvas>(true);
        }

        Transform uiRoot = rootCanvas != null ? rootCanvas.transform : settingsRootObj.transform;

        RectTransform uiRect = uiRoot as RectTransform;
        if (uiRect != null)
        {
            TransformWriter.ApplySizeDelta(
                uiRect,
                new Vector2(RuntimeSettingsDefaultCanvasWidth, RuntimeSettingsDefaultCanvasHeight));
            TransformWriter.ApplyLocalScale(
                uiRect,
                new Vector3(
                    SettingsPanelSizeMeters.x / RuntimeSettingsDefaultCanvasWidth,
                    SettingsPanelSizeMeters.y / RuntimeSettingsDefaultCanvasHeight,
                    1f));
        }

        RectTransform panelRect = RuntimeUiElementFactory.CreateRectChild("Panel", uiRoot, out GameObject panelObj);
        TransformWriter.ApplyStretchRect(
            panelRect,
            Vector2.zero,
            Vector2.one,
            Vector2.zero,
            Vector2.zero);
        Image panelImage = RuntimeUiElementFactory.AddImage(panelObj);
        UiComponentWriter.ApplyGraphicColor(panelImage, new Color(0f, 0f, 0f, 0.65f));

        // **3 列に切る。** 以前は要素ごとに勝手な位置と幅を持っていて、実測で
        // ラベルとスライダーが 58px、値とスライダーが 58px 重なっていた（2026-09-03）。
        // 列を固定すれば重なりが構造的に起きない。
        //
        //   ラベル列 : -450 〜 -190（左寄せ）
        //   操作列   : -180 〜  240（スライダー / ボタン）
        //   値列     :  260 〜  440（右寄せ）
        //
        // 行は 84px 間隔。ラベルの枠は 90px だが文字は中央にあるので、
        // 枠が数 px 触れても見た目は重ならない。
        //
        // 外したもの:
        //   FOVx      … 配置には効かず（manifest の fx_norm が優先される）、
        //                動かすと映像だけ拡縮して奥行きの対応が壊れる（2026-09-03）
        //   Yaw       … 対象を掴んで回せるようになったので重複（同上）
        //   矢印の説明 … 同上で役割が薄い（同上）
        //   Track / Rot 0 / Scl 1 / Del / Scale / Keys
        //             … **系統が違う。** ここに残っていたのは「いま選んでいる対象を編集する」
        //                操作で、Motion や Screen Dist のような系全体の設定ではなかった。
        //                モデル編集タブ（Change パネル）へ移した（2026-09-04 ユーザー提案）。
        CreateSettingsLabel(panelObj.transform, "Title", "Settings", SettingsRowY(0), 60, TextAnchor.MiddleCenter, 900f, 0f);

        CreateSettingsLabel(panelObj.transform, "InteractiveMotionLabel", "Motion", SettingsRowY(1), 40, TextAnchor.MiddleLeft);
        runtimeInteractiveMotionValueText =
            CreateSettingsValue(panelObj.transform, "InteractiveMotionValue", string.Empty, SettingsRowY(1), 36);
        Button motionToggle = CreateSmallButton(
            panelObj.transform, "InteractiveMotionToggleButton", new Vector2(-120f, SettingsRowY(1)), "Toggle");
        BindRuntimeButton(motionToggle, OnRuntimeInteractiveMotionToggleClicked);

        CreateSettingsLabel(panelObj.transform, "ScreenDistLabel", "Screen Dist", SettingsRowY(2), 40, TextAnchor.MiddleLeft);
        runtimeScreenDistanceValueText =
            CreateSettingsValue(panelObj.transform, "ScreenDistValue", string.Empty, SettingsRowY(2), 36);
        runtimeScreenDistanceSlider = CreateSlider(panelObj.transform, "ScreenDistanceSlider", SettingsRowY(2));
        if (runtimeScreenDistanceSlider != null)
        {
            UnbindRuntimeSlider(runtimeScreenDistanceSlider, OnRuntimeScreenDistanceSliderChanged);
            UpdateRuntimeScreenDistanceSliderRange();
            UiComponentWriter.ApplySliderValueWithoutNotify(runtimeScreenDistanceSlider, ClampRuntimeScreenDistance(screenDistanceMeters));
            BindRuntimeSlider(runtimeScreenDistanceSlider, OnRuntimeScreenDistanceSliderChanged);
        }
        UpdateRuntimeScreenDistanceText(screenDistanceMeters);

        // 掴み代は**下端**。上に置くとタイトルと重なり、視線も上へ引っ張られる。
        CreateRuntimePanelDragHandle(
            panelObj.transform, "PanelDragHandle", new Vector2(0f, SettingsDragHandleY), new Vector2(760f, 24f));

        UpdateRuntimeInteractiveMotionUiState();

        return settingsRootObj;
    }



    // 設定パネルの 3 列。canvas は 900x340 で、原点は中心。
    //
    // 高さは 640 → 340。対象ごとの編集をモデル編集タブへ移して 3 行しか残らず、
    // そのままだと下 2/3 が空いた枠だけの板になる（「UI が汚い」の一因）。
    // メートル換算は 1px あたり 0.615/640 のまま据え置き、文字の大きさは変えない。
    private const float SettingsLabelCenterX = -320f;
    private const float SettingsLabelWidth = 260f;
    private const float SettingsValueCenterX = 350f;
    private const float SettingsValueWidth = 180f;
    private const float SettingsControlCenterX = 30f;
    private const float SettingsControlWidth = 420f;
    private const float SettingsRowTopY = 118f;
    private const float SettingsRowPitch = 84f;
    private const float SettingsDragHandleY = -150f;


    private static float SettingsRowY(int row)
    {
        return SettingsRowTopY - row * SettingsRowPitch;
    }


    private Text CreateSettingsLabel(
        Transform parent, string name, string initialText, float y, int fontSize, TextAnchor anchor,
        // 既定の枠は 78。行間 84 に対して 6px の隙間が残るので、隣の行と触れない。
        // 90 にしていたときは上下の行と 6px ずつ重なっていた（実測 2026-09-03）。
        float width = SettingsLabelWidth, float centerX = SettingsLabelCenterX, float height = 78f)
    {
        RectTransform rect = RuntimeUiElementFactory.CreateRectChild(name, parent, out GameObject obj);
        TransformWriter.ApplyCenteredRect(rect, new Vector2(centerX, y), new Vector2(width, height));

        Text text = RuntimeUiElementFactory.AddText(obj);
        UiComponentWriter.ApplyTextStyle(text, GetRuntimeUiFont(), fontSize, anchor, Color.white);
        UiComponentWriter.ApplyTextOverflow(text, HorizontalWrapMode.Wrap, VerticalWrapMode.Truncate);
        UiComponentWriter.ApplyTextContent(text, initialText);
        return text;
    }


    private Text CreateSettingsValue(Transform parent, string name, string initialText, float y, int fontSize)
    {
        return CreateSettingsLabel(
            parent, name, initialText, y, fontSize, TextAnchor.MiddleRight,
            SettingsValueWidth, SettingsValueCenterX);
    }


    private Text CreateLabel(Transform parent, string name, string initialText, float anchorX, float anchorY, int fontSize, TextAnchor anchor)
    {
        RectTransform rect = RuntimeUiElementFactory.CreateRectChild(name, parent, out GameObject obj);
        Vector2 anchorPoint = new Vector2(anchorX, anchorY);
        TransformWriter.ApplyAnchoredRect(rect, anchorPoint, anchorPoint, Vector2.zero, new Vector2(280f, 90f));

        Text text = RuntimeUiElementFactory.AddText(obj);
        UiComponentWriter.ApplyTextStyle(text, GetRuntimeUiFont(), fontSize, anchor, Color.white);
        UiComponentWriter.ApplyTextContent(text, initialText);
        return text;
    }



    private Text CreateWideLabel(Transform parent, string name, string initialText, float anchorX, float anchorY, int fontSize, TextAnchor anchor)
    {
        RectTransform rect = RuntimeUiElementFactory.CreateRectChild(name, parent, out GameObject obj);
        Vector2 anchorPoint = new Vector2(anchorX, anchorY);
        TransformWriter.ApplyAnchoredRect(rect, anchorPoint, anchorPoint, Vector2.zero, new Vector2(760f, 64f));

        Text text = RuntimeUiElementFactory.AddText(obj);
        UiComponentWriter.ApplyTextStyle(text, GetRuntimeUiFont(), fontSize, anchor, Color.white);
        UiComponentWriter.ApplyTextOverflow(text, HorizontalWrapMode.Wrap, VerticalWrapMode.Truncate);
        UiComponentWriter.ApplyTextContent(text, initialText);
        return text;
    }



    // y は行の**ピクセル座標**（中心原点）。以前は 0..1 の割合で、幅 520 を中央に置いていたので
    // 左のラベルと右の値の両方に食い込んでいた。操作列の内側に収める。
    private Slider CreateSlider(Transform parent, string name, float y)
    {
        RectTransform sliderRect = RuntimeUiElementFactory.CreateRectChild(name, parent, out GameObject sliderObj);
        TransformWriter.ApplyCenteredRect(
            sliderRect, new Vector2(SettingsControlCenterX, y), new Vector2(SettingsControlWidth, 44f));

        Image background = RuntimeUiElementFactory.AddImage(sliderObj);
        UiComponentWriter.ApplyGraphicColor(background, new Color(1f, 1f, 1f, 0.2f));
        Slider slider = RuntimeUiElementFactory.AddSlider(sliderObj);
        UiComponentWriter.ApplySliderDirection(slider, Slider.Direction.LeftToRight);
        UiComponentWriter.ApplyTargetGraphic(slider, background);

        RectTransform fillAreaRect = RuntimeUiElementFactory.CreateRectChild("Fill Area", sliderObj.transform, out GameObject fillArea);
        TransformWriter.ApplyStretchRect(
            fillAreaRect,
            new Vector2(0f, 0.25f),
            new Vector2(1f, 0.75f),
            new Vector2(25f, 0f),
            new Vector2(-25f, 0f));

        GameObject fillObj = RuntimeUiElementFactory.CreateChild("Fill", fillArea.transform);
        Image fillImage = RuntimeUiElementFactory.AddImage(fillObj);
        UiComponentWriter.ApplyGraphicColor(fillImage, new Color(0.22f, 0.72f, 1f, 0.95f));
        RectTransform fillRect = fillObj.GetComponent<RectTransform>();
        TransformWriter.ApplyStretchRect(
            fillRect,
            new Vector2(0f, 0f),
            new Vector2(1f, 1f),
            Vector2.zero,
            Vector2.zero);

        RectTransform handleAreaRect = RuntimeUiElementFactory.CreateRectChild("Handle Slide Area", sliderObj.transform, out GameObject handleArea);
        TransformWriter.ApplyStretchRect(
            handleAreaRect,
            Vector2.zero,
            Vector2.one,
            new Vector2(20f, 0f),
            new Vector2(-20f, 0f));

        GameObject handleObj = RuntimeUiElementFactory.CreateChild("Handle", handleArea.transform);
        Image handleImage = RuntimeUiElementFactory.AddImage(handleObj);
        UiComponentWriter.ApplyGraphicColor(handleImage, new Color(0.95f, 0.95f, 0.95f, 1f));
        RectTransform handleRect = handleObj.GetComponent<RectTransform>();
        TransformWriter.ApplySizeDelta(handleRect, new Vector2(26f, 56f));

        UiComponentWriter.ApplySliderRects(slider, fillRect, handleRect);

        return slider;
    }



    private Button CreateSmallButton(Transform parent, string name, Vector2 anchoredPos, string label)
    {
        RectTransform buttonRect = RuntimeUiElementFactory.CreateRectChild(name, parent, out GameObject buttonObj);
        TransformWriter.ApplyCenteredRect(buttonRect, anchoredPos, new Vector2(110f, 64f));

        Image buttonImage = RuntimeUiElementFactory.AddImage(buttonObj);
        UiComponentWriter.ApplyGraphicColor(buttonImage, new Color(0.13f, 0.13f, 0.13f, 0.9f));
        Button button = RuntimeUiElementFactory.AddButton(buttonObj);
        UiComponentWriter.ApplyTargetGraphic(button, buttonImage);

        RectTransform textRect = RuntimeUiElementFactory.CreateRectChild("Label", buttonObj.transform, out GameObject textObj);
        TransformWriter.ApplyStretchRect(
            textRect,
            Vector2.zero,
            Vector2.one,
            Vector2.zero,
            Vector2.zero);

        Text text = RuntimeUiElementFactory.AddText(textObj);
        UiComponentWriter.ApplyTextStyle(text, GetRuntimeUiFont(), 34, TextAnchor.MiddleCenter, Color.white);
        UiComponentWriter.ApplyTextContent(text, label);
        return button;
    }



    private void InitializeRuntimeFovxIfNeeded()
    {
        if (runtimeFovxInitialized)
        {
            return;
        }

        runtimeFovxInitialized = true;
        // Always initialize from bundle metadata first so the first shown value
        // matches current content regardless of inspector override leftovers.
        float initial = metaHeader.fovxDeg;
        if (initial <= 0f)
        {
            initial = GetManifestFovxDeg();
        }
        if (initial <= 0f)
        {
            initial = RuntimeFovxDefaultDeg;
        }

        runtimeFovxDeg = ClampRuntimeFovx(initial);
        useRuntimeFovxOverride = false;
    }



    private float ClampRuntimeFovx(float value)
    {
        float min = Mathf.Min(RuntimeFovxMinDeg, RuntimeFovxMaxDeg);
        float max = Mathf.Max(RuntimeFovxMinDeg, RuntimeFovxMaxDeg);
        return Mathf.Clamp(value, min, max);
    }



    private float ClampRuntimeScreenDistance(float value)
    {
        float min = Mathf.Min(RuntimeScreenDistanceMinMeters, RuntimeScreenDistanceMaxMeters);
        float max = Mathf.Max(RuntimeScreenDistanceMinMeters, RuntimeScreenDistanceMaxMeters);
        return Mathf.Clamp(value, min, max);
    }



    private void UpdateFovxSliderRange()
    {
        if (runtimeFovxSlider == null)
        {
            return;
        }

        float min = Mathf.Min(RuntimeFovxMinDeg, RuntimeFovxMaxDeg);
        float max = Mathf.Max(RuntimeFovxMinDeg, RuntimeFovxMaxDeg);
        UiComponentWriter.ApplySliderRange(runtimeFovxSlider, min, max);
    }



    private void UpdateRuntimeScreenDistanceSliderRange()
    {
        if (runtimeScreenDistanceSlider == null)
        {
            return;
        }

        float min = Mathf.Min(RuntimeScreenDistanceMinMeters, RuntimeScreenDistanceMaxMeters);
        float max = Mathf.Max(RuntimeScreenDistanceMinMeters, RuntimeScreenDistanceMaxMeters);
        UiComponentWriter.ApplySliderRange(runtimeScreenDistanceSlider, min, max);
    }



    private void OnRuntimeFovxSliderChanged(float value)
    {
        runtimeFovxDeg = ClampRuntimeFovx(value);
        useRuntimeFovxOverride = true;
        UpdateRuntimeFovxText(runtimeFovxDeg);

        if (fitScreenToFov)
        {
            PlaceScreensWithoutMovingSettings();
        }
    }



    private void OnRuntimeScreenDistanceSliderChanged(float value)
    {
        if (suppressRuntimeScreenDistanceCallback)
        {
            return;
        }

        screenDistanceMeters = ClampRuntimeScreenDistance(value);
        UpdateRuntimeScreenDistanceText(screenDistanceMeters);
        ReleaseLockedScalesForViewingChange();
        PlaceScreensWithoutMovingSettings();
    }



    private void UpdateRuntimeFovxText(float value)
    {
        if (runtimeFovxValueText == null)
        {
            return;
        }

        UiComponentWriter.ApplyTextContent(runtimeFovxValueText, value.ToString("F1") + " deg");
    }



    private void UpdateRuntimeScreenDistanceText(float value)
    {
        if (runtimeScreenDistanceValueText == null)
        {
            return;
        }

        UiComponentWriter.ApplyTextContent(runtimeScreenDistanceValueText, value.ToString("F2") + " m");
    }



    // スクリーン距離を変えたら、確定済みの表示スケールを捨てる。
    //
    // **見かけの大きさは距離を変えても変わらないのが正しい。**
    // モデルは映像の対象に張り付いているので、スクリーンが遠ざかればモデルも
    // 遠ざかり、同じ画角を占める。DecodeAnchorDepthMetersFromBundle は
    // zPlacement = screenDist - eps - popout なので深度は追従する。
    //
    // ところが Human / Animal のスケールは shot 先頭で凍らせてある
    // （GetOrLockModelLocalScale）。深度だけ動いてスケールが据え置かれるので、
    // **人だけ大きさが変わって見えていた**
    // （2026-09-05 実機報告: Else と動画本体は変わらず Human だけ変わる）。
    // ロックを外せば次のフレームで新しい距離に合わせて張り直される。
    //
    // **補正倍率（⑨）は捨てない。** あれは track の姿勢に対する比率で、
    // 視点の距離とは無関係。捨てるとその瞬間の姿勢で測り直され、
    // モデル差し替えで踏んだのと同じ跦ねが起きる。
    private void ReleaseLockedScalesForViewingChange()
    {
        lockedModelLocalScaleByTrack.Clear();
        scaleRefinedByTrack.Clear();
    }


    private void ToggleRuntimeSettingsPanel()
    {
        runtimeSettingsOpen = !runtimeSettingsOpen;
        if (runtimeSettingsRoot != null)
        {
            SceneObjectWriter.ApplyActive(runtimeSettingsRoot, runtimeSettingsOpen && enableRuntimeControls);
            SetScreenColliderBlockForRuntimePanels();
            if (runtimeSettingsOpen)
            {
                if (runtimeModelPickerOpen)
                {
                    CloseRuntimeModelPickerPanel();
                }
                UpdateRuntimeSettingsPlacement();
                UpdateRuntimeScreenDistanceUiState();
                UpdateRuntimeTrackRotationUiState();
            }
        }
        UpdateSettingsButtonLabel();
    }



    private void SetScreenColliderBlockForRuntimePanels()
    {
        SetScreenColliderBlockForSettings(enableRuntimeControls && (runtimeSettingsOpen || runtimeModelPickerOpen));
    }



    private void SetScreenColliderBlockForSettings(bool settingsOpen)
    {
        // Screen colliders can steal controller/UI rays from the settings canvas.
        bool colliderEnabled = !settingsOpen;
        SetColliderEnabled(leftScreen, colliderEnabled);
        SetColliderEnabled(rightScreen, colliderEnabled);
    }



    private static void SetColliderEnabled(Transform target, bool enabled)
    {
        if (target == null)
        {
            return;
        }

        Collider[] colliders = target.GetComponents<Collider>();
        for (int i = 0; i < colliders.Length; i++)
        {
            Collider collider = colliders[i];
            if (collider != null)
            {
                SceneObjectWriter.ApplyColliderEnabled(collider, enabled);
            }
        }
    }



    private void UpdateSettingsButtonLabel()
    {
        if (runtimeSettingsButtonText == null)
        {
            return;
        }

        UiComponentWriter.ApplyTextContent(runtimeSettingsButtonText, "Settings");
    }



    // パネル位置を、頭から見て前後にずらす。回転は頭を向いたままでよいので触らない
    // （同じ視線上を滑らせるだけなので向きは変わらない）。
    private Vector3 ApplyRuntimePanelDistanceOffset(Vector3 position)
    {
        if (Mathf.Abs(runtimePanelDistanceOffsetMeters) < 0.0001f)
        {
            return position;
        }

        Transform head = GetViewOrHeadTransform();
        if (head == null)
        {
            return position;
        }

        Vector3 away = position - head.position;
        if (away.sqrMagnitude < 0.0001f)
        {
            return position;
        }

        // 近づけすぎて頭に刺さらないよう、最低 0.35m は残す。
        float current = away.magnitude;
        // 手が届く距離まで寄せたいので下限を 0.35m から 0.25m へ（2026-09-02 の要望）。
        float target = Mathf.Max(0.25f, current + runtimePanelDistanceOffsetMeters);
        return head.position + away.normalized * target;
    }


    // **上下のドラッグ量では距離を変えない。** 「上へ動かす＝遠ざかる」は対応がねじれていて、
    // VR では手を前後させるほうが自然（2026-08-31 の相談）。
    // UI のドラッグイベントは 2D の差分しか持たないので、掴んでいる間だけ
    // コントローラの姿勢を直接読み、頭→パネル方向への移動量をそのまま距離にする。
    private void OnRuntimePanelDragStateChanged(bool dragging)
    {
        runtimePanelDragActive = dragging;
        if (!dragging)
        {
            Debug.Log($"[PANELDRAG] 離した offset={runtimePanelDistanceOffsetMeters:F3}m");
            return;
        }

        runtimePanelDragStartOffset = runtimePanelDistanceOffsetMeters;
        bool gotPointer = TryReadRuntimePanelPointerPosition(out Vector3 p);
        runtimePanelDragStartPointer = gotPointer ? p : Vector3.zero;
        // 掴めているか・コントローラの位置が取れているかを 1 行で分かるようにする。
        // 実機でしか動かない経路なので、これが無いと切り分けができない。
        Debug.Log($"[PANELDRAG] 掴んだ pointerOK={gotPointer} start={runtimePanelDragStartPointer:F3} offset={runtimePanelDragStartOffset:F3}m");
    }


    private void UpdateRuntimePanelDrag()
    {
        if (!runtimePanelDragActive || !TryReadRuntimePanelPointerPosition(out Vector3 pointer))
        {
            return;
        }

        Transform head = GetViewOrHeadTransform();
        if (head == null)
        {
            return;
        }

        // 頭から見た「奥へ」の向き。パネルは水平方向に置くので水平面へ射影する。
        Vector3 away = Vector3.ProjectOnPlane(head.forward, Vector3.up);
        if (away.sqrMagnitude < 0.000001f)
        {
            return;
        }

        float moved = Vector3.Dot(pointer - runtimePanelDragStartPointer, away.normalized);
        float before = runtimePanelDistanceOffsetMeters;
        runtimePanelDistanceOffsetMeters = Mathf.Clamp(
            runtimePanelDragStartOffset + moved * RuntimePanelDragGain,
            RuntimePanelDistanceOffsetMin,
            RuntimePanelDistanceOffsetMax);

        // 位置の再計算はパネル側の毎フレーム更新に任せられない
        // （UpdateRuntimeControlsPlacement は PlaceScreens 経由で、毎フレームとは限らない）。
        // 動かした本人がその場で反映する。
        UpdateRuntimeSettingsPlacement();
        UpdateRuntimeModelPickerPlacement();

        // 掴んでいる間は 0.5 秒ごとに必ず 1 行出す。動いていないのか、そもそも
        // ここまで到達していないのかを実機ログで区別するため。
        if (Time.unscaledTime - runtimePanelDragLoggedAt >= 0.5f ||
            Mathf.Abs(runtimePanelDistanceOffsetMeters - runtimePanelDragLoggedOffset) >= 0.02f)
        {
            runtimePanelDragLoggedAt = Time.unscaledTime;
            runtimePanelDragLoggedOffset = runtimePanelDistanceOffsetMeters;
            Debug.Log($"[PANELDRAG] moved={moved:F3}m offset={before:F3} -> {runtimePanelDistanceOffsetMeters:F3}m pointer={pointer:F3}");
        }
    }


    private bool TryReadRuntimePanelPointerPosition(out Vector3 position)
    {
        position = Vector3.zero;
        if (!RuntimeXrRayPickReader.TryReadPointerPose(
                xrInputDevices, out Vector3 local, out Quaternion _, out bool _))
        {
            return false;
        }

        // コントローラの姿勢はトラッキング空間なので、リグの姿勢を掛けて world にする。
        Transform head = GetViewOrHeadTransform();
        if (head == null)
        {
            position = local;
            return true;
        }

        if (!RuntimeXrRayPickReader.TryReadHeadPose(xrInputDevices, out Vector3 headLocal, out Quaternion headLocalRot))
        {
            position = local;
            return true;
        }

        Quaternion rigRotation = head.rotation * Quaternion.Inverse(headLocalRot);
        position = head.position + rigRotation * (local - headLocal);
        return true;
    }


    // パネル上端の掴み代。ここをドラッグすると前後に動く。
    private void CreateRuntimePanelDragHandle(Transform parent, string name, Vector2 anchoredPos, Vector2 size)
    {
        RectTransform rect = RuntimeUiElementFactory.CreateRectChild(name, parent, out GameObject obj);
        TransformWriter.ApplyCenteredRect(rect, anchoredPos, size);

        Image image = RuntimeUiElementFactory.AddImage(obj);
        UiComponentWriter.ApplyGraphicColor(image, new Color(0.30f, 0.34f, 0.40f, 0.55f));

        RectTransform textRect = RuntimeUiElementFactory.CreateRectChild("Label", obj.transform, out GameObject textObj);
        TransformWriter.ApplyStretchRect(textRect, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
        Text text = RuntimeUiElementFactory.AddText(textObj);
        UiComponentWriter.ApplyTextStyle(text, GetRuntimeUiFont(), 22, TextAnchor.MiddleCenter, new Color(0.85f, 0.9f, 1f, 1f));
        UiComponentWriter.ApplyTextContent(text, "= hold here and move your hand to push / pull =");

        RuntimePanelDragHandle handle = obj.AddComponent<RuntimePanelDragHandle>();
        handle.onDragStateChanged = OnRuntimePanelDragStateChanged;
    }


    private void EnsureEventSystem()
    {
#if UNITY_2023_1_OR_NEWER
        EventSystem eventSystem = Object.FindFirstObjectByType<EventSystem>();
#else
        EventSystem eventSystem = Object.FindObjectOfType<EventSystem>();
#endif
        RuntimeEventSystemFactory.Ensure(eventSystem);
    }



    private void EnsureCanvasRaycasters(GameObject root)
    {
        if (root == null)
        {
            return;
        }

        Canvas[] canvases = root.GetComponentsInChildren<Canvas>(true);
        for (int i = 0; i < canvases.Length; i++)
        {
            Canvas canvas = canvases[i];
            if (canvas == null)
            {
                continue;
            }

            GameObject canvasGo = canvas.gameObject;
            // Settings panel is rotated 180deg in this scene setup.
            // Accept reversed graphics so slider/button raycasts still hit.
            RuntimeCanvasComponentFactory.EnsureGraphicRaycaster(canvasGo, false);

            System.Type trackedRaycasterType = RuntimeTrackedDeviceGraphicRaycasterResolver.Resolve();
            if (trackedRaycasterType != null && canvasGo.GetComponent(trackedRaycasterType) == null)
            {
                RuntimeCanvasComponentFactory.EnsureComponent(canvasGo, trackedRaycasterType);
            }
        }
    }



}
