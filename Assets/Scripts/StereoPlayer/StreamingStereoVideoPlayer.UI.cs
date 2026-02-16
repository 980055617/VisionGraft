using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityEngine.InputSystem.UI;
using UnityEngine.XR;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    private const float RuntimeControlsDefaultCanvasWidth = 1000f;
    private const float RuntimeControlsDefaultCanvasHeight = 200f;
    private GameObject runtimeControlsRoot;
    private Text runtimePauseButtonText;
    private readonly List<InputDevice> xrInputDevices = new List<InputDevice>();

    private void EnsureRuntimeControls()
    {
        if (!enableRuntimeControls)
        {
            if (runtimeControlsRoot != null)
            {
                runtimeControlsRoot.SetActive(false);
            }
            return;
        }

        if (runtimeControlsRoot == null)
        {
            runtimeControlsRoot = BuildRuntimeControlsUi();
        }

        if (runtimeControlsRoot != null)
        {
            runtimeControlsRoot.SetActive(true);
            ApplyRuntimeControlsSizing();
            UpdateRuntimeControlsPlacement();
            UpdatePauseButtonLabel();
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
            BindRuntimeControlsUi(prefabRoot);
            return prefabRoot;
        }

        // Fallback for projects that do not assign a prefab yet.
        GameObject root = new GameObject("RuntimeControlsBar");
        var canvas = root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.worldCamera = GetViewCamera();
        root.AddComponent<GraphicRaycaster>();

        var rect = root.GetComponent<RectTransform>();
        rect.sizeDelta = new Vector2(RuntimeControlsDefaultCanvasWidth, RuntimeControlsDefaultCanvasHeight);
        rect.localScale = new Vector3(
            controlsBarSizeMeters.x / RuntimeControlsDefaultCanvasWidth,
            controlsBarSizeMeters.y / RuntimeControlsDefaultCanvasHeight,
            1f);

        GameObject panelObj = new GameObject("Panel");
        panelObj.transform.SetParent(root.transform, false);
        var panelRect = panelObj.AddComponent<RectTransform>();
        panelRect.anchorMin = Vector2.zero;
        panelRect.anchorMax = Vector2.one;
        panelRect.offsetMin = Vector2.zero;
        panelRect.offsetMax = Vector2.zero;
        var panelImage = panelObj.AddComponent<Image>();
        panelImage.color = new Color(0f, 0f, 0f, 0.45f);

        GameObject buttonObj = new GameObject("PauseToggleButton");
        buttonObj.transform.SetParent(panelObj.transform, false);
        var buttonRect = buttonObj.AddComponent<RectTransform>();
        buttonRect.anchorMin = new Vector2(0.5f, 0.5f);
        buttonRect.anchorMax = new Vector2(0.5f, 0.5f);
        buttonRect.sizeDelta = new Vector2(340f, 130f);
        buttonRect.anchoredPosition = Vector2.zero;

        var buttonImage = buttonObj.AddComponent<Image>();
        buttonImage.color = new Color(0.13f, 0.13f, 0.13f, 0.9f);
        var button = buttonObj.AddComponent<Button>();
        button.targetGraphic = buttonImage;
        var colors = button.colors;
        colors.normalColor = new Color(0.13f, 0.13f, 0.13f, 0.9f);
        colors.highlightedColor = new Color(0.2f, 0.2f, 0.2f, 0.95f);
        colors.pressedColor = new Color(0.08f, 0.08f, 0.08f, 1f);
        button.colors = colors;
        button.onClick.AddListener(TogglePausePlayback);

        GameObject textObj = new GameObject("Label");
        textObj.transform.SetParent(buttonObj.transform, false);
        var textRect = textObj.AddComponent<RectTransform>();
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = Vector2.zero;
        textRect.offsetMax = Vector2.zero;

        var text = textObj.AddComponent<Text>();
        text.alignment = TextAnchor.MiddleCenter;
        text.color = Color.white;
        text.fontSize = 56;
        text.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        runtimePauseButtonText = text;
        button.onClick.RemoveListener(TogglePausePlayback);
        button.onClick.AddListener(TogglePausePlayback);

        return root;
    }

    private void EnsurePauseButtonExists(GameObject root)
    {
        if (root == null)
        {
            return;
        }

        if (root.GetComponentInChildren<Button>(true) != null)
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

        GameObject panelObj = new GameObject("AutoPanel");
        panelObj.transform.SetParent(canvas.transform, false);
        RectTransform panelRect = panelObj.AddComponent<RectTransform>();
        panelRect.anchorMin = Vector2.zero;
        panelRect.anchorMax = Vector2.one;
        panelRect.offsetMin = Vector2.zero;
        panelRect.offsetMax = Vector2.zero;
        Image panelImage = panelObj.AddComponent<Image>();
        panelImage.color = new Color(0f, 0f, 0f, 0.45f);

        GameObject buttonObj = new GameObject("PauseToggleButton");
        buttonObj.transform.SetParent(panelObj.transform, false);
        RectTransform buttonRect = buttonObj.AddComponent<RectTransform>();
        buttonRect.anchorMin = new Vector2(0.5f, 0.5f);
        buttonRect.anchorMax = new Vector2(0.5f, 0.5f);
        buttonRect.sizeDelta = new Vector2(340f, 130f);
        buttonRect.anchoredPosition = Vector2.zero;

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
        text.fontSize = 56;
        text.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        text.text = "Pause";
    }

    private void BindRuntimeControlsUi(GameObject root)
    {
        if (root == null)
        {
            return;
        }

        runtimePauseButtonText = null;
        Button button = root.GetComponentInChildren<Button>(true);
        if (button != null)
        {
            button.onClick.RemoveListener(TogglePausePlayback);
            button.onClick.AddListener(TogglePausePlayback);
            runtimePauseButtonText = button.GetComponentInChildren<Text>(true);
        }
    }

    private void EnsureEventSystem()
    {
#if UNITY_2023_1_OR_NEWER
        var eventSystem = Object.FindFirstObjectByType<EventSystem>();
#else
        var eventSystem = Object.FindObjectOfType<EventSystem>();
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

        var canvas = GetRuntimeControlsCanvas();
        if (canvas != null)
        {
            canvas.worldCamera = GetViewCamera();
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
