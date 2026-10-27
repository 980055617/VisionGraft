using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

// 人・動物の frame out 継続（2026-10-24 ユーザー「画面の外へ出た後も進み続ける…いいですよ，それがほかの動画でもできるように
// なっているのが一番いい，女お化けの動画も出てから進んでほしい」）。Else の frame out 継続（ElseFrameOut.partial.cs、車が画面の端から
// 出た後も少し進んでから消える）と同じ「配置」の機能で、モデルが出る条件ではすべて効く（動作の条件 enableInteractiveMotion には
// 結び付けない）。被験者実験の記録（ExperimentLog）には何も出さない（車と同じ。[BODYFRAMEOUT] のログだけ）。
//
// bundle は画面の外を表さない（bbox は可視部分だけ。出ていく最後の数フレームは生成側が anchor を止める = hold）。そのまま置くと、
// モデルは端で立ち止まり（テレビの女性 f569〜579 の 11 フレーム、犬の動画の犬 f682〜698 の 17 フレーム）、track が消えた瞬間に消えた。
//   1. 測る: 左右に見切れていない・anchor が動いているフレームで、追従で置いた root の速さ（EMA、m/秒）
//   2. 始める: 左右の端に掛かった track の anchor が止まった（前のフレームと同じ）最初のフレーム。hold の無いまま消えたときは消えた直後
//   3. 進める: 始めた位置から地面の上を、測った速度で進める（出ていく端の外向きと揃っていなければ端の外向き、速さは表示した身長の
//      0.3〜2.5 倍/秒に切る）。
//      bodyFrameOutFollowBodyFacing（既定 OFF・試作、2026-10-25）なら、向きは測った速度ではなく端に掛かる前の体の前（フィールドの説明）。上下の端は world の上下に使わない。人は歩きのクリップ（humanWalkClips、進む速さに合わせて速さを変える）
//      を回して進む向きへ向き直り（始めの 0.3 秒で向きと姿勢を混ぜる）、体の Hips と足の高さを始めの体に合わせる。動物は向きを変えず、
//      最後の追従の姿勢に歩きのクリップ（animalWalkClips）を重ねる
//   4. 終える: 視界から映らなくなったら非表示（車と同じ IsCoastingInstanceInView）。安全弁は 180 フレーム・表示した身長の 6 倍
//   5. 戻る: track の anchor がまた動いたら（入ってきた・周回・シーク）、続きを捨てて追従へ（混ぜない）
//   6. 本当に出ていく出口だけ（meta を先読み）: 止まってから bodyFrameOutLeaveWithinSeconds（1.5 秒）のうちに track が消え、
//      消えてから bodyFrameOutReturnSkipSeconds（3 秒）のうちに戻らない。端で立ち止まって anchor が同じになるだけの track
//      （旧実験の動物で 46 か所）や、すぐ戻る出口（練習の犬）では始めない（前と同じく端で消えて戻る）
// 再出現の地点へ歩いて戻る動き（既存の system frame-out、表示する track を指定したバッチだけで走る）はしない。
public partial class StreamingStereoVideoPlayer
{
    [Header("Person / Animal frame-out continuation (2026-10-24)")]
    public bool enableBodyFrameOutContinuation = true;
    [Min(1)] public int bodyFrameOutMaxFrames = 180;
    [Min(0.5f)] public float bodyFrameOutMaxHeights = 6f;
    // この秒数のうちに同じ track がまた映るなら（ちょっと出てすぐ戻る。練習の犬は f77 で右へ出て f102 に戻る）、続きは始めない。
    // 外へ歩き続けた後に戻る地点へ跳ぶより、前と同じく端で消えて戻るほうが自然なため。
    [Min(0f)] public float bodyFrameOutReturnSkipSeconds = 3f;
    // 端で anchor が止まってから、この秒数のうちに track が消える出口だけで始める（本当に出ていく。実験の 3 つの出口は 5〜13 フレームで消える）。
    [Min(0.1f)] public float bodyFrameOutLeaveWithinSeconds = 1.5f;
    public bool logBodyFrameOut = true;
    // 2026-10-25（実機のユーザー「テレビ frame out するときは左に今行っているけど，もっとユーザーの隣を通り過ぎるように，まっすぐに歩いてほしい」）:
    // 進む向きを、測った root の速さの向きではなく、端に掛かる前の体の前（地面の上、速さと同じフレームで EMA）にする。
    // テレビの女性は視聴者の方へ歩いて左の端から出るが（SMPL の向き -163°、transl の z は f490〜568 で 80 → 34）、端に掛かる前の root の速さは
    // 横の成分しか無く（10/24 の記録 v = (-0.101, 0, -0.003) m/s）、0.3 秒で 71° 左へ向き直って横へ歩いていた。
    // 体の点（投影に使う骨）がどれも目から bodyFrameOutClearanceMeters より近くを通らないよう、足りなければ出ていく側へ最小の角だけ回す
    // （目のまわりの円の接線）。もうその円にいる体（犬の動画の犬）はほぼ横向きのまま。速さ・始める条件・終わり方・人の歩きのクリップ・
    // 動物の向きは変えない。OFF なら今の向き（測った速さ・端の外向き）。調査役 Q2 の案、確かめ役 V_Q2 の指摘で既定 OFF
    // （Docs/tmp/survey_20261025/README.md「Q2」）。確かめ役: 進む向きを実際に決めるのは体の向きではなく下の離す距離で、0.25 m では今
    // （一番近い骨 0.311 m）より近くを通る。離す距離はユーザーの判断（0.20 m 以下 = まっすぐ・一番近い骨 0.21〜0.23 m、0.25 m = 5〜11° 曲がる、
    // 0.30 m = 22〜32° 曲がる、0.35 m = 今の横歩きに戻る）。
    // 2026-10-27 から既定 ON（見る用の版 l の中身を既定にした）。上の既定の説明はそれまでの話。
    public bool bodyFrameOutFollowBodyFacing = true;
    // 上の目から離す距離（m、地面の上）。既定は ⑧ が体を置く近さの下限 MinDistanceFromHeadMeters と同じ 0.25 m。
    [Min(0f)] public float bodyFrameOutClearanceMeters = 0.25f;
    // 2026-10-25（調査役 O3、既定 OFF）: 動物の続きで毎 tick 解き直す姿勢を、最後に追従した tick の姿勢（端で anchor が止まった hold の間の
    // フレーム）ではなく、速さを測った最後のフレーム（左右に見切れず anchor が動いている = 端に掛かる前。速さと同じフレーム）の追従の姿勢にする。
    // 犬の動画の犬は出口の直前 f684 の SMAL の根の向きだけが 1 フレーム 142° 跳ね（f683 と f685 の差は 22°）、今の続きはその姿勢を毎 tick FK で
    // 解き直すので、根の平滑（smalSmoothHalfLifeSec 0.12 s）で体が 0.5 秒で約 130° 回り頭が 26° 上がって、横へ滑りながら跳んでいるように見えた。
    // root の向き（animal-keep）・進む向き・速さ・始める条件・終わり方は変えない（10/24 に入れなかった「進む向きへ root を回す」案とは別）。
    // OFF なら今のまま（最後に追従した tick の姿勢）。測ったフレームが無いとき、続きを始めたフレームより bodyFrameOutLeaveWithinSeconds より
    // 前のとき（長く見切れ・hold が続いた後の古い姿勢。確かめ役 V_O3 の指摘）も今のまま。
    // 2026-10-27 から既定 ON（見る用の版 l の中身を既定にした）。上の既定の説明はそれまでの話。
    public bool bodyFrameOutAnimalPoseFromMeasuredFrame = true;

