using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UI;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    private const float BundlePickerDistanceMeters = 1.1f;
    private const int BundlePickerEntriesPerPage = 8;
    private const bool BundlePickerFlipHorizontal = true;
    private static readonly Vector2 BundlePickerOffsetMeters = Vector2.zero;
    private static readonly Vector2 BundlePickerSizeMeters = new Vector2(1.05f, 0.82f);

    [Header("Bundle Picker")]
    public bool showBundlePickerOnStart = true;
    public string bundlePickerInitialDirectory = "/storage/emulated/0";
    public GameObject bundlePickerCanvasWithInteractionRayPrefab;

    private GameObject bundlePickerRoot;
    private Text bundlePickerPathText;
    private Text bundlePickerPageText;
    private Text bundlePickerStatusText;
    private Button bundlePickerPrevButton;
    private Button bundlePickerNextButton;
    private readonly List<Button> bundlePickerEntryButtons = new List<Button>();
    private readonly List<BundlePickerEntry> bundlePickerEntries = new List<BundlePickerEntry>();
    private string bundlePickerCurrentDirectory;
    private string bundlePickerSelectedPath;
    private int bundlePickerPageIndex;
    private bool bundlePickerDone;
    private bool bundlePickerActive;
    private bool bundlePickerPlacementLocked;

    private struct BundlePickerEntry
    {
        public string path;
        public bool isDirectory;
    }

    private IEnumerator RunBundlePickerFlowAndPrepareVideo()
    {
        yield return RequestStoragePermissionIfNeeded();

        OpenBundlePickerUi();
        while (!bundlePickerDone)
        {
            yield return null;
        }

        CloseBundlePickerUi();
        yield return EnsureBundleAndPrepareVideo(bundlePickerSelectedPath);
    }

    private IEnumerator RequestStoragePermissionIfNeeded()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.ExternalStorageRead))
        {
            UnityEngine.Android.Permission.RequestUserPermission(UnityEngine.Android.Permission.ExternalStorageRead);
            float timeout = 0.75f;
            while (timeout > 0f)
            {
                timeout = RuntimeClock.ResolveTimeoutRemaining(timeout, GetRuntimeUnscaledDeltaTime());
                yield return null;
            }
        }
