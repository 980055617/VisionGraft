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
            GameObject prefabRoot = RuntimeUiRootFactory.Create("RuntimeControlsBar", runtimeControlsPrefab);
            EnsureCanvasRaycasters(prefabRoot);
            EnsurePauseButtonExists(prefabRoot);
            EnsureSettingsButtonExists(prefabRoot);
            EnsureProgressControlsExists(prefabRoot);
            BindRuntimeControlsUi(prefabRoot);
            return prefabRoot;
        }

        GameObject root = RuntimeUiRootFactory.Create("RuntimeControlsBar", null);
        Canvas canvas = RuntimeCanvasComponentFactory.EnsureCanvas(root);
        RuntimeCanvasWriter.ApplyWorldSpaceCamera(canvas, GetViewCamera());
        RuntimeCanvasComponentFactory.EnsureGraphicRaycaster(root, false);
        EnsureCanvasRaycasters(root);

        RectTransform rect = root.GetComponent<RectTransform>();
        RuntimeUiTransformWriter.ApplySizeDelta(
            rect,
            new Vector2(RuntimeControlsDefaultCanvasWidth, RuntimeControlsDefaultCanvasHeight));
        RuntimeUiTransformWriter.ApplyLocalScale(
            rect,
            new Vector3(
                ControlsBarSizeMeters.x / RuntimeControlsDefaultCanvasWidth,
                ControlsBarSizeMeters.y / RuntimeControlsDefaultCanvasHeight,
                1f));

        RectTransform panelRect = RuntimeUiElementFactory.CreateRectChild("Panel", root.transform, out GameObject panelObj);
        RuntimeUiTransformWriter.ApplyStretchRect(
            panelRect,
            Vector2.zero,
            Vector2.one,
            Vector2.zero,
            Vector2.zero);
        Image panelImage = RuntimeUiElementFactory.AddImage(panelObj);
        RuntimeGraphicWriter.ApplyColor(panelImage, new Color(0f, 0f, 0f, 0.45f));

        Button pauseButton = CreateBarButton(panelObj.transform, "PauseToggleButton", new Vector2(-180f, -38f));
        runtimePauseButtonText = GetButtonText(pauseButton);
        BindRuntimeButton(pauseButton, TogglePausePlayback);

        Button settingsButton = CreateBarButton(panelObj.transform, "SettingsButton", new Vector2(180f, -38f));
        runtimeSettingsButtonText = GetButtonText(settingsButton);
        BindRuntimeButton(settingsButton, ToggleRuntimeSettingsPanel);

        CreateProgressControls(panelObj.transform);
        runtimeProgressSlider = FindSlider(root, "progressslider");
        if (runtimeProgressSlider != null)
        {
            BindRuntimeSlider(runtimeProgressSlider, OnRuntimeProgressSliderChanged);
        }
        runtimeProgressText = FindText(root, "progresstext");
        RepositionRuntimeBarElements(root);

        return root;
    }


    private Button CreateBarButton(Transform parent, string name, Vector2 anchoredPos)
    {
        RectTransform buttonRect = RuntimeUiElementFactory.CreateRectChild(name, parent, out GameObject buttonObj);
        RuntimeUiTransformWriter.ApplyCenteredRect(buttonRect, anchoredPos, new Vector2(300f, 120f));

        Image buttonImage = RuntimeUiElementFactory.AddImage(buttonObj);
        RuntimeGraphicWriter.ApplyColor(buttonImage, new Color(0.13f, 0.13f, 0.13f, 0.9f));
        Button button = RuntimeUiElementFactory.AddButton(buttonObj);
        RuntimeGraphicWriter.ApplyTargetGraphic(button, buttonImage);
        ColorBlock colors = button.colors;
        colors.normalColor = new Color(0.13f, 0.13f, 0.13f, 0.9f);
        colors.highlightedColor = new Color(0.2f, 0.2f, 0.2f, 0.95f);
        colors.pressedColor = new Color(0.08f, 0.08f, 0.08f, 1f);
        RuntimeGraphicWriter.ApplyColors(button, colors);

        RectTransform textRect = RuntimeUiElementFactory.CreateRectChild("Label", buttonObj.transform, out GameObject textObj);
        RuntimeUiTransformWriter.ApplyStretchRect(
            textRect,
            Vector2.zero,
            Vector2.one,
            Vector2.zero,
            Vector2.zero);

        Text text = RuntimeUiElementFactory.AddText(textObj);
        RuntimeTextWriter.ApplyStyle(text, GetRuntimeUiFont(), 52, TextAnchor.MiddleCenter, Color.white);
        RuntimeTextWriter.ApplyContent(text, name.Contains("Settings") ? "Settings" : "Pause");

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

        Transform parent = EnsureRuntimeControlsCanvasTransform(root);
        Button settings = FindButton(root, "setting");
        if (settings != null && settings.transform.parent != null)
        {
            parent = settings.transform.parent;
        }

        Button pause = CreateBarButton(parent, "PauseToggleButton", new Vector2(-180f, -38f));
        BindRuntimeButton(pause, TogglePausePlayback);
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

        Transform parent = EnsureRuntimeControlsCanvasTransform(root);
        Button pause = FindButton(root, "pause");
        if (pause != null && pause.transform.parent != null)
        {
            parent = pause.transform.parent;
        }

        Button settings = CreateBarButton(parent, "SettingsButton", new Vector2(180f, -38f));
        BindRuntimeButton(settings, ToggleRuntimeSettingsPanel);
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

        Transform parent = EnsureRuntimeControlsCanvasTransform(root);
        Button pause = FindButton(root, "pause");
        if (pause != null && pause.transform.parent != null)
        {
            parent = pause.transform.parent;
        }

        CreateProgressControls(parent);
    }


    private Transform EnsureRuntimeControlsCanvasTransform(GameObject root)
    {
        Canvas canvas = root.GetComponentInChildren<Canvas>(true);
        if (canvas == null)
        {
            canvas = RuntimeCanvasComponentFactory.EnsureCanvas(root);
            RuntimeCanvasWriter.ApplyWorldSpaceCamera(canvas, GetViewCamera());
            RuntimeCanvasComponentFactory.EnsureGraphicRaycaster(root, false);
        }

        RectTransform canvasRect = canvas.GetComponent<RectTransform>();
        if (canvasRect != null && canvasRect.sizeDelta == Vector2.zero)
        {
            RuntimeUiTransformWriter.ApplySizeDelta(
                canvasRect,
                new Vector2(RuntimeControlsDefaultCanvasWidth, RuntimeControlsDefaultCanvasHeight));
        }

        return canvas.transform;
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
            BindRuntimeButton(pauseButton, TogglePausePlayback);
            runtimePauseButtonText = GetButtonText(pauseButton);
        }

        Button settingsButton = FindButton(root, "setting");
        if (settingsButton != null)
        {
            BindRuntimeButton(settingsButton, ToggleRuntimeSettingsPanel);
            runtimeSettingsButtonText = GetButtonText(settingsButton);
        }

        runtimeProgressSlider = FindSlider(root, "progressslider");
        if (runtimeProgressSlider != null)
        {
            BindRuntimeSlider(runtimeProgressSlider, OnRuntimeProgressSliderChanged);
        }

        runtimeProgressText = FindText(root, "progresstext");
        RepositionRuntimeBarElements(root);
    }


    private Button FindButton(GameObject root, string namePartLower)
    {
        return RuntimeUiLookup.FindButton(root, namePartLower);
    }


    private Text GetButtonText(Button button)
    {
        return RuntimeUiLookup.GetButtonText(button);
    }


    private Slider FindSlider(GameObject root, string namePartLower)
    {
        return RuntimeUiLookup.FindSlider(root, namePartLower);
    }


    private Text FindText(GameObject root, string namePartLower)
    {
        return RuntimeUiLookup.FindText(root, namePartLower);
    }


    private void CreateProgressControls(Transform parent)
    {
        if (parent == null)
        {
            return;
        }

        RectTransform sliderRect = RuntimeUiElementFactory.CreateRectChild("ProgressSlider", parent, out GameObject sliderObj);
        RuntimeUiTransformWriter.ApplyCenteredRect(sliderRect, new Vector2(0f, 56f), new Vector2(780f, 42f));

        Image background = RuntimeUiElementFactory.AddImage(sliderObj);
        RuntimeGraphicWriter.ApplyColor(background, new Color(1f, 1f, 1f, 0.2f));
        Slider slider = RuntimeUiElementFactory.AddSlider(sliderObj);
        RuntimeSliderWriter.ApplyDirection(slider, Slider.Direction.LeftToRight);
        RuntimeGraphicWriter.ApplyTargetGraphic(slider, background);
        RuntimeSliderWriter.ApplyRange(slider, 0f, 1f);
        RuntimeSliderWriter.ApplyValueWithoutNotify(slider, 0f);

        RectTransform fillAreaRect = RuntimeUiElementFactory.CreateRectChild("Fill Area", sliderObj.transform, out GameObject fillArea);
        RuntimeUiTransformWriter.ApplyStretchRect(
            fillAreaRect,
            new Vector2(0f, 0.2f),
            new Vector2(1f, 0.8f),
            new Vector2(20f, 0f),
            new Vector2(-20f, 0f));

        GameObject fillObj = RuntimeUiElementFactory.CreateChild("Fill", fillArea.transform);
        Image fillImage = RuntimeUiElementFactory.AddImage(fillObj);
        RuntimeGraphicWriter.ApplyColor(fillImage, new Color(0.22f, 0.72f, 1f, 0.95f));
        RectTransform fillRect = fillObj.GetComponent<RectTransform>();
        RuntimeUiTransformWriter.ApplyStretchRect(
            fillRect,
            new Vector2(0f, 0f),
            new Vector2(1f, 1f),
            Vector2.zero,
            Vector2.zero);

        RectTransform handleAreaRect = RuntimeUiElementFactory.CreateRectChild("Handle Slide Area", sliderObj.transform, out GameObject handleArea);
        RuntimeUiTransformWriter.ApplyStretchRect(
            handleAreaRect,
            Vector2.zero,
            Vector2.one,
            new Vector2(16f, 0f),
            new Vector2(-16f, 0f));

        GameObject handleObj = RuntimeUiElementFactory.CreateChild("Handle", handleArea.transform);
        Image handleImage = RuntimeUiElementFactory.AddImage(handleObj);
        RuntimeGraphicWriter.ApplyColor(handleImage, new Color(0.95f, 0.95f, 0.95f, 1f));
        RectTransform handleRect = handleObj.GetComponent<RectTransform>();
        RuntimeUiTransformWriter.ApplySizeDelta(handleRect, new Vector2(22f, 40f));

        RuntimeSliderWriter.ApplyRects(slider, fillRect, handleRect);

        RectTransform textRect = RuntimeUiElementFactory.CreateRectChild("ProgressText", parent, out GameObject textObj);
        RuntimeUiTransformWriter.ApplyCenteredRect(textRect, new Vector2(0f, 88f), new Vector2(420f, 30f));

        Text text = RuntimeUiElementFactory.AddText(textObj);
        RuntimeTextWriter.ApplyStyle(text, GetRuntimeUiFont(), 24, TextAnchor.MiddleCenter, Color.white);
        RuntimeTextWriter.ApplyContent(text, "00:00 / 00:00");
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
                RuntimeUiTransformWriter.ApplyAnchoredPosition(r, new Vector2(-180f, -62f));
            }
        }

        Button settings = FindButton(root, "setting");
        if (settings != null)
        {
            RectTransform r = settings.transform as RectTransform;
            if (r != null)
            {
                RuntimeUiTransformWriter.ApplyAnchoredPosition(r, new Vector2(180f, -62f));
            }
        }

        Slider progress = FindSlider(root, "progressslider");
        if (progress != null)
        {
            RectTransform r = progress.transform as RectTransform;
            if (r != null)
            {
                RuntimeUiTransformWriter.ApplyAnchoredPosition(r, new Vector2(0f, 56f));
            }
        }

        Text progressText = FindText(root, "progresstext");
        if (progressText != null)
        {
            RectTransform r = progressText.transform as RectTransform;
            if (r != null)
            {
                RuntimeUiTransformWriter.ApplyAnchoredPosition(r, new Vector2(0f, 88f));
            }
        }
    }
}