    private const float BodyFrameOutVelocitySmoothing = 0.3f;
    private const float BodyFrameOutBlendSeconds = 0.3f;
    // 人の歩きのクリップの速さ: 身長の 0.75 倍/秒で進むときに 1 倍（Mixamo の歩きの目安）。進む速さに比例させ、足が滑らないようにする。
    private const float BodyFrameOutNaturalWalkHeightsPerSecond = 0.75f;
    private const int BodyFrameOutEdgeMarginPx = 2;
    private const int BodyFrameOutMaxGapFrames = 3;

    private sealed class BodyFrameOutState
    {
        public byte categoryId;
        // anchor の hold の判定（動画のフレームごと）
        public int anchorFrame = -1;
        public ushort anchorU;
        public ushort anchorV;
        public float anchorZ01;
        public bool heldNow;
        // 端に掛かる前の動き（追従で置いた root、m/秒）
        public int measureFrame = -1;
        public Vector3 measurePosition;
        public bool hasVelocity;
        public Vector3 velocity;
        // 端に掛かる前の体の前（地面の上の単位ベクトル、速さと同じフレームで EMA。bodyFrameOutFollowBodyFacing）
        public bool hasBodyForward;
        public Vector3 bodyForward;
        // 速さを測った最後のフレームの追従の姿勢（動物、bodyFrameOutAnimalPoseFromMeasuredFrame）。InteractiveMotion の控え（cachedAnimalPose）の写し。
        public bool hasMeasuredAnimalPose;
        public int measuredAnimalPoseFrame = -1;
        public AnimalPoseWorldData measuredAnimalPose;
        public Vector3 measuredAnimalBasePosition;
        public Quaternion measuredAnimalBaseRotation = Quaternion.identity;
        public bool measuredAnimalHasSmalPose;
        public AnimalSmalPose measuredAnimalSmalPose;
        // 端に掛かる前の box の高さの伸び（frameOutPassByViewer の frameOutPassBySpeedFromApproach。どの端にも触れていない続いたフレームの
        // ln(高さの比) の EMA、/ 動画のフレーム）
        public bool hasLoom;
        public int loomFrame = -1;
        public float loomBBoxH;
        public float loomRatePerFrame;
        // 最後に映ったフレーム
        public int lastSeenFrame = -1;
        public int lastClipSide;
        // 続き
        public bool active;
        public bool ended;
        // この出口で始めてよいか（ShouldStartBodyFrameOut の先読み、動画のフレームごとに 1 回）。
        public int exitDecisionFrame = -1;
        public bool exitAllowed;
        public GameObject instance;
        public int startFrame;
        public double startVideoTime;
        public Vector3 startPosition;
        public Quaternion startRotation = Quaternion.identity;
        public Quaternion facing = Quaternion.identity;
        public Vector3 groundVelocity;
        public float clipRate = 1f;
        public float maxDistance;
        public Vector3 camOrigin;
        public Quaternion camRotation = Quaternion.identity;
        public PlayableGraph graph;
        public AnimationClip clip;
        public Animator animator;
        // 人: 追従は体（Hips）を root から離して置くことがあり、歩きのクリップに替えると Hips が root の上へ戻って体が跳ぶ。
        // 始めの Hips の位置（地面の上）と足の高さに、クリップの姿勢の Hips と足を合わせる。
        public bool hasBodyAnchor;
        public Vector3 startHips;
        public float groundHeight;
        public AnimalGesturePose animalWalkClip;
        public readonly Dictionary<HumanBodyBones, Quaternion> startBoneLocalRotations = new Dictionary<HumanBodyBones, Quaternion>();
    }

    private readonly Dictionary<uint, BodyFrameOutState> bodyFrameOutByTrack = new Dictionary<uint, BodyFrameOutState>();
    private readonly List<uint> bodyFrameOutKeys = new List<uint>();
    private readonly HashSet<uint> bodyFrameOutKeepTracks = new HashSet<uint>();

    // Model の切り替え（既定1 ⇄ 既定2）が作り直さない track（画面の外へ進んでいる最中。車の慣性と同じく、押した瞬間に消さない）。
    private bool IsBodyFrameOutActive(uint trackId)
    {
        return bodyFrameOutByTrack.TryGetValue(trackId, out BodyFrameOutState st) && st != null && st.active;
    }

