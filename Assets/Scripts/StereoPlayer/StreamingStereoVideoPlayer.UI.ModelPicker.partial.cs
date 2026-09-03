using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    private static readonly Vector2 RuntimeModelPickerSizeMeters = new Vector2(0.84f, 0.58f);

    // 一覧は 2 行 3 列。1 行 6 件の縦並びだと 1 件あたり 64 単位しか取れず、
    // プレビューが小さすぎて何のモデルか分からなかった（2026-08-31 実機）。
    // 件数は同じ 6 のまま、1 件あたりの面積を約 5 倍にしている。
    private const int ModelPickerColumns = 3;
    private static readonly Vector2 ModelPickerCellSize = new Vector2(300f, 175f);
    private const float ModelPickerCellPitchX = 316f;
    private const float ModelPickerCellPitchY = 200f;
    // 上は track 行（y = 208）、下は Prev/Page/Next（y = -282、高さ 54）。両方に触らない位置。
    private const float ModelPickerGridCenterY = -40f;
    // セル内でプレビューを置く高さ（名前はセル下端に出る）。
    private const float ModelPickerPreviewOffsetY = 22f;
    private const float ModelPickerPreviewTargetSize = 108f;
    // レイが乗っているセルのモデルを何倍にするか。
    // セルの間隔は 316、素のプレビューは 108 なので 2.6 倍（281）でも隣のモデルには当たらない。
    private const float ModelPickerPreviewHoverBoost = 2.6f;

    // ヘッダの track ボタン列。**固定枠にしない。**
    // 同時に出る track は bundle_train の実測で最大 5 だが、上限を決め打ちすると
    // それを超えた ID に到達できなくなる。出ている数だけその場で作る。
    private const float ModelPickerTargetRowY = 208f;
    private const float ModelPickerTargetButtonWidth = 96f;
    private const float ModelPickerTargetRowMaxWidth = 900f;


    // 一覧 i 番目のセル中心（Panel ローカル座標）。ボタンとプレビューで必ず同じ式を使う。
    private static Vector2 ResolveModelPickerCellCenter(int index)
    {
        int col = index % ModelPickerColumns;
        int row = index / ModelPickerColumns;
        return new Vector2(
            (col - (ModelPickerColumns - 1) * 0.5f) * ModelPickerCellPitchX,
            ModelPickerGridCenterY + (0.5f - row) * ModelPickerCellPitchY);
    }

    private GameObject BuildRuntimeModelPickerUi()
    {
        EnsureEventSystem();

        // Settings パネルと同じく runtimeControlsPrefab（CanvasWithInteractionRay）から作る。
        // 素の Canvas + GraphicRaycaster だけだと ISDK_RayCanvasInteraction
        // （RayInteractable / PointableCanvas）が無く、コントローラのレイが当たらない。
        GameObject root;
        if (runtimeControlsPrefab != null)
        {
            root = RuntimeUiRootFactory.Create("RuntimeModelPickerPanel", runtimeControlsPrefab);
        }
        else
        {
            root = RuntimeUiRootFactory.Create("RuntimeModelPickerPanel", null);
            Canvas canvas = RuntimeCanvasComponentFactory.EnsureCanvas(root);
            UiComponentWriter.ApplyWorldSpaceCamera(canvas, GetViewCamera());
            RuntimeCanvasComponentFactory.EnsureGraphicRaycaster(root, false);
        }
        EnsureCanvasRaycasters(root);

        Canvas rootCanvas = root.GetComponent<Canvas>();
        if (rootCanvas == null)
        {
            rootCanvas = root.GetComponentInChildren<Canvas>(true);
        }

        Transform uiRoot = rootCanvas != null ? rootCanvas.transform : root.transform;

        RectTransform rect = uiRoot as RectTransform;
        if (rect != null)
        {
            TransformWriter.ApplySizeDelta(
                rect,
                new Vector2(RuntimeModelPickerDefaultCanvasWidth, RuntimeModelPickerDefaultCanvasHeight));
            TransformWriter.ApplyLocalScale(
                rect,
                new Vector3(
                    RuntimeModelPickerSizeMeters.x / RuntimeModelPickerDefaultCanvasWidth,
                    RuntimeModelPickerSizeMeters.y / RuntimeModelPickerDefaultCanvasHeight,
                    1f));
        }

        RectTransform panelRect = RuntimeUiElementFactory.CreateRectChild("Panel", uiRoot, out GameObject panelObj);
        TransformWriter.ApplyStretchRect(
            panelRect,
            Vector2.zero,
            Vector2.one,
            Vector2.zero,
            Vector2.zero);
        Image panelImage = RuntimeUiElementFactory.AddImage(panelObj);
        UiComponentWriter.ApplyGraphicColor(panelImage, new Color(0.06f, 0.07f, 0.08f, 0.88f));

        runtimeModelPickerTitleText = CreateModelPickerText(
            panelObj.transform,
            "Title",
            "Change Model",
            new Vector2(0f, 282f),
            new Vector2(860f, 64f),
            46,
            TextAnchor.MiddleCenter,
            Color.white);
        runtimeModelPickerStatusText = CreateModelPickerText(
            panelObj.transform,
            "Status",
            "Point at an object, or pick a track below.",
            new Vector2(0f, 250f),
            new Vector2(760f, 40f),
            24,
            TextAnchor.MiddleCenter,
            new Color(0.9f, 0.95f, 1f, 1f));

        // 対象の選択。以前は「Next target >」で 1 つずつ送るだけだったので、
        // 目的の track に届くまで何度も押す必要があり、いま何番を触っているかも分からなかった。
        // **出ている track の ID を全部並べて直接押せる**ようにする（2026-08-31 の要望）。
        //
        // VR でドロップダウンにしないのは、開いたリストが別のワールド座標に浮いて
        // レイで追いにくくなるため。常時表示なら 1 クリックで届く。
        // 実体は UpdateRuntimeModelPickerTargetButtons が必要な数だけ作る。
        runtimeModelPickerTargetButtons.Clear();

        // 「この track にはモデルを置かない」。track 行のすぐ下、一覧の左上の手前に置く。
        // モデルの中に混ぜるとページを跨いだときに見失うので、常に同じ場所に出す。
        runtimeModelPickerHideButton = CreateModelPickerButton(
            panelObj.transform,
            "ModelPickerHideButton",
            "表示しない",
            new Vector2(-360f, ModelPickerTargetRowY),
            new Vector2(200f, 44f),
            OnRuntimeModelPickerHideClicked,
            TextAnchor.MiddleCenter);

        runtimeModelPickerEntryButtons.Clear();
        for (int i = 0; i < RuntimeModelPickerEntriesPerPage; i++)
        {
            int localIndex = i;
            Button button = CreateModelPickerButton(
                panelObj.transform,
                $"ModelEntryButton_{i}",
                string.Empty,
                ResolveModelPickerCellCenter(i),
                ModelPickerCellSize,
                () => OnRuntimeModelPickerEntryClicked(localIndex),
                // 名前はセルの下端。上半分はプレビューの場所として空けておく。
                TextAnchor.LowerCenter);

            // レイが乗ったらそのセルのモデルを拡大する。
            RuntimeHoverNotifier hover = button.gameObject.AddComponent<RuntimeHoverNotifier>();
            hover.index = localIndex;
            hover.onHoverChanged = OnRuntimeModelPickerEntryHoverChanged;

            runtimeModelPickerEntryButtons.Add(button);
        }

        runtimeModelPickerPrevButton = CreateModelPickerButton(
            panelObj.transform,
            "ModelPickerPrevButton",
            "< Prev",
            new Vector2(-220f, -252f),
            new Vector2(180f, 54f),
            PrevRuntimeModelPickerPage,
            TextAnchor.MiddleCenter);
        runtimeModelPickerPageText = CreateModelPickerText(
            panelObj.transform,
            "PageText",
            "Page 1/1",
            new Vector2(0f, -252f),
            new Vector2(250f, 54f),
            26,
            TextAnchor.MiddleCenter,
            Color.white);
        runtimeModelPickerNextButton = CreateModelPickerButton(
            panelObj.transform,
            "ModelPickerNextButton",
            "Next >",
            new Vector2(220f, -252f),
            new Vector2(180f, 54f),
            NextRuntimeModelPickerPage,
            TextAnchor.MiddleCenter);
        // 閉じるは右上の X。以前は "Close" が対象送りボタンのすぐ隣にあり、
        // 押し間違えやすかった（2026-08-31 の指摘）。離して、形でも区別できるようにする。
        CreateModelPickerButton(
            panelObj.transform,
            "ModelPickerCloseButton",
            "X",
            new Vector2(438f, 288f),
            new Vector2(60f, 60f),
            CloseRuntimeModelPickerPanel,
            TextAnchor.MiddleCenter);

        // Settings と同じ掴み代。**下端**に置く。
        CreateRuntimePanelDragHandle(
            panelObj.transform, "PanelDragHandle", new Vector2(0f, -316f), new Vector2(760f, 26f));

        SceneObjectWriter.ApplyActive(root, false);
        return root;
    }


    // いま画面に出ている track を順に回して、モデル変更の対象を切り替える。
    private void OnRuntimeModelPickerEntryHoverChanged(int index, bool hovered)
    {
        if (hovered)
        {
            runtimeModelPickerHoverIndex = index;
        }
        else if (runtimeModelPickerHoverIndex == index)
        {
            // 別のセルへ移った場合、Exit より先に Enter が来ることがある。
            // 自分が現在の hover のときだけ解除する。
            runtimeModelPickerHoverIndex = -1;
        }

        ApplyRuntimeModelPickerPreviewHover();
    }


    // 現在 hover しているセルのモデルだけ拡大し、他は素のスケールへ戻す。
    private void ApplyRuntimeModelPickerPreviewHover()
    {
        for (int i = 0; i < runtimeModelPickerPreviewInstances.Count; i++)
        {
            GameObject holder = runtimeModelPickerPreviewInstances[i];
            if (holder == null || i >= runtimeModelPickerPreviewBaseScales.Count)
            {
                continue;
            }

            float factor = i == runtimeModelPickerHoverIndex ? ModelPickerPreviewHoverBoost : 1f;
            TransformWriter.ApplyLocalScale(holder.transform, runtimeModelPickerPreviewBaseScales[i] * factor);
        }
    }


    // 「表示しない」。もう一度押すと既定のモデルに戻す（トグル）。
    private void OnRuntimeModelPickerHideClicked()
    {
        if (!TryGetRuntimeModelPickerTarget(out uint trackId, out byte categoryId, out _))
        {
            return;
        }

        PauseForManualRotationEdit();

        bool nowHidden = !IsHiddenModelIndex(ResolveSelectedModelIndex(trackId, 0));
        if (nowHidden)
        {
            selectedModelIndexByTrack[trackId] = HiddenModelIndex;
        }
        else
        {
            // 既定へ戻す。カテゴリごとの既定 index を使う。
            selectedModelIndexByTrack[trackId] = IsCategoryAnimal(categoryId) ? selectedAnimalIndex
                : IsCategoryOther(categoryId) ? selectedElseIndex : selectedHumanIndex;
        }

        RecreateTrackInstanceForModelSelection(trackId);

        GameObject[] prefabs = ResolveRuntimeModelPickerPrefabs(categoryId);
        string persisted = HiddenModelName;
        if (!nowHidden && prefabs != null)
        {
            int idx = Mathf.Clamp(selectedModelIndexByTrack[trackId], 0, prefabs.Length - 1);
            persisted = prefabs[idx] != null ? prefabs[idx].name : null;
        }
        PersistModelSelection(trackId, persisted);

        ExperimentLog.Operation(
            "change_model",
            $"track={trackId} category={ResolveRuntimeModelPickerCategoryLabel(categoryId).ToLowerInvariant()} " +
            $"index={selectedModelIndexByTrack[trackId]} prefab={(nowHidden ? HiddenModelName : persisted)}");

        UpdateRuntimeModelPickerUiState();
    }


    // ヘッダの track ボタン。slot 番目に割り当てられている track へ直接切り替える。
    private void OnRuntimeModelPickerTargetClicked(int slot)
    {
        List<uint> ids = GetAvailableTrackIdsForManualRotation();
        if (ids == null || slot < 0 || slot >= ids.Count)
        {
            return;
        }

        PauseForManualRotationEdit();
        runtimeModelPickerTrackId = (int)ids[slot];
        runtimeModelPickerPageIndex = 0;
        // 回転の対象も合わせておく。別々だと「どれを触っているか」が分からなくなる。
        selectedManualRotationTrackId = runtimeModelPickerTrackId;
        Debug.Log($"[ModelPicker] target -> track={runtimeModelPickerTrackId} (slot {slot})");
        UpdateRuntimeModelPickerUiState();
    }


    // 出ている track の ID をボタン列へ流し込む。足りなければ作り、余ったら隠す。
    private void UpdateRuntimeModelPickerTargetButtons()
    {
        List<uint> ids = GetAvailableTrackIdsForManualRotation();
        int count = ids != null ? ids.Count : 0;

        Transform panel = runtimeModelPickerRoot != null ? runtimeModelPickerRoot.transform.Find("Panel") : null;
        if (logPlacementMeasurement)
        {
            Debug.Log($"[TARGETROW] ids={count} buttons={runtimeModelPickerTargetButtons.Count} panel={(panel != null ? panel.name : "null")}");
        }
        while (panel != null && runtimeModelPickerTargetButtons.Count < count)
        {
            int slot = runtimeModelPickerTargetButtons.Count;
            Button created = CreateModelPickerButton(
                panel,
                $"ModelPickerTargetButton_{slot}",
                string.Empty,
                Vector2.zero,
                new Vector2(ModelPickerTargetButtonWidth, 44f),
                () => OnRuntimeModelPickerTargetClicked(slot),
                TextAnchor.MiddleCenter);
            runtimeModelPickerTargetButtons.Add(created);
        }

        // 数が変わっても中央に並ぶよう、間隔は毎回引き直す。
        // 数が多いときは詰めて、行が canvas からはみ出さないようにする。
        float pitch = count > 1
            ? Mathf.Min(ModelPickerTargetButtonWidth + 8f, ModelPickerTargetRowMaxWidth / count)
            : 0f;

        for (int i = 0; i < runtimeModelPickerTargetButtons.Count; i++)
        {
            Button button = runtimeModelPickerTargetButtons[i];
            if (button == null)
            {
                continue;
            }

            bool used = i < count;
            SceneObjectWriter.ApplyActive(button.gameObject, used);
            if (!used)
            {
                continue;
            }

            RectTransform rect = button.GetComponent<RectTransform>();
            if (rect != null)
            {
                TransformWriter.ApplyCenteredRect(
                    rect,
                    new Vector2((i - (count - 1) * 0.5f) * pitch, ModelPickerTargetRowY),
                    new Vector2(Mathf.Min(ModelPickerTargetButtonWidth, Mathf.Max(40f, pitch - 8f)), 44f));
            }

            bool isCurrent = (int)ids[i] == runtimeModelPickerTrackId;
            Text label = button.GetComponentInChildren<Text>(true);
            if (label != null)
            {
                UiComponentWriter.ApplyTextContent(label, ids[i].ToString());
            }

            if (button.targetGraphic is Image image)
            {
                UiComponentWriter.ApplyGraphicColor(
                    image,
                    isCurrent
                        ? new Color(0.15f, 0.35f, 0.48f, 0.95f)
                        : new Color(0.13f, 0.14f, 0.15f, 0.92f));
            }

            UiComponentWriter.ApplyInteractable(button, !isCurrent);
        }
    }


    private void StepRuntimeModelPickerTarget()
    {
        PauseForManualRotationEdit();

        List<uint> ids = GetAvailableTrackIdsForManualRotation();
        if (ids == null || ids.Count == 0)
        {
            Debug.LogWarning("[ModelPicker] 切り替えられる track がありません。");
            return;
        }

        int current = ids.IndexOf((uint)Mathf.Max(0, runtimeModelPickerTrackId));
        int next = ids.Count > 0 ? (current + 1) % ids.Count : 0;
        runtimeModelPickerTrackId = (int)ids[next];
        runtimeModelPickerPageIndex = 0;

        // 回転の対象も合わせておく。別々だと「どれを触っているか」が分からなくなる。
        selectedManualRotationTrackId = runtimeModelPickerTrackId;

        Debug.Log($"[ModelPicker] target -> track={runtimeModelPickerTrackId} ({next + 1}/{ids.Count})");
        UpdateRuntimeModelPickerUiState();
    }


    private void ToggleRuntimeModelPickerPanel()
    {
        if (runtimeModelPickerOpen)
        {
            CloseRuntimeModelPickerPanel();
            return;
        }

        runtimeModelPickerOpen = true;
        runtimeSettingsOpen = false;
        runtimeModelPickerPageIndex = 0;

        // 開いている間は再生を止める。動いている対象を見ながら選ぶのは難しく、
        // 選んだ瞬間にインスタンスを作り直すので画が飛ぶ（2026-08-28 の要望）。
        // 回転編集と同じ扱い（PauseForManualRotationEdit）。
        PauseForManualRotationEdit();

        if (TryGetRuntimeModelPickerTarget(out uint trackId, out byte targetCategoryId, out _))
        {
            runtimeModelPickerTrackId = (int)trackId;
            Debug.Log($"[ModelPicker] target track={trackId} category={ResolveRuntimeModelPickerCategoryLabel(targetCategoryId)}");
        }
        else
        {
            // どれも対象にできなかった。何が見えているかを出さないと原因が分からない。
            Debug.LogWarning(
                $"[ModelPicker] 対象が決まりません: metaLoaded={metaLoaded} normalMode={isNormalMode} " +
                $"frameObjects={(metaFrameObjects != null ? metaFrameObjects.Count : -1)} " +
                $"pickerTrack={runtimeModelPickerTrackId} " +
                $"displayTracks={(displayTrackIds != null ? displayTrackIds.Length : 0)}");
        }

        if (runtimeSettingsRoot != null)
        {
            SceneObjectWriter.ApplyActive(runtimeSettingsRoot, false);
        }
        if (runtimeModelPickerRoot != null)
        {
            SceneObjectWriter.ApplyActive(runtimeModelPickerRoot, enableRuntimeControls);
            UpdateRuntimeModelPickerPlacement();
            UpdateRuntimeModelPickerUiState();
        }

        SetScreenColliderBlockForRuntimePanels();
        UpdateSettingsButtonLabel();
        UpdateModelPickerButtonLabel();
    }


    private void CloseRuntimeModelPickerPanel()
    {
        runtimeModelPickerOpen = false;
        ClearRuntimeModelPickerPreviews();
        if (runtimeModelPickerRoot != null)
        {
            SceneObjectWriter.ApplyActive(runtimeModelPickerRoot, false);
        }
        SetScreenColliderBlockForRuntimePanels();
        UpdateModelPickerButtonLabel();
    }


    private void UpdateRuntimeModelPickerPlacement()
    {
        if (runtimeModelPickerRoot == null || !enableRuntimeControls || !runtimeModelPickerOpen)
        {
            return;
        }

        Transform baseScreen = rightScreen != null ? rightScreen : leftScreen;
        Transform basis = baseScreen != null
            ? baseScreen
            : (runtimeControlsRoot != null ? runtimeControlsRoot.transform : null);
        if (basis == null)
        {
            return;
        }

        Canvas canvas = runtimeModelPickerRoot.GetComponent<Canvas>();
        UiComponentWriter.ApplyWorldCamera(canvas, GetViewCamera());

        Transform head = GetViewOrHeadTransform();
        float basisWidth = ControlsBarSizeMeters.x;
        if (baseScreen != null)
        {
            GetScreenSizeMeters(baseScreen, out float screenWidthMeters, out _, out _);
            basisWidth = Mathf.Abs(screenWidthMeters);
        }

        RuntimeControlsPlacement.Pose pose = RuntimeControlsPlacement.ResolveSettingsPose(
            basis.position,
            basis.forward,
            basis.right,
            basis.up,
            basis.rotation,
            head != null,
            head != null ? head.position : Vector3.zero,
            basisWidth,
            RuntimeModelPickerSizeMeters,
            SettingsPanelGapMeters,
            SettingsPanelOffsetMeters,
            SettingsPanelForwardOffsetMeters);
        TransformWriter.ApplyPose(
            runtimeModelPickerRoot.transform, ApplyRuntimePanelDistanceOffset(pose.position), pose.rotation);

        RectTransform rect = runtimeModelPickerRoot.GetComponent<RectTransform>();
        if (rect != null)
        {
            TransformWriter.ApplyLocalScale(
                rect,
                new Vector3(
                    RuntimeModelPickerSizeMeters.x / RuntimeModelPickerDefaultCanvasWidth,
                    RuntimeModelPickerSizeMeters.y / RuntimeModelPickerDefaultCanvasHeight,
                    1f));
        }
    }


    private void UpdateRuntimeModelPickerUiState()
    {
        UpdateModelPickerButtonLabel();
        if (runtimeModelPickerRoot == null || !runtimeModelPickerOpen)
        {
            return;
        }

        UpdateRuntimeModelPickerTargetButtons();

        if (!TryGetRuntimeModelPickerTarget(out uint trackId, out byte categoryId, out _))
        {
            ApplyRuntimeModelPickerUnavailable("Point at a displayed object first.");
            return;
        }

        runtimeModelPickerTrackId = (int)trackId;
        GameObject[] prefabs = ResolveRuntimeModelPickerPrefabs(categoryId);
        string category = ResolveRuntimeModelPickerCategoryLabel(categoryId);
        if (prefabs == null || prefabs.Length == 0)
        {
            ApplyRuntimeModelPickerUnavailable($"No {category.ToLowerInvariant()} models found.");
            return;
        }

        int defaultIndex = IsCategoryAnimal(categoryId) ? selectedAnimalIndex
            : IsCategoryOther(categoryId) ? selectedElseIndex : selectedHumanIndex;
        int rawSelected = ResolveSelectedModelIndex(trackId, defaultIndex);
        bool hidden = IsHiddenModelIndex(rawSelected);
        // 非表示のときは「どれも選ばれていない」ことを示したいので、どのセルとも一致しない値にする。
        int selectedIndex = hidden ? -1 : Mathf.Clamp(rawSelected, 0, prefabs.Length - 1);

        if (runtimeModelPickerHideButton != null)
        {
            Text hideLabel = runtimeModelPickerHideButton.GetComponentInChildren<Text>(true);
            if (hideLabel != null)
            {
                UiComponentWriter.ApplyTextContent(hideLabel, hidden ? "表示する" : "表示しない");
            }

            if (runtimeModelPickerHideButton.targetGraphic is Image hideImage)
            {
                UiComponentWriter.ApplyGraphicColor(
                    hideImage,
                    hidden
                        ? new Color(0.48f, 0.24f, 0.16f, 0.95f)
                        : new Color(0.13f, 0.14f, 0.15f, 0.92f));
            }
        }
        int pageCount = GetRuntimeModelPickerPageCount(prefabs.Length);
        runtimeModelPickerPageIndex = Mathf.Clamp(runtimeModelPickerPageIndex, 0, Mathf.Max(0, pageCount - 1));

        if (runtimeModelPickerTitleText != null)
        {
            UiComponentWriter.ApplyTextContent(runtimeModelPickerTitleText, $"{category} models");
        }
        if (runtimeModelPickerStatusText != null)
        {
            string selectedName = hidden
                ? "表示しない"
                : (prefabs[selectedIndex] != null ? CleanModelDisplayName(prefabs[selectedIndex].name) : "missing");
            List<uint> targets = GetAvailableTrackIdsForManualRotation();
            int pos = targets != null ? targets.IndexOf(trackId) + 1 : 0;
            string targetInfo = targets != null && targets.Count > 1
                ? $"Track {trackId} ({pos}/{targets.Count})"
                : $"Track {trackId}";
            UiComponentWriter.ApplyTextContent(
                runtimeModelPickerStatusText, $"{targetInfo}  |  {category}  |  Selected: {selectedName}");
        }

        UpdateRuntimeModelPickerEntryButtons(prefabs, selectedIndex, categoryId);
    }


    private void ApplyRuntimeModelPickerUnavailable(string message)
    {
        ClearRuntimeModelPickerPreviews();
        if (runtimeModelPickerTitleText != null)
        {
            UiComponentWriter.ApplyTextContent(runtimeModelPickerTitleText, "Change Model");
        }
        if (runtimeModelPickerStatusText != null)
        {
            UiComponentWriter.ApplyTextContent(runtimeModelPickerStatusText, message);
        }
        for (int i = 0; i < runtimeModelPickerEntryButtons.Count; i++)
        {
            Button button = runtimeModelPickerEntryButtons[i];
            if (button == null)
            {
                continue;
            }

            SceneObjectWriter.ApplyActive(button.gameObject, false);
        }
        if (runtimeModelPickerPageText != null)
        {
            UiComponentWriter.ApplyTextContent(runtimeModelPickerPageText, "Page 1/1");
        }
        UiComponentWriter.ApplyInteractable(runtimeModelPickerPrevButton, false);
        UiComponentWriter.ApplyInteractable(runtimeModelPickerNextButton, false);
    }


    // プレビューを作り直したときの状態。**毎フレーム作り直さないための番人。**
    //
    // UpdateRuntimeModelPickerUiState は UI.cs から毎フレーム呼ばれる。素直に書くと
    // 6 体のモデルを毎フレーム Instantiate / Destroy することになり（人体は 1 体 30 renderer）、
    // Quest では確実に重い。ページ・対象・選択が変わったときだけ作り直す。
    private int previewBuiltPage = -1;
    private int previewBuiltSelectedIndex = -1;
    private int previewBuiltTrackId = -1;
    private byte previewBuiltCategoryId = 255;
    private int previewBuiltPrefabCount = -1;

    private void InvalidateRuntimeModelPickerPreviews()
    {
        previewBuiltPage = -1;
    }


    private void UpdateRuntimeModelPickerEntryButtons(GameObject[] prefabs, int selectedIndex, byte categoryId)
    {
        int currentTrackId = runtimeModelPickerTrackId;
        bool previewsAreCurrent =
            previewBuiltPage == runtimeModelPickerPageIndex &&
            previewBuiltSelectedIndex == selectedIndex &&
            previewBuiltTrackId == currentTrackId &&
            previewBuiltCategoryId == categoryId &&
            previewBuiltPrefabCount == prefabs.Length &&
            runtimeModelPickerPreviewInstances.Count > 0;

        if (!previewsAreCurrent)
        {
            ClearRuntimeModelPickerPreviews();
            previewBuiltPage = runtimeModelPickerPageIndex;
            previewBuiltSelectedIndex = selectedIndex;
            previewBuiltTrackId = currentTrackId;
            previewBuiltCategoryId = categoryId;
            previewBuiltPrefabCount = prefabs.Length;
        }

        int startIndex = runtimeModelPickerPageIndex * RuntimeModelPickerEntriesPerPage;
        Transform previewParent = runtimeModelPickerRoot != null ? runtimeModelPickerRoot.transform.Find("Panel") : null;
        for (int i = 0; i < runtimeModelPickerEntryButtons.Count; i++)
        {
            Button button = runtimeModelPickerEntryButtons[i];
            if (button == null)
            {
                continue;
            }

            int modelIndex = startIndex + i;
            Text label = button.GetComponentInChildren<Text>(true);
            Image image = button.targetGraphic as Image;
            if (modelIndex < prefabs.Length)
            {
                GameObject prefab = prefabs[modelIndex];
                string modelName = prefab != null ? CleanModelDisplayName(prefab.name) : "missing";
                if (label != null)
                {
                    // 選択中は枠の色で示すので、行頭の "> " は付けない（中央寄せだと中心がずれる）。
                    UiComponentWriter.ApplyTextContent(label, $"{modelIndex + 1}. {modelName}");
                }
                if (image != null)
                {
                    UiComponentWriter.ApplyGraphicColor(
                        image,
                        modelIndex == selectedIndex
                            ? new Color(0.15f, 0.35f, 0.48f, 0.95f)
                            : new Color(0.13f, 0.14f, 0.15f, 0.92f));
                }

                SceneObjectWriter.ApplyActive(button.gameObject, true);
                UiComponentWriter.ApplyInteractable(button, true);
                if (prefab != null && previewParent != null && !previewsAreCurrent)
                {
                    Vector2 cell = ResolveModelPickerCellCenter(i);
                    CreateRuntimeModelPickerPreview(
                        prefab, previewParent, new Vector3(cell.x, cell.y + ModelPickerPreviewOffsetY, 0f));
                }
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

        ApplyRuntimeModelPickerPreviewHover();

        int pageCount = GetRuntimeModelPickerPageCount(prefabs.Length);
        if (runtimeModelPickerPageText != null)
        {
            UiComponentWriter.ApplyTextContent(runtimeModelPickerPageText, $"Page {runtimeModelPickerPageIndex + 1}/{Mathf.Max(1, pageCount)}");
        }
        UiComponentWriter.ApplyInteractable(runtimeModelPickerPrevButton, runtimeModelPickerPageIndex > 0);
        UiComponentWriter.ApplyInteractable(runtimeModelPickerNextButton, runtimeModelPickerPageIndex < pageCount - 1);
    }


    private void OnRuntimeModelPickerEntryClicked(int localIndex)
    {
        if (!TryGetRuntimeModelPickerTarget(out uint trackId, out byte categoryId, out _))
        {
            return;
        }

        GameObject[] prefabs = ResolveRuntimeModelPickerPrefabs(categoryId);
        if (prefabs == null)
        {
            return;
        }

        int modelIndex = runtimeModelPickerPageIndex * RuntimeModelPickerEntriesPerPage + localIndex;
        if (modelIndex < 0 || modelIndex >= prefabs.Length)
        {
            return;
        }

        selectedModelIndexByTrack[trackId] = modelIndex;
        RecreateTrackInstanceForModelSelection(trackId);
        PersistModelSelection(trackId, prefabs[modelIndex] != null ? prefabs[modelIndex].name : null);

        // どの試行でどのモデルを見ていたかは分析時の交絡要因になり得るので、
        // prefab 名まで残す（Docs/experiment-flow.md「操作の統制」参照）。
        GameObject selectedPrefab = prefabs[modelIndex];
        ExperimentLog.Operation(
            "change_model",
            $"track={trackId} category={ResolveRuntimeModelPickerCategoryLabel(categoryId).ToLowerInvariant()} " +
            $"index={modelIndex} prefab={(selectedPrefab != null ? selectedPrefab.name : "null")}");

        UpdateRuntimeModelPickerUiState();
    }


    private bool TryGetRuntimeModelPickerTarget(out uint trackId, out byte categoryId, out bool isAnimal)
    {
        trackId = 0;
        categoryId = 0;
        isAnimal = false;

        if (!metaLoaded || isNormalMode)
        {
            return false;
        }

        int frame = GetCurrentPlaybackFrame();
        if (!TryReadFrameObjects(frame, metaFrameObjects) || metaFrameObjects.Count == 0)
        {
            return false;
        }

        if (TryResolveRuntimeModelPickerTargetFromTrack(runtimeModelPickerTrackId, out trackId, out categoryId, out isAnimal))
        {
            return true;
        }

        if (displayTrackIds != null && displayTrackIds.Length > 0 &&
            TryResolveRuntimeModelPickerTargetFromTrack(displayTrackIds[0], out trackId, out categoryId, out isAnimal))
        {
            return true;
        }

        if (TryResolveRuntimeModelPickerTargetFromTrack(selectedManualRotationTrackId, out trackId, out categoryId, out isAnimal))
        {
            return true;
        }

        if (TryResolveRuntimeModelPickerTargetFromTrack(lastAutoTrackId, out trackId, out categoryId, out isAnimal))
        {
            return true;
        }

        for (int i = 0; i < metaFrameObjects.Count; i++)
        {
            // Else も対象にする（2026-08-28）。以前はここで person / animal 以外を
            // 弾いていたため、train の 8 個の Else がモデル変更できなかった。
            MetaObj obj = metaFrameObjects[i];
            trackId = obj.trackId;
            categoryId = obj.categoryId;
            isAnimal = IsCategoryAnimal(categoryId);
            return true;
        }

        return false;
    }


    private bool TryResolveRuntimeModelPickerTargetFromTrack(int candidateTrackId, out uint trackId, out byte categoryId, out bool isAnimal)
    {
        trackId = 0;
        categoryId = 0;
        isAnimal = false;
        if (candidateTrackId < 0)
        {
            return false;
        }

        uint candidate = (uint)candidateTrackId;
        for (int i = 0; i < metaFrameObjects.Count; i++)
        {
            MetaObj obj = metaFrameObjects[i];
            if (obj.trackId != candidate)
            {
                continue;
            }
            trackId = obj.trackId;
            categoryId = obj.categoryId;
            isAnimal = IsCategoryAnimal(categoryId);
            return true;
        }

        return false;
    }


    // 2026-08-28: Else を対象に加えた。Else は bundle に向きの推定値が無く
    // （train は全 track が skel=0 smpl=0 smal=0）、モデルも向きも人が仕込むしかない。
    private GameObject[] ResolveRuntimeModelPickerPrefabs(byte categoryId)
    {
        return ResolvePrefabsForCategory(categoryId);
    }


    private string ResolveRuntimeModelPickerCategoryLabel(byte categoryId)
    {
        if (IsCategoryAnimal(categoryId)) return "Animal";
        if (IsCategoryOther(categoryId)) return "Else";
        return "Human";
    }


    // Priority animal prefabs are named with a leading "<index>_" (e.g. "0_Dog") so the
    // Project window / selectedAnimalIndex mapping is obvious at a glance (see
    // AnimalModelPriorityOrder). Strip that prefix for the in-VR picker label only.
    private static string CleanModelDisplayName(string rawName)
    {
        if (string.IsNullOrEmpty(rawName))
        {
            return rawName;
        }

        int underscoreIndex = rawName.IndexOf('_');
        if (underscoreIndex <= 0)
        {
            return rawName;
        }

        for (int i = 0; i < underscoreIndex; i++)
        {
            if (!char.IsDigit(rawName[i]))
            {
                return rawName;
            }
        }

        return rawName.Substring(underscoreIndex + 1);
    }


    private void PrevRuntimeModelPickerPage()
    {
        runtimeModelPickerPageIndex = Mathf.Max(0, runtimeModelPickerPageIndex - 1);
        UpdateRuntimeModelPickerUiState();
    }


    private void NextRuntimeModelPickerPage()
    {
        if (!TryGetRuntimeModelPickerTarget(out _, out byte categoryId, out _))
        {
            return;
        }

        GameObject[] prefabs = ResolveRuntimeModelPickerPrefabs(categoryId);
        int pageCount = prefabs != null ? GetRuntimeModelPickerPageCount(prefabs.Length) : 1;
        runtimeModelPickerPageIndex = Mathf.Min(Mathf.Max(0, pageCount - 1), runtimeModelPickerPageIndex + 1);
        UpdateRuntimeModelPickerUiState();
    }


    private int GetRuntimeModelPickerPageCount(int count)
    {
        if (count <= 0)
        {
            return 1;
        }

        return Mathf.CeilToInt(count / (float)RuntimeModelPickerEntriesPerPage);
    }


    // batchSwapModelSpec を解釈して、指定フレームに達したらモデルを差し替える。
    // ピッカーのクリック（OnRuntimeModelPickerEntryClicked）と同じ 2 手を踏むので、
    // 実機の操作とまったく同じ状態遷移になる。
    private readonly HashSet<int> batchSwapDone = new HashSet<int>();

    private void ApplyBatchSwapModelSpecForFrame(int frame)
    {
        if (string.IsNullOrEmpty(batchSwapModelSpec))
        {
            return;
        }

        string[] parts = batchSwapModelSpec.Split(',');
        for (int i = 0; i < parts.Length; i++)
        {
            if (batchSwapDone.Contains(i))
            {
                continue;
            }

            string[] kv = parts[i].Split(':');
            if (kv.Length != 3 ||
                !uint.TryParse(kv[0].Trim(), out uint trackId) ||
                !int.TryParse(kv[1].Trim(), out int atFrame) ||
                !int.TryParse(kv[2].Trim(), out int modelIndex))
            {
                Debug.LogWarning($"[Customization] batchSwapModelSpec を解釈できません: '{parts[i]}'");
                batchSwapDone.Add(i);
                continue;
            }

            if (frame < atFrame)
            {
                continue;
            }

            batchSwapDone.Add(i);
            selectedModelIndexByTrack[trackId] = modelIndex;
            RecreateTrackInstanceForModelSelection(trackId);
            Debug.Log($"[SWAP] f={frame} track={trackId} modelIndex={modelIndex} に差し替え");
        }
    }


    private void RecreateTrackInstanceForModelSelection(uint trackId)
    {
        if (trackInstances.TryGetValue(trackId, out GameObject existing) && existing != null)
        {
            SceneObjectWriter.DestroyObject(existing);
        }

        trackInstances.Remove(trackId);
        trackPrefabSources.Remove(trackId);
        lockedModelLocalScaleByTrack.Remove(trackId);
        smoothedJointsByTrack.Remove(trackId);
    }


    private Text CreateModelPickerText(
        Transform parent,
        string name,
        string initialText,
        Vector2 anchoredPos,
        Vector2 size,
        int fontSize,
        TextAnchor anchor,
        Color color)
    {
        RectTransform rect = RuntimeUiElementFactory.CreateRectChild(name, parent, out GameObject obj);
        TransformWriter.ApplyCenteredRect(rect, anchoredPos, size);
        Text text = RuntimeUiElementFactory.AddText(obj);
        UiComponentWriter.ApplyTextStyle(text, GetRuntimeUiFont(), fontSize, anchor, color);
        UiComponentWriter.ApplyTextOverflow(text, HorizontalWrapMode.Wrap, VerticalWrapMode.Truncate);
        UiComponentWriter.ApplyTextContent(text, initialText);
        return text;
    }


    private Button CreateModelPickerButton(
        Transform parent,
        string name,
        string label,
        Vector2 anchoredPos,
        Vector2 size,
        UnityEngine.Events.UnityAction action,
        TextAnchor textAnchor)
    {
        RectTransform buttonRect = RuntimeUiElementFactory.CreateRectChild(name, parent, out GameObject buttonObj);
        TransformWriter.ApplyCenteredRect(buttonRect, anchoredPos, size);

        Image buttonImage = RuntimeUiElementFactory.AddImage(buttonObj);
        UiComponentWriter.ApplyGraphicColor(buttonImage, new Color(0.13f, 0.14f, 0.15f, 0.92f));
        Button button = RuntimeUiElementFactory.AddButton(buttonObj);
        UiComponentWriter.ApplyTargetGraphic(button, buttonImage);

        RectTransform textRect = RuntimeUiElementFactory.CreateRectChild("Label", buttonObj.transform, out GameObject textObj);
        // LowerCenter はセル下端に名前を置くグリッド用。下辺にべったり付かないよう余白を取る。
        Vector2 offsetMin =
            textAnchor == TextAnchor.MiddleLeft ? new Vector2(116f, 0f) :
            textAnchor == TextAnchor.LowerCenter ? new Vector2(8f, 12f) : Vector2.zero;
        Vector2 offsetMax =
            textAnchor == TextAnchor.MiddleLeft ? new Vector2(-18f, 0f) :
            textAnchor == TextAnchor.LowerCenter ? new Vector2(-8f, 0f) : Vector2.zero;
        TransformWriter.ApplyStretchRect(
            textRect,
            Vector2.zero,
            Vector2.one,
            offsetMin,
            offsetMax);
        Text text = RuntimeUiElementFactory.AddText(textObj);
        UiComponentWriter.ApplyTextStyle(text, GetRuntimeUiFont(), 28, textAnchor, Color.white);
        UiComponentWriter.ApplyTextOverflow(text, HorizontalWrapMode.Wrap, VerticalWrapMode.Truncate);
        UiComponentWriter.ApplyTextContent(text, label);

        BindRuntimeButton(button, action);
        return button;
    }


    // 一覧の各行の左に、そのモデルの実物を小さく置く。
    //
    // **world space canvas の中に 3D を置くときは z の扱いに注意。**
    // canvas の localScale は (幅m/幅px, 高さm/高さpx, **1**) で、x/y は 1/1000 程度なのに
    // z だけ等倍。ここを uniform スケールで作ると、1.7m の人体が
    // 幅 4cm・奥行き 51m という針のような形になり、しかも localPosition.z = -35 が
    // 「35 メートル手前」を意味してしまう。実機で「名前しか出ない」「変な点が見える」と
    // 報告されたのはこれ（2026-08-31）。
    //
    // 対策は 2 つ:
    //   - z 位置はキャンバス平面から**メートル単位**でわずかに手前へ（パネルとの z 争いを避ける）
    //   - z のスケールに canvas の y スケールを掛けて、見かけを等倍に戻す
    private void CreateRuntimeModelPickerPreview(GameObject prefab, Transform parent, Vector3 localPosition)
    {
        GameObject holder = new GameObject("ModelPreview");
        holder.transform.SetParent(parent, false);
        // パネルの Image と同一平面だと描画順で消えるので、1cm だけ手前に出す。
        // canvas の z は等倍なので、この 0.01 はそのまま 1cm。
        holder.transform.localPosition = localPosition + new Vector3(0f, 0f, -PreviewForwardOffsetMeters);
        holder.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
        runtimeModelPickerPreviewInstances.Add(holder);

        GameObject preview = Object.Instantiate(prefab, holder.transform);
        preview.name = $"Preview_{prefab.name}";
        preview.transform.localPosition = Vector3.zero;
        // 配置側（TrackInstanceFactory / ApplyModelBaseRotation）が prefab の root 補正を
        // 尊重するので、プレビューも同じ向きで見せる。identity に潰すと
        // 06_DieselLocomotive のように「プレビューは縦、配置は横」で食い違う。
        preview.transform.localRotation = prefab.transform.localRotation;
        preview.transform.localScale = Vector3.one;
        DisableRuntimeModelPickerPreviewComponents(preview);

        // プレビューは画面上で小さいので、放っておくと LOD が最下位に落ちて粗く見える。
        LODGroup[] lodGroups = preview.GetComponentsInChildren<LODGroup>(true);
        for (int i = 0; i < lodGroups.Length; i++)
        {
            if (lodGroups[i] != null)
            {
                lodGroups[i].ForceLOD(0);
            }
        }

        if (!TryCalculateLocalRendererBounds(holder.transform, out Bounds bounds))
        {
            ApplyPreviewScale(holder.transform, 52f);
            return;
        }

        // **奥行きの方が横幅より長いモデルは、横向きに回して見せる。**
        // 機関車は長軸（18.5m）がホルダーの Z に向くので、正面から見ると端から見る形になり
        // 「細長い塊」にしか見えない（2026-09-02 の指摘）。90 度回して側面を見せる。
        // 球は差が出ず、人・動物は高さが最長なのでこの条件に入らない。実測で該当するのは
        // 06_DieselLocomotive だけ。
        if (bounds.size.z > bounds.size.x * 1.2f)
        {
            preview.transform.localRotation = Quaternion.Euler(0f, 90f, 0f) * preview.transform.localRotation;
            // 回したので測り直す。回転前の bounds で中心を引くと位置がずれる。
            if (!TryCalculateLocalRendererBounds(holder.transform, out bounds))
            {
                ApplyPreviewScale(holder.transform, ModelPickerPreviewTargetSize);
                return;
            }
        }

        preview.transform.localPosition -= bounds.center;
        float maxSize = Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z));
        float scale = maxSize > 0.0001f ? ModelPickerPreviewTargetSize / maxSize : ModelPickerPreviewTargetSize;
        // 上限 90 は野球ボール（0.076m）のような小さいモデルを潰していた。
        // 必要な倍率は 118 / 0.076 = 1553。bounds の計算を直したので広い範囲を許してよい。
        float applied = Mathf.Clamp(scale, 1f, 3000f);
        ApplyPreviewScale(holder.transform, applied);

        if (logPlacementMeasurement)
        {
            Debug.Log(
                $"[PREVIEW] {prefab.name} boundsSize={bounds.size:F3} maxSize={maxSize:F3} " +
                $"scale={scale:F2} applied={applied:F2} lossy={holder.transform.lossyScale:F5}");
        }
    }


    // canvas の z が等倍であることを打ち消して、見かけを等倍にする。
    // x/y はキャンバス座標（px 相当）なのでそのまま、z だけ「1px 相当のメートル数」を掛ける。
    private void ApplyPreviewScale(Transform holder, float scale)
    {
        float metersPerCanvasUnit = RuntimeModelPickerSizeMeters.y / RuntimeModelPickerDefaultCanvasHeight;
        Vector3 baseScale = new Vector3(scale, scale, scale * metersPerCanvasUnit);
        TransformWriter.ApplyLocalScale(holder, baseScale);

        // hover で拡大したあと戻す先。プレビューと同じ順序で積む。
        runtimeModelPickerPreviewBaseScales.Add(baseScale);
    }


    private void DisableRuntimeModelPickerPreviewComponents(GameObject preview)
    {
        Collider[] colliders = preview.GetComponentsInChildren<Collider>(true);
        for (int i = 0; i < colliders.Length; i++)
        {
            SceneObjectWriter.ApplyColliderEnabled(colliders[i], false);
        }

        Animator[] animators = preview.GetComponentsInChildren<Animator>(true);
        for (int i = 0; i < animators.Length; i++)
        {
            SceneObjectWriter.ApplyAnimatorEnabled(animators[i], false);
        }

        MonoBehaviour[] behaviours = preview.GetComponentsInChildren<MonoBehaviour>(true);
        for (int i = 0; i < behaviours.Length; i++)
        {
            if (behaviours[i] != null)
            {
                behaviours[i].enabled = false;
            }
        }
    }


    // プレビュー用に、holder から見たモデルの大きさを測る。
    //
    // **renderer.bounds（world AABB）を使ってはいけない。** holder は world space canvas の
    // 配下にあり、親の lossyScale が (0.00086, 0.00088, **1**) と極端に非等倍で、しかも
    // パネルごと回転している。world の軸に沿った AABB を holder のローカルへ戻すと、
    // パネル法線方向（スケール 1）の僅かな厚みが x（1/0.00086 倍）へ漏れて桁が跳ねる。
    // 実測で人体 1 体が (623.160, 2.043, 0.650) と出て、maxSize=623 から
    // scale が下限 8 にクランプされ、プレビューが本来の 1/3 の大きさになっていた
    // （2026-08-31。実機で「名前しか出ない」と報告された症状の一部）。
    //
    // mesh の**ローカル** bounds を holder までの行列で運べば、途中のスケールは
    // 行列の中で相殺されるので正しい寸法が出る（ElseOrientationDiagnostics と同じやり方）。
    private static bool TryCalculateLocalRendererBounds(Transform root, out Bounds bounds)
    {
        bounds = default;
        if (root == null)
        {
            return false;
        }

        bool hasBounds = false;
        Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer renderer = renderers[i];
            Mesh mesh = ResolvePreviewMesh(renderer);
            if (mesh == null)
            {
                continue;
            }

            Matrix4x4 toRoot = root.worldToLocalMatrix * renderer.transform.localToWorldMatrix;
            Bounds inRoot = TransformBoundsByMatrix(mesh.bounds, toRoot);
            if (!hasBounds)
            {
                bounds = inRoot;
                hasBounds = true;
            }
            else
            {
                bounds.Encapsulate(inRoot);
            }
        }

        return hasBounds;
    }


    private static Mesh ResolvePreviewMesh(Renderer renderer)
    {
        if (renderer == null)
        {
            return null;
        }

        if (renderer is SkinnedMeshRenderer skinned)
        {
            return skinned.sharedMesh;
        }

        MeshFilter filter = renderer.GetComponent<MeshFilter>();
        return filter != null ? filter.sharedMesh : null;
    }


    private static Bounds TransformBoundsByMatrix(Bounds b, Matrix4x4 m)
    {
        Vector3 center = m.MultiplyPoint3x4(b.center);
        Vector3 e = b.extents;
        Vector3 x = m.MultiplyVector(new Vector3(e.x, 0f, 0f));
        Vector3 y = m.MultiplyVector(new Vector3(0f, e.y, 0f));
        Vector3 z = m.MultiplyVector(new Vector3(0f, 0f, e.z));
        Vector3 extents = new Vector3(
            Mathf.Abs(x.x) + Mathf.Abs(y.x) + Mathf.Abs(z.x),
            Mathf.Abs(x.y) + Mathf.Abs(y.y) + Mathf.Abs(z.y),
            Mathf.Abs(x.z) + Mathf.Abs(y.z) + Mathf.Abs(z.z));
        return new Bounds(center, extents * 2f);
    }


    private void ClearRuntimeModelPickerPreviews()
    {
        for (int i = 0; i < runtimeModelPickerPreviewInstances.Count; i++)
        {
            if (runtimeModelPickerPreviewInstances[i] != null)
            {
                SceneObjectWriter.DestroyObject(runtimeModelPickerPreviewInstances[i]);
            }
        }

        runtimeModelPickerPreviewInstances.Clear();
        runtimeModelPickerPreviewBaseScales.Clear();
        runtimeModelPickerHoverIndex = -1;
    }


    private void UpdateModelPickerButtonLabel()
    {
        if (runtimeModelPickerButtonText == null)
        {
            return;
        }

        UiComponentWriter.ApplyTextContent(runtimeModelPickerButtonText, "Change");
    }


    private void UnbindRuntimeModelPickerButtons()
    {
        RuntimeUnityEventBinding.ClearButtonListenersInChildren(runtimeModelPickerRoot);
        ClearRuntimeModelPickerPreviews();
    }
}
