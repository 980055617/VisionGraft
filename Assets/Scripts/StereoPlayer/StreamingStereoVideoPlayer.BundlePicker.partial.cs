using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UI;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    [Header("Bundle Picker")]
    public bool skipSelection = false;
    public string defaultsvb = "bundle.svb";
    public bool showBundlePickerOnStart = true;
    public bool fallbackToStreamingBundleWhenCanceled = true;
    public string bundlePickerInitialDirectory = "/storage/emulated/0";
    public float bundlePickerDistanceMeters = 1.1f;
    public Vector2 bundlePickerOffsetMeters = Vector2.zero;
    public Vector2 bundlePickerSizeMeters = new Vector2(1.05f, 0.82f);
    [Range(4, 16)] public int bundlePickerEntriesPerPage = 8;
    public bool bundlePickerFlipHorizontal = true;
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

        if (!string.IsNullOrEmpty(bundlePickerSelectedPath))
        {
            yield return EnsureBundleAndPrepareVideo(bundlePickerSelectedPath);
            yield break;
        }

        if (fallbackToStreamingBundleWhenCanceled)
        {
            yield return EnsureDefaultSvbAndPrepareVideo();
        }
    }

    private IEnumerator EnsureDefaultSvbAndPrepareVideo()
    {
        string defaultPath = ResolveDefaultSvbPath();
        if (string.IsNullOrEmpty(defaultPath))
        {
            yield return EnsureBundleAndPrepareVideo();
            yield break;
        }

        yield return EnsureBundleAndPrepareVideo(defaultPath);
    }

    private string ResolveDefaultSvbPath()
    {
        string candidate = string.IsNullOrEmpty(defaultsvb) ? bundleFileName : defaultsvb.Trim();
        if (string.IsNullOrEmpty(candidate) ||
            string.Equals(candidate, bundleFileName, System.StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (Path.IsPathRooted(candidate))
        {
            return candidate;
        }

        string persistentCandidate = Path.Combine(Application.persistentDataPath, candidate);
        if (File.Exists(persistentCandidate))
        {
            return persistentCandidate;
        }

        string sdcardCandidate = Path.Combine("/storage/emulated/0", candidate);
        if (File.Exists(sdcardCandidate))
        {
            return sdcardCandidate;
        }

        return candidate;
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
                timeout -= Time.unscaledDeltaTime;
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
        Canvas canvas = bundlePickerRoot.GetComponent<Canvas>();
        if (canvas == null)
        {
            canvas = bundlePickerRoot.AddComponent<Canvas>();
        }
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.worldCamera = GetViewCamera();
        if (bundlePickerRoot.GetComponent<GraphicRaycaster>() == null)
        {
            bundlePickerRoot.AddComponent<GraphicRaycaster>();
        }
        EnsureCanvasRaycasters(bundlePickerRoot);

        RectTransform canvasRect = bundlePickerRoot.GetComponent<RectTransform>();
        canvasRect.sizeDelta = new Vector2(canvasW, canvasH);
        canvasRect.localScale = new Vector3(
            Mathf.Max(0.01f, bundlePickerSizeMeters.x) / canvasW,
            Mathf.Max(0.01f, bundlePickerSizeMeters.y) / canvasH,
            1f);

        Transform contentRoot = ResolveBundlePickerContentRoot(bundlePickerRoot);
        GameObject panelObj = new GameObject("Panel");
        panelObj.transform.SetParent(contentRoot, false);
        RectTransform panelRect = panelObj.AddComponent<RectTransform>();
        panelRect.anchorMin = Vector2.zero;
        panelRect.anchorMax = Vector2.one;
        panelRect.offsetMin = Vector2.zero;
        panelRect.offsetMax = Vector2.zero;
        Image panelImage = panelObj.AddComponent<Image>();
        panelImage.color = new Color(0.08f, 0.09f, 0.11f, 0.92f);

        CreateBundlePickerText(panelObj.transform, "Title", "Select bundle.svb", new Vector2(0f, 390f), new Vector2(1050f, 70f), 54, TextAnchor.MiddleCenter, Color.white);
        bundlePickerPathText = CreateBundlePickerText(panelObj.transform, "Path", string.Empty, new Vector2(0f, 325f), new Vector2(1060f, 62f), 28, TextAnchor.MiddleLeft, new Color(0.9f, 0.95f, 1f, 1f));
        bundlePickerStatusText = CreateBundlePickerText(panelObj.transform, "Status", string.Empty, new Vector2(0f, 282f), new Vector2(1060f, 38f), 24, TextAnchor.MiddleLeft, new Color(0.95f, 0.77f, 0.4f, 1f));

        CreateBundlePickerButton(panelObj.transform, "UpButton", "Up", new Vector2(-360f, 230f), new Vector2(200f, 56f), () => NavigateBundlePickerUp());
        CreateBundlePickerButton(panelObj.transform, "RefreshButton", "Refresh", new Vector2(-130f, 230f), new Vector2(220f, 56f), RefreshBundlePickerEntries);
        CreateBundlePickerButton(panelObj.transform, "DefaultButton", "Use Default", new Vector2(145f, 230f), new Vector2(260f, 56f), UseDefaultBundleAndClosePicker);
        CreateBundlePickerButton(panelObj.transform, "CancelButton", "Cancel", new Vector2(405f, 230f), new Vector2(190f, 56f), CancelBundlePicker);

        bundlePickerEntryButtons.Clear();
        for (int i = 0; i < Mathf.Clamp(bundlePickerEntriesPerPage, 4, 16); i++)
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
            Destroy(bundlePickerRoot);
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
        if (canvas != null && canvas.worldCamera == null)
        {
            canvas.worldCamera = GetViewCamera();
        }

        Transform head = GetViewOrHeadTransform();
        if (head == null)
        {
            return;
        }

        float distance = bundlePickerDistanceMeters > 0f
            ? bundlePickerDistanceMeters
            : screenDistanceMeters;
        Vector3 pos =
            head.position +
            head.forward * Mathf.Max(0.2f, distance) +
            head.right * bundlePickerOffsetMeters.x +
            head.up * bundlePickerOffsetMeters.y;
        bundlePickerRoot.transform.position = pos;
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
        if (bundlePickerFlipHorizontal)
        {
            rot *= Quaternion.Euler(0f, 180f, 0f);
        }
        bundlePickerRoot.transform.rotation = rot;
        bundlePickerPlacementLocked = true;
    }

    private GameObject CreateBundlePickerRootObject()
    {
        if (bundlePickerCanvasWithInteractionRayPrefab != null)
        {
            GameObject instance = Instantiate(bundlePickerCanvasWithInteractionRayPrefab);
            instance.name = "BundlePickerUI";
            return instance;
        }

        return new GameObject("BundlePickerUI");
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
            bundlePickerPathText.text = $"Path: {bundlePickerCurrentDirectory}";
        }
        if (bundlePickerStatusText != null)
        {
            bundlePickerStatusText.text = string.IsNullOrEmpty(status)
                ? $"Found {bundlePickerEntries.Count} entries"
                : status;
        }

        UpdateBundlePickerEntryButtons();
    }

    private void UpdateBundlePickerEntryButtons()
    {
        int perPage = Mathf.Clamp(bundlePickerEntriesPerPage, 4, 16);
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
                    label.text = entry.isDirectory ? $"[DIR] {name}" : name;
                    label.alignment = TextAnchor.MiddleLeft;
                }

                button.gameObject.SetActive(true);
                button.interactable = true;
            }
            else
            {
                if (label != null)
                {
                    label.text = string.Empty;
                }
                button.gameObject.SetActive(false);
            }
        }

        int pageCount = GetBundlePickerPageCount();
        if (bundlePickerPageText != null)
        {
            bundlePickerPageText.text = $"Page {bundlePickerPageIndex + 1}/{Mathf.Max(1, pageCount)}";
        }
        if (bundlePickerPrevButton != null)
        {
            bundlePickerPrevButton.interactable = bundlePickerPageIndex > 0;
        }
        if (bundlePickerNextButton != null)
        {
            bundlePickerNextButton.interactable = bundlePickerPageIndex < pageCount - 1;
        }
    }

    private void OnBundlePickerEntryClicked(int localIndex)
    {
        int perPage = Mathf.Clamp(bundlePickerEntriesPerPage, 4, 16);
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
        int perPage = Mathf.Clamp(bundlePickerEntriesPerPage, 4, 16);
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

    private void CancelBundlePicker()
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
        GameObject obj = new GameObject(name);
        obj.transform.SetParent(parent, false);
        SetLayerRecursively(obj, 5); // UI layer
        RectTransform rect = obj.AddComponent<RectTransform>();
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = anchoredPos;
        rect.sizeDelta = size;

        Text text = obj.AddComponent<Text>();
        text.text = initialText;
        text.font = GetRuntimeUiFont();
        text.fontSize = fontSize;
        text.alignment = alignment;
        text.color = color;
        text.raycastTarget = false;
        return text;
    }

    private Button CreateBundlePickerButton(Transform parent, string name, string label, Vector2 anchoredPos, Vector2 size, UnityEngine.Events.UnityAction onClick, TextAnchor alignment = TextAnchor.MiddleCenter)
    {
        GameObject obj = new GameObject(name);
        obj.transform.SetParent(parent, false);
        SetLayerRecursively(obj, 5); // UI layer
        RectTransform rect = obj.AddComponent<RectTransform>();
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = anchoredPos;
        rect.sizeDelta = size;

        Image image = obj.AddComponent<Image>();
        image.color = new Color(0.22f, 0.26f, 0.34f, 0.95f);

        Button button = obj.AddComponent<Button>();
        ColorBlock colors = button.colors;
        colors.normalColor = image.color;
        colors.highlightedColor = new Color(0.30f, 0.35f, 0.44f, 0.98f);
        colors.pressedColor = new Color(0.16f, 0.20f, 0.28f, 1f);
        colors.selectedColor = colors.highlightedColor;
        colors.disabledColor = new Color(0.18f, 0.18f, 0.18f, 0.6f);
        button.colors = colors;
        button.targetGraphic = image;
        if (onClick != null)
        {
            button.onClick.AddListener(onClick);
        }

        GameObject textObj = new GameObject("Label");
        textObj.transform.SetParent(obj.transform, false);
        RectTransform textRect = textObj.AddComponent<RectTransform>();
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = new Vector2(14f, 0f);
        textRect.offsetMax = new Vector2(-14f, 0f);

        Text text = textObj.AddComponent<Text>();
        text.text = label;
        text.font = GetRuntimeUiFont();
        text.fontSize = 30;
        text.alignment = alignment;
        text.color = Color.white;
        text.raycastTarget = false;
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