    // ApplyMetaTarget から（その track がこのフレームに映っている）。インスタンスを取る（GetOrCreateTrackInstance）前に呼ぶ:
    // 続きの最中に Model の切り替えで名前が替わっても、作り直さずに今のモデルを外へ進める（車の慣性と同じ）。
    // true なら追従の配置はしない（続きが置いた・隠した）。
    private bool TryApplyBodyFrameOutWhilePresent(MetaObj target, int frame)
    {
        if (!enableBodyFrameOutContinuation ||
            !(IsCategoryPerson(target.categoryId) || IsCategoryAnimal(target.categoryId)))
        {
            return false;
        }

        trackInstances.TryGetValue(target.trackId, out GameObject instance);

        if (!bodyFrameOutByTrack.TryGetValue(target.trackId, out BodyFrameOutState st))
        {
            st = new BodyFrameOutState();
            bodyFrameOutByTrack[target.trackId] = st;
        }

        st.categoryId = target.categoryId;
        if (frame != st.anchorFrame)
        {
            if (st.anchorFrame >= 0 && frame < st.anchorFrame)
            {
                // 周回・シークで戻った。覚えていた動きと続きを捨てる。
                EndBodyFrameOut(target.trackId, st, false, "frame went back");
                st.ended = false;
                st.exitDecisionFrame = -1;
                st.hasVelocity = false;
                st.hasBodyForward = false;
                st.hasMeasuredAnimalPose = false;
                st.measureFrame = -1;
                st.hasLoom = false;
                st.loomFrame = -1;
            }

            st.heldNow = st.anchorFrame >= 0 && frame == st.anchorFrame + 1 &&
                         target.anchorU == st.anchorU && target.anchorV == st.anchorV && target.anchorZ01 == st.anchorZ01;
            st.anchorFrame = frame;
            st.anchorU = target.anchorU;
            st.anchorV = target.anchorV;
            st.anchorZ01 = target.anchorZ01;
        }

        int clipSide = ResolveBodyFrameOutClipSide(target);
        st.lastSeenFrame = frame;
        st.lastClipSide = clipSide;

        if (st.active)
        {
            if (instance == null || st.instance != instance)
            {
                // 続きの途中でモデルが作り直された（ピッカー）。続きは捨て、新しいモデルは追従に任せる。
                EndBodyFrameOut(target.trackId, st, false, "model recreated");
                return false;
            }

            if (!st.heldNow)
            {
                // anchor がまた動いた（入ってきた・hold が解けた）= 本物の追従のデータ。続きを捨てて追従へ（混ぜない）。
                EndBodyFrameOut(target.trackId, st, false, "anchor moves again");
                st.ended = false;
                return false;
            }

            ApplyBodyFrameOutStep(target.trackId, st, instance, frame);
            return true;
        }

        if (st.ended)
        {
            if (!st.heldNow)
            {
                st.ended = false;
                return false;
            }

            // 視界の外まで進んで消した後も、まだ端で hold されている間は出さない。
            if (instance != null)
            {
                SceneObjectWriter.ApplyActive(instance, false);
            }

            return true;
        }

        if (instance != null && clipSide != 0 && st.heldNow && st.hasVelocity && IsInteractiveMotionInactive(target.trackId) &&
            ShouldStartBodyFrameOut(target.trackId, st, frame) &&
            StartBodyFrameOut(target.trackId, st, instance, frame, "anchor hold at the edge"))
        {
            ApplyBodyFrameOutStep(target.trackId, st, instance, frame);
            return true;
        }

        return false;
    }

    // ApplyMetaTarget の追従の配置の後（ObserveInteractiveMotionDisplayedRoot と同じ所）。端に掛かる前の動きを測る。
    private void ObserveBodyFrameOutTracked(MetaObj target, GameObject instance, int frame)
    {
        if (!enableBodyFrameOutContinuation || instance == null ||
            !bodyFrameOutByTrack.TryGetValue(target.trackId, out BodyFrameOutState st) || st == null || st.active)
        {
            return;
        }

        if (st.lastClipSide != 0 || st.heldNow || frame == st.measureFrame)
        {
            return;
        }

        Vector3 position = instance.transform.position;
        if (st.measureFrame >= 0 && frame > st.measureFrame && frame - st.measureFrame <= BodyFrameOutMaxGapFrames)
        {
            Vector3 v = (position - st.measurePosition) * (ResolveMetaFps() / (frame - st.measureFrame));
            st.velocity = st.hasVelocity ? Vector3.Lerp(st.velocity, v, BodyFrameOutVelocitySmoothing) : v;
            st.hasVelocity = true;
        }

        // frameOutPassByViewer（2026-10-26）も体の前から始めるので、どちらかが ON なら測る。
        if ((bodyFrameOutFollowBodyFacing || frameOutPassByViewer) && TryResolveBodyForwardOnGround(instance, st.categoryId, out Vector3 bodyForward))
        {
            Vector3 f = st.hasBodyForward ? Vector3.Lerp(st.bodyForward, bodyForward, BodyFrameOutVelocitySmoothing) : bodyForward;
            st.hasBodyForward = f.sqrMagnitude > 0.000001f;
            st.bodyForward = st.hasBodyForward ? f.normalized : Vector3.zero;
        }

        // frameOutPassByViewer（2026-10-26）: 端に掛かる前の box の高さの伸び（frameOutPassBySpeedFromApproach の速さ）。
        if (frameOutPassByViewer)
        {
            UpdateFrameOutLoom(target, frame, ref st.hasLoom, ref st.loomFrame, ref st.loomBBoxH, ref st.loomRatePerFrame);
        }

        // 同じ tick の追従（TryApplyAnimalPosePipeline の CacheLiveAnimalPoseForInteractiveMotion）が控えた姿勢を写す。配列は写す（控えは tick ごとに差し替わる）。
        if (bodyFrameOutAnimalPoseFromMeasuredFrame && IsCategoryAnimal(st.categoryId) &&
            interactiveMotionByTrack.TryGetValue(target.trackId, out InteractiveMotionState measuredMotion) && measuredMotion != null &&
            measuredMotion.hasCachedAnimalPose)
        {
            st.hasMeasuredAnimalPose = true;
            st.measuredAnimalPoseFrame = frame;
            st.measuredAnimalPose = CopyBodyFrameOutAnimalPose(measuredMotion.cachedAnimalPose);
            st.measuredAnimalBasePosition = measuredMotion.cachedAnimalBasePosition;
            st.measuredAnimalBaseRotation = measuredMotion.cachedAnimalBaseRotation;
            st.measuredAnimalHasSmalPose = measuredMotion.cachedAnimalHasSmalPose;
            st.measuredAnimalSmalPose = CopyBodyFrameOutSmalPose(measuredMotion.cachedAnimalSmalPose);
        }

        st.measureFrame = frame;
        st.measurePosition = position;
    }

