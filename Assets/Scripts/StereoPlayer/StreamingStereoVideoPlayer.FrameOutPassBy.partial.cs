using System.Collections.Generic;
using UnityEngine;

// 画面の左右の端から出た物・人・動物を、視聴者の横を通り過ぎるように進める（2026-10-26、frameOutPassByViewer、既定 OFF・試作）。
// 幾何は FrameOutPassBy.cs（純粋な計算）。ここは Unity の部品（測った向き・点の集め方・速さ・ログ）。
//
// 実機のユーザー（10/26）:「人の動画ボールやっぱり右に行っちゃうけど，もっとユーザーの隣を通り過ぎる感じがいいね」
// 「車もまだ真左行っちゃうと思うけど，ユーザーのとなりを通り過ぎるように」「犬も全然真左行ってる」。10/25 のテレビの女性
// 「もっとユーザーの隣を通り過ぎるように，まっすぐに歩いてほしい」。
// 今の続きの向き:
//   - 物（ElseFrameOut の慣性）: 画面の内側の端の px の速さを最後の深さで画面の横へ（奥行き・高さ・大きさは変えない）→ 真横
//   - 人・動物（BodyFrameOut、bodyFrameOutFollowBodyFacing）: 体の前から、目から bodyFrameOutClearanceMeters（0.25 m）を保つよう
//     目から離れる向きへ最小の角だけ回す → もう 0.25 m の近くにいる犬は 79° 回って真横（体は視聴者を向いたまま横へ滑る）
// この切り替え（物・人・動物で同じ 1 つの決まり、FrameOutPassBy.Resolve。調べ・案 A/B・判定は scratchpad passby/）:
//   1. 測った向きから始める。物 = 端に掛かる前（見切れていない最後のフレームまで）の動きを、同じ絵になる「大きさの変わらない物」の動きに
//      直したもの v − (p − 目) × (大きさの伸び ÷ 大きさ)（ElseFrameOutState の velocity3D・motionPosition・scaleRatePerFrame・motionScale）。
//      物の奥行きは近さの下限（0.63 m）や ⑨ で決まり、近づく分は大きさの伸びになっているので、root の動きだけだと真横に見える
//      （ボール yaw 91°・車 −88〜−132°）。直すと本物と同じく視聴者へほぼまっすぐ（ボール 177°・車 −171〜−178°。本物は +7°・−5〜−1°）。
//      目のまわりに大きさごと動かす（⑨・S）分は絵が変わらないので、この動きには入らない（S の有無で同じ向き）。
//      測ってから FrameOutPassByMaxAgeSeconds（3 秒）より古い動きは使わず、今の慣性の向きから始める（長く端に止まっていた物）。
//      人・動物 = 端に掛かる前の体の前（bodyFrameOutFollowBodyFacing と同じ測り方）
//   2. その向きにまっすぐ進んだとき、目にいちばん近づく点（物 = renderer の箱の角、人・動物 = ⑧ が投影に使う骨）が、出ていく側を
//      frameOutPassMinMeters〜frameOutPassMaxMeters（0.15〜0.30 m、地面の上）で通るならそのまま。通らなければ通るようになる最小の角だけ回す
//      （frameOutPassMaxTurnDegrees 45° まで）
//   3. 速さは今のまま（物 = 今の慣性の速さ、人・動物 = 今の歩く速さ）。frameOutPassBySpeedFromApproach（既定 OFF）なら、端に掛かる前の
//      box の高さの伸び（/秒）× 目からの距離 = 映像の物が目の面に着くまでの時間に合う速さが今より速いときだけ上げる（上限は大きさの 2.5 倍/秒）。
//      終わり方は今のまま（見ているカメラの視界から外れたら隠す・安全弁）
//   4. 向き: 人は進む向きへ向く（今と同じ。この決まりを使ったときも root を先に回して Hips を残す）、動物は root を回さない（今と同じ）。
//      物はホイールのあるもの（車）だけ同じ角だけ向きを回す（始めの BodyFrameOutBlendSeconds で混ぜる。物理の理由: 車は向いている方へしか
//      走れない。ホイールの無い球・風船は回さない）
//   5. 回す角が 45° を超える（画面を横切って出ていく物・人）・どこかの点がもう 0.15 m より近い・測った向きが無いときは使わない（今の向き）。
//      上下の端から出る物（car の 5 台・風船・犬の動画のボール）と、端に掛かっている間（車の 0.9〜1.75 秒）は変えない
// 撮影の前の予測（scratchpad passby/judge）: ボールは回さず（S は 5〜7°）ほぼまっすぐ視聴者の右を箱の角 0.27〜0.30 m で通る、車は 29〜37°
// 視聴者の方へ曲がって左を 0.30 m で通る、テレビの女性は曲げずに体の向きのまま左を 0.21〜0.25 m で通る、犬は 31° 左へ曲がって視聴者の方へ
// 歩くが 0.07 m/s なので動画が周回する前には通り過ぎない（frameOutPassBySpeedFromApproach なら 0.18 m/s で、周回の時に視界の左の外）。
// この犬の 2 つの速さは S なしの予測。撮影（10/26、S ON、scratchpad validate/combined/passby/runs_final、点検 wf_b39a3f2f-bac）: ボールは 6.2〜6.5°
// 視聴者の方へ曲がって右を 0.30 m、車は 22.7〜31.7° 曲がって左を 0.30 m、テレビの女性・ゾンビは曲げず 0.24〜0.25 m、犬・狼は 32° 曲がり
// 0.205〜0.215 m/s（frameOutPassBySpeedFromApproach でも 0.210 m/s）。犬の鼻先は水平 0.23 m まで来て、体が通り過ぎる前に動画が周回する
// （そのとき頭は目の左 0.27 m）。体が 25° 回って頭が外へ振れるので、まっすぐ進めた計画の 0.15 m より 8 cm 遠い。
public partial class StreamingStereoVideoPlayer
{
    [Header("Frame-out pass-by (2026-10-26)")]
    // 2026-10-27 から既定 ON（見る用の版 l の中身を既定にした）。上の既定の説明はそれまでの話。
    public bool frameOutPassByViewer = true;
    // 目のいちばん近くを通る点（物 = 箱の角、人・動物 = 骨）の距離の帯（m、地面の上）。下より近くは通さない、上より遠いなら目の方へ曲げる。
    // 下 0.15 m: 犬（始めに一番近い骨 0.26 m）が 45° 以内で通れる（0.20 m では 47° 要り、今の向きに戻る）。CenterEyeAnchor の近い面は 0.1 m。
    // 上 0.30 m: 10/26 に Quest で見ていただいた版のテレビの女性の通り方（一番近い骨 0.25 m）が帯に入る値（その通り方の採否は聞いていない）。
    // どちらもユーザーの判断（どれだけ横を通すか）。
    [Min(0f)] public float frameOutPassMinMeters = 0.15f;
    [Min(0f)] public float frameOutPassMaxMeters = 0.30f;
    [Range(0f, 90f)] public float frameOutPassMaxTurnDegrees = 45f;
    // 上の決まりを使った出口だけ、速さも近づき方から決める（既定 OFF）: 端に掛かる前の、どの端にも触れていないフレームの box の高さの伸び
    // （ln の比の EMA、/秒、測ってから 3 秒以内）× 目から点の中心までの距離（地面の上）。今の速さより遅くはしない。上限は大きさ（人・動物は
    // renderer の高さ、物は全長）の 2.5 倍/秒。予測（S なし）: 犬・狼 0.07 → 0.18 m/秒、車の 2・3 台目 0.30 → 1.12・1.18 → 1.94 m/秒、ボール・車の
    // 1 台目・テレビの女性は変わらない。撮影（10/26、S ON）: 犬は 0.205 → 0.210 m/秒でほぼ変わらない（もとの速さが高く、点の中心まで 0.40 m と近い）。
    // 動物の歩きのクリップは速さに合わせないので足が滑る。
    public bool frameOutPassBySpeedFromApproach = false;
    // 上の決まりを使った物（Else。曲げ 0 の「帯の中」も含む。coastPassBy）は、箱の 8 角と中心が全部見ているカメラの視錐台から出るまで消さない
    // （2026-10-26、既定 true、frameOutPassByViewer が ON のときだけ効く）。今の消え方は「箱の中心が顔の前 5 cm より近い、または視錐台から出た」（IsCoastingInstanceInView）で、目の横 0.30 m を
    // 通る長い車（全長 1.15〜2.15 m）は、中心が目の面に来た時に後ろ半分がまだ視野の中（前から 40〜50°、視野の端は約 53°）にあり、前を向いていると
    // 視野の左の端でぱっと消えていた（3 台 × モデル 2 組の 6 通りすべて。点検 wf_b39a3f2f-bac の vanish-beside-viewer）。曲げる前は視野の外へ出てから
    // 消えていた。人・動物（BodyFrameOut）は変えない（テレビの女性は目の左 81° で消え、もう視野の外）。安全弁（距離・フレーム数）はそのまま。
    public bool frameOutPassByKeepsUntilOutOfView = true;
    // 続きを始めた所で 1 行（[PASSBY] else start / [BODYFRAMEOUT] start の「pass-by …」）。
    public bool logFrameOutPassBy = true;
    // 続きの tick ごとに、目から見た位置を [PASSBY] step に出す（真上から見た道筋をログから描く用。既定 OFF。frameOutPassByViewer が OFF でも出す）。
    public bool logFrameOutPassByFrames = false;

