using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// 実験フローで使うワールド空間パネル（セットアップ / 待機 / 終了）。
//
// 見出し + 本文 + ボタン列という同じ構成を局面ごとに差し替えて使い回す。
// 生成手順は StreamingStereoVideoPlayer の bundle picker に合わせてあり、
// runtimeControlsPrefab と同じ ISDK レイ操作用 prefab をそのまま渡せる。
public sealed class ExperimentPanel
{
    public struct ButtonSpec
    {
        public string label;
        public Action onClick;
        public bool interactable;

        public static ButtonSpec Create(string label, Action onClick, bool interactable = true)
        {
            return new ButtonSpec { label = label, onClick = onClick, interactable = interactable };
        }
    }

    private const float CanvasWidth = 1200f;
    private const float CanvasHeight = 900f;
    private const int UiLayer = 5;

    private readonly GameObject prefab;
    private readonly Func<Camera> cameraProvider;

    private GameObject root;
    private Text titleText;
    private Text bodyText;
    private Transform buttonRow;
    private readonly List<Button> buttons = new List<Button>();
    private bool placementLocked;

    public ExperimentPanel(GameObject prefab, Func<Camera> cameraProvider)
    {
        this.prefab = prefab;
        this.cameraProvider = cameraProvider;
    }

    public bool IsVisible
    {
        get { return root != null && root.activeSelf; }
    }

    public Vector2 SizeMeters = new Vector2(1.05f, 0.82f);
    public float DistanceMeters = 1.2f;
    // 頭の向きを基準にした横・縦のずらし量。試行中パネルは映像を隠さないよう下にずらす。
    public Vector2 OffsetMeters = Vector2.zero;
    public bool FlipHorizontal = true;

    public void Show(string title, string body, IList<ButtonSpec> buttonSpecs)
    {
        EnsureRoot();
        placementLocked = false;

        // SizeMeters は局面ごとに変わる（セットアップは大きく、試行中は小さく）ので、
        // ルート生成時ではなく表示のたびに反映する。
        ApplyPanelScale();

        UiComponentWriter.ApplyTextContent(titleText, title);
        UiComponentWriter.ApplyTextContent(bodyText, body);
        RebuildButtons(buttonSpecs);

        SceneObjectWriter.ApplyActive(root, true);
        SetLayerRecursively(root, UiLayer);
    }

    public void SetBody(string body)
    {
        UiComponentWriter.ApplyTextContent(bodyText, body);
    }

    public void Hide()
    {
        if (root != null)
        {
            SceneObjectWriter.ApplyActive(root, false);
        }
    }

    // パネルは表示のたびに「そのときの正面」へ 1 度だけ置く。毎フレーム追従させると
    // 読んでいる最中にパネルが動いて酔うため、位置は最初のフレームで固定する。
    public void UpdatePlacement(Transform head)
    {
        if (root == null || !root.activeSelf || placementLocked || head == null)
        {
            return;
        }

        Canvas canvas = root.GetComponent<Canvas>();
        UiComponentWriter.ApplyWorldCameraIfMissing(canvas, ResolveCamera());

        Vector3 pos =
            head.position +
            head.forward * Mathf.Max(0.2f, DistanceMeters) +
            head.right * OffsetMeters.x +
            head.up * OffsetMeters.y;
        Vector3 toHead = (head.position - pos).normalized;
        if (Mathf.Abs(Vector3.Dot(toHead, Vector3.up)) > 0.98f)
        {
            Vector3 fallback = Vector3.ProjectOnPlane(head.forward, Vector3.up);
            if (fallback.sqrMagnitude > 0.000001f)
            {
                toHead = fallback.normalized;
            }
        }

        Quaternion rot = Quaternion.LookRotation(toHead, Vector3.up);
        if (FlipHorizontal)
        {
            rot *= Quaternion.Euler(0f, 180f, 0f);
        }

        TransformWriter.ApplyPose(root.transform, pos, rot);
        placementLocked = true;
    }

    public void Destroy()
    {
        ClearButtons();
        if (root != null)
        {
            SceneObjectWriter.DestroyObject(root);
            root = null;
        }
    }

    private Camera ResolveCamera()
    {
        return cameraProvider != null ? cameraProvider() : Camera.main;
    }