    private static AnimalPoseWorldData CopyBodyFrameOutAnimalPose(AnimalPoseWorldData pose)
    {
        AnimalPoseWorldData copy = pose;
        copy.jointsWorld = pose.jointsWorld != null ? (Vector3[])pose.jointsWorld.Clone() : null;
        copy.jointsCam = pose.jointsCam != null ? (Vector3[])pose.jointsCam.Clone() : null;
        copy.jointVis = pose.jointVis != null ? (byte[])pose.jointVis.Clone() : null;
        return copy;
    }

    private static AnimalSmalPose CopyBodyFrameOutSmalPose(AnimalSmalPose pose)
    {
        AnimalSmalPose copy = pose;
        copy.bodyPose = pose.bodyPose != null ? (Quaternion[])pose.bodyPose.Clone() : null;
        copy.betas = pose.betas != null ? (float[])pose.betas.Clone() : null;
        return copy;
    }

    // 測ったフレームの姿勢を使うか（bodyFrameOutAnimalPoseFromMeasuredFrame）。続きを始めたフレームから bodyFrameOutLeaveWithinSeconds より前の姿勢は使わない。
    private bool UseBodyFrameOutMeasuredAnimalPose(BodyFrameOutState st)
    {
        if (!bodyFrameOutAnimalPoseFromMeasuredFrame || !st.hasMeasuredAnimalPose || st.measuredAnimalPoseFrame < 0)
        {
            return false;
        }

        int maxAge = Mathf.Max(1, Mathf.RoundToInt(bodyFrameOutLeaveWithinSeconds * ResolveMetaFps()));
        int age = st.startFrame - st.measuredAnimalPoseFrame;
        return age >= 0 && age <= maxAge;
    }

    // track が映っていない track の続き（TryApplyAllTracks・TryApplyDisplayedTracks・track が 1 つも無いフレーム）。
    // 進めている track を applied に足す（足した track は HideUnselectedTrackInstances が隠さない）。allowed が null なら全部。
    private void ApplyBodyFrameOutForAbsentTracks(HashSet<uint> applied, int frame, HashSet<uint> allowed)
    {
        if (!enableBodyFrameOutContinuation || bodyFrameOutByTrack.Count == 0)
        {
            return;
        }

        bodyFrameOutKeys.Clear();
        bodyFrameOutKeys.AddRange(bodyFrameOutByTrack.Keys);
        for (int i = 0; i < bodyFrameOutKeys.Count; i++)
        {
            uint trackId = bodyFrameOutKeys[i];
            if (applied.Contains(trackId) || (allowed != null && !allowed.Contains(trackId)))
            {
                continue;
            }

            BodyFrameOutState st = bodyFrameOutByTrack[trackId];
            if (st.lastSeenFrame >= 0 && frame < st.lastSeenFrame)
            {
                EndBodyFrameOut(trackId, st, true, "frame went back");
                st.ended = false;
                continue;
            }

            if (!trackInstances.TryGetValue(trackId, out GameObject instance) || instance == null)
            {
                EndBodyFrameOut(trackId, st, false, "no instance");
                continue;
            }

            if (!st.active)
            {
                // hold の無いまま端から消えた（消えた直後だけ）。
                bool justLeftByEdge = !st.ended && st.lastClipSide != 0 && st.hasVelocity && st.lastSeenFrame >= 0 &&
                                      frame - st.lastSeenFrame <= BodyFrameOutMaxGapFrames && instance.activeSelf &&
                                      IsInteractiveMotionInactive(trackId) && ShouldStartBodyFrameOut(trackId, st, st.lastSeenFrame);
                if (!justLeftByEdge || !StartBodyFrameOut(trackId, st, instance, frame, "track ended at the edge"))
                {
                    continue;
                }
            }
            else if (st.instance != instance)
            {
                EndBodyFrameOut(trackId, st, false, "model recreated");
                continue;
            }

            if (ApplyBodyFrameOutStep(trackId, st, instance, frame))
            {
                applied.Add(trackId);
            }
        }
    }

