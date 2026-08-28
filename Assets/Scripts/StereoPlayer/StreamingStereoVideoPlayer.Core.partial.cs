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


    private void OnApplicationPause(bool paused)
    {
        if (paused)
        {
            FlushTrackCustomizationSaveNow();
        }
    }


    private void OnApplicationQuit()
    {
        FlushTrackCustomizationSaveNow();
    }


    private void OnDestroy()
    {
        FlushTrackCustomizationSaveNow();
        UnsubscribeRecenterEvents();
        UnsubscribeVideoPlayerEvents();
        UnbindRuntimeControls();
        DisposeInteractiveMotion();
    }


    private IEnumerator Start()
    {
        ApplyPendingExperimentTrialRequest();
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
        vp.loopPointReached -= OnVideoLoopPointReached;
        vp.loopPointReached += OnVideoLoopPointReached;
        vp.errorReceived -= OnVideoErrorReceived;
        vp.errorReceived += OnVideoErrorReceived;

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
        vp.loopPointReached -= OnVideoLoopPointReached;
        vp.errorReceived -= OnVideoErrorReceived;
    }


    // 端末のデコーダが対応しない動画（例: MPEG-4 Part 2 / mp4v で書かれた
    // source/pre_removal_stereo_video.mp4）を url に入れると prepare が黙って失敗し、
    // frameReady が来ないので画面が黒いままになる。原因が分かるよう必ずログに出す。
    private void OnVideoErrorReceived(VideoPlayer source, string message)
    {
        Debug.LogError($"[Video] error: {message} | url={(source != null ? source.url : "null")}");
    }


    // 実験モードでは ExperimentController が試行ごとにこのシーンをロードし直し、
    // 「どの bundle をどの条件で再生するか」を ExperimentTrialHandoff に置いてくる。
    // 通常シーン（TestScene 等）では Pending が null なので Inspector の設定で動く。
    // 実験の試行として起動したか。Home へ戻るボタンの生成可否に使う。
    private bool startedAsExperimentTrial;

    // 入口シーンへ戻れるか。実験中は戻らせない。
    private bool CanReturnToHomeScene()
    {
        return !startedAsExperimentTrial && enableRuntimeControls;
    }


    private void ReturnToHomeScene()
    {
        if (startedAsExperimentTrial)
        {
            return;
        }

        // 保存待ちがあれば取りこぼさない。シーンを抜けると OnDestroy でも書くが、
        // 明示しておく（Docs/model-selection-persistence.md）。
        FlushTrackCustomizationSaveNow();

        // 次に開いたときへ持ち越さない。
        ExperimentTrialHandoff.Clear();
        HomeLaunchHandoff.Clear();

        Debug.Log("[Home] return to HomeScene");
        UnityEngine.SceneManagement.SceneManager.LoadScene("HomeScene", UnityEngine.SceneManagement.LoadSceneMode.Single);
    }


    // bundle を選び直す。シーンを読み直してピッカーから始める。
    private void ReopenBundlePicker()
    {
        if (startedAsExperimentTrial)
        {
            return;
        }

        FlushTrackCustomizationSaveNow();
        ExperimentTrialHandoff.Clear();
        HomeLaunchHandoff.RequestBundlePicker();

        Debug.Log("[Home] reopen bundle picker");
        UnityEngine.SceneManagement.SceneManager.LoadScene(
            UnityEngine.SceneManagement.SceneManager.GetActiveScene().name,
            UnityEngine.SceneManagement.LoadSceneMode.Single);
    }


    private void ApplyPendingExperimentTrialRequest()
    {
        ExperimentTrialRequest request = ExperimentTrialHandoff.Consume();
        if (request == null)
        {
            // Home の「自由に見る」から来たときだけピッカーを出す。
            // TestScene に焼き込まれた showBundlePickerOnStart は 0 のまま触らない
            // （バッチ実行と EditMode テストが TestScene を開くので、1 にすると
            // ピッカーが選択待ちで止まる）。
            if (HomeLaunchHandoff.ConsumeShowBundlePicker())
            {
                showBundlePickerOnStart = true;
                Debug.Log("[Home] bundle picker requested");
            }

            return;
        }

        // 実験の指示が来たらピッカーの要求は捨てる。両方立つことはないはずだが、
        // 残っていると次に手動で開いたときに誤爆する。
        HomeLaunchHandoff.Clear();

        startedAsExperimentTrial = true;
        bundleFileName = request.bundleFileName;
        showBundlePickerOnStart = false;
        startInNormalMode = request.StartInNormalMode;
        // 被験者に表示条件を切り替えさせない。
        enableNormalModeToggleButton = false;

        Debug.Log(
            $"[Experiment] trial {request.trialIndex}: {request.video} / {request.mode} → {bundleFileName}");
    }


    // 実験ログが再生位置を記録し、ExperimentController が再生開始を待つための読み取り口。
    public double CurrentVideoTimeSeconds
    {
        get { return vp != null ? vp.time : 0d; }
    }


    public bool IsVideoPlaying
    {
        get { return vp != null && vp.isPlaying; }
    }


    private void OnVideoFrameReady(VideoPlayer player, long frame)
    {
        lastFrameReadyFrame = RuntimePlaybackTimeline.NormalizeFrameReadyFrame(frame);
        ApplyVideoFrameTexture(player);
    }


    // isLooping = true なので動画は最後まで再生すると先頭に戻って再生し続ける
    // （被験者が納得するまで何周でも見られる設計）。実験ログには何周見たかを残す。
    private void OnVideoLoopPointReached(VideoPlayer source)
    {
        ExperimentLog.VideoLooped();
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
        FlushTrackCustomizationSaveIfDue();

        // 再生中の切り替えにも追従させたいので毎フレーム適用する。
        // audioTrackCount は Prepare 後に確定するため、ここで見るのが確実。
        if (mute != appliedMute || (mute && vp != null && vp.isPrepared))
        {
            RuntimePlaybackController.ApplyMute(vp, mute);
            appliedMute = mute;
        }

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


    // ハンドラの中で TrySetTrackingOriginMode を呼ぶと、それがまたこのハンドラを呼ぶ。
    // 2026-08-28 の実機ログはこの往復で `[MetaXRFeature] OnAppSpaceChange: 103 / 101` が
    // **4532 行**（毎フレーム振動）。ワールドが毎フレームずれるので、ガーディアンの境界が
    // 合わず、UI パネルも視界から飛ぶ。再入防止とモード一致チェックの二重で止める。
    private bool handlingTrackingOriginUpdate;

    // TrySetTrackingOriginMode が通らない環境で毎回試し続けないための記憶。
    private bool trackingOriginApplyFailed;

    private void OnTrackingOriginUpdated(XRInputSubsystem _)
    {
        if (handlingTrackingOriginUpdate)
        {
            return;
        }

        handlingTrackingOriginUpdate = true;
        try
        {
            if (ForceStationaryTrackingOrigin && !trackingOriginApplyFailed)
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
        finally
        {
            handlingTrackingOriginUpdate = false;
        }
    }


    private void TryApplyPreferredTrackingOriginMode(XRInputSubsystem xr)
    {
        if (!ForceStationaryTrackingOrigin || xr == null || trackingOriginApplyFailed)
        {
            return;
        }

        // **すでに目的のモードなら何もしない。** これが無いと上のループになる。
        if (xr.GetTrackingOriginMode() == TrackingOriginModeFlags.Device)
        {
            return;
        }

        TrackingOriginModeFlags supported = xr.GetSupportedTrackingOriginModes();
        if ((supported & TrackingOriginModeFlags.Device) == 0)
        {
            return;
        }

        if (xr.TrySetTrackingOriginMode(TrackingOriginModeFlags.Device))
        {
            Debug.Log("[XR] tracking origin -> Device");
            return;
        }

        trackingOriginApplyFailed = true;
        Debug.LogWarning("[XR] TrySetTrackingOriginMode(Device) が失敗しました。以降は試みません。");
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
        var result = new List<GameObject>(all.Length);
        for (int i = 0; i < all.Length; i++)
        {
            if (all[i] != null && IsIndexedPrefabName(all[i].name))
            {
                result.Add(all[i]);
            }
        }

        // Resources.LoadAll の戻り順は保証されないため番号順に整列させ、
        // selectedHumanIndex / selectedElseIndex / trackModelIndices の index を安定させる。
        result.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
        return result.ToArray();
    }

    // 運用: Resources/Models/{Human,Animal,Else} 直下の prefab は 2 桁ゼロ埋め番号 + "_" で始める
    // （例: 00_Baseball）。Sources/ 配下の素材はこの規則に合わないため自動的に除外される。
    // 旧実装は「大文字始まりも許可」だったので Else/Sources/DieselLocomotive.glb が紛れ込み、
    // Else が 8 件（本来 7 件）になって index がずれる恐れがあった。
    private static bool IsIndexedPrefabName(string name)
    {
        return !string.IsNullOrEmpty(name) &&
               name.Length >= 3 &&
               char.IsDigit(name[0]) &&
               char.IsDigit(name[1]) &&
               name[2] == '_';
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

