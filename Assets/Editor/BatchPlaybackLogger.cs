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
