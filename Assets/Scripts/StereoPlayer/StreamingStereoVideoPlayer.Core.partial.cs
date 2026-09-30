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
            appPausedAtUnscaledTime = Time.unscaledTime;
            return;
        }

        // 休止中も Time.realtimeSinceStartup は進む。戻した最初のフレームで「フレームが進んでいない時間」に
        // 休止時間が丸ごと乗り、DetectStalledPlayback が偽の詰まりとして Stop → Prepare → シークをやり直して
        // 1 秒ほど黒くなる（2026-09-29 の監査）。計測をやり直す。
        stallLastFrame = -1L;
        stallSinceRealtime = -1f;
        // モーションが動画を止めている最中に外されたら、その時間は「モーションで止まっていた時間」ではない。
        // 引かないと interactions.csv の paused_sec に装着外の時間が丸ごと乗る（2026-09-29 の 4 回目の監査）。
        NoteAppResumeForRandomMotionPause();
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
        Debug.Log($"[LOADTIME] シーン開始（起動から {Time.realtimeSinceStartup:F2} 秒）");
        ApplyPendingExperimentTrialRequest();

        // **ピッカーを先に出して、読み込みはその裏で進める。**
        // bundle を選ぶのに人は数秒使うので、その間に終わる。
        // 待つのは実際に prefab が要る直前（bundle を開くところ）だけ。
        StartCoroutine(LoadModelPrefabsAsync());

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

        Debug.Log($"[LOADTIME] ピッカーへ入る（起動から {Time.realtimeSinceStartup:F2} 秒）");
        if (showBundlePickerOnStart)
        {
            yield return RunBundlePickerFlowAndPrepareVideo();
        }
        else
        {
            yield return WaitForModelPrefabs();
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
            // 画面も UI も作れていない異常系。**ここで「再生が始まった」を立てない。**立てると
            // ExperimentController が読み込み待ちを抜けてしまい、黒いまま試行が進む。立てなければ
            // load_timeout で aborted として残り、実験者に「やり直す / 飛ばす」が出る
            // （2026-09-30 の 5 回目の監査）。
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

        // 再生を始める時点の目の位置を基準にする。bundle を変えると見る場所も変わる。
        ResetScreenAnchorLock();
        PlaceScreens();
        EnsureRuntimeControls();

        RuntimePlaybackController.Apply(vp, RuntimePlaybackController.Command.Play);
        hasPlaybackStarted = true;
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
        // 実験の試行は「再生が始まるまで待つ」ので、デコードできない動画（mp4v 等）はここで失敗にしないと
        // timeout まで待つことになる（2026-09-25）。再生中のエラーでも立つが、ExperimentController は
        // 読み込み中しか見ない。
        FailBundleLoad($"video error: {message}");
    }


    // 実験モードでは ExperimentController が試行ごとにこのシーンをロードし直し、
    // 「どの bundle をどの条件で再生するか」を ExperimentTrialHandoff に置いてくる。
    // 通常シーン（TestScene 等）では Pending が null なので Inspector の設定で動く。
    // 実験の試行として起動したか。Home へ戻るボタンの生成可否に使う。
    private bool startedAsExperimentTrial;

    // 実験の指示「この category の track だけ読む」（null / 空 = 全部）。
    // TryReadFrameObjects で落とすので、表示・pick・ピッカー・深度較正のすべてがこの絞り込みを見る。
    // public なのはバッチ検証（BatchPlaybackLogger の -onlyCategory）が play mode 前に流し込むため。
    // **シーンには書き込まない**: [HideInInspector] は Inspector に出さないだけで serialize は止めないので
    // [NonSerialized] を付ける（付けないと TrialScene を保存した瞬間に焼き込まれる。2026-09-29 の監査）。
    [System.NonSerialized] public string experimentOnlyCategory;

    // 実験の指示「animal の既定モデルはこの prefab 名」。prefab 一覧はフレームを跨いで読まれるので、
    // 最初に animal track を置くときに index へ解決する（ApplyExperimentPreferredAnimalModelOnce）。
    [System.NonSerialized] public string experimentPreferredAnimalModelName;

    // 実験の指示「model_selection.json とセッション上書きを読まない」。
    [System.NonSerialized] private bool experimentSkipTrackCustomizationRestore;

    // 実験の単眼条件: 除去前ステレオ動画の左目映像を 1 枚の板に出し、両目でその 1 枚を見る（右の板は消す）。
    // Screens.cs の SetupScreensAndMaterials / PlaceScreens。startInNormalMode と組で使う（Monocular は normal mode の一種）。
    [System.NonSerialized] private bool experimentMonocular;

    // Settings パネルの Motion トグルを出さずに固定するか。単眼・ステレオ（モデルが出ない）は固定、
    // 置換ありは被験者が切り替えられる（2026-09-25 のユーザー指示。ExperimentTrialRequest.LockInteractiveMotion）。
    [System.NonSerialized] private bool experimentLockInteractiveMotion;


    private bool IsCategoryExcludedByExperiment(byte categoryId)
    {
        return !string.IsNullOrEmpty(experimentOnlyCategory) && !IsCategoryNamed(categoryId, experimentOnlyCategory);
    }


    // animal の既定モデルを prefab 名で決める。一覧がまだ無ければ次回に持ち越す。
    private void ApplyExperimentPreferredAnimalModelOnce()
    {
        if (string.IsNullOrEmpty(experimentPreferredAnimalModelName) || animalPrefabs == null || animalPrefabs.Length == 0)
        {
            return;
        }

        string wanted = experimentPreferredAnimalModelName;
        experimentPreferredAnimalModelName = null;

        for (int i = 0; i < animalPrefabs.Length; i++)
        {
            GameObject prefab = animalPrefabs[i];
            if (prefab != null && string.Equals(prefab.name, wanted, System.StringComparison.OrdinalIgnoreCase))
            {
                selectedAnimalIndex = i;
                Debug.Log($"[Experiment] animal の既定モデル = {prefab.name}（index {i}）");
                return;
            }
        }

        Debug.LogWarning($"[Experiment] animal モデル '{wanted}' が一覧に無いので selectedAnimalIndex={selectedAnimalIndex} のまま");
    }


    // 実験の説明パネルを画面に被せないための読み取り口。画面の中心・上・右・大きさ（m）。
    // 画面より手前に置いた UI でないとレイが画面のコライダーに取られて押せない（2026-09-11 実機指摘）ので、
    // ExperimentController はこれを基準に画面の外側へパネルを置く。
    public bool TryGetScreenFrame(out Vector3 center, out Vector3 up, out Vector3 right, out Vector2 sizeMeters)
    {
        center = Vector3.zero;
        up = Vector3.up;
        right = Vector3.right;
        sizeMeters = Vector2.zero;

        Transform basis = leftScreen != null ? leftScreen : rightScreen;
        if (basis == null)
        {
            return false;
        }

        center = leftScreen != null && rightScreen != null
            ? (leftScreen.position + rightScreen.position) * 0.5f
            : basis.position;
        GetScreenSizeMeters(basis, out float width, out float height, out _);
        up = basis.up;
        right = basis.right;
        sizeMeters = new Vector2(Mathf.Abs(width), Mathf.Abs(height));
        return sizeMeters.x > 0.001f && sizeMeters.y > 0.001f;
    }

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

        experimentOnlyCategory = request.onlyCategory;
        experimentPreferredAnimalModelName = request.preferredAnimalModelName;
        experimentSkipTrackCustomizationRestore = request.skipTrackCustomizationRestore;
        experimentMonocular = request.StartMonocular;

        // モーションは条件で決める（置換あり = ON、それ以外 = OFF）。シーンの serialize 値
        // （TrialScene は 0）より優先する（2026-09-25）。
        // **置換あり条件だけ、被験者が Settings の Motion で切り替えられる**（同日のユーザー指示で
        // 固定を取り消した）。単眼・ステレオはモデルが出ないので固定のまま（トグルを出さない）。
        enableInteractiveMotion = request.InteractiveMotionEnabled;
        experimentLockInteractiveMotion = request.LockInteractiveMotion;

        // 置換ありブロックのチュートリアルだけ発火間隔を短くし、練習中に必ず 1 回は見せる。
        if (request.UseTutorialInteractiveMotionInterval)
        {
            interactiveMotionMinIntervalSeconds = ExperimentTrialRequest.TutorialInteractiveMotionMinIntervalSeconds;
            interactiveMotionMaxIntervalSeconds = ExperimentTrialRequest.TutorialInteractiveMotionMaxIntervalSeconds;
        }

        Debug.Log(
            $"[Experiment] trial {request.trialIndex}: {request.video} / {request.mode} → {bundleFileName} " +
            $"monocular={experimentMonocular} " +
            $"motion={enableInteractiveMotion}{(experimentLockInteractiveMotion ? "(locked)" : "(switchable)")} " +
            $"onlyCategory={(string.IsNullOrEmpty(request.onlyCategory) ? "(all)" : request.onlyCategory)} " +
            $"animalModel={(string.IsNullOrEmpty(request.preferredAnimalModelName) ? "(inspector)" : request.preferredAnimalModelName)} " +
            $"skipRestore={request.skipTrackCustomizationRestore}");
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


    // 一度でも再生を始めたか。ExperimentController が「読み込みが終わった」の判定に使う。
    // `IsVideoPlaying` だけで待つと、Prepare が終わって Play した直後に被験者が A を押した場合に
    // 読み込み中のまま期限切れになり、その試行が捨てられた（2026-09-29 の 4 回目の監査）。
    public bool HasPlaybackStarted
    {
        get { return hasPlaybackStarted; }
    }

    private bool hasPlaybackStarted;


    // perf.csv 用。動画の現在フレーム（Prepare 前は -1）。
    public long CurrentVideoFrame
    {
        get { return vp != null && vp.isPrepared ? vp.frame : -1L; }
    }


    public bool InteractiveMotionEnabled
    {
        get { return enableInteractiveMotion; }
    }


    public float ScreenDistanceMeters
    {
        get { return screenDistanceMeters; }
    }


    public bool IsExperimentMonocular
    {
        get { return experimentMonocular; }
    }


    public bool HumanBoneLengthCorrectionEnabled
    {
        get { return enableHumanBoneLengthCorrection; }
    }


    private void OnVideoFrameReady(VideoPlayer player, long frame)
    {
        // sendFrameReadyEvents は切ってあるので通常は呼ばれない。
        // 誰かが再び有効にしたときのために、記録だけは残しておく。
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


    // **実機で測るための一時的な仕掛け（2026-09-09）。**
    //
    // バッチは実機の 6 倍の fps で走るため、⑧ のように毎 Update 反復する処理は
    // バッチで測っても実機を再現しない（1 動画フレームあたり 15.5 tick 対 2.4 tick）。
    // 実機で実際に何が起きているかを見るために、診断ログを実機でも出す。
    //
    // **ログ自体が fps を下げると、測りたいもの（tick 数）が変わる。**
    // なので毎秒 1 回、実測した fps と tick/フレームを [FPS] で出して検算できるようにする。
    //
    // **計測するときだけ true にする。**ログを毎フレーム出すので実機が重くなり、
    // 測りたいもの（1 動画フレームあたりの tick 数）自体が変わりうる。
    // 2026-09-09 の計測では 71.0〜72.1fps を維持できていたので影響は無かったが、
    // 常用するものではない。
    public bool logDeviceDiagnostics;

    private bool deviceDiagnosticsApplied;
    private float deviceDiagLoggedAt;
    private int deviceDiagUpdateCount;

    private float loggedScreenDistance = float.NaN;
    private float loggedFovx = float.NaN;

    private void LogPlacementSettingsSnapshot(string reason)
    {
        loggedScreenDistance = screenDistanceMeters;
        loggedFovx = runtimeFovxDeg;
        Debug.Log(
            $"[SET] {reason} screenDist={screenDistanceMeters:F3}m fovx={runtimeFovxDeg:F1}deg " +
            $"fovxOverride={useRuntimeFovxOverride} fitToFov={fitScreenToFov} " +
            $"lockAnchor={lockScreenAnchorPosition} originFacing={useTrackingOriginForScreenFacing} " +
            $"refineDepth={refineDepthFromProjectedBones} fastLo={depthRefineFastTrackLow:F2} " +
            $"fastHi={depthRefineFastTrackHigh:F2} smoothSec={projectedDepthSmoothingSeconds:F2}");
    }


    private void ApplyDeviceDiagnosticsIfEnabled()
    {
        if (!logDeviceDiagnostics)
        {
            return;
        }

        if (!deviceDiagnosticsApplied)
        {
            deviceDiagnosticsApplied = true;
            logPlacementMeasurement = true;
            logPlacementMeasurementEveryNFrames = 1;
            logDepthRefineStages = true;
            Debug.Log("[FPS] 実機診断ログを有効化した（計測後に logDeviceDiagnostics を false へ）");
            LogPlacementSettingsSnapshot("起動時");
        }

        // **設定値は推測せずログに出す。**
        // 2026-09-09、実機とバッチで深度が 1.8 倍違った原因を Screen Dist だと推測したが、
        // ログに出していなかったので確かめられなかった。ユーザー指摘
        // 「疑うなら調べればいいのでは、ログあるなら」。**変わったら必ず出す。**
        if (!Mathf.Approximately(loggedScreenDistance, screenDistanceMeters) ||
            !Mathf.Approximately(loggedFovx, runtimeFovxDeg))
        {
            LogPlacementSettingsSnapshot("変更");
        }

        deviceDiagUpdateCount++;
        float now = Time.unscaledTime;
        if (now - deviceDiagLoggedAt < 1f)
        {
            return;
        }

        float elapsed = Mathf.Max(0.0001f, now - deviceDiagLoggedAt);
        float fps = deviceDiagUpdateCount / elapsed;
        deviceDiagLoggedAt = now;
        deviceDiagUpdateCount = 0;
        Debug.Log(
            $"[FPS] update={fps:F1}/s  動画30fpsなら {fps / 30f:F2} tick/フレーム" +
            $"  vp.frame={(vp != null ? vp.frame : -1)}");
    }


    private void Update()
    {
        ApplyDeviceDiagnosticsIfEnabled();
        FlushTrackCustomizationSaveIfDue();

        // 対象を掴んで回す。パネルの掴み代と同じく毎フレーム走らせる必要がある。
        UpdateGrabRotate();

        // 掴み代を掴んでいる間、コントローラの前後移動をパネル距離へ反映する。
        //
        // **EnsureRuntimeControls に置いてはいけない。** あれは OnPrepared から 1 回しか
        // 呼ばれないので、掴んでも offset が 0 のまま動かなかった（2026-09-01 実機ログ:
        // 「掴んだ pointerOK=True」は出るのに「moved=」が一度も出ない）。
        // この下には bundle ピッカー用の早期 return があるので、その前で呼ぶ。
        UpdateRuntimePanelDrag();

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
        // 実験ログ: パネルの開閉と Screen Dist の変更を、状態の変化として 1 行ずつ残す。
        LogExperimentPanelStateChanges();
        FlushPendingExperimentScreenDistLog();
        TickInteractiveMotionAudioFade();
        // **画面のテクスチャをここで当てる。**
        // 以前は OnVideoFrameReady からしか呼ばれておらず、
        // sendFrameReadyEvents を切ったとたん画面が真っ黒になった（2026-09-04）。
        // player.texture の**中身**はイベントに関係なく更新されるので、
        // 参照を当て直すだけでよい。Stop → Prepare の後に差し替わるのも拾える。
        ApplyVideoFrameTexture(vp);

        DetectStalledPlayback();
        ResumeAfterStallIfPending();
        RunBatchSeekTestIfRequested();
        VerifyBatchSeekTestIfDue();
    }


    // **「再生中なのにフレームが進まない」を検出して復帰させる。**
    //
    // 実機で再現した（2026-09-04）。isPlaying=True・prepared=True のまま
    // frame=80 から 9 秒間 1 つも進まず、logcat では Play() の直後に
    // c2.qti.avc.decoder が flush → release されていた。デコーダが落ちても
    // VideoPlayer 側の状態は「再生中」のままなので、押しても何も起きない。
    //
    // 原因側（なぜ release されるか）は未特定。ただ、ここで詰まると動画を
    // 見る手段が完全に無くなるので、まず抜け出せるようにする。
    private void DetectStalledPlayback()
    {
        if (vp == null || !vp.isPrepared || !vp.isPlaying)
        {
            stallLastFrame = -1L;
            stallSinceRealtime = -1f;
            return;
        }

        long frame = vp.frame;
        if (frame != stallLastFrame)
        {
            stallLastFrame = frame;
            stallSinceRealtime = Time.realtimeSinceStartup;
            return;
        }

        if (stallSinceRealtime < 0f)
        {
            stallSinceRealtime = Time.realtimeSinceStartup;
            return;
        }

        float stalledSeconds = Time.realtimeSinceStartup - stallSinceRealtime;
        if (stalledSeconds < StallRecoverySeconds)
        {
            return;
        }

        stallSinceRealtime = Time.realtimeSinceStartup;
        stallRecoveryCount++;
        Debug.LogWarning(
            $"[STALL] 再生中なのに {stalledSeconds:F1} 秒フレームが進みません " +
            $"frame={frame} prepared={vp.isPrepared} url={(string.IsNullOrEmpty(vp.url) ? "(空)" : "あり")} " +
            $"復帰 {stallRecoveryCount} 回目");

        // 同じ位置へ戻せるよう覚えてから作り直す。
        double resumeSeconds = vp.time;
        vp.Stop();
        vp.Prepare();
        stallResumeSeconds = resumeSeconds;
        stallResumePending = true;
    }


    // Prepare が終わったら、止まった位置へ戻して再生を続ける。
    private void ResumeAfterStallIfPending()
    {
        if (!stallResumePending || vp == null || !vp.isPrepared)
        {
            return;
        }

        stallResumePending = false;
        RuntimePlaybackController.ApplySeekTarget(
            vp, new RuntimePlaybackTimeline.SeekTarget(true, stallResumeSeconds, false, 0L));
        RuntimePlaybackController.Apply(vp, RuntimePlaybackController.Command.Play);
        Debug.Log($"[STALL] {stallResumeSeconds:F2} 秒の位置から再生を戻しました");
        UpdatePauseButtonLabel();
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

            // **再センタリングを記録に残す。**被験者が Meta ボタンで Reset View すると頭部姿勢の座標が
            // 1 フレームで飛び、画面も動く。行が無いと解析でその不連続の理由が追えなかった
            // （2026-09-29 の 4 回目の監査）。この行が実機のログに出るかどうかで、Quest の Reset View が
            // trackingOriginUpdated を出すのか（= パネルの置き直しが効いているのか）も判る。
            Transform resetHead = GetViewOrHeadTransform();
            ExperimentLog.Operation(
                "reset_view",
                resetHead != null
                    ? $"head_x={ExperimentCsv.Format(resetHead.position.x)} head_y={ExperimentCsv.Format(resetHead.position.y)} " +
                      $"head_z={ExperimentCsv.Format(resetHead.position.z)} head_yaw={ExperimentCsv.Format(resetHead.eulerAngles.y)}"
                    : "head=none");
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

        // **既定では頭の動きで再センタリングしない。**
        //
        // 以前は 0.35m / 35° を越えると画面が新しい正面へ飛んでいたが、
        // 設定を操作するために視線を動かすとこれが発動し、画面が設定パネルに被って
        // 戻れなくなっていた（2026-09-07 実機指摘）。
        // 画面はトラッキング原点の正面に固定するので追従は要らない。
        // 原点自体はユーザーの Reset View で動き、それは
        // trackingOriginUpdated → RecenterScreensToCurrentFacing で拾っている。
        if (autoRecenterScreensOnHeadMove)
        {
            float deltaPos = Vector3.Distance(lastHeadPos, head.position);
            float deltaRotDeg = Quaternion.Angle(lastHeadRot, head.rotation);
            if (deltaPos > AutoRecenterHeadMoveMeters || deltaRotDeg > AutoRecenterHeadTurnDegrees)
            {
                RecenterScreensToCurrentFacing();
            }
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

        // Reset View は「いまの場所・いまの向きを正面にする」操作なので、
        // 固定していた基準点もここで取り直す。これ以外では動かさない。
        ResetScreenAnchorLock();
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

    // モデル prefab を**フレームを跨いで**読む。
    //
    // **Resources.LoadAll を Start で直接呼んではいけない。**
    // あれは同期で、prefab だけでなく mesh / texture まで全部読む。
    // 実機で 75 prefab に **2304ms**（2026-09-04 実測: Human 867 / Animal 1246 / Else 195）。
    // その間 Update が一切回らず、VR では頭の向きだけコンポジタが再投影するので
    // 「コントローラーだけ空中で固まる」見え方になる。
    //
    // 1 つずつ Resources.LoadAsync すればフレームを跨げるが、そのためには
    // 読む前に名前を知っている必要がある。Resources に一覧 API は無いので、
    // ModelResourceIndexGenerator がビルド前に一覧を作っている。
    private IEnumerator LoadModelPrefabsAsync()
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var human = new List<GameObject>();
        var animal = new List<GameObject>();
        var other = new List<GameObject>();

        TextAsset index = Resources.Load<TextAsset>(ModelIndexResourcePath);
        if (index == null || string.IsNullOrEmpty(index.text))
        {
            // 一覧が無いときは従来どおり同期で読む。遅いが壊れはしない。
            Debug.LogWarning(
                $"[Model] {ModelIndexResourcePath} が無いので同期読み込みに落ちます。" +
                "Tools/VisionGraft/モデル一覧を作り直す で作れます。");
            humanPrefabs  = LoadPrefabsFromResources("Models/Human");
            animalPrefabs = SortByPriority(LoadPrefabsFromResources("Models/Animal"), AnimalModelPriorityOrder);
            elsePrefabs   = LoadPrefabsFromResources("Models/Else");
        }
        else
        {
            string[] paths = index.text.Split(ModelIndexLineSeparators, System.StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < paths.Length; i++)
            {
                string path = paths[i].Trim();
                if (path.Length == 0)
                {
                    continue;
                }

                ResourceRequest request = Resources.LoadAsync<GameObject>(path);
                while (!request.isDone)
                {
                    yield return null;
                }

                if (!(request.asset is GameObject prefab))
                {
                    Debug.LogWarning($"[Model] 読めません: {path}");
                    continue;
                }

                if (path.StartsWith("Models/Human", System.StringComparison.Ordinal))
                {
                    human.Add(prefab);
                }
                else if (path.StartsWith("Models/Animal", System.StringComparison.Ordinal))
                {
                    animal.Add(prefab);
                }
                else
                {
                    other.Add(prefab);
                }
            }

            // 並びは従来と同じにする。ずれると selectedHumanIndex や
            // trackModelIndices が指すモデルが変わってしまう。
            human.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
            other.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
            animal.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
            humanPrefabs = human.ToArray();
            elsePrefabs = other.ToArray();
            animalPrefabs = SortByPriority(animal.ToArray(), AnimalModelPriorityOrder);
        }

        Debug.Log($"[Model] Human: {humanPrefabs.Length} prefab, Animal: {animalPrefabs.Length} prefab, Else: {elsePrefabs.Length} prefab");
        Debug.Log($"[LOADTIME] prefab 読み込み {stopwatch.ElapsedMilliseconds}ms（フレームを跨いで）");
        VerifyModelPrefabOrderInBatch();
        modelPrefabsReady = true;
    }


    // 一覧経由で読んだ並びが、従来の Resources.LoadAll と同じかを確かめる。
    // **並びがずれると trackModelIndices や selectedHumanIndex が別のモデルを指す。**
    // 静かに壊れるので、batchmode でだけ突き合わせる（実機では走らせない）。
    private void VerifyModelPrefabOrderInBatch()
    {
        if (!Application.isBatchMode)
        {
            return;
        }

        CompareModelPrefabOrder("Human", humanPrefabs, LoadPrefabsFromResources("Models/Human"));
        CompareModelPrefabOrder(
            "Animal", animalPrefabs, SortByPriority(LoadPrefabsFromResources("Models/Animal"), AnimalModelPriorityOrder));
        CompareModelPrefabOrder("Else", elsePrefabs, LoadPrefabsFromResources("Models/Else"));
    }


    private static void CompareModelPrefabOrder(string label, GameObject[] actual, GameObject[] expected)
    {
        if (actual.Length != expected.Length)
        {
            Debug.LogError($"[ORDERCHECK] {label} 数が違う 一覧={actual.Length} LoadAll={expected.Length}");
            return;
        }

        for (int i = 0; i < actual.Length; i++)
        {
            if (actual[i] == null || expected[i] == null || actual[i].name != expected[i].name)
            {
                Debug.LogError(
                    $"[ORDERCHECK] {label} [{i}] が違う 一覧={(actual[i] != null ? actual[i].name : "null")} " +
                    $"LoadAll={(expected[i] != null ? expected[i].name : "null")}");
                return;
            }
        }

        Debug.Log($"[ORDERCHECK] {label} {actual.Length} 件すべて一致");
    }


    // prefab が揃うまで待つ。bundle を開く直前にだけ必要で、
    // それより前（ピッカー表示中）は揃っていなくてよい。
    private IEnumerator WaitForModelPrefabs()
    {
        if (modelPrefabsReady)
        {
            yield break;
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (!modelPrefabsReady)
        {
            yield return null;
        }

        Debug.Log($"[LOADTIME] prefab を待った時間 {stopwatch.ElapsedMilliseconds}ms");
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

