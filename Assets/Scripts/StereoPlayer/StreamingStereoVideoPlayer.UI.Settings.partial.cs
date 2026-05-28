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
            settingsRootObj = Instantiate(runtimeControlsPrefab);
            settingsRootObj.name = "RuntimeSettingsPanel";
        }
        else
        {
            settingsRootObj = new GameObject("RuntimeSettingsPanel");
            Canvas canvas = settingsRootObj.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.worldCamera = GetViewCamera();
            settingsRootObj.AddComponent<GraphicRaycaster>();
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
            uiRect.sizeDelta = new Vector2(RuntimeSettingsDefaultCanvasWidth, RuntimeSettingsDefaultCanvasHeight);
            uiRect.localScale = new Vector3(
                SettingsPanelSizeMeters.x / RuntimeSettingsDefaultCanvasWidth,
                SettingsPanelSizeMeters.y / RuntimeSettingsDefaultCanvasHeight,
                1f);
        }

        GameObject panelObj = new GameObject("Panel");
        panelObj.transform.SetParent(uiRoot, false);
        RectTransform panelRect = panelObj.AddComponent<RectTransform>();
        panelRect.anchorMin = Vector2.zero;
        panelRect.anchorMax = Vector2.one;
        panelRect.offsetMin = Vector2.zero;
        panelRect.offsetMax = Vector2.zero;
        Image panelImage = panelObj.AddComponent<Image>();
        panelImage.color = new Color(0f, 0f, 0f, 0.65f);

        CreateLabel(panelObj.transform, "Title", "Settings", 0.5f, 0.88f, 64, TextAnchor.MiddleCenter);
        CreateLabel(panelObj.transform, "FovLabel", "FOVx", 0.12f, 0.55f, 48, TextAnchor.MiddleLeft);
        runtimeFovxValueText = CreateLabel(panelObj.transform, "FovValue", string.Empty, 0.88f, 0.55f, 44, TextAnchor.MiddleRight);

        runtimeFovxSlider = CreateSlider(panelObj.transform, "FovxSlider", 0.55f);
        if (runtimeFovxSlider != null)
        {
            runtimeFovxSlider.onValueChanged.RemoveListener(OnRuntimeFovxSliderChanged);
            UpdateFovxSliderRange();
            runtimeFovxSlider.SetValueWithoutNotify(runtimeFovxDeg);
            runtimeFovxSlider.onValueChanged.AddListener(OnRuntimeFovxSliderChanged);
        }
        UpdateRuntimeFovxText(runtimeFovxDeg);

        CreateLabel(panelObj.transform, "ScreenDistLabel", "Screen Dist", 0.12f, 0.43f, 42, TextAnchor.MiddleLeft);
        runtimeScreenDistanceValueText = CreateLabel(panelObj.transform, "ScreenDistValue", string.Empty, 0.88f, 0.43f, 38, TextAnchor.MiddleRight);
        runtimeScreenDistanceSlider = CreateSlider(panelObj.transform, "ScreenDistanceSlider", 0.43f);
        if (runtimeScreenDistanceSlider != null)
        {
            runtimeScreenDistanceSlider.onValueChanged.RemoveListener(OnRuntimeScreenDistanceSliderChanged);
            UpdateRuntimeScreenDistanceSliderRange();
            runtimeScreenDistanceSlider.SetValueWithoutNotify(ClampRuntimeScreenDistance(screenDistanceMeters));
            runtimeScreenDistanceSlider.onValueChanged.AddListener(OnRuntimeScreenDistanceSliderChanged);
        }
        UpdateRuntimeScreenDistanceText(screenDistanceMeters);

        CreateLabel(panelObj.transform, "TrackLabel", "Track", 0.12f, 0.28f, 44, TextAnchor.MiddleLeft);
        runtimeTrackSelectionText = CreateLabel(panelObj.transform, "TrackValue", "none", 0.88f, 0.28f, 40, TextAnchor.MiddleRight);
        runtimeTrackFrontGuideText = CreateWideLabel(panelObj.transform, "TrackFrontGuide", "Arrow above head = FRONT  |  +:left  -:right", 0.5f, 0.36f, 24, TextAnchor.MiddleCenter);
        runtimeTrackKeyInfoText = CreateWideLabel(panelObj.transform, "TrackKeyInfo", "Keys:0  Frame:0", 0.5f, 0.05f, 24, TextAnchor.MiddleCenter);

        Button prevTrack = CreateSmallButton(panelObj.transform, "TrackPrevButton", new Vector2(-210f, -115f), "<");
        prevTrack.onClick.RemoveListener(OnRuntimeTrackPrevClicked);
        prevTrack.onClick.AddListener(OnRuntimeTrackPrevClicked);

        Button nextTrack = CreateSmallButton(panelObj.transform, "TrackNextButton", new Vector2(-90f, -115f), ">");
        nextTrack.onClick.RemoveListener(OnRuntimeTrackNextClicked);
        nextTrack.onClick.AddListener(OnRuntimeTrackNextClicked);

        Button resetYaw = CreateSmallButton(panelObj.transform, "TrackYawResetButton", new Vector2(210f, -115f), "Reset");
        resetYaw.onClick.RemoveListener(OnRuntimeTrackYawResetClicked);
        resetYaw.onClick.AddListener(OnRuntimeTrackYawResetClicked);

        CreateLabel(panelObj.transform, "InteractiveMotionLabel", "Motion", 0.12f, 0.70f, 40, TextAnchor.MiddleLeft);
        runtimeInteractiveMotionValueText = CreateLabel(panelObj.transform, "InteractiveMotionValue", string.Empty, 0.72f, 0.70f, 36, TextAnchor.MiddleRight);
        Button motionToggle = CreateSmallButton(panelObj.transform, "InteractiveMotionToggleButton", new Vector2(315f, 105f), "Toggle");
        motionToggle.onClick.RemoveListener(OnRuntimeInteractiveMotionToggleClicked);
        motionToggle.onClick.AddListener(OnRuntimeInteractiveMotionToggleClicked);

        CreateLabel(panelObj.transform, "YawLabel", "Yaw", 0.12f, 0.12f, 44, TextAnchor.MiddleLeft);
        runtimeTrackYawValueText = CreateLabel(panelObj.transform, "YawValue", "0.0 deg", 0.88f, 0.12f, 40, TextAnchor.MiddleRight);
        runtimeTrackYawSlider = CreateSlider(panelObj.transform, "TrackYawSlider", 0.12f);
        if (runtimeTrackYawSlider != null)
        {
            runtimeTrackYawSlider.minValue = -180f;
            runtimeTrackYawSlider.maxValue = 180f;
            runtimeTrackYawSlider.SetValueWithoutNotify(0f);
            runtimeTrackYawSlider.onValueChanged.RemoveListener(OnRuntimeTrackYawSliderChanged);
            runtimeTrackYawSlider.onValueChanged.AddListener(OnRuntimeTrackYawSliderChanged);
        }

        UpdateRuntimeTrackRotationUiState();
        UpdateRuntimeInteractiveMotionUiState();

        return settingsRootObj;
    }



    private Text CreateLabel(Transform parent, string name, string initialText, float anchorX, float anchorY, int fontSize, TextAnchor anchor)
    {
        GameObject obj = new GameObject(name);
        obj.transform.SetParent(parent, false);
        RectTransform rect = obj.AddComponent<RectTransform>();
        rect.anchorMin = new Vector2(anchorX, anchorY);
        rect.anchorMax = new Vector2(anchorX, anchorY);
        rect.sizeDelta = new Vector2(280f, 90f);
        rect.anchoredPosition = Vector2.zero;

        Text text = obj.AddComponent<Text>();
        text.font = GetRuntimeUiFont();
        text.fontSize = fontSize;
        text.alignment = anchor;
        text.color = Color.white;
        text.text = initialText;
        return text;
    }



    private Text CreateWideLabel(Transform parent, string name, string initialText, float anchorX, float anchorY, int fontSize, TextAnchor anchor)
    {
        GameObject obj = new GameObject(name);
        obj.transform.SetParent(parent, false);
        RectTransform rect = obj.AddComponent<RectTransform>();
        rect.anchorMin = new Vector2(anchorX, anchorY);
        rect.anchorMax = new Vector2(anchorX, anchorY);
        rect.sizeDelta = new Vector2(760f, 64f);
        rect.anchoredPosition = Vector2.zero;

        Text text = obj.AddComponent<Text>();
        text.font = GetRuntimeUiFont();
        text.fontSize = fontSize;
        text.alignment = anchor;
        text.color = Color.white;
        text.horizontalOverflow = HorizontalWrapMode.Wrap;
        text.verticalOverflow = VerticalWrapMode.Truncate;
        text.text = initialText;
        return text;
    }



    private Slider CreateSlider(Transform parent, string name, float anchorY)
    {
        GameObject sliderObj = new GameObject(name);
        sliderObj.transform.SetParent(parent, false);
        RectTransform sliderRect = sliderObj.AddComponent<RectTransform>();
        sliderRect.anchorMin = new Vector2(0.5f, anchorY);
        sliderRect.anchorMax = new Vector2(0.5f, anchorY);
        sliderRect.sizeDelta = new Vector2(520f, 60f);
        sliderRect.anchoredPosition = Vector2.zero;

        Image background = sliderObj.AddComponent<Image>();
        background.color = new Color(1f, 1f, 1f, 0.2f);
        Slider slider = sliderObj.AddComponent<Slider>();
        slider.direction = Slider.Direction.LeftToRight;
        slider.targetGraphic = background;

        GameObject fillArea = new GameObject("Fill Area");
        fillArea.transform.SetParent(sliderObj.transform, false);
        RectTransform fillAreaRect = fillArea.AddComponent<RectTransform>();
        fillAreaRect.anchorMin = new Vector2(0f, 0.25f);
        fillAreaRect.anchorMax = new Vector2(1f, 0.75f);
        fillAreaRect.offsetMin = new Vector2(25f, 0f);
        fillAreaRect.offsetMax = new Vector2(-25f, 0f);

        GameObject fillObj = new GameObject("Fill");
        fillObj.transform.SetParent(fillArea.transform, false);
        Image fillImage = fillObj.AddComponent<Image>();
        fillImage.color = new Color(0.22f, 0.72f, 1f, 0.95f);
        RectTransform fillRect = fillObj.GetComponent<RectTransform>();
        fillRect.anchorMin = new Vector2(0f, 0f);
        fillRect.anchorMax = new Vector2(1f, 1f);
        fillRect.offsetMin = Vector2.zero;
        fillRect.offsetMax = Vector2.zero;

        GameObject handleArea = new GameObject("Handle Slide Area");
        handleArea.transform.SetParent(sliderObj.transform, false);
        RectTransform handleAreaRect = handleArea.AddComponent<RectTransform>();
        handleAreaRect.anchorMin = Vector2.zero;
        handleAreaRect.anchorMax = Vector2.one;
        handleAreaRect.offsetMin = new Vector2(20f, 0f);
        handleAreaRect.offsetMax = new Vector2(-20f, 0f);

        GameObject handleObj = new GameObject("Handle");
        handleObj.transform.SetParent(handleArea.transform, false);
        Image handleImage = handleObj.AddComponent<Image>();
        handleImage.color = new Color(0.95f, 0.95f, 0.95f, 1f);
        RectTransform handleRect = handleObj.GetComponent<RectTransform>();
        handleRect.sizeDelta = new Vector2(26f, 56f);

        slider.fillRect = fillRect;
        slider.handleRect = handleRect;

        return slider;
    }



    private Button CreateSmallButton(Transform parent, string name, Vector2 anchoredPos, string label)
    {
        GameObject buttonObj = new GameObject(name);
        buttonObj.transform.SetParent(parent, false);
        RectTransform buttonRect = buttonObj.AddComponent<RectTransform>();
        buttonRect.anchorMin = new Vector2(0.5f, 0.5f);
        buttonRect.anchorMax = new Vector2(0.5f, 0.5f);
        buttonRect.sizeDelta = new Vector2(110f, 64f);
        buttonRect.anchoredPosition = anchoredPos;

        Image buttonImage = buttonObj.AddComponent<Image>();
        buttonImage.color = new Color(0.13f, 0.13f, 0.13f, 0.9f);
        Button button = buttonObj.AddComponent<Button>();
        button.targetGraphic = buttonImage;

        GameObject textObj = new GameObject("Label");
        textObj.transform.SetParent(buttonObj.transform, false);
        RectTransform textRect = textObj.AddComponent<RectTransform>();
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = Vector2.zero;
        textRect.offsetMax = Vector2.zero;

        Text text = textObj.AddComponent<Text>();
        text.alignment = TextAnchor.MiddleCenter;
        text.color = Color.white;
        text.fontSize = 34;
        text.font = GetRuntimeUiFont();
        text.text = label;
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
        runtimeFovxSlider.minValue = min;
        runtimeFovxSlider.maxValue = max;
    }



    private void UpdateRuntimeScreenDistanceSliderRange()
    {
        if (runtimeScreenDistanceSlider == null)
        {
            return;
        }

        float min = Mathf.Min(RuntimeScreenDistanceMinMeters, RuntimeScreenDistanceMaxMeters);
        float max = Mathf.Max(RuntimeScreenDistanceMinMeters, RuntimeScreenDistanceMaxMeters);
        runtimeScreenDistanceSlider.minValue = min;
        runtimeScreenDistanceSlider.maxValue = max;
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

        runtimeFovxValueText.text = value.ToString("F1") + " deg";
    }



    private void UpdateRuntimeScreenDistanceText(float value)
    {
        if (runtimeScreenDistanceValueText == null)
        {
            return;
        }

        runtimeScreenDistanceValueText.text = value.ToString("F2") + " m";
    }



    private void ToggleRuntimeSettingsPanel()
    {
        runtimeSettingsOpen = !runtimeSettingsOpen;
        if (runtimeSettingsRoot != null)
        {
            runtimeSettingsRoot.SetActive(runtimeSettingsOpen && enableRuntimeControls);
            SetScreenColliderBlockForSettings(runtimeSettingsOpen && enableRuntimeControls);
            if (runtimeSettingsOpen)
            {
                UpdateRuntimeSettingsPlacement();
                UpdateRuntimeScreenDistanceUiState();
                UpdateRuntimeTrackRotationUiState();
            }
        }
        UpdateSettingsButtonLabel();
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
                collider.enabled = enabled;
            }
        }
    }



    private void UpdateSettingsButtonLabel()
    {
        if (runtimeSettingsButtonText == null)
        {
            return;
        }

        runtimeSettingsButtonText.text = runtimeSettingsOpen ? "Close" : "Settings";
    }



    private void EnsureEventSystem()
    {
#if UNITY_2023_1_OR_NEWER
        EventSystem eventSystem = Object.FindFirstObjectByType<EventSystem>();
#else
        EventSystem eventSystem = Object.FindObjectOfType<EventSystem>();
#endif
        if (eventSystem == null)
        {
            GameObject eventSystemObj = new GameObject("EventSystem");
            eventSystem = eventSystemObj.AddComponent<EventSystem>();
        }

        if (eventSystem.GetComponent<InputSystemUIInputModule>() == null)
        {
            eventSystem.gameObject.AddComponent<InputSystemUIInputModule>();
        }
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
            GraphicRaycaster raycaster = canvasGo.GetComponent<GraphicRaycaster>();
            if (raycaster == null)
            {
                raycaster = canvasGo.AddComponent<GraphicRaycaster>();
            }

            // Settings panel is rotated 180deg in this scene setup.
            // Accept reversed graphics so slider/button raycasts still hit.
            raycaster.ignoreReversedGraphics = false;

            System.Type trackedRaycasterType = GetTrackedDeviceGraphicRaycasterType();
            if (trackedRaycasterType != null && canvasGo.GetComponent(trackedRaycasterType) == null)
            {
                canvasGo.AddComponent(trackedRaycasterType);
            }
        }
    }

    private static System.Type GetTrackedDeviceGraphicRaycasterType()
    {
        if (trackedDeviceGraphicRaycasterTypeResolved)
        {
            return trackedDeviceGraphicRaycasterType;
        }

        trackedDeviceGraphicRaycasterTypeResolved = true;
        trackedDeviceGraphicRaycasterType =
            System.Type.GetType("UnityEngine.XR.Interaction.Toolkit.UI.TrackedDeviceGraphicRaycaster, Unity.XR.Interaction.Toolkit") ??
            System.Type.GetType("UnityEngine.XR.Interaction.Toolkit.UI.TrackedDeviceGraphicRaycaster, Unity.XR.Interaction.Toolkit.Runtime");
        return trackedDeviceGraphicRaycasterType;
    }



}