#endif
        yield break;
    }

    private void OpenBundlePickerUi()
    {
        CloseBundlePickerUi();
        EnsureEventSystem();

        bundlePickerDone = false;
        bundlePickerSelectedPath = null;
        bundlePickerPageIndex = 0;
        bundlePickerActive = true;
        bundlePickerPlacementLocked = false;

        const float canvasW = 1200f;
        const float canvasH = 900f;

        bundlePickerRoot = CreateBundlePickerRootObject();
        SetLayerRecursively(bundlePickerRoot, 5); // UI layer
        Canvas canvas = RuntimeCanvasComponentFactory.EnsureCanvas(bundlePickerRoot);
        RuntimeCanvasWriter.ApplyWorldSpaceCamera(canvas, GetViewCamera());
        RuntimeCanvasComponentFactory.EnsureGraphicRaycaster(bundlePickerRoot, false);
        EnsureCanvasRaycasters(bundlePickerRoot);

        RectTransform canvasRect = bundlePickerRoot.GetComponent<RectTransform>();
        RuntimeUiTransformWriter.ApplySizeDelta(canvasRect, new Vector2(canvasW, canvasH));
        RuntimeUiTransformWriter.ApplyLocalScale(
            canvasRect,
            new Vector3(
                Mathf.Max(0.01f, BundlePickerSizeMeters.x) / canvasW,
                Mathf.Max(0.01f, BundlePickerSizeMeters.y) / canvasH,
                1f));

        Transform contentRoot = ResolveBundlePickerContentRoot(bundlePickerRoot);
        RectTransform panelRect = RuntimeUiElementFactory.CreateRectChild("Panel", contentRoot, out GameObject panelObj);
        RuntimeUiTransformWriter.ApplyStretchRect(
            panelRect,
            Vector2.zero,
            Vector2.one,
            Vector2.zero,
            Vector2.zero);
        Image panelImage = RuntimeUiElementFactory.AddImage(panelObj);
        RuntimeGraphicWriter.ApplyColor(panelImage, new Color(0.08f, 0.09f, 0.11f, 0.92f));

        CreateBundlePickerText(panelObj.transform, "Title", "Select bundle.svb", new Vector2(0f, 390f), new Vector2(1050f, 70f), 54, TextAnchor.MiddleCenter, Color.white);
        bundlePickerPathText = CreateBundlePickerText(panelObj.transform, "Path", string.Empty, new Vector2(0f, 325f), new Vector2(1060f, 62f), 28, TextAnchor.MiddleLeft, new Color(0.9f, 0.95f, 1f, 1f));
        bundlePickerStatusText = CreateBundlePickerText(panelObj.transform, "Status", string.Empty, new Vector2(0f, 282f), new Vector2(1060f, 38f), 24, TextAnchor.MiddleLeft, new Color(0.95f, 0.77f, 0.4f, 1f));

        CreateBundlePickerButton(panelObj.transform, "UpButton", "Up", new Vector2(-360f, 230f), new Vector2(200f, 56f), () => NavigateBundlePickerUp());
        CreateBundlePickerButton(panelObj.transform, "RefreshButton", "Refresh", new Vector2(-130f, 230f), new Vector2(220f, 56f), RefreshBundlePickerEntries);
        CreateBundlePickerButton(panelObj.transform, "DefaultButton", "Use Default", new Vector2(275f, 230f), new Vector2(260f, 56f), UseDefaultBundleAndClosePicker);

        bundlePickerEntryButtons.Clear();
        for (int i = 0; i < BundlePickerEntriesPerPage; i++)
        {
            int localIndex = i;
            float y = 160f - i * 64f;
            Button button = CreateBundlePickerButton(
                panelObj.transform,
                $"EntryButton_{i}",
                string.Empty,
                new Vector2(0f, y),
                new Vector2(1030f, 54f),
                () => OnBundlePickerEntryClicked(localIndex),
                TextAnchor.MiddleLeft);
            bundlePickerEntryButtons.Add(button);
        }

        bundlePickerPrevButton = CreateBundlePickerButton(panelObj.transform, "PrevButton", "< Prev", new Vector2(-230f, -365f), new Vector2(180f, 56f), PrevBundlePickerPage);
        bundlePickerPageText = CreateBundlePickerText(panelObj.transform, "PageText", "Page 1/1", new Vector2(0f, -365f), new Vector2(260f, 56f), 28, TextAnchor.MiddleCenter, Color.white);
        bundlePickerNextButton = CreateBundlePickerButton(panelObj.transform, "NextButton", "Next >", new Vector2(230f, -365f), new Vector2(180f, 56f), NextBundlePickerPage);

        bundlePickerCurrentDirectory = ResolveBundlePickerStartDirectory();
        RefreshBundlePickerEntries();
        SetLayerRecursively(bundlePickerRoot, 5); // ensure all descendants are UI layer
        UpdateBundlePickerPlacement();
    }

    private void CloseBundlePickerUi()
    {
        bundlePickerActive = false;
        bundlePickerPlacementLocked = false;
        if (bundlePickerRoot != null)
        {
            RuntimeUnityEventBinding.ClearButtonListenersInChildren(bundlePickerRoot);
            GameObjectLifecycleWriter.DestroyObject(bundlePickerRoot);
            bundlePickerRoot = null;
        }
    }

    private void UpdateBundlePickerPlacement()
    {
        if (!bundlePickerActive || bundlePickerRoot == null || bundlePickerPlacementLocked)
        {
            return;
        }

        Canvas canvas = bundlePickerRoot.GetComponent<Canvas>();
        RuntimeCanvasWriter.ApplyWorldCameraIfMissing(canvas, GetViewCamera());

        Transform head = GetViewOrHeadTransform();
        if (head == null)
        {
            return;
        }

        float distance = BundlePickerDistanceMeters > 0f
            ? BundlePickerDistanceMeters
            : screenDistanceMeters;
        Vector3 pos =
            head.position +
            head.forward * Mathf.Max(0.2f, distance) +
            head.right * BundlePickerOffsetMeters.x +
            head.up * BundlePickerOffsetMeters.y;
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
        if (BundlePickerFlipHorizontal)
        {
            rot *= Quaternion.Euler(0f, 180f, 0f);
        }
        RuntimeUiTransformWriter.ApplyWorldPose(bundlePickerRoot.transform, pos, rot);
        bundlePickerPlacementLocked = true;
    }

    private GameObject CreateBundlePickerRootObject()
    {
        return RuntimeUiRootFactory.Create("BundlePickerUI", bundlePickerCanvasWithInteractionRayPrefab);
    }

    private static Transform ResolveBundlePickerContentRoot(GameObject root)
    {
        if (root == null)
        {
            return null;
        }

        Transform interactionRoot = FindDeepChildByName(root.transform, "ISDK_RayCanvasInteraction");
        if (interactionRoot != null)
        {
            Transform surface = FindDeepChildByName(interactionRoot, "Surface");
            if (surface != null)
            {
                return surface;
            }
        }

        return root.transform;
    }

    private void RefreshBundlePickerEntries()
    {
        bundlePickerEntries.Clear();
        string status = string.Empty;

        if (!string.IsNullOrEmpty(bundlePickerCurrentDirectory) && Directory.Exists(bundlePickerCurrentDirectory))
        {
            try
            {
                string[] dirs = Directory.GetDirectories(bundlePickerCurrentDirectory);
                System.Array.Sort(dirs, System.StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < dirs.Length; i++)
                {
                    bundlePickerEntries.Add(new BundlePickerEntry { path = dirs[i], isDirectory = true });
                }
            }
            catch (System.Exception ex)
            {
                status = $"Cannot list directories: {ex.GetType().Name}";
            }

            try
            {
                string[] files = Directory.GetFiles(bundlePickerCurrentDirectory);
                System.Array.Sort(files, System.StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < files.Length; i++)
                {
                    string ext = Path.GetExtension(files[i]);
                    if (!string.Equals(ext, ".svb", System.StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    bundlePickerEntries.Add(new BundlePickerEntry { path = files[i], isDirectory = false });
                }
            }
            catch (System.Exception ex)
            {
                if (string.IsNullOrEmpty(status))
                {
                    status = $"Cannot list files: {ex.GetType().Name}";
                }
            }
        }
        else
        {
            status = "Directory not found.";
        }

        int pageCount = GetBundlePickerPageCount();
        bundlePickerPageIndex = Mathf.Clamp(bundlePickerPageIndex, 0, Mathf.Max(0, pageCount - 1));

        if (bundlePickerPathText != null)
        {
            RuntimeTextWriter.ApplyContent(bundlePickerPathText, $"Path: {bundlePickerCurrentDirectory}");
        }
        if (bundlePickerStatusText != null)
        {
            RuntimeTextWriter.ApplyContent(
                bundlePickerStatusText,
                string.IsNullOrEmpty(status) ? $"Found {bundlePickerEntries.Count} entries" : status);
        }

        UpdateBundlePickerEntryButtons();
    }

    private void UpdateBundlePickerEntryButtons()
    {
        int perPage = BundlePickerEntriesPerPage;
        int startIndex = bundlePickerPageIndex * perPage;

        for (int i = 0; i < bundlePickerEntryButtons.Count; i++)
        {
            Button button = bundlePickerEntryButtons[i];
            if (button == null)
            {
                continue;
            }

            int entryIndex = startIndex + i;
            Text label = button.GetComponentInChildren<Text>(true);
            if (entryIndex < bundlePickerEntries.Count)
            {
                BundlePickerEntry entry = bundlePickerEntries[entryIndex];
                if (label != null)
                {
                    string name = Path.GetFileName(entry.path);
                    if (string.IsNullOrEmpty(name))
                    {
                        name = entry.path;
                    }
                    RuntimeTextWriter.ApplyContent(label, entry.isDirectory ? $"[DIR] {name}" : name);
                    RuntimeTextWriter.ApplyStyle(label, label.font, label.fontSize, TextAnchor.MiddleLeft, label.color);
                }

                GameObjectLifecycleWriter.ApplyActive(button.gameObject, true);
                RuntimeSelectableWriter.ApplyInteractable(button, true);
            }
            else
            {
                if (label != null)
                {
                    RuntimeTextWriter.ApplyContent(label, string.Empty);
                }
                GameObjectLifecycleWriter.ApplyActive(button.gameObject, false);
            }
        }

        int pageCount = GetBundlePickerPageCount();
        if (bundlePickerPageText != null)
        {
            RuntimeTextWriter.ApplyContent(bundlePickerPageText, $"Page {bundlePickerPageIndex + 1}/{Mathf.Max(1, pageCount)}");
        }
        if (bundlePickerPrevButton != null)
        {
            RuntimeSelectableWriter.ApplyInteractable(bundlePickerPrevButton, bundlePickerPageIndex > 0);
        }
        if (bundlePickerNextButton != null)
        {
            RuntimeSelectableWriter.ApplyInteractable(bundlePickerNextButton, bundlePickerPageIndex < pageCount - 1);
        }
    }

    private void OnBundlePickerEntryClicked(int localIndex)
    {
        int perPage = BundlePickerEntriesPerPage;
        int entryIndex = bundlePickerPageIndex * perPage + localIndex;
        if (entryIndex < 0 || entryIndex >= bundlePickerEntries.Count)
        {
            return;
        }

        BundlePickerEntry entry = bundlePickerEntries[entryIndex];
        if (entry.isDirectory)
        {
            bundlePickerCurrentDirectory = entry.path;
            bundlePickerPageIndex = 0;
            RefreshBundlePickerEntries();
            return;
        }

        bundlePickerSelectedPath = entry.path;
        bundlePickerDone = true;
    }

    private void NavigateBundlePickerUp()
    {
        if (string.IsNullOrEmpty(bundlePickerCurrentDirectory))
        {
            return;
        }

        DirectoryInfo parent = Directory.GetParent(bundlePickerCurrentDirectory);
        if (parent == null)
        {
            return;
        }

        bundlePickerCurrentDirectory = parent.FullName;
        bundlePickerPageIndex = 0;
        RefreshBundlePickerEntries();
    }

    private void PrevBundlePickerPage()
    {
        bundlePickerPageIndex = Mathf.Max(0, bundlePickerPageIndex - 1);
        UpdateBundlePickerEntryButtons();
    }

    private void NextBundlePickerPage()
    {
        int pageCount = GetBundlePickerPageCount();
        bundlePickerPageIndex = Mathf.Min(Mathf.Max(0, pageCount - 1), bundlePickerPageIndex + 1);
        UpdateBundlePickerEntryButtons();
    }

    private int GetBundlePickerPageCount()
    {
        int perPage = BundlePickerEntriesPerPage;
        if (bundlePickerEntries.Count <= 0)
        {
            return 1;
        }

        return Mathf.CeilToInt(bundlePickerEntries.Count / (float)perPage);
    }

    private void UseDefaultBundleAndClosePicker()
    {
        bundlePickerSelectedPath = null;
        bundlePickerDone = true;
    }

    private string ResolveBundlePickerStartDirectory()
    {
        if (!string.IsNullOrEmpty(bundlePickerInitialDirectory) && Directory.Exists(bundlePickerInitialDirectory))
        {
            return bundlePickerInitialDirectory;
        }

        string[] candidates =
        {
            "/storage/emulated/0",
            "/sdcard",
            Application.persistentDataPath,
            Application.dataPath,
        };

        for (int i = 0; i < candidates.Length; i++)
        {
            string candidate = candidates[i];
            if (!string.IsNullOrEmpty(candidate) && Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return Application.persistentDataPath;
    }

    private Text CreateBundlePickerText(Transform parent, string name, string initialText, Vector2 anchoredPos, Vector2 size, int fontSize, TextAnchor alignment, Color color)
    {
        RectTransform rect = RuntimeUiElementFactory.CreateRectChild(name, parent, out GameObject obj);
        SetLayerRecursively(obj, 5); // UI layer
        RuntimeUiTransformWriter.ApplyCenteredRect(rect, anchoredPos, size);

        Text text = RuntimeUiElementFactory.AddText(obj);
        RuntimeTextWriter.ApplyStyle(text, GetRuntimeUiFont(), fontSize, alignment, color);
        RuntimeTextWriter.ApplyContent(text, initialText);
        RuntimeTextWriter.ApplyInteraction(text, false);
        return text;
    }

    private Button CreateBundlePickerButton(Transform parent, string name, string label, Vector2 anchoredPos, Vector2 size, UnityEngine.Events.UnityAction onClick, TextAnchor alignment = TextAnchor.MiddleCenter)
    {
        RectTransform rect = RuntimeUiElementFactory.CreateRectChild(name, parent, out GameObject obj);
        SetLayerRecursively(obj, 5); // UI layer
        RuntimeUiTransformWriter.ApplyCenteredRect(rect, anchoredPos, size);

        Image image = RuntimeUiElementFactory.AddImage(obj);
        RuntimeGraphicWriter.ApplyColor(image, new Color(0.22f, 0.26f, 0.34f, 0.95f));

        Button button = RuntimeUiElementFactory.AddButton(obj);
        ColorBlock colors = button.colors;
        colors.normalColor = image.color;
        colors.highlightedColor = new Color(0.30f, 0.35f, 0.44f, 0.98f);
        colors.pressedColor = new Color(0.16f, 0.20f, 0.28f, 1f);
        colors.selectedColor = colors.highlightedColor;
        colors.disabledColor = new Color(0.18f, 0.18f, 0.18f, 0.6f);
        RuntimeGraphicWriter.ApplyColors(button, colors);
        RuntimeGraphicWriter.ApplyTargetGraphic(button, image);
        if (onClick != null)
        {
            RuntimeUnityEventBinding.Bind(button, onClick);
        }

        RectTransform textRect = RuntimeUiElementFactory.CreateRectChild("Label", obj.transform, out GameObject textObj);
        RuntimeUiTransformWriter.ApplyStretchRect(
            textRect,
            Vector2.zero,
            Vector2.one,
            new Vector2(14f, 0f),
            new Vector2(-14f, 0f));

        Text text = RuntimeUiElementFactory.AddText(textObj);
        RuntimeTextWriter.ApplyStyle(text, GetRuntimeUiFont(), 30, alignment, Color.white);
        RuntimeTextWriter.ApplyContent(text, label);
        RuntimeTextWriter.ApplyInteraction(text, false);
        SetLayerRecursively(obj, 5); // include label child

        return button;
    }

    private static void SetLayerRecursively(GameObject root, int layer)
    {
        if (root == null)
        {
            return;
        }

        root.layer = layer;
        Transform tr = root.transform;
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
