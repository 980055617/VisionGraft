using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    private const string HumanStaticClipAssetFolder = "Assets/Animations/InteractiveMotion/Human/Static";
    private const string HumanWalkClipAssetFolder = "Assets/Animations/InteractiveMotion/Human/Walk";
    private const string AnimalStaticClipAssetFolder = "Assets/Animations/InteractiveMotion/Animal/Static";
    private const string AnimalWalkClipAssetFolder = "Assets/Animations/InteractiveMotion/Animal/Walk";
    private const float DynamicEventProbability = 0.18f;
    private const float MinWalkDurationSeconds = 1.0f;
    private const float MinGestureDurationSeconds = 1.5f;
    private const float SystemTriggerLoopSeconds = 2.0f;
    private const float AnimalBodyTurnMaxDegrees = 35f;

    private enum InteractiveMotionSubject { Person, Animal, Rigid }
    private enum InteractiveEventKind { Static, Dynamic }
    private enum InteractiveTriggerSource { Random, SystemFrameOut }
    private enum InteractiveDynamicPhase { WalkIn, Gesture, WalkBack }
    private enum InteractiveEventStage { Inactive, Owned, HandoffBlend }
    private enum InteractiveAnimalPreset { FaceViewer, BodyTurnViewer, TailWag, PawWave, DataDrivenClip }
    private enum InteractiveHumanPreset { ClipGesture, FaceViewer }

    public struct AnimalFrameOutLoopPose
    {
        public Vector3 position;
        public Quaternion rotation;
    }

    private sealed class InteractiveMotionState
    {
        public InteractiveEventStage stage = InteractiveEventStage.Inactive;
        public InteractiveEventKind kind;
        public InteractiveTriggerSource triggerSource;
        public InteractiveDynamicPhase dynamicPhase;
        public InteractiveMotionSubject subject;
        public byte lastCategoryId;

        public float nextTriggerTime;
        public float phaseStartTime;
        public float phaseDuration;
        public float handoffStartTime;

        public Vector3 originPosition;
        public Quaternion originRotation = Quaternion.identity;
        public Vector3 phaseFromPosition;
        public Quaternion phaseFromRotation = Quaternion.identity;
        public Vector3 phaseToPosition;
        public Quaternion phaseToRotation = Quaternion.identity;
        public Vector3 gesturePosition;
        public Quaternion gestureRotation = Quaternion.identity;

        public Vector3 handoffFromPosition;
        public Quaternion handoffFromRotation = Quaternion.identity;

        public bool hasLiveSample;
        public Vector3 livePosition;
        public Quaternion liveRotation = Quaternion.identity;
        public float lastGoodBBoxArea;
        public bool hasReliableBBoxThisFrame;
        public Transform lastScreen;
        public Vector2 lastAnchorEyePixel;
        public Vector2 previousAnchorEyePixel;
        public bool hasLastAnchorEyePixel;
        public bool hasPreviousAnchorEyePixel;
        public Rect lastBBoxEye;
        public float lastEyeWidth;
        public float lastEyeHeight;
        public bool hasLastBBoxEye;

        public int triggerStartFrame;
        public float frameInQualityWaitStartTime;
        public int frameInFrame;
        public bool hasFrameInPosition;
        public Vector3 frameInPosition;
        public Vector3 frameOutDirection;

        public AnimationClip humanClip;
        public InteractiveHumanPreset humanPreset;

        public InteractiveAnimalPreset animalPreset;
        public AnimalGesturePose animalGestureClip;
        public AnimalGesturePose animalWalkClip;
        public bool hasCachedAnimalPose;
        public AnimalPoseWorldData cachedAnimalPose;
        public Vector3 cachedAnimalBasePosition;
        public Quaternion cachedAnimalBaseRotation = Quaternion.identity;
        public bool cachedAnimalHasSmalPose;
        public AnimalSmalPose cachedAnimalSmalPose;

        // Else（剛体）の走行イベント。経路は ElseDrivePath、向きは出発時の回転を yaw だけ回す。
        public ElseDrivePath.Path drivePath;
        public float driveSpeed;
        public float driveLastDistance;
        public int driveSegmentLogged = -1;
        public Vector3 driveHeading = Vector3.forward;
        public Vector3 rigidAxleLocal = Vector3.right;
        public float wheelAngleDegrees;
        public readonly List<RigidWheel> rigidWheels = new List<RigidWheel>();

        public readonly Dictionary<HumanBodyBones, Quaternion> fallbackBoneBaseLocalRotations = new Dictionary<HumanBodyBones, Quaternion>();
        public readonly Dictionary<HumanBodyBones, Quaternion> handoffFromBoneLocalRotations = new Dictionary<HumanBodyBones, Quaternion>();
        public readonly Dictionary<Transform, Quaternion> handoffFromAnimalBoneLocalRotations = new Dictionary<Transform, Quaternion>();
    }

    private sealed class InteractiveClipPlayback
    {
        public PlayableGraph graph;
        public AnimationClip clip;
        public Animator animator;
        public bool loop;
    }

    private readonly Dictionary<uint, InteractiveMotionState> interactiveMotionByTrack = new Dictionary<uint, InteractiveMotionState>();
    private readonly Dictionary<uint, InteractiveClipPlayback> interactiveClipPlaybackByTrack = new Dictionary<uint, InteractiveClipPlayback>();

    // --- Live sample + scheduling -------------------------------------------------------

    // A track's detection box routinely collapses for the last few frames before it leaves
    // the visible frame (the detector is only catching a sliver at the edge), and the anchor
    // position becomes unreliable in exactly those frames. Reject a frame from updating the
    // displayed-root sample once its bbox area drops below this fraction of the last accepted
    // (good) bbox area, so a frame-out origin freezes at the last reliable frame instead of a
    // corrupted tail frame.
    private const float BBoxQualityMinAreaRatio = 0.5f;

    private void ObserveInteractiveMotionLiveTrackedSample(uint trackId, MetaObj obj, Transform screen)
    {
        InteractiveMotionState state = GetOrCreateInteractiveMotionState(trackId);
        if (state.hasLastAnchorEyePixel)
        {
            state.previousAnchorEyePixel = state.lastAnchorEyePixel;
            state.hasPreviousAnchorEyePixel = true;
        }
        if (ResolveAnchorToScreen(obj.anchorU, out Transform resolvedScreen, out int uEye, out _))
        {
            state.lastAnchorEyePixel = new Vector2(uEye, obj.anchorV);
            state.hasLastAnchorEyePixel = true;
            state.lastScreen = resolvedScreen;
        }
        if (manifest != null && manifest.eye_w > 0f && manifest.eye_h > 0f)
        {
            state.lastBBoxEye = new Rect(obj.bboxX, obj.bboxY, obj.bboxW, obj.bboxH);
            state.lastEyeWidth = manifest.eye_w;
            state.lastEyeHeight = manifest.eye_h;
            state.hasLastBBoxEye = obj.bboxW > 0 && obj.bboxH > 0;
        }
        state.lastCategoryId = obj.categoryId;
        if (state.lastScreen == null)
        {
            state.lastScreen = screen;
        }

        float bboxArea = (float)obj.bboxW * obj.bboxH;
        state.hasReliableBBoxThisFrame = bboxArea > 0f &&
            (state.lastGoodBBoxArea <= 0f || bboxArea >= state.lastGoodBBoxArea * BBoxQualityMinAreaRatio);
        if (state.hasReliableBBoxThisFrame)
        {
            state.lastGoodBBoxArea = bboxArea;
        }
    }

    // Called after the full tracked placement pipeline (anchor + bbox fit + skeleton) has
    // written the model's actual displayed transform for this frame. Interactive motion must
    // freeze/originate from this, not the raw pinhole anchor: the anchor is often well above
    // the model's grounded feet position, so freezing to it pops the model upward. Skipped on
    // frames whose bbox just collapsed (see BBoxQualityMinAreaRatio), so the sample used to
    // start a frame-out walk is the last reliable frame, not a corrupted edge-of-frame one.
    private void ObserveInteractiveMotionDisplayedRoot(uint trackId, GameObject instance)
    {
        if (instance == null)
        {
            return;
        }

        InteractiveMotionState state = GetOrCreateInteractiveMotionState(trackId);
        if (state.hasLiveSample && !state.hasReliableBBoxThisFrame)
        {
            return;
        }

        state.livePosition = instance.transform.position;
        state.liveRotation = instance.transform.rotation;
        state.hasLiveSample = true;
    }

    private void UpdateInteractiveMotionSchedule(uint trackId, MetaObj obj, int frame)
    {
        // Else は、同じフレームに Person / Animal がいないときだけ（car クリップ）。人物クリップのボールまで走らせると、
        // ⑨ の深度追従や接触補正がイベント中の位置を毎フレーム書き戻して動きが壊れる。
        bool isSupportedCategory = IsCategoryPerson(obj.categoryId) || IsCategoryAnimal(obj.categoryId) ||
                                   (enableElseInteractiveMotion && IsCategoryOther(obj.categoryId) && !FrameHasPersonOrAnimal());
        if (!enableInteractiveMotion || !isSupportedCategory)
        {
            return;
        }

        InteractiveMotionState state = GetOrCreateInteractiveMotionState(trackId);
        if (state.stage != InteractiveEventStage.Inactive)
        {
            return;
        }

        // **動画が止まっている間は発火させない。**止まっている動画を「中断」する意味は無く、しかも誰が
        // 止めたか（被験者の A / Model パネル / シークバーの掴み）を知らずに発火すると、そちらの再開処理と
        // モーションの一時停止が衝突して、モーション中に動画が動き出したり、パネルが開いたまま再生が
        // 始まったりした（2026-09-29 の監査）。次の発火時刻はそのまま持ち越す。
        if (vp == null || !vp.isPlaying)
        {
            return;
        }

        RuntimeClock.TickContext tick = GetRuntimeTickContext();
        InteractiveMotionSchedule.Decision decision = InteractiveMotionSchedule.ResolveRandomTrigger(
            enableInteractiveMotion,
            isSupportedCategory,
            true,
            IsRandomInteractiveMotionInProgress(),
            state.nextTriggerTime,
            tick.now,
            RandomInteractiveInterval());
        state.nextTriggerTime = decision.nextTriggerTime;
        if (!decision.shouldStart)
        {
            return;
        }

        StartRandomInteractiveMotion(trackId, obj, tick.now);
    }

    private float RandomInteractiveInterval()
    {
        float min = Mathf.Max(0.1f, interactiveMotionMinIntervalSeconds);
        float max = Mathf.Max(min, interactiveMotionMaxIntervalSeconds);
        return UnityEngine.Random.Range(min, max);
    }

    private InteractiveMotionState GetOrCreateInteractiveMotionState(uint trackId)
    {
        if (interactiveMotionByTrack.TryGetValue(trackId, out InteractiveMotionState state) && state != null)
        {
            return state;
        }

        state = new InteractiveMotionState
        {
            nextTriggerTime = RuntimeClock.ResolveNextTime(GetRuntimeTickContext().now, RandomInteractiveInterval())
        };
        interactiveMotionByTrack[trackId] = state;
        return state;
    }

    private GameObject GetTrackInstanceOrNull(uint trackId)
    {
        return trackInstances.TryGetValue(trackId, out GameObject instance) ? instance : null;
    }

    // --- Random-triggered events: static or dynamic (walk-in / gesture / walk-back) ----

    private void StartRandomInteractiveMotion(uint trackId, MetaObj obj, float now)
    {
        if (IsCategoryOther(obj.categoryId))
        {
            if (!StartRigidDriveMotion(trackId, now))
            {
                // 経路が組めない（視聴者が近すぎる等）ときは次の機会へ。
                GetOrCreateInteractiveMotionState(trackId).nextTriggerTime = RuntimeClock.ResolveNextTime(now, RandomInteractiveInterval());
            }
            return;
        }

        bool isAnimal = IsCategoryAnimal(obj.categoryId);
        InteractiveEventKind kind = UnityEngine.Random.value < DynamicEventProbability ? InteractiveEventKind.Dynamic : InteractiveEventKind.Static;
        StartInteractiveMotion(trackId, isAnimal, kind, now);
    }

    // Editor-only debug entry point (see InteractiveMotionDebugTools) to force-trigger one event
    // without waiting on InteractiveMotionSchedule's random interval, for visually verifying
    // static/dynamic playback and the handoff blend during Play Mode. Skips tracks already
    // mid-event so it never overrides the exclusive-authority state machine.
    public void DebugForceInteractiveMotion(bool dynamicKind)
    {
        if (!enableInteractiveMotion || IsRandomInteractiveMotionInProgress())
        {
            return;
        }

        InteractiveEventKind kind = dynamicKind ? InteractiveEventKind.Dynamic : InteractiveEventKind.Static;
        RuntimeClock.TickContext tick = GetRuntimeTickContext();
        foreach (KeyValuePair<uint, GameObject> kv in trackInstances)
        {
            uint trackId = kv.Key;
            if (!interactiveMotionByTrack.TryGetValue(trackId, out InteractiveMotionState state) || state == null)
            {
                continue;
            }
            if (state.stage != InteractiveEventStage.Inactive)
            {
                continue;
            }
            bool isAnimal = IsCategoryAnimal(state.lastCategoryId);
            bool isPerson = IsCategoryPerson(state.lastCategoryId);
            if (!isAnimal && !isPerson)
            {
                if (enableElseInteractiveMotion && IsCategoryOther(state.lastCategoryId) && !FrameHasPersonOrAnimal() &&
                    StartRigidDriveMotion(trackId, tick.now))
                {
                    Debug.Log($"DebugForceInteractiveMotion: track {trackId} forced into rigid drive (Else).");
                    return;
                }
                continue;
            }

            StartInteractiveMotion(trackId, isAnimal, kind, tick.now);
            Debug.Log($"DebugForceInteractiveMotion: track {trackId} forced into {kind} ({(isAnimal ? "Animal" : "Human")}).");
            return;
        }
    }

    // Counts random-triggered events currently in progress (Owned or HandoffBlend), across all
    // tracks. Random scheduling treats any positive count as a global lock so only one random
    // animation can run at a time. System (frame-out) events are tied to the real video timeline
    // (they walk out and back to match when the subject actually reappears), so they deliberately
    // do not pause anything here.
    private int activeRandomInteractiveMotionCount;

    private bool IsRandomInteractiveMotionInProgress()
    {
        return activeRandomInteractiveMotionCount > 0;
    }

    // Random イベントで動画を止めた時刻（実時間）。止めていないときは負。
    // 実験ログに「何秒止めたか」を残す（2026-09-25、第 3 条件の duration_sec を解釈するため）。
    private float randomMotionVideoPauseStartedAt = -1f;

    // 止める前後の音量フェード（2026-09-28、実機のユーザー指摘「急に音が消えると驚く」）。
    // 動画はフェードアウトが終わってから止める（モデルの動きはフェードの開始と同時に始まる）。
    // 再開は先に Play してから同じ時間でフェードイン。秒数は interactiveMotionAudioFadeSeconds（0 なら即時）。
    private readonly InteractiveMotionAudioFade motionAudioFade = new InteractiveMotionAudioFade();
    private uint motionAudioFadeTrackId;

    private void BeginRandomInteractiveMotionVideoPause(uint trackId)
    {
        activeRandomInteractiveMotionCount++;
        if (activeRandomInteractiveMotionCount == 1 && vp != null && vp.isPlaying)
        {
            motionAudioFadeTrackId = trackId;
            float seconds = Mathf.Max(0f, interactiveMotionAudioFadeSeconds);
            motionAudioFade.BeginFadeOut(GetDirectAudioVolumeForFade(), seconds);
            Debug.Log($"[MOTION] audio fade out begin track={trackId} seconds={seconds:F2} videoTime={vp.time:F2}s");
            if (seconds <= 0f)
            {
                TickInteractiveMotionAudioFade();   // 即時（以前の挙動）
            }
        }
    }

    // Model パネルを閉じる・シークバーを離すときに「本当は再開したいが Random モーションが動画を止めている」
    // 場合に立てる。モーションが終わるときに代わりに再開する（誰が止めたかの所有者を持たせる）。
    private bool deferredVideoResumeAfterRandomMotion;

    // Random モーションが動画を止めている最中なら、再開をモーションの終わりへ回して true を返す。
    private bool TryDeferVideoResumeToRandomMotionEnd()
    {
        if (!IsRandomInteractiveMotionInProgress())
        {
            return false;
        }

        deferredVideoResumeAfterRandomMotion = true;
        Debug.Log("[MOTION] video resume deferred until the random event ends");
        return true;
    }

    private void EndRandomInteractiveMotionVideoPause(uint trackId)
    {
        activeRandomInteractiveMotionCount = Mathf.Max(0, activeRandomInteractiveMotionCount - 1);
        if (activeRandomInteractiveMotionCount != 0 || vp == null)
        {
            return;
        }

        float seconds = Mathf.Max(0f, interactiveMotionAudioFadeSeconds);
        // フェードアウトの途中（まだ止めていない）でイベントが終わった（Motion OFF・非表示・モデル差し替え）:
        // 止める予定を取り消して音量だけ戻す。取り消さないと 0.5 秒後に Tick が動画を止め、
        // イベントが無いのに動画が止まって音量 0 のままになる（2026-09-29、別セッションの指摘）。
        if (motionAudioFade.CurrentPhase == InteractiveMotionAudioFade.Phase.FadingOut)
        {
            motionAudioFade.BeginFadeIn(seconds);
        }
        // 止めたのが自分（PauseVideoForInteractiveMotion が実際に止めた）か、止めた側から再開を預かったか。
        // それ以外（被験者が A で止めた・パネルが止めて開いたまま）は戻さない。以前は「止まっていれば戻す」だったので、
        // パネルが開いたまま再生が始まる・被験者の停止が勝手に解ける、が起きた（2026-09-29 の監査）。
        bool pausedByMotion = randomMotionVideoPauseStartedAt >= 0f;
        float paused = pausedByMotion ? Time.unscaledTime - randomMotionVideoPauseStartedAt : 0f;
        randomMotionVideoPauseStartedAt = -1f;
        bool shouldResume = pausedByMotion || deferredVideoResumeAfterRandomMotion;
        deferredVideoResumeAfterRandomMotion = false;

        if (pausedByMotion)
        {
            // begin と必ず対にする（再開しない経路でも）。
            Debug.Log($"[MOTION] video pause end track={trackId} paused={paused:F2}s");
            ExperimentLog.Interaction(trackId, "video_pause_end", $"paused_sec={ExperimentCsv.Format(paused)}");
        }

        if (!shouldResume || vp.isPlaying)
        {
            // 手動で再開済みなら音量は TickInteractiveMotionAudioFade が戻す。
            if (seconds <= 0f)
            {
                TickInteractiveMotionAudioFade();
            }
            return;
        }

        // Model パネルを開いている間は戻さない。閉じるときに戻す（開く前に再生中だった扱いにする）。
        if (runtimeModelPickerOpen)
        {
            modelPickerWasPlayingBeforeOpen = true;
            Debug.Log($"[MOTION] video resume handed to the model panel track={trackId}");
            return;
        }

        // シークバーを掴んでいる間も同じ（離すときに戻す）。
        if (runtimeProgressDragNotifier != null && runtimeProgressDragNotifier.IsDragging)
        {
            runtimeProgressDragWasPlaying = true;
            Debug.Log($"[MOTION] video resume handed to the seek drag track={trackId}");
            return;
        }

        motionAudioFade.BeginFadeIn(seconds);
        RuntimePlaybackController.Apply(vp, RuntimePlaybackController.Command.Play);
        UpdatePauseButtonLabel();
        if (seconds <= 0f)
        {
            TickInteractiveMotionAudioFade();
        }
    }

    // フェードアウトが終わったときに呼ばれる。既に止まっていたら（被験者の A・パネル）止めた側の所有なので
    // 触らず、記録だけ残す。
    private void PauseVideoForInteractiveMotion()
    {
        if (vp == null)
        {
            return;
        }
        if (!vp.isPlaying)
        {
            Debug.Log($"[MOTION] video already paused by someone else track={motionAudioFadeTrackId}");
            ExperimentLog.Interaction(motionAudioFadeTrackId, "video_pause_begin", "already_paused=1");
            return;
        }
        RuntimePlaybackController.Apply(vp, RuntimePlaybackController.Command.Pause);
        UpdatePauseButtonLabel();
        randomMotionVideoPauseStartedAt = Time.unscaledTime;
        Debug.Log($"[MOTION] video pause begin track={motionAudioFadeTrackId} videoTime={vp.time:F2}s");
        ExperimentLog.Interaction(motionAudioFadeTrackId, "video_pause_begin", null);
    }

    // 毎描画フレーム（UpdateRuntimePlaybackTick）から呼ぶ。実時間で進める。
    private void TickInteractiveMotionAudioFade()
    {
        if (motionAudioFade.CurrentPhase == InteractiveMotionAudioFade.Phase.Idle)
        {
            return;
        }
        // 止めている間に被験者が手動で再開したら、無音のままにせず音量を戻す。
        if (motionAudioFade.CurrentPhase == InteractiveMotionAudioFade.Phase.Paused && vp != null && vp.isPlaying)
        {
            motionAudioFade.BeginFadeIn(Mathf.Max(0f, interactiveMotionAudioFadeSeconds));
        }
        InteractiveMotionAudioFade.Step step = motionAudioFade.Tick(Time.unscaledDeltaTime);
        if (step.applyVolume)
        {
            SetDirectAudioVolumeForFade(step.volume);
        }
        if (step.pauseNow)
        {
            PauseVideoForInteractiveMotion();
        }
    }

    private float GetDirectAudioVolumeForFade()
    {
        return vp != null && vp.audioTrackCount > 0 ? vp.GetDirectAudioVolume(0) : 1f;
    }

    private void SetDirectAudioVolumeForFade(float volume)
    {
        if (vp == null)
        {
            return;
        }
        for (ushort track = 0; track < vp.audioTrackCount; track++)
        {
            vp.SetDirectAudioVolume(track, volume);
        }
    }

    // 各フェーズの長さを残す。第 3 条件で動画が合計何秒止まるかは、この行の和で分かる。
    private void LogInteractiveMotionPhase(uint trackId, string phase, string clipName, float durationSeconds, float distanceMeters)
    {
        Debug.Log($"[MOTION] {phase} track={trackId} clip={clipName} duration={durationSeconds:F2}s distance={distanceMeters:F2}m");
        ExperimentLog.Interaction(
            trackId,
            phase,
            $"clip={clipName} duration_sec={ExperimentCsv.Format(durationSeconds)} distance_m={ExperimentCsv.Format(distanceMeters)}");
    }

    private void StartInteractiveMotion(uint trackId, bool isAnimal, InteractiveEventKind kind, float now)
    {
        InteractiveMotionState state = GetOrCreateInteractiveMotionState(trackId);
        state.subject = isAnimal ? InteractiveMotionSubject.Animal : InteractiveMotionSubject.Person;
        state.triggerSource = InteractiveTriggerSource.Random;
        state.kind = kind;
        BeginRandomInteractiveMotionVideoPause(trackId);
        state.originPosition = state.hasLiveSample ? state.livePosition : Vector3.zero;
        state.originRotation = state.hasLiveSample ? state.liveRotation : Quaternion.identity;

        ExperimentLog.Interaction(
            trackId,
            $"random_{kind}",
            $"subject={(isAnimal ? "animal" : "human")}");

        if (state.kind == InteractiveEventKind.Static)
        {
            BeginGesturePhase(state, trackId, isAnimal, state.originPosition, state.originRotation, now);
            return;
        }

        BeginWalkInPhase(state, trackId, isAnimal, now);
    }

    private void BeginWalkInPhase(InteractiveMotionState state, uint trackId, bool isAnimal, float now)
    {
        state.fallbackBoneBaseLocalRotations.Clear();
        state.dynamicPhase = InteractiveDynamicPhase.WalkIn;
        Vector3 upAxis = Vector3.up;
        Transform viewer = GetViewOrHeadTransform();
        Vector3 forwardFallback = state.lastScreen != null ? -state.lastScreen.forward : Vector3.forward;
        Vector3 viewerPosition = viewer != null ? viewer.position : state.originPosition + forwardFallback;
        Vector3 toViewer = Vector3.ProjectOnPlane(viewerPosition - state.originPosition, upAxis);
        float stopDistance = isAnimal ? animalApproachStopDistanceMeters : humanApproachStopDistanceMeters;
        Vector3 destination = toViewer.sqrMagnitude > 0.000001f
            ? viewerPosition - toViewer.normalized * stopDistance
            : state.originPosition;
        destination = PreserveHeight(destination, state.originPosition, upAxis);
        Quaternion destinationRotation = state.originRotation;
        if (toViewer.sqrMagnitude > 0.000001f)
        {
            destinationRotation = isAnimal
                ? ResolveAnimalTurnedRotation(GetTrackInstanceOrNull(trackId), state.originRotation, toViewer, upAxis)
                : Quaternion.LookRotation(toViewer.normalized, upAxis);
        }

        state.phaseFromPosition = state.originPosition;
        state.phaseFromRotation = state.originRotation;
        state.phaseToPosition = destination;
        state.phaseToRotation = destinationRotation;

        float speed = Mathf.Max(0.05f, isAnimal ? animalWalkSpeedMetersPerSecond : humanWalkSpeedMetersPerSecond);
        float distance = Vector3.Distance(state.phaseFromPosition, state.phaseToPosition);
        state.phaseStartTime = now;
        state.phaseDuration = Mathf.Max(MinWalkDurationSeconds, distance / speed);
        state.stage = InteractiveEventStage.Owned;
        LogInteractiveMotionPhase(trackId, "walk_in", isAnimal ? "animal_walk" : "human_walk", state.phaseDuration, distance);

        if (isAnimal)
        {
            state.animalWalkClip = PickAnimalWalkClip();
        }
        else
        {
            StartHumanClipPlayback(trackId, GetTrackInstanceOrNull(trackId), PickHumanWalkClip(), true);
        }
    }

    private void BeginGesturePhase(InteractiveMotionState state, uint trackId, bool isAnimal, Vector3 freezePosition, Quaternion freezeRotation, float now)
    {
        state.fallbackBoneBaseLocalRotations.Clear();
        state.dynamicPhase = InteractiveDynamicPhase.Gesture;
        state.gesturePosition = freezePosition;
        state.gestureRotation = freezeRotation;
        state.phaseStartTime = now;
        state.stage = InteractiveEventStage.Owned;
        GameObject instance = GetTrackInstanceOrNull(trackId);

        if (isAnimal)
        {
            state.animalPreset = PickAnimalPreset(state.cachedAnimalHasSmalPose, out AnimalGesturePose gestureClip);
            state.animalGestureClip = gestureClip;
            if (state.animalPreset == InteractiveAnimalPreset.FaceViewer)
            {
                state.gestureRotation = ResolveAnimalFaceViewerRotation(instance, state.gesturePosition, freezeRotation, state.lastScreen);
            }
            state.phaseDuration = gestureClip != null
                ? Mathf.Max(MinGestureDurationSeconds, gestureClip.duration)
                : Mathf.Max(MinGestureDurationSeconds, staticAnimationDurationSeconds);
            LogInteractiveMotionPhase(
                trackId, "gesture", gestureClip != null ? gestureClip.name : state.animalPreset.ToString(), state.phaseDuration, 0f);
            return;
        }

        state.gestureRotation = ResolveFaceViewerRotation(state.gesturePosition, freezeRotation, state.lastScreen);
        state.humanClip = PickHumanClip(humanStaticGestureClips);
        if (state.humanClip != null)
        {
            state.humanPreset = InteractiveHumanPreset.ClipGesture;
            state.phaseDuration = Mathf.Max(MinGestureDurationSeconds, state.humanClip.length > 0.0001f ? state.humanClip.length : staticAnimationDurationSeconds);
            LogInteractiveMotionPhase(trackId, "gesture", state.humanClip.name, state.phaseDuration, 0f);
            StartHumanClipPlayback(trackId, instance, state.humanClip, false);
            return;
        }

        state.humanPreset = InteractiveHumanPreset.FaceViewer;
        state.phaseDuration = Mathf.Max(MinGestureDurationSeconds, staticAnimationDurationSeconds);
        LogInteractiveMotionPhase(trackId, "gesture", "FaceViewer", state.phaseDuration, 0f);
    }

    private void BeginWalkBackPhase(InteractiveMotionState state, uint trackId, bool isAnimal, float now)
    {
        state.fallbackBoneBaseLocalRotations.Clear();
        state.dynamicPhase = InteractiveDynamicPhase.WalkBack;
        Vector3 upAxis = Vector3.up;
        Vector3 travel = Vector3.ProjectOnPlane(state.originPosition - state.gesturePosition, upAxis);
        state.phaseFromPosition = state.gesturePosition;
        state.phaseFromRotation = state.gestureRotation;
        state.phaseToPosition = state.originPosition;
        state.phaseToRotation = state.gestureRotation;
        if (travel.sqrMagnitude > 0.000001f)
        {
            state.phaseToRotation = isAnimal
                ? ResolveAnimalTurnedRotation(GetTrackInstanceOrNull(trackId), state.gestureRotation, travel, upAxis)
                : Quaternion.LookRotation(travel.normalized, upAxis);
        }

        float speed = Mathf.Max(0.05f, isAnimal ? animalWalkSpeedMetersPerSecond : humanWalkSpeedMetersPerSecond);
        float distance = Vector3.Distance(state.phaseFromPosition, state.phaseToPosition);
        state.phaseStartTime = now;
        state.phaseDuration = Mathf.Max(MinWalkDurationSeconds, distance / speed);
        state.stage = InteractiveEventStage.Owned;
        LogInteractiveMotionPhase(trackId, "walk_back", isAnimal ? "animal_walk" : "human_walk", state.phaseDuration, distance);

        if (isAnimal)
        {
            state.animalWalkClip = PickAnimalWalkClip();
        }
        else
        {
            StartHumanClipPlayback(trackId, GetTrackInstanceOrNull(trackId), PickHumanWalkClip(), true);
        }
    }

    private void AdvanceRandomEventPhase(uint trackId, InteractiveMotionState state, float now)
    {
        bool isAnimal = state.subject == InteractiveMotionSubject.Animal;
        if (state.kind == InteractiveEventKind.Static)
        {
            BeginHandoff(trackId, state, now);
            return;
        }

        switch (state.dynamicPhase)
        {
            case InteractiveDynamicPhase.WalkIn:
                BeginGesturePhase(state, trackId, isAnimal, state.phaseToPosition, state.phaseToRotation, now);
                break;
            case InteractiveDynamicPhase.Gesture:
                BeginWalkBackPhase(state, trackId, isAnimal, now);
                break;
            default:
                BeginHandoff(trackId, state, now);
                break;
        }
    }

    // --- System-triggered events: fills the gap while a track is offscreen -------------

    private bool TryApplyInteractiveSystemTriggerTrack(uint trackId, int frame)
    {
        GameObject instance = GetTrackInstanceOrNull(trackId);
        if (instance == null || !interactiveMotionByTrack.TryGetValue(trackId, out InteractiveMotionState state) || state == null)
        {
            return false;
        }

        if (state.stage == InteractiveEventStage.Inactive)
        {
            if (!enableInteractiveMotion || !state.hasLiveSample)
            {
                return false;
            }
            if (!IsCategoryPerson(state.lastCategoryId) && !IsCategoryAnimal(state.lastCategoryId))
            {
                return false;
            }
            StartSystemTriggerInteractiveMotion(trackId, instance, state, frame);
        }

        SceneObjectWriter.ApplyActive(instance, true);
        if (state.stage == InteractiveEventStage.Owned)
        {
            return TryApplyOwnedInteractiveMotion(trackId, instance, null, frame);
        }

        return state.stage == InteractiveEventStage.HandoffBlend;
    }

    private void StartSystemTriggerInteractiveMotion(uint trackId, GameObject instance, InteractiveMotionState state, int frame)
    {
        bool isAnimal = IsCategoryAnimal(state.lastCategoryId);
        state.subject = isAnimal ? InteractiveMotionSubject.Animal : InteractiveMotionSubject.Person;
        state.triggerSource = InteractiveTriggerSource.SystemFrameOut;
        state.kind = InteractiveEventKind.Dynamic;
        state.dynamicPhase = InteractiveDynamicPhase.WalkIn;
        state.originPosition = state.livePosition;
        state.phaseFromPosition = state.livePosition;
        state.triggerStartFrame = frame;
        state.hasFrameInPosition = false;
        state.frameInFrame = -1;

        ExperimentLog.Interaction(
            trackId,
            "system_frameout",
            $"subject={(isAnimal ? "animal" : "human")} frame={frame}");

        Vector3 pixelRight = Vector3.right;
        Vector3 pixelUp = Vector3.up;
        if (TryGetPinholeBasis(state.lastScreen, out _, out Quaternion pinholeRotation))
        {
            pixelRight = pinholeRotation * Vector3.right;
            pixelUp = pinholeRotation * Vector3.up;
        }
        Vector3 fallbackDirection = state.lastScreen != null ? -state.lastScreen.forward : Vector3.forward;
        Vector3 motionDirection = AnimalFrameOutMotion.ResolveDirectionFromScreenMotion(
            state.previousAnchorEyePixel,
            state.lastAnchorEyePixel,
            state.hasPreviousAnchorEyePixel && state.hasLastAnchorEyePixel,
            pixelRight,
            pixelUp,
            fallbackDirection);
        state.frameOutDirection = AnimalFrameOutMotion.ResolveDirectionFromScreenExit(
            state.lastBBoxEye,
            state.lastEyeWidth,
            state.lastEyeHeight,
            pixelRight,
            pixelUp,
            motionDirection);

        // frameOutDirection must stay derived from the bbox edge (above) - it determines which
        // way the model actually walks, and has to point off the visible screen for the track
        // to disappear at all. (A previous version pointed it at the measured nose direction
        // instead, to keep facing and movement self-consistent - that broke the walk-out
        // entirely, since the measured facing direction has no necessary relationship to which
        // screen edge the track is exiting through.)
        //
        // liveRotation (instanceRoot's tracked rotation) carries no usable heading on its own
        // for a SMAL-posed animal (see ResolveAnimalFaceViewerRotation), so turn it to face
        // frameOutDirection the same way FaceViewer does. This measurement is noisier than usual
        // right at this exact moment (the bbox is already collapsing in the last few tracked
        // frames before disappearing - see "Animal tracks" in Docs/interactive-motion-events.md),
        // so the resulting facing can be a little off from the true exit direction - accepted as
        // the lesser problem versus not exiting the screen at all.
        Vector3 faceViewerUpAxis = state.lastScreen != null ? state.lastScreen.up : Vector3.up;
        Quaternion outboundRotation = isAnimal
            ? ResolveAnimalTurnedRotation(instance, state.liveRotation, state.frameOutDirection, faceViewerUpAxis)
            : state.liveRotation;
        state.originRotation = outboundRotation;
        state.phaseFromRotation = outboundRotation;

        TryPrepareFrameInTarget(trackId, frame, state);

        float now = GetRuntimeTickContext().now;
        state.phaseStartTime = now;
        state.phaseDuration = state.hasFrameInPosition
            ? Mathf.Max(MinWalkDurationSeconds, (state.frameInFrame - frame) / Mathf.Max(1f, ResolveMetaFps()))
            : SystemTriggerLoopSeconds;
        state.stage = InteractiveEventStage.Owned;

        if (isAnimal)
        {
            state.animalWalkClip = PickAnimalWalkClip();
        }
        else
        {
            StartHumanClipPlayback(trackId, instance, PickHumanWalkClip(), true);
        }
    }

    // The detector's bbox is routinely still small/partial for the first few frames after a
    // track reappears (mirroring the same collapse on the way out), so the normal tracked
    // pipeline's bbox-fit placement can be briefly wrong right when visibility resumes.
    // Handing off into that frame would blend toward a bad position. Keep running the
    // synthetic frame-out walk until a reliable frame is seen, up to a timeout so a track
    // that never recovers full bbox confidence doesn't get stuck.
    private const float MaxFrameInQualityWaitSeconds = 0.5f;

    private void TryStopSystemTriggerOnVisibleFrame(uint trackId)
    {
        if (!interactiveMotionByTrack.TryGetValue(trackId, out InteractiveMotionState state) || state == null)
        {
            return;
        }
        if (state.stage != InteractiveEventStage.Owned || state.triggerSource != InteractiveTriggerSource.SystemFrameOut)
        {
            return;
        }

        float now = GetRuntimeTickContext().now;
        if (!state.hasReliableBBoxThisFrame)
        {
            if (state.frameInQualityWaitStartTime <= 0f)
            {
                state.frameInQualityWaitStartTime = now;
            }
            if (now - state.frameInQualityWaitStartTime < MaxFrameInQualityWaitSeconds)
            {
                return;
            }
        }

        state.frameInQualityWaitStartTime = 0f;
        BeginHandoff(trackId, state, now);
    }

    private bool TryPrepareFrameInTarget(uint trackId, int triggerStartFrame, InteractiveMotionState state)
    {
        if (state == null || frameOffsets == null || frameOffsets.Length == 0)
        {
            return false;
        }

        if (!TryFindNextTrackObject(trackId, triggerStartFrame + 1, out int frameInFrame, out MetaObj frameInObj))
        {
            return false;
        }

        if (!TryResolveMetaObjectAnchorWorld(frameInObj, out Vector3 frameInPosition))
        {
            return false;
        }

        state.frameInFrame = frameInFrame;
        state.frameInPosition = PreserveHeight(frameInPosition, state.originPosition, Vector3.up);
        state.hasFrameInPosition = true;
        return true;
    }

    private bool TryFindNextTrackObject(uint trackId, int startFrame, out int foundFrame, out MetaObj foundObj)
    {
        foundFrame = -1;
        foundObj = default(MetaObj);
        if (!metaLoaded || frameOffsets == null || frameOffsets.Length == 0)
        {
            return false;
        }

        int endFrame = frameOffsets.Length;
        List<MetaObj> scanObjects = new List<MetaObj>(16);
        for (int frame = Mathf.Max(0, startFrame); frame < endFrame; frame++)
        {
            if (!TryReadFrameObjects(frame, scanObjects))
            {
                continue;
            }

            for (int i = 0; i < scanObjects.Count; i++)
            {
                MetaObj obj = scanObjects[i];
                if (obj.trackId != trackId)
                {
                    continue;
                }

                foundFrame = frame;
                foundObj = obj;
                return true;
            }
        }

        return false;
    }

    private bool TryResolveMetaObjectAnchorWorld(MetaObj obj, out Vector3 world)
    {
        world = Vector3.zero;
        if (manifest == null || manifest.eye_w <= 0 || manifest.eye_h <= 0)
        {
            return false;
        }

        if (!ResolveAnchorToScreen(obj.anchorU, out Transform screen, out int uEye, out _))
        {
            return false;
        }

        float uEyeF = Mathf.Clamp(uEye, 0f, manifest.eye_w - 1f);
        float vEyeF = Mathf.Clamp(obj.anchorV, 0f, manifest.eye_h - 1f);
        world = AnchorUvZToWorldPinhole(screen, uEyeF, vEyeF, obj.anchorZ);
        return true;
    }

    private float ResolveMetaFps()
    {
        return metaHeader.fps > 0f ? metaHeader.fps : (manifest != null && manifest.fps > 0f ? manifest.fps : 30f);
    }

    // --- Owned-stage apply (shared by random and system-triggered events) --------------

    private bool TryApplyOwnedInteractiveMotion(uint trackId, GameObject instance, Transform screen, int frame)
    {
        if (!interactiveMotionByTrack.TryGetValue(trackId, out InteractiveMotionState state) || state == null || state.stage != InteractiveEventStage.Owned)
        {
            return false;
        }

        RuntimeClock.TickContext tick = GetRuntimeTickContext();
        if (state.subject == InteractiveMotionSubject.Rigid)
        {
            return ApplyRigidDrive(trackId, instance, state, tick);
        }

        bool isAnimal = state.subject == InteractiveMotionSubject.Animal;
        bool isGesturePhase = state.kind == InteractiveEventKind.Static || state.dynamicPhase == InteractiveDynamicPhase.Gesture;

        // Permanent frame-out: no reappearance in remaining metadata — walk indefinitely until
        // off-screen rather than stopping at a fixed control point.
        if (state.triggerSource == InteractiveTriggerSource.SystemFrameOut && !state.hasFrameInPosition)
        {
            return TryApplyPermanentFrameOutWalk(trackId, instance, state, isAnimal, tick);
        }

        if (isGesturePhase)
        {
            ApplyFrozenPoseAndGesture(trackId, instance, state, tick);
        }
        else
        {
            ResolveOwnedWalkPose(state, frame, tick.now, out Vector3 position, out Quaternion rotation);
            if (isAnimal)
            {
                // AnimalFrameOutMotion.ResolveOneWaySegmentPose recomputes its own
                // Quaternion.LookRotation(travel, up) every frame independent of
                // phaseToRotation - travel is fixed for the whole walk phase, so that result is
                // actually constant and equivalent to phaseToRotation for Human. For Animal,
                // phaseToRotation was already corrected (BeginWalkInPhase/BeginWalkBackPhase) to
                // turn the model's measured real nose direction, not its naive local +Z, toward
                // the target - the Random trigger source should use that corrected value
                // instead. (SystemFrameOut already rotates relative to a known-good live
                // rotation via AnimalFrameOutMotion.ResolveRotation, so it needs no change.)
                if (state.triggerSource == InteractiveTriggerSource.Random)
                {
                    rotation = state.phaseToRotation;
                }
                ApplyMovingAnimalPose(instance, state, position, rotation, tick);
            }
            else
            {
                ApplyHumanClipPlayback(trackId, instance, tick.now, tick.deltaTime);
                if (!HasActiveHumanClipPlayback(trackId))
                {
                    ApplyFallbackHumanWalk(instance, state, tick.now);
                }
                // Same re-pin as the gesture phase: the looping walk clip may have written its
                // own root motion onto the instance transform during Evaluate().
                TrackPlacementWriter.Apply(instance.transform, new TrackPlacementCommand(position, rotation, instance.transform.localScale));
            }
        }

        float elapsed = RuntimeClock.ResolveElapsed(tick.now, state.phaseStartTime);
        if (state.triggerSource == InteractiveTriggerSource.Random && elapsed >= state.phaseDuration)
        {
            AdvanceRandomEventPhase(trackId, state, tick.now);
        }

        return true;
    }

    // Permanent frame-out walk: linear constant-speed movement in frameOutDirection until the
    // instance is no longer visible to any camera, or the safety timeout fires. Constant speed
    // (not ResolveLoopPose's smoothstep) keeps root movement in sync with the looping walk
    // animation. On hide: deactivate, reset to Inactive, clear hasLiveSample so the trigger
    // does not immediately restart. See docs/adr/0005-permanent-frameout-indefinite-walk.md.
    private bool TryApplyPermanentFrameOutWalk(uint trackId, GameObject instance, InteractiveMotionState state, bool isAnimal, RuntimeClock.TickContext tick)
    {
        float elapsed = RuntimeClock.ResolveElapsed(tick.now, state.phaseStartTime);
        float speed = Mathf.Max(0.05f, isAnimal ? animalWalkSpeedMetersPerSecond : humanWalkSpeedMetersPerSecond) * SystemTriggerSpeedMultiplier;
        Vector3 position = state.phaseFromPosition + state.frameOutDirection * (elapsed * speed);
        Quaternion rotation = state.phaseFromRotation;

        if (isAnimal)
        {
            ApplyMovingAnimalPose(instance, state, position, rotation, tick);
        }
        else
        {
            ApplyHumanClipPlayback(trackId, instance, tick.now, tick.deltaTime);
            if (!HasActiveHumanClipPlayback(trackId))
            {
                ApplyFallbackHumanWalk(instance, state, tick.now);
            }
            TrackPlacementWriter.Apply(instance.transform, new TrackPlacementCommand(position, rotation, instance.transform.localScale));
        }

        bool invisible = !IsInstanceVisibleToAnyCamera(instance);
        bool timedOut = elapsed >= ResolvePermanentFrameOutTimeoutSeconds();
        if (invisible || timedOut)
        {
            SceneObjectWriter.ApplyActive(instance, false);
            state.stage = InteractiveEventStage.Inactive;
            state.hasLiveSample = false;
            StopHumanClipPlayback(trackId);
        }

        return true;
    }

    private static bool IsInstanceVisibleToAnyCamera(GameObject instance)
    {
        if (instance == null)
        {
            return false;
        }

        Renderer[] renderers = instance.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < renderers.Length; i++)
        {
            if (renderers[i] != null && renderers[i].isVisible)
            {
                return true;
            }
        }

        return false;
    }

    private float ResolvePermanentFrameOutTimeoutSeconds()
    {
        return vp != null && vp.length > 0.0001 ? (float)(vp.length * 0.5) : 60f;
    }

    private void ResolveOwnedWalkPose(InteractiveMotionState state, int frame, float now, out Vector3 position, out Quaternion rotation)
    {
        float elapsed = RuntimeClock.ResolveElapsed(now, state.phaseStartTime);
        if (state.triggerSource == InteractiveTriggerSource.SystemFrameOut)
        {
            float t = ResolveSystemTriggerNormalizedTime(state, frame, elapsed);
            AnimalFrameOutLoopPose pose = AnimalFrameOutMotion.ResolveLoopPose(
                state.phaseFromPosition,
                state.phaseFromRotation,
                state.frameOutDirection,
                state.frameInPosition,
                state.hasFrameInPosition,
                t,
                ResolveSystemTriggerTravelDistance(state),
                1f,
                Vector3.up);
            position = pose.position;
            rotation = pose.rotation;
            return;
        }

        float walkT = Mathf.Clamp01(elapsed / Mathf.Max(0.001f, state.phaseDuration));
        AnimalFrameOutLoopPose walkPose = AnimalFrameOutMotion.ResolveOneWaySegmentPose(
            state.phaseFromPosition, state.phaseToPosition, state.phaseFromRotation, Vector3.up, walkT);
        position = walkPose.position;
        rotation = walkPose.rotation;
    }

    private float ResolveSystemTriggerNormalizedTime(InteractiveMotionState state, int frame, float elapsed)
    {
        if (state.hasFrameInPosition && state.frameInFrame > state.triggerStartFrame)
        {
            int span = Mathf.Max(1, state.frameInFrame - state.triggerStartFrame);
            return Mathf.Clamp01((frame - state.triggerStartFrame) / (float)span);
        }
        return Mathf.Clamp01(elapsed / Mathf.Max(0.001f, state.phaseDuration));
    }

    // Returns the "overshoot" distance ResolveControlPoint adds past the predicted frame-in
    // point, sized so that walking the whole out-and-back path (frameInProjection + overshoot,
    // there; overshoot, back) at the configured walk speed takes exactly as long as the track
    // is actually hidden. Previously this returned hiddenSeconds * speed directly as the
    // overshoot itself, then ResolveLoopPose split that same duration into a fixed 75/25
    // out/back time ratio regardless of how that compared to the actual out/back distance
    // ratio - whichever leg was geometrically shorter still got 25%/75% of the time, so it
    // moved faster than the configured speed (and faster than the other leg).
    // A frame-out walk reads as more natural a bit faster than the random-triggered
    // walk-toward-viewer (which is tuned for a deliberate, visible approach) - the subject is
    // already in motion when it leaves frame, not starting from a stand-still.
    private const float SystemTriggerSpeedMultiplier = 1.5f;

    private float ResolveSystemTriggerTravelDistance(InteractiveMotionState state)
    {
        bool isAnimal = state.subject == InteractiveMotionSubject.Animal;
        float speed = Mathf.Max(0.05f, isAnimal ? animalWalkSpeedMetersPerSecond : humanWalkSpeedMetersPerSecond) * SystemTriggerSpeedMultiplier;
        float seconds = state.hasFrameInPosition && state.frameInFrame > state.triggerStartFrame
            ? (state.frameInFrame - state.triggerStartFrame) / Mathf.Max(1f, ResolveMetaFps())
            : SystemTriggerLoopSeconds;
        float totalPathDistance = AnimalFrameOutMotion.ResolveTravelDistance(seconds, speed);

        if (!state.hasFrameInPosition)
        {
            return totalPathDistance;
        }

        Vector3 forward = Vector3.ProjectOnPlane(state.frameOutDirection, Vector3.up);
        if (forward.sqrMagnitude <= 0.000001f)
        {
            forward = Vector3.forward;
        }
        forward.Normalize();
        Vector3 toFrameIn = Vector3.ProjectOnPlane(state.frameInPosition - state.originPosition, Vector3.up);
        float frameInProjection = Mathf.Max(0f, Vector3.Dot(toFrameIn, forward));
        return Mathf.Max(0f, (totalPathDistance - frameInProjection) / 2f);
    }

    private static Vector3 PreserveHeight(Vector3 position, Vector3 referencePosition, Vector3 upAxis)
    {
        Vector3 safeUp = upAxis.sqrMagnitude > 0.000001f ? upAxis.normalized : Vector3.up;
        return position + safeUp * Vector3.Dot(referencePosition - position, safeUp);
    }

    // --- Handoff blend: fixed-duration transition back to live tracking ----------------

    private void BeginHandoff(uint trackId, InteractiveMotionState state, float now)
    {
        GameObject instance = GetTrackInstanceOrNull(trackId);
        state.handoffFromPosition = instance != null ? instance.transform.position : state.originPosition;
        state.handoffFromRotation = instance != null ? instance.transform.rotation : state.originRotation;
        state.handoffFromBoneLocalRotations.Clear();
        state.handoffFromAnimalBoneLocalRotations.Clear();
        if (instance != null && state.subject == InteractiveMotionSubject.Person)
        {
            CaptureHumanoidBoneLocalRotations(instance, state.handoffFromBoneLocalRotations);
        }
        else if (instance != null && state.subject == InteractiveMotionSubject.Animal)
        {
            animalPoseApplier.CaptureBoneLocalRotations(instance.transform, state.handoffFromAnimalBoneLocalRotations);
        }
        else if (state.subject == InteractiveMotionSubject.Rigid)
        {
            RestoreRigidWheels(state);
        }
        state.stage = InteractiveEventStage.HandoffBlend;
        state.handoffStartTime = now;
        StopHumanClipPlayback(trackId);
    }

    private void ApplyInteractiveHandoffBlendIfActive(uint trackId, GameObject instance, int frame)
    {
        if (instance == null || !interactiveMotionByTrack.TryGetValue(trackId, out InteractiveMotionState state) || state == null || state.stage != InteractiveEventStage.HandoffBlend)
        {
            return;
        }

        RuntimeClock.TickContext tick = GetRuntimeTickContext();
        float elapsed = RuntimeClock.ResolveElapsed(tick.now, state.handoffStartTime);
        float duration = Mathf.Max(0.05f, interactiveHandoffBlendSeconds);
        float t = Mathf.Clamp01(elapsed / duration);

        // The tracked pipeline already wrote this frame's fully-tracked bone pose before this
        // runs; blend the body pose toward it too, not just the root, otherwise the body snaps
        // instantly into the tracked pose while only the root smoothly catches up.
        if (state.subject == InteractiveMotionSubject.Person && state.handoffFromBoneLocalRotations.Count > 0)
        {
            BlendHumanoidBoneLocalRotations(instance, state.handoffFromBoneLocalRotations, t);
        }
        else if (state.subject == InteractiveMotionSubject.Animal && state.handoffFromAnimalBoneLocalRotations.Count > 0)
        {
            AnimalPoseApplier.BlendBoneLocalRotations(state.handoffFromAnimalBoneLocalRotations, t);
        }

        Vector3 blendedPosition = Vector3.Lerp(state.handoffFromPosition, instance.transform.position, t);
        Quaternion blendedRotation = Quaternion.Slerp(state.handoffFromRotation, instance.transform.rotation, t);
        TrackPlacementWriter.Apply(instance.transform, new TrackPlacementCommand(blendedPosition, blendedRotation, instance.transform.localScale));

        if (t >= 1f)
        {
            state.stage = InteractiveEventStage.Inactive;
            state.nextTriggerTime = RuntimeClock.ResolveNextTime(tick.now, RandomInteractiveInterval());
            if (state.triggerSource == InteractiveTriggerSource.Random)
            {
                EndRandomInteractiveMotionVideoPause(trackId);
            }
        }
    }

    private static void CaptureHumanoidBoneLocalRotations(GameObject instance, Dictionary<HumanBodyBones, Quaternion> destination)
    {
        Animator animator = instance.GetComponentInChildren<Animator>();
        if (animator == null || !animator.isHuman)
        {
            return;
        }

        foreach (HumanBodyBones boneId in Enum.GetValues(typeof(HumanBodyBones)))
        {
            if (boneId == HumanBodyBones.LastBone)
            {
                continue;
            }

            Transform bone = animator.GetBoneTransform(boneId);
            if (bone != null)
            {
                destination[boneId] = bone.localRotation;
            }
        }
    }

    private static void BlendHumanoidBoneLocalRotations(GameObject instance, Dictionary<HumanBodyBones, Quaternion> fromLocalRotations, float weight)
    {
        Animator animator = instance.GetComponentInChildren<Animator>();
        if (animator == null || !animator.isHuman)
        {
            return;
        }

        foreach (KeyValuePair<HumanBodyBones, Quaternion> kv in fromLocalRotations)
        {
            Transform bone = animator.GetBoneTransform(kv.Key);
            if (bone == null)
            {
                continue;
            }

            TransformWriter.ApplyLocalRotation(bone, Quaternion.Slerp(kv.Value, bone.localRotation, Mathf.Clamp01(weight)));
        }
    }

    // --- Human gesture / walk clip playback ---------------------------------------------

    private AnimationClip PickHumanClip(AnimationClip[] clips)
    {
        if (clips == null || clips.Length == 0)
        {
            return null;
        }

        int start = UnityEngine.Random.Range(0, clips.Length);
        for (int i = 0; i < clips.Length; i++)
        {
            AnimationClip clip = clips[(start + i) % clips.Length];
            if (clip != null)
            {
                return clip;
            }
        }

        return null;
    }

    private AnimationClip PickHumanWalkClip()
    {
        return PickHumanClip(humanWalkClips);
    }

    private void ApplyFrozenPoseAndGesture(uint trackId, GameObject instance, InteractiveMotionState state, RuntimeClock.TickContext tick)
    {
        if (state.subject == InteractiveMotionSubject.Person)
        {
            if (state.humanPreset == InteractiveHumanPreset.ClipGesture)
            {
                ApplyHumanClipPlayback(trackId, instance, tick.now, tick.deltaTime);
            }
            else
            {
                ApplyFallbackHumanWave(instance, state, tick.now);
            }
            // Clip evaluation may have written its own (incorrect) root motion onto the
            // instance transform; re-pin the frozen gesture pose after evaluating the clip.
            TrackPlacementWriter.Apply(instance.transform, new TrackPlacementCommand(state.gesturePosition, state.gestureRotation, instance.transform.localScale));
            return;
        }

        TrackPlacementWriter.Apply(instance.transform, new TrackPlacementCommand(state.gesturePosition, state.gestureRotation, instance.transform.localScale));
        ApplyFrozenAnimalGesture(instance, state, tick);
    }

    private void StartHumanClipPlayback(uint trackId, GameObject instance, AnimationClip clip, bool loop)
    {
        StopHumanClipPlayback(trackId);
        if (instance == null || clip == null)
        {
            return;
        }

        Animator animator = instance.GetComponentInChildren<Animator>();
        if (animator == null)
        {
            return;
        }

        SceneObjectWriter.ApplyAnimatorEnabled(animator, true);
        SceneObjectWriter.ApplyRootMotion(animator, false);
        PlayableGraph graph = PlayableGraph.Create("InteractiveMotion_" + trackId);
        graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
        AnimationClipPlayable playable = AnimationClipPlayable.Create(graph, clip);
        playable.SetApplyFootIK(false);
        playable.SetApplyPlayableIK(false);
        AnimationPlayableOutput output = AnimationPlayableOutput.Create(graph, "Animation", animator);
        output.SetSourcePlayable(playable);
        SceneObjectWriter.ApplyPlay(graph);
        interactiveClipPlaybackByTrack[trackId] = new InteractiveClipPlayback
        {
            graph = graph,
            clip = clip,
            animator = animator,
            loop = loop
        };
    }

    private void ApplyHumanClipPlayback(uint trackId, GameObject instance, float now, float deltaTime)
    {
        if (!interactiveMotionByTrack.TryGetValue(trackId, out InteractiveMotionState state) || state == null)
        {
            return;
        }
        if (!interactiveClipPlaybackByTrack.TryGetValue(trackId, out InteractiveClipPlayback playback) ||
            playback == null ||
            !playback.graph.IsValid() ||
            playback.clip == null)
        {
            return;
        }

        Transform animatorTransform = playback.animator != null ? playback.animator.transform : null;
        Vector3 animatorLocalPositionBefore = animatorTransform != null ? animatorTransform.localPosition : Vector3.zero;
        Quaternion animatorLocalRotationBefore = animatorTransform != null ? animatorTransform.localRotation : Quaternion.identity;

        float elapsed = RuntimeClock.ResolveElapsed(now, state.phaseStartTime);
        double time = elapsed;
        if (playback.loop && playback.clip.length > 0.0001f)
        {
            time %= playback.clip.length;
        }
        else if (playback.clip.length > 0.0001f)
        {
            time = Mathf.Min(elapsed, playback.clip.length);
        }
        playback.graph.GetRootPlayable(0).SetTime(time);
        playback.graph.Evaluate(deltaTime);

        if (ShouldRestoreAnimatorLocalTransformSeparately(animatorTransform, instance.transform))
        {
            TransformWriter.ApplyLocalPose(animatorTransform, animatorLocalPositionBefore, animatorLocalRotationBefore);
        }
    }

    public static bool ShouldRestoreAnimatorLocalTransformSeparately(Transform animatorTransform, Transform instanceTransform)
    {
        return animatorTransform != null && instanceTransform != null && animatorTransform != instanceTransform;
    }

    private bool HasActiveHumanClipPlayback(uint trackId)
    {
        return interactiveClipPlaybackByTrack.TryGetValue(trackId, out InteractiveClipPlayback playback) &&
            playback != null &&
            playback.graph.IsValid() &&
            playback.clip != null;
    }

    private void StopHumanClipPlayback(uint trackId)
    {
        if (!interactiveClipPlaybackByTrack.TryGetValue(trackId, out InteractiveClipPlayback playback) || playback == null)
        {
            return;
        }

        if (playback.graph.IsValid())
        {
            playback.graph.Destroy();
        }
        interactiveClipPlaybackByTrack.Remove(trackId);
    }

    private void ApplyFallbackHumanWave(GameObject instance, InteractiveMotionState state, float now)
    {
        if (instance == null)
        {
            return;
        }

        Animator animator = instance.GetComponentInChildren<Animator>();
        if (animator == null || !animator.isHuman)
        {
            return;
        }

        Transform upper = animator.GetBoneTransform(HumanBodyBones.RightUpperArm);
        Transform lower = animator.GetBoneTransform(HumanBodyBones.RightLowerArm);
        if (upper == null || lower == null)
        {
            return;
        }

        Quaternion upperBase = ResolveFallbackBoneBaseRotation(state, HumanBodyBones.RightUpperArm, upper);
        Quaternion lowerBase = ResolveFallbackBoneBaseRotation(state, HumanBodyBones.RightLowerArm, lower);

        float elapsed = RuntimeClock.ResolveElapsed(now, state.phaseStartTime);
        float wave = Mathf.Sin(elapsed * Mathf.PI * 2.5f);
        TransformWriter.ApplyLocalRotation(upper, upperBase * Quaternion.Euler(-45f, 0f, 25f));
        TransformWriter.ApplyLocalRotation(lower, lowerBase * Quaternion.Euler(0f, 0f, 35f * wave));
    }

    private void ApplyFallbackHumanWalk(GameObject instance, InteractiveMotionState state, float now)
    {
        if (instance == null)
        {
            return;
        }

        Animator animator = instance.GetComponentInChildren<Animator>();
        if (animator == null || !animator.isHuman)
        {
            return;
        }

        Transform leftUpperLeg = animator.GetBoneTransform(HumanBodyBones.LeftUpperLeg);
        Transform rightUpperLeg = animator.GetBoneTransform(HumanBodyBones.RightUpperLeg);
        if (leftUpperLeg == null || rightUpperLeg == null)
        {
            return;
        }

        Quaternion leftLegBase = ResolveFallbackBoneBaseRotation(state, HumanBodyBones.LeftUpperLeg, leftUpperLeg);
        Quaternion rightLegBase = ResolveFallbackBoneBaseRotation(state, HumanBodyBones.RightUpperLeg, rightUpperLeg);

        float elapsed = RuntimeClock.ResolveElapsed(now, state.phaseStartTime);
        float stride = Mathf.Sin(elapsed * Mathf.PI * 3f);
        float legDegrees = 28f * stride;
        TransformWriter.ApplyLocalRotation(leftUpperLeg, leftLegBase * Quaternion.Euler(legDegrees, 0f, 0f));
        TransformWriter.ApplyLocalRotation(rightUpperLeg, rightLegBase * Quaternion.Euler(-legDegrees, 0f, 0f));

        Transform leftUpperArm = animator.GetBoneTransform(HumanBodyBones.LeftUpperArm);
        Transform rightUpperArm = animator.GetBoneTransform(HumanBodyBones.RightUpperArm);
        if (leftUpperArm != null && rightUpperArm != null)
        {
            Quaternion leftArmBase = ResolveFallbackBoneBaseRotation(state, HumanBodyBones.LeftUpperArm, leftUpperArm);
            Quaternion rightArmBase = ResolveFallbackBoneBaseRotation(state, HumanBodyBones.RightUpperArm, rightUpperArm);
            float armDegrees = legDegrees * 0.6f;
            TransformWriter.ApplyLocalRotation(leftUpperArm, leftArmBase * Quaternion.Euler(-armDegrees, 0f, 0f));
            TransformWriter.ApplyLocalRotation(rightUpperArm, rightArmBase * Quaternion.Euler(armDegrees, 0f, 0f));
        }
    }

    private static Quaternion ResolveFallbackBoneBaseRotation(InteractiveMotionState state, HumanBodyBones bone, Transform boneTransform)
    {
        if (!state.fallbackBoneBaseLocalRotations.TryGetValue(bone, out Quaternion baseRotation))
        {
            baseRotation = boneTransform.localRotation;
            state.fallbackBoneBaseLocalRotations[bone] = baseRotation;
        }
        return baseRotation;
    }

    // --- Animal gesture / moving pose (replays the last real tracked pose, rigidly remapped) --

    private void CacheLiveAnimalPoseForInteractiveMotion(uint trackId, AnimalPoseWorldData pose, Vector3 basePosition, Quaternion baseRotation, bool hasSmalPose, AnimalSmalPose smalPose)
    {
        InteractiveMotionState state = GetOrCreateInteractiveMotionState(trackId);
        state.hasCachedAnimalPose = true;
        state.cachedAnimalPose = pose;
        state.cachedAnimalBasePosition = basePosition;
        state.cachedAnimalBaseRotation = baseRotation;
        state.cachedAnimalHasSmalPose = hasSmalPose;
        state.cachedAnimalSmalPose = smalPose;
    }

    private void ApplyFrozenAnimalGesture(GameObject instance, InteractiveMotionState state, RuntimeClock.TickContext tick)
    {
        if (!state.hasCachedAnimalPose)
        {
            return;
        }

        AnimalPoseWorldData pose = RemapAnimalPoseRigid(state.cachedAnimalPose, state.cachedAnimalBasePosition, state.cachedAnimalBaseRotation, state.gesturePosition, state.gestureRotation);
        float elapsed = RuntimeClock.ResolveElapsed(tick.now, state.phaseStartTime);
        float wave = Mathf.Sin(elapsed * Mathf.PI * 1.5f);

        AnimalGesturePose gestureOverlayClip = null;
        float gestureOverlayNormalizedTime = 0f;
        if (!state.cachedAnimalHasSmalPose)
        {
            switch (state.animalPreset)
            {
                case InteractiveAnimalPreset.BodyTurnViewer:
                    ApplyAnimalBodyTurnViewer(instance.transform, state.lastScreen, ref pose, 1f);
                    break;
                case InteractiveAnimalPreset.TailWag:
                    ApplyAnimalTailWag(state.lastScreen, ref pose, wave);
                    break;
                case InteractiveAnimalPreset.PawWave:
                    ApplyAnimalPawWave(state.lastScreen, ref pose, wave, 1f);
                    break;
            }
        }
        if (state.animalGestureClip != null)
        {
            // Applied post-FK in AnimalPoseApplier (ApplyGestureOverlay), not on pose.animalControl
            // here, so it works whether this track's pose came from SMAL or animal control targets.
            // On a SMAL track this runs *together* with FaceViewer's turn (PickAnimalPreset sets
            // both at once there), not instead of it - the two don't compete for the root the
            // way the pre-FK presets above do.
            gestureOverlayClip = state.animalGestureClip;
            gestureOverlayNormalizedTime = state.phaseDuration > 0.0001f ? Mathf.Clamp01(elapsed / state.phaseDuration) : 0f;
        }

        ApplyAnimalPoseRequest(instance, pose, state.cachedAnimalHasSmalPose, state.cachedAnimalSmalPose, state.gesturePosition, state.gestureRotation, tick, gestureOverlayClip, gestureOverlayNormalizedTime);
    }

    private void ApplyMovingAnimalPose(GameObject instance, InteractiveMotionState state, Vector3 position, Quaternion rotation, RuntimeClock.TickContext tick)
    {
        if (!state.hasCachedAnimalPose)
        {
            TrackPlacementWriter.Apply(instance.transform, new TrackPlacementCommand(position, rotation, instance.transform.localScale));
            return;
        }

        AnimalPoseWorldData pose = RemapAnimalPoseRigid(state.cachedAnimalPose, state.cachedAnimalBasePosition, state.cachedAnimalBaseRotation, position, rotation);

        if (tick.frameCount % 90 == 0)
        {
            Transform root = instance != null ? instance.transform : null;
            Animator anim = root != null ? root.GetComponentInChildren<Animator>() : null;
            Transform rigRoot = anim != null ? anim.transform : root;
            Transform spineBone = FindDeepChildByName(rigRoot, "spine");
            Transform neckBone = FindDeepChildByName(rigRoot, "neck");
            Transform headBone = FindDeepChildByName(rigRoot, "head");
            string spineFwd = spineBone != null ? spineBone.forward.ToString("F3") : "null";
            string neckFwd = neckBone != null ? neckBone.forward.ToString("F3") : "null";
            string headFwd = headBone != null ? headBone.forward.ToString("F3") : "null";
            string goEuler = state.cachedAnimalHasSmalPose ? state.cachedAnimalSmalPose.globalOrient.eulerAngles.ToString("F1") : "N/A";
            Debug.Log($"[FRAMEOUT-DBG] model={instance.name} hasSmalPose={state.cachedAnimalHasSmalPose} globalOrient.euler={goEuler} spine.fwd={spineFwd} neck.fwd={neckFwd} head.fwd={headFwd} frameOutDir={state.frameOutDirection:F3}");
        }

        float loopTime = 0f;
        if (state.animalWalkClip != null)
        {
            // Same per-point curve data as the static gesture preset, but looped instead of
            // played once - this is what turns the otherwise rigid walk/walk-back translation
            // into a leg cycle, on any model AnimalRigDefinition can resolve, regardless of
            // whether this track's pose came from SMAL or animal control targets.
            float elapsed = RuntimeClock.ResolveElapsed(tick.now, state.phaseStartTime);
            float clipDuration = Mathf.Max(0.0001f, state.animalWalkClip.duration);
            loopTime = (elapsed % clipDuration) / clipDuration;
        }
        ApplyAnimalPoseRequest(instance, pose, state.cachedAnimalHasSmalPose, state.cachedAnimalSmalPose, position, rotation, tick, state.animalWalkClip, loopTime);
    }

    private void ApplyAnimalPoseRequest(GameObject instance, AnimalPoseWorldData pose, bool hasSmalPose, AnimalSmalPose smalPose, Vector3 targetPosition, Quaternion targetRotation, RuntimeClock.TickContext tick, AnimalGesturePose gestureOverlayClip, float gestureOverlayNormalizedTime)
    {
        Animator animator = instance.GetComponentInChildren<Animator>();
        DisableAnimalAnimatorPlayback(animator);
        animalPoseApplier.Apply(new AnimalPoseRequest
        {
            instanceRoot = instance.transform,
            animator = animator,
            pose = pose,
            settings = BuildAnimalPoseSettings(),
            tickContext = tick,
            freezeAnimalDistal = false,
            enableBoneApply = enableBoneApply,
            hasSmalPose = hasSmalPose,
            smalPose = smalPose,
            gestureOverlayClip = gestureOverlayClip,
            gestureOverlayNormalizedTime = gestureOverlayNormalizedTime
        });

        // AnimalPoseApplier re-aligns and low-pass-filters the root from the solved bone
        // placement, which drifts away from the intended walk/freeze height over time. Re-pin
        // the root explicitly; the limb/head/tail bones are children, so this only rigidly
        // carries the already-solved pose along with it, it does not undo their solving.
        TrackPlacementWriter.Apply(instance.transform, new TrackPlacementCommand(targetPosition, targetRotation, instance.transform.localScale));
    }

    private static AnimalPoseWorldData RemapAnimalPoseRigid(AnimalPoseWorldData basePose, Vector3 basePosition, Quaternion baseRotation, Vector3 newPosition, Quaternion newRotation)
    {
        // Yaw-only delta: the cached pose's rotation can carry pitch/roll from the animal's
        // natural body tilt. Remapping with the full 3D delta would "un-tilt" the cached pose
        // around basePosition, shifting every point's height. Walking/turning should only
        // ever rotate the cached pose around the world-up axis.
        Quaternion delta = ResolveYawOnlyRotation(newRotation, Vector3.up) * Quaternion.Inverse(ResolveYawOnlyRotation(baseRotation, Vector3.up));
        Vector3 Map(Vector3 point) => newPosition + delta * (point - basePosition);

        AnimalPoseWorldData result = basePose;
        result.rootWorld = Map(basePose.rootWorld);
        result.jointsWorld = MapPoints(basePose.jointsWorld, Map);
        if (basePose.hasAnimalControl)
        {
            result.animalControl = RemapAnimalControlRigid(basePose.animalControl, Map);
        }
        return result;
    }


    private static Quaternion ResolveYawOnlyRotation(Quaternion rotation, Vector3 upAxis)
    {
        Vector3 safeUp = upAxis.sqrMagnitude > 0.000001f ? upAxis.normalized : Vector3.up;
        Vector3 forward = Vector3.ProjectOnPlane(rotation * Vector3.forward, safeUp);
        if (forward.sqrMagnitude <= 0.000001f)
        {
            forward = Vector3.ProjectOnPlane(Vector3.forward, safeUp);
        }
        if (forward.sqrMagnitude <= 0.000001f)
        {
            forward = Vector3.right;
        }
        return Quaternion.LookRotation(forward.normalized, safeUp);
    }

    private static Vector3[] MapPoints(Vector3[] source, Func<Vector3, Vector3> map)
    {
        if (source == null)
        {
            return null;
        }

        Vector3[] result = new Vector3[source.Length];
        for (int i = 0; i < source.Length; i++)
        {
            result[i] = map(source[i]);
        }
        return result;
    }

    private static AnimalControlWorldData RemapAnimalControlRigid(AnimalControlWorldData control, Func<Vector3, Vector3> map)
    {
        AnimalControlWorldData result = control;
        if (control.hasRoot) result.rootWorld = map(control.rootWorld);
        if (control.hasWithers) result.withersWorld = map(control.withersWorld);
        if (control.hasHeadRoot) result.headRootWorld = map(control.headRootWorld);
        if (control.hasHeadTip) result.headTipWorld = map(control.headTipWorld);
        if (control.hasTailBase) result.tailBaseWorld = map(control.tailBaseWorld);
        if (control.hasTailTip) result.tailTipWorld = map(control.tailTipWorld);
        if (control.hasForwardHint) result.forwardHintWorld = map(control.forwardHintWorld);
        if (control.hasUpHint) result.upHintWorld = map(control.upHintWorld);
        result.frontLeftLegWorld = MapPoints(control.frontLeftLegWorld, map);
        result.frontRightLegWorld = MapPoints(control.frontRightLegWorld, map);
        result.rearLeftLegWorld = MapPoints(control.rearLeftLegWorld, map);
        result.rearRightLegWorld = MapPoints(control.rearRightLegWorld, map);
        result.headWorld = MapPoints(control.headWorld, map);
        result.tailWorld = MapPoints(control.tailWorld, map);
        return result;
    }

    private InteractiveAnimalPreset PickAnimalPreset(bool cachedHasSmalPose, out AnimalGesturePose dataDrivenClip)
    {
        dataDrivenClip = null;
        bool hasDataDrivenClips = animalStaticGestureClips != null && animalStaticGestureClips.Length > 0;
        int value = UnityEngine.Random.Range(0, 100);

        if (cachedHasSmalPose)
        {
            // TailWag/PawWave/BodyTurnViewer perturb AnimalControlWorldData pre-FK, so they
            // stay unavailable on SMAL tracks - FaceViewer (turning toward the viewer) is the
            // only thing left that can run pre-FK. DataDrivenClip applies post-FK (see
            // AnimalPoseApplier.ApplyGestureOverlay) so it works here too, and unlike the
            // pre-FK presets it doesn't compete with FaceViewer for the root - both can run at
            // once, so always combine them instead of picking one.
            if (hasDataDrivenClips)
            {
                dataDrivenClip = animalStaticGestureClips[UnityEngine.Random.Range(0, animalStaticGestureClips.Length)];
            }
            return InteractiveAnimalPreset.FaceViewer;
        }

        if (!hasDataDrivenClips)
        {
            if (value < 30) return InteractiveAnimalPreset.FaceViewer;
            if (value < 55) return InteractiveAnimalPreset.TailWag;
            if (value < 80) return InteractiveAnimalPreset.PawWave;
            return InteractiveAnimalPreset.BodyTurnViewer;
        }

        // With data-driven clips available, carve out a flat 20% chance for them and scale
        // the existing hand-coded presets down proportionally so their relative weights
        // (30/25/25/20) are unchanged among themselves.
        if (value < 24) return InteractiveAnimalPreset.FaceViewer;
        if (value < 44) return InteractiveAnimalPreset.TailWag;
        if (value < 64) return InteractiveAnimalPreset.PawWave;
        if (value < 80) return InteractiveAnimalPreset.BodyTurnViewer;
        dataDrivenClip = animalStaticGestureClips[UnityEngine.Random.Range(0, animalStaticGestureClips.Length)];
        return InteractiveAnimalPreset.DataDrivenClip;
    }

    private AnimalGesturePose PickAnimalWalkClip()
    {
        if (animalWalkClips == null || animalWalkClips.Length == 0)
        {
            return null;
        }

        return animalWalkClips[UnityEngine.Random.Range(0, animalWalkClips.Length)];
    }

    private void ApplyAnimalBodyTurnViewer(Transform instanceRoot, Transform screen, ref AnimalPoseWorldData pose, float weight)
    {
        if (!pose.hasAnimalControl || weight <= 0f)
        {
            return;
        }

        Vector3 root = pose.animalControl.hasRoot ? pose.animalControl.rootWorld : pose.rootWorld;
        Vector3 up = ResolveInteractiveUpAxis(screen, pose.animalControl, instanceRoot);
        Vector3 currentForward = ResolveAnimalControlForward(pose.animalControl, instanceRoot, up);
        if (currentForward.sqrMagnitude <= 0.000001f)
        {
            return;
        }

        Vector3 toViewer = ResolveInteractiveViewerDirection(root, screen, instanceRoot, up);
        if (toViewer.sqrMagnitude <= 0.000001f)
        {
            return;
        }

        float maxRadians = AnimalBodyTurnMaxDegrees * Mathf.Deg2Rad * Mathf.Clamp01(weight);
        Vector3 turnedForward = Vector3.RotateTowards(currentForward.normalized, toViewer.normalized, maxRadians, 0f);
        if (turnedForward.sqrMagnitude <= 0.000001f)
        {
            return;
        }

        float hintLength = ResolveAnimalForwardHintLength(pose.animalControl, root);
        pose.animalControl.hasRoot = true;
        pose.animalControl.rootWorld = root;
        pose.animalControl.hasUpHint = true;
        pose.animalControl.upHintWorld = root + up * ResolveAnimalUpHintLength(pose.animalControl, root);
        pose.animalControl.hasForwardHint = true;
        pose.animalControl.forwardHintWorld = root + turnedForward.normalized * hintLength;
    }

    private void ApplyAnimalTailWag(Transform screen, ref AnimalPoseWorldData pose, float wave)
    {
        if (!pose.hasAnimalControl || !pose.animalControl.hasTailBase || !pose.animalControl.hasTailTip)
        {
            return;
        }

        Vector3 right = screen != null ? screen.right : Vector3.right;
        Vector3 basePos = pose.animalControl.tailBaseWorld;
        Vector3 tip = pose.animalControl.tailTipWorld;
        float length = Mathf.Max(0.04f, Vector3.Distance(basePos, tip));
        pose.animalControl.tailTipWorld = tip + right * (length * 0.35f * wave);
    }

    private void ApplyAnimalPawWave(Transform screen, ref AnimalPoseWorldData pose, float wave, float weight)
    {
        Vector3 up = screen != null ? screen.up : Vector3.up;
        Vector3 right = screen != null ? screen.right : Vector3.right;
        Vector3 offset = up * (0.10f * Mathf.Abs(wave)) + right * (0.04f * wave);

        if (pose.hasAnimalControl && pose.animalControl.frontRightLegWorld != null && pose.animalControl.frontRightLegWorld.Length > 0)
        {
            int end = pose.animalControl.frontRightLegWorld.Length - 1;
            pose.animalControl.frontRightLegWorld[end] += offset;
        }

        if (pose.jointsWorld != null && pose.jointsWorld.Length > 14)
        {
            pose.jointsWorld[14] += offset * Mathf.Clamp01(weight);
            if (pose.jointVis != null && pose.jointVis.Length > 14)
            {
                pose.jointVis[14] = 1;
            }
        }
    }

    private Vector3 ResolveInteractiveViewerDirection(Vector3 root, Transform screen, Transform instanceRoot, Vector3 up)
    {
        Transform head = GetViewOrHeadTransform();
        Vector3 direction = head != null ? head.position - root : Vector3.zero;
        if (direction.sqrMagnitude <= 0.000001f)
        {
            direction = screen != null ? -screen.forward : (instanceRoot != null ? instanceRoot.forward : Vector3.forward);
        }

        direction = Vector3.ProjectOnPlane(direction, up);
        return direction.sqrMagnitude > 0.000001f ? direction.normalized : Vector3.zero;
    }

    private static Vector3 ResolveInteractiveUpAxis(Transform screen, AnimalControlWorldData control, Transform instanceRoot)
    {
        Vector3 up = Vector3.zero;
        if (control.hasRoot && control.hasUpHint)
        {
            up = control.upHintWorld - control.rootWorld;
        }
        if (up.sqrMagnitude <= 0.000001f && screen != null)
        {
            up = screen.up;
        }
        if (up.sqrMagnitude <= 0.000001f && instanceRoot != null)
        {
            up = instanceRoot.up;
        }
        if (up.sqrMagnitude <= 0.000001f)
        {
            up = Vector3.up;
        }

        return up.normalized;
    }

    private static Vector3 ResolveAnimalControlForward(AnimalControlWorldData control, Transform instanceRoot, Vector3 up)
    {
        Vector3 root = control.hasRoot ? control.rootWorld : Vector3.zero;
        Vector3 forward = Vector3.zero;
        if (control.hasRoot && control.hasForwardHint)
        {
            forward = control.forwardHintWorld - root;
        }
        else if (control.hasRoot && control.hasWithers)
        {
            forward = control.withersWorld - root;
        }
        else if (instanceRoot != null)
        {
            forward = instanceRoot.forward;
        }

        forward = Vector3.ProjectOnPlane(forward, up);
        return forward.sqrMagnitude > 0.000001f ? forward.normalized : Vector3.zero;
    }

    private static float ResolveAnimalForwardHintLength(AnimalControlWorldData control, Vector3 root)
    {
        if (control.hasForwardHint)
        {
            return Mathf.Max(0.05f, Vector3.Distance(root, control.forwardHintWorld));
        }
        if (control.hasWithers)
        {
            return Mathf.Max(0.05f, Vector3.Distance(root, control.withersWorld));
        }

        return 0.25f;
    }

    private static float ResolveAnimalUpHintLength(AnimalControlWorldData control, Vector3 root)
    {
        if (control.hasUpHint)
        {
            return Mathf.Max(0.05f, Vector3.Distance(root, control.upHintWorld));
        }

        return 0.25f;
    }

    // --- Shared viewer-facing rotation helper -------------------------------------------

    private Quaternion ResolveFaceViewerRotation(Vector3 position, Quaternion fallbackRotation, Transform screen)
    {
        Vector3 upAxis = screen != null ? screen.up : Vector3.up;
        Transform viewer = GetViewOrHeadTransform();
        Vector3 fallbackForward = screen != null ? -screen.forward : Vector3.forward;
        Vector3 viewerPosition = viewer != null ? viewer.position : position - fallbackForward;
        return ResolveHumanoidViewerFacingRotation(position, fallbackRotation, upAxis, viewerPosition, true, fallbackForward);
    }

    // AnimalSmalFkApplier.TryApplyAnimalSmalFk now prepends instanceRoot's own yaw onto its FK
    // root rotation, the same way a Humanoid Avatar's local joint rotations naturally compose
    // with its root through Unity's transform hierarchy (see ShouldUseHumanSmplRootOrientation).
    // But unlike Human, there is no Avatar-standardized "local +Z is the nose" guarantee for an
    // animal rig, so naively assigning Quaternion.LookRotation(toViewer, up) - which points
    // local +Z at toViewer - does not reliably point the real nose there. Instead, measure
    // which way the model is actually facing right now (AnimalPoseApplier.
    // TryGetCurrentNoseWorldDirection) and turn fallbackRotation (the track's current real
    // rotation) by the angle from there to toViewer - the same "measure live state, apply a
    // relative delta" approach AnimalFrameOutMotion.ResolveRotation already uses successfully
    // for the system frame-out trigger.
    private Quaternion ResolveAnimalFaceViewerRotation(GameObject instance, Vector3 position, Quaternion fallbackRotation, Transform screen)
    {
        Vector3 upAxis = screen != null ? screen.up : Vector3.up;
        Transform viewer = GetViewOrHeadTransform();
        Vector3 fallbackForward = screen != null ? -screen.forward : Vector3.forward;
        Vector3 viewerPosition = viewer != null ? viewer.position : position - fallbackForward;
        Vector3 toViewer = Vector3.ProjectOnPlane(viewerPosition - position, upAxis);
        return ResolveAnimalTurnedRotation(instance, fallbackRotation, toViewer, upAxis);
    }

    private Quaternion ResolveAnimalTurnedRotation(GameObject instance, Quaternion currentRotation, Vector3 targetDirectionWorld, Vector3 upAxis)
    {
        Vector3 targetFlat = Vector3.ProjectOnPlane(targetDirectionWorld, upAxis);
        if (instance == null || targetFlat.sqrMagnitude <= 0.000001f ||
            !animalPoseApplier.TryGetCurrentNoseWorldDirection(instance.transform, out Vector3 noseWorld))
        {
            return currentRotation;
        }

        Vector3 noseFlat = Vector3.ProjectOnPlane(noseWorld, upAxis);
        if (noseFlat.sqrMagnitude <= 0.000001f)
        {
            return currentRotation;
        }

        float signedAngle = Vector3.SignedAngle(noseFlat, targetFlat, upAxis);
        return Quaternion.AngleAxis(signedAngle, upAxis) * currentRotation;
    }

    public static Quaternion ResolveHumanoidViewerFacingRotation(
        Vector3 rootPosition,
        Quaternion fallbackRotation,
        Vector3 upAxis,
        Vector3 viewerPosition,
        bool hasViewer,
        Vector3 fallbackForward)
    {
        Vector3 safeUp = upAxis.sqrMagnitude > 0.000001f ? upAxis.normalized : Vector3.up;
        Vector3 toViewer = hasViewer ? Vector3.ProjectOnPlane(viewerPosition - rootPosition, safeUp) : Vector3.zero;
        if (toViewer.sqrMagnitude <= 0.000001f)
        {
            return MakeUprightYawRotation(fallbackRotation, safeUp, fallbackForward);
        }

        return Quaternion.LookRotation(toViewer.normalized, safeUp);
    }

    private static Quaternion MakeUprightYawRotation(Quaternion sourceRotation, Vector3 upAxis, Vector3 fallbackForward)
    {
        Vector3 safeUp = upAxis.sqrMagnitude > 0.000001f ? upAxis.normalized : Vector3.up;
        Vector3 forward = Vector3.ProjectOnPlane(sourceRotation * Vector3.forward, safeUp);
        if (forward.sqrMagnitude <= 0.000001f)
        {
            forward = Vector3.ProjectOnPlane(fallbackForward, safeUp);
        }
        if (forward.sqrMagnitude <= 0.000001f)
        {
            forward = Vector3.ProjectOnPlane(Vector3.forward, safeUp);
        }

        return Quaternion.LookRotation(forward.normalized, safeUp);
    }

    // --- Lifecycle -----------------------------------------------------------------------

    // --- Else（剛体）: 走って近づく → U ターン → 走って戻る（2026-09-25） --------------------
    //
    // 車にはリグもクリップも無いので、root の移動と yaw だけで作る。経路は ElseDrivePath（純粋な幾何）。
    // 向きは出発時の回転を「経路の接線と出発時の向きの差」だけ yaw で回す（ピッチ・ロールは手動キーの値のまま）。
    // 前後のホイール（FL/FR/RL/RR）から車の前方向を決め、走った距離ぶんホイールを回す。
    // ホイールが無いモデル（ボール等）は向きを変えずに経路の上を滑る。
    // 終点は出発位置・出発時の向きなので、ハンドオフで跳ばない（動画は止めているので追従の姿勢も同じ）。

    public struct RigidWheel
    {
        public Transform transform;
        public Quaternion relativeRotation;   // 出発時の root 回転から見たホイールの world 回転
        public Quaternion baseLocalRotation;  // 復元用
        public float radius;
        public bool isFront;
    }

    // U ターンの半径。car クリップの車は world で 0.2〜0.5 m あり（popout の距離に対して大きい）、
    // 車長 × 0.6 では視聴者までの余地（0.2〜0.4 m）に入らなかった（2026-09-25 バッチ実測）。
    // おもちゃの車のように小回りさせる: 車長 × 0.25、上限 0.12 m、さらに余地に合わせて縮める。
    private const float ElseTurnRadiusFromLengthFactor = 0.25f;
    private const float ElseTurnRadiusMin = 0.02f;
    private const float ElseTurnRadiusMax = 0.12f;

    private bool FrameHasPersonOrAnimal()
    {
        for (int i = 0; i < metaFrameObjects.Count; i++)
        {
            byte category = metaFrameObjects[i].categoryId;
            if (IsCategoryPerson(category) || IsCategoryAnimal(category))
            {
                return true;
            }
        }
        return false;
    }

    // 実験の集計とバッチの確認用: どれかのトラックでイベント中（Owned か HandoffBlend）か。
    public bool IsAnyInteractiveMotionActive()
    {
        foreach (KeyValuePair<uint, InteractiveMotionState> kv in interactiveMotionByTrack)
        {
            if (kv.Value != null && kv.Value.stage != InteractiveEventStage.Inactive)
            {
                return true;
            }
        }
        return false;
    }

    private bool StartRigidDriveMotion(uint trackId, float now)
    {
        GameObject instance = GetTrackInstanceOrNull(trackId);
        InteractiveMotionState state = GetOrCreateInteractiveMotionState(trackId);
        if (instance == null || !state.hasLiveSample || state.stage != InteractiveEventStage.Inactive)
        {
            return false;
        }

        Vector3 up = Vector3.up;
        Transform viewer = GetViewOrHeadTransform();
        Vector3 forwardFallback = state.lastScreen != null ? -state.lastScreen.forward : Vector3.forward;
        Vector3 viewerPosition = viewer != null ? viewer.position : state.livePosition + forwardFallback;
        Vector3 toViewer = Vector3.ProjectOnPlane(viewerPosition - state.livePosition, up);
        float stopDistance = Mathf.Max(0.05f, elseApproachStopDistanceMeters);
        if (toViewer.magnitude <= stopDistance + 0.01f)
        {
            Debug.Log($"[MOTION] rigid drive skipped track={trackId}: viewer too close ({toViewer.magnitude:F2}m <= stop {stopDistance:F2}m)");
            return false;
        }
        Vector3 stop = PreserveHeight(viewerPosition - toViewer.normalized * stopDistance, state.livePosition, up);

        // 車の前方向: 前後のホイールの中点の差（root ローカル）。無ければ視聴者方向を向いていることにして、向きは変えない。
        state.rigidWheels.Clear();
        bool hasForward = TryResolveRigidWheels(instance, state.liveRotation, state.rigidWheels, out Vector3 forwardLocal);
        Vector3 headingWorld = hasForward ? Vector3.ProjectOnPlane(state.liveRotation * forwardLocal, up) : toViewer;
        if (headingWorld.sqrMagnitude <= 0.000001f)
        {
            headingWorld = toViewer;
        }
        headingWorld.Normalize();
        Vector3 upLocal = Quaternion.Inverse(state.liveRotation) * up;
        state.rigidAxleLocal = hasForward ? Vector3.Cross(upLocal, forwardLocal).normalized : Vector3.right;

        float radius = elseTurnRadiusMeters > 0f
            ? elseTurnRadiusMeters
            : Mathf.Clamp(ResolveRigidLengthAlong(instance, headingWorld) * ElseTurnRadiusFromLengthFactor, ElseTurnRadiusMin, ElseTurnRadiusMax);
        float driveDistance = Vector3.Distance(state.livePosition, stop);
        // 余地が短いときは半径を縮めて収める（下限 ElseTurnRadiusMin）。
        radius = Mathf.Min(radius, Mathf.Max(ElseTurnRadiusMin, (driveDistance - 0.02f) * 0.5f));
        if (driveDistance < ElseDrivePath.MinimumDriveDistance(radius))
        {
            Debug.Log($"[MOTION] rigid drive skipped track={trackId}: drive {driveDistance:F2}m < minimum {ElseDrivePath.MinimumDriveDistance(radius):F2}m (radius {radius:F3}m)");
            state.rigidWheels.Clear();
            return false;
        }

        state.subject = InteractiveMotionSubject.Rigid;
        state.triggerSource = InteractiveTriggerSource.Random;
        state.kind = InteractiveEventKind.Dynamic;
        state.dynamicPhase = InteractiveDynamicPhase.WalkIn;
        state.originPosition = state.livePosition;
        state.originRotation = state.liveRotation;
        state.driveHeading = headingWorld;
        state.drivePath = ElseDrivePath.Build(state.originPosition, headingWorld, stop, up, radius);
        state.driveSpeed = Mathf.Max(0.05f, elseDriveSpeedMetersPerSecond);
        state.driveLastDistance = 0f;
        state.driveSegmentLogged = -1;
        state.wheelAngleDegrees = 0f;
        state.phaseStartTime = now;
        state.phaseDuration = state.drivePath.TotalLength / state.driveSpeed;
        state.stage = InteractiveEventStage.Owned;
        BeginRandomInteractiveMotionVideoPause(trackId);
        ExperimentLog.Interaction(trackId, "random_Dynamic", $"subject=else wheels={state.rigidWheels.Count} radius_m={ExperimentCsv.Format(radius)}");
        Debug.Log(
            $"[MOTION] rigid drive start track={trackId} wheels={state.rigidWheels.Count} hasForward={hasForward} " +
            $"radius={radius:F3}m drive={driveDistance:F2}m total={state.drivePath.TotalLength:F2}m duration={state.phaseDuration:F2}s");
        LogRigidSegmentIfEntered(trackId, state, ElseDrivePath.SegmentDriveIn);
        return true;
    }

    private static readonly string[] RigidFrontWheelNames = { "FL", "FR" };
    private static readonly string[] RigidRearWheelNames = { "RL", "RR" };

    // FL/FR/RL/RR という名前の子（大文字小文字は区別しない）を探す。前後とも見つかったときだけ前方向を返す。
    private static bool TryResolveRigidWheels(GameObject instance, Quaternion rootRotation, List<RigidWheel> wheels, out Vector3 forwardLocal)
    {
        forwardLocal = Vector3.forward;
        Transform[] all = instance.GetComponentsInChildren<Transform>(true);
        Vector3 frontSum = Vector3.zero;
        Vector3 rearSum = Vector3.zero;
        int frontCount = 0;
        int rearCount = 0;
        Quaternion inverseRoot = Quaternion.Inverse(rootRotation);
        for (int i = 0; i < all.Length; i++)
        {
            Transform t = all[i];
            if (t == null || t == instance.transform)
            {
                continue;
            }
            bool isFront = MatchesAny(t.name, RigidFrontWheelNames);
            bool isRear = !isFront && MatchesAny(t.name, RigidRearWheelNames);
            if (!isFront && !isRear)
            {
                continue;
            }
            Renderer renderer = t.GetComponentInChildren<Renderer>();
            float radius = renderer != null
                ? Mathf.Max(renderer.bounds.extents.x, Mathf.Max(renderer.bounds.extents.y, renderer.bounds.extents.z))
                : 0.01f;
            wheels.Add(new RigidWheel
            {
                transform = t,
                relativeRotation = inverseRoot * t.rotation,
                baseLocalRotation = t.localRotation,
                radius = Mathf.Max(0.002f, radius),
                isFront = isFront,
            });
            Vector3 local = instance.transform.InverseTransformPoint(t.position);
            if (isFront)
            {
                frontSum += local;
                frontCount++;
            }
            else
            {
                rearSum += local;
                rearCount++;
            }
        }

        if (frontCount == 0 || rearCount == 0)
        {
            return false;
        }
        Vector3 axis = frontSum / frontCount - rearSum / rearCount;
        if (axis.sqrMagnitude <= 0.000001f)
        {
            return false;
        }
        forwardLocal = axis.normalized;
        return true;
    }

    private static bool MatchesAny(string name, string[] candidates)
    {
        for (int i = 0; i < candidates.Length; i++)
        {
            if (string.Equals(name, candidates[i], System.StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    // 車体の長さ（world の描画 bounds を進行方向へ射影した幅）。
    private static float ResolveRigidLengthAlong(GameObject instance, Vector3 directionWorld)
    {
        Renderer[] renderers = instance.GetComponentsInChildren<Renderer>(false);
        float min = float.MaxValue;
        float max = float.MinValue;
        Vector3 dir = directionWorld.normalized;
        for (int r = 0; r < renderers.Length; r++)
        {
            if (renderers[r] == null || !renderers[r].enabled)
            {
                continue;
            }
            Bounds b = renderers[r].bounds;
            for (int c = 0; c < 8; c++)
            {
                Vector3 corner = new Vector3(
                    (c & 1) == 0 ? b.min.x : b.max.x,
                    (c & 2) == 0 ? b.min.y : b.max.y,
                    (c & 4) == 0 ? b.min.z : b.max.z);
                float d = Vector3.Dot(corner, dir);
                min = Mathf.Min(min, d);
                max = Mathf.Max(max, d);
            }
        }
        return max > min ? max - min : 0.05f;
    }

    private bool ApplyRigidDrive(uint trackId, GameObject instance, InteractiveMotionState state, RuntimeClock.TickContext tick)
    {
        if (state.drivePath == null || state.drivePath.TotalLength <= 0f)
        {
            BeginHandoff(trackId, state, tick.now);
            return true;
        }

        float elapsed = RuntimeClock.ResolveElapsed(tick.now, state.phaseStartTime);
        float distance = Mathf.Clamp(elapsed * state.driveSpeed, 0f, state.drivePath.TotalLength);
        ElseDrivePath.Sample sample = state.drivePath.Evaluate(distance);
        LogRigidSegmentIfEntered(trackId, state, sample.segment);

        // 向き: 出発時の回転を、経路の接線と出発時の向きの差だけ yaw で回す。ピッチ・ロールは保つ。
        float yaw = Vector3.SignedAngle(state.driveHeading, sample.tangent, Vector3.up);
        Quaternion rotation = Quaternion.AngleAxis(yaw, Vector3.up) * state.originRotation;
        TrackPlacementWriter.Apply(instance.transform, new TrackPlacementCommand(sample.position, rotation, instance.transform.localScale));

        // ホイール: 進んだ距離ぶん車軸まわりに回す（前進なので常に正）。
        float advanced = Mathf.Max(0f, distance - state.driveLastDistance);
        state.driveLastDistance = distance;
        if (state.rigidWheels.Count > 0)
        {
            Vector3 axleWorld = rotation * state.rigidAxleLocal;
            state.wheelAngleDegrees += advanced / state.rigidWheels[0].radius * Mathf.Rad2Deg;
            for (int i = 0; i < state.rigidWheels.Count; i++)
            {
                RigidWheel wheel = state.rigidWheels[i];
                if (wheel.transform == null)
                {
                    continue;
                }
                // relativeRotation で root に対する姿勢を保ちつつ、車軸まわりに回す。
                wheel.transform.rotation = Quaternion.AngleAxis(state.wheelAngleDegrees, axleWorld) * (rotation * wheel.relativeRotation);
            }
        }

        if (distance >= state.drivePath.TotalLength)
        {
            BeginHandoff(trackId, state, tick.now);
        }
        return true;
    }

    private void LogRigidSegmentIfEntered(uint trackId, InteractiveMotionState state, int segment)
    {
        if (segment == state.driveSegmentLogged || state.drivePath == null)
        {
            return;
        }
        state.driveSegmentLogged = segment;
        string phase = segment == ElseDrivePath.SegmentUTurn ? "u_turn" : (segment == ElseDrivePath.SegmentDriveBack ? "drive_back" : "drive_in");
        float length = state.drivePath.segmentLengths[Mathf.Clamp(segment, 0, 2)];
        LogInteractiveMotionPhase(trackId, phase, "else_drive", length / Mathf.Max(0.05f, state.driveSpeed), length);
    }

    private static void RestoreRigidWheels(InteractiveMotionState state)
    {
        for (int i = 0; i < state.rigidWheels.Count; i++)
        {
            if (state.rigidWheels[i].transform != null)
            {
                state.rigidWheels[i].transform.localRotation = state.rigidWheels[i].baseLocalRotation;
            }
        }
        state.rigidWheels.Clear();
        state.drivePath = null;
    }

    private void StopInteractiveMotion(uint trackId)
    {
        if (interactiveMotionByTrack.TryGetValue(trackId, out InteractiveMotionState state) && state != null)
        {
            // Random イベントの途中で止めるなら、動画の一時停止も解く。以前はカウンタが残ったままになり、
            // Motion を OFF にしたあと動画が止まったまま・以後 Random が二度と発火しない状態になり得た（2026-09-25）。
            bool wasRandomActive = state.stage != InteractiveEventStage.Inactive &&
                                   state.triggerSource == InteractiveTriggerSource.Random;
            state.stage = InteractiveEventStage.Inactive;
            state.humanClip = null;
            RestoreRigidWheels(state);
            if (wasRandomActive)
            {
                EndRandomInteractiveMotionVideoPause(trackId);
            }
        }
        StopHumanClipPlayback(trackId);
    }

    // モデルを消された・作り直された track の後片付け。走っていないときは何もしない（毎フレーム呼ばれる経路なので）。
    private void StopInteractiveMotionForHiddenTrack(uint trackId)
    {
        if (!interactiveMotionByTrack.TryGetValue(trackId, out InteractiveMotionState state) ||
            state == null ||
            state.stage == InteractiveEventStage.Inactive)
        {
            return;
        }

        Debug.Log($"[MOTION] stop track={trackId}: モデルが非表示になったので中断（動画の一時停止も解く）");
        StopInteractiveMotion(trackId);
    }

    // このフレームに居ない track の Random モーションだけ片付ける（System = frame-out の walk は対象外。
    // それは「居なくなった track を歩かせる」ものなので、居ないことを理由に止めてはいけない）。
    private void StopRandomInteractiveMotionForAbsentTrack(uint trackId)
    {
        if (!interactiveMotionByTrack.TryGetValue(trackId, out InteractiveMotionState state) ||
            state == null ||
            state.stage == InteractiveEventStage.Inactive ||
            state.triggerSource != InteractiveTriggerSource.Random)
        {
            return;
        }

        Debug.Log($"[MOTION] stop track={trackId}: このフレームに居ないので Random イベントを中断（動画の一時停止も解く）");
        StopInteractiveMotion(trackId);
    }

    private void StopAllInteractiveMotion()
    {
        List<uint> keys = new List<uint>(interactiveMotionByTrack.Keys);
        for (int i = 0; i < keys.Count; i++)
        {
            StopInteractiveMotion(keys[i]);
        }
    }

    private void DisposeInteractiveMotion()
    {
        List<uint> keys = new List<uint>(interactiveClipPlaybackByTrack.Keys);
        for (int i = 0; i < keys.Count; i++)
        {
            StopHumanClipPlayback(keys[i]);
        }
        interactiveMotionByTrack.Clear();
    }

#if UNITY_EDITOR
    private void OnValidate()
    {
        AutoAssignHumanInteractiveClipsInEditor(ref humanStaticGestureClips, HumanStaticClipAssetFolder);
        AutoAssignHumanInteractiveClipsInEditor(ref humanWalkClips, HumanWalkClipAssetFolder);
        AutoAssignAnimalGestureClipsInEditor(ref animalStaticGestureClips, AnimalStaticClipAssetFolder);
        AutoAssignAnimalGestureClipsInEditor(ref animalWalkClips, AnimalWalkClipAssetFolder);
    }

    private void AutoAssignAnimalGestureClipsInEditor(ref AnimalGesturePose[] clips, string folder)
    {
        if (clips != null && clips.Length > 0)
        {
            return;
        }

        string[] guids = UnityEditor.AssetDatabase.FindAssets("t:AnimalGesturePose", new[] { folder });
        if (guids == null || guids.Length == 0)
        {
            return;
        }

        List<AnimalGesturePose> found = new List<AnimalGesturePose>();
        for (int i = 0; i < guids.Length; i++)
        {
            string path = UnityEditor.AssetDatabase.GUIDToAssetPath(guids[i]);
            AnimalGesturePose clip = UnityEditor.AssetDatabase.LoadAssetAtPath<AnimalGesturePose>(path);
            if (clip != null)
            {
                found.Add(clip);
            }
        }

        if (found.Count == 0)
        {
            return;
        }

        clips = found.ToArray();
        UnityEditor.EditorUtility.SetDirty(this);
    }

    private void AutoAssignHumanInteractiveClipsInEditor(ref AnimationClip[] clips, string folder)
    {
        if (clips != null && clips.Length > 0)
        {
            return;
        }

        string[] guids = UnityEditor.AssetDatabase.FindAssets("t:AnimationClip", new[] { folder });
        if (guids == null || guids.Length == 0)
        {
            return;
        }

        List<AnimationClip> found = new List<AnimationClip>();
        for (int i = 0; i < guids.Length; i++)
        {
            string path = UnityEditor.AssetDatabase.GUIDToAssetPath(guids[i]);
            UnityEngine.Object[] assets = UnityEditor.AssetDatabase.LoadAllAssetRepresentationsAtPath(path);
            for (int j = 0; j < assets.Length; j++)
            {
                AnimationClip clip = assets[j] as AnimationClip;
                if (clip == null || clip.name.StartsWith("__preview__", StringComparison.Ordinal))
                {
                    continue;
                }
                found.Add(clip);
            }
        }

        if (found.Count == 0)
        {
            return;
        }

        clips = found.ToArray();
        UnityEditor.EditorUtility.SetDirty(this);
    }
#endif
}
