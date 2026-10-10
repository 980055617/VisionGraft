using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;

// tick ごとの連番撮影（2026-10-04、-recordFrames）。「動画を撮って実際の動きを見たい」（ユーザー）のための道具。
//
// -captureFrames は指定フレームで動画を止めて 1 枚撮る（静止画用）。止めるので、フレーム間の補間や
// 1 tick ごとの揺れは写らない。こちらはゲーム時間を固定刻み（Time.captureDeltaTime = 1/fps）で進め、
// 動画もゲーム時間で再生する（-videoGameTime）ので、撮影に何 ms 掛かっても 1 tick = 1/fps 秒のまま。
// 実機（72 Hz）と同じ「tick ごとに見える姿勢」を、区間の全 tick について連番で残す。
//
//   -recordFrames "590-640,830-880"  動画フレームの区間（両端を含む）。この区間に入っている tick を全部撮る
//   -recordDir <dir>                 出力先。w0590_t0000.jpg … と record_index.csv（tick ごとの時刻・動画フレーム）
//   -recordFps 72                    1 tick の刻み（既定 72 = Quest 3 の表示）
//   -recordWidth 1280                メインの絵の幅（16:9）。画角は動画の画面にちょうど合わせる（視点は視聴者のまま）
//   -recordViews "Track_0/spine:2.0:90:30;..."  補助カメラ（書式は -captureViews と同じ）。区間の最初の tick で
//                                    向きと距離を決めて固定し、注視点だけゆっくり追う（骨に貼り付くと上下の揺れが消えるため）
//
// 全区間を撮り終えたら play mode を抜ける（-playSeconds は長めに渡してよい）。
// 確かめ方: record_index.csv の t が 1/fps 刻みで、vf が vt×30 の切り捨てに沿って進むこと（デコードの遅れが無いこと）。
public static partial class BatchPlaybackLogger
{
    private const string KeyRecordFrames = "BatchPlaybackLogger.RecordFrames";
    private const string KeyRecordDir = "BatchPlaybackLogger.RecordDir";
    private const string KeyRecordFps = "BatchPlaybackLogger.RecordFps";
    private const string KeyRecordWidth = "BatchPlaybackLogger.RecordWidth";
    private const string KeyRecordViews = "BatchPlaybackLogger.RecordViews";

    private static int recordLastFrameCount = -1;
    private static int recordWindow = -1;
    private static int recordTick;
    private static bool recordDone;
    private static StreamWriter recordIndex;
    private static StreamWriter recordPose;
    private static List<long[]> recordWindows;
    // 補助カメラの状態（区間ごとに作り直す）: 固定した水平方向・距離と、ゆっくり追う注視点。
    private static readonly Dictionary<string, Vector3> recordViewDir = new Dictionary<string, Vector3>();
    private static readonly Dictionary<string, float> recordViewDist = new Dictionary<string, float>();
    private static readonly Dictionary<string, Vector3> recordViewTarget = new Dictionary<string, Vector3>();

    private static void StoreRecordArgs(string frames, string dir, int fps, int width, string views)
    {
        SessionState.SetString(KeyRecordFrames, frames ?? string.Empty);
        SessionState.SetString(KeyRecordDir, dir ?? string.Empty);
        SessionState.SetInt(KeyRecordFps, Mathf.Clamp(fps, 10, 240));
        SessionState.SetInt(KeyRecordWidth, Mathf.Clamp(width, 320, 3840));
        SessionState.SetString(KeyRecordViews, views ?? string.Empty);
        if (!string.IsNullOrEmpty(frames))
        {
            Debug.Log($"[REC] frames='{frames}' dir='{dir}' fps={fps} width={width} views='{views}'");
        }
    }

    // play mode に入った最初の tick で呼ぶ。撮影がある走りだけ、ゲーム時間を固定刻みにする。
    // 表示の速さ（targetFrameRate）も同じ fps にして、区間に入るまではほぼ実時間で動画をデコードさせる
    // （固定刻みのまま全速で回すと、デコードが追いつかずに動画のフレームが飛ぶおそれがある）。
    private static void ApplyRecordTimestep()
    {
        if (string.IsNullOrEmpty(SessionState.GetString(KeyRecordFrames, string.Empty)))
        {
            return;
        }

        int fps = SessionState.GetInt(KeyRecordFps, 72);
        Time.captureDeltaTime = 1f / fps;
        if (SessionState.GetInt(KeyTargetFps, 0) <= 0)
        {
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = fps;
        }

        Debug.Log($"[REC] captureDeltaTime=1/{fps} targetFrameRate={Application.targetFrameRate}");
    }

