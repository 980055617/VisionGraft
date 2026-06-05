using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    private const string HumanInteractiveClipAssetFolder = "Assets/Animations/InteractiveMotion/Human";
    private const float AnimalBodyTurnMaxDegrees = 35f;
    private const float AnimalFrameOutLoopSeconds = 2.0f;
    private const float AnimalFrameOutSpeedMetersPerSecond = 0.2f;

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
        BodyTurnViewer,
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
        public bool startedFromFrameOut;
        public InteractiveMotionSubject subject;
        public InteractiveMotionMode mode;
        public InteractiveAnimalPreset animalPreset;
        public InteractiveHumanPreset humanPreset;
        public float startTime;
        public float duration;
        public float nextTriggerTime;
        public Vector3 startPosition;
        public Quaternion startRotation = Quaternion.identity;
        public Vector3 previousTrackedPosition;
        public Vector3 lastTrackedPosition;
        public Quaternion lastTrackedRotation = Quaternion.identity;
        public bool hasLastDisplayedRoot;
        public Vector3 lastDisplayedRootPosition;
        public Quaternion lastDisplayedRootRotation = Quaternion.identity;
        public bool hasLastDisplayedBoundsBottomY;
        public float lastDisplayedBoundsBottomY;
        public bool hasLastDisplayedProjectedBottomV;
        public float lastDisplayedProjectedBottomV;
        public bool hasPinnedInPlaceRoot;
        public Vector3 pinnedInPlacePosition;
        public Quaternion pinnedInPlaceRotation = Quaternion.identity;
        public Vector2 previousAnchorEyePixel;
        public Vector2 lastAnchorEyePixel;
        public Rect lastBBoxEye;
        public float lastEyeWidth;
        public float lastEyeHeight;
        public bool hasPreviousAnchorEyePixel;
        public bool hasLastAnchorEyePixel;
        public bool hasLastBBoxEye;
        public bool hasPreviousTrackedTransform;
        public bool hasLastTrackedTransform;
        public byte lastCategoryId;
        public Transform lastScreen;
        public int visibleDebugLogCount;
        public AnimationClip humanClip;
        public int frameOutStartFrame;
        public int frameInFrame;
        public int frameOutDebugLogCount;
        public bool hasFrameInPosition;
        public Vector3 frameInPosition;
        public Vector3 frameOutDirection;
    }

    public struct AnimalFrameOutLoopPose
    {
        public Vector3 position;
        public Quaternion rotation;
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
        if (state.hasLastTrackedTransform)
        {
            state.previousTrackedPosition = state.lastTrackedPosition;
            state.hasPreviousTrackedTransform = true;
        }
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
        state.lastTrackedPosition = instance.transform.position;
        state.lastTrackedRotation = instance.transform.rotation;
        state.hasLastTrackedTransform = true;
        state.lastCategoryId = obj.categoryId;
        if (state.lastScreen == null)
        {
            state.lastScreen = screen;
        }
    }

    private void UpdateInteractiveMotionSchedule(GameObject instance, MetaObj obj, Transform screen, int frame)
    {
        if (instance == null)
        {
            return;
        }

        ObserveInteractiveMotionTarget(instance, obj, screen);
        if (TryStopFrameOutInteractiveMotionOnFrameIn(obj.trackId))
        {
            return;
        }

        bool isSupportedCategory = IsCategoryPerson(obj.categoryId) || IsCategoryAnimal(obj.categoryId);
        if (!enableInteractiveMotion || !isSupportedCategory)
        {
            return;
        }

        InteractiveMotionState state = GetOrCreateInteractiveMotionState(obj.trackId);
        RuntimeClock.TickContext tick = GetRuntimeTickContext();
        float nextInterval = RandomInteractiveInterval();
        InteractiveMotionSchedule.Decision decision = InteractiveMotionSchedule.Resolve(
            enableInteractiveMotion,
            isSupportedCategory,
            state.active,
            state.nextTriggerTime,
            state.startTime,
            state.duration,
            tick.now,
            nextInterval);

        state.nextTriggerTime = decision.nextTriggerTime;
        if (decision.action == InteractiveMotionSchedule.Action.Stop)
        {
            StopInteractiveMotion(obj.trackId);
            return;
        }

        if (decision.action != InteractiveMotionSchedule.Action.Start)
        {
            return;
        }

        StartInteractiveMotion(obj.trackId, instance, obj, frame, false, tick.now);
    }

    private bool TryApplyInteractiveFrameOutTrack(uint trackId, int frame)
    {
        bool hasInstance = trackInstances.TryGetValue(trackId, out GameObject instance) && instance != null;
        if (!hasInstance)
        {
            return false;
        }

        bool hasState = interactiveMotionByTrack.TryGetValue(trackId, out InteractiveMotionState state) && state != null;
        bool isSupportedCategory = hasState && (IsCategoryPerson(state.lastCategoryId) || IsCategoryAnimal(state.lastCategoryId));
        InteractiveMotionSchedule.FrameOutAction action = InteractiveMotionSchedule.ResolveFrameOutAction(
            enableInteractiveMotion,
            hasInstance,
            hasState,
            hasState && state.hasLastTrackedTransform,
            isSupportedCategory,
            hasState && state.active,
            hasState && state.startedFromFrameOut,
            hasState && state.mode == InteractiveMotionMode.Replacement);
        if (action == InteractiveMotionSchedule.FrameOutAction.None)
        {
            return false;
        }

        RuntimeClock.TickContext tick = GetRuntimeTickContext();
        if (action == InteractiveMotionSchedule.FrameOutAction.StartThenApply)
        {
            StartInteractiveMotion(trackId, instance, default(MetaObj), frame, true, tick.now);
        }

        if (!state.active || state.mode != InteractiveMotionMode.Replacement)
        {
            return false;
        }

        GameObjectLifecycleWriter.ApplyActive(instance, true);
        if (state.subject == InteractiveMotionSubject.Animal)
        {
            ApplyAnimalFrameOutTransform(trackId, instance, state.lastScreen, frame, tick.now);
        }
        else
        {
            ApplyInteractiveReplacementTransform(trackId, instance, state.lastScreen, tick.now);
            ApplyHumanClipPlayback(trackId, instance, tick.now, tick.deltaTime);
        }
        if (ShouldStopInteractiveFrameOutTrack(state, frame, tick.now))
        {
            StopInteractiveMotion(trackId);
        }
        return true;
    }

    private bool ShouldStopInteractiveFrameOutTrack(InteractiveMotionState state, int frame, float now)
    {
        return InteractiveMotionSchedule.ShouldStopFrameOut(
            state == null,
            state != null && state.subject == InteractiveMotionSubject.Animal,
            state != null && state.startedFromFrameOut,
            state != null ? state.frameInFrame : -1,
            state != null ? state.frameOutStartFrame : -1,
            frame,
            state != null ? RuntimeClock.ResolveElapsed(now, state.startTime) : 0f,
            state != null ? state.duration : 0f);
    }

    private bool TryStopFrameOutInteractiveMotionOnFrameIn(uint trackId)
    {
        if (!interactiveMotionByTrack.TryGetValue(trackId, out InteractiveMotionState state) ||
            state == null ||
            !state.active ||
            !state.startedFromFrameOut)
        {
            return false;
        }

        StopInteractiveMotion(trackId);
        state.nextTriggerTime = RuntimeClock.ResolveNextTime(GetRuntimeTickContext().now, RandomInteractiveInterval());
        return true;
    }

    private void StartInteractiveMotion(uint trackId, GameObject instance, MetaObj obj, int frame, bool frameOut, float now)
    {
        InteractiveMotionState state = GetOrCreateInteractiveMotionState(trackId);
        bool isAnimal = frameOut ? IsCategoryAnimal(state.lastCategoryId) : IsCategoryAnimal(obj.categoryId);
        bool isPerson = frameOut ? IsCategoryPerson(state.lastCategoryId) : IsCategoryPerson(obj.categoryId);
        if (!isAnimal && !isPerson)
        {
            return;
        }

        state.active = true;
        state.startedFromFrameOut = frameOut;
        if (frameOut)
        {
            state.visibleDebugLogCount = 0;
        }
        state.subject = isAnimal ? InteractiveMotionSubject.Animal : InteractiveMotionSubject.Person;
        state.startTime = now;
        state.duration = GetEffectiveInteractiveMotionDuration();
        Vector3 displayedPosition = ResolveInteractiveFrameOutStartPosition(
            instance.transform.position,
            frameOut && state.hasLastDisplayedRoot,
            state.lastDisplayedRootPosition);
        Quaternion displayedRotation = frameOut && state.hasLastDisplayedRoot
            ? state.lastDisplayedRootRotation
            : instance.transform.rotation;
        if (frameOut && isAnimal)
        {
            TrackPlacementWriter.Apply(
                instance.transform,
                new TrackPlacementCommand(displayedPosition, displayedRotation, instance.transform.localScale));
            PreserveAnimalFrameOutDisplayedBoundsBottom(instance, state);
            PreserveAnimalFrameOutProjectedBottom(instance, state);
            displayedPosition = instance.transform.position;
            displayedRotation = instance.transform.rotation;
        }
        state.startPosition = displayedPosition;
        state.startRotation = displayedRotation;
        state.lastTrackedPosition = displayedPosition;
        state.lastTrackedRotation = displayedRotation;
        state.hasPinnedInPlaceRoot = false;
        state.pinnedInPlacePosition = instance.transform.position;
        state.pinnedInPlaceRotation = Quaternion.identity;
        state.frameOutStartFrame = frame;
        state.frameInFrame = -1;
        state.frameOutDebugLogCount = 0;
        if (frameOut)
        {
            LogAnimalFrameOutDebug("start", trackId, frame, instance, state, null, 0f, 0f);
        }
        state.hasFrameInPosition = false;
        state.frameInPosition = Vector3.zero;
        ResolveFrameOutPixelAxes(state.lastScreen, instance.transform, out Vector3 pixelRight, out Vector3 pixelUp);
        Vector3 motionDirection = ResolveAnimalFrameOutDirectionFromScreenMotion(
            state.previousAnchorEyePixel,
            state.lastAnchorEyePixel,
            state.hasPreviousAnchorEyePixel && state.hasLastAnchorEyePixel,
            pixelRight,
            pixelUp,
            ResolveAnimalFrameOutDirection(
                state.previousTrackedPosition,
                state.lastTrackedPosition,
                state.hasPreviousTrackedTransform,
                state.startRotation,
                state.lastScreen != null ? state.lastScreen.up : instance.transform.up));
        state.frameOutDirection = ResolveAnimalFrameOutDirectionFromScreenExit(
            state.lastBBoxEye,
            state.lastEyeWidth,
            state.lastEyeHeight,
            pixelRight,
            pixelUp,
            motionDirection);
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
        if (frameOut)
        {
            TryPrepareAnimalFrameInTarget(trackId, frame, state);
        }
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

    public static Vector3 ResolveInteractiveFrameOutStartPosition(
        Vector3 instancePosition,
        bool hasLastDisplayedRoot,
        Vector3 lastDisplayedRootPosition)
    {
        return hasLastDisplayedRoot ? lastDisplayedRootPosition : instancePosition;
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

        int value = Random.Range(0, 100);
        if (value < 35)
        {
            return InteractiveAnimalPreset.LookAtViewer;
        }
        if (value < 60)
        {
            return InteractiveAnimalPreset.TailWag;
        }
        if (value < 80)
        {
            return InteractiveAnimalPreset.PawWave;
        }
        if (value < 95)
        {
            return InteractiveAnimalPreset.BodyTurnViewer;
        }

        return InteractiveAnimalPreset.ApproachViewer;
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

    private void ObserveInteractiveMotionDisplayedRoot(uint trackId, GameObject instance)
    {
        if (instance == null ||
            !interactiveMotionByTrack.TryGetValue(trackId, out InteractiveMotionState state) ||
            state == null)
        {
            return;
        }

        state.lastDisplayedRootPosition = instance.transform.position;
        state.lastDisplayedRootRotation = instance.transform.rotation;
        state.hasLastDisplayedRoot = true;
        CaptureInteractiveDisplayedBoundsBottom(state, instance);
        LogAnimalVisibleDebug(trackId, instance, state);
        state.lastTrackedPosition = instance.transform.position;
        state.lastTrackedRotation = instance.transform.rotation;
    }

    private void LogAnimalVisibleDebug(uint trackId, GameObject instance, InteractiveMotionState state)
    {
        if (state == null || !IsCategoryAnimal(state.lastCategoryId) || state.visibleDebugLogCount >= 80)
        {
            return;
        }

        state.visibleDebugLogCount++;
        Vector3 root = instance != null ? instance.transform.position : Vector3.zero;
        Vector3 euler = instance != null ? instance.transform.rotation.eulerAngles : Vector3.zero;
        Bounds bounds = default(Bounds);
        float bottomV = float.NaN;
        bool hasBounds = instance != null && TryGetRendererWorldBounds(instance, out bounds);
        bool hasProjection = instance != null && TryProjectRendererBoundsToEyeHeight(instance, state.lastScreen, out _, out bottomV, out _, out _);
        Debug.Log(
            $"[DEBUG-AVIS] track={trackId} rootY={root.y:F4} boundsMinY={(hasBounds ? bounds.min.y : float.NaN):F4} " +
            $"bottomV={(hasProjection ? bottomV : float.NaN):F2} rotEuler=({euler.x:F1},{euler.y:F1},{euler.z:F1}) active={state.active} frameOut={state.startedFromFrameOut}");
    }

    private void CaptureInteractiveDisplayedBoundsBottom(InteractiveMotionState state, GameObject instance)
    {
        if (state == null || instance == null)
        {
            return;
        }

        if (TryGetRendererWorldBounds(instance, out Bounds bounds))
        {
            state.lastDisplayedBoundsBottomY = bounds.min.y;
            state.hasLastDisplayedBoundsBottomY = true;
        }
        if (TryProjectRendererBoundsToEyeHeight(instance, state.lastScreen, out _, out float bottomV, out _, out _))
        {
            state.lastDisplayedProjectedBottomV = bottomV;
            state.hasLastDisplayedProjectedBottomV = true;
        }
    }

    public static bool ShouldFitDisplayedModelToBBoxDuringInteractiveMotion(bool isReplacing, bool isHumanoidInPlace)
    {
        return !isReplacing && !isHumanoidInPlace;
    }

    public static bool ShouldInitialFitHumanoidInPlaceRootBeforePinning(
        bool isHumanoidInPlace,
        bool isPinned,
        bool shouldUseHumanSmplRootPlacement)
    {
        return isHumanoidInPlace && !isPinned && !shouldUseHumanSmplRootPlacement;
    }

    public static bool ShouldPreserveHumanoidInteractiveRootPosition(bool allowHipsTranslation)
    {
        return !allowHipsTranslation;
    }

    public static bool ShouldApplyHumanFaceViewerRootTransform(bool isReplacement, bool isFaceViewerPreset)
    {
        return !isReplacement && isFaceViewerPreset;
    }

    public static Vector3 ResolveHumanoidInteractiveRootPosition(
        Vector3 currentPosition,
        Vector3 startPosition,
        bool allowHipsTranslation)
    {
        return allowHipsTranslation ? currentPosition : startPosition;
    }

    public static Vector3 ResolveHumanoidInteractiveRootPosition(
        Vector3 currentPosition,
        Vector3 startPosition,
        bool allowHipsTranslation,
        float preservationWeight)
    {
        return allowHipsTranslation
            ? currentPosition
            : Vector3.Lerp(currentPosition, startPosition, Mathf.Clamp01(preservationWeight));
    }

    public static Quaternion ResolveHumanoidInteractiveRootRotation(
        Quaternion currentRotation,
        Quaternion startRotation,
        bool allowHipsTranslation,
        Vector3 upAxis,
        Vector3 fallbackForward)
    {
        return ResolveHumanoidInteractiveRootRotation(
            currentRotation,
            startRotation,
            allowHipsTranslation,
            upAxis,
            fallbackForward,
            1.0f);
    }

    public static Quaternion ResolveHumanoidInteractiveRootRotation(
        Quaternion currentRotation,
        Quaternion startRotation,
        bool allowHipsTranslation,
        Vector3 upAxis,
        Vector3 fallbackForward,
        float preservationWeight)
    {
        return allowHipsTranslation
            ? currentRotation
            : Quaternion.Slerp(
                MakeUprightYawRotation(currentRotation, upAxis, fallbackForward),
                MakeUprightYawRotation(startRotation, upAxis, fallbackForward),
                Mathf.Clamp01(preservationWeight));
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

    public static TrackPlacementCommand ResolveInteractiveReplacementPlacementCommand(
        Vector3 position,
        Quaternion rotation,
        Vector3 currentLocalScale)
    {
        return new TrackPlacementCommand(position, rotation, currentLocalScale);
    }

    public static TrackPlacementCommand ResolveRotationOnlyPlacementCommand(
        Vector3 currentPosition,
        Quaternion rotation,
        Vector3 currentLocalScale)
    {
        return TrackPlacementCommand.RotationOnly(currentPosition, rotation, currentLocalScale);
    }

    public static TrackPlacementCommand ResolvePositionOnlyPlacementCommand(
        Vector3 position,
        Quaternion currentRotation,
        Vector3 currentLocalScale)
    {
        return TrackPlacementCommand.PositionOnly(position, currentRotation, currentLocalScale);
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
            RuntimeClock.TickContext tick = GetRuntimeTickContext();
            ApplyInteractiveReplacementTransform(trackId, instance, screen, tick.now);
            ApplyHumanClipPlayback(trackId, instance, tick.now, tick.deltaTime);
            return true;
        }
        else if (ShouldApplyHumanFaceViewerRootTransform(false, true))
        {
            RuntimeClock.TickContext tick = GetRuntimeTickContext();
            ApplyInteractiveFaceViewerTransform(instance, state, screen, tick.now);
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

        RuntimeClock.TickContext tick = GetRuntimeTickContext();
        ApplyHumanClipPlayback(trackId, instance, tick.now, tick.deltaTime);
    }

    private void ApplyInteractiveReplacementTransform(uint trackId, GameObject instance, Transform screen, float now)
    {
        if (instance == null || !interactiveMotionByTrack.TryGetValue(trackId, out InteractiveMotionState state) || state == null)
        {
            return;
        }

        float elapsed = RuntimeClock.ResolveElapsed(now, state.startTime);
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

        Quaternion lookRotation = Quaternion.LookRotation(towardHead, upAxis);
        Quaternion trackedRotation = state.hasLastTrackedTransform ? state.lastTrackedRotation : state.startRotation;
        Quaternion uprightTrackedRotation = MakeUprightYawRotation(trackedRotation, upAxis, instance.transform.forward);
        TrackPlacementWriter.Apply(
            instance.transform,
            ResolveInteractiveReplacementPlacementCommand(
                blendedPosition,
                Quaternion.Slerp(uprightTrackedRotation, lookRotation, eventWeight),
                instance.transform.localScale));
    }

    private void ApplyAnimalFrameOutTransform(uint trackId, GameObject instance, Transform screen, int frame, float now)
    {
        if (instance == null || !interactiveMotionByTrack.TryGetValue(trackId, out InteractiveMotionState state) || state == null)
        {
            return;
        }

        float elapsed = RuntimeClock.ResolveElapsed(now, state.startTime);
        float t = ResolveAnimalFrameOutNormalizedTime(state, frame, elapsed);
        Vector3 upAxis = Vector3.up;
        AnimalFrameOutLoopPose pose = ResolveAnimalFrameOutLoopPose(
            state.startPosition,
            state.startRotation,
            state.frameOutDirection,
            state.frameInPosition,
            state.hasFrameInPosition,
            t,
            ResolveAnimalFrameOutTravelDistance(state),
            1.0f,
            upAxis);

        TrackPlacementWriter.Apply(
            instance.transform,
            ResolvePositionOnlyPlacementCommand(
                pose.position,
                instance.transform.rotation,
                instance.transform.localScale));
        ApplyAnimalFrameOutWalkAnimation(instance.transform, pose, t, ResolveAnimalFrameOutTravelDistance(state), upAxis);
        LogAnimalFrameOutDebug("after-pose", trackId, frame, instance, state, pose, t, elapsed);
        PreserveAnimalFrameOutDisplayedBoundsBottom(instance, state);
        PreserveAnimalFrameOutProjectedBottom(instance, state);
        LogAnimalFrameOutDebug("after-preserve", trackId, frame, instance, state, pose, t, elapsed);
    }

    private void LogAnimalFrameOutDebug(
        string phase,
        uint trackId,
        int frame,
        GameObject instance,
        InteractiveMotionState state,
        AnimalFrameOutLoopPose? pose,
        float normalizedTime,
        float elapsed)
    {
        if (state == null || state.subject != InteractiveMotionSubject.Animal || state.frameOutDebugLogCount >= 120)
        {
            return;
        }

        state.frameOutDebugLogCount++;
        Vector3 root = instance != null ? instance.transform.position : Vector3.zero;
        Vector3 euler = instance != null ? instance.transform.rotation.eulerAngles : Vector3.zero;
        Bounds bounds = default(Bounds);
        float bottomV = float.NaN;
        bool hasBounds = instance != null && TryGetRendererWorldBounds(instance, out bounds);
        bool hasProjection = instance != null && TryProjectRendererBoundsToEyeHeight(instance, state.lastScreen, out _, out bottomV, out _, out _);
        Vector3 posePosition = pose.HasValue ? pose.Value.position : Vector3.zero;
        Debug.Log(
            $"[DEBUG-AFRAME] phase={phase} track={trackId} frame={frame} t={normalizedTime:F3} elapsed={elapsed:F3} " +
            $"rootY={root.y:F4} startY={state.startPosition.y:F4} lastDisplayedRootY={(state.hasLastDisplayedRoot ? state.lastDisplayedRootPosition.y : float.NaN):F4} " +
            $"boundsMinY={(hasBounds ? bounds.min.y : float.NaN):F4} targetBoundsMinY={(state.hasLastDisplayedBoundsBottomY ? state.lastDisplayedBoundsBottomY : float.NaN):F4} " +
            $"bottomV={(hasProjection ? bottomV : float.NaN):F2} targetBottomV={(state.hasLastDisplayedProjectedBottomV ? state.lastDisplayedProjectedBottomV : float.NaN):F2} " +
            $"rotEuler=({euler.x:F1},{euler.y:F1},{euler.z:F1}) poseY={(pose.HasValue ? posePosition.y : float.NaN):F4} hasFrameIn={state.hasFrameInPosition} frameInY={(state.hasFrameInPosition ? state.frameInPosition.y : float.NaN):F4}");
    }

    private void PreserveAnimalFrameOutProjectedBottom(GameObject instance, InteractiveMotionState state)
    {
        if (instance == null ||
            state == null ||
            !state.hasLastDisplayedProjectedBottomV ||
            !TryProjectRendererBoundsToEyeHeight(instance, state.lastScreen, out _, out float bottomV, out _, out float depthMeters))
        {
            return;
        }

        AlignProjectedModelBottomToBBox(
            instance.transform,
            state.lastScreen,
            bottomV,
            depthMeters,
            state.lastDisplayedProjectedBottomV);
    }

    private void PreserveAnimalFrameOutDisplayedBoundsBottom(GameObject instance, InteractiveMotionState state)
    {
        if (instance == null ||
            state == null ||
            !state.hasLastDisplayedBoundsBottomY ||
            !TryGetRendererWorldBounds(instance, out Bounds bounds))
        {
            return;
        }

        TrackPlacementWriter.Apply(
            instance.transform,
            ResolvePositionOnlyPlacementCommand(
                ResolvePositionPreservingBoundsBottom(
                    instance.transform.position,
                    bounds.min.y,
                    true,
                    state.lastDisplayedBoundsBottomY),
                instance.transform.rotation,
                instance.transform.localScale));
    }

    public static Vector3 ResolvePositionPreservingBoundsBottom(
        Vector3 currentPosition,
        float currentBottomY,
        bool hasTargetBottomY,
        float targetBottomY)
    {
        return hasTargetBottomY
            ? currentPosition + Vector3.up * (targetBottomY - currentBottomY)
            : currentPosition;
    }

    private float ResolveAnimalFrameOutNormalizedTime(InteractiveMotionState state, int frame, float elapsed)
    {
        if (state != null && state.frameInFrame > state.frameOutStartFrame)
        {
            int span = Mathf.Max(1, state.frameInFrame - state.frameOutStartFrame);
            return Mathf.Clamp01((frame - state.frameOutStartFrame) / (float)span);
        }

        float loopDuration = Mathf.Max(0.001f, Mathf.Min(state != null ? state.duration : AnimalFrameOutLoopSeconds, AnimalFrameOutLoopSeconds));
        return Mathf.Clamp01(elapsed / loopDuration);
    }

    private float ResolveAnimalFrameOutTravelDistance(InteractiveMotionState state)
    {
        if (state != null && state.frameInFrame > state.frameOutStartFrame)
        {
            float fps = metaHeader.fps > 0f ? metaHeader.fps : (manifest != null && manifest.fps > 0f ? manifest.fps : 30f);
            float gapSeconds = (state.frameInFrame - state.frameOutStartFrame) / Mathf.Max(1f, fps);
            return ResolveAnimalFrameOutTravelDistance(gapSeconds, AnimalFrameOutSpeedMetersPerSecond);
        }

        return ResolveAnimalFrameOutTravelDistance(AnimalFrameOutLoopSeconds, AnimalFrameOutSpeedMetersPerSecond);
    }

    public static float ResolveAnimalFrameOutTravelDistance(float hiddenSeconds, float speedMetersPerSecond)
    {
        return AnimalFrameOutMotion.ResolveTravelDistance(hiddenSeconds, speedMetersPerSecond);
    }

    public static Vector3 ResolveAnimalFrameOutDirection(
        Vector3 previousPosition,
        Vector3 currentPosition,
        bool hasPreviousPosition,
        Quaternion fallbackRotation,
        Vector3 upAxis)
    {
        return AnimalFrameOutMotion.ResolveDirection(
            previousPosition,
            currentPosition,
            hasPreviousPosition,
            fallbackRotation,
            upAxis);
    }

    public static Vector3 ResolveAnimalFrameOutDirectionFromScreenMotion(
        Vector2 previousEyePixel,
        Vector2 currentEyePixel,
        bool hasPreviousEyePixel,
        Vector3 screenRight,
        Vector3 screenUp,
        Vector3 fallbackDirection)
    {
        return AnimalFrameOutMotion.ResolveDirectionFromScreenMotion(
            previousEyePixel,
            currentEyePixel,
            hasPreviousEyePixel,
            screenRight,
            screenUp,
            fallbackDirection);
    }

    public static Vector3 ResolveAnimalFrameOutDirectionFromScreenExit(
        Rect bboxEye,
        float eyeWidth,
        float eyeHeight,
        Vector3 screenRight,
        Vector3 screenUp,
        Vector3 fallbackDirection)
    {
        return AnimalFrameOutMotion.ResolveDirectionFromScreenExit(
            bboxEye,
            eyeWidth,
            eyeHeight,
            screenRight,
            screenUp,
            fallbackDirection);
    }

    private void ResolveFrameOutPixelAxes(Transform screen, Transform fallback, out Vector3 pixelRight, out Vector3 pixelUp)
    {
        if (TryGetPinholeBasis(screen, out _, out Quaternion pinholeRotation))
        {
            pixelRight = pinholeRotation * Vector3.right;
            pixelUp = pinholeRotation * Vector3.up;
            return;
        }

        pixelRight = fallback != null ? fallback.right : Vector3.right;
        pixelUp = fallback != null ? fallback.up : Vector3.up;
    }

    public static AnimalFrameOutLoopPose ResolveAnimalFrameOutLoopPose(
        Vector3 startPosition,
        Quaternion startRotation,
        Vector3 frameOutDirection,
        Vector3 frameInPosition,
        bool hasFrameInPosition,
        float normalizedTime,
        float travelDistance,
        float weight,
        Vector3 upAxis)
    {
        return AnimalFrameOutMotion.ResolveLoopPose(
            startPosition,
            startRotation,
            frameOutDirection,
            frameInPosition,
            hasFrameInPosition,
            normalizedTime,
            travelDistance,
            weight,
            upAxis);
    }

    public static Vector3 PreserveAnimalFrameOutStartHeight(Vector3 position, Vector3 startPosition, Vector3 upAxis)
    {
        return AnimalFrameOutMotion.PreserveStartHeight(position, startPosition, upAxis);
    }

    public static Vector3 ResolveAnimalFrameOutControlPoint(
        Vector3 startPosition,
        Vector3 frameInPosition,
        bool hasFrameInPosition,
        Vector3 frameOutDirection,
        float travelDistance,
        Vector3 upAxis)
    {
        return AnimalFrameOutMotion.ResolveControlPoint(
            startPosition,
            frameInPosition,
            hasFrameInPosition,
            frameOutDirection,
            travelDistance,
            upAxis);
    }

    private static void ApplyAnimalFrameOutWalkAnimation(
        Transform root,
        AnimalFrameOutLoopPose pose,
        float normalizedTime,
        float travelDistance,
        Vector3 upAxis)
    {
        if (root == null)
        {
            return;
        }

        TrackPlacementWriter.Apply(
            root,
            new TrackPlacementCommand(
                ResolveAnimalFrameOutWalkPosition(pose.position),
                pose.rotation,
                root.localScale));
    }

    public static Vector3 ResolveAnimalFrameOutWalkPosition(Vector3 posePosition)
    {
        return AnimalFrameOutMotion.ResolveWalkPosition(posePosition);
    }

    private bool TryPrepareAnimalFrameInTarget(uint trackId, int frameOutStartFrame, InteractiveMotionState state)
    {
        if (state == null || frameOffsets == null || frameOffsets.Length == 0)
        {
            return false;
        }

        if (!TryFindNextTrackObject(trackId, frameOutStartFrame + 1, out int frameInFrame, out MetaObj frameInObj))
        {
            return false;
        }

        if (!TryResolveMetaObjectAnchorWorld(frameInObj, out Vector3 frameInPosition))
        {
            return false;
        }

        state.frameInFrame = frameInFrame;
        state.frameInPosition = frameInPosition;
        state.hasFrameInPosition = true;
        float fps = metaHeader.fps > 0f ? metaHeader.fps : (manifest != null && manifest.fps > 0f ? manifest.fps : 30f);
        state.duration = Mathf.Max(0.1f, (frameInFrame - frameOutStartFrame) / Mathf.Max(1f, fps));
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

    private void ApplyInteractiveFaceViewerTransform(GameObject instance, InteractiveMotionState state, Transform screen, float now)
    {
        if (instance == null || state == null)
        {
            return;
        }

        float elapsed = RuntimeClock.ResolveElapsed(now, state.startTime);
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
        TrackPlacementWriter.Apply(
            instance.transform,
            ResolveRotationOnlyPlacementCommand(
                instance.transform.position,
                Quaternion.Slerp(uprightTrackedRotation, lookRotation, Mathf.Clamp01(weight * 0.75f)),
                instance.transform.localScale));
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

        RuntimeClock.TickContext tick = GetRuntimeTickContext();
        float elapsed = RuntimeClock.ResolveElapsed(tick.now, state.startTime);
        float t = Mathf.Clamp01(elapsed / Mathf.Max(0.001f, state.duration));
        float weight = InteractiveEnvelope(elapsed, state.duration);
        float wave = Mathf.Sin(t * Mathf.PI * 6f) * weight;

        if (state.mode == InteractiveMotionMode.Replacement)
        {
            Vector3 before = pose.rootWorld;
            ApplyInteractiveReplacementTransform(trackId, instanceRoot != null ? instanceRoot.gameObject : null, screen, tick.now);
            Vector3 after = instanceRoot != null ? instanceRoot.position : before;
            Vector3 delta = after - before;
            OffsetAnimalPose(ref pose, delta);
            return;
        }

        switch (state.animalPreset)
        {
            case InteractiveAnimalPreset.LookAtViewer:
                ApplyAnimalBodyTurnViewer(instanceRoot, screen, ref pose, weight);
                break;
            case InteractiveAnimalPreset.BodyTurnViewer:
                ApplyAnimalBodyTurnViewer(instanceRoot, screen, ref pose, weight);
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

        SetAnimalJointWorld(ref pose, 14, GetAnimalJointWorldOrRoot(pose, 14) + offset * Mathf.Clamp01(weight));
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

        PoseAnimatorWriter.ApplyEnabled(animator, true);
        PoseAnimatorWriter.ApplyRootMotion(animator, false);
        PlayableGraph graph = PlayableGraph.Create("InteractiveMotion_" + trackId);
        graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
        AnimationClipPlayable playable = AnimationClipPlayable.Create(graph, clip);
        playable.SetApplyFootIK(false);
        playable.SetApplyPlayableIK(false);
        AnimationPlayableOutput output = AnimationPlayableOutput.Create(graph, "Animation", animator);
        output.SetSourcePlayable(playable);
        PoseAnimatorWriter.ApplyPlay(graph);
        interactiveClipPlaybackByTrack[trackId] = new InteractiveClipPlayback
        {
            graph = graph,
            clip = clip,
            animator = animator,
            loop = IsWalkingHumanClip(clip)
        };
        CacheHumanoidPlaybackBones(interactiveClipPlaybackByTrack[trackId], animator);
    }

    private void ApplyHumanClipPlayback(uint trackId, GameObject instance, float now, float deltaTime)
    {
        if (!interactiveMotionByTrack.TryGetValue(trackId, out InteractiveMotionState state) || state == null)
        {
            return;
        }

        bool allowHipsTranslation = state.mode == InteractiveMotionMode.Replacement;
        if (!interactiveClipPlaybackByTrack.TryGetValue(trackId, out InteractiveClipPlayback playback) ||
            playback == null ||
            !playback.graph.IsValid() ||
            playback.clip == null)
        {
            ApplyFallbackHumanWave(instance, state, now);
            float fallbackWeight = InteractiveEnvelope(RuntimeClock.ResolveElapsed(now, state.startTime), state.duration);
            if (ShouldPreserveHumanoidInteractiveRootPosition(allowHipsTranslation) && instance != null)
            {
                Vector3 upAxis = state.lastScreen != null ? state.lastScreen.up : Vector3.up;
                TrackPlacementWriter.Apply(
                    instance.transform,
                    new TrackPlacementCommand(
                        ResolveHumanoidInteractiveRootPosition(
                        instance.transform.position,
                        state.startPosition,
                        allowHipsTranslation,
                        fallbackWeight),
                        ResolveHumanoidInteractiveRootRotation(
                        instance.transform.rotation,
                        state.startRotation,
                        allowHipsTranslation,
                        upAxis,
                        instance.transform.forward,
                        fallbackWeight),
                        instance.transform.localScale));
            }
            return;
        }

        float elapsed = RuntimeClock.ResolveElapsed(now, state.startTime);
        float weight = InteractiveEnvelope(elapsed, state.duration);
        Vector3 instancePositionBeforePlayback = instance != null ? instance.transform.position : Vector3.zero;
        Quaternion instanceRotationBeforePlayback = instance != null ? instance.transform.rotation : Quaternion.identity;
        Transform animatorTransform = playback.animator != null ? playback.animator.transform : null;
        Vector3 animatorLocalPositionBeforePlayback = animatorTransform != null ? animatorTransform.localPosition : Vector3.zero;
        Quaternion animatorLocalRotationBeforePlayback = animatorTransform != null ? animatorTransform.localRotation : Quaternion.identity;
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
        playback.graph.Evaluate(deltaTime);

        CaptureHumanoidPose(playback.bones, playback.animatedLocalRotations, playback.animatedLocalPositions);
        BlendHumanoidPose(playback.bones, playback.beforeLocalRotations, playback.beforeLocalPositions, playback.animatedLocalRotations, playback.animatedLocalPositions, weight, allowHipsTranslation);
        if (instance != null)
        {
            if (ShouldRestoreAnimatorLocalTransformSeparately(animatorTransform, instance.transform))
            {
                PoseTransformWriter.ApplyLocalPose(
                    animatorTransform,
                    animatorLocalPositionBeforePlayback,
                    animatorLocalRotationBeforePlayback);
            }
            Vector3 upAxis = state.lastScreen != null ? state.lastScreen.up : Vector3.up;
            TrackPlacementWriter.Apply(
                instance.transform,
                new TrackPlacementCommand(
                    ShouldPreserveHumanoidInteractiveRootPosition(allowHipsTranslation)
                        ? ResolveHumanoidInteractiveRootPosition(
                            instancePositionBeforePlayback,
                            state.startPosition,
                            allowHipsTranslation,
                            weight)
                        : instancePositionBeforePlayback,
                    ResolveHumanoidInteractiveRootRotation(
                        instanceRotationBeforePlayback,
                        state.startRotation,
                        allowHipsTranslation,
                        upAxis,
                        instance.transform.forward,
                        weight),
                    instance.transform.localScale));
        }
    }

    private bool IsHumanoidInteractiveRootPinned(uint trackId)
    {
        return interactiveMotionByTrack.TryGetValue(trackId, out InteractiveMotionState state) &&
            state != null &&
            state.hasPinnedInPlaceRoot;
    }

    private void PinHumanoidInteractiveRootAfterBBox(uint trackId, GameObject instance, Transform screen)
    {
        if (!interactiveMotionByTrack.TryGetValue(trackId, out InteractiveMotionState state) ||
            state == null ||
            state.subject != InteractiveMotionSubject.Person ||
            state.mode == InteractiveMotionMode.Replacement)
        {
            return;
        }

        PinHumanoidInPlaceRoot(instance, state, screen);
    }

    private void PinHumanoidInPlaceRoot(GameObject instance, InteractiveMotionState state, Transform screen)
    {
        if (instance == null || state == null || state.hasPinnedInPlaceRoot)
        {
            return;
        }

        Vector3 upAxis = screen != null ? screen.up : (state.lastScreen != null ? state.lastScreen.up : Vector3.up);
        Transform viewer = GetViewOrHeadTransform();
        state.pinnedInPlacePosition = instance.transform.position;
        Vector3 viewerPosition = viewer != null ? viewer.position : Vector3.zero;
        bool hasViewer = viewer != null;
        if (!hasViewer && screen != null)
        {
            viewerPosition = state.pinnedInPlacePosition - screen.forward;
            hasViewer = true;
        }
        state.pinnedInPlaceRotation = ResolveHumanoidViewerFacingRotation(
            state.pinnedInPlacePosition,
            instance.transform.rotation,
            upAxis,
            viewerPosition,
            hasViewer,
            screen != null ? -screen.forward : instance.transform.forward);
        state.startPosition = state.pinnedInPlacePosition;
        state.startRotation = state.pinnedInPlaceRotation;
        state.hasPinnedInPlaceRoot = true;
        TrackPlacementWriter.Apply(
            instance.transform,
            new TrackPlacementCommand(
                state.pinnedInPlacePosition,
                state.pinnedInPlaceRotation,
                instance.transform.localScale));
    }

    public static bool ShouldRestoreAnimatorLocalTransformSeparately(Transform animatorTransform, Transform instanceTransform)
    {
        return animatorTransform != null && instanceTransform != null && animatorTransform != instanceTransform;
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
                PoseTransformWriter.ApplyLocalRotation(bone, Quaternion.Slerp(baseRotation, animatedRotation, t));
            }

            if (kv.Key == HumanBodyBones.Hips &&
                basePositions.TryGetValue(kv.Key, out Vector3 basePosition) &&
                animatedPositions.TryGetValue(kv.Key, out Vector3 animatedPosition))
            {
                PoseTransformWriter.ApplyLocalPosition(
                    bone,
                    ResolveHumanoidInteractiveLocalPosition(kv.Key, basePosition, animatedPosition, t, allowHipsTranslation));
            }
            else if (basePositions.TryGetValue(kv.Key, out Vector3 preservedPosition))
            {
                PoseTransformWriter.ApplyLocalPosition(bone, preservedPosition);
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

        float elapsed = RuntimeClock.ResolveElapsed(now, state.startTime);
        float t = Mathf.Clamp01(elapsed / Mathf.Max(0.001f, state.duration));
        float weight = InteractiveEnvelope(elapsed, state.duration);
        float wave = Mathf.Sin(t * Mathf.PI * 8f) * weight;
        PoseTransformWriter.ApplyLocalRotation(
            upper,
            upper.localRotation * Quaternion.Euler(-45f * weight, 0f, 25f * weight));
        PoseTransformWriter.ApplyLocalRotation(
            lower,
            lower.localRotation * Quaternion.Euler(0f, 0f, 35f * wave));
    }

    private void StopInteractiveMotion(uint trackId)
    {
        if (interactiveMotionByTrack.TryGetValue(trackId, out InteractiveMotionState state) && state != null)
        {
            state.active = false;
            state.startedFromFrameOut = false;
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
