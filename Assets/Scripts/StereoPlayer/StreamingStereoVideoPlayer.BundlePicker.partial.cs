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

    // ユーザーがピッカーを 1 度でも操作したか。置き直しをやめる判定に使う。
    private bool bundlePickerInteracted;

    // 開いた時刻。ここから数フレームだけ置き直す。
    private float bundlePickerOpenedAt;
    private const float BundlePickerPlacementSettleSeconds = 0.5f;

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
        // 書き込みも要る（VisionGraft フォルダの作成）。読みだけ要求していたので追加した。
        if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.ExternalStorageRead) ||
            !UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.ExternalStorageWrite))
        {
            UnityEngine.Android.Permission.RequestUserPermission(UnityEngine.Android.Permission.ExternalStorageRead);
            UnityEngine.Android.Permission.RequestUserPermission(UnityEngine.Android.Permission.ExternalStorageWrite);
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
        bundlePickerInteracted = false;
        bundlePickerOpenedAt = Time.unscaledTime;

        const float canvasW = 1200f;
        const float canvasH = 900f;

        bundlePickerRoot = CreateBundlePickerRootObject();
        SetLayerRecursively(bundlePickerRoot, 5); // UI layer
        Canvas canvas = RuntimeCanvasComponentFactory.EnsureCanvas(bundlePickerRoot);
        UiComponentWriter.ApplyWorldSpaceCamera(canvas, GetViewCamera());
        RuntimeCanvasComponentFactory.EnsureGraphicRaycaster(bundlePickerRoot, false);
        EnsureCanvasRaycasters(bundlePickerRoot);

        RectTransform canvasRect = bundlePickerRoot.GetComponent<RectTransform>();
        TransformWriter.ApplySizeDelta(canvasRect, new Vector2(canvasW, canvasH));
        TransformWriter.ApplyLocalScale(
            canvasRect,
            new Vector3(
                Mathf.Max(0.01f, BundlePickerSizeMeters.x) / canvasW,
                Mathf.Max(0.01f, BundlePickerSizeMeters.y) / canvasH,
                1f));

        Transform contentRoot = ResolveBundlePickerContentRoot(bundlePickerRoot);
        RectTransform panelRect = RuntimeUiElementFactory.CreateRectChild("Panel", contentRoot, out GameObject panelObj);
        TransformWriter.ApplyStretchRect(
            panelRect,
            Vector2.zero,
            Vector2.one,
            Vector2.zero,
            Vector2.zero);
        Image panelImage = RuntimeUiElementFactory.AddImage(panelObj);
        UiComponentWriter.ApplyGraphicColor(panelImage, new Color(0.08f, 0.09f, 0.11f, 0.92f));

        CreateBundlePickerText(panelObj.transform, "Title", "Select bundle.svb", new Vector2(0f, 390f), new Vector2(1050f, 70f), 54, TextAnchor.MiddleCenter, Color.white);
        bundlePickerPathText = CreateBundlePickerText(panelObj.transform, "Path", string.Empty, new Vector2(0f, 325f), new Vector2(1060f, 62f), 28, TextAnchor.MiddleLeft, new Color(0.9f, 0.95f, 1f, 1f));
        bundlePickerStatusText = CreateBundlePickerText(panelObj.transform, "Status", string.Empty, new Vector2(0f, 282f), new Vector2(1060f, 38f), 24, TextAnchor.MiddleLeft, new Color(0.95f, 0.77f, 0.4f, 1f));

        CreateBundlePickerButton(panelObj.transform, "UpButton", "Up", new Vector2(-360f, 230f), new Vector2(200f, 56f), () => NavigateBundlePickerUp());
        CreateBundlePickerButton(panelObj.transform, "RefreshButton", "Refresh", new Vector2(-130f, 230f), new Vector2(220f, 56f), RefreshBundlePickerEntries);
        CreateBundlePickerButton(panelObj.transform, "DefaultButton", "Use Default", new Vector2(275f, 230f), new Vector2(260f, 56f), UseDefaultBundleAndClosePicker);

        // bundle を選ぶ前に入口へ戻る道。実験中は出さない（CanReturnToHomeScene）。
        if (CanReturnToHomeScene())
        {
            CreateBundlePickerButton(panelObj.transform, "HomeButton", "Home", new Vector2(-360f, -390f), new Vector2(200f, 56f), ReturnToHomeScene);
        }

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
            SceneObjectWriter.DestroyObject(bundlePickerRoot);
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
        UiComponentWriter.ApplyWorldCameraIfMissing(canvas, GetViewCamera());

        Transform head = GetViewOrHeadTransform();
        if (head == null)
        {
            return;
        }

        float distance = BundlePickerDistanceMeters > 0f
            ? BundlePickerDistanceMeters
            : screenDistanceMeters;

        // **head.forward をそのまま使わない。** 開いた瞬間に少し上を向いていると、
        // 1.1m 先ではその角度ぶん持ち上がる（30 度で 0.55m）。実機で「目線を上に
        // やらないと見えない」位置に出ていたのがこれ（2026-08-28）。
        // 水平面に射影して、**常に目の高さ**に置く。
        Vector3 flatForward = Vector3.ProjectOnPlane(head.forward, Vector3.up);
        if (flatForward.sqrMagnitude < 0.000001f)
        {
            // 真上か真下を向いている。頭の up を使って前方を作る。
            flatForward = Vector3.ProjectOnPlane(head.up, Vector3.up);
        }
        if (flatForward.sqrMagnitude < 0.000001f)
        {
            flatForward = Vector3.forward;
        }
        flatForward.Normalize();

        Vector3 pos =
            head.position +
            flatForward * Mathf.Max(0.2f, distance) +
            Vector3.Cross(Vector3.up, flatForward) * -BundlePickerOffsetMeters.x +
            Vector3.up * BundlePickerOffsetMeters.y;
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
        TransformWriter.ApplyPose(bundlePickerRoot.transform, pos, rot);

        // **追従はしない。** ただし開いた直後の一瞬だけは置き直す。
        // tracking origin が Device に切り替わるまでの数フレームで固定すると、
        // 切り替えでワールドがずれてパネルが視界の外へ飛ぶ（2026-08-28 実機）。
        // 落ち着いたら固定して、閲覧中は動かさない。
        if (Time.unscaledTime - bundlePickerOpenedAt >= BundlePickerPlacementSettleSeconds || bundlePickerInteracted)
        {
            bundlePickerPlacementLocked = true;
        }
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
            UiComponentWriter.ApplyTextContent(bundlePickerPathText, $"Path: {bundlePickerCurrentDirectory}");
        }
        if (bundlePickerStatusText != null)
        {
            UiComponentWriter.ApplyTextContent(
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
                    UiComponentWriter.ApplyTextContent(label, entry.isDirectory ? $"[DIR] {name}" : name);
                    UiComponentWriter.ApplyTextStyle(label, label.font, label.fontSize, TextAnchor.MiddleLeft, label.color);
                }

                SceneObjectWriter.ApplyActive(button.gameObject, true);
                UiComponentWriter.ApplyInteractable(button, true);
            }
            else
            {
                if (label != null)
                {
                    UiComponentWriter.ApplyTextContent(label, string.Empty);
                }
                SceneObjectWriter.ApplyActive(button.gameObject, false);
            }
        }

        int pageCount = GetBundlePickerPageCount();
        if (bundlePickerPageText != null)
        {
            UiComponentWriter.ApplyTextContent(bundlePickerPageText, $"Page {bundlePickerPageIndex + 1}/{Mathf.Max(1, pageCount)}");
        }
        if (bundlePickerPrevButton != null)
        {
            UiComponentWriter.ApplyInteractable(bundlePickerPrevButton, bundlePickerPageIndex > 0);
        }
        if (bundlePickerNextButton != null)
        {
            UiComponentWriter.ApplyInteractable(bundlePickerNextButton, bundlePickerPageIndex < pageCount - 1);
        }
    }

    private void OnBundlePickerEntryClicked(int localIndex)
    {
        bundlePickerInteracted = true;
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
        bundlePickerInteracted = true;
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
        bundlePickerInteracted = true;
        bundlePickerPageIndex = Mathf.Max(0, bundlePickerPageIndex - 1);
        UpdateBundlePickerEntryButtons();
    }

    private void NextBundlePickerPage()
    {
        bundlePickerInteracted = true;
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

    // 起点はコードで決める。Inspector の公開フィールドにしていたが、**シーンの serialize 値が
    // コードの既定を黙って上書きする**ので事故になっていた（2026-08-28）。実際 TestScene /
    // TrialScene には persistentDataPath
    // （/storage/emulated/0/Android/data/<package>/files）が焼き込まれていて、
    // コードに書いてある "/storage/emulated/0" は一度も効いていなかった。
    // そこは Android 11 以降 MTP から見えないので、PC から .svb を置きづらい場所でもある。
    //
    // 上から順に、実在する最初のディレクトリを使う。
    private const string BundlePickerFolderName = "VisionGraft";

    // 共有ストレージの根。同じ場所への別名なので、実在した最初の 1 つだけ使う。
    private static readonly string[] BundlePickerSharedStorageRoots =
    {
        "/storage/emulated/0",
        "/sdcard",
    };

    // 探索先。上から順に、実在する最初のものを使う。
    //
    // **persistentDataPath を先頭に置いている理由**（2026-08-28）:
    // targetSdk 32 では scoped storage が効き、READ_EXTERNAL_STORAGE を granted されていても
    // /sdcard 直下の**メディア以外のファイルは File API から見えない**（.svb はメディア扱いされない）。
    // 実機で /sdcard/VisionGraft に 3 本置いたのに "found 0 entries" になったのがこれ。
    // アプリ自身の外部ディレクトリ（/sdcard/Android/data/<pkg>/files）は権限不要で必ず読める。
    //
    // /storage/emulated/0/VisionGraft は MANAGE_EXTERNAL_STORAGE（全ファイルアクセス）が
    // 付いていれば読める。MTP から見えるので PC からの置き換えが楽。付いていなければ
    // 素通りするだけなので、両方を並べておく。
    private static string[] BuildBundleSearchDirectories()
    {
        return new[]
        {
            // 権限不要。adb push は通るが MTP からは見えない。
            Application.persistentDataPath,
            // 全ファイルアクセスがあれば読める。MTP から見える。
            "/storage/emulated/0/" + BundlePickerFolderName,
            "/sdcard/" + BundlePickerFolderName,
            "/storage/emulated/0",
            "/sdcard",
        };
    }

    // 共有ストレージの直下に VisionGraft フォルダが無ければ作る。
    //
    // 新しいヘッドセットには当然無い。**PC から .svb を置く先として先に見えていてほしい**
    // ので、アプリ側で作る（2026-08-28 の要望）。
    //
    // 失敗しても黙って続ける。作れなければ ResolveBundlePickerStartDirectory が
    // /storage/emulated/0 に落ちるだけで、機能は失われない。
    // targetSdk 32 の scoped storage で書けるかは実機で確認すること。
    private static int CountVisibleBundles(string dir)
    {
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            return 0;
        }

        try
        {
            int n = Directory.GetFiles(dir, "*.svb").Length;
            Debug.Log($"[BundlePicker] {dir}: {n} bundle(s) visible");
            return n;
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"[BundlePicker] cannot list {dir}: {ex.GetType().Name}");
            return 0;
        }
    }


    // 全ファイルアクセス（MANAGE_EXTERNAL_STORAGE）が付いているかを 1 度だけログに出す。
    // これが false だと /sdcard 直下の .svb は見えない（scoped storage）。
    private static bool loggedExternalStorageAccessState;

    private static void LogExternalStorageAccessState()
    {
        if (loggedExternalStorageAccessState)
        {
            return;
        }

        loggedExternalStorageAccessState = true;
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            using (var env = new AndroidJavaClass("android.os.Environment"))
            {
                bool manager = env.CallStatic<bool>("isExternalStorageManager");
                Debug.Log($"[BundlePicker] isExternalStorageManager={manager}");
                if (!manager)
                {
                    Debug.LogWarning(
                        "[BundlePicker] 全ファイルアクセスが無いので /sdcard 直下の .svb は見えません。" +
                        "adb shell appops set --uid <package> MANAGE_EXTERNAL_STORAGE allow で付与するか、" +
                        "persistentDataPath に置いてください。");
                }
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"[BundlePicker] isExternalStorageManager 判定に失敗: {ex.Message}");
        }
