using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using System.IO;
using UnityEngine;

// バッチモードでシーンを再生し、配置系の診断ログ（[GAP] / [PLACE] / [BALLHEAD]）を
// -logFile に落とすためだけの一時ツール。
//
//   Unity.exe -batchmode -projectPath <proj> -executeMethod BatchPlaybackLogger.Run
//             -logFile out.log -scene Assets/Scenes/TrialScene.unity -playSeconds 16
//
// -nographics は付けないこと（VideoPlayer がフレームを進めないと meta が読まれない）。
// ドメインリロードを越えて状態を持ち越すため SessionState を使う。
public static partial class BatchPlaybackLogger
{
    private const string KeyRunning = "BatchPlaybackLogger.Running";
    private const string KeyDeadline = "BatchPlaybackLogger.Deadline";
    private const string KeyStarted = "BatchPlaybackLogger.Started";
    private const string KeyCaptureFrames = "BatchPlaybackLogger.CaptureFrames";
    private const string KeyHeadShift = "BatchPlaybackLogger.HeadShift";
    private const string KeyTargetFps = "BatchPlaybackLogger.TargetFps";
    private const string KeyCaptureDir = "BatchPlaybackLogger.CaptureDir";
    private const string KeyCaptureWidth = "BatchPlaybackLogger.CaptureWidth";
    // 骨を注視する補助カメラの絵（2026-10-04、-captureViews）。CaptureAuxViews を参照。
    private const string KeyCaptureViews = "BatchPlaybackLogger.CaptureViews";
    // 動画の時計をゲーム時間で進める（2026-10-04、-videoGameTime、撮影があるときは既定 ON）。ApplyVideoGameTime を参照。
    private const string KeyVideoGameTime = "BatchPlaybackLogger.VideoGameTime";
    // インタラクティブモーションの確認用（2026-09-25）: 動画時刻が -forceMotionAt 秒に達したら 1 回だけ強制発火し、
    // -captureMotion 秒おきにイベント中（Owned / HandoffBlend）の絵を m00000.png… で撮る。
    private const string KeyForceMotionAt = "BatchPlaybackLogger.ForceMotionAt";
    private const string KeyCaptureMotionEvery = "BatchPlaybackLogger.CaptureMotionEvery";
    // -forceMotionKind static|dynamic（既定 dynamic）と -forceStaticClip <名前の一部>（2026-10-04）: 強制発火の種類と、static のときの
    // 動物のジェスチャ（animalStaticGestureClips を名前で 1 つに絞る。絞らないと 4 つから乱数で選ばれ、新旧で同じジェスチャを比べられない）。
    private const string KeyForceMotionKind = "BatchPlaybackLogger.ForceMotionKind";
    private const string KeyForceStaticClip = "BatchPlaybackLogger.ForceStaticClip";
    // 区間を絞った毎フレーム記録（2026-10-02）: -diagWindows "285-340,895-985" の内側でだけ診断ログを出す。
    // 全編を -diagEveryN 1 で回すと Debug.Log の負荷で動画が飛ぶ（0→38）ので、見たい区間だけにする。
    // -noStackTrace true は Debug.Log のスタックトレースを切ってログ 1 行の負荷を下げる。
    private const string KeyDiagWindows = "BatchPlaybackLogger.DiagWindows";
    private const string KeyDiagEveryN = "BatchPlaybackLogger.DiagEveryN";
    private const string KeyDiagMeshParts = "BatchPlaybackLogger.DiagMeshParts";
    private const string KeyNoStackTrace = "BatchPlaybackLogger.NoStackTrace";