    private static List<long[]> ParseRecordWindows(string spec)
    {
        var list = new List<long[]>();
        foreach (string raw in spec.Split(','))
        {
            string part = raw.Trim();
            int dash = part.IndexOf('-');
            if (dash <= 0) { continue; }
            if (!long.TryParse(part.Substring(0, dash), out long from)) { continue; }
            if (!long.TryParse(part.Substring(dash + 1), out long to)) { continue; }
            if (to >= from) { list.Add(new[] { from, to }); }
        }

        return list;
    }

    private static void TryRecordFrames()
    {
        if (recordDone) { return; }
        string spec = SessionState.GetString(KeyRecordFrames, string.Empty);
        string dir = SessionState.GetString(KeyRecordDir, string.Empty);
        if (string.IsNullOrEmpty(spec) || string.IsNullOrEmpty(dir)) { return; }

        // 1 tick（player の 1 フレーム）に 1 枚。Editor の update が同じフレームに 2 回来ても撮り直さない。
        if (Time.frameCount == recordLastFrameCount) { return; }

        var vp = UnityEngine.Object.FindFirstObjectByType<UnityEngine.Video.VideoPlayer>();
        if (vp == null || !vp.isPrepared) { return; }
        long cur = vp.frame;
        if (cur < 0) { return; }

        if (recordWindows == null)
        {
            recordWindows = ParseRecordWindows(spec);
        }

        int w = -1;
        bool anyAhead = false;
        for (int i = 0; i < recordWindows.Count; i++)
        {
            if (cur >= recordWindows[i][0] && cur <= recordWindows[i][1]) { w = i; break; }
            if (cur < recordWindows[i][1]) { anyAhead = true; }
        }

        if (w < 0)
        {
            if (!anyAhead && recordWindows.Count > 0)
            {
                FinishRecording($"vp.frame={cur} は全区間の後");
            }
            return;
        }

        Directory.CreateDirectory(dir);
        if (recordIndex == null)
        {
            recordIndex = new StreamWriter(Path.Combine(dir, "record_index.csv"), false, new System.Text.UTF8Encoding(false));
            recordIndex.WriteLine("name,window,tick,frameCount,t,vt,vf,ct,dt");
        }

        if (w != recordWindow)
        {
            recordWindow = w;
            recordTick = 0;
            recordViewDir.Clear();
            recordViewDist.Clear();
            recordViewTarget.Clear();
            Debug.Log($"[REC] window {recordWindows[w][0]}-{recordWindows[w][1]} start vp.frame={cur} t={Time.time:F4} vt={vp.time:F4}");
        }

        recordLastFrameCount = Time.frameCount;
        Camera cam = ResolveRecordCamera();
        if (cam == null) { return; }

        int W = SessionState.GetInt(KeyRecordWidth, 1280);
        int H = Mathf.RoundToInt(W * 9f / 16f);
        string name = $"w{recordWindows[w][0]:D4}_t{recordTick:D4}";
        float fov = ResolveScreenFitFov(cam, (float)W / H);
        RenderRecordView(cam, cam.transform.position, cam.transform.rotation, fov, cam.nearClipPlane, cam.farClipPlane,
            W, H, Path.Combine(dir, name + ".jpg"), null);
        RecordAuxViews(cam, dir, name);
        WriteRecordPose(dir, name, cam);

        var inv = CultureInfo.InvariantCulture;
        recordIndex.WriteLine(string.Join(",", name, recordWindows[w][0].ToString(inv), recordTick.ToString(inv),
            Time.frameCount.ToString(inv), Time.time.ToString("F5", inv), vp.time.ToString("F5", inv), cur.ToString(inv),
            vp.clockTime.ToString("F5", inv), Time.deltaTime.ToString("F5", inv)));
        if (recordTick % 72 == 0)
        {
            recordIndex.Flush();
            Debug.Log($"[REC] {name} vf={cur} t={Time.time:F4} vt={vp.time:F4} ct={vp.clockTime:F4} dt={Time.deltaTime:F5}");
        }

        recordTick++;
    }

