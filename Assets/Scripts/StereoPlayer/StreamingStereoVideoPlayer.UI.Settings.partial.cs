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

        CreateLabel(panelObj.transform, "Title", "Settings", 0.5f, 0.88f, 64, TextAnchor.MiddleCenter);
        CreateLabel(panelObj.transform, "FovLabel", "FOVx", 0.12f, 0.55f, 48, TextAnchor.MiddleLeft);
        runtimeFovxValueText = CreateLabel(panelObj.transform, "FovValue", string.Empty, 0.88f, 0.55f, 44, TextAnchor.MiddleRight);

        runtimeFovxSlider = CreateSlider(panelObj.transform, "FovxSlider", 0.55f);
        if (runtimeFovxSlider != null)
        {
            UnbindRuntimeSlider(runtimeFovxSlider, OnRuntimeFovxSliderChanged);
            UpdateFovxSliderRange();
            UiComponentWriter.ApplySliderValueWithoutNotify(runtimeFovxSlider, runtimeFovxDeg);
            BindRuntimeSlider(runtimeFovxSlider, OnRuntimeFovxSliderChanged);
        }
        UpdateRuntimeFovxText(runtimeFovxDeg);

        CreateLabel(panelObj.transform, "ScreenDistLabel", "Screen Dist", 0.12f, 0.43f, 42, TextAnchor.MiddleLeft);
        runtimeScreenDistanceValueText = CreateLabel(panelObj.transform, "ScreenDistValue", string.Empty, 0.88f, 0.43f, 38, TextAnchor.MiddleRight);
        runtimeScreenDistanceSlider = CreateSlider(panelObj.transform, "ScreenDistanceSlider", 0.43f);
        if (runtimeScreenDistanceSlider != null)
        {
            UnbindRuntimeSlider(runtimeScreenDistanceSlider, OnRuntimeScreenDistanceSliderChanged);
            UpdateRuntimeScreenDistanceSliderRange();
            UiComponentWriter.ApplySliderValueWithoutNotify(runtimeScreenDistanceSlider, ClampRuntimeScreenDistance(screenDistanceMeters));
            BindRuntimeSlider(runtimeScreenDistanceSlider, OnRuntimeScreenDistanceSliderChanged);
        }
        UpdateRuntimeScreenDistanceText(screenDistanceMeters);

        CreateLabel(panelObj.transform, "TrackLabel", "Track", 0.12f, 0.28f, 44, TextAnchor.MiddleLeft);
        runtimeTrackSelectionText = CreateLabel(panelObj.transform, "TrackValue", "none", 0.88f, 0.28f, 40, TextAnchor.MiddleRight);
        runtimeTrackFrontGuideText = CreateWideLabel(panelObj.transform, "TrackFrontGuide", "Arrow above head = FRONT  |  +:left  -:right", 0.5f, 0.36f, 24, TextAnchor.MiddleCenter);
        runtimeTrackKeyInfoText = CreateWideLabel(panelObj.transform, "TrackKeyInfo", "Keys:0  Frame:0", 0.5f, 0.05f, 24, TextAnchor.MiddleCenter);

        Button prevTrack = CreateSmallButton(panelObj.transform, "TrackPrevButton", new Vector2(-210f, -115f), "<");
        BindRuntimeButton(prevTrack, OnRuntimeTrackPrevClicked);

        Button nextTrack = CreateSmallButton(panelObj.transform, "TrackNextButton", new Vector2(-90f, -115f), ">");
        BindRuntimeButton(nextTrack, OnRuntimeTrackNextClicked);

        Button resetYaw = CreateSmallButton(panelObj.transform, "TrackYawResetButton", new Vector2(210f, -115f), "Reset");
        BindRuntimeButton(resetYaw, OnRuntimeTrackYawResetClicked);

        CreateLabel(panelObj.transform, "InteractiveMotionLabel", "Motion", 0.12f, 0.70f, 40, TextAnchor.MiddleLeft);
        runtimeInteractiveMotionValueText = CreateLabel(panelObj.transform, "InteractiveMotionValue", string.Empty, 0.72f, 0.70f, 36, TextAnchor.MiddleRight);
        Button motionToggle = CreateSmallButton(panelObj.transform, "InteractiveMotionToggleButton", new Vector2(315f, 105f), "Toggle");
        BindRuntimeButton(motionToggle, OnRuntimeInteractiveMotionToggleClicked);

        CreateLabel(panelObj.transform, "YawLabel", "Yaw", 0.12f, 0.12f, 44, TextAnchor.MiddleLeft);
        runtimeTrackYawValueText = CreateLabel(panelObj.transform, "YawValue", "0.0 deg", 0.88f, 0.12f, 40, TextAnchor.MiddleRight);
        runtimeTrackYawSlider = CreateSlider(panelObj.transform, "TrackYawSlider", 0.12f);
        if (runtimeTrackYawSlider != null)
        {
            UiComponentWriter.ApplySliderRange(runtimeTrackYawSlider, -180f, 180f);
            UiComponentWriter.ApplySliderValueWithoutNotify(runtimeTrackYawSlider, 0f);
            BindRuntimeSlider(runtimeTrackYawSlider, OnRuntimeTrackYawSliderChanged);
        }

        UpdateRuntimeTrackRotationUiState();
        UpdateRuntimeInteractiveMotionUiState();

        return settingsRootObj;
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



    private Slider CreateSlider(Transform parent, string name, float anchorY)
    {
        RectTransform sliderRect = RuntimeUiElementFactory.CreateRectChild(name, parent, out GameObject sliderObj);
        Vector2 sliderAnchor = new Vector2(0.5f, anchorY);
        TransformWriter.ApplyAnchoredRect(sliderRect, sliderAnchor, sliderAnchor, Vector2.zero, new Vector2(520f, 60f));

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