    public static void Run()
    {
        string scene = "Assets/Scenes/TrialScene.unity";
        double seconds = 16.0;
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "-scene") scene = args[i + 1];
            if (args[i] == "-playSeconds") double.TryParse(args[i + 1], out seconds);
        }

        float screenDistance = -1f;
        float popoutRange = -1f;
        float boneRatioTarget = -1f;
        bool diagLogs = false;
        int diagEveryN = 10;
        bool meshParts = false;
        float depthK = -1f;
        float depthSmooth = -1f;
        float depthEps = -1f;
        bool depthOff = false;
        bool penetOff = false;
        float frontBias = -1f;
        bool metricOff = false;
        // bool 3 つは「指定されなかったらシーンの値をそのまま使う」。
        // 既定値を代入してしまうと、シーン側の設定を黙って上書きしてしまう
        // （2026-08-25: boneLen の既定 true がシーンの OFF を踏み潰していた）。
        bool? aimAt = null;
        bool? armLen = null;
        bool? boneLen = null;
        float gapSmooth = -1f;
        string depthRef = null;
        // 実験（チュートリアル）の絞り込みを同じ経路で検証するため。ExperimentTrialHandoff は
        // static なので play mode 入りのドメインリロードで消える。プレイヤーの serialize 済み
        // フィールドへ直接入れる（displayTracks / animalIndex と同じやり方）。
        string onlyCategory = null;
        string animalModel = null;
        bool? otherScale = null;
        bool? bodyAlign = null;
        bool? genericBones = null;
        bool? extendH = null;
        float maxExtrap = -1f;
        float minRatio = -1f;
        float fastLo = -1f;
        float fastHi = -1f;
        bool? alignTop = null;
        bool? noBend = null;
        bool? accumBend = null;
        int? rootYaw = null;
        bool? headChain = null;
        bool? headAim = null;
        int? animalIndex = null;
        int? elseIndex = null;     // Else の既定モデル index（2026-09-18、車の絵を撮るため）
        // Human の既定モデル index（2026-10-02、モデル間比較用）。-remember false と組で使う
        // （-remember true だと model_selection.json の track 指定が優先される）。
        int? humanIndex = null;
        string diagWindows = null;
        bool noStackTrace = false;
        // Animal の動きの切り分け用（2026-10-02）: SMAL の平滑の半減期と、骨の割り当ての上書き。
        float smalHalfLife = -1f;
        string animalBoneOverride = null;
        // プレイヤーの公開フィールドを名前で上書きする（2026-10-03）。"aimAtTargetFromSmpl=true;centeredSmplRotationFilter=true"。
        // 既定 OFF の試作フラグを足すたびに引数を増やさずに済む。名前が合わなければ警告を出す（黙って無視しない）。
        string setFields = null;
        bool? elseFrameOutMotion = null;
        bool? animalFastTrack = null;
        bool? keepScaleContinuousShot = null;
        bool? headBodyMap = null;
        bool? twoAxis = null;
        bool? animAim = null;
        bool? headPose = null;
        bool? pinUi = null;
        bool? originFacing = null;
        bool? anchorLock = null;
        bool? frameSmooth = null;
        string headShift = null;
        int targetFps = 0;
        bool? remember = null;
        string bundleName = null;
        string manualYaw = null;
        string manualScale = null;
        bool openSettings = false;
        bool openPicker = false;
        int pickerPage = 0;
        string pickerTab = null;
        int seekTestFrame = -1;
        bool dumpLayout = false;
        string displayTracks = null;
        string swapModel = null;
        bool? elseChain = null;
        bool? elseFrameOut = null;
        bool? elseVerticalFrameOut = null;
        // 1 フレーム孤立の bbox 跳ね除去（rejectIsolatedBBoxSpikes）の A/B 用。
        bool? bboxSpikeFix = null;
        // インタラクティブモーションの ON/OFF（両シーンとも serialize 値は 0）。第 3 条件で動画が
        // 合計何秒止まるかを測るために足した（2026-09-25）。[MOTION] のログで集計する。
        bool? motion = null;
        string captureFrames = null;
        float forceMotionAt = -1f;
        string forceMotionKind = "dynamic";
        string forceStaticClip = string.Empty;
        float captureMotionEvery = 0f;
        string captureDir = null;
        int captureWidth = 3840;
        string captureViews = null;
        bool? videoGameTime = null;
        // tick ごとの連番撮影（2026-10-04）。BatchPlaybackLogger.Record.cs を参照。
        string recordFrames = null;
        string recordDir = null;
        int recordFps = 72;
        int recordWidth = 1280;
        string recordViews = null;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "-screenDistance") float.TryParse(args[i + 1], out screenDistance);
            if (args[i] == "-popoutRange") float.TryParse(args[i + 1], out popoutRange);
            if (args[i] == "-boneRatioTarget") float.TryParse(args[i + 1], out boneRatioTarget);
            if (args[i] == "-diagLogs") bool.TryParse(args[i + 1], out diagLogs);
            if (args[i] == "-diagEveryN") int.TryParse(args[i + 1], out diagEveryN);
            if (args[i] == "-meshParts") bool.TryParse(args[i + 1], out meshParts);
            if (args[i] == "-depthK") float.TryParse(args[i + 1], out depthK);
            if (args[i] == "-depthSmooth") float.TryParse(args[i + 1], out depthSmooth);
            if (args[i] == "-depthEps") float.TryParse(args[i + 1], out depthEps);
            if (args[i] == "-depthOff") bool.TryParse(args[i + 1], out depthOff);
            if (args[i] == "-penetOff") bool.TryParse(args[i + 1], out penetOff);
            if (args[i] == "-frontBias") float.TryParse(args[i + 1], out frontBias);
            if (args[i] == "-metricOff") bool.TryParse(args[i + 1], out metricOff);
            if (args[i] == "-aimAt" && bool.TryParse(args[i + 1], out bool vAim)) aimAt = vAim;
            if (args[i] == "-armLen" && bool.TryParse(args[i + 1], out bool vArm)) armLen = vArm;
            if (args[i] == "-boneLen" && bool.TryParse(args[i + 1], out bool vBone)) boneLen = vBone;
            if (args[i] == "-gapSmooth") float.TryParse(args[i + 1], out gapSmooth);
            if (args[i] == "-depthRef") depthRef = args[i + 1];
            if (args[i] == "-otherScale" && bool.TryParse(args[i + 1], out bool vOs)) otherScale = vOs;
            if (args[i] == "-bodyAlign" && bool.TryParse(args[i + 1], out bool vBa)) bodyAlign = vBa;
            if (args[i] == "-genericBones" && bool.TryParse(args[i + 1], out bool vGb)) genericBones = vGb;
            if (args[i] == "-extendH" && bool.TryParse(args[i + 1], out bool vEh)) extendH = vEh;
            if (args[i] == "-maxExtrap") float.TryParse(args[i + 1], out maxExtrap);
            if (args[i] == "-minRatio") float.TryParse(args[i + 1], out minRatio);
            if (args[i] == "-fastLo") float.TryParse(args[i + 1], out fastLo);
            if (args[i] == "-fastHi") float.TryParse(args[i + 1], out fastHi);
            if (args[i] == "-remember" && bool.TryParse(args[i + 1], out bool vRm)) remember = vRm;
            if (args[i] == "-animAim" && bool.TryParse(args[i + 1], out bool vAa)) animAim = vAa;
            if (args[i] == "-twoAxis" && bool.TryParse(args[i + 1], out bool vTa)) twoAxis = vTa;
            if (args[i] == "-headPose" && bool.TryParse(args[i + 1], out bool vHp)) headPose = vHp;
            if (args[i] == "-pinUi" && bool.TryParse(args[i + 1], out bool vPu)) pinUi = vPu;
            if (args[i] == "-originFacing" && bool.TryParse(args[i + 1], out bool vOf)) originFacing = vOf;
            if (args[i] == "-anchorLock" && bool.TryParse(args[i + 1], out bool vAl)) anchorLock = vAl;
            if (args[i] == "-frameSmooth" && bool.TryParse(args[i + 1], out bool vFs)) frameSmooth = vFs;
            if (args[i] == "-headShift") headShift = args[i + 1];
            if (args[i] == "-targetFps") int.TryParse(args[i + 1], out targetFps);
            if (args[i] == "-noBend" && bool.TryParse(args[i + 1], out bool vNb)) noBend = vNb;
            if (args[i] == "-accumBend" && bool.TryParse(args[i + 1], out bool vAb)) accumBend = vAb;
            if (args[i] == "-rootYaw" && int.TryParse(args[i + 1], out int vRy)) rootYaw = vRy;
            if (args[i] == "-headChain" && bool.TryParse(args[i + 1], out bool vHc)) headChain = vHc;
            if (args[i] == "-headAim" && bool.TryParse(args[i + 1], out bool vHa)) headAim = vHa;
            if (args[i] == "-animalIndex" && int.TryParse(args[i + 1], out int vAi)) animalIndex = vAi;
            if (args[i] == "-elseIndex" && int.TryParse(args[i + 1], out int vEi)) elseIndex = vEi;
            if (args[i] == "-humanIndex" && int.TryParse(args[i + 1], out int vHi)) humanIndex = vHi;
            if (args[i] == "-diagWindows") diagWindows = args[i + 1];
            if (args[i] == "-noStackTrace") bool.TryParse(args[i + 1], out noStackTrace);
            if (args[i] == "-smalHalfLife") float.TryParse(args[i + 1], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out smalHalfLife);
            if (args[i] == "-animalBoneOverride") animalBoneOverride = args[i + 1];
            if (args[i] == "-setFields") setFields = args[i + 1];
            if (args[i] == "-elseFrameOutMotion" && bool.TryParse(args[i + 1], out bool vEm)) elseFrameOutMotion = vEm;
            if (args[i] == "-animalFastTrack" && bool.TryParse(args[i + 1], out bool vAf)) animalFastTrack = vAf;
            if (args[i] == "-keepScaleContinuousShot" && bool.TryParse(args[i + 1], out bool vKs)) keepScaleContinuousShot = vKs;
            if (args[i] == "-headBodyMap" && bool.TryParse(args[i + 1], out bool vHb)) headBodyMap = vHb;
            if (args[i] == "-alignTop" && bool.TryParse(args[i + 1], out bool vAt)) alignTop = vAt;
            if (args[i] == "-bundle") bundleName = args[i + 1];
            if (args[i] == "-onlyCategory") onlyCategory = args[i + 1];
            if (args[i] == "-animalModel") animalModel = args[i + 1];
            if (args[i] == "-manualYaw") manualYaw = args[i + 1];
            if (args[i] == "-manualScale") manualScale = args[i + 1];
            if (args[i] == "-openSettings") bool.TryParse(args[i + 1], out openSettings);
            if (args[i] == "-openPicker") bool.TryParse(args[i + 1], out openPicker);
            if (args[i] == "-pickerPage") int.TryParse(args[i + 1], out pickerPage);
            if (args[i] == "-pickerTab") pickerTab = args[i + 1];
            if (args[i] == "-seekTestFrame") int.TryParse(args[i + 1], out seekTestFrame);
            if (args[i] == "-dumpPanelLayout") bool.TryParse(args[i + 1], out dumpLayout);
            // "all" で全 track 表示（displayTrackIds を空にする）。"0,1" のように ID 列も可。
            if (args[i] == "-displayTracks") displayTracks = args[i + 1];
            if (args[i] == "-swapModel") swapModel = args[i + 1];
            if (args[i] == "-elseChain" && bool.TryParse(args[i + 1], out bool vEc)) elseChain = vEc;
            if (args[i] == "-elseFrameOut" && bool.TryParse(args[i + 1], out bool vEf)) elseFrameOut = vEf;
            if (args[i] == "-elseVerticalFrameOut" && bool.TryParse(args[i + 1], out bool vEv)) elseVerticalFrameOut = vEv;
            if (args[i] == "-bboxSpikeFix" && bool.TryParse(args[i + 1], out bool vBs)) bboxSpikeFix = vBs;
            if (args[i] == "-motion" && bool.TryParse(args[i + 1], out bool vMo)) motion = vMo;
            if (args[i] == "-captureFrames") captureFrames = args[i + 1];
            if (args[i] == "-forceMotionAt") float.TryParse(args[i + 1], out forceMotionAt);
            if (args[i] == "-forceMotionKind") forceMotionKind = args[i + 1];
            if (args[i] == "-forceStaticClip") forceStaticClip = args[i + 1];
            if (args[i] == "-captureMotion") float.TryParse(args[i + 1], out captureMotionEvery);
            if (args[i] == "-captureDir") captureDir = args[i + 1];
            if (args[i] == "-captureWidth") int.TryParse(args[i + 1], out captureWidth);
            if (args[i] == "-captureViews") captureViews = args[i + 1];
            if (args[i] == "-videoGameTime" && bool.TryParse(args[i + 1], out bool vVg)) videoGameTime = vVg;
            if (args[i] == "-recordFrames") recordFrames = args[i + 1];
            if (args[i] == "-recordDir") recordDir = args[i + 1];
            if (args[i] == "-recordFps") int.TryParse(args[i + 1], out recordFps);
            if (args[i] == "-recordWidth") int.TryParse(args[i + 1], out recordWidth);
            if (args[i] == "-recordViews") recordViews = args[i + 1];
        }

        // バックグラウンド実行なので動画の音を鳴らさない（user の常設要望）。
        // Editor 側のトグルとランタイム側の両方を落とす。元の値は戻せるよう控える。
        savedAudioMute = EditorUtility.audioMasterMute;
        EditorUtility.audioMasterMute = true;
        AudioListener.volume = 0f;
        // **これだけでは動画の音が消えない。** VideoPlayer は Direct 出力なので
        // AudioListener を経由しない（RuntimePlaybackController.ApplyMute が
        // SetDirectAudioMute を呼んでいるのがその証拠）。プレイヤー側の mute を立てて、
        // Update で毎フレーム SetDirectAudioMute が掛かるようにする。
        // 保険として BatchAudioMute も batchmode 中ずっと掛け直している。
        foreach (var p in UnityEngine.Object.FindObjectsByType<StreamingStereoVideoPlayer>(
                     FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            p.mute = true;
            EditorUtility.SetDirty(p);
        }
        Debug.Log("[BATCH] audio muted (listener + VideoPlayer direct)");

        Debug.Log("[BATCH] opening scene: " + scene);
        EditorSceneManager.OpenScene(scene, OpenSceneMode.Single);

        if (screenDistance > 0f)
        {
            int applied = 0;
            foreach (var p in UnityEngine.Object.FindObjectsByType<StreamingStereoVideoPlayer>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                p.screenDistanceMeters = screenDistance;
                EditorUtility.SetDirty(p);
                applied++;
            }
            Debug.Log("[BATCH] screenDistanceMeters=" + screenDistance + " applied to " + applied);
        }

        // popout レンジは screenDistance と組で振ることが多い（比が効くため）。
        if (popoutRange >= 0f)
        {
            int applied = 0;
            foreach (var p in UnityEngine.Object.FindObjectsByType<StreamingStereoVideoPlayer>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                p.popoutRangeMeters = popoutRange;
                EditorUtility.SetDirty(p);
                applied++;
            }
            Debug.Log("[BATCH] popoutRangeMeters=" + popoutRange + " applied to " + applied);
        }

        if (depthK > 0f)
        {
            int applied = 0;
            foreach (var p in UnityEngine.Object.FindObjectsByType<StreamingStereoVideoPlayer>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                p.projectedDepthScaleK = depthK;
                EditorUtility.SetDirty(p);
                applied++;
            }
            Debug.Log("[BATCH] projectedDepthScaleK=" + depthK + " applied to " + applied);
        }

        if (depthSmooth >= 0f)
        {
            int applied = 0;
            foreach (var p in UnityEngine.Object.FindObjectsByType<StreamingStereoVideoPlayer>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                p.projectedDepthSmoothingSeconds = depthSmooth;
                EditorUtility.SetDirty(p);
                applied++;
            }
            Debug.Log("[BATCH] projectedDepthSmoothingSeconds=" + depthSmooth + " applied to " + applied);
        }

        // 以前はここに「どれか 1 つでも指定されたら」という条件式があったが、
        // 新しいフラグを足すたびに条件へ追加する必要があり、追加し忘れると
        // そのフラグは**黙って無視される**。2026-08-25 に -gapSmooth がこれで
        // 効かず、tau=0 と tau=1.2 の A/B が実際には同一設定の 2 回実行になり、
        // 「平滑化が効いていない」という誤った結論を出しかけた。
        // 条件は付けず常に走らせ、各フラグが自分で「指定されたか」を判定する。
        {
            int applied = 0;
            foreach (var p in UnityEngine.Object.FindObjectsByType<StreamingStereoVideoPlayer>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (depthEps >= 0f) { p.projectedDepthOrderEpsilonMeters = depthEps; }
                if (depthOff) { p.refineDepthFromProjectedBones = false; }
                if (penetOff) { p.resolveOtherPenetration = false; }
                if (frontBias >= 0f) { p.penetrationFrontBias = frontBias; }
                if (metricOff) { p.useMetricRatioForOtherDepth = false; }
                if (aimAt.HasValue) { p.enableKeypointAimAt = aimAt.Value; }
                if (armLen.HasValue) { p.enableHumanArmLengthCorrection = armLen.Value; }
                if (boneLen.HasValue) { p.enableHumanBoneLengthCorrection = boneLen.Value; }
                if (gapSmooth >= 0f) { p.otherDepthGapSmoothingSeconds = gapSmooth; }
                if (!string.IsNullOrEmpty(depthRef) &&
                    Enum.TryParse(depthRef, true, out StreamingStereoVideoPlayer.HumanDepthReferenceMode refMode))
                {
                    p.otherDepthSkeletonReference = refMode;
                }
                if (otherScale.HasValue) { p.matchOtherScaleToFollowedDepth = otherScale.Value; }
                if (bodyAlign.HasValue) { p.alignModelBodyToAnchorDepth = bodyAlign.Value; }
                if (genericBones.HasValue) { p.projectGenericRigBones = genericBones.Value; }
                if (extendH.HasValue) { p.extendTargetHeightForClippedBBox = extendH.Value; }
                if (maxExtrap > 0f) { p.maxClippedHeightExtrapolation = maxExtrap; }
                if (minRatio > 0f) { p.depthRefineMinRatio = minRatio; }
                if (fastLo >= 0f) { p.depthRefineFastTrackLow = fastLo; }
                if (fastHi >= 0f) { p.depthRefineFastTrackHigh = fastHi; }
                if (alignTop.HasValue) { p.alignTopWhenBottomClipped = alignTop.Value; }
                if (noBend.HasValue) { p.SetSmalBendDisabledForDiag(noBend.Value); }
                if (accumBend.HasValue) { p.SetAccumulateSmalParentBend(accumBend.Value); }
                if (rootYaw.HasValue) { p.SetForceRootYawFix(rootYaw.Value); }
                if (headChain.HasValue) { p.SetExcludeHeadFromChain(!headChain.Value); }
                if (headAim.HasValue) { p.SetHeadAimFromModelForward(headAim.Value); }
                if (animalIndex.HasValue) { p.selectedAnimalIndex = animalIndex.Value; }
                if (elseIndex.HasValue) { p.selectedElseIndex = elseIndex.Value; }
                if (humanIndex.HasValue) { p.selectedHumanIndex = humanIndex.Value; }
                if (smalHalfLife >= 0f) { p.smalSmoothHalfLifeSec = smalHalfLife; }
                if (!string.IsNullOrEmpty(animalBoneOverride)) { p.batchAnimalBoneOverrideSpec = animalBoneOverride; }
                if (elseFrameOutMotion.HasValue) { p.elseFrameOutFollowMotion = elseFrameOutMotion.Value; }
                if (animalFastTrack.HasValue) { p.depthRefineFastTrackForAnimal = animalFastTrack.Value; }
                if (keepScaleContinuousShot.HasValue) { p.keepScaleAcrossContinuousShotBoundary = keepScaleContinuousShot.Value; }
                if (headBodyMap.HasValue) { p.SetHeadUseBodyFrameMap(headBodyMap.Value); }
                if (twoAxis.HasValue) { p.SetTwoAxisJointFrameMap(twoAxis.Value); }
                if (headPose.HasValue) { p.SetAnimalHeadPose(headPose.Value); }
                if (pinUi.HasValue) { p.SetPinRuntimeUiDistance(pinUi.Value); }
                if (originFacing.HasValue) { p.SetUseTrackingOriginForScreenFacing(originFacing.Value); }
                if (anchorLock.HasValue) { p.SetLockScreenAnchorPosition(anchorLock.Value); }
                if (frameSmooth.HasValue) { p.SetSmoothDepthPerVideoFrame(frameSmooth.Value); }
                if (animAim.HasValue) { p.SetAnimalKeypointAimAt(animAim.Value); }
                if (elseChain.HasValue) { p.enableElseChainPlacement = elseChain.Value; }
                if (elseFrameOut.HasValue) { p.enableElseFrameOutContinuation = elseFrameOut.Value; }
                if (elseVerticalFrameOut.HasValue) { p.enableElseVerticalFrameOutContinuation = elseVerticalFrameOut.Value; }
                if (bboxSpikeFix.HasValue) { p.rejectIsolatedBBoxSpikes = bboxSpikeFix.Value; }
                if (motion.HasValue) { p.enableInteractiveMotion = motion.Value; }
                // バッチは測定環境なので、明示的に -remember true と言われない限り OFF。
                // persistentDataPath に保存済みの選択が残っていると A/B が静かに汚れる。
                p.rememberTrackCustomization = remember.HasValue && remember.Value;
                EditorUtility.SetDirty(p);
                applied++;
            }
            Debug.Log("[BATCH] tweaks applied to " + applied
                + " | depthEps=" + depthEps + " depthOff=" + depthOff + " penetOff=" + penetOff
                + " frontBias=" + frontBias + " metricOff=" + metricOff
                + " aimAt=" + (aimAt.HasValue ? aimAt.Value.ToString() : "scene")
                + " armLen=" + (armLen.HasValue ? armLen.Value.ToString() : "scene")
                + " boneLen=" + (boneLen.HasValue ? boneLen.Value.ToString() : "scene")
                + " gapSmooth=" + gapSmooth + " depthRef=" + (depthRef ?? "scene")
                + " otherScale=" + (otherScale.HasValue ? otherScale.Value.ToString() : "scene")
                + " bodyAlign=" + (bodyAlign.HasValue ? bodyAlign.Value.ToString() : "scene")
                + " genericBones=" + (genericBones.HasValue ? genericBones.Value.ToString() : "scene")
                + " extendH=" + (extendH.HasValue ? extendH.Value.ToString() : "scene") + " maxExtrap=" + maxExtrap + " minRatio=" + minRatio + " fastLo=" + fastLo + " fastHi=" + fastHi + " alignTop=" + (alignTop.HasValue ? alignTop.Value.ToString() : "scene") + " noBend=" + (noBend.HasValue ? noBend.Value.ToString() : "scene") + " twoAxis=" + (twoAxis.HasValue ? twoAxis.Value.ToString() : "scene") + " animAim=" + (animAim.HasValue ? animAim.Value.ToString() : "scene") + " elseChain=" + (elseChain.HasValue ? elseChain.Value.ToString() : "scene") + " elseFrameOut=" + (elseFrameOut.HasValue ? elseFrameOut.Value.ToString() : "scene") + " elseVerticalFrameOut=" + (elseVerticalFrameOut.HasValue ? elseVerticalFrameOut.Value.ToString() : "scene") + " remember=" + (remember.HasValue ? remember.Value.ToString() : "False(batch既定)")
                + " motion=" + (motion.HasValue ? motion.Value.ToString() : "scene"));
        }

        if (!string.IsNullOrEmpty(setFields))
        {
            foreach (var p in UnityEngine.Object.FindObjectsByType<StreamingStereoVideoPlayer>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                ApplySetFields(p, setFields);
                EditorUtility.SetDirty(p);
            }
        }

        // 検証用 bundle を差し替える（シーンには保存しない）。
        if (!string.IsNullOrEmpty(bundleName))
        {
            int applied = 0;
            foreach (var p in UnityEngine.Object.FindObjectsByType<StreamingStereoVideoPlayer>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                p.bundleFileName = bundleName;
                EditorUtility.SetDirty(p);
                applied++;
            }
            Debug.Log("[BATCH] bundleFileName=" + bundleName + " applied to " + applied);
        }

        // 実験の絞り込み（category）と animal の既定モデル名をこの実行の間だけ入れる。
        if (!string.IsNullOrEmpty(onlyCategory) || !string.IsNullOrEmpty(animalModel))
        {
            int applied = 0;
            foreach (var p in UnityEngine.Object.FindObjectsByType<StreamingStereoVideoPlayer>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (!string.IsNullOrEmpty(onlyCategory)) { p.experimentOnlyCategory = onlyCategory; }
                if (!string.IsNullOrEmpty(animalModel)) { p.experimentPreferredAnimalModelName = animalModel; }
                EditorUtility.SetDirty(p);
                applied++;
            }
            Debug.Log("[BATCH] onlyCategory=" + (onlyCategory ?? "(all)") + " animalModel=" + (animalModel ?? "(scene)") + " applied to " + applied);
        }

        // 表示 track の絞り込みをこの実行の間だけ差し替える（シーンには保存しない）。
        if (!string.IsNullOrEmpty(displayTracks))
        {
            int[] ids;
            if (displayTracks.Trim().ToLowerInvariant() == "all")
            {
                ids = new int[0];
            }
            else
            {
                var list = new List<int>();
                foreach (string part in displayTracks.Split(','))
                {
                    if (int.TryParse(part.Trim(), out int id)) { list.Add(id); }
                }
                ids = list.ToArray();
            }

            int applied = 0;
            foreach (var p in UnityEngine.Object.FindObjectsByType<StreamingStereoVideoPlayer>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                p.displayTrackIds = ids;
                EditorUtility.SetDirty(p);
                applied++;
            }
            Debug.Log("[BATCH] displayTracks=" + displayTracks + " count=" + ids.Length + " applied to " + applied);
        }

        // 手動 yaw / 手動スケールの注入（実機の VR UI 操作を Editor で代替する）。
        if (!string.IsNullOrEmpty(manualYaw) || !string.IsNullOrEmpty(manualScale) ||
            !string.IsNullOrEmpty(swapModel) || openSettings || openPicker || dumpLayout || seekTestFrame >= 0)
        {
            int applied = 0;
            foreach (var p in UnityEngine.Object.FindObjectsByType<StreamingStereoVideoPlayer>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (!string.IsNullOrEmpty(manualYaw)) { p.batchManualYawSpec = manualYaw; }
                if (!string.IsNullOrEmpty(manualScale)) { p.batchManualScaleSpec = manualScale; }
                if (openSettings) { p.batchOpenSettingsOnStart = true; }
                if (seekTestFrame >= 0) { p.batchSeekTestFrame = seekTestFrame; }
                if (dumpLayout) { p.batchDumpPanelLayout = true; }
                if (openPicker)
                {
                    p.batchOpenModelPickerOnStart = true;
                    p.batchModelPickerPage = pickerPage;
                    p.batchModelPickerTab = pickerTab;
                }
                if (!string.IsNullOrEmpty(swapModel)) { p.batchSwapModelSpec = swapModel; }
                EditorUtility.SetDirty(p);
                applied++;
            }
            Debug.Log("[BATCH] manualYaw=" + manualYaw + " manualScale=" + manualScale +
                      " swapModel=" + swapModel + " openSettings=" + openSettings + " applied to " + applied);
        }

        // 診断ログはシーンに保存せず、この実行の間だけ有効にする。
        if (boneRatioTarget > 0f || diagLogs)
        {
            int n = 0;
            foreach (var p in UnityEngine.Object.FindObjectsByType<StreamingStereoVideoPlayer>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (boneRatioTarget > 0f) { p.projectedBoneRatioTarget = boneRatioTarget; }
                if (diagLogs)
                {
                    // -diagWindows があるときは区間に入るまで切っておく（Tick の ApplyDiagWindows が開閉する）。
                    SetDiagFlags(p, string.IsNullOrEmpty(diagWindows), Mathf.Max(1, diagEveryN), meshParts);
                }
                EditorUtility.SetDirty(p);
                n++;
            }
            Debug.Log($"[BATCH] boneRatioTarget={boneRatioTarget} diagLogs={diagLogs} applied to {n}");
        }

        SessionState.SetString(KeyDiagWindows, diagLogs ? (diagWindows ?? string.Empty) : string.Empty);
        SessionState.SetInt(KeyDiagEveryN, Mathf.Max(1, diagEveryN));
        SessionState.SetBool(KeyDiagMeshParts, meshParts);
        SessionState.SetBool(KeyNoStackTrace, noStackTrace);
        if (!string.IsNullOrEmpty(diagWindows) || noStackTrace)
        {
            Debug.Log($"[BATCH] diagWindows='{diagWindows}' noStackTrace={noStackTrace} humanIndex={(humanIndex.HasValue ? humanIndex.Value.ToString() : "scene")}");
        }
        if (smalHalfLife >= 0f || !string.IsNullOrEmpty(animalBoneOverride))
        {
            Debug.Log($"[BATCH] smalHalfLife={(smalHalfLife >= 0f ? smalHalfLife.ToString(System.Globalization.CultureInfo.InvariantCulture) : "scene")} animalBoneOverride='{animalBoneOverride}'");
        }
        SessionState.SetString(KeyCaptureFrames, captureFrames ?? string.Empty);
        SessionState.SetString(KeyHeadShift, headShift ?? string.Empty);
        SessionState.SetInt(KeyTargetFps, targetFps);
        SessionState.SetString(KeyCaptureDir, captureDir ?? string.Empty);
        SessionState.SetInt(KeyCaptureWidth, captureWidth);
        SessionState.SetString(KeyCaptureViews, captureViews ?? string.Empty);
        SessionState.SetBool(KeyVideoGameTime, videoGameTime ?? (!string.IsNullOrEmpty(captureFrames) || !string.IsNullOrEmpty(recordFrames)));
        StoreRecordArgs(recordFrames, recordDir, recordFps, recordWidth, recordViews);
        SessionState.SetFloat(KeyForceMotionAt, forceMotionAt);
        SessionState.SetString(KeyForceMotionKind, forceMotionKind);
        SessionState.SetString(KeyForceStaticClip, forceStaticClip);
        SessionState.SetFloat(KeyCaptureMotionEvery, captureMotionEvery);
        SessionState.SetBool(KeyRunning, true);
        SessionState.SetBool(KeyStarted, false);
        SessionState.SetFloat(KeyDeadline, (float)seconds);
        EditorApplication.update += Tick;
        Debug.Log("[BATCH] entering playmode, playSeconds=" + seconds);
        EditorApplication.EnterPlaymode();
    }

    [InitializeOnLoadMethod]
    private static void Reattach()
    {
        if (SessionState.GetBool(KeyRunning, false))
        {
            EditorApplication.update += Tick;
        }
    }

    private static double startedAt;
    private static bool savedAudioMute;

    private static readonly HashSet<long> captured = new HashSet<long>();
    private static int captureDiagTicks;
    private static bool headShiftApplied;

    // VideoPlayer.frame を監視し、指定フレームに達したらカメラの絵を PNG で保存する。
    // -nographics を付けていないので通常どおりレンダリングでき、目視比較に使える。
    // **バッチの Update 回数を実機に合わせる。**
    //
    // ⑧ は毎 Update に「今の投影から ratio を再計算して深度を寄せる」不動点反復で、
    // fast track の発火条件も **1 tick 前との相対誤差**で決まる。つまり
    // **Update の回数が変われば挙動が変わる。**
    // 実測（2026-09-09）: バッチは約 465fps（1 動画フレームあたり 15.5 tick）、
    // 実機は 72Hz（同 2.4 tick）で **6 倍違った**。この差のせいで
    // バッチで詰めた fastLo の調整が実機で効かなかった。
    //
    // `-targetFps 72` で実機に揃える。**揃ったかは必ずログの tick 数で検算すること。**
    private static void ApplyTargetFrameRate()
    {
        int fps = SessionState.GetInt(KeyTargetFps, 0);
        if (fps <= 0)
        {
            return;
        }

        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = fps;
        Debug.Log($"[BATCH] targetFrameRate={fps}（vSyncCount=0）");
    }


    private static void TryCaptureFrames()
    {
        string spec = SessionState.GetString(KeyCaptureFrames, string.Empty);
        string dir = SessionState.GetString(KeyCaptureDir, string.Empty);
        if (string.IsNullOrEmpty(spec) || string.IsNullOrEmpty(dir)) { return; }

        var vp = UnityEngine.Object.FindFirstObjectByType<UnityEngine.Video.VideoPlayer>();
        if (vp == null)
        {
            if (captureDiagTicks++ % 120 == 0) { Debug.Log("[CAPDIAG] VideoPlayer not found"); }
            return;
        }
        long cur = vp.frame;
        if (captureDiagTicks++ % 120 == 0)
        {
            Debug.Log($"[CAPDIAG] vp.frame={cur} isPlaying={vp.isPlaying} prepared={vp.isPrepared} spec='{spec}'");
        }
        if (cur < 0) { return; }

        // spec は "150,320"（個別）と "110-360"／"110-360:2"（範囲・間引き）を混在できる。
        // 範囲指定では VideoPlayer.frame が飛ぶことがあるので、到達した実フレームを撮る。
        long want = -1;
        foreach (string raw in spec.Split(','))
        {
            string part = raw.Trim();
            if (part.Length == 0) { continue; }

            int dash = part.IndexOf('-');
            if (dash > 0)
            {
                string range = part;
                long step = 1;
                int colon = part.IndexOf(':');
                if (colon > dash)
                {
                    range = part.Substring(0, colon);
                    if (!long.TryParse(part.Substring(colon + 1), out step) || step < 1) { step = 1; }
                }
                dash = range.IndexOf('-');
                if (!long.TryParse(range.Substring(0, dash), out long from)) { continue; }
                if (!long.TryParse(range.Substring(dash + 1), out long to)) { continue; }
                if (cur < from || cur > to) { continue; }
                if ((cur - from) % step != 0) { continue; }
                if (captured.Contains(cur)) { continue; }
                want = cur;
                break;
            }

            if (!long.TryParse(part, out long single)) { continue; }
            if (captured.Contains(single)) { continue; }
            if (cur < single) { continue; }
            want = single;
            break;
        }

        if (want < 0)
        {
            if (captureDiagTicks % 120 == 1) { Debug.Log($"[CAPDIAG] no match for cur={cur}"); }
            return;
        }

        // -headShift: 「首を振ってから Screen Dist を触る」を再現してから撮る。
        // スライダーと同じ経路で置き直すので、基準点を固定していなければ
        // 画面とモデルが視点に付いてきて、固定していれば残る。
        if (!headShiftApplied)
        {
            string shiftSpec = SessionState.GetString(KeyHeadShift, string.Empty);
            string[] xyz = string.IsNullOrEmpty(shiftSpec) ? null : shiftSpec.Split(',');
            if (xyz != null && xyz.Length == 3 &&
                float.TryParse(xyz[0], out float sx) &&
                float.TryParse(xyz[1], out float sy) &&
                float.TryParse(xyz[2], out float sz))
            {
                var shifted = UnityEngine.Object.FindFirstObjectByType<StreamingStereoVideoPlayer>();
                if (shifted != null)
                {
                    shifted.BatchShiftViewerAndReplaceScreens(new Vector3(sx, sy, sz));
                    headShiftApplied = true;
                    Debug.Log($"[CAPDIAG] headShift 適用 ({sx},{sy},{sz}) at frame={want}");
                }
            }
            else
            {
                headShiftApplied = true;
            }
        }

        {
            // XR ランタイムが無いバッチ環境では XR Rig 配下のカメラが inactive のままになる。
            // Camera.Render() は GameObject が非アクティブでも手動で呼べるので、
            // inactive も含めて探し、見つかったものをそのまま使う。
            Camera cam = Camera.main;
            if (cam == null) { cam = UnityEngine.Object.FindFirstObjectByType<Camera>(); }
            if (cam == null)
            {
                var all = UnityEngine.Object.FindObjectsByType<Camera>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None);
                if (all.Length > 0) { cam = all[0]; }
                if (cam == null)
                {
                    if (captureDiagTicks % 120 == 1) { Debug.Log("[CAPDIAG] no camera at all"); }
                    return;
                }
                if (captureDiagTicks % 120 == 1)
                {
                    Debug.Log($"[CAPDIAG] using inactive camera '{cam.name}' (found {all.Length})");
                }
            }

            // 目視比較に使うので高解像度で撮る。スクリーンはカメラ視野の一部にしか
            // 映らないため、この解像度でも切り出すと 1000px 程度にしかならない。
            // 連番で撮るときは -captureWidth 1920 などに落とさないと容量と時間が嵩む。
            //
            // 撮っている間は動画を止める（2026-09-17）。止めないと撮影の後に VideoPlayer が
            // 1〜2 秒ぶん飛び（car f236 で撮ったら次に読めた frame が 289）、
            // "236-262:2" のような連番指定が 1 枚しか撮れない。
            bool resumeVideo = vp.isPlaying;
            if (resumeVideo) { vp.Pause(); }
            int W = Mathf.Clamp(SessionState.GetInt(KeyCaptureWidth, 3840), 640, 3840);
            int H = Mathf.RoundToInt(W * 9f / 16f);
            var rt = new RenderTexture(W, H, 24) { antiAliasing = 8 };
            RenderTexture prevTarget = cam.targetTexture;
            RenderTexture prevActive = RenderTexture.active;
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(W, H, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
            tex.Apply();
            cam.targetTexture = prevTarget;
            RenderTexture.active = prevActive;

            Directory.CreateDirectory(dir);
            // ffmpeg で連番として扱えるようゼロ埋めする。
            string path = Path.Combine(dir, $"f{want:D5}.png");
            File.WriteAllBytes(path, tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);
            rt.Release();
            UnityEngine.Object.DestroyImmediate(rt);
            captured.Add(want);
            CaptureAuxViews(cam, dir, want);
            if (resumeVideo) { vp.Play(); }
            Debug.Log($"[CAPTURE] frame={want} (vp={cur}) -> {path}");
        }
    }

    // -captureViews "Hips:2.2:90:25;LeftHand:0.35:0:30"（2026-10-04）: 撮るたびに、骨を注視する補助カメラで追加の絵を撮る。
    // 正面（メインカメラ）の絵だけでは、胴の前後の傾きや手指・肉球が見えないので足した。
    // 各指定 = 骨:距離:方位:fov[:仰角]
    //   骨   = HumanBodyBones 名（最初に見つかった Humanoid）か Transform 名。"モデル名の一部/骨" でモデルを絞れる
    //          （Animator かインスタンスの root の名前で絞る。animal の root は Track_0 のような名前）
    //   距離 = モデルの大きさ（Renderer の bounds の最大辺）の倍数
    //   方位 = 「骨 → メインカメラ」の水平方向を world の上向きまわりに回す角度（0 = メインカメラ側、+90 = 視聴者から見て左側）
    //   fov  = 縦の画角（度）、仰角 = 上から見下ろす角度（度、省略 0）
    // 出力は f00605_Hips_90.png のように、メインの絵と同じフォルダへ。
    private static void CaptureAuxViews(Camera mainCam, string dir, long frame)
    {
        string spec = SessionState.GetString(KeyCaptureViews, string.Empty);
        if (string.IsNullOrEmpty(spec) || mainCam == null) { return; }

        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var style = System.Globalization.NumberStyles.Float;
        foreach (string raw in spec.Split(';'))
        {
            string[] p = raw.Trim().Split(':');
            // 方位に "front" を書くと、メインカメラではなく体の前後軸（骨 → 同じ root の head の水平方向）を基準に、頭の側から見る。
            bool bodyFront = p.Length >= 3 && p[2] == "front";
            float azimuth = 0f;
            if (p.Length < 4 ||
                !float.TryParse(p[1], style, inv, out float distFactor) ||
                (!bodyFront && !float.TryParse(p[2], style, inv, out azimuth)) ||
                !float.TryParse(p[3], style, inv, out float fov))
            {
                continue;
            }

            float elevation = 0f;
            if (p.Length >= 5) { float.TryParse(p[4], style, inv, out elevation); }
            if (!TryResolveAuxViewTarget(p[0], out Transform target, out float modelSize))
            {
                Debug.Log($"[CAPVIEW] '{p[0]}' が見つからない（frame={frame}）");
                continue;
            }

            Vector3 toCam = mainCam.transform.position - target.position;
            toCam.y = 0f;
            if (toCam.sqrMagnitude < 1e-8f)
            {
                toCam = -mainCam.transform.forward;
                toCam.y = 0f;
            }

            Vector3 dirH = Quaternion.AngleAxis(azimuth, Vector3.up) * toCam.normalized;
            if (bodyFront && TryFindSiblingBone(target, "head", out Transform headBone))
            {
                Vector3 fwd = headBone.position - target.position;
                fwd.y = 0f;
                if (fwd.sqrMagnitude > 1e-8f)
                {
                    dirH = fwd.normalized;
                }
            }
            float el = elevation * Mathf.Deg2Rad;
            Vector3 viewDir = (dirH * Mathf.Cos(el) + Vector3.up * Mathf.Sin(el)).normalized;
            float dist = Mathf.Max(0.01f, distFactor * modelSize);

            // メインカメラのリグ（XR Origin）配下の表示（コントローラの輪など）が寄りの絵に写り込むので、
            // 撮る間だけ隠す。対象のモデルがリグ配下にあるときは隠さない。
            var hidden = new List<Renderer>();
            Transform rig = mainCam.transform.root;
            if (rig != null && !target.IsChildOf(rig))
            {
                foreach (Renderer r in rig.GetComponentsInChildren<Renderer>())
                {
                    if (r.enabled) { r.enabled = false; hidden.Add(r); }
                }
            }

            var go = new GameObject("CapViewCamera");
            try
            {
                Camera cam = go.AddComponent<Camera>();
                cam.CopyFrom(mainCam);
                cam.enabled = false;
                cam.stereoTargetEye = StereoTargetEyeMask.None;
                cam.transform.SetPositionAndRotation(
                    target.position + viewDir * dist,
                    Quaternion.LookRotation(-viewDir, Vector3.up));
                cam.fieldOfView = fov;
                cam.nearClipPlane = Mathf.Max(0.001f, dist * 0.05f);
                cam.farClipPlane = 100f;

                const int W = 1280;
                const int H = 960;
                var rt = new RenderTexture(W, H, 24) { antiAliasing = 8 };
                RenderTexture prevActive = RenderTexture.active;
                cam.targetTexture = rt;
                cam.Render();
                RenderTexture.active = rt;
                var tex = new Texture2D(W, H, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
                tex.Apply();
                cam.targetTexture = null;
                RenderTexture.active = prevActive;
                string label = p[0].Replace('/', '-');
                string path = Path.Combine(dir, $"f{frame:D5}_{label}_{(bodyFront ? "front" : Mathf.RoundToInt(azimuth).ToString())}.png");
                File.WriteAllBytes(path, tex.EncodeToPNG());
                UnityEngine.Object.DestroyImmediate(tex);
                rt.Release();
                UnityEngine.Object.DestroyImmediate(rt);
                Debug.Log($"[CAPVIEW] frame={frame} target={target.name} dist={dist:F3}m az={azimuth} el={elevation} fov={fov} " +
                          $"hidden={hidden.Count} under '{(rig != null ? rig.name : "-")}' -> {path}");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
                foreach (Renderer r in hidden)
                {
                    if (r != null) { r.enabled = true; }
                }
            }
        }
    }

    // target と同じインスタンス（最上位の Track_N か Animator の root）の中から名前の一致する骨を探す。
    private static bool TryFindSiblingBone(Transform target, string name, out Transform bone)
    {
        bone = null;
        Transform root = target;
        while (root.parent != null && !root.name.StartsWith("Track_", StringComparison.Ordinal))
        {
            root = root.parent;
        }

        foreach (Transform t in root.GetComponentsInChildren<Transform>())
        {
            if (t.name == name) { bone = t; return true; }
        }

        return false;
    }

    private static bool TryResolveAuxViewTarget(string spec, out Transform target, out float modelSize)
    {
        target = null;
        modelSize = 1f;
        string filter = null;
        string boneName = spec;
        int slash = spec.IndexOf('/');
        if (slash > 0)
        {
            filter = spec.Substring(0, slash);
            boneName = spec.Substring(slash + 1);
        }

        // 探す root の候補: 絞り込みがあれば、その名前の GameObject（animal のインスタンスは Animator を持たず
        // root が Track_0 のような名前なので、Animator だけを探すと見つからない）と、名前が一致する Animator。
        var roots = new List<Transform>();
        if (filter != null)
        {
            GameObject named = GameObject.Find(filter);
            if (named != null) { roots.Add(named.transform); }
        }

        foreach (Animator animator in UnityEngine.Object.FindObjectsByType<Animator>(FindObjectsSortMode.None))
        {
            if (filter == null ||
                animator.name.IndexOf(filter, StringComparison.Ordinal) >= 0 ||
                animator.transform.root.name.IndexOf(filter, StringComparison.Ordinal) >= 0)
            {
                roots.Add(animator.transform);
            }
        }

        bool isHumanBone = Enum.TryParse(boneName, out HumanBodyBones humanBone) && humanBone != HumanBodyBones.LastBone;
        foreach (Transform root in roots)
        {
            Transform found = null;
            Animator animator = root.GetComponentInChildren<Animator>();
            if (isHumanBone && animator != null && animator.isHuman)
            {
                found = animator.GetBoneTransform(humanBone);
            }

            if (found == null)
            {
                foreach (Transform t in root.GetComponentsInChildren<Transform>())
                {
                    if (t.name == boneName) { found = t; break; }
                }
            }

            if (found == null) { continue; }

            bool hasBounds = false;
            Bounds b = default(Bounds);
            foreach (Renderer r in root.GetComponentsInChildren<Renderer>())
            {
                if (!r.enabled) { continue; }
                if (!hasBounds) { b = r.bounds; hasBounds = true; }
                else { b.Encapsulate(r.bounds); }
            }

            target = found;
            modelSize = hasBounds ? Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z)) : 1f;
            return true;
        }

        return false;
    }

    private static bool forcedMotion;
    private static double nextMotionCaptureAt;
    private static int motionCaptureCount;

    // -forceMotionAt: 動画時刻が指定秒に達したら 1 回だけ Dynamic を強制発火（Random の抽選を待たない）。
    // -captureMotion: イベント中は指定秒おきにカメラの絵を m00000.png… で撮る（動画は止まっているので
    // フレーム番号では区別できない）。連番を ffmpeg で並べれば動きを確認できる。
    private static void TryForceMotionAndCaptureMotion()
    {
        float forceAt = SessionState.GetFloat(KeyForceMotionAt, -1f);
        float every = SessionState.GetFloat(KeyCaptureMotionEvery, 0f);
        if (forceAt < 0f && every <= 0f) { return; }

        var player = UnityEngine.Object.FindFirstObjectByType<StreamingStereoVideoPlayer>();
        var vp = UnityEngine.Object.FindFirstObjectByType<UnityEngine.Video.VideoPlayer>();
        if (player == null || vp == null) { return; }

        if (forceAt >= 0f && !forcedMotion && vp.isPlaying && vp.time >= forceAt)
        {
            forcedMotion = true;
            bool dynamicKind = SessionState.GetString(KeyForceMotionKind, "dynamic") != "static";
            string clipFilter = SessionState.GetString(KeyForceStaticClip, string.Empty);
            if (!dynamicKind && !string.IsNullOrEmpty(clipFilter) && player.animalStaticGestureClips != null)
            {
                var kept = new List<AnimalGesturePose>();
                foreach (AnimalGesturePose clip in player.animalStaticGestureClips)
                {
                    if (clip != null && clip.name.IndexOf(clipFilter, StringComparison.Ordinal) >= 0) { kept.Add(clip); }
                }

                if (kept.Count > 0)
                {
                    player.animalStaticGestureClips = kept.ToArray();
                }
                else
                {
                    Debug.LogWarning($"[BATCH] -forceStaticClip '{clipFilter}' に合うジェスチャが無いので絞らない");
                }
            }

            player.DebugForceInteractiveMotion(dynamicKind);
            Debug.Log($"[BATCH] forceMotionAt {forceAt}s → DebugForceInteractiveMotion({(dynamicKind ? "dynamic" : "static")}) at videoTime={vp.time:F2}" +
                (dynamicKind ? string.Empty : $" clips={string.Join(",", Array.ConvertAll(player.animalStaticGestureClips ?? new AnimalGesturePose[0], c => c != null ? c.name : "null"))}"));
        }

        if (every <= 0f || !player.IsAnyInteractiveMotionActive()) { return; }
        double now = EditorApplication.timeSinceStartup;
        if (now < nextMotionCaptureAt) { return; }
        nextMotionCaptureAt = now + every;

        string dir = SessionState.GetString(KeyCaptureDir, string.Empty);
        if (string.IsNullOrEmpty(dir)) { return; }
        Camera cam = Camera.main;
        if (cam == null) { cam = UnityEngine.Object.FindFirstObjectByType<Camera>(); }
        if (cam == null)
        {
            var all = UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            if (all.Length > 0) { cam = all[0]; }
        }
        if (cam == null) { return; }

        int W = Mathf.Clamp(SessionState.GetInt(KeyCaptureWidth, 3840), 640, 3840);
        int H = Mathf.RoundToInt(W * 9f / 16f);
        var rt = new RenderTexture(W, H, 24) { antiAliasing = 8 };
        RenderTexture prevTarget = cam.targetTexture;
        RenderTexture prevActive = RenderTexture.active;
        cam.targetTexture = rt;
        cam.Render();
        RenderTexture.active = rt;
        var tex = new Texture2D(W, H, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
        tex.Apply();
        cam.targetTexture = prevTarget;
        RenderTexture.active = prevActive;
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, $"m{motionCaptureCount:D5}.png");
        File.WriteAllBytes(path, tex.EncodeToPNG());
        motionCaptureCount++;
        UnityEngine.Object.DestroyImmediate(tex);
        rt.Release();
        UnityEngine.Object.DestroyImmediate(rt);
    }

    // -setFields "name=value;name=value" をプレイヤーの公開フィールドへ書く（bool / int / float / string / enum）。
    private static void ApplySetFields(StreamingStereoVideoPlayer p, string spec)
    {
        foreach (string pair in spec.Split(';'))
        {
            int eq = pair.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            string key = pair.Substring(0, eq).Trim();
            string value = pair.Substring(eq + 1).Trim();
            System.Reflection.FieldInfo field = typeof(StreamingStereoVideoPlayer).GetField(
                key, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (field == null)
            {
                Debug.LogWarning($"[BATCH] setFields: 不明なフィールド '{key}'（無視しない: 綴りを確認すること）");
                continue;
            }

            object parsed = null;
            System.Type t = field.FieldType;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            if (t == typeof(bool) && bool.TryParse(value, out bool b)) parsed = b;
            else if (t == typeof(int) && int.TryParse(value, System.Globalization.NumberStyles.Integer, inv, out int iv)) parsed = iv;
            else if (t == typeof(float) && float.TryParse(value, System.Globalization.NumberStyles.Float, inv, out float fv)) parsed = fv;
            else if (t == typeof(string)) parsed = value;
            else if (t.IsEnum)
            {
                try { parsed = Enum.Parse(t, value, true); } catch { parsed = null; }
            }

            if (parsed == null)
            {
                Debug.LogWarning($"[BATCH] setFields: '{key}' に '{value}' を書けない（型 {t.Name}）");
                continue;
            }

            field.SetValue(p, parsed);
            Debug.Log($"[BATCH] setFields {key}={parsed}");
        }
    }

    // -diagLogs が立てる診断フラグ一式。区間の開閉（ApplyDiagWindows）も同じ集合を切り替える。
    private static void SetDiagFlags(StreamingStereoVideoPlayer p, bool on, int every, bool meshParts)
    {
        p.logPlacementMeasurement = on;
        p.logPlacementMeasurementEveryNFrames = every;
        // 頂点投影（[MESH2D]）。[PLACE] と同じ間隔でだけ走る。
        p.logMeshProjection = on;
        p.logMeshProjectionParts = on && meshParts;
        p.logHumanOtherGap = on;
        p.logHumanOtherGapEveryNFrames = every;
        p.logDepthRefineStages = on;
        p.logPenetrationResolve = on;
        p.logDepthAffineFit = on;
        p.logOtherDepthFollow = on;
        p.logBodyAnchorAlign = on;
        p.logHorizontalPlacement = on;
        p.logElseChainPlacement = on;
        p.logAnimalBoneVsKeypoint = on;
        p.logOtherDepthFollowEveryNFrames = every;
        p.logBoneVsKeypoint = on;
        p.logBoneVsKeypointEveryNFrames = every;
        // [POSE] = 表示中のモデルの骨を投影して元映像の keypoint と比べる。
        // 左右が入れ替わっていないかは、これの dx の符号で判る。
        p.logHumanPoseError = on;
        p.logHumanPoseErrorEveryNFrames = every;
    }

    private static bool videoGameTimeLogged;

    // -videoGameTime（2026-10-04、撮影があるときは既定 ON）: 動画の時計を実時間（UnscaledGameTime）からゲーム時間へ替え、
    // 1 tick で進む時間の上限を 1 動画フレーム（1/30 秒）にする。
    // 撮影は 1 枚（補助カメラ込みで 5 枚なら約 1.3 秒）止まるので、実時間の時計だとその分だけ動画が先へ飛び
    // （2026-10-04: f605 を撮った直後の f620 の指定が vp=645 で撮られ、ファイル名は f00620 のままだった）、
    // 次の指定フレームを取り逃がすか、別のフレームを指定の名前で保存してしまう。ゲーム時間なら止まっている間は進まない。
    // 実機は実時間の時計のまま（RuntimePlaybackController.ConfigureForApiPlayback）。バッチの撮影の再現性のためだけの切り替え。
    private static void ApplyVideoGameTime()
    {
        if (!SessionState.GetBool(KeyVideoGameTime, false)) { return; }

        var vp = UnityEngine.Object.FindFirstObjectByType<UnityEngine.Video.VideoPlayer>();
        if (vp == null) { return; }

        Time.maximumDeltaTime = 1f / 30f;
        if (vp.timeUpdateMode != UnityEngine.Video.VideoTimeUpdateMode.GameTime)
        {
            vp.timeUpdateMode = UnityEngine.Video.VideoTimeUpdateMode.GameTime;
            if (!videoGameTimeLogged)
            {
                videoGameTimeLogged = true;
                Debug.Log("[BATCH] videoGameTime: VideoPlayer.timeUpdateMode=GameTime, maximumDeltaTime=1/30");
            }
        }
    }

    private static int diagWindowState = -1;   // -1 未適用 / 0 区間外 / 1 区間内

    // 現在の動画フレームが -diagWindows のどれかに入っているときだけ診断ログを出す。
    // 状態が変わった tick でだけフラグを書き換える。
    private static void ApplyDiagWindows()
    {
        string spec = SessionState.GetString(KeyDiagWindows, string.Empty);
        if (string.IsNullOrEmpty(spec)) { return; }

        var vp = UnityEngine.Object.FindFirstObjectByType<UnityEngine.Video.VideoPlayer>();
        var player = UnityEngine.Object.FindFirstObjectByType<StreamingStereoVideoPlayer>();
        if (vp == null || player == null) { return; }
        long cur = vp.frame;

        bool inside = false;
        foreach (string raw in spec.Split(','))
        {
            string part = raw.Trim();
            int dash = part.IndexOf('-');
            if (dash <= 0) { continue; }
            if (!long.TryParse(part.Substring(0, dash), out long from)) { continue; }
            if (!long.TryParse(part.Substring(dash + 1), out long to)) { continue; }
            if (cur >= from && cur <= to) { inside = true; break; }
        }

        int state = inside ? 1 : 0;
        if (state == diagWindowState) { return; }
        diagWindowState = state;
        SetDiagFlags(player, inside, SessionState.GetInt(KeyDiagEveryN, 1), SessionState.GetBool(KeyDiagMeshParts, false));
        Debug.Log($"[BATCH] diagWindow {(inside ? "open" : "close")} at vp.frame={cur}");
    }

    private static void Tick()
    {
        if (!SessionState.GetBool(KeyRunning, false))
        {
            EditorApplication.update -= Tick;
            return;
        }

        if (!EditorApplication.isPlaying)
        {
            if (SessionState.GetBool(KeyStarted, false))
            {
                Debug.Log("[BATCH] playmode exited, quitting");
                EditorUtility.audioMasterMute = savedAudioMute;
                SessionState.SetBool(KeyRunning, false);
                EditorApplication.update -= Tick;
                EditorApplication.Exit(0);
            }
            return;
        }

        if (!SessionState.GetBool(KeyStarted, false))
        {
            SessionState.SetBool(KeyStarted, true);
            // playmode に入るとランタイム側の AudioListener が作り直されるので掛け直す。
            AudioListener.volume = 0f;
            EditorUtility.audioMasterMute = true;
            ApplyTargetFrameRate();
            ApplyRecordTimestep();
            if (SessionState.GetBool(KeyNoStackTrace, false))
            {
                Application.SetStackTraceLogType(LogType.Log, StackTraceLogType.None);
                Debug.Log("[BATCH] stack traces off for LogType.Log");
            }
            startedAt = EditorApplication.timeSinceStartup;
            Debug.Log("[BATCH] playmode started at " + startedAt.ToString("F2"));
            return;
        }

        if (startedAt <= 0.0)
        {
            startedAt = EditorApplication.timeSinceStartup;
        }

        ApplyVideoGameTime();
        ApplyDiagWindows();
        TryCaptureFrames();
        TryRecordFrames();
        TryForceMotionAndCaptureMotion();

        double elapsed = EditorApplication.timeSinceStartup - startedAt;
        if (elapsed > SessionState.GetFloat(KeyDeadline, 16f))
        {
            Debug.Log("[BATCH] elapsed " + elapsed.ToString("F2") + "s, stopping playmode");
            EditorApplication.isPlaying = false;
        }
    }
}