#endif
    }


    private static void EnsureBundlePickerPreferredDirectoryExists()
    {
        for (int i = 0; i < BundlePickerSharedStorageRoots.Length; i++)
        {
            string root = BundlePickerSharedStorageRoots[i];
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                // 共有ストレージが無い環境（エディタ・PC）では何もしない。
                continue;
            }

            string dir = Path.Combine(root, BundlePickerFolderName).Replace("\\", "/");
            if (Directory.Exists(dir))
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(dir);
                Debug.Log($"[BundlePicker] created: {dir}");
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[BundlePicker] could not create {dir}: {ex.GetType().Name} {ex.Message}");
            }

            return;
        }
    }


    private string ResolveBundlePickerStartDirectory()
    {
        EnsureBundlePickerPreferredDirectoryExists();
        LogExternalStorageAccessState();

        // .svb が実際に見えるディレクトリを優先する。存在しても scoped storage で
        // 中身が見えないことがあるので、**ファイルが 1 つ以上見えるか**まで確かめる。
        string[] candidates = BuildBundleSearchDirectories();
        for (int i = 0; i < candidates.Length; i++)
        {
            if (CountVisibleBundles(candidates[i]) > 0)
            {
                return candidates[i];
            }
        }

        for (int i = 0; i < candidates.Length; i++)
        {
            string candidate = candidates[i];
            if (!string.IsNullOrEmpty(candidate) && Directory.Exists(candidate))
            {
                Debug.LogWarning($"[BundlePicker] .svb が見えないディレクトリから開始します: {candidate}");
                return candidate;
            }
        }

        // エディタ・PC 実行のフォールバック。
        if (Directory.Exists(Application.persistentDataPath))
        {
            return Application.persistentDataPath;
        }

        return Application.dataPath;
    }

    private Text CreateBundlePickerText(Transform parent, string name, string initialText, Vector2 anchoredPos, Vector2 size, int fontSize, TextAnchor alignment, Color color)
    {
        RectTransform rect = RuntimeUiElementFactory.CreateRectChild(name, parent, out GameObject obj);
        SetLayerRecursively(obj, 5); // UI layer
        TransformWriter.ApplyCenteredRect(rect, anchoredPos, size);

        Text text = RuntimeUiElementFactory.AddText(obj);
        UiComponentWriter.ApplyTextStyle(text, GetRuntimeUiFont(), fontSize, alignment, color);
        UiComponentWriter.ApplyTextContent(text, initialText);
        UiComponentWriter.ApplyTextInteraction(text, false);
        return text;
    }

    private Button CreateBundlePickerButton(Transform parent, string name, string label, Vector2 anchoredPos, Vector2 size, UnityEngine.Events.UnityAction onClick, TextAnchor alignment = TextAnchor.MiddleCenter)
    {
        RectTransform rect = RuntimeUiElementFactory.CreateRectChild(name, parent, out GameObject obj);
        SetLayerRecursively(obj, 5); // UI layer
        TransformWriter.ApplyCenteredRect(rect, anchoredPos, size);

        Image image = RuntimeUiElementFactory.AddImage(obj);
        UiComponentWriter.ApplyGraphicColor(image, new Color(0.22f, 0.26f, 0.34f, 0.95f));

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
            RuntimeUnityEventBinding.Bind(button, onClick);
        }

        RectTransform textRect = RuntimeUiElementFactory.CreateRectChild("Label", obj.transform, out GameObject textObj);
        TransformWriter.ApplyStretchRect(
            textRect,
            Vector2.zero,
            Vector2.one,
            new Vector2(14f, 0f),
            new Vector2(-14f, 0f));

        Text text = RuntimeUiElementFactory.AddText(textObj);
        UiComponentWriter.ApplyTextStyle(text, GetRuntimeUiFont(), 30, alignment, Color.white);
        UiComponentWriter.ApplyTextContent(text, label);
        UiComponentWriter.ApplyTextInteraction(text, false);
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