    // tick ごとの頭・首・root の world の位置・回転と視聴者（main カメラ）の位置を record_pose.csv へ（2026-10-05）。
    // 頭を視聴者へ向ける機能の検算用。鼻先は頭ローカルの鼻の方向（ログの [LOOKAT] local=）を掛けてオフラインで出す。
    // BoneWorldDump は追従の経路の最後でしか書かないので、インタラクティブモーションのイベント中の tick が入らない。
    private static void WriteRecordPose(string dir, string name, Camera cam)
    {
        if (recordPose == null)
        {
            recordPose = new StreamWriter(Path.Combine(dir, "record_pose.csv"), false, new System.Text.UTF8Encoding(false));
            // 2026-10-05 追記: 役の骨の位置（足先 4 本・尾 3 点・四肢の付け根 4 本）を末尾に足した（足の接地・滑り・着地の順・尾の振れを測るため）。
            recordPose.WriteLine("name,frameCount,vx,vy,vz,hpx,hpy,hpz,hqx,hqy,hqz,hqw,nqx,nqy,nqz,nqw,rpx,rpy,rpz,rqx,rqy,rqz,rqw," +
                "npx,npy,npz,spx,spy,spz,sqx,sqy,sqz,sqw," +
                "flpx,flpy,flpz,frpx,frpy,frpz,rlpx,rlpy,rlpz,rrpx,rrpy,rrpz,tbx,tby,tbz,tmx,tmy,tmz,ttx,tty,ttz," +
                "flux,fluy,fluz,frux,fruy,fruz,rlux,rluy,rluz,rrux,rruy,rruz");
        }

        // 撮る track は補助カメラの指定の最初の "Track_N/..." から決める（既定 Track_0。Lynx は実験と同じ track 1 で撮る）。
        string track = ResolveRecordPoseTrack();
        GameObject trackObject = GameObject.Find(track);
        var player = UnityEngine.Object.FindFirstObjectByType<StreamingStereoVideoPlayer>();
        AnimalRigCache cache = player != null && trackObject != null ? player.PeekAnimalRigCacheForBatch(trackObject) : null;
        Transform head = cache != null ? cache.head : null;
        if (head == null && !TryResolveAuxViewTarget(track + "/head", out head, out _))
        {
            return;
        }

        Transform neck = cache != null ? cache.neck : null;
        Transform spine = cache != null ? cache.spine : null;
        if (neck == null) { TryResolveAuxViewTarget(track + "/neck", out neck, out _); }
        if (spine == null) { TryResolveAuxViewTarget(track + "/spine", out spine, out _); }
        Transform root = head;
        while (root.parent != null && !root.name.StartsWith("Track_", StringComparison.Ordinal))
        {
            root = root.parent;
        }

        var inv = CultureInfo.InvariantCulture;
        var cols = new List<string> { name, Time.frameCount.ToString(inv) };
        void V(Vector3 v) { cols.Add(v.x.ToString("F5", inv)); cols.Add(v.y.ToString("F5", inv)); cols.Add(v.z.ToString("F5", inv)); }
        void Q(Quaternion q) { cols.Add(q.x.ToString("F6", inv)); cols.Add(q.y.ToString("F6", inv)); cols.Add(q.z.ToString("F6", inv)); cols.Add(q.w.ToString("F6", inv)); }
        V(cam.transform.position);
        V(head.position);
        Q(head.rotation);
        Q(neck != null ? neck.rotation : Quaternion.identity);
        V(root.position);
        Q(root.rotation);
        // 首と背骨（体の前の向きを neck − spine の水平で近似する用）
        V(neck != null ? neck.position : Vector3.zero);
        V(spine != null ? spine.position : Vector3.zero);
        Q(spine != null ? spine.rotation : Quaternion.identity);
        // 役の骨（取れない骨は 0）。後ろ足はつま先があればつま先
        Vector3 P(Transform t) => t != null ? t.position : Vector3.zero;
        V(P(cache?.leftFrontPaw));
        V(P(cache?.rightFrontPaw));
        V(P(cache != null ? (cache.leftRearToe != null ? cache.leftRearToe : cache.leftRearPaw) : null));
        V(P(cache != null ? (cache.rightRearToe != null ? cache.rightRearToe : cache.rightRearPaw) : null));
        V(P(cache?.tailBase));
        V(P(cache?.tailMid));
        V(P(cache?.tailTip));
        V(P(cache?.leftFrontUpper));
        V(P(cache?.rightFrontUpper));
        V(P(cache?.leftRearUpper));
        V(P(cache?.rightRearUpper));
        recordPose.WriteLine(string.Join(",", cols));
    }

