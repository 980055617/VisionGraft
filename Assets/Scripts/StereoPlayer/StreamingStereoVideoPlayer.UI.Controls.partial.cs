using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;
using UnityEngine.XR;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    private GameObject BuildRuntimeControlsUi()
    {
        EnsureEventSystem();
        if (runtimeControlsPrefab != null)
        {
            GameObject prefabRoot = Instantiate(runtimeControlsPrefab);
            prefabRoot.name = "RuntimeControlsBar";
            EnsureCanvasRaycasters(prefabRoot);
            EnsurePauseButtonExists(prefabRoot);
            EnsureSettingsButtonExists(prefabRoot);
            EnsureProgressControlsExists(prefabRoot);
            BindRuntimeControlsUi(prefabRoot);
            return prefabRoot;
        }

        GameObject root = new GameObject("RuntimeControlsBar");
        Canvas canvas = root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.worldCamera = GetViewCamera();
        root.AddComponent<GraphicRaycaster>();
        EnsureCanvasRaycasters(root);

        RectTransform rect = root.GetComponent<RectTransform>();
        rect.sizeDelta = new Vector2(RuntimeControlsDefaultCanvasWidth, RuntimeControlsDefaultCanvasHeight);
        rect.localScale = new Vector3(
            ControlsBarSizeMeters.x / RuntimeControlsDefaultCanvasWidth,
            ControlsBarSizeMeters.y / RuntimeControlsDefaultCanvasHeight,
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

        Button pauseButton = CreateBarButton(panelObj.transform, "PauseToggleButton", new Vector2(-180f, -38f));
        runtimePauseButtonText = GetButtonText(pauseButton);
        pauseButton.onClick.RemoveListener(TogglePausePlayback);
        pauseButton.onClick.AddListener(TogglePausePlayback);

        Button settingsButton = CreateBarButton(panelObj.transform, "SettingsButton", new Vector2(180f, -38f));
        runtimeSettingsButtonText = GetButtonText(settingsButton);
        settingsButton.onClick.RemoveListener(ToggleRuntimeSettingsPanel);
        settingsButton.onClick.AddListener(ToggleRuntimeSettingsPanel);

        CreateProgressControls(panelObj.transform);
        runtimeProgressSlider = FindSlider(root, "progressslider");
        if (runtimeProgressSlider != null)
        {
            runtimeProgressSlider.onValueChanged.RemoveListener(OnRuntimeProgressSliderChanged);
            runtimeProgressSlider.onValueChanged.AddListener(OnRuntimeProgressSliderChanged);
        }
        runtimeProgressText = FindText(root, "progresstext");
        RepositionRuntimeBarElements(root);

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
        text.font = GetRuntimeUiFont();
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

        Button pause = CreateBarButton(parent, "PauseToggleButton", new Vector2(-180f, -38f));
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

        Button settings = CreateBarButton(parent, "SettingsButton", new Vector2(180f, -38f));
        settings.onClick.RemoveListener(ToggleRuntimeSettingsPanel);
        settings.onClick.AddListener(ToggleRuntimeSettingsPanel);
    }


    private void EnsureProgressControlsExists(GameObject root)
    {
        if (root == null)
        {
            return;
        }

        Slider existing = FindSlider(root, "progressslider");
        if (existing != null)
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

        CreateProgressControls(parent);
    }


    private void BindRuntimeControlsUi(GameObject root)
    {
        if (root == null)
        {
            return;
        }

        runtimePauseButtonText = null;
        runtimeSettingsButtonText = null;
        runtimeProgressSlider = null;
        runtimeProgressText = null;

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

        runtimeProgressSlider = FindSlider(root, "progressslider");
        if (runtimeProgressSlider != null)
        {
            runtimeProgressSlider.onValueChanged.RemoveListener(OnRuntimeProgressSliderChanged);
            runtimeProgressSlider.onValueChanged.AddListener(OnRuntimeProgressSliderChanged);
        }

        runtimeProgressText = FindText(root, "progresstext");
        RepositionRuntimeBarElements(root);
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


    private Slider FindSlider(GameObject root, string namePartLower)
    {
        if (root == null)
        {
            return null;
        }

        Slider[] sliders = root.GetComponentsInChildren<Slider>(true);
        for (int i = 0; i < sliders.Length; i++)
        {
            Slider slider = sliders[i];
            if (slider == null)
            {
                continue;
            }

            string n = slider.name.ToLowerInvariant();
            if (n.Contains(namePartLower))
            {
                return slider;
            }
        }

        return null;
    }


    private Text FindText(GameObject root, string namePartLower)
    {
        if (root == null)
        {
            return null;
        }

        Text[] texts = root.GetComponentsInChildren<Text>(true);
        for (int i = 0; i < texts.Length; i++)
        {
            Text text = texts[i];
            if (text == null)
            {
                continue;
            }

            string n = text.name.ToLowerInvariant();
            if (n.Contains(namePartLower))
            {
                return text;
            }
        }

        return null;
    }


    private void CreateProgressControls(Transform parent)
    {
        if (parent == null)
        {
            return;
        }

        GameObject sliderObj = new GameObject("ProgressSlider");
        sliderObj.transform.SetParent(parent, false);
        RectTransform sliderRect = sliderObj.AddComponent<RectTransform>();
        sliderRect.anchorMin = new Vector2(0.5f, 0.5f);
        sliderRect.anchorMax = new Vector2(0.5f, 0.5f);
        sliderRect.sizeDelta = new Vector2(780f, 42f);
        sliderRect.anchoredPosition = new Vector2(0f, 56f);

        Image background = sliderObj.AddComponent<Image>();
        background.color = new Color(1f, 1f, 1f, 0.2f);
        Slider slider = sliderObj.AddComponent<Slider>();
        slider.direction = Slider.Direction.LeftToRight;
        slider.targetGraphic = background;
        slider.minValue = 0f;
        slider.maxValue = 1f;
        slider.SetValueWithoutNotify(0f);

        GameObject fillArea = new GameObject("Fill Area");
        fillArea.transform.SetParent(sliderObj.transform, false);
        RectTransform fillAreaRect = fillArea.AddComponent<RectTransform>();
        fillAreaRect.anchorMin = new Vector2(0f, 0.2f);
        fillAreaRect.anchorMax = new Vector2(1f, 0.8f);
        fillAreaRect.offsetMin = new Vector2(20f, 0f);
        fillAreaRect.offsetMax = new Vector2(-20f, 0f);

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
        handleAreaRect.offsetMin = new Vector2(16f, 0f);
        handleAreaRect.offsetMax = new Vector2(-16f, 0f);

        GameObject handleObj = new GameObject("Handle");
        handleObj.transform.SetParent(handleArea.transform, false);
        Image handleImage = handleObj.AddComponent<Image>();
        handleImage.color = new Color(0.95f, 0.95f, 0.95f, 1f);
        RectTransform handleRect = handleObj.GetComponent<RectTransform>();
        handleRect.sizeDelta = new Vector2(22f, 40f);

        slider.fillRect = fillRect;
        slider.handleRect = handleRect;

        GameObject textObj = new GameObject("ProgressText");
        textObj.transform.SetParent(parent, false);
        RectTransform textRect = textObj.AddComponent<RectTransform>();
        textRect.anchorMin = new Vector2(0.5f, 0.5f);
        textRect.anchorMax = new Vector2(0.5f, 0.5f);
        textRect.sizeDelta = new Vector2(420f, 30f);
        textRect.anchoredPosition = new Vector2(0f, 88f);

        Text text = textObj.AddComponent<Text>();
        text.font = GetRuntimeUiFont();
        text.fontSize = 24;
        text.alignment = TextAnchor.MiddleCenter;
        text.color = Color.white;
        text.text = "00:00 / 00:00";
    }


    private void RepositionRuntimeBarElements(GameObject root)
    {
        if (root == null)
        {
            return;
        }

        Button pause = FindButton(root, "pause");
        if (pause != null)
        {
            RectTransform r = pause.transform as RectTransform;
            if (r != null)
            {
                r.anchoredPosition = new Vector2(-180f, -62f);
            }
        }

        Button settings = FindButton(root, "setting");
        if (settings != null)
        {
            RectTransform r = settings.transform as RectTransform;
            if (r != null)
            {
                r.anchoredPosition = new Vector2(180f, -62f);
            }
        }

        Slider progress = FindSlider(root, "progressslider");
        if (progress != null)
        {
            RectTransform r = progress.transform as RectTransform;
            if (r != null)
            {
                r.anchoredPosition = new Vector2(0f, 56f);
            }
        }

        Text progressText = FindText(root, "progresstext");
        if (progressText != null)
        {
            RectTransform r = progressText.transform as RectTransform;
            if (r != null)
            {
                r.anchoredPosition = new Vector2(0f, 88f);
            }
        }
    }
}