    private void EnsureRoot()
    {
        if (root != null)
        {
            return;
        }

        EnsureEventSystem();

        root = RuntimeUiRootFactory.Create("ExperimentPanel", prefab);
        SetLayerRecursively(root, UiLayer);

        Canvas canvas = RuntimeCanvasComponentFactory.EnsureCanvas(root);
        UiComponentWriter.ApplyWorldSpaceCamera(canvas, ResolveCamera());
        RuntimeCanvasComponentFactory.EnsureGraphicRaycaster(root, false);
        EnsureCanvasRaycasters(root);

        RectTransform canvasRect = root.GetComponent<RectTransform>();
        TransformWriter.ApplySizeDelta(canvasRect, new Vector2(CanvasWidth, CanvasHeight));

        Transform contentRoot = ResolveContentRoot(root);
        RectTransform panelRect = RuntimeUiElementFactory.CreateRectChild("Panel", contentRoot, out GameObject panelObj);
        TransformWriter.ApplyStretchRect(panelRect, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
        Image panelImage = RuntimeUiElementFactory.AddImage(panelObj);
        UiComponentWriter.ApplyGraphicColor(panelImage, new Color(0.08f, 0.09f, 0.11f, 0.94f));

        titleText = CreateText(panelObj.transform, "Title", new Vector2(0f, 360f), new Vector2(1080f, 100f),
            56, TextAnchor.MiddleCenter, Color.white);
        bodyText = CreateText(panelObj.transform, "Body", new Vector2(0f, 60f), new Vector2(1080f, 480f),
            36, TextAnchor.UpperLeft, new Color(0.92f, 0.95f, 1f, 1f));

        RectTransform rowRect = RuntimeUiElementFactory.CreateRectChild("ButtonRow", panelObj.transform, out GameObject rowObj);
        TransformWriter.ApplyCenteredRect(rowRect, new Vector2(0f, -330f), new Vector2(1080f, 200f));
        buttonRow = rowObj.transform;
    }

    private void ApplyPanelScale()
    {
        RectTransform canvasRect = root != null ? root.GetComponent<RectTransform>() : null;
        if (canvasRect == null)
        {
            return;
        }

        TransformWriter.ApplyLocalScale(
            canvasRect,
            new Vector3(
                Mathf.Max(0.01f, SizeMeters.x) / CanvasWidth,
                Mathf.Max(0.01f, SizeMeters.y) / CanvasHeight,
                1f));
    }

    private void RebuildButtons(IList<ButtonSpec> specs)
    {
        ClearButtons();
        if (specs == null || specs.Count == 0)
        {
            return;
        }

        // 1 行あたり 3 個まで。それを超えたら 2 段目に折り返す。
        const int perRow = 3;
        const float buttonWidth = 330f;
        const float buttonHeight = 84f;
        const float gapX = 20f;
        const float gapY = 16f;

        for (int i = 0; i < specs.Count; i++)
        {
            int row = i / perRow;
            int col = i % perRow;
            int countInRow = Mathf.Min(perRow, specs.Count - row * perRow);
            float rowWidth = countInRow * buttonWidth + (countInRow - 1) * gapX;
            float x = -rowWidth * 0.5f + buttonWidth * 0.5f + col * (buttonWidth + gapX);
            float y = -row * (buttonHeight + gapY);

            ButtonSpec spec = specs[i];
            Button button = CreateButton(buttonRow, $"Button_{i}", spec.label, new Vector2(x, y),
                new Vector2(buttonWidth, buttonHeight), spec.onClick);
            UiComponentWriter.ApplyInteractable(button, spec.interactable);
            buttons.Add(button);
        }

        SetLayerRecursively(root, UiLayer);
    }

    private void ClearButtons()
    {
        for (int i = 0; i < buttons.Count; i++)
        {
            Button button = buttons[i];
            if (button == null)
            {
                continue;
            }

            // 局面が変わるたびにボタンを作り直すので、前の局面のハンドラが残らないよう
            // 破棄前に必ず外す。
            RuntimeUnityEventBinding.ClearButtonListenersInChildren(button.gameObject);
            // 再生中の Destroy はフレーム末まで遅延する。同じフレームで新しいボタンを
            // 同じ位置に作るため、先に非アクティブにして重なりと誤クリックを防ぐ。
            SceneObjectWriter.ApplyActive(button.gameObject, false);
            SceneObjectWriter.DestroyObject(button.gameObject);
        }

        buttons.Clear();
    }

    private static Transform ResolveContentRoot(GameObject rootObject)
    {
        if (rootObject == null)
        {
            return null;
        }

        Transform interactionRoot = FindDeepChildByName(rootObject.transform, "ISDK_RayCanvasInteraction");
        if (interactionRoot != null)
        {
            Transform surface = FindDeepChildByName(interactionRoot, "Surface");
            if (surface != null)
            {
                return surface;
            }
        }

        return rootObject.transform;
    }

    private static Transform FindDeepChildByName(Transform parent, string name)
    {
        if (parent == null)
        {
            return null;
        }

        for (int i = 0; i < parent.childCount; i++)
        {
            Transform child = parent.GetChild(i);
            if (child == null)
            {
                continue;
            }

            if (string.Equals(child.name, name, StringComparison.Ordinal))
            {
                return child;
            }

            Transform found = FindDeepChildByName(child, name);
            if (found != null)
            {
                return found;
            }
        }

        return null;
    }

    private static Text CreateText(Transform parent, string name, Vector2 anchoredPos, Vector2 size,
        int fontSize, TextAnchor alignment, Color color)
    {
        RectTransform rect = RuntimeUiElementFactory.CreateRectChild(name, parent, out GameObject obj);
        TransformWriter.ApplyCenteredRect(rect, anchoredPos, size);

        Text text = RuntimeUiElementFactory.AddText(obj);
        UiComponentWriter.ApplyTextStyle(text, ExperimentUiFont.Resolve(), fontSize, alignment, color);
        UiComponentWriter.ApplyTextOverflow(text, HorizontalWrapMode.Wrap, VerticalWrapMode.Overflow);
        UiComponentWriter.ApplyTextInteraction(text, false);
        return text;
    }

    private static Button CreateButton(Transform parent, string name, string label, Vector2 anchoredPos,
        Vector2 size, Action onClick)
    {
        RectTransform rect = RuntimeUiElementFactory.CreateRectChild(name, parent, out GameObject obj);
        TransformWriter.ApplyCenteredRect(rect, anchoredPos, size);

        Image image = RuntimeUiElementFactory.AddImage(obj);
        UiComponentWriter.ApplyGraphicColor(image, new Color(0.22f, 0.26f, 0.34f, 0.96f));

        Button button = RuntimeUiElementFactory.AddButton(obj);
        ColorBlock colors = button.colors;
        colors.normalColor = image.color;
        colors.highlightedColor = new Color(0.30f, 0.35f, 0.44f, 0.98f);
        colors.pressedColor = new Color(0.16f, 0.20f, 0.28f, 1f);
        colors.selectedColor = colors.highlightedColor;
        colors.disabledColor = new Color(0.18f, 0.18f, 0.18f, 0.6f);
        UiComponentWriter.ApplySelectableColors(button, colors);
        UiComponentWriter.ApplyTargetGraphic(button, image);

        if (onClick != null)
        {
            RuntimeUnityEventBinding.Bind(button, new UnityEngine.Events.UnityAction(onClick));
        }

        RectTransform textRect = RuntimeUiElementFactory.CreateRectChild("Label", obj.transform, out GameObject textObj);
        TransformWriter.ApplyStretchRect(textRect, Vector2.zero, Vector2.one, new Vector2(12f, 0f), new Vector2(-12f, 0f));

        Text text = RuntimeUiElementFactory.AddText(textObj);
        UiComponentWriter.ApplyTextStyle(text, ExperimentUiFont.Resolve(), 32, TextAnchor.MiddleCenter, Color.white);
        UiComponentWriter.ApplyTextOverflow(text, HorizontalWrapMode.Wrap, VerticalWrapMode.Truncate);
        UiComponentWriter.ApplyTextContent(text, label);
        UiComponentWriter.ApplyTextInteraction(text, false);

        return button;
    }

    private static void EnsureEventSystem()
    {
#if UNITY_2023_1_OR_NEWER
        EventSystem eventSystem = UnityEngine.Object.FindFirstObjectByType<EventSystem>();
#else
        EventSystem eventSystem = UnityEngine.Object.FindObjectOfType<EventSystem>();
#endif
        RuntimeEventSystemFactory.Ensure(eventSystem);
    }

    private static void EnsureCanvasRaycasters(GameObject rootObject)
    {
        if (rootObject == null)
        {
            return;
        }

        Canvas[] canvases = rootObject.GetComponentsInChildren<Canvas>(true);
        for (int i = 0; i < canvases.Length; i++)
        {
            Canvas canvas = canvases[i];
            if (canvas == null)
            {
                continue;
            }

            GameObject canvasGo = canvas.gameObject;
            RuntimeCanvasComponentFactory.EnsureGraphicRaycaster(canvasGo, false);

            Type trackedRaycasterType = RuntimeTrackedDeviceGraphicRaycasterResolver.Resolve();
            if (trackedRaycasterType != null && canvasGo.GetComponent(trackedRaycasterType) == null)
            {
                RuntimeCanvasComponentFactory.EnsureComponent(canvasGo, trackedRaycasterType);
            }
        }
    }

    private static void SetLayerRecursively(GameObject target, int layer)
    {
        if (target == null)
        {
            return;
        }

        target.layer = layer;
        Transform tr = target.transform;
        for (int i = 0; i < tr.childCount; i++)
        {
            Transform child = tr.GetChild(i);
            if (child != null)
            {
                SetLayerRecursively(child.gameObject, layer);
            }
        }
    }
}

// パネルのフォント解決。StreamingStereoVideoPlayer 側の GetRuntimeUiFont と同じ優先順位。
public static class ExperimentUiFont
{
    private static Font cached;

    public static Font Resolve()
    {
        if (cached != null)
        {
            return cached;
        }

        try
        {
            cached = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (cached != null)
            {
                return cached;
            }
        }
        catch
        {
        }

        try
        {
            cached = Resources.GetBuiltinResource<Font>("Arial.ttf");
        }
        catch
        {
        }

        return cached;
    }
}
