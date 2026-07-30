using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Video;
using UnityEngine.XR;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    // Depends on: shared runtime fields in Core.cs, Bundle/UI/Screens partials
    // Provides: Awake/OnEnable/OnDisable/OnDestroy/Start/OnPrepared/Update/LateUpdate and recenter flow

    private void OnEnable()
    {
        SubscribeRecenterEvents();
    }


    private void OnDisable()
    {
        UnsubscribeRecenterEvents();
    }


    private void OnDestroy()
    {
        UnsubscribeRecenterEvents();
        UnsubscribeVideoPlayerEvents();
        UnbindRuntimeControls();
        DisposeInteractiveMotion();
    }


    private IEnumerator Start()
    {
        LoadModelPrefabs();

        vp = GetComponent<VideoPlayer>();
        if (vp == null)
        {
            yield break;
        }

        RuntimePlaybackController.ConfigureForApiPlayback(vp);
        vp.frameReady -= OnVideoFrameReady;
        vp.frameReady += OnVideoFrameReady;
        vp.prepareCompleted -= OnPrepared;
        vp.prepareCompleted += OnPrepared;

        if (showBundlePickerOnStart)
        {
            yield return RunBundlePickerFlowAndPrepareVideo();
        }
        else
        {
            yield return EnsureBundleAndPrepareVideo();
        }
    }


    private void OnPrepared(VideoPlayer source)
    {
        float w = source.width;
        float h = source.height;

        EnsureScreensExist();
        SetupScreensAndMaterials();

        if (w <= 0 || h <= 0)
        {
            RuntimePlaybackController.Apply(vp, RuntimePlaybackController.Command.Play);
            return;
        }

        float perEyeWidth = manifest != null && manifest.eye_w > 0 ? manifest.eye_w : w * 0.5f;
        float aspect = perEyeWidth / h;
        Vector3 screenScale = new Vector3(aspect * BaseHeight, BaseHeight, 1f);

        if (leftScreen != null)
        {
            TransformWriter.ApplyLocalScale(leftScreen, screenScale);
        }

        if (rightScreen != null)
        {
            TransformWriter.ApplyLocalScale(rightScreen, screenScale);
        }

        PlaceScreens();
        EnsureRuntimeControls();

        RuntimePlaybackController.Apply(vp, RuntimePlaybackController.Command.Play);
        UpdatePauseButtonLabel();
        vp.prepareCompleted -= OnPrepared;
    }


    private void UnsubscribeVideoPlayerEvents()
    {
        if (vp == null)
        {
            return;
        }

        vp.frameReady -= OnVideoFrameReady;
        vp.prepareCompleted -= OnPrepared;
        vp.prepareCompleted -= OnModeSwitchPrepared;
    }


    private void OnVideoFrameReady(VideoPlayer player, long frame)
    {
        lastFrameReadyFrame = RuntimePlaybackTimeline.NormalizeFrameReadyFrame(frame);
        ApplyVideoFrameTexture(player);
    }


    private void LateUpdate()
    {
        if (!ForceScreensInFrontOfViewCamera)
        {
            return;
        }

        Camera cam = GetViewCamera();
        if (cam == null)
        {
            return;
        }

        StereoScreenPlacement.ForcedPose pose = StereoScreenPlacement.ResolveForcedInFrontPose(
            cam.transform.position,
            cam.transform.forward,
            screenDistanceMeters);

        if (leftScreen != null)
        {
            TransformWriter.ApplyPose(leftScreen, pose.position, pose.rotation);
        }

        if (rightScreen != null)
        {
            TransformWriter.ApplyPose(rightScreen, pose.position, pose.rotation);
        }
    }


    private void Update()
    {
        StreamingStereoUpdateFlow.Decision decision = StreamingStereoUpdateFlow.Resolve(bundlePickerActive);
        if (decision.updateBundlePickerPlacement)
        {
            UpdateBundlePickerTick();
            return;
        }

        if (decision.updateRuntimePlayback)
        {
            UpdateRuntimePlaybackTick();
        }
    }


    private void UpdateBundlePickerTick()
    {
        UpdateBundlePickerPlacement();
    }


    private void UpdateRuntimePlaybackTick()
    {
        UpdatePickingTick();
        DisplayModelTick();
        DetectRuntimeRecenterFallback();
        HandleRuntimePauseInput();
        RefreshRuntimeSettingsPerFrame();
        UpdateRuntimeProgressUi();
    }


    private void UpdatePickingTick()
    {
        if (TryPick(out PickResult pick))
        {
            TrySelectDisplayTrackFromPick(pick);
        }
    }


    private RuntimeClock.TickContext GetRuntimeTickContext()
    {
        return RuntimeClock.ResolveTickContext(Time.time, Time.deltaTime, Time.frameCount);
    }


    private float GetRuntimeUnscaledDeltaTime()
    {
        return Time.unscaledDeltaTime;
    }


    private void SubscribeRecenterEvents()
    {
        UnsubscribeRecenterEvents();
        SubsystemManager.GetSubsystems(xrInputSubsystems);
        for (int i = 0; i < xrInputSubsystems.Count; i++)
        {
            XRInputSubsystem xr = xrInputSubsystems[i];
            if (xr == null)
            {
                continue;
            }

            TryApplyPreferredTrackingOriginMode(xr);
            xr.trackingOriginUpdated += OnTrackingOriginUpdated;
        }
    }


    private void UnsubscribeRecenterEvents()
    {
        for (int i = 0; i < xrInputSubsystems.Count; i++)
        {
            XRInputSubsystem xr = xrInputSubsystems[i];
            if (xr == null)
            {
                continue;
            }

            xr.trackingOriginUpdated -= OnTrackingOriginUpdated;
        }

        xrInputSubsystems.Clear();
    }


    private void OnTrackingOriginUpdated(XRInputSubsystem _)
    {
        if (ForceStationaryTrackingOrigin)
        {
            for (int i = 0; i < xrInputSubsystems.Count; i++)
            {
                XRInputSubsystem xr = xrInputSubsystems[i];
                if (xr != null)
                {
                    TryApplyPreferredTrackingOriginMode(xr);
                }
            }
        }

        RecenterScreensToCurrentFacing();
    }


    private void TryApplyPreferredTrackingOriginMode(XRInputSubsystem xr)
    {
        if (!ForceStationaryTrackingOrigin || xr == null)
        {
            return;
        }

        TrackingOriginModeFlags supported = xr.GetSupportedTrackingOriginModes();
        if ((supported & TrackingOriginModeFlags.Device) == 0)
        {
            return;
        }

        xr.TrySetTrackingOriginMode(TrackingOriginModeFlags.Device);
    }


    private void DetectRuntimeRecenterFallback()
    {
        if (ForceScreensInFrontOfViewCamera)
        {
            headPosePrimed = false;
            return;
        }

        Transform head = GetViewOrHeadTransform();
        if (head == null)
        {
            headPosePrimed = false;
            return;
        }

        if (!headPosePrimed)
        {
            headPosePrimed = true;
            lastHeadPos = head.position;
            lastHeadRot = head.rotation;
            return;
        }

        float deltaPos = Vector3.Distance(lastHeadPos, head.position);
        float deltaRotDeg = Quaternion.Angle(lastHeadRot, head.rotation);
        if (deltaPos > 0.35f || deltaRotDeg > 35f)
        {
            RecenterScreensToCurrentFacing();
        }

        lastHeadPos = head.position;
        lastHeadRot = head.rotation;
    }


    private void RecenterScreensToCurrentFacing()
    {
        if (ForceScreensInFrontOfViewCamera)
        {
            return;
        }

        if (leftScreen == null && rightScreen == null)
        {
            return;
        }

        PlaceScreens();
    }


    // カノニカルボーン名にリネーム済み（= SMAL FKで正しく姿勢追従する）Animalモデルを
    // Change Model UI / selectedAnimalIndex の先頭に固定表示するための優先順位。
    // Resources/Models/Animal 内の全prefabが2桁ゼロ埋めの番号プレフィックス
    // （例: "00_Dog.prefab", "14_BoarV2.prefab"）にリネーム済みなので、この配列に載って
    // いないモデルも含め、selectedAnimalIndex に入れる数字とファイル名の番号が常に一致する
    // （Assets/Editor/AnimalIndexPrefixer.cs 参照）。ゼロ埋めなのはUnityのordinal文字列
    // ソートで "10_Foo" が "6_Bar" より前に来て番号とズレるのを防ぐため。新しい種を
    // リネームしたらここに追記する（docs/animal-bone-rename-mapping.md 参照）。
    private static readonly string[] AnimalModelPriorityOrder =
    {
        "00_Dog", "01_Wolf", "02_WildBoar", "03_Buffalo", "04_Lion", "05_Horse",
    };

    private void LoadModelPrefabs()
    {
        humanPrefabs  = LoadPrefabsFromResources("Models/Human");
        animalPrefabs = SortByPriority(LoadPrefabsFromResources("Models/Animal"), AnimalModelPriorityOrder);
        elsePrefabs   = LoadPrefabsFromResources("Models/Else");
        Debug.Log($"[Model] Human: {humanPrefabs.Length} prefab, Animal: {animalPrefabs.Length} prefab, Else: {elsePrefabs.Length} prefab");
    }

    // Resources.LoadAll は Sources/ 内 FBX も拾うため、大文字始まり／数字始まりの名前のみ使用する
    // 命名規則: Prefab は大文字か数字始まり（Bear.prefab, 0_Dog.prefab）、FBX は小文字始まり（bear.fbx）
    private static GameObject[] LoadPrefabsFromResources(string resourcePath)
    {
        GameObject[] all = Resources.LoadAll<GameObject>(resourcePath);
        int count = 0;
        for (int i = 0; i < all.Length; i++)
            if (all[i] != null && all[i].name.Length > 0 && IsPrefabNameStart(all[i].name[0]))
                count++;

        GameObject[] result = new GameObject[count];
        int j = 0;
        for (int i = 0; i < all.Length; i++)
            if (all[i] != null && all[i].name.Length > 0 && IsPrefabNameStart(all[i].name[0]))
                result[j++] = all[i];
        return result;
    }

    private static bool IsPrefabNameStart(char c)
    {
        return char.IsUpper(c) || char.IsDigit(c);
    }

    private static GameObject[] SortByPriority(GameObject[] prefabs, string[] priorityOrder)
    {
        var sorted = new List<GameObject>(prefabs);
        sorted.Sort((a, b) =>
        {
            int indexA = System.Array.IndexOf(priorityOrder, a.name);
            int indexB = System.Array.IndexOf(priorityOrder, b.name);
            if (indexA < 0) indexA = priorityOrder.Length;
            if (indexB < 0) indexB = priorityOrder.Length;
            return indexA != indexB
                ? indexA.CompareTo(indexB)
                : string.Compare(a.name, b.name, System.StringComparison.Ordinal);
        });
        return sorted.ToArray();
    }
}

