using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    private const string HumanInteractiveClipAssetFolder = "Assets/Animations/InteractiveMotion/Human";

    private enum InteractiveMotionSubject
    {
        Person,
        Animal
    }

    private enum InteractiveMotionMode
    {
        Overlay,
        Replacement
    }

    private enum InteractiveAnimalPreset
    {
        LookAtViewer,
        TailWag,
        PawWave,
        ApproachViewer
    }

    private enum InteractiveHumanPreset
    {
        ClipInPlace,
        FaceViewer,
        ApproachViewer
    }

    private sealed class InteractiveMotionState
    {
        public bool active;
        public InteractiveMotionSubject subject;
        public InteractiveMotionMode mode;
        public InteractiveAnimalPreset animalPreset;
        public InteractiveHumanPreset humanPreset;
        public float startTime;
        public float duration;
        public float nextTriggerTime;
        public Vector3 startPosition;
        public Quaternion startRotation = Quaternion.identity;
        public Vector3 lastTrackedPosition;
        public Quaternion lastTrackedRotation = Quaternion.identity;
        public bool hasLastTrackedTransform;
        public byte lastCategoryId;
        public Transform lastScreen;
        public AnimationClip humanClip;
    }

    private sealed class InteractiveClipPlayback
    {
        public PlayableGraph graph;
        public AnimationClip clip;
        public Animator animator;
        public bool loop;
        public readonly Dictionary<HumanBodyBones, Transform> bones = new Dictionary<HumanBodyBones, Transform>();
        public readonly Dictionary<HumanBodyBones, Quaternion> beforeLocalRotations = new Dictionary<HumanBodyBones, Quaternion>();
        public readonly Dictionary<HumanBodyBones, Vector3> beforeLocalPositions = new Dictionary<HumanBodyBones, Vector3>();
        public readonly Dictionary<HumanBodyBones, Quaternion> animatedLocalRotations = new Dictionary<HumanBodyBones, Quaternion>();
        public readonly Dictionary<HumanBodyBones, Vector3> animatedLocalPositions = new Dictionary<HumanBodyBones, Vector3>();
    }

    private readonly Dictionary<uint, InteractiveMotionState> interactiveMotionByTrack = new Dictionary<uint, InteractiveMotionState>();
    private readonly Dictionary<uint, InteractiveClipPlayback> interactiveClipPlaybackByTrack = new Dictionary<uint, InteractiveClipPlayback>();

    private void ObserveInteractiveMotionTarget(GameObject instance, MetaObj obj, Transform screen)
    {
        if (instance == null)
        {
            return;
        }

        InteractiveMotionState state = GetOrCreateInteractiveMotionState(obj.trackId);
        state.lastTrackedPosition = instance.transform.position;
        state.lastTrackedRotation = instance.transform.rotation;
        state.hasLastTrackedTransform = true;
        state.lastCategoryId = obj.categoryId;
        state.lastScreen = screen;
    }

    private void UpdateInteractiveMotionSchedule(GameObject instance, MetaObj obj, Transform screen, int frame)
    {
        if (instance == null)
        {
            return;
        }

        ObserveInteractiveMotionTarget(instance, obj, screen);
        if (!enableInteractiveMotion || (!IsCategoryPerson(obj.categoryId) && !IsCategoryAnimal(obj.categoryId)))
        {
            return;
        }

        InteractiveMotionState state = GetOrCreateInteractiveMotionState(obj.trackId);
        float now = Time.time;
        if (state.nextTriggerTime <= 0f)
        {
            state.nextTriggerTime = now + RandomInteractiveInterval();
        }

        if (state.active)
        {
            if (now - state.startTime > state.duration)
            {
                StopInteractiveMotion(obj.trackId);
                state.nextTriggerTime = now + RandomInteractiveInterval();
            }
            return;
        }

        if (now < state.nextTriggerTime)
        {
            return;
        }

        StartInteractiveMotion(obj.trackId, instance, obj, frame, false);
    }

    private bool TryApplyInteractiveFrameOutTrack(uint trackId, int frame)
    {
        if (!enableInteractiveMotion || !trackInstances.TryGetValue(trackId, out GameObject instance) || instance == null)
        {
            return false;
        }

        if (!interactiveMotionByTrack.TryGetValue(trackId, out InteractiveMotionState state) ||
            !state.hasLastTrackedTransform ||
            (!IsCategoryPerson(state.lastCategoryId) && !IsCategoryAnimal(state.lastCategoryId)))
        {
            return false;
        }

        if (!state.active)
        {
            StartInteractiveMotion(trackId, instance, default(MetaObj), frame, true);
        }

        if (!state.active || state.mode != InteractiveMotionMode.Replacement)
        {
            return false;
        }

        instance.SetActive(true);
        ApplyInteractiveReplacementTransform(trackId, instance, state.lastScreen);
        ApplyHumanClipPlayback(trackId, instance);
        if (Time.time - state.startTime > state.duration)
        {
            StopInteractiveMotion(trackId);
        }
        return true;
    }

    private void StartInteractiveMotion(uint trackId, GameObject instance, MetaObj obj, int frame, bool frameOut)
    {
        InteractiveMotionState state = GetOrCreateInteractiveMotionState(trackId);
        bool isAnimal = frameOut ? IsCategoryAnimal(state.lastCategoryId) : IsCategoryAnimal(obj.categoryId);
        bool isPerson = frameOut ? IsCategoryPerson(state.lastCategoryId) : IsCategoryPerson(obj.categoryId);
        if (!isAnimal && !isPerson)
        {
            return;
        }

        state.active = true;
        state.subject = isAnimal ? InteractiveMotionSubject.Animal : InteractiveMotionSubject.Person;
        state.startTime = Time.time;
        state.duration = GetEffectiveInteractiveMotionDuration();
        state.startPosition = instance.transform.position;
        state.startRotation = instance.transform.rotation;
        state.lastTrackedPosition = instance.transform.position;
        state.lastTrackedRotation = instance.transform.rotation;
        state.hasLastTrackedTransform = true;
        if (!frameOut)
        {
            state.lastCategoryId = obj.categoryId;
        }

        if (isPerson)
        {
            state.humanClip = PickHumanInteractiveClip();
            state.humanPreset = PickHumanPreset(frameOut, state.humanClip);
            state.mode = state.humanPreset == InteractiveHumanPreset.ApproachViewer
                ? InteractiveMotionMode.Replacement
                : InteractiveMotionMode.Overlay;
            state.duration = GetHumanInteractiveMotionDuration(state.humanClip, state.humanPreset);
            StartHumanClipPlayback(trackId, instance, state.humanClip);
            return;
        }

        state.animalPreset = PickAnimalPreset(frameOut);
        state.mode = state.animalPreset == InteractiveAnimalPreset.ApproachViewer
            ? InteractiveMotionMode.Replacement
            : InteractiveMotionMode.Overlay;
    }

    private InteractiveMotionState GetOrCreateInteractiveMotionState(uint trackId)
    {
        if (interactiveMotionByTrack.TryGetValue(trackId, out InteractiveMotionState state) && state != null)
        {
            return state;
        }

        state = new InteractiveMotionState
        {
            nextTriggerTime = Time.time + RandomInteractiveInterval()
        };
        interactiveMotionByTrack[trackId] = state;
        return state;
    }

    private float RandomInteractiveInterval()
    {
        float min = Mathf.Max(0.1f, interactiveMotionMinIntervalSeconds);
        float max = Mathf.Max(min, interactiveMotionMaxIntervalSeconds);
        return Random.Range(min, max);
    }

    private AnimationClip PickHumanInteractiveClip()
    {
        if (humanInteractiveClips == null || humanInteractiveClips.Length == 0)
        {
            return null;
        }

        int start = Random.Range(0, humanInteractiveClips.Length);
        for (int i = 0; i < humanInteractiveClips.Length; i++)
        {
            AnimationClip clip = humanInteractiveClips[(start + i) % humanInteractiveClips.Length];
            if (clip != null)
            {
                return clip;
            }
        }

        return null;
    }

    private InteractiveAnimalPreset PickAnimalPreset(bool frameOut)
    {
        if (frameOut)
        {
            return InteractiveAnimalPreset.ApproachViewer;
        }

        int value = Random.Range(0, 4);
        return (InteractiveAnimalPreset)value;
    }

    private InteractiveHumanPreset PickHumanPreset(bool frameOut, AnimationClip clip)
    {
        if (frameOut)
        {
            return InteractiveHumanPreset.ApproachViewer;
        }

        if (IsWalkingHumanClip(clip))
        {
            return InteractiveHumanPreset.ApproachViewer;
        }

        // Most visible in-frame interactions should stay in place. Approach is occasional.
        int value = Random.Range(0, 4);
        if (value == 0)
        {
            return InteractiveHumanPreset.FaceViewer;
        }
        return InteractiveHumanPreset.ClipInPlace;
    }

    private bool IsInteractiveMotionReplacing(uint trackId)
    {
        return enableInteractiveMotion &&
            interactiveMotionByTrack.TryGetValue(trackId, out InteractiveMotionState state) &&
            state != null &&
            state.active &&
            state.mode == InteractiveMotionMode.Replacement;
    }

    private bool IsHumanoidInteractiveMotionInPlace(uint trackId)
    {
        return enableInteractiveMotion &&
            interactiveMotionByTrack.TryGetValue(trackId, out InteractiveMotionState state) &&
            state != null &&
            state.active &&
            state.subject == InteractiveMotionSubject.Person &&
            state.mode != InteractiveMotionMode.Replacement;
    }

    public static bool ShouldFitDisplayedModelToBBoxDuringInteractiveMotion(bool isReplacing, bool isHumanoidInPlace)
    {
        return !isReplacing && !isHumanoidInPlace;
    }

    public static bool ShouldPreserveHumanoidInteractiveRootPosition(bool allowHipsTranslation)
    {
        return !allowHipsTranslation;
    }

    public static Vector3 ResolveHumanoidInteractiveRootPosition(
        Vector3 currentPosition,
        Vector3 startPosition,
        bool allowHipsTranslation)
    {
        return ShouldPreserveHumanoidInteractiveRootPosition(allowHipsTranslation)
            ? startPosition
            : currentPosition;
    }

    private bool TryApplyHumanInteractivePreIk(GameObject instance, uint trackId, Transform screen)
    {
        if (!enableInteractiveMotion ||
            !interactiveMotionByTrack.TryGetValue(trackId, out InteractiveMotionState state) ||
            state == null ||
            !state.active ||
            state.subject != InteractiveMotionSubject.Person)
        {
            return false;
        }

        if (state.mode == InteractiveMotionMode.Replacement)
        {
            ApplyInteractiveReplacementTransform(trackId, instance, screen);
            ApplyHumanClipPlayback(trackId, instance);
            return true;
        }
        else
        {
            ApplyInteractiveFaceViewerTransform(instance, state, screen);
        }

        return false;
    }

    private void ApplyHumanInteractiveOverlay(GameObject instance, uint trackId)
    {
        if (!enableInteractiveMotion ||
            !interactiveMotionByTrack.TryGetValue(trackId, out InteractiveMotionState state) ||
            state == null ||
            !state.active ||
            state.subject != InteractiveMotionSubject.Person ||
            state.mode == InteractiveMotionMode.Replacement)
        {
            return;
        }

        ApplyHumanClipPlayback(trackId, instance);
    }

    private void ApplyInteractiveReplacementTransform(uint trackId, GameObject instance, Transform screen)
    {
        if (instance == null || !interactiveMotionByTrack.TryGetValue(trackId, out InteractiveMotionState state) || state == null)
        {
            return;
        }

        float elapsed = Mathf.Max(0f, Time.time - state.startTime);
        float duration = Mathf.Max(0.001f, state.duration);
        float t = Mathf.Clamp01(elapsed / duration);
        float blend = GetEffectiveInteractiveMotionBlend();
        float inWeight = Mathf.Clamp01(elapsed / blend);
        float outWeight = Mathf.Clamp01((duration - elapsed) / blend);
        float eventWeight = Mathf.Min(inWeight, outWeight);

        Transform head = GetViewOrHeadTransform();
        Vector3 towardHead = Vector3.zero;
        if (head != null)
        {
            towardHead = head.position - state.startPosition;
            Vector3 up = screen != null ? screen.up : Vector3.up;
            towardHead = Vector3.ProjectOnPlane(towardHead, up);
        }
        if (towardHead.sqrMagnitude <= 0.000001f)
        {
            towardHead = screen != null ? -screen.forward : instance.transform.forward;
        }
        towardHead.Normalize();

        Vector3 upAxis = screen != null ? screen.up : Vector3.up;
        float approach = SmoothMotion01(Mathf.Sin(t * Mathf.PI)) * GetEffectiveInteractiveApproachDistance() * eventWeight;
        Vector3 eventPosition = state.startPosition + towardHead * approach;
        Vector3 targetPosition = state.hasLastTrackedTransform ? state.lastTrackedPosition : state.startPosition;
        Vector3 blendedPosition = Vector3.Lerp(targetPosition, eventPosition, eventWeight);
        blendedPosition += upAxis * Vector3.Dot(targetPosition - blendedPosition, upAxis);
        instance.transform.position = blendedPosition;

        Quaternion lookRotation = Quaternion.LookRotation(towardHead, upAxis);
        Quaternion trackedRotation = state.hasLastTrackedTransform ? state.lastTrackedRotation : state.startRotation;
        Quaternion uprightTrackedRotation = MakeUprightYawRotation(trackedRotation, upAxis, instance.transform.forward);
        instance.transform.rotation = Quaternion.Slerp(uprightTrackedRotation, lookRotation, eventWeight);
    }

    private void ApplyInteractiveFaceViewerTransform(GameObject instance, InteractiveMotionState state, Transform screen)
    {
        if (instance == null || state == null)
        {
            return;
        }

        float elapsed = Mathf.Max(0f, Time.time - state.startTime);
        float weight = InteractiveEnvelope(elapsed, state.duration);
        Vector3 upAxis = screen != null ? screen.up : Vector3.up;
        Transform head = GetViewOrHeadTransform();
        Vector3 toViewer = head != null
            ? Vector3.ProjectOnPlane(head.position - instance.transform.position, upAxis)
            : Vector3.zero;
        if (toViewer.sqrMagnitude <= 0.000001f)
        {
            toViewer = screen != null ? -screen.forward : instance.transform.forward;
            toViewer = Vector3.ProjectOnPlane(toViewer, upAxis);
        }
        if (toViewer.sqrMagnitude <= 0.000001f)
        {
            toViewer = Vector3.ProjectOnPlane(Vector3.forward, upAxis);
        }
        toViewer.Normalize();

        Quaternion lookRotation = Quaternion.LookRotation(toViewer, upAxis);
        Quaternion trackedRotation = state.hasLastTrackedTransform ? state.lastTrackedRotation : instance.transform.rotation;
        Quaternion uprightTrackedRotation = MakeUprightYawRotation(trackedRotation, upAxis, instance.transform.forward);
        instance.transform.rotation = Quaternion.Slerp(uprightTrackedRotation, lookRotation, Mathf.Clamp01(weight * 0.75f));
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

    private void ApplyAnimalInteractiveMotion(uint trackId, Transform instanceRoot, Transform screen, ref AnimalPoseWorldData pose)
    {
        if (!enableInteractiveMotion ||
            !interactiveMotionByTrack.TryGetValue(trackId, out InteractiveMotionState state) ||
            state == null ||
            !state.active ||
            state.subject != InteractiveMotionSubject.Animal)
        {
            return;
        }

        float elapsed = Mathf.Max(0f, Time.time - state.startTime);
        float t = Mathf.Clamp01(elapsed / Mathf.Max(0.001f, state.duration));
        float weight = InteractiveEnvelope(elapsed, state.duration);
        float wave = Mathf.Sin(t * Mathf.PI * 6f) * weight;

        if (state.mode == InteractiveMotionMode.Replacement)
        {
            Vector3 before = pose.rootWorld;
            ApplyInteractiveReplacementTransform(trackId, instanceRoot != null ? instanceRoot.gameObject : null, screen);
            Vector3 after = instanceRoot != null ? instanceRoot.position : before;
            Vector3 delta = after - before;
            OffsetAnimalPose(ref pose, delta);
            return;
        }

        switch (state.animalPreset)
        {
            case InteractiveAnimalPreset.LookAtViewer:
                ApplyAnimalLookAtViewer(screen, ref pose, weight);
                break;
            case InteractiveAnimalPreset.TailWag:
                ApplyAnimalTailWag(screen, ref pose, wave);
                break;
            case InteractiveAnimalPreset.PawWave:
                ApplyAnimalPawWave(screen, ref pose, wave, weight);
                break;
        }
    }

    private float InteractiveEnvelope(float elapsed, float duration)
    {
        float blend = GetEffectiveInteractiveMotionBlend();
        return Mathf.Min(Mathf.Clamp01(elapsed / blend), Mathf.Clamp01((duration - elapsed) / blend));
    }

    private float GetEffectiveInteractiveMotionDuration()
    {
        return Mathf.Max(4.5f, interactiveMotionDurationSeconds);
    }

    private float GetHumanInteractiveMotionDuration(AnimationClip clip, InteractiveHumanPreset preset)
    {
        if (preset == InteractiveHumanPreset.ApproachViewer)
        {
            return GetEffectiveInteractiveMotionDuration();
        }

        float clipLength = clip != null && clip.length > 0.0001f ? clip.length : 2.0f;
        return Mathf.Max(clipLength + GetEffectiveInteractiveMotionBlend(), 1.2f);
    }

    private float GetEffectiveInteractiveMotionBlend()
    {
        return Mathf.Max(0.9f, interactiveMotionBlendSeconds);
    }

    private float GetEffectiveInteractiveApproachDistance()
    {
        return Mathf.Clamp(interactiveApproachDistanceMeters, 0f, 0.3f);
    }

    private static float SmoothMotion01(float value)
    {
        value = Mathf.Clamp01(value);
        return value * value * (3f - 2f * value);
    }

    private void ApplyAnimalLookAtViewer(Transform screen, ref AnimalPoseWorldData pose, float weight)
    {
        Transform head = GetViewOrHeadTransform();
        if (head == null || weight <= 0f)
        {
            return;
        }

        Vector3 headRoot;
        if (pose.hasAnimalControl && pose.animalControl.hasHeadRoot)
        {
            headRoot = pose.animalControl.headRootWorld;
        }
        else if (!TryGetAnimalJointWorld(pose, 18, out headRoot))
        {
            headRoot = pose.rootWorld;
        }

        Vector3 toViewer = head.position - headRoot;
        if (toViewer.sqrMagnitude <= 0.000001f)
        {
            return;
        }
        toViewer.Normalize();

        Vector3 currentTip;
        if (pose.hasAnimalControl && pose.animalControl.hasHeadTip)
        {
            currentTip = pose.animalControl.headTipWorld;
        }
        else if (!TryGetAnimalJointWorld(pose, 19, out currentTip))
        {
            currentTip = headRoot + toViewer * 0.12f;
        }

        float length = Mathf.Max(0.05f, Vector3.Distance(headRoot, currentTip));
        Vector3 targetTip = headRoot + toViewer * length;
        Vector3 blendedTip = Vector3.Lerp(currentTip, targetTip, Mathf.Clamp01(weight));

        if (pose.hasAnimalControl)
        {
            pose.animalControl.hasHeadRoot = true;
            pose.animalControl.headRootWorld = headRoot;
            pose.animalControl.hasHeadTip = true;
            pose.animalControl.headTipWorld = blendedTip;
        }
        SetAnimalJointWorld(ref pose, 19, blendedTip);
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

        SetAnimalJointWorld(ref pose, 14, GetAnimalJointWorldOrRoot(pose, 14) + offset * Mathf.Clamp01(weight));
    }

    private bool TryGetAnimalJointWorld(AnimalPoseWorldData pose, int index, out Vector3 value)
    {
        value = Vector3.zero;
        if (pose.jointsWorld == null || index < 0 || index >= pose.jointsWorld.Length)
        {
            return false;
        }
        value = pose.jointsWorld[index];
        return true;
    }

    private Vector3 GetAnimalJointWorldOrRoot(AnimalPoseWorldData pose, int index)
    {
        return TryGetAnimalJointWorld(pose, index, out Vector3 value) ? value : pose.rootWorld;
    }

    private void SetAnimalJointWorld(ref AnimalPoseWorldData pose, int index, Vector3 value)
    {
        if (pose.jointsWorld == null || index < 0 || index >= pose.jointsWorld.Length)
        {
            return;
        }
        pose.jointsWorld[index] = value;
        if (pose.jointVis != null && index < pose.jointVis.Length)
        {
            pose.jointVis[index] = 1;
        }
    }

    private void OffsetAnimalPose(ref AnimalPoseWorldData pose, Vector3 delta)
    {
        pose.rootWorld += delta;
        if (pose.jointsWorld != null)
        {
            for (int i = 0; i < pose.jointsWorld.Length; i++)
            {
                pose.jointsWorld[i] += delta;
            }
        }

        if (!pose.hasAnimalControl)
        {
            return;
        }

        OffsetAnimalControl(ref pose.animalControl, delta);
    }

    private void OffsetAnimalControl(ref AnimalControlWorldData control, Vector3 delta)
    {
        if (control.hasRoot) control.rootWorld += delta;
        if (control.hasWithers) control.withersWorld += delta;
        if (control.hasHeadRoot) control.headRootWorld += delta;
        if (control.hasHeadTip) control.headTipWorld += delta;
        if (control.hasTailBase) control.tailBaseWorld += delta;
        if (control.hasTailTip) control.tailTipWorld += delta;
        if (control.hasForwardHint) control.forwardHintWorld += delta;
        if (control.hasUpHint) control.upHintWorld += delta;
        OffsetChain(control.frontLeftLegWorld, delta);
        OffsetChain(control.frontRightLegWorld, delta);
        OffsetChain(control.rearLeftLegWorld, delta);
        OffsetChain(control.rearRightLegWorld, delta);
        OffsetChain(control.headWorld, delta);
        OffsetChain(control.tailWorld, delta);
    }

    private static void OffsetChain(Vector3[] chain, Vector3 delta)
    {
        if (chain == null)
        {
            return;
        }
        for (int i = 0; i < chain.Length; i++)
        {
            chain[i] += delta;
        }
    }

    private void StartHumanClipPlayback(uint trackId, GameObject instance, AnimationClip clip)
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

        animator.enabled = true;
        animator.applyRootMotion = false;
        PlayableGraph graph = PlayableGraph.Create("InteractiveMotion_" + trackId);
        graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
        AnimationClipPlayable playable = AnimationClipPlayable.Create(graph, clip);
        playable.SetApplyFootIK(false);
        playable.SetApplyPlayableIK(false);
        AnimationPlayableOutput output = AnimationPlayableOutput.Create(graph, "Animation", animator);
        output.SetSourcePlayable(playable);
        graph.Play();
        interactiveClipPlaybackByTrack[trackId] = new InteractiveClipPlayback
        {
            graph = graph,
            clip = clip,
            animator = animator,
            loop = IsWalkingHumanClip(clip)
        };
        CacheHumanoidPlaybackBones(interactiveClipPlaybackByTrack[trackId], animator);
    }

    private void ApplyHumanClipPlayback(uint trackId, GameObject instance)
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
            ApplyFallbackHumanWave(instance, state);
            bool fallbackAllowsHipsTranslation = state.mode == InteractiveMotionMode.Replacement;
            if (ShouldPreserveHumanoidInteractiveRootPosition(fallbackAllowsHipsTranslation) && instance != null)
            {
                instance.transform.position = ResolveHumanoidInteractiveRootPosition(
                    instance.transform.position,
                    state.startPosition,
                    fallbackAllowsHipsTranslation);
            }
            return;
        }

        float elapsed = Mathf.Max(0f, Time.time - state.startTime);
        float weight = InteractiveEnvelope(elapsed, state.duration);
        Vector3 instancePositionBeforePlayback = instance != null ? instance.transform.position : Vector3.zero;
        Transform animatorTransform = playback.animator != null ? playback.animator.transform : null;
        Vector3 animatorLocalPositionBeforePlayback = animatorTransform != null ? animatorTransform.localPosition : Vector3.zero;
        CaptureHumanoidPose(playback.bones, playback.beforeLocalRotations, playback.beforeLocalPositions);

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
        playback.graph.Evaluate(Time.deltaTime > 0.0001f ? Time.deltaTime : (1f / 60f));

        CaptureHumanoidPose(playback.bones, playback.animatedLocalRotations, playback.animatedLocalPositions);
        bool allowHipsTranslation = state.mode == InteractiveMotionMode.Replacement;
        BlendHumanoidPose(playback.bones, playback.beforeLocalRotations, playback.beforeLocalPositions, playback.animatedLocalRotations, playback.animatedLocalPositions, weight, allowHipsTranslation);
        if (ShouldPreserveHumanoidInteractiveRootPosition(allowHipsTranslation) && instance != null)
        {
            instance.transform.position = ResolveHumanoidInteractiveRootPosition(
                instancePositionBeforePlayback,
                state.startPosition,
                allowHipsTranslation);
            if (animatorTransform != null)
            {
                animatorTransform.localPosition = animatorLocalPositionBeforePlayback;
            }
        }
    }

    private static bool IsWalkingHumanClip(AnimationClip clip)
    {
        if (clip == null || string.IsNullOrEmpty(clip.name))
        {
            return false;
        }

        string name = clip.name.ToLowerInvariant();
        return name.Contains("walk") || name.Contains("run") || name.Contains("step") || name.Contains("approach");
    }

    private static void CacheHumanoidPlaybackBones(InteractiveClipPlayback playback, Animator animator)
    {
        if (playback == null || animator == null || !animator.isHuman)
        {
            return;
        }

        foreach (HumanBodyBones boneId in System.Enum.GetValues(typeof(HumanBodyBones)))
        {
            if (boneId == HumanBodyBones.LastBone)
            {
                continue;
            }

            Transform bone = animator.GetBoneTransform(boneId);
            if (bone != null)
            {
                playback.bones[boneId] = bone;
            }
        }
    }

    private static void CaptureHumanoidPose(
        Dictionary<HumanBodyBones, Transform> bones,
        Dictionary<HumanBodyBones, Quaternion> rotations,
        Dictionary<HumanBodyBones, Vector3> positions)
    {
        rotations.Clear();
        positions.Clear();
        foreach (KeyValuePair<HumanBodyBones, Transform> kv in bones)
        {
            Transform bone = kv.Value;
            if (bone == null)
            {
                continue;
            }

            rotations[kv.Key] = bone.localRotation;
            positions[kv.Key] = bone.localPosition;
        }
    }

    private static void BlendHumanoidPose(
        Dictionary<HumanBodyBones, Transform> bones,
        Dictionary<HumanBodyBones, Quaternion> baseRotations,
        Dictionary<HumanBodyBones, Vector3> basePositions,
        Dictionary<HumanBodyBones, Quaternion> animatedRotations,
        Dictionary<HumanBodyBones, Vector3> animatedPositions,
        float weight,
        bool allowHipsTranslation)
    {
        float t = Mathf.Clamp01(weight);
        foreach (KeyValuePair<HumanBodyBones, Transform> kv in bones)
        {
            Transform bone = kv.Value;
            if (bone == null)
            {
                continue;
            }

            if (baseRotations.TryGetValue(kv.Key, out Quaternion baseRotation) &&
                animatedRotations.TryGetValue(kv.Key, out Quaternion animatedRotation))
            {
                bone.localRotation = Quaternion.Slerp(baseRotation, animatedRotation, t);
            }

            if (kv.Key == HumanBodyBones.Hips &&
                basePositions.TryGetValue(kv.Key, out Vector3 basePosition) &&
                animatedPositions.TryGetValue(kv.Key, out Vector3 animatedPosition))
            {
                bone.localPosition = ResolveHumanoidInteractiveLocalPosition(kv.Key, basePosition, animatedPosition, t, allowHipsTranslation);
            }
            else if (basePositions.TryGetValue(kv.Key, out Vector3 preservedPosition))
            {
                bone.localPosition = preservedPosition;
            }
        }
    }

    public static Vector3 ResolveHumanoidInteractiveLocalPosition(
        HumanBodyBones boneId,
        Vector3 basePosition,
        Vector3 animatedPosition,
        float weight,
        bool allowHipsTranslation)
    {
        if (boneId != HumanBodyBones.Hips || !allowHipsTranslation)
        {
            return basePosition;
        }

        return Vector3.Lerp(basePosition, animatedPosition, Mathf.Clamp01(weight));
    }

    private void ApplyFallbackHumanWave(GameObject instance, InteractiveMotionState state)
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

        float elapsed = Mathf.Max(0f, Time.time - state.startTime);
        float t = Mathf.Clamp01(elapsed / Mathf.Max(0.001f, state.duration));
        float weight = InteractiveEnvelope(elapsed, state.duration);
        float wave = Mathf.Sin(t * Mathf.PI * 8f) * weight;
        upper.localRotation = upper.localRotation * Quaternion.Euler(-45f * weight, 0f, 25f * weight);
        lower.localRotation = lower.localRotation * Quaternion.Euler(0f, 0f, 35f * wave);
    }

    private void StopInteractiveMotion(uint trackId)
    {
        if (interactiveMotionByTrack.TryGetValue(trackId, out InteractiveMotionState state) && state != null)
        {
            state.active = false;
            state.humanClip = null;
        }
        StopHumanClipPlayback(trackId);
    }

    private void StopAllInteractiveMotion()
    {
        List<uint> keys = new List<uint>(interactiveMotionByTrack.Keys);
        for (int i = 0; i < keys.Count; i++)
        {
            StopInteractiveMotion(keys[i]);
        }
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
        AutoAssignHumanInteractiveClipsInEditor();
    }

    private void AutoAssignHumanInteractiveClipsInEditor()
    {
        if (humanInteractiveClips != null && humanInteractiveClips.Length > 0)
        {
            return;
        }

        string[] guids = UnityEditor.AssetDatabase.FindAssets("t:AnimationClip", new[] { HumanInteractiveClipAssetFolder });
        if (guids == null || guids.Length == 0)
        {
            return;
        }

        List<AnimationClip> clips = new List<AnimationClip>();
        for (int i = 0; i < guids.Length; i++)
        {
            string path = UnityEditor.AssetDatabase.GUIDToAssetPath(guids[i]);
            UnityEngine.Object[] assets = UnityEditor.AssetDatabase.LoadAllAssetRepresentationsAtPath(path);
            for (int j = 0; j < assets.Length; j++)
            {
                AnimationClip clip = assets[j] as AnimationClip;
                if (clip == null || clip.name.StartsWith("__preview__", System.StringComparison.Ordinal))
                {
                    continue;
                }
                clips.Add(clip);
            }
        }

        if (clips.Count == 0)
        {
            return;
        }

        humanInteractiveClips = clips.ToArray();
        UnityEditor.EditorUtility.SetDirty(this);
    }
#endif
}