    private bool StartBodyFrameOut(uint trackId, BodyFrameOutState st, GameObject instance, int frame, string reason)
    {
        if (!TryResolveElseFrameOutBasis(st.anchorU, out Transform screen, out Vector3 camOrigin, out Quaternion camRotation, out _, out _))
        {
            return false;
        }

        Vector3 up = screen != null && screen.up.sqrMagnitude > 0.000001f ? screen.up.normalized : Vector3.up;
        Vector3 exitDirection = Vector3.ProjectOnPlane(camRotation * Vector3.right * st.lastClipSide, up);
        if (exitDirection.sqrMagnitude <= 0.000001f)
        {
            return false;
        }

        exitDirection.Normalize();
        float height = TryGetRendererWorldBounds(instance, out Bounds bounds) ? Mathf.Max(0.05f, bounds.size.y) : 0.5f;
        Vector3 ground = Vector3.ProjectOnPlane(st.velocity, up);
        bool measured = st.hasVelocity && ground.magnitude > 0.1f * height && Vector3.Dot(ground.normalized, exitDirection) > 0.2f;
        Vector3 velocity = measured ? ground : exitDirection * (0.8f * height);
        float speed = Mathf.Clamp(velocity.magnitude, 0.3f * height, 2.5f * height);
        velocity = velocity.normalized * speed;
        string headingNote = measured ? "measured" : "edge";
        // frameOutPassByViewer（2026-10-26、既定 OFF、FrameOutPassBy.partial.cs）: 体の前を、視聴者の横を通る向きへ曲げる（速さはそのまま。
        // frameOutPassBySpeedFromApproach のときだけ、端に掛かる前の box の高さの伸びから決めた速さが今より速ければ上げる）。
        // 曲げられないとき（回す角が frameOutPassMaxTurnDegrees を超える・体がもう frameOutPassMinMeters より近い）は今の向き（下の 2 つ）。
        string passByNote = null;
        bool passByUsed = false;
        float passByCentreDistance = 0f;
        if (frameOutPassByViewer && st.hasBodyForward &&
            TryResolveBodyFrameOutPassByHeading(instance, st.bodyForward, exitDirection, camRotation, up, camOrigin, out Vector3 passByHeading,
                out passByNote, out passByCentreDistance))
        {
            passByUsed = true;
            if (frameOutPassBySpeedFromApproach &&
                TryGetFrameOutApproachRate(st.hasLoom, st.loomFrame, st.loomRatePerFrame, frame, ResolveMetaFps(), out float growthPerSecond))
            {
                float approach = Mathf.Min(passByCentreDistance * growthPerSecond, 2.5f * height);
                if (approach > speed)
                {
                    passByNote += $" speed {speed:F3}->{approach:F3}m/s (approach, growth {growthPerSecond:F2}/s)";
                    speed = approach;
                }
            }

            velocity = passByHeading * speed;
            headingNote = passByNote;
        }
        else if (bodyFrameOutFollowBodyFacing && st.hasBodyForward &&
            TryResolveBodyFrameOutPassHeading(instance, st.bodyForward, exitDirection, up, camOrigin, out Vector3 bodyHeading, out string passNote))
        {
            velocity = bodyHeading * speed;
            headingNote = passByNote != null ? passNote + " (" + passByNote + ")" : passNote;
        }
        else if (passByNote != null)
        {
            headingNote += " (" + passByNote + ")";
        }

        bool isAnimal = IsCategoryAnimal(st.categoryId);
        st.clipRate = Mathf.Clamp(speed / (BodyFrameOutNaturalWalkHeightsPerSecond * height), 0.4f, 1.6f);
        st.instance = instance;
        st.startFrame = frame;
        st.startVideoTime = ResolveBodyFrameOutVideoTime(frame);
        st.startPosition = instance.transform.position;
        st.startRotation = instance.transform.rotation;
        st.groundVelocity = velocity;
        // 人は root が画面の向きのまま（体の向きは FK の Hips）なので、歩きのクリップの前（+Z）を進む向きへ回す。
        // 動物は回さない: 体の向きは最後の追従の姿勢が持っている。回して揃える案は 10/24 に試して入れなかった（鼻の向き・骨の位置で
        // 測って root を回すと、Meshy の犬は後ろ向きに歩き、ラブラドールは SMAL の向きの平滑と食い違って途中で右を向いてから正面に戻った）。
        st.facing = isAnimal ? st.startRotation : Quaternion.LookRotation(velocity.normalized, up);
        string turnNote = isAnimal ? "animal-keep" : "human-face-travel";
        st.maxDistance = height * Mathf.Max(0.5f, bodyFrameOutMaxHeights);
        st.camOrigin = camOrigin;
        st.camRotation = camRotation;
        st.startBoneLocalRotations.Clear();
        st.clip = null;
        st.animator = null;
        st.animalWalkClip = null;
        if (isAnimal)
        {
            st.animalWalkClip = PickAnimalWalkClip();
        }
        else
        {
            NoteHumanHipsTrackLocalPosition(instance);
            st.hasBodyAnchor = TryGetHumanBodyAnchor(instance, Vector3.up, out st.startHips, out st.groundHeight);
            // frameOutPassByViewer の決まりを使ったときも視聴者の方へ歩く（体の向きが ±180° をまたぐ）ので、同じく root を先に回す
            // （使わなかったときは今のまま）。
            if (bodyFrameOutFollowBodyFacing || passByUsed)
            {
                // root を最初から進む向きへ回し、Hips の world の回転はそのまま残す（局所を root のぶん逆に回す）。混ぜる 0.3 秒の間に
                // root（今の向き → 進む向き）と Hips の局所（始めの姿勢 → クリップ）が別々に最短の回転を取ると、体の向きが ±180° を
                // またぐとき（正面を向いて近づく人）に体が 1 回転するため。混ぜる間の体の回りは「体の向き − 進む向き」だけになる。
                RotateBodyFrameOutRootKeepingHips(instance, st.facing);
                st.startRotation = instance.transform.rotation;
            }

            CaptureHumanoidBoneLocalRotations(instance, st.startBoneLocalRotations);
            StartBodyFrameOutClip(st, instance);
        }

        st.active = true;
        st.ended = false;
        if (logBodyFrameOut)
        {
            Debug.Log($"[BODYFRAMEOUT] start f={frame} track={trackId} model={instance.name} reason={reason} side={st.lastClipSide} " +
                      $"v=({velocity.x:F3},{velocity.y:F3},{velocity.z:F3})m/s speed={speed:F3}m/s ({headingNote}) " +
                      $"headingYaw={Mathf.Atan2(velocity.x, velocity.z) * Mathf.Rad2Deg:F1} " +
                      $"bodyYaw={(st.hasBodyForward ? (Mathf.Atan2(st.bodyForward.x, st.bodyForward.z) * Mathf.Rad2Deg).ToString("F1") : "-")} " +
                      $"height={height:F3}m facing={turnNote} clip={(st.clip != null ? st.clip.name : (st.animalWalkClip != null ? st.animalWalkClip.name : "-"))}");
        }

        return true;
    }