    private static string ResolveRecordPoseTrack()
    {
        string spec = SessionState.GetString(KeyRecordViews, string.Empty);
        foreach (string raw in spec.Split(';'))
        {
            string key = raw.Trim();
            int slash = key.IndexOf('/');
            if (slash > 0 && key.StartsWith("Track_", StringComparison.Ordinal))
            {
                return key.Substring(0, slash);
            }
        }

        return "Track_0";
    }

    private static void FinishRecording(string reason)
    {
        recordDone = true;
        if (recordIndex != null)
        {
            recordIndex.Flush();
            recordIndex.Dispose();
            recordIndex = null;
        }

        if (recordPose != null)
        {
            recordPose.Flush();
            recordPose.Dispose();
            recordPose = null;
        }

        Debug.Log($"[REC] done ({reason}), stopping playmode");
        EditorApplication.isPlaying = false;
    }

    private static Camera ResolveRecordCamera()
    {
        Camera cam = Camera.main;
        if (cam == null) { cam = UnityEngine.Object.FindFirstObjectByType<Camera>(); }
        if (cam == null)
        {
            var all = UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            if (all.Length > 0) { cam = all[0]; }
        }

        return cam;
    }

    // 視点（メインカメラの位置・向き）はそのままに、縦の画角だけを動画の画面の 4 隅が入る大きさに絞る。
    // 画面はメインカメラの正面にあるので、絵の中央に画面が来る（-captureFrames の絵を後で切り出していたのと同じ範囲）。
    private static float ResolveScreenFitFov(Camera cam, float aspect)
    {
        var player = UnityEngine.Object.FindFirstObjectByType<StreamingStereoVideoPlayer>();
        if (player == null || !player.TryGetScreenFrame(out Vector3 c, out Vector3 up, out Vector3 right, out Vector2 size))
        {
            return cam.fieldOfView;
        }

        Quaternion toLocal = Quaternion.Inverse(cam.transform.rotation);
        float need = 0f;
        for (int sx = -1; sx <= 1; sx += 2)
        {
            for (int sy = -1; sy <= 1; sy += 2)
            {
                Vector3 corner = c + right * (sx * size.x * 0.5f) + up * (sy * size.y * 0.5f);
                Vector3 local = toLocal * (corner - cam.transform.position);
                if (local.z <= 0.01f) { continue; }
                need = Mathf.Max(need, Mathf.Max(Mathf.Abs(local.y / local.z), Mathf.Abs(local.x / local.z) / aspect));
            }
        }

        return need > 0f ? 2f * Mathf.Atan(need * 1.03f) * Mathf.Rad2Deg : cam.fieldOfView;
    }