    // 測った動き（物の向き）と box の高さの伸び（速さ）を使ってよい古さ。
    private const float FrameOutPassByMaxAgeSeconds = 3f;
    private const float FrameOutPassByLoomSmoothing = 0.3f;

    private readonly List<Vector3> frameOutPassPoints = new List<Vector3>(128);
    private readonly List<Vector3> frameOutPassStepPoints = new List<Vector3>(128);
    private readonly List<RigidWheel> frameOutPassWheels = new List<RigidWheel>(8);

    // ElseFrameOut の慣性を始めた tick に、今の慣性の速度（velocityPerFrame）を決めた後で呼ぶ。曲げられたら velocityPerFrame と
    // 向きの回し先（coastRotationTarget）を差し替える。切り替えが OFF・上下の端・曲げられないときは今のまま（coastPassBy = false）。
    private void ApplyFrameOutPassByToElseCoast(uint trackId, ElseFrameOutState st, GameObject instance, Transform screen,
        Vector3 camOrigin, Quaternion camRotation, int frame)
    {
        st.coastPassBy = false;
        st.coastSteers = false;
        st.coastRotationTarget = st.lastRotation;
        float speed = st.velocityPerFrame.magnitude;
        if (!frameOutPassByViewer || instance == null || st.clipSide == 0 || speed <= 1e-6f)
        {
            return;
        }

        float fps = Mathf.Max(1f, ResolveMetaFps());
        int maxAge = Mathf.Max(1, Mathf.RoundToInt(FrameOutPassByMaxAgeSeconds * fps));
        Vector3 up = screen != null && screen.up.sqrMagnitude > 0.000001f ? screen.up.normalized : Vector3.up;
        Vector3 camRight = camRotation * Vector3.right;
        Vector3 camForward = camRotation * Vector3.forward;
        Vector3 measured = st.velocityPerFrame;
        string source = "coast";
        if (st.hasMotion && st.motionScale > 1e-6f && st.motionFrame >= 0 && frame - st.motionFrame <= maxAge)
        {
            // 同じ絵になる大きさの変わらない物の動き（目のまわりに大きさごと動かす分を除く）。
            Vector3 equivalent = st.velocity3D - (st.motionPosition - camOrigin) * (st.scaleRatePerFrame / st.motionScale);
            if (Vector3.ProjectOnPlane(equivalent, up).sqrMagnitude > 1e-12f)
            {
                measured = equivalent;
                source = "picture";
            }
        }
        else if (st.hasMotion && st.motionFrame >= 0)
        {
            source = "coast(motion " + (frame - st.motionFrame) + "f old)";
        }

        Camera cam = GetViewCamera();
        Vector3 eye = cam != null ? cam.transform.position : camOrigin;
        // 点 = renderer ごとのローカル箱の 8 角（回転込み、TryProjectRendererBoundsToEye と同じ箱）。慣性は最後に見えた位置から始まるので、そこへずらす。
        List<Vector3> points = frameOutPassPoints;
        points.Clear();
        CollectFrameOutPassBoxCorners(instance, eye, st.lastPosition - instance.transform.position, points);
        if (points.Count == 0)
        {
            return;
        }

        frameOutPassWheels.Clear();
        bool hasWheels = TryResolveRigidWheels(instance, instance.transform.rotation, frameOutPassWheels, out _);
        frameOutPassWheels.Clear();
        FrameOutPassBy.Result r = FrameOutPassBy.Resolve(points, st.lastPosition - eye, measured, camRight * st.clipSide, camForward, up,
            frameOutPassMinMeters, Mathf.Max(frameOutPassMinMeters, frameOutPassMaxMeters), frameOutPassMaxTurnDegrees, hasWheels);

        // 速さ（frameOutPassBySpeedFromApproach のときだけ変わる。点の中心までの地面の上の距離 × box の高さの伸び、上限は全長の 2.5 倍/秒）。
        float newSpeed = speed;
        string speedNote = "edge";
        bool hasGrowth = TryGetFrameOutApproachRate(st.hasLoom, st.loomFrame, st.loomRatePerFrame, frame, fps, out float growthPerSecond);
        if (r.ok && frameOutPassBySpeedFromApproach && hasGrowth)
        {
            float approach = Mathf.Min(FrameOutPassCentreGroundDistance(points, up) * growthPerSecond, 2.5f * Mathf.Max(0.01f, st.lastLength)) / fps;
            if (approach > newSpeed)
            {
                newSpeed = approach;
                speedNote = "approach";
            }
        }

        if (logFrameOutPassBy)
        {
            Vector3 start = st.lastPosition - eye;
            GetFrameOutPassExtentOnCamera(points, camRight, camForward, out float minX, out float maxX, out float minZ, out float maxZ);
            Debug.Log($"[PASSBY] else start f={frame} track={trackId} model={instance.name} side={st.clipSide} from={source} ok={r.ok} note={r.note} " +
                      $"yawCam {YawOnCamera(measured, camRight, camForward):F1} -> {(r.ok ? YawOnCamera(r.heading, camRight, camForward).ToString("F1") : "-")} " +
                      $"bend={r.bendDegrees:F1}deg pass={FormatPassMeters(r.passMeters)}m measuredPass={FormatPassMeters(r.measuredPassMeters)}m " +
                      $"nearest={r.nearestStartMeters:F3}m speed={speed * fps:F3}->{newSpeed * fps:F3}m/s ({speedNote}) " +
                      $"growth={(hasGrowth ? growthPerSecond.ToString("F2") : "-")}/s wheels={hasWheels} " +
                      $"rootCam=({Vector3.Dot(start, camRight):F3},{Vector3.Dot(start, up):F3},{Vector3.Dot(start, camForward):F3}) " +
                      $"boxCam x[{minX:F3},{maxX:F3}] z[{minZ:F3},{maxZ:F3}] points={points.Count}");
        }

        if (!r.ok)
        {
            return;
        }

        st.velocityPerFrame = r.heading * newSpeed;
        st.coastPassBy = true;
        st.coastSteers = hasWheels && Mathf.Abs(r.bendDegrees) > 0.01f;
        st.coastRotationTarget = Quaternion.AngleAxis(r.bendDegrees, up) * st.lastRotation;
    }

