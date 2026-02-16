using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityEngine.XR;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    private GameObject runtimeControlsRoot;
    private Text runtimePauseButtonText;
    private bool pausedByUser;
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
            UpdateRuntimeControlsPlacement();
            UpdatePauseButtonLabel();
        }
    }

    private GameObject BuildRuntimeControlsUi()
    {
        EnsureEventSystem();

        GameObject root = new GameObject("RuntimeControlsBar");
        var canvas = root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.worldCamera = GetViewCamera();
        root.AddComponent<GraphicRaycaster>();

        var rect = root.GetComponent<RectTransform>();
        rect.sizeDelta = new Vector2(1000f, 200f);
        rect.localScale = new Vector3(
            controlsBarSizeMeters.x / 1000f,
            controlsBarSizeMeters.y / 200f,
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

        return root;
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
        eventSystemObj.AddComponent<StandaloneInputModule>();
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

        var canvas = runtimeControlsRoot.GetComponent<Canvas>();
        if (canvas != null)
        {
            canvas.worldCamera = GetViewCamera();
        }
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
            pausedByUser = true;
        }
        else
        {
            vp.Play();
            pausedByUser = false;
        }

        UpdatePauseButtonLabel();
    }

    private void UpdatePauseButtonLabel()
    {
        if (runtimePauseButtonText == null)
        {
            return;
        }

        runtimePauseButtonText.text = (vp != null && vp.isPlaying && !pausedByUser) ? "Pause" : "Resume";
    }
}