    // 補助カメラ。区間の最初の tick で水平方向と距離を決めて固定し、注視点は半減期 0.15 秒で骨を追う。
    private static void RecordAuxViews(Camera mainCam, string dir, string name)
    {
        string spec = SessionState.GetString(KeyRecordViews, string.Empty);
        if (string.IsNullOrEmpty(spec)) { return; }

        var inv = CultureInfo.InvariantCulture;
        var style = NumberStyles.Float;
        float alpha = 1f - Mathf.Pow(0.5f, Time.deltaTime / 0.15f);
        float alphaDir = 1f - Mathf.Pow(0.5f, Time.deltaTime / 0.15f);
        foreach (string raw in spec.Split(';'))
        {
            string key = raw.Trim();
            string[] p = key.Split(':');
            // "viewer": 視聴者の目（main カメラの位置）からモデルを寄りで撮る（2026-10-04、頭が視聴者を見ているかを確かめる用）。
            // 2 つ目 = 枠に入れる大きさ（モデルの大きさの倍数）、画角は距離から毎 tick 決める。6 つ目が "c" なら bounds の中心を見る。
            if (p.Length >= 3 && p[2] == "viewer")
            {
                if (p.Length < 2 || !float.TryParse(p[1], style, inv, out float frameFactor) ||
                    !TryResolveAuxViewTarget(p[0], out Transform vTarget, out float vSize))
                {
                    continue;
                }

                Vector3 vAim = p.Length >= 6 && p[5] == "c" ? ResolveModelBoundsCenter(vTarget) : vTarget.position;
                Vector3 eye = mainCam.transform.position;
                Vector3 look = vAim - eye;
                if (look.sqrMagnitude < 1e-8f) { continue; }

                float vDist = look.magnitude;
                float vFov = Mathf.Clamp(2f * Mathf.Atan(0.5f * frameFactor * vSize / vDist) * Mathf.Rad2Deg, 2f, 90f);
                RenderRecordView(mainCam, eye, Quaternion.LookRotation(look / vDist, Vector3.up), vFov,
                    Mathf.Max(0.001f, vDist * 0.05f), 100f, 960, 720, Path.Combine(dir, $"{name}_{p[0].Replace('/', '-')}_viewer.jpg"), vTarget);
                continue;
            }

            bool bodyFront = p.Length >= 3 && p[2] == "front";
            // "side" / "side-": 体の真横（体の前後軸から +90° / −90°）。体の向きに合わせて毎 tick 回し、半減期 0.15 秒でならす
            // （区間の最初で固定すると、走って向きを変えた動物を後ろから撮ってしまい脚の振りが見えない）。
            // 注視点は水平方向だけ骨にそのまま付け、上下だけならす（走る動物が枠から外れないように。上下の弾みは残す）。
            int bodySide = p.Length >= 3 ? (p[2] == "side" ? 1 : (p[2] == "side-" ? -1 : 0)) : 0;
            float azimuth = 0f;
            if (p.Length < 4 ||
                !float.TryParse(p[1], style, inv, out float distFactor) ||
                (!bodyFront && bodySide == 0 && !float.TryParse(p[2], style, inv, out azimuth)) ||
                !float.TryParse(p[3], style, inv, out float fov))
            {
                continue;
            }

            float elevation = 0f;
            if (p.Length >= 5) { float.TryParse(p[4], style, inv, out elevation); }
            if (!TryResolveAuxViewTarget(p[0], out Transform target, out float modelSize))
            {
                continue;
            }

            // 6 つ目が "c" なら、注視点を骨ではなくモデル全体（Track_N の表示の bounds）の中心にする（2026-10-04）。
            // 背骨の "spine" は多くのリグで骨盤寄りの 1 本なので、そこを見ると体の前半分が枠から外れた（S1 の 27_GermanShepherd）。
            bool aimAtBoundsCenter = p.Length >= 6 && p[5] == "c";
            Vector3 aimPoint = aimAtBoundsCenter ? ResolveModelBoundsCenter(target) : target.position;

            if (bodySide != 0 && TryResolveBodyForward(target, out Vector3 bodyFwd))
            {
                Vector3 sideH = Quaternion.AngleAxis(90f * bodySide, Vector3.up) * bodyFwd;
                float elS = elevation * Mathf.Deg2Rad;
                Vector3 want = (sideH * Mathf.Cos(elS) + Vector3.up * Mathf.Sin(elS)).normalized;
                if (recordViewDir.TryGetValue(key, out Vector3 prevDir))
                {
                    recordViewDir[key] = Vector3.Slerp(prevDir, want, alphaDir).normalized;
                }
                else
                {
                    recordViewDir[key] = want;
                    recordViewDist[key] = Mathf.Max(0.01f, distFactor * modelSize);
                    recordViewTarget[key] = aimPoint;
                }
            }

            if (!recordViewDir.TryGetValue(key, out Vector3 viewDir))
            {
                Vector3 toCam = mainCam.transform.position - aimPoint;
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
                    if (fwd.sqrMagnitude > 1e-8f) { dirH = fwd.normalized; }
                }

                float el = elevation * Mathf.Deg2Rad;
                viewDir = (dirH * Mathf.Cos(el) + Vector3.up * Mathf.Sin(el)).normalized;
                recordViewDir[key] = viewDir;
                recordViewDist[key] = Mathf.Max(0.01f, distFactor * modelSize);
                recordViewTarget[key] = aimPoint;
            }

            Vector3 prevAim = recordViewTarget[key];
            Vector3 aim = bodySide != 0
                ? new Vector3(aimPoint.x, Mathf.Lerp(prevAim.y, aimPoint.y, alpha), aimPoint.z)
                : Vector3.Lerp(prevAim, aimPoint, alpha);
            recordViewTarget[key] = aim;
            float dist = recordViewDist[key];
            string label = p[0].Replace('/', '-') + "_" +
                (bodyFront ? "front" : bodySide > 0 ? "side" : bodySide < 0 ? "sideL" : Mathf.RoundToInt(azimuth).ToString(inv));
            RenderRecordView(mainCam, aim + viewDir * dist, Quaternion.LookRotation(-viewDir, Vector3.up), fov,
                Mathf.Max(0.001f, dist * 0.05f), 100f, 960, 720, Path.Combine(dir, $"{name}_{label}.jpg"), target);
        }
    }

    // 骨が属するモデル（Track_N のインスタンス）の、有効な Renderer の bounds の中心。無ければ骨の位置。
    private static Vector3 ResolveModelBoundsCenter(Transform target)
    {
        Transform model = target;
        while (model.parent != null && !model.name.StartsWith("Track_", StringComparison.Ordinal))
        {
            model = model.parent;
        }

        bool hasBounds = false;
        Bounds b = default(Bounds);
        foreach (Renderer r in model.GetComponentsInChildren<Renderer>())
        {
            if (!r.enabled) { continue; }
            if (!hasBounds) { b = r.bounds; hasBounds = true; }
            else { b.Encapsulate(r.bounds); }
        }

        return hasBounds ? b.center : target.position;
    }

    // 体の前後軸（水平）。Humanoid は左右の大腿の付け根から（右 − 左 と上の外積）、Animal は同じインスタンスの
    // "spine"（無ければ骨そのもの）→ Spine4（無ければ neck、head）。頭は首と一緒に振れるので、あれば根と一緒に動く背骨の骨を使う。
    // 起点を "spine" にするのは、脚の骨を注視するとき（"front_l_upper" など）に、骨 → 首が上向きになって水平成分が定まらず、
    // 横からのはずの画が後ろや上からになったため（2026-10-04、S1）。
    private static bool TryResolveBodyForward(Transform target, out Vector3 fwd)
    {
        fwd = Vector3.zero;
        Animator animator = target.GetComponentInParent<Animator>();
        if (animator != null && animator.isHuman)
        {
            Transform l = animator.GetBoneTransform(HumanBodyBones.LeftUpperLeg);
            Transform r = animator.GetBoneTransform(HumanBodyBones.RightUpperLeg);
            if (l != null && r != null)
            {
                fwd = Vector3.Cross(r.position - l.position, Vector3.up);
            }
        }
        else if (TryFindSiblingBone(target, "Spine4", out Transform front) ||
                 TryFindSiblingBone(target, "neck", out front) ||
                 TryFindSiblingBone(target, "head", out front))
        {
            Transform origin = TryFindSiblingBone(target, "spine", out Transform spine) && spine != front ? spine : target;
            fwd = front.position - origin.position;
        }

        fwd.y = 0f;
        if (fwd.sqrMagnitude < 1e-8f)
        {
            return false;
        }

        fwd.Normalize();
        return true;
    }

    // mainCam の設定を写した一時カメラで 1 枚撮って JPEG で保存する。auxTarget があるときは、そのモデル（Track_N の
    // インスタンス）以外の表示を撮る間だけ隠す。動画の画面やほかの track が横から写り込むと、手足の動きが読みにくい。
    private static void RenderRecordView(Camera mainCam, Vector3 pos, Quaternion rot, float fov, float near, float far,
        int W, int H, string path, Transform auxTarget)
    {
        var hidden = new List<Renderer>();
        if (auxTarget != null)
        {
            Transform model = auxTarget;
            while (model.parent != null && !model.name.StartsWith("Track_", StringComparison.Ordinal))
            {
                model = model.parent;
            }

            foreach (Renderer r in UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None))
            {
                if (r.enabled && !r.transform.IsChildOf(model)) { r.enabled = false; hidden.Add(r); }
            }
        }

        var go = new GameObject("RecordCamera");
        RenderTexture prevActive = RenderTexture.active;
        try
        {
            Camera cam = go.AddComponent<Camera>();
            cam.CopyFrom(mainCam);
            cam.enabled = false;
            cam.stereoTargetEye = StereoTargetEyeMask.None;
            cam.transform.SetPositionAndRotation(pos, rot);
            cam.fieldOfView = fov;
            cam.nearClipPlane = near;
            cam.farClipPlane = far;
            var rt = new RenderTexture(W, H, 24) { antiAliasing = 4 };
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(W, H, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
            tex.Apply();
            cam.targetTexture = null;
            RenderTexture.active = prevActive;
            File.WriteAllBytes(path, tex.EncodeToJPG(92));
            UnityEngine.Object.DestroyImmediate(tex);
            rt.Release();
            UnityEngine.Object.DestroyImmediate(rt);
        }
        finally
        {
            RenderTexture.active = prevActive;
            UnityEngine.Object.DestroyImmediate(go);
            foreach (Renderer r in hidden)
            {
                if (r != null) { r.enabled = true; }
            }
        }
    }
}