    // 慣性の向き（coastSteers のとき、始めの BodyFrameOutBlendSeconds で最後に見えた向き → 回し先）。
    private Quaternion ResolveElseCoastRotation(ElseFrameOutState st, int elapsed)
    {
        if (!st.coastSteers)
        {
            return st.lastRotation;
        }

        float blendFrames = Mathf.Max(1f, BodyFrameOutBlendSeconds * ResolveMetaFps());
        return Quaternion.Slerp(st.lastRotation, st.coastRotationTarget, Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(elapsed / blendFrames)));
    }

    // StartBodyFrameOut から（bodyFrameOutFollowBodyFacing の TryResolveBodyFrameOutPassHeading より先）。点 = ⑧ が投影に使う骨
    // （無ければ renderer の箱の中心）、測った向き = 端に掛かる前の体の前。note は [BODYFRAMEOUT] start の向きの欄に入る（使わなかったときも）。
    // centreDistance = 目から点の中心までの地面の上の距離（frameOutPassBySpeedFromApproach の速さに使う）。
    private bool TryResolveBodyFrameOutPassByHeading(GameObject instance, Vector3 bodyForward, Vector3 exitDirection, Quaternion camRotation,
        Vector3 up, Vector3 camOrigin, out Vector3 heading, out string note, out float centreDistance)
    {
        heading = Vector3.zero;
        note = null;
        centreDistance = 0f;
        if (!frameOutPassByViewer || instance == null)
        {
            return false;
        }

        Camera cam = GetViewCamera();
        Vector3 eye = cam != null ? cam.transform.position : camOrigin;
        List<Vector3> points = frameOutPassPoints;
        points.Clear();
        Animator animator = instance.GetComponentInChildren<Animator>(true);
        List<KeyValuePair<string, Transform>> bones = ResolveProjectionBones(instance, animator);
        Vector3 sum = Vector3.zero;
        for (int i = 0; i < bones.Count; i++)
        {
            if (bones[i].Value != null)
            {
                Vector3 p = bones[i].Value.position - eye;
                points.Add(p);
                sum += p;
            }
        }

        if (points.Count == 0)
        {
            Vector3 center = TryGetRendererWorldBounds(instance, out Bounds bounds) ? bounds.center : instance.transform.position;
            points.Add(center - eye);
            sum = center - eye;
        }

        centreDistance = FrameOutPassCentreGroundDistance(points, up);
        FrameOutPassBy.Result r = FrameOutPassBy.Resolve(points, sum / points.Count, bodyForward, exitDirection, camRotation * Vector3.forward, up,
            frameOutPassMinMeters, Mathf.Max(frameOutPassMinMeters, frameOutPassMaxMeters), frameOutPassMaxTurnDegrees, false);
        note = $"pass-by {r.note} bend {r.bendDegrees:F0}deg pass {FormatPassMeters(r.passMeters)}m body {FormatPassMeters(r.measuredPassMeters)}m " +
               $"nearest {r.nearestStartMeters:F3}m";
        if (!r.ok)
        {
            return false;
        }

        heading = r.heading;
        return true;
    }

    // 端に掛かる前の box の高さの伸び（frameOutPassBySpeedFromApproach の速さ）。box がどの端にも触れていないフレームどうしで測る。間が
    // BodyFrameOutMaxGapFrames（3）フレームまでなら飛んだフレームの数で割る（実機で動画のフレームが飛んでも測れなくならないように。速さの測り方と
    // 同じ）。同じフレームの繰り返しは数えない。frameOutPassByViewer が ON のときだけ呼ばれる（ElseFrameOut・ObserveBodyFrameOutTracked）。
    private void UpdateFrameOutLoom(MetaObj obj, int frame, ref bool hasLoom, ref int loomFrame, ref float loomBBoxH, ref float loomRatePerFrame)
    {
        if (manifest == null || manifest.eye_w <= 0 || manifest.eye_h <= 0 || obj.bboxW == 0 || obj.bboxH == 0 ||
            obj.bboxX <= 0 || obj.bboxY <= 0 || obj.bboxX + obj.bboxW >= manifest.eye_w - 1 || obj.bboxY + obj.bboxH >= manifest.eye_h - 1)
        {
            return;
        }

        if (frame == loomFrame)
        {
            return;
        }

        int gap = frame - loomFrame;
        if (loomFrame >= 0 && gap > 0 && gap <= BodyFrameOutMaxGapFrames && loomBBoxH > 0f)
        {
            float rate = Mathf.Log(obj.bboxH / loomBBoxH) / gap;
            loomRatePerFrame = hasLoom ? Mathf.Lerp(loomRatePerFrame, rate, FrameOutPassByLoomSmoothing) : rate;
            hasLoom = true;
        }

        loomFrame = frame;
        loomBBoxH = obj.bboxH;
    }

    // 測った box の高さの伸び（/秒）。測ってから FrameOutPassByMaxAgeSeconds 以内で、伸びている（近づいている）ときだけ true。
    private static bool TryGetFrameOutApproachRate(bool hasLoom, int loomFrame, float loomRatePerFrame, int frame, float fps, out float growthPerSecond)
    {
        growthPerSecond = 0f;
        int maxAge = Mathf.Max(1, Mathf.RoundToInt(FrameOutPassByMaxAgeSeconds * Mathf.Max(1f, fps)));
        int age = frame - loomFrame;
        if (!hasLoom || loomFrame < 0 || age < 0 || age > maxAge)
        {
            return false;
        }

        growthPerSecond = loomRatePerFrame * Mathf.Max(1f, fps);
        return growthPerSecond > 0f;
    }

    // 点（目からの位置）の中心の、地面の上の距離。
    private static float FrameOutPassCentreGroundDistance(List<Vector3> pointsFromEye, Vector3 up)
    {
        if (pointsFromEye == null || pointsFromEye.Count == 0)
        {
            return 0f;
        }

        Vector3 c = Vector3.zero;
        for (int i = 0; i < pointsFromEye.Count; i++)
        {
            c += pointsFromEye[i];
        }

        return Vector3.ProjectOnPlane(c / pointsFromEye.Count, up).magnitude;
    }

    // 続きの tick ごとの位置（logFrameOutPassByFrames）。目（見ているカメラ）から見た箱の中心と、箱の角のうち目にいちばん近い距離。
    // frameOutPassByViewer が OFF でも出す（直す前の撮影の道筋を同じ形で描くため）。
    private void LogFrameOutPassByStep(string kind, uint trackId, int frame, float elapsed, GameObject instance, Vector3 camOrigin, Quaternion camRotation)
    {
        if (!logFrameOutPassByFrames || instance == null)
        {
            return;
        }

        Camera cam = GetViewCamera();
        Vector3 eye = cam != null ? cam.transform.position : camOrigin;
        Quaternion view = cam != null ? cam.transform.rotation : camRotation;
        Quaternion toView = Quaternion.Inverse(view);
        frameOutPassStepPoints.Clear();
        CollectFrameOutPassBoxCorners(instance, eye, Vector3.zero, frameOutPassStepPoints);
        Vector3 center = Vector3.zero;
        float nearest = float.PositiveInfinity;
        for (int i = 0; i < frameOutPassStepPoints.Count; i++)
        {
            center += frameOutPassStepPoints[i];
            nearest = Mathf.Min(nearest, frameOutPassStepPoints[i].magnitude);
        }

        if (frameOutPassStepPoints.Count > 0)
        {
            center /= frameOutPassStepPoints.Count;
        }

        Vector3 local = toView * center;
        Debug.Log($"[PASSBY] step kind={kind} track={trackId} f={frame} elapsed={elapsed:F3} centreView=({local.x:F3},{local.y:F3},{local.z:F3}) " +
                  $"angle={Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg:F1}deg nearestCorner={(float.IsPositiveInfinity(nearest) ? "-" : nearest.ToString("F3"))}m " +
                  $"active={instance.activeInHierarchy}");
    }

    // renderer ごとのローカル箱の 8 角（world、目から、shift だけずらす）。
    private static void CollectFrameOutPassBoxCorners(GameObject instance, Vector3 eye, Vector3 shift, List<Vector3> points)
    {
        Renderer[] renderers = instance.GetComponentsInChildren<Renderer>(true);
        for (int r = 0; r < renderers.Length; r++)
        {
            Renderer renderer = renderers[r];
            if (renderer == null || !renderer.enabled || !TryGetRendererLocalBounds(renderer, out Bounds local))
            {
                continue;
            }

            Matrix4x4 toWorld = renderer.localToWorldMatrix;
            Vector3 c = local.center;
            Vector3 e = local.extents;
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = toWorld.MultiplyPoint3x4(c + new Vector3(
                    ((i & 1) == 0 ? -e.x : e.x),
                    ((i & 2) == 0 ? -e.y : e.y),
                    ((i & 4) == 0 ? -e.z : e.z)));
                points.Add(corner + shift - eye);
            }
        }
    }

    private static void GetFrameOutPassExtentOnCamera(List<Vector3> points, Vector3 camRight, Vector3 camForward,
        out float minX, out float maxX, out float minZ, out float maxZ)
    {
        minX = float.PositiveInfinity;
        maxX = float.NegativeInfinity;
        minZ = float.PositiveInfinity;
        maxZ = float.NegativeInfinity;
        for (int i = 0; i < points.Count; i++)
        {
            float x = Vector3.Dot(points[i], camRight);
            float z = Vector3.Dot(points[i], camForward);
            minX = Mathf.Min(minX, x);
            maxX = Mathf.Max(maxX, x);
            minZ = Mathf.Min(minZ, z);
            maxZ = Mathf.Max(maxZ, z);
        }
    }

    // 画面の向き（pinhole の基準）で見た yaw（0 = 画面の奥、+90 = 右、±180 = 視聴者の方）。
    private static float YawOnCamera(Vector3 v, Vector3 camRight, Vector3 camForward)
    {
        return Mathf.Atan2(Vector3.Dot(v, camRight), Vector3.Dot(v, camForward)) * Mathf.Rad2Deg;
    }

    private static string FormatPassMeters(float meters)
    {
        return float.IsPositiveInfinity(meters) ? "inf" : meters.ToString("F3");
    }
}
