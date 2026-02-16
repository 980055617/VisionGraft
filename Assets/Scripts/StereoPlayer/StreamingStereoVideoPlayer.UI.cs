using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
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
    private Slider runtimeFovxSlider;
    private Text runtimeFovxValueText;
    private bool runtimeSettingsOpen;
    private bool runtimeFovxInitialized;
    private readonly List<InputDevice> xrInputDevices = new List<InputDevice>();

    private void EnsureRuntimeControls()
    {
        if (!enableRuntimeControls)
        {
            if (runtimeControlsRoot != null)
            {
                runtimeControlsRoot.SetActive(false);
            }
            if (runtimeSettingsRoot != null)
            {
                runtimeSettingsRoot.SetActive(false);
            }
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
            runtimeControlsRoot.SetActive(true);
            ApplyRuntimeControlsSizing();
            UpdateRuntimeControlsPlacement();
            UpdatePauseButtonLabel();
            UpdateSettingsButtonLabel();
        }

        if (runtimeSettingsRoot != null)
        {
            runtimeSettingsRoot.SetActive(runtimeSettingsOpen);
            UpdateRuntimeSettingsPlacement();
        }
    }

    private GameObject BuildRuntimeControlsUi()
    {
        EnsureEventSystem();
        if (runtimeControlsPrefab != null)
        {
            GameObject prefabRoot = Instantiate(runtimeControlsPrefab);
            prefabRoot.name = "RuntimeControlsBar";
            EnsurePauseButtonExists(prefabRoot);
            EnsureSettingsButtonExists(prefabRoot);
            BindRuntimeControlsUi(prefabRoot);
            return prefabRoot;
        }

        GameObject root = new GameObject("RuntimeControlsBar");
        Canvas canvas = root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.worldCamera = GetViewCamera();
        root.AddComponent<GraphicRaycaster>();

        RectTransform rect = root.GetComponent<RectTransform>();
        rect.sizeDelta = new Vector2(RuntimeControlsDefaultCanvasWidth, RuntimeControlsDefaultCanvasHeight);
        rect.localScale = new Vector3(
            controlsBarSizeMeters.x / RuntimeControlsDefaultCanvasWidth,
            controlsBarSizeMeters.y / RuntimeControlsDefaultCanvasHeight,
            1f);

        GameObject panelObj = new GameObject("Panel");
        panelObj.transform.SetParent(root.transform, false);
        RectTransform panelRect = panelObj.AddComponent<RectTransform>();
        panelRect.anchorMin = Vector2.zero;
        panelRect.anchorMax = Vector2.one;
        panelRect.offsetMin = Vector2.zero;
        panelRect.offsetMax = Vector2.zero;
        Image panelImage = panelObj.AddComponent<Image>();
        panelImage.color = new Color(0f, 0f, 0f, 0.45f);

        Button pauseButton = CreateBarButton(panelObj.transform, "PauseToggleButton", new Vector2(-180f, 0f));
        runtimePauseButtonText = GetButtonText(pauseButton);
        pauseButton.onClick.RemoveListener(TogglePausePlayback);
        pauseButton.onClick.AddListener(TogglePausePlayback);

        Button settingsButton = CreateBarButton(panelObj.transform, "SettingsButton", new Vector2(180f, 0f));
        runtimeSettingsButtonText = GetButtonText(settingsButton);
        settingsButton.onClick.RemoveListener(ToggleRuntimeSettingsPanel);
        settingsButton.onClick.AddListener(ToggleRuntimeSettingsPanel);

        return root;
    }

    private Button CreateBarButton(Transform parent, string name, Vector2 anchoredPos)
    {
        GameObject buttonObj = new GameObject(name);
        buttonObj.transform.SetParent(parent, false);
        RectTransform buttonRect = buttonObj.AddComponent<RectTransform>();
        buttonRect.anchorMin = new Vector2(0.5f, 0.5f);
        buttonRect.anchorMax = new Vector2(0.5f, 0.5f);
        buttonRect.sizeDelta = new Vector2(300f, 120f);
        buttonRect.anchoredPosition = anchoredPos;

        Image buttonImage = buttonObj.AddComponent<Image>();
        buttonImage.color = new Color(0.13f, 0.13f, 0.13f, 0.9f);
        Button button = buttonObj.AddComponent<Button>();
        button.targetGraphic = buttonImage;
        ColorBlock colors = button.colors;
        colors.normalColor = new Color(0.13f, 0.13f, 0.13f, 0.9f);
        colors.highlightedColor = new Color(0.2f, 0.2f, 0.2f, 0.95f);
        colors.pressedColor = new Color(0.08f, 0.08f, 0.08f, 1f);
        button.colors = colors;

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
        text.fontSize = 52;
        text.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        text.text = name.Contains("Settings") ? "Settings" : "Pause";

        return button;
    }

    private void EnsurePauseButtonExists(GameObject root)
    {
        if (root == null)
        {
            return;
        }

        if (FindButton(root, "pause") != null)
        {
            return;
        }

        Canvas canvas = root.GetComponentInChildren<Canvas>(true);
        if (canvas == null)
        {
            canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            root.AddComponent<GraphicRaycaster>();
        }

        RectTransform canvasRect = canvas.GetComponent<RectTransform>();
        if (canvasRect != null && canvasRect.sizeDelta == Vector2.zero)
        {
            canvasRect.sizeDelta = new Vector2(RuntimeControlsDefaultCanvasWidth, RuntimeControlsDefaultCanvasHeight);
        }

        Transform parent = canvas.transform;
        Button settings = FindButton(root, "setting");
        if (settings != null && settings.transform.parent != null)
        {
            parent = settings.transform.parent;
        }

        Button pause = CreateBarButton(parent, "PauseToggleButton", new Vector2(-180f, 0f));
        pause.onClick.RemoveListener(TogglePausePlayback);
        pause.onClick.AddListener(TogglePausePlayback);
    }

    private void EnsureSettingsButtonExists(GameObject root)
    {
        if (root == null)
        {
            return;
        }

        if (FindButton(root, "setting") != null)
        {
            return;
        }

        Canvas canvas = root.GetComponentInChildren<Canvas>(true);
        if (canvas == null)
        {
            canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            root.AddComponent<GraphicRaycaster>();
        }

        RectTransform canvasRect = canvas.GetComponent<RectTransform>();
        if (canvasRect != null && canvasRect.sizeDelta == Vector2.zero)
        {
            canvasRect.sizeDelta = new Vector2(RuntimeControlsDefaultCanvasWidth, RuntimeControlsDefaultCanvasHeight);
        }

        Transform parent = canvas.transform;
        Button pause = FindButton(root, "pause");
        if (pause != null && pause.transform.parent != null)
        {
            parent = pause.transform.parent;
        }

        Button settings = CreateBarButton(parent, "SettingsButton", new Vector2(180f, 0f));
        settings.onClick.RemoveListener(ToggleRuntimeSettingsPanel);
        settings.onClick.AddListener(ToggleRuntimeSettingsPanel);
    }

    private void BindRuntimeControlsUi(GameObject root)
    {
        if (root == null)
        {
            return;
        }

        runtimePauseButtonText = null;
        runtimeSettingsButtonText = null;

        Button pauseButton = FindButton(root, "pause");
        if (pauseButton == null)
        {
            pauseButton = root.GetComponentInChildren<Button>(true);
        }
        if (pauseButton != null)
        {
            pauseButton.onClick.RemoveListener(TogglePausePlayback);
            pauseButton.onClick.AddListener(TogglePausePlayback);
            runtimePauseButtonText = GetButtonText(pauseButton);
        }

        Button settingsButton = FindButton(root, "setting");
        if (settingsButton != null)
        {
            settingsButton.onClick.RemoveListener(ToggleRuntimeSettingsPanel);
            settingsButton.onClick.AddListener(ToggleRuntimeSettingsPanel);
            runtimeSettingsButtonText = GetButtonText(settingsButton);
        }
    }

    private Button FindButton(GameObject root, string namePartLower)
    {
        if (root == null)
        {
            return null;
        }

        Button[] buttons = root.GetComponentsInChildren<Button>(true);
        for (int i = 0; i < buttons.Length; i++)
        {
            Button button = buttons[i];
            if (button == null)
            {
                continue;
            }

            string n = button.name.ToLowerInvariant();
            if (n.Contains(namePartLower))
            {
                return button;
            }
        }

        return null;
    }

    private Text GetButtonText(Button button)
    {
        if (button == null)
        {
            return null;
        }

        return button.GetComponentInChildren<Text>(true);
    }

    private GameObject BuildRuntimeSettingsUi()
    {
        EnsureEventSystem();
        InitializeRuntimeFovxIfNeeded();

        GameObject root;
        if (runtimeControlsPrefab != null)
        {
            root = Instantiate(runtimeControlsPrefab);
            root.name = "RuntimeSettingsPanel";
        }
        else
        {
            root = new GameObject("RuntimeSettingsPanel");
            Canvas canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.worldCamera = GetViewCamera();
            root.AddComponent<GraphicRaycaster>();
        }

        Canvas rootCanvas = root.GetComponent<Canvas>();
        if (rootCanvas == null)
        {
            rootCanvas = root.GetComponentInChildren<Canvas>(true);
        }

        Transform uiRoot = rootCanvas != null ? rootCanvas.transform : root.transform;

        RectTransform uiRect = uiRoot as RectTransform;
        if (uiRect != null)
        {
            uiRect.sizeDelta = new Vector2(RuntimeSettingsDefaultCanvasWidth, RuntimeSettingsDefaultCanvasHeight);
            uiRect.localScale = new Vector3(
                settingsPanelSizeMeters.x / RuntimeSettingsDefaultCanvasWidth,
                settingsPanelSizeMeters.y / RuntimeSettingsDefaultCanvasHeight,
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

        runtimeFovxSlider = CreateSlider(panelObj.transform, "FovxSlider");
        if (runtimeFovxSlider != null)
        {
            runtimeFovxSlider.onValueChanged.RemoveListener(OnRuntimeFovxSliderChanged);
            UpdateFovxSliderRange();
            runtimeFovxSlider.SetValueWithoutNotify(runtimeFovxDeg);
            runtimeFovxSlider.onValueChanged.AddListener(OnRuntimeFovxSliderChanged);
        }
        UpdateRuntimeFovxText(runtimeFovxDeg);

        return root;
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
        text.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        text.fontSize = fontSize;
        text.alignment = anchor;
        text.color = Color.white;
        text.text = initialText;
        return text;
    }

    private Slider CreateSlider(Transform parent, string name)
    {
        GameObject sliderObj = new GameObject(name);
        sliderObj.transform.SetParent(parent, false);
        RectTransform sliderRect = sliderObj.AddComponent<RectTransform>();
        sliderRect.anchorMin = new Vector2(0.5f, 0.55f);
        sliderRect.anchorMax = new Vector2(0.5f, 0.55f);
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
            initial = runtimeFovxDefaultDeg;
        }

        runtimeFovxDeg = ClampRuntimeFovx(initial);
        useRuntimeFovxOverride = false;
    }

    private float ClampRuntimeFovx(float value)
    {
        float min = Mathf.Min(runtimeFovxMinDeg, runtimeFovxMaxDeg);
        float max = Mathf.Max(runtimeFovxMinDeg, runtimeFovxMaxDeg);
        return Mathf.Clamp(value, min, max);
    }

    private void UpdateFovxSliderRange()
    {
        if (runtimeFovxSlider == null)
        {
            return;
        }

        float min = Mathf.Min(runtimeFovxMinDeg, runtimeFovxMaxDeg);
        float max = Mathf.Max(runtimeFovxMinDeg, runtimeFovxMaxDeg);
        runtimeFovxSlider.minValue = min;
        runtimeFovxSlider.maxValue = max;
    }

    private void OnRuntimeFovxSliderChanged(float value)
    {
        runtimeFovxDeg = ClampRuntimeFovx(value);
        useRuntimeFovxOverride = true;
        loggedFovSource = false;
        UpdateRuntimeFovxText(runtimeFovxDeg);

        if (fitScreenToFov)
        {
            PlaceScreens();
        }
    }

    private void UpdateRuntimeFovxText(float value)
    {
        if (runtimeFovxValueText == null)
        {
            return;
        }

        runtimeFovxValueText.text = value.ToString("F1") + " deg";
    }

    private void ToggleRuntimeSettingsPanel()
    {
        runtimeSettingsOpen = !runtimeSettingsOpen;
        if (runtimeSettingsRoot != null)
        {
            runtimeSettingsRoot.SetActive(runtimeSettingsOpen && enableRuntimeControls);
            if (runtimeSettingsOpen)
            {
                UpdateRuntimeSettingsPlacement();
            }
        }
        UpdateSettingsButtonLabel();
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
        if (eventSystem != null)
        {
            return;
        }

        GameObject eventSystemObj = new GameObject("EventSystem");
        eventSystemObj.AddComponent<EventSystem>();
        eventSystemObj.AddComponent<InputSystemUIInputModule>();
    }

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

        Transform head = GetViewCamera() != null ? GetViewCamera().transform : GetHeadTransform();
        Vector3 toHead = head != null ? (head.position - center).normalized : -basis.forward;
        if (toHead == Vector3.zero)
        {
            toHead = -basis.forward;
        }

        Vector3 right = basis.right;
        Vector3 up = basis.up;
        GetScreenSizeMeters(basis, out _, out float screenHeightMeters, out _);
        float halfScreenH = Mathf.Abs(screenHeightMeters) * 0.5f;
        float halfBarH = Mathf.Abs(controlsBarSizeMeters.y) * 0.5f;
        float downFromCenter = halfScreenH + controlsBarGapMeters + halfBarH - controlsBarOffsetMeters.y;
        runtimeControlsRoot.transform.position =
            center
            + right * controlsBarOffsetMeters.x
            - up * downFromCenter
            + toHead * controlsBarForwardOffsetMeters;
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

        // Anchor the settings panel relative to the runtime bar so it does not
        // jump when FOV changes and screen width is recalculated.
        Transform basis = runtimeControlsRoot != null ? runtimeControlsRoot.transform : (leftScreen != null ? leftScreen : rightScreen);
        if (basis == null)
        {
            return;
        }

        Transform head = GetViewCamera() != null ? GetViewCamera().transform : GetHeadTransform();
        Vector3 toHead = head != null ? (head.position - basis.position).normalized : -basis.forward;
        if (toHead == Vector3.zero)
        {
            toHead = -basis.forward;
        }

        float halfBarW = Mathf.Abs(controlsBarSizeMeters.x) * 0.5f;
        float halfPanelW = Mathf.Abs(settingsPanelSizeMeters.x) * 0.5f;
        float rightFromBar = halfBarW + settingsPanelGapMeters + halfPanelW + settingsPanelOffsetMeters.x;
        runtimeSettingsRoot.transform.position =
            basis.position
            + basis.right * rightFromBar
            + basis.up * settingsPanelOffsetMeters.y
            + toHead * settingsPanelForwardOffsetMeters;
        runtimeSettingsRoot.transform.rotation = basis.rotation;

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
                settingsPanelSizeMeters.x / size.x,
                settingsPanelSizeMeters.y / size.y,
                1f);
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
            controlsBarSizeMeters.x / size.x,
            controlsBarSizeMeters.y / size.y,
            1f);
    }

    private void HandleRuntimePauseInput()
    {
        if (!enablePauseHotkey)
        {
            return;
        }

        if (Input.GetKeyDown(KeyCode.Space) || Input.GetKeyDown(KeyCode.P))
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
