using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    private static readonly Vector2 RuntimeModelPickerSizeMeters = new Vector2(0.84f, 0.58f);

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
            "Point at an object, or use Next target.",
            new Vector2(0f, 236f),
            new Vector2(870f, 44f),
            24,
            TextAnchor.MiddleCenter,
            new Color(0.9f, 0.95f, 1f, 1f));

        // 対象送り。displayTrackIds の先頭（human 動画なら人）が常に選ばれるので、
        // ボール（Else）へ切り替える手段が「指す」しか無かった。小さい対象は指しにくいので
        // ボタンで回せるようにする（2026-08-28 の要望）。回転パネルの Prev/Next と同じ考え方。
        CreateModelPickerButton(
            panelObj.transform,
            "ModelPickerTargetButton",
            "Next target >",
            new Vector2(330f, 236f),
            new Vector2(220f, 46f),
            StepRuntimeModelPickerTarget,
            TextAnchor.MiddleCenter);

        runtimeModelPickerEntryButtons.Clear();
        for (int i = 0; i < RuntimeModelPickerEntriesPerPage; i++)
        {
            int localIndex = i;
            float y = 166f - i * 74f;
            Button button = CreateModelPickerButton(
                panelObj.transform,
                $"ModelEntryButton_{i}",
                string.Empty,
                new Vector2(0f, y),
                new Vector2(860f, 64f),
                () => OnRuntimeModelPickerEntryClicked(localIndex),
                TextAnchor.MiddleLeft);
            runtimeModelPickerEntryButtons.Add(button);
        }

        runtimeModelPickerPrevButton = CreateModelPickerButton(
            panelObj.transform,
            "ModelPickerPrevButton",
            "< Prev",
            new Vector2(-220f, -282f),
            new Vector2(180f, 54f),
            PrevRuntimeModelPickerPage,
            TextAnchor.MiddleCenter);
        runtimeModelPickerPageText = CreateModelPickerText(
            panelObj.transform,
            "PageText",
            "Page 1/1",
            new Vector2(0f, -282f),
            new Vector2(250f, 54f),
            26,
            TextAnchor.MiddleCenter,
            Color.white);
        runtimeModelPickerNextButton = CreateModelPickerButton(
            panelObj.transform,
            "ModelPickerNextButton",
            "Next >",
            new Vector2(220f, -282f),
            new Vector2(180f, 54f),
            NextRuntimeModelPickerPage,
            TextAnchor.MiddleCenter);
        CreateModelPickerButton(
            panelObj.transform,
            "ModelPickerCloseButton",
            "Close",
            new Vector2(390f, 282f),
            new Vector2(130f, 48f),
            CloseRuntimeModelPickerPanel,
            TextAnchor.MiddleCenter);

        SceneObjectWriter.ApplyActive(root, false);
        return root;
    }


    // いま画面に出ている track を順に回して、モデル変更の対象を切り替える。
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
        TransformWriter.ApplyPose(runtimeModelPickerRoot.transform, pose.position, pose.rotation);

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
        int selectedIndex = Mathf.Clamp(ResolveSelectedModelIndex(trackId, defaultIndex), 0, prefabs.Length - 1);
        int pageCount = GetRuntimeModelPickerPageCount(prefabs.Length);
        runtimeModelPickerPageIndex = Mathf.Clamp(runtimeModelPickerPageIndex, 0, Mathf.Max(0, pageCount - 1));

        if (runtimeModelPickerTitleText != null)
        {
            UiComponentWriter.ApplyTextContent(runtimeModelPickerTitleText, $"{category} models");
        }
        if (runtimeModelPickerStatusText != null)
        {
            string selectedName = prefabs[selectedIndex] != null ? CleanModelDisplayName(prefabs[selectedIndex].name) : "missing";
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


    private void UpdateRuntimeModelPickerEntryButtons(GameObject[] prefabs, int selectedIndex, byte categoryId)
    {
        ClearRuntimeModelPickerPreviews();

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
                string mark = modelIndex == selectedIndex ? "> " : "  ";
                if (label != null)
                {
                    UiComponentWriter.ApplyTextContent(label, $"{mark}{modelIndex + 1}. {modelName}");
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
                if (prefab != null && previewParent != null)
                {
                    float y = 166f - i * 74f;
                    CreateRuntimeModelPickerPreview(prefab, previewParent, new Vector3(-360f, y, -35f));
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
        Vector2 offsetMin = textAnchor == TextAnchor.MiddleLeft ? new Vector2(116f, 0f) : Vector2.zero;
        Vector2 offsetMax = textAnchor == TextAnchor.MiddleLeft ? new Vector2(-18f, 0f) : Vector2.zero;
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


    private void CreateRuntimeModelPickerPreview(GameObject prefab, Transform parent, Vector3 localPosition)
    {
        GameObject holder = new GameObject("ModelPreview");
        holder.transform.SetParent(parent, false);
        holder.transform.localPosition = localPosition;
        holder.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
        runtimeModelPickerPreviewInstances.Add(holder);

        GameObject preview = Object.Instantiate(prefab, holder.transform);
        preview.name = $"Preview_{prefab.name}";
        preview.transform.localPosition = Vector3.zero;
        preview.transform.localRotation = Quaternion.identity;
        preview.transform.localScale = Vector3.one;
        DisableRuntimeModelPickerPreviewComponents(preview);

        if (!TryCalculateLocalRendererBounds(holder.transform, out Bounds bounds))
        {
            holder.transform.localScale = Vector3.one * 52f;
            return;
        }

        preview.transform.localPosition -= bounds.center;
        float maxSize = Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z));
        float scale = maxSize > 0.0001f ? 52f / maxSize : 52f;
        holder.transform.localScale = Vector3.one * Mathf.Clamp(scale, 8f, 90f);
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


    private bool TryCalculateLocalRendererBounds(Transform root, out Bounds bounds)
    {
        bounds = default;
        if (root == null)
        {
            return false;
        }

        Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
        bool hasBounds = false;
        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer renderer = renderers[i];
            if (renderer == null)
            {
                continue;
            }

            Bounds worldBounds = renderer.bounds;
            Vector3 min = worldBounds.min;
            Vector3 max = worldBounds.max;
            Vector3[] corners =
            {
                new Vector3(min.x, min.y, min.z),
                new Vector3(min.x, min.y, max.z),
                new Vector3(min.x, max.y, min.z),
                new Vector3(min.x, max.y, max.z),
                new Vector3(max.x, min.y, min.z),
                new Vector3(max.x, min.y, max.z),
                new Vector3(max.x, max.y, min.z),
                new Vector3(max.x, max.y, max.z),
            };

            for (int j = 0; j < corners.Length; j++)
            {
                Vector3 local = root.InverseTransformPoint(corners[j]);
                if (!hasBounds)
                {
                    bounds = new Bounds(local, Vector3.zero);
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(local);
                }
            }
        }

        return hasBounds;
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
