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
//      0.3〜2.5 倍/秒に切る）。上下の端は world の上下に使わない。人は歩きのクリップ（humanWalkClips、進む速さに合わせて速さを変える）
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
                st.measureFrame = -1;
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

        st.measureFrame = frame;
        st.measurePosition = position;
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
            CaptureHumanoidBoneLocalRotations(instance, st.startBoneLocalRotations);
            st.hasBodyAnchor = TryGetHumanBodyAnchor(instance, Vector3.up, out st.startHips, out st.groundHeight);
            StartBodyFrameOutClip(st, instance);
        }

        st.active = true;
        st.ended = false;
        if (logBodyFrameOut)
        {
            Debug.Log($"[BODYFRAMEOUT] start f={frame} track={trackId} model={instance.name} reason={reason} side={st.lastClipSide} " +
                      $"v=({velocity.x:F3},{velocity.y:F3},{velocity.z:F3})m/s speed={speed:F3}m/s ({(measured ? "measured" : "edge")}) " +
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
            if (interactiveMotionByTrack.TryGetValue(trackId, out InteractiveMotionState motion) && motion != null && motion.hasCachedAnimalPose)
            {
                AnimalPoseWorldData pose = RemapAnimalPoseRigid(motion.cachedAnimalPose, motion.cachedAnimalBasePosition, motion.cachedAnimalBaseRotation, position, rotation);
                float loop = 0f;
                if (st.animalWalkClip != null)
                {
                    float duration = Mathf.Max(0.0001f, st.animalWalkClip.duration);
                    loop = (t % duration) / duration;
                }

                ApplyAnimalPoseRequest(instance, pose, motion.cachedAnimalHasSmalPose, motion.cachedAnimalSmalPose, position, rotation,
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
