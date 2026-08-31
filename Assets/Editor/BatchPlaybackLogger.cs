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
public static class BatchPlaybackLogger
{
    private const string KeyRunning = "BatchPlaybackLogger.Running";
    private const string KeyDeadline = "BatchPlaybackLogger.Deadline";
    private const string KeyStarted = "BatchPlaybackLogger.Started";
    private const string KeyCaptureFrames = "BatchPlaybackLogger.CaptureFrames";
    private const string KeyCaptureDir = "BatchPlaybackLogger.CaptureDir";
    private const string KeyCaptureWidth = "BatchPlaybackLogger.CaptureWidth";

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
        bool? twoAxis = null;
        bool? animAim = null;
        bool? remember = null;
        string bundleName = null;
        string manualYaw = null;
        string manualScale = null;
        bool openSettings = false;
        string displayTracks = null;
        string captureFrames = null;
        string captureDir = null;
        int captureWidth = 3840;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "-screenDistance") float.TryParse(args[i + 1], out screenDistance);
            if (args[i] == "-popoutRange") float.TryParse(args[i + 1], out popoutRange);
            if (args[i] == "-boneRatioTarget") float.TryParse(args[i + 1], out boneRatioTarget);
            if (args[i] == "-diagLogs") bool.TryParse(args[i + 1], out diagLogs);
            if (args[i] == "-diagEveryN") int.TryParse(args[i + 1], out diagEveryN);
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
            if (args[i] == "-noBend" && bool.TryParse(args[i + 1], out bool vNb)) noBend = vNb;
            if (args[i] == "-alignTop" && bool.TryParse(args[i + 1], out bool vAt)) alignTop = vAt;
            if (args[i] == "-bundle") bundleName = args[i + 1];
            if (args[i] == "-manualYaw") manualYaw = args[i + 1];
            if (args[i] == "-manualScale") manualScale = args[i + 1];
            if (args[i] == "-openSettings") bool.TryParse(args[i + 1], out openSettings);
            // "all" で全 track 表示（displayTrackIds を空にする）。"0,1" のように ID 列も可。
            if (args[i] == "-displayTracks") displayTracks = args[i + 1];
            if (args[i] == "-captureFrames") captureFrames = args[i + 1];
            if (args[i] == "-captureDir") captureDir = args[i + 1];
            if (args[i] == "-captureWidth") int.TryParse(args[i + 1], out captureWidth);
        }

        // バックグラウンド実行なので動画の音を鳴らさない（user の常設要望）。
        // Editor 側のトグルとランタイム側の両方を落とす。元の値は戻せるよう控える。
        savedAudioMute = EditorUtility.audioMasterMute;
        EditorUtility.audioMasterMute = true;
        AudioListener.volume = 0f;
        Debug.Log("[BATCH] audio muted");

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
                if (twoAxis.HasValue) { p.SetTwoAxisJointFrameMap(twoAxis.Value); }
                if (animAim.HasValue) { p.SetAnimalKeypointAimAt(animAim.Value); }
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
                + " extendH=" + (extendH.HasValue ? extendH.Value.ToString() : "scene") + " maxExtrap=" + maxExtrap + " minRatio=" + minRatio + " fastLo=" + fastLo + " fastHi=" + fastHi + " alignTop=" + (alignTop.HasValue ? alignTop.Value.ToString() : "scene") + " noBend=" + (noBend.HasValue ? noBend.Value.ToString() : "scene") + " twoAxis=" + (twoAxis.HasValue ? twoAxis.Value.ToString() : "scene") + " animAim=" + (animAim.HasValue ? animAim.Value.ToString() : "scene") + " remember=" + (remember.HasValue ? remember.Value.ToString() : "False(batch既定)"));
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
        if (!string.IsNullOrEmpty(manualYaw) || !string.IsNullOrEmpty(manualScale) || openSettings)
        {
            int applied = 0;
            foreach (var p in UnityEngine.Object.FindObjectsByType<StreamingStereoVideoPlayer>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (!string.IsNullOrEmpty(manualYaw)) { p.batchManualYawSpec = manualYaw; }
                if (!string.IsNullOrEmpty(manualScale)) { p.batchManualScaleSpec = manualScale; }
                if (openSettings) { p.batchOpenSettingsOnStart = true; }
                EditorUtility.SetDirty(p);
                applied++;
            }
            Debug.Log("[BATCH] manualYaw=" + manualYaw + " manualScale=" + manualScale +
                      " openSettings=" + openSettings + " applied to " + applied);
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
                    int every = Mathf.Max(1, diagEveryN);
                    p.logPlacementMeasurement = true;
                    p.logPlacementMeasurementEveryNFrames = every;
                    p.logHumanOtherGap = true;
                    p.logHumanOtherGapEveryNFrames = every;
                    p.logDepthRefineStages = true;
                    p.logPenetrationResolve = true;
                    p.logDepthAffineFit = true;
                    p.logOtherDepthFollow = true;
                    p.logBodyAnchorAlign = true;
                    p.logHorizontalPlacement = true;
                    p.logAnimalBoneVsKeypoint = true;
                    p.logOtherDepthFollowEveryNFrames = every;
                    p.logBoneVsKeypoint = true;
                    p.logBoneVsKeypointEveryNFrames = every;
                }
                EditorUtility.SetDirty(p);
                n++;
            }
            Debug.Log($"[BATCH] boneRatioTarget={boneRatioTarget} diagLogs={diagLogs} applied to {n}");
        }

        SessionState.SetString(KeyCaptureFrames, captureFrames ?? string.Empty);
        SessionState.SetString(KeyCaptureDir, captureDir ?? string.Empty);
        SessionState.SetInt(KeyCaptureWidth, captureWidth);
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

    // VideoPlayer.frame を監視し、指定フレームに達したらカメラの絵を PNG で保存する。
    // -nographics を付けていないので通常どおりレンダリングでき、目視比較に使える。
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
            Debug.Log($"[CAPTURE] frame={want} (vp={cur}) -> {path}");
        }
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
            startedAt = EditorApplication.timeSinceStartup;
            Debug.Log("[BATCH] playmode started at " + startedAt.ToString("F2"));
            return;
        }

        if (startedAt <= 0.0)
        {
            startedAt = EditorApplication.timeSinceStartup;
        }

        TryCaptureFrames();

        double elapsed = EditorApplication.timeSinceStartup - startedAt;
        if (elapsed > SessionState.GetFloat(KeyDeadline, 16f))
        {
            Debug.Log("[BATCH] elapsed " + elapsed.ToString("F2") + "s, stopping playmode");
            EditorApplication.isPlaying = false;
        }
    }
}