    // 1 tick 進める。まだ見えていれば true、視界から外れた・安全弁に掛かって隠したら false。
    private bool ApplyBodyFrameOutStep(uint trackId, BodyFrameOutState st, GameObject instance, int frame)
    {
        float t = Mathf.Max(0f, (float)(ResolveBodyFrameOutVideoTime(frame) - st.startVideoTime));
        Vector3 delta = st.groundVelocity * t;
        Vector3 position = st.startPosition + delta;
        float blend = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t / BodyFrameOutBlendSeconds));
        Quaternion rotation = Quaternion.Slerp(st.startRotation, st.facing, blend);
        SceneObjectWriter.ApplyActive(instance, true);

        if (IsCategoryAnimal(st.categoryId))
        {
            TrackPlacementWriter.Apply(instance.transform, new TrackPlacementCommand(position, rotation, instance.transform.localScale));
            interactiveMotionByTrack.TryGetValue(trackId, out InteractiveMotionState motion);
            bool useMeasuredPose = UseBodyFrameOutMeasuredAnimalPose(st);
            if (useMeasuredPose || (motion != null && motion.hasCachedAnimalPose))
            {
                AnimalPoseWorldData basePose = useMeasuredPose ? st.measuredAnimalPose : motion.cachedAnimalPose;
                Vector3 basePosition = useMeasuredPose ? st.measuredAnimalBasePosition : motion.cachedAnimalBasePosition;
                Quaternion baseRotation = useMeasuredPose ? st.measuredAnimalBaseRotation : motion.cachedAnimalBaseRotation;
                bool baseHasSmal = useMeasuredPose ? st.measuredAnimalHasSmalPose : motion.cachedAnimalHasSmalPose;
                AnimalSmalPose baseSmal = useMeasuredPose ? st.measuredAnimalSmalPose : motion.cachedAnimalSmalPose;
                AnimalPoseWorldData pose = RemapAnimalPoseRigid(basePose, basePosition, baseRotation, position, rotation);
                float loop = 0f;
                if (st.animalWalkClip != null)
                {
                    float duration = Mathf.Max(0.0001f, st.animalWalkClip.duration);
                    loop = (t % duration) / duration;
                }

                ApplyAnimalPoseRequest(instance, pose, baseHasSmal, baseSmal, position, rotation,
                    GetRuntimeTickContext(), st.animalWalkClip, loop);
            }
        }
        else
        {
            if (st.graph.IsValid() && st.clip != null)
            {
                Transform animatorTransform = st.animator != null ? st.animator.transform : null;
                Vector3 localPosition = animatorTransform != null ? animatorTransform.localPosition : Vector3.zero;
                Quaternion localRotation = animatorTransform != null ? animatorTransform.localRotation : Quaternion.identity;
                double clipTime = st.clip.length > 0.0001f ? (t * st.clipRate) % st.clip.length : 0d;
                st.graph.GetRootPlayable(0).SetTime(clipTime);
                st.graph.Evaluate(0f);
                if (ShouldRestoreAnimatorLocalTransformSeparately(animatorTransform, instance.transform))
                {
                    TransformWriter.ApplyLocalPose(animatorTransform, localPosition, localRotation);
                }

                // humanHandRollFixMode（既定 0 = OFF、2026-10-25）: 歩きのクリップの手にも追従の手と同じ回転を当てる。混ぜの前に当て、混ぜの両端
                // （追従の最後の姿勢・クリップの姿勢）をどちらも回した姿勢にする。OFF なら何もしない。
                ApplyHumanHandRollFixAfterClip(st.animator);

                if (blend < 1f && st.startBoneLocalRotations.Count > 0)
                {
                    // 始めの 0.3 秒は、追従の最後の姿勢から歩きのクリップの姿勢へ混ぜる。
                    BlendHumanoidBoneLocalRotations(instance, st.startBoneLocalRotations, blend);
                }
            }

            TrackPlacementWriter.Apply(instance.transform, new TrackPlacementCommand(position, rotation, instance.transform.localScale));
            if (st.hasBodyAnchor && TryGetHumanBodyAnchor(instance, Vector3.up, out Vector3 hips, out float feet))
            {
                // root を、この姿勢の Hips が「始めの Hips + 進んだ分」の真上に、足が始めの足の高さに来るようにずらす。
                Vector3 horizontal = Vector3.ProjectOnPlane((st.startHips + delta) - hips, Vector3.up);
                Vector3 correction = horizontal + Vector3.up * (st.groundHeight - feet);
                TrackPlacementWriter.Apply(instance.transform, new TrackPlacementCommand(instance.transform.position + correction, rotation, instance.transform.localScale));
            }
        }

        LogFrameOutPassByStep("body", trackId, frame, t, instance, st.camOrigin, st.camRotation);
        int elapsedFrames = frame - st.startFrame;
        string endReason = null;
        if (elapsedFrames >= 2 && !IsCoastingInstanceInView(instance, st.camOrigin, st.camRotation))
        {
            endReason = "out of view";
        }
        else if (elapsedFrames > bodyFrameOutMaxFrames)
        {
            endReason = "max frames";
        }
        else if (delta.magnitude > st.maxDistance)
        {
            endReason = "max distance";
        }

        if (endReason == null)
        {
            return true;
        }

        EndBodyFrameOut(trackId, st, true, endReason);
        return false;
    }

    private void StartBodyFrameOutClip(BodyFrameOutState st, GameObject instance)
    {
        AnimationClip clip = humanWalkClips != null && humanWalkClips.Length > 0 ? humanWalkClips[0] : null;
        Animator animator = instance.GetComponentInChildren<Animator>();
        if (clip == null || animator == null || !animator.isHuman)
        {
            return;
        }

        SceneObjectWriter.ApplyAnimatorEnabled(animator, true);
        SceneObjectWriter.ApplyRootMotion(animator, false);
        PlayableGraph graph = PlayableGraph.Create("BodyFrameOut_" + instance.GetInstanceID());
        graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
        AnimationClipPlayable playable = AnimationClipPlayable.Create(graph, clip);
        playable.SetApplyFootIK(false);
        playable.SetApplyPlayableIK(false);
        AnimationPlayableOutput output = AnimationPlayableOutput.Create(graph, "Animation", animator);
        output.SetSourcePlayable(playable);
        SceneObjectWriter.ApplyPlay(graph);
        st.graph = graph;
        st.clip = clip;
        st.animator = animator;
    }

    // hide: 置いていたモデルを隠す（視界の外・安全弁・映っていないのに戻った）。false はそのまま（追従が置き直す・作り直された）。
    private void EndBodyFrameOut(uint trackId, BodyFrameOutState st, bool hide, string reason)
    {
        if (st == null || !st.active)
        {
            return;
        }

        if (st.graph.IsValid())
        {
            st.graph.Destroy();
        }

        // 歩きのクリップのために有効にした Animator は無効に戻す（人の Animator は再生しない決まり。追従の経路も毎 tick 無効にする）。
        if (st.animator != null)
        {
            SceneObjectWriter.ApplyAnimatorEnabled(st.animator, false);
        }

        if (hide && st.instance != null)
        {
            SceneObjectWriter.ApplyActive(st.instance, false);
        }

        if (logBodyFrameOut)
        {
            Debug.Log($"[BODYFRAMEOUT] end track={trackId} reason={reason} frames={st.anchorFrame - st.startFrame} " +
                      $"model={(st.instance != null ? st.instance.name : "-")}");
        }

        st.active = false;
        st.ended = true;
        st.instance = null;
        st.clip = null;
        st.animator = null;
        st.animalWalkClip = null;
        st.startBoneLocalRotations.Clear();
    }

    // 本物のカット: 進めている track は隠して、覚えていた動きを捨てる（次の shot の動きと混ぜない）。
    private void ResetBodyFrameOutForShotBoundary()
    {
        foreach (KeyValuePair<uint, BodyFrameOutState> kv in bodyFrameOutByTrack)
        {
            EndBodyFrameOut(kv.Key, kv.Value, true, "shot boundary");
        }

        bodyFrameOutByTrack.Clear();
    }

    private void DisposeBodyFrameOut()
    {
        foreach (KeyValuePair<uint, BodyFrameOutState> kv in bodyFrameOutByTrack)
        {
            if (kv.Value != null && kv.Value.graph.IsValid())
            {
                kv.Value.graph.Destroy();
            }
        }

        bodyFrameOutByTrack.Clear();
    }

    private readonly List<MetaObj> bodyFrameOutScanObjects = new List<MetaObj>(16);

    // この出口で続きを始めてよいか（meta を先読みする。判定は動画のフレームごとに 1 回）:
    //   - 本当に出ていく: bodyFrameOutLeaveWithinSeconds のうちに track が消える。端で立ち止まって anchor が同じになるだけの track
    //     （旧実験の動物は端に大きく写ったまま 1〜8 フレーム止まるのが 46 か所。その後も映り続ける）では始めない
    //   - すぐ戻らない: 消えてから bodyFrameOutReturnSkipSeconds のうちにまた映るなら始めない（練習の犬）
    // 動画の終わりで打ち切る（周回の先頭は見ない。周回では frame が戻ったときに続きを捨てる）。
    private bool ShouldStartBodyFrameOut(uint trackId, BodyFrameOutState st, int frame)
    {
        if (st.exitDecisionFrame == frame)
        {
            return st.exitAllowed;
        }

        st.exitDecisionFrame = frame;
        st.exitAllowed = false;
        int numFrames = (int)metaHeader.numFrames;
        float fps = Mathf.Max(1f, ResolveMetaFps());
        int leaveWindow = Mathf.Max(1, Mathf.RoundToInt(bodyFrameOutLeaveWithinSeconds * fps));
        int returnWindow = Mathf.RoundToInt(bodyFrameOutReturnSkipSeconds * fps);
        if (numFrames <= 0)
        {
            return false;
        }

        int exitFrame = -1;
        for (int f = frame + 1; f < numFrames; f++)
        {
            bool present = IsTrackInFrame(trackId, f);
            if (exitFrame < 0)
            {
                if (!present)
                {
                    exitFrame = f;
                }
                else if (f - frame > leaveWindow)
                {
                    if (logBodyFrameOut)
                    {
                        Debug.Log($"[BODYFRAMEOUT] skip track={trackId} f={frame}: still on screen after {leaveWindow} frames (not leaving)");
                    }

                    return false;
                }

                continue;
            }

            if (present)
            {
                if (logBodyFrameOut)
                {
                    Debug.Log($"[BODYFRAMEOUT] skip track={trackId} f={frame}: comes back at f{f} ({f - exitFrame} frames out)");
                }

                return false;
            }

            if (f - exitFrame > returnWindow)
            {
                break;
            }
        }

        // 動画の終わりまで映り続けるなら、出ていかない。
        st.exitAllowed = exitFrame >= 0 || frame + 1 >= numFrames;
        return st.exitAllowed;
    }

    private bool IsTrackInFrame(uint trackId, int frame)
    {
        if (!TryReadFrameObjects(frame, bodyFrameOutScanObjects))
        {
            return false;
        }

        for (int i = 0; i < bodyFrameOutScanObjects.Count; i++)
        {
            if (bodyFrameOutScanObjects[i].trackId == trackId)
            {
                return true;
            }
        }

        return false;
    }

    // 人の体の基準: Hips の world 位置と、両足（つま先があればつま先も）の一番低い高さ（world の上方向）。
    private static bool TryGetHumanBodyAnchor(GameObject instance, Vector3 up, out Vector3 hips, out float feet)
    {
        hips = Vector3.zero;
        feet = 0f;
        Animator animator = instance != null ? instance.GetComponentInChildren<Animator>() : null;
        if (animator == null || !animator.isHuman)
        {
            return false;
        }

        Transform hipsBone = animator.GetBoneTransform(HumanBodyBones.Hips);
        if (hipsBone == null)
        {
            return false;
        }

        hips = hipsBone.position;
        float lowest = float.MaxValue;
        foreach (HumanBodyBones bone in new[] { HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot, HumanBodyBones.LeftToes, HumanBodyBones.RightToes })
        {
            Transform t = animator.GetBoneTransform(bone);
            if (t != null)
            {
                lowest = Mathf.Min(lowest, Vector3.Dot(t.position, up));
            }
        }

        if (lowest == float.MaxValue)
        {
            return false;
        }

        feet = lowest;
        return true;
    }

    // 体の前（地面の上の単位ベクトル）。人: 左右の太ももの付け根の線に直交する向き Cross(右 − 左, 上)。SMPL の FK では Hips の前
    // （camRotation × globalOrient の前）と同じ（サッカーの 48 フレームで差の中央 0.0°、p10〜p90 ±0.3°）。動物: 後ろ脚の付け根の中点 →
    // 前脚の付け根の中点（役の骨。前脚の役が無いリグは尾の付け根 → 首）。
    private bool TryResolveBodyForwardOnGround(GameObject instance, byte categoryId, out Vector3 forward)
    {
        forward = Vector3.zero;
        if (instance == null)
        {
            return false;
        }

        if (IsCategoryPerson(categoryId))
        {
            Animator animator = instance.GetComponentInChildren<Animator>();
            Transform left = animator != null && animator.isHuman ? animator.GetBoneTransform(HumanBodyBones.LeftUpperLeg) : null;
            Transform right = animator != null && animator.isHuman ? animator.GetBoneTransform(HumanBodyBones.RightUpperLeg) : null;
            if (left == null || right == null)
            {
                return false;
            }

            forward = Vector3.Cross(right.position - left.position, Vector3.up);
        }
        else if (IsCategoryAnimal(categoryId))
        {
            AnimalRigCache cache = animalPoseApplier.PeekAnimalRigCache(instance);
            if (cache == null)
            {
                return false;
            }

            if (cache.leftFrontUpper != null && cache.rightFrontUpper != null && cache.leftRearUpper != null && cache.rightRearUpper != null)
            {
                forward = (cache.leftFrontUpper.position + cache.rightFrontUpper.position - cache.leftRearUpper.position - cache.rightRearUpper.position) * 0.5f;
            }
            else if (cache.tailBase != null && cache.neck != null)
            {
                forward = cache.neck.position - cache.tailBase.position;
            }
            else
            {
                return false;
            }
        }
        else
        {
            return false;
        }

        forward = Vector3.ProjectOnPlane(forward, Vector3.up);
        if (forward.sqrMagnitude <= 0.000001f)
        {
            return false;
        }

        forward.Normalize();
        return true;
    }

    // 人の root を rotation へ回し、Hips の world の回転を保つ（Hips の局所を親の回りのぶん逆に回す）。位置は ApplyBodyFrameOutStep が
    // 毎 tick Hips を「始めの Hips + 進んだ分」へ合わせ直すので、ここでは触らない。
    private static void RotateBodyFrameOutRootKeepingHips(GameObject instance, Quaternion rotation)
    {
        Animator animator = instance != null ? instance.GetComponentInChildren<Animator>() : null;
        Transform hips = animator != null && animator.isHuman ? animator.GetBoneTransform(HumanBodyBones.Hips) : null;
        if (hips == null || hips.parent == null)
        {
            return;
        }

        Quaternion hipsWorld = hips.rotation;
        TrackPlacementWriter.Apply(instance.transform, new TrackPlacementCommand(instance.transform.position, rotation, instance.transform.localScale));
        // root を回した後に Hips の world を回す前の値へ戻す（局所を逆に回すのと同じ結果。ApplyWorldRotation で書く、確かめ役 V_Q2）
        TransformWriter.ApplyWorldRotation(hips, hipsWorld);
    }

    private readonly List<Vector3> bodyFrameOutPassPoints = new List<Vector3>(128);

    // 体の前へまっすぐ進む向き。体の点（⑧ が投影に使う骨。無ければ renderer の箱の中心）がどれも、視聴者の目から地面の上で
    // bodyFrameOutClearanceMeters より近くを通らないようにする。足りない点があれば、出ていく側へ最小の角だけ回す（点ごとに、目のまわりの
    // 半径 c の円への接線まで。2 回繰り返す）。使わない（false = 今の向き）: 体の点がもう円の中・目から遠ざかる向きで出ていく側と逆へ向かう・
    // 回す角が 90° を超える。目は見ているカメラ（実機は HMD、バッチは main カメラ）、取れなければ pinhole の原点。
    private bool TryResolveBodyFrameOutPassHeading(GameObject instance, Vector3 bodyForward, Vector3 exitDirection, Vector3 up, Vector3 camOrigin,
        out Vector3 heading, out string note)
    {
        heading = Vector3.zero;
        note = null;
        Vector3 h = Vector3.ProjectOnPlane(bodyForward, up);
        Vector3 exit = Vector3.ProjectOnPlane(exitDirection, up);
        if (instance == null || h.sqrMagnitude <= 0.000001f || exit.sqrMagnitude <= 0.000001f)
        {
            return false;
        }

        h.Normalize();
        exit.Normalize();
        Camera cam = GetViewCamera();
        Vector3 eye = cam != null ? cam.transform.position : camOrigin;
        float c = Mathf.Max(0f, bodyFrameOutClearanceMeters);
        List<Vector3> points = bodyFrameOutPassPoints;
        points.Clear();
        Animator animator = instance.GetComponentInChildren<Animator>(true);
        List<KeyValuePair<string, Transform>> bones = ResolveProjectionBones(instance, animator);
        for (int i = 0; i < bones.Count; i++)
        {
            if (bones[i].Value != null)
            {
                points.Add(Vector3.ProjectOnPlane(bones[i].Value.position - eye, up));
            }
        }

        if (points.Count == 0)
        {
            Vector3 center = TryGetRendererWorldBounds(instance, out Bounds bounds) ? bounds.center : instance.transform.position;
            points.Add(Vector3.ProjectOnPlane(center - eye, up));
        }

        for (int i = 0; i < points.Count; i++)
        {
            if (points[i].magnitude <= c)
            {
                note = "inside-clearance";
                return false;
            }
        }

        // 出ていく側へ回す向き: up まわりに +ε 回すと h は Cross(up, h) へ動く。
        float sign = Vector3.Dot(Vector3.Cross(up, h), exit) >= 0f ? 1f : -1f;
        float total = 0f;
        for (int pass = 0; pass < 2; pass++)
        {
            bool approaching = false;
            float need = 0f;
            for (int i = 0; i < points.Count; i++)
            {
                Vector3 p = points[i];
                if (Vector3.Dot(p, h) >= 0f)
                {
                    continue; // この点は目から遠ざかる
                }

                approaching = true;
                float d = p.magnitude;
                float phi = Mathf.Asin(Mathf.Clamp01(c / d)) * Mathf.Rad2Deg;
                // 目への向きから体の前までの角（出ていく側を +）。phi 以上なら、この点は目から c 以上離れて出ていく側を通る。
                float angle = Vector3.SignedAngle(-p / d, h, up) * sign;
                if (angle < phi)
                {
                    need = Mathf.Max(need, phi - angle);
                }
            }

            if (!approaching && pass == 0 && Vector3.Dot(h, exit) < 0f)
            {
                note = "away-wrong-side";
                return false; // 目から遠ざかりながら、出ていく端と逆へ向かう
            }

            if (need <= 0f)
            {
                break;
            }

            total += need;
            h = Quaternion.AngleAxis(sign * need, up) * h;
        }

        if (total > 90f)
        {
            note = $"turn {total:F0}deg > 90";
            return false;
        }

        heading = h;
        note = total > 0f ? $"body+pass {total:F0}deg" : "body";
        return true;
    }

    private int ResolveBodyFrameOutClipSide(MetaObj obj)
    {
        if (manifest == null || manifest.eye_w <= 0 || obj.bboxW == 0)
        {
            return 0;
        }

        bool left = obj.bboxX <= BodyFrameOutEdgeMarginPx;
        bool right = obj.bboxX + obj.bboxW >= manifest.eye_w - 1 - BodyFrameOutEdgeMarginPx;
        return left == right ? 0 : (right ? 1 : -1);
    }

    private bool IsInteractiveMotionInactive(uint trackId)
    {
        return !interactiveMotionByTrack.TryGetValue(trackId, out InteractiveMotionState state) || state == null ||
               state.stage == InteractiveEventStage.Inactive;
    }

    // 続きの時刻は動画の時刻（止めれば止まる・72 Hz の tick ごとに滑らかに進む）。動画が無いときはフレームから。
    private double ResolveBodyFrameOutVideoTime(int frame)
    {
        if (vp != null && vp.isPrepared)
        {
            return vp.time;
        }

        return frame / (double)Mathf.Max(1f, ResolveMetaFps());
    }
}
