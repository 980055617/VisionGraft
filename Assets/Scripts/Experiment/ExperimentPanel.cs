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

    // 局面ごとの文字の大きさと縦の詰め方。canvas の幅は 1200 固定で、高さと文字サイズだけ変える。
    // SizeMeters を canvas と同じ縦横比にすると文字が潰れない（1200×900 なら 4:3、1200×660 なら 0.6×0.33 m）。
    public struct Layout
    {
        public float canvasHeight;
        public int titleFontSize;
        public int bodyFontSize;
        public int buttonFontSize;
        public float buttonHeight;
    }

    public static readonly Layout DefaultLayout = new Layout
    {
        canvasHeight = CanvasHeight, titleFontSize = 56, bodyFontSize = 36, buttonFontSize = 32, buttonHeight = 84f,
    };

    // チュートリアルの説明用。字を大きく、縦を詰める（2026-09-11 実機「字が小さい」）。
    // 0.6×0.33 m で等倍になり、本文 44px は 1.2 m 先で約 22 mm（約 1°）。1 行 24 文字・4〜5 行が入る。
    public static readonly Layout CompactLargeTextLayout = new Layout
    {
        canvasHeight = 660f, titleFontSize = 60, bodyFontSize = 44, buttonFontSize = 40, buttonHeight = 100f,
    };

    // 試行中の「視聴を終了」用。見出し 1 行とボタン 1 つだけで本文は使わない（2026-09-11 実機「字が潰れている」）。
    // 0.6×0.19 m で等倍。見出し 56px ≈ 28 mm、ボタンの字 40px ≈ 20 mm（1.2 m 先）。
    // 高さ 380 のとき: 見出し中心 +100（50〜150）、ボタン列中心 −70（1 段 100px → −120〜−20）。
    public static readonly Layout TrialBarLayout = new Layout
    {
        canvasHeight = 380f, titleFontSize = 56, bodyFontSize = 36, buttonFontSize = 40, buttonHeight = 100f,
    };

    public Layout CurrentLayout = DefaultLayout;

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

    // ワールド固定の物（動画の画面）を基準に置くときの基準点。設定されていれば OffsetMeters は使わず、
    // 頭からこの点へ向かう視線上の DistanceMeters に置く（角度上の位置が基準点と一致する）。
    // 頭の向き基準だと、表示した瞬間に少し下を向いていただけで画面に被り、画面のコライダーに
    // レイを取られてボタンが押せなくなる（2026-09-11 実機指摘）。
    public Vector3? WorldAnchor;

    // keepPlacement: 表示中のパネルの位置を保ったまま中身だけ差し替える
    // （チュートリアルの段階送り。段階ごとに置き直すと読んでいる最中に飛ぶ）。
    public void Show(string title, string body, IList<ButtonSpec> buttonSpecs, bool keepPlacement = false)
    {
        bool wasVisible = IsVisible;
        EnsureRoot();
        if (!(keepPlacement && wasVisible))
        {
            placementLocked = false;
            WorldAnchor = null;
        }

        // SizeMeters は局面ごとに変わる（セットアップは大きく、試行中は小さく）ので、
        // ルート生成時ではなく表示のたびに反映する。
        ApplyPanelScale();
        ApplyLayout();

        UiComponentWriter.ApplyTextContent(titleText, title);
        UiComponentWriter.ApplyTextContent(bodyText, body);
        RebuildButtons(buttonSpecs);

        SceneObjectWriter.ApplyActive(root, true);
        SetLayerRecursively(root, UiLayer);
    }

    // worldAnchor の方向（頭から見た角度）に置く。画面の外側に出したいときに使う。
    public void ShowAnchored(string title, string body, IList<ButtonSpec> buttonSpecs, Vector3 worldAnchor)
    {
        Show(title, body, buttonSpecs, false);
        WorldAnchor = worldAnchor;
    }

    public void SetBody(string body)
    {
        UiComponentWriter.ApplyTextContent(bodyText, body);
    }

    // index 番目のボタンの文言と押せるかを、パネルを作り直さずに変える（試行中の「視聴を終了」を時間が来たら押せるようにする）。
    public void SetButtonState(int index, string label, bool interactable)
    {
        if (index < 0 || index >= buttons.Count || buttons[index] == null)
        {
            return;
        }

        Button button = buttons[index];
        UiComponentWriter.ApplyInteractable(button, interactable);
        Text text = button.GetComponentInChildren<Text>(true);
        UiComponentWriter.ApplyTextContent(text, label);
    }

    public void Hide()
    {
        if (root != null)
        {
            SceneObjectWriter.ApplyActive(root, false);
        }
    }

    // パネルを置く「正面」。動画の画面と同じ向きにする。
    // プレイヤーは画面の向きをトラッキング原点（再センタリングで決まる向き。頭の親 = TrackingSpace）の
    // ヨーで決めている（`useTrackingOriginForScreenFacing`、`ResolveScreenAnchor`）ので、ここも同じ元から取る。
    // 頭の向きは使わない。窓が切り替わるたびにそのとき向いていた方向に出ると、動画の正面からずれる
    // （2026-09-11 実機「常に動画と同じセットした正面に」）。原点が取れないときだけ頭の向きに逃げる。
    public static Vector3 ResolveFrontForward(Transform head)
    {
        Transform origin = head != null ? head.parent : null;
        Quaternion rotation = origin != null
            ? origin.rotation
            : (head != null ? head.rotation : Quaternion.identity);
        Vector3 flat = Vector3.ProjectOnPlane(rotation * Vector3.forward, Vector3.up);
        return flat.sqrMagnitude > 0.000001f ? flat.normalized : Vector3.forward;
    }

    // パネルは表示のたびに 1 度だけ置く。毎フレーム追従させると読んでいる最中にパネルが動いて酔うため、
    // 位置は最初のフレームで固定する。
    //
    // 向きは frontForward（動画の画面と同じ「セットした正面」。呼び出し側がトラッキング原点のヨーから渡す）で、
    // **頭の向きは使わない**。頭の向きで置くと、窓が切り替わるたびにそのとき向いていた方向に出て
    // 動画の正面からずれた（2026-09-11 実機「常に動画と同じセットした正面に」）。
    // WorldAnchor があるとき（画面の上など）はそちらが優先で、frontForward は向きの逃げにだけ使う。
    public void UpdatePlacement(Transform head, Vector3 frontForward)
    {
        if (root == null || !root.activeSelf || placementLocked || head == null)
        {
            return;
        }

        Canvas canvas = root.GetComponent<Canvas>();
        UiComponentWriter.ApplyWorldCameraIfMissing(canvas, ResolveCamera());

        // 水平成分だけ。上を向いたまま窓が切り替わっても目の高さの正面に出る。
        Vector3 flatForward = Vector3.ProjectOnPlane(frontForward, Vector3.up);
        if (flatForward.sqrMagnitude < 0.000001f)
        {
            flatForward = Vector3.forward;
        }
        flatForward.Normalize();

        float distance = Mathf.Max(0.2f, DistanceMeters);
        Vector3 pos;
        Vector3 anchorDir = WorldAnchor.HasValue ? WorldAnchor.Value - head.position : Vector3.zero;
        if (WorldAnchor.HasValue && anchorDir.sqrMagnitude > 0.000001f)
        {
            pos = head.position + anchorDir.normalized * distance;
        }
        else
        {
            Vector3 flatRight = Vector3.Cross(Vector3.up, flatForward);
            pos =
                head.position +
                flatForward * distance +
                flatRight * OffsetMeters.x +
                Vector3.up * OffsetMeters.y;
        }

        Vector3 toHead = (head.position - pos).normalized;
        if (Mathf.Abs(Vector3.Dot(toHead, Vector3.up)) > 0.98f)
        {
            // 真上・真下にあるときは正面に置いたときと同じ向きにする。
            toHead = -flatForward;
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

        float canvasHeight = Mathf.Max(100f, CurrentLayout.canvasHeight);
        TransformWriter.ApplyLocalScale(
            canvasRect,
            new Vector3(
                Mathf.Max(0.01f, SizeMeters.x) / CanvasWidth,
                Mathf.Max(0.01f, SizeMeters.y) / canvasHeight,
                1f));
    }

    // canvas の高さ・見出し/本文/ボタン列の位置・文字サイズを CurrentLayout に合わせる。
    // 位置は高さ H から決める: 見出しは上端から 40px、ボタン列は下端から 20px、本文はその間。
    private void ApplyLayout()
    {
        RectTransform canvasRect = root != null ? root.GetComponent<RectTransform>() : null;
        if (canvasRect == null || titleText == null || bodyText == null || buttonRow == null)
        {
            return;
        }

        float h = Mathf.Max(100f, CurrentLayout.canvasHeight);
        TransformWriter.ApplySizeDelta(canvasRect, new Vector2(CanvasWidth, h));

        float titleCenterY = h * 0.5f - 90f;
        float bodyTop = h * 0.5f - 150f;
        float bodyBottom = -h * 0.5f + 230f;
        float buttonRowCenterY = -(h * 0.5f - 120f);

        TransformWriter.ApplyCenteredRect(titleText.rectTransform, new Vector2(0f, titleCenterY), new Vector2(1080f, 100f));
        TransformWriter.ApplyCenteredRect(
            bodyText.rectTransform,
            new Vector2(0f, (bodyTop + bodyBottom) * 0.5f),
            new Vector2(1080f, Mathf.Max(50f, bodyTop - bodyBottom)));
        RectTransform rowRect = buttonRow as RectTransform;
        if (rowRect != null)
        {
            TransformWriter.ApplyCenteredRect(rowRect, new Vector2(0f, buttonRowCenterY), new Vector2(1080f, 200f));
        }

        Font font = ExperimentUiFont.Resolve();
        UiComponentWriter.ApplyTextStyle(titleText, font, CurrentLayout.titleFontSize, TextAnchor.MiddleCenter, Color.white);
        UiComponentWriter.ApplyTextStyle(bodyText, font, CurrentLayout.bodyFontSize, TextAnchor.UpperLeft, new Color(0.92f, 0.95f, 1f, 1f));
    }

    private void RebuildButtons(IList<ButtonSpec> specs)
    {
        ClearButtons();
        if (specs == null || specs.Count == 0)
        {
            return;
        }

        // 1 行あたり 3 個まで。それを超えたら 2 段目に折り返す。ボタン列の高さ（200）には 2 段しか
        // 入らないので、7 個以上のときは幅を詰めて 1 行 4 個にする（セットアップ画面の 7 個）。
        int perRow = specs.Count > 6 ? 4 : 3;
        float buttonWidth = perRow == 4 ? 250f : 330f;
        float buttonHeight = Mathf.Max(40f, CurrentLayout.buttonHeight);
        const float gapX = 20f;
        const float gapY = 16f;

        // 段全体をボタン列の枠（高さ 200）の上下中央に置く。以前は 1 段目を枠の中心に置き、
        // 2 段目をその下（−100）に足していたので、2 段目の下端がパネルの外（−472 < −450）に出ていた
        // （2026-09-11 実機「セットアップの欄からボタンがはみ出る」）。
        int rowCount = (specs.Count + perRow - 1) / perRow;
        float rowsHeight = rowCount * buttonHeight + (rowCount - 1) * gapY;
        float firstRowY = rowsHeight * 0.5f - buttonHeight * 0.5f;

        for (int i = 0; i < specs.Count; i++)
        {
            int row = i / perRow;
            int col = i % perRow;
            int countInRow = Mathf.Min(perRow, specs.Count - row * perRow);
            float rowWidth = countInRow * buttonWidth + (countInRow - 1) * gapX;
            float x = -rowWidth * 0.5f + buttonWidth * 0.5f + col * (buttonWidth + gapX);
            float y = firstRowY - row * (buttonHeight + gapY);

            ButtonSpec spec = specs[i];
            Button button = CreateButton(buttonRow, $"Button_{i}", spec.label, new Vector2(x, y),
                new Vector2(buttonWidth, buttonHeight), spec.onClick, CurrentLayout.buttonFontSize);
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
        Vector2 size, Action onClick, int fontSize)
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
        UiComponentWriter.ApplyTextStyle(text, ExperimentUiFont.Resolve(), Mathf.Max(8, fontSize), TextAnchor.MiddleCenter, Color.white);
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
