using System.Collections.Generic;
using UnityEngine;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    private const float HumanOtherMaximumCorrectionDeltaPerFramePixels = 18f;
    private const float HumanOtherMaximumDepthDeltaPerFrameMeters = 0.015f;
    private const float HumanOtherMaximumContactParameterDeltaPerFrame = 0.15f;
    private const float HumanOtherMaximumContactDirectionDegreesPerFrame = 25f;

    private enum HumanOtherContactSegmentId
    {
        LeftFoot,
        RightFoot,
        LeftShin,
        RightShin,
        LeftThigh,
        RightThigh,
        LeftForearm,
        RightForearm,
        LeftUpperArm,
        RightUpperArm,
        Torso,
        Shoulders,
        Head
    }

    private struct HumanOtherSourceSegmentBinding
    {
        public readonly HumanOtherContactSegmentId id;
        public readonly int sourceStart;
        public readonly int sourceEnd;
        public readonly HumanBodyBones displayedStart;
        public readonly HumanBodyBones displayedEnd;
        public readonly float displayedProxyRadiusToLength;

        public HumanOtherSourceSegmentBinding(
            HumanOtherContactSegmentId id,
            int sourceStart,
            int sourceEnd,
            HumanBodyBones displayedStart,
            HumanBodyBones displayedEnd,
            float displayedProxyRadiusToLength)
        {
            this.id = id;
            this.sourceStart = sourceStart;
            this.sourceEnd = sourceEnd;
            this.displayedStart = displayedStart;
            this.displayedEnd = displayedEnd;
            this.displayedProxyRadiusToLength =
                displayedProxyRadiusToLength;
        }
    }

    private struct HumanOtherContactCandidate
    {
        public uint humanTrackId;
        public HumanOtherContactSegmentId segmentId;
        public bool matchesPreviousSegment;
        public float contactWeight;
        public float sourceCenterToSegmentPixels;
        public float sourceSegmentParameter;
        public Vector2 sourceLocalDirection;
        public Vector2 displayedStartPixel;
        public Vector2 displayedEndPixel;
        public Vector3 displayedStartCam;
        public Vector3 displayedEndCam;
        public float minimumCenterToSegmentPixels;
    }

    private struct HumanOtherContactState
    {
        public uint humanTrackId;
        public HumanOtherContactSegmentId segmentId;
        public int lastFrame;
        public Vector2 appliedPixelOffset;
        public float appliedDepthOffsetMeters;
        public bool hasContactMapping;
        public float contactSegmentParameter;
        public Vector2 contactLocalDirection;
    }

    private static readonly HumanOtherSourceSegmentBinding[]
        HumanOtherSourceSegments =
        {
            // HMR2 OpenPose25 indices:
            // right ankle/toe/heel = 11/22/24, left = 14/19/21.
            new HumanOtherSourceSegmentBinding(
                HumanOtherContactSegmentId.LeftFoot,
                14,
                19,
                HumanBodyBones.LeftFoot,
                HumanBodyBones.LeftToes,
                0.25f),
            new HumanOtherSourceSegmentBinding(
                HumanOtherContactSegmentId.LeftFoot,
                14,
                21,
                HumanBodyBones.LeftFoot,
                HumanBodyBones.LeftToes,
                0.25f),
            new HumanOtherSourceSegmentBinding(
                HumanOtherContactSegmentId.RightFoot,
                11,
                22,
                HumanBodyBones.RightFoot,
                HumanBodyBones.RightToes,
                0.25f),
            new HumanOtherSourceSegmentBinding(
                HumanOtherContactSegmentId.RightFoot,
                11,
                24,
                HumanBodyBones.RightFoot,
                HumanBodyBones.RightToes,
                0.25f),
            new HumanOtherSourceSegmentBinding(
                HumanOtherContactSegmentId.LeftShin,
                13,
                14,
                HumanBodyBones.LeftLowerLeg,
                HumanBodyBones.LeftFoot,
                0.12f),
            new HumanOtherSourceSegmentBinding(
                HumanOtherContactSegmentId.RightShin,
                10,
                11,
                HumanBodyBones.RightLowerLeg,
                HumanBodyBones.RightFoot,
                0.12f),
            new HumanOtherSourceSegmentBinding(
                HumanOtherContactSegmentId.LeftThigh,
                12,
                13,
                HumanBodyBones.LeftUpperLeg,
                HumanBodyBones.LeftLowerLeg,
                0.18f),
            new HumanOtherSourceSegmentBinding(
                HumanOtherContactSegmentId.RightThigh,
                9,
                10,
                HumanBodyBones.RightUpperLeg,
                HumanBodyBones.RightLowerLeg,
                0.18f),
            new HumanOtherSourceSegmentBinding(
                HumanOtherContactSegmentId.LeftForearm,
                6,
                7,
                HumanBodyBones.LeftLowerArm,
                HumanBodyBones.LeftHand,
                0.12f),
            new HumanOtherSourceSegmentBinding(
                HumanOtherContactSegmentId.RightForearm,
                3,
                4,
                HumanBodyBones.RightLowerArm,
                HumanBodyBones.RightHand,
                0.12f),
            new HumanOtherSourceSegmentBinding(
                HumanOtherContactSegmentId.LeftUpperArm,
                5,
                6,
                HumanBodyBones.LeftUpperArm,
                HumanBodyBones.LeftLowerArm,
                0.16f),
            new HumanOtherSourceSegmentBinding(
                HumanOtherContactSegmentId.RightUpperArm,
                2,
                3,
                HumanBodyBones.RightUpperArm,
                HumanBodyBones.RightLowerArm,
                0.16f),
            new HumanOtherSourceSegmentBinding(
                HumanOtherContactSegmentId.Torso,
                8,
                1,
                HumanBodyBones.Hips,
                HumanBodyBones.Neck,
                0.30f),
            new HumanOtherSourceSegmentBinding(
                HumanOtherContactSegmentId.Shoulders,
                2,
                5,
                HumanBodyBones.RightShoulder,
                HumanBodyBones.LeftShoulder,
                0.30f),
            new HumanOtherSourceSegmentBinding(
                HumanOtherContactSegmentId.Head,
                1,
                0,
                HumanBodyBones.Neck,
                HumanBodyBones.Head,
                0.35f)
        };

    // hmr2_openpose25_extra19 のうち骨長推定に使うインデックス
    private const int HumanSourceKeypointNeck = 1;
    private const int HumanSourceKeypointRightHip = 9;
    private const int HumanSourceKeypointRightKnee = 10;
    private const int HumanSourceKeypointRightAnkle = 11;
    private const int HumanSourceKeypointLeftHip = 12;
    private const int HumanSourceKeypointLeftKnee = 13;
    private const int HumanSourceKeypointLeftAnkle = 14;
    private const int HumanSourceKeypointPelvis = 39;
    private const int HumanSourceKeypointMinimumCount = 40;

    // 脚（大腿+下腿）と胴の合計に対する全身高の比。頭部ぶんを補う。
    // bundle_human.svb 実測: thigh 0.388 + shin 0.403 + torso 0.478 = 1.269、
    // head_top→ankle から推定した身長 1.455 → 比 1.15。
    private const float HumanSourceKeypointHeightFromLimbTorsoRatio = 1.15f;

    private readonly Dictionary<uint, HumanOtherContactState>
        humanOtherContactStateByTrack =
            new Dictionary<uint, HumanOtherContactState>();
    // keypoints3d (jointsCam) は HMR2 の実寸（root 相対 m）で、bbox から決まる表示スケールとは
    // 一致しない。骨長は姿勢によらずほぼ一定（実測で標準偏差 3-4%）なので、track ごとに
    // 一度だけ推定してキャッシュする。
    private readonly Dictionary<uint, float>
        humanKeypointHeightMetersByTrack = new Dictionary<uint, float>();

    private void ResetHumanOtherContactState()
    {
        humanOtherContactStateByTrack.Clear();
        humanKeypointHeightMetersByTrack.Clear();
    }

    // shot 境界で呼ぶ。前 shot で確定した接触オフセットは新しいカットのカメラ距離では
    // 意味を持たないので捨てる。骨長キャッシュ (humanKeypointHeightMetersByTrack) は
    // root 相対の実寸から求めた値で、カメラ距離が変わっても不変なので保持する。
    private void ResetHumanOtherContactStateForShotBoundary()
    {
        humanOtherContactStateByTrack.Clear();
    }

    private void ApplyHumanOtherContactCorrectionForFrame()
    {
        LogHumanOtherContactEntryIfEnabled(GetCurrentFrameIndex());
        if (!enableHumanOtherContactCorrection)
        {
            return;
        }

        if (manifest == null ||
            manifest.eye_w <= 0 ||
            manifest.eye_h <= 0 ||
            !TryGetFocalLengths(out float fx, out float fy))
        {
            LogHumanOtherContactSkipIfEnabled(
                GetCurrentFrameIndex(),
                0,
                "manifest / focal length が未解決");
            return;
        }

        int frameIndex = GetCurrentFrameIndex();
        for (int otherIndex = 0;
             otherIndex < metaFrameObjects.Count;
             otherIndex++)
        {
            MetaObj other = metaFrameObjects[otherIndex];
            if (!IsCategoryOther(other.categoryId))
            {
                continue;
            }

            if (!trackInstances.TryGetValue(
                    other.trackId,
                    out GameObject otherInstance) ||
                otherInstance == null ||
                !otherInstance.activeInHierarchy)
            {
                LogHumanOtherContactSkipIfEnabled(
                    frameIndex,
                    other.trackId,
                    "Other の instance が無い/非アクティブ");
                continue;
            }

            if (!ResolveAnchorToScreen(
                    other.anchorU,
                    out Transform otherScreen,
                    out int otherUEye,
                    out bool otherIsRightEye) ||
                !TryGetPinholeBasis(
                    otherScreen,
                    out Vector3 camOrigin,
                    out Quaternion camRotation))
            {
                LogHumanOtherContactSkipIfEnabled(
                    frameIndex,
                    other.trackId,
                    "screen / pinhole basis が未解決");
                continue;
            }

            float sourceOtherRadiusPixels =
                Mathf.Max(1f, (other.bboxW + other.bboxH) * 0.25f);
            Vector2 sourceOtherPixel =
                new Vector2(otherUEye, other.anchorV);
            float displayedOtherRadiusPixels = sourceOtherRadiusPixels;
            if (TryResolveRenderedOtherRadiusPixels(
                    otherInstance,
                    otherScreen,
                    fx,
                    fy,
                    out float renderedOtherRadiusPixels))
            {
                displayedOtherRadiusPixels =
                    Mathf.Max(
                        displayedOtherRadiusPixels,
                        renderedOtherRadiusPixels);
            }

            bool hasPreviousState =
                humanOtherContactStateByTrack.TryGetValue(
                    other.trackId,
                    out HumanOtherContactState previousState) &&
                previousState.lastFrame >= frameIndex - 1 &&
                previousState.lastFrame <= frameIndex;
            bool hasCandidate = false;
            HumanOtherContactCandidate bestCandidate =
                default(HumanOtherContactCandidate);
            for (int humanIndex = 0;
                 humanIndex < metaFrameObjects.Count;
                 humanIndex++)
            {
                MetaObj human = metaFrameObjects[humanIndex];
                if (!IsCategoryPerson(human.categoryId) ||
                    !trackInstances.TryGetValue(
                        human.trackId,
                        out GameObject humanInstance) ||
                    humanInstance == null ||
                    !humanInstance.activeInHierarchy ||
                    !TryResolveHumanOtherContactCandidate(
                        frameIndex,
                        human,
                        humanInstance,
                        sourceOtherPixel,
                        sourceOtherRadiusPixels,
                        displayedOtherRadiusPixels,
                        otherIsRightEye,
                        camOrigin,
                        camRotation,
                        fx,
                        fy,
                        hasPreviousState,
                        previousState,
                        out HumanOtherContactCandidate candidate))
                {
                    continue;
                }

                if (!hasCandidate ||
                    (candidate.matchesPreviousSegment &&
                     !bestCandidate.matchesPreviousSegment) ||
                    (candidate.matchesPreviousSegment ==
                         bestCandidate.matchesPreviousSegment &&
                     (candidate.contactWeight > bestCandidate.contactWeight ||
                      (Mathf.Approximately(
                           candidate.contactWeight,
                           bestCandidate.contactWeight) &&
                       candidate.sourceCenterToSegmentPixels <
                           bestCandidate.sourceCenterToSegmentPixels))))
                {
                    hasCandidate = true;
                    bestCandidate = candidate;
                }
            }

            if (!hasCandidate || bestCandidate.contactWeight <= 0f)
            {
                LogHumanOtherContactSkipIfEnabled(
                    frameIndex,
                    other.trackId,
                    hasCandidate
                        ? $"contactWeight=0 (seg={bestCandidate.segmentId} " +
                          $"srcDist={bestCandidate.sourceCenterToSegmentPixels:F1}px)"
                        : "接触候補なし（Human 側の対応部位が見つからない）");
                if (hasPreviousState &&
                    previousState.lastFrame == frameIndex - 1)
                {
                    Vector2 releasedPixelOffset = Vector2.MoveTowards(
                        previousState.appliedPixelOffset,
                        Vector2.zero,
                        HumanOtherMaximumCorrectionDeltaPerFramePixels);
                    float releasedDepthOffset = Mathf.MoveTowards(
                        previousState.appliedDepthOffsetMeters,
                        0f,
                        HumanOtherMaximumDepthDeltaPerFrameMeters);
                    ApplyHumanOtherContactOffset(
                        other,
                        otherInstance,
                        otherScreen,
                        sourceOtherPixel,
                        releasedPixelOffset,
                        releasedDepthOffset,
                        camOrigin,
                        camRotation,
                        fx,
                        fy);
                    previousState.lastFrame = frameIndex;
                    previousState.appliedPixelOffset =
                        releasedPixelOffset;
                    previousState.appliedDepthOffsetMeters =
                        releasedDepthOffset;
                    humanOtherContactStateByTrack[other.trackId] =
                        previousState;
                }
                else
                {
                    humanOtherContactStateByTrack.Remove(other.trackId);
                }
                continue;
            }

            float contactSegmentParameter =
                bestCandidate.sourceSegmentParameter;
            Vector2 contactLocalDirection =
                bestCandidate.sourceLocalDirection;
            bool continuesSameSegment =
                hasPreviousState &&
                previousState.hasContactMapping &&
                previousState.humanTrackId == bestCandidate.humanTrackId &&
                previousState.segmentId == bestCandidate.segmentId &&
                previousState.lastFrame >= frameIndex - 1 &&
                previousState.lastFrame <= frameIndex;
            if (continuesSameSegment &&
                previousState.lastFrame == frameIndex)
            {
                contactSegmentParameter =
                    previousState.contactSegmentParameter;
                contactLocalDirection =
                    previousState.contactLocalDirection;
            }
            else if (continuesSameSegment)
            {
                contactSegmentParameter = Mathf.MoveTowards(
                    previousState.contactSegmentParameter,
                    contactSegmentParameter,
                    HumanOtherMaximumContactParameterDeltaPerFrame);
                contactLocalDirection = RotateDirectionTowards(
                    previousState.contactLocalDirection,
                    contactLocalDirection,
                    HumanOtherMaximumContactDirectionDegreesPerFrame);
            }

            if (!HumanOtherContactCorrectionMath.TryResolveDisplayedSegmentContact(
                    bestCandidate.displayedStartPixel,
                    bestCandidate.displayedEndPixel,
                    contactSegmentParameter,
                    contactLocalDirection,
                    bestCandidate.minimumCenterToSegmentPixels,
                    out Vector2 targetPixel))
            {
                continue;
            }
            float targetDepthMeters = Mathf.Lerp(
                bestCandidate.displayedStartCam.z,
                bestCandidate.displayedEndCam.z,
                contactSegmentParameter);
            Vector2 desiredPixelOffset =
                (targetPixel - sourceOtherPixel) *
                bestCandidate.contactWeight;
            float desiredDepthOffset =
                (targetDepthMeters - other.anchorZ) *
                bestCandidate.contactWeight;
            Vector2 appliedPixelOffset = desiredPixelOffset;
            float appliedDepthOffset = desiredDepthOffset;
            if (hasPreviousState &&
                previousState.lastFrame == frameIndex)
            {
                appliedPixelOffset =
                    previousState.appliedPixelOffset;
                appliedDepthOffset =
                    previousState.appliedDepthOffsetMeters;
            }
            else if (hasPreviousState &&
                     previousState.lastFrame == frameIndex - 1)
            {
                appliedPixelOffset = Vector2.MoveTowards(
                    previousState.appliedPixelOffset,
                    desiredPixelOffset,
                    HumanOtherMaximumCorrectionDeltaPerFramePixels);
                appliedDepthOffset = Mathf.MoveTowards(
                    previousState.appliedDepthOffsetMeters,
                    desiredDepthOffset,
                    HumanOtherMaximumDepthDeltaPerFrameMeters);
            }

            LogHumanOtherContactIfEnabled(
                frameIndex,
                other.trackId,
                bestCandidate,
                sourceOtherPixel,
                targetPixel,
                desiredPixelOffset,
                appliedPixelOffset,
                appliedDepthOffset,
                hasPreviousState,
                previousState);
            ApplyHumanOtherContactOffset(
                other,
                otherInstance,
                otherScreen,
                sourceOtherPixel,
                appliedPixelOffset,
                appliedDepthOffset,
                camOrigin,
                camRotation,
                fx,
                fy);
            humanOtherContactStateByTrack[other.trackId] =
                new HumanOtherContactState
                {
                    humanTrackId = bestCandidate.humanTrackId,
                    segmentId = bestCandidate.segmentId,
                    lastFrame = frameIndex,
                    appliedPixelOffset = appliedPixelOffset,
                    appliedDepthOffsetMeters = appliedDepthOffset,
                    hasContactMapping = true,
                    contactSegmentParameter = contactSegmentParameter,
                    contactLocalDirection = contactLocalDirection
                };
        }
    }

    private int lastHumanOtherContactLoggedFrame = -1;

    // 診断用。どの部位を選び、どれだけ動かしたかを出す。
    // clamped=False は移動量の上限が効いていないケース（初回接触、またはフレーム跳びで
    // previousState.lastFrame が frameIndex-1 にならなかった場合）を示す。
    private void LogHumanOtherContactIfEnabled(
        int frameIndex,
        uint otherTrackId,
        HumanOtherContactCandidate candidate,
        Vector2 sourceOtherPixel,
        Vector2 targetPixel,
        Vector2 desiredPixelOffset,
        Vector2 appliedPixelOffset,
        float appliedDepthOffset,
        bool hasPreviousState,
        HumanOtherContactState previousState)
    {
        if (frameIndex == lastHumanOtherContactLoggedFrame ||
            frameIndex % Mathf.Max(1, logHumanOtherContactEveryNFrames) != 0)
        {
            return;
        }

        lastHumanOtherContactLoggedFrame = frameIndex;
        bool clamped =
            hasPreviousState && previousState.lastFrame == frameIndex - 1;
        string previousSegment = hasPreviousState
            ? previousState.segmentId.ToString()
            : "-";
        Debug.Log(
            $"[CONTACT] f={frameIndex} track={otherTrackId} " +
            $"seg={candidate.segmentId} prevSeg={previousSegment} " +
            $"w={candidate.contactWeight:F2} " +
            $"srcDist={candidate.sourceCenterToSegmentPixels:F1}px " +
            $"src=({sourceOtherPixel.x:F0},{sourceOtherPixel.y:F0}) " +
            $"tgt=({targetPixel.x:F0},{targetPixel.y:F0}) " +
            $"desired={desiredPixelOffset.magnitude:F1}px " +
            $"applied={appliedPixelOffset.magnitude:F1}px " +
            $"depth={appliedDepthOffset:F3}m clamped={clamped}");
    }

    private int lastHumanOtherEntryLoggedFrame = -1;

    // 【調査中の一時措置】以下 3 つのログは logHumanOtherContact のガードを外してある。
    // フラグを既定 true にしても Unity がシーン側の旧値 (false) を優先して反映されず、
    // 診断が進まなかったため。調査が終わったら 3 メソッドとも
    // `if (!logHumanOtherContact || ...)` のガードを戻すか、ログ自体を削除すること。

    // ApplyHumanOtherContactCorrectionForFrame が実際に呼ばれているかを確認する入口ログ。
    // これすら出ない場合は DisplayModelTick が補正の呼び出しまで到達していない。
    private void LogHumanOtherContactEntryIfEnabled(int frameIndex)
    {
        if (frameIndex == lastHumanOtherEntryLoggedFrame ||
            frameIndex % Mathf.Max(1, logHumanOtherContactEveryNFrames * 6) != 0)
        {
            return;
        }

        lastHumanOtherEntryLoggedFrame = frameIndex;
        Debug.Log(
            $"[CONTACT-ENTRY] f={frameIndex} " +
            $"enable={enableHumanOtherContactCorrection} " +
            $"metaObjs={metaFrameObjects.Count} " +
            $"instances={trackInstances.Count}");
    }

    // 補正が適用されなかったフレームの理由を出す。
    private void LogHumanOtherContactSkipIfEnabled(
        int frameIndex,
        uint otherTrackId,
        string reason)
    {
        if (frameIndex == lastHumanOtherContactLoggedFrame ||
            frameIndex % Mathf.Max(1, logHumanOtherContactEveryNFrames) != 0)
        {
            return;
        }

        lastHumanOtherContactLoggedFrame = frameIndex;
        Debug.Log(
            $"[CONTACT] f={frameIndex} track={otherTrackId} 補正なし: {reason}");
    }

    private static Vector2 RotateDirectionTowards(
        Vector2 from,
        Vector2 to,
        float maximumDegrees)
    {
        if (from.sqrMagnitude <= 0.0001f)
        {
            return to.normalized;
        }
        if (to.sqrMagnitude <= 0.0001f)
        {
            return from.normalized;
        }

        from.Normalize();
        to.Normalize();
        float angle = Mathf.Clamp(
            Vector2.SignedAngle(from, to),
            -Mathf.Max(0f, maximumDegrees),
            Mathf.Max(0f, maximumDegrees));
        float radians = angle * Mathf.Deg2Rad;
        float cos = Mathf.Cos(radians);
        float sin = Mathf.Sin(radians);
        return new Vector2(
            from.x * cos - from.y * sin,
            from.x * sin + from.y * cos).normalized;
    }

    private void ApplyHumanOtherContactOffset(
        MetaObj other,
        GameObject otherInstance,
        Transform otherScreen,
        Vector2 sourceOtherPixel,
        Vector2 pixelOffset,
        float depthOffsetMeters,
        Vector3 camOrigin,
        Quaternion camRotation,
        float fx,
        float fy)
    {
        Vector2 correctedPixel =
            sourceOtherPixel + pixelOffset;
        float correctedDepth =
            Mathf.Max(0.001f, other.anchorZ + depthOffsetMeters);
        Vector3 originalAnchorWorld = AnchorUvZToWorldPinhole(
            otherScreen,
            sourceOtherPixel.x,
            sourceOtherPixel.y,
            other.anchorZ);
        Vector3 correctedAnchorWorld =
            PinholePlacementSpace.EyePixelDepthToWorld(
                camOrigin,
                camRotation,
                manifest,
                correctedPixel.x,
                correctedPixel.y,
                correctedDepth,
                fx,
                fy);
        TrackPlacementWriter.ApplyPosition(
            otherInstance.transform,
            otherInstance.transform.position +
            correctedAnchorWorld -
            originalAnchorWorld);
    }

    private bool TryResolveHumanOtherContactCandidate(
        int frameIndex,
        MetaObj human,
        GameObject humanInstance,
        Vector2 sourceOtherPixel,
        float sourceOtherRadiusPixels,
        float displayedOtherRadiusPixels,
        bool otherIsRightEye,
        Vector3 camOrigin,
        Quaternion camRotation,
        float fx,
        float fy,
        bool hasPreviousState,
        HumanOtherContactState previousState,
        out HumanOtherContactCandidate candidate)
    {
        candidate = default(HumanOtherContactCandidate);
        if (!ResolveAnchorToScreen(
                human.anchorU,
                out _,
                out _,
                out bool humanIsRightEye) ||
            humanIsRightEye != otherIsRightEye ||
            !TryBuildHumanSourceContactPose(
                human,
                fx,
                fy,
                out HumanSourcePose2D sourcePose))
        {
            return false;
        }

        Animator animator =
            humanInstance.GetComponentInChildren<Animator>(true);
        if (animator == null || !animator.isHuman)
        {
            return false;
        }
        HumanoidRigCache cache = GetOrBuildHumanoidCache(animator);
        if (cache == null || !cache.ready)
        {
            return false;
        }

        Quaternion worldToCam = Quaternion.Inverse(camRotation);
        bool hasBestCandidate = false;
        HumanOtherContactCandidate bestCandidate =
            default(HumanOtherContactCandidate);
        bool hasPreviousCandidate = false;
        HumanOtherContactCandidate previousCandidate =
            default(HumanOtherContactCandidate);
        for (int i = 0; i < HumanOtherSourceSegments.Length; i++)
        {
            HumanOtherSourceSegmentBinding binding =
                HumanOtherSourceSegments[i];
            if (binding.sourceStart < 0 ||
                binding.sourceEnd < 0 ||
                binding.sourceStart >= sourcePose.keypoints.Length ||
                binding.sourceEnd >= sourcePose.keypoints.Length)
            {
                continue;
            }

            Vector2 sourceStart =
                sourcePose.keypoints[binding.sourceStart];
            Vector2 sourceEnd =
                sourcePose.keypoints[binding.sourceEnd];
            float sourceCenterToSegmentPixels =
                HumanOtherContactCorrectionMath.DistanceToSegment(
                    sourceOtherPixel,
                    sourceStart,
                    sourceEnd);
            float sourceProxyRadiusPixels =
                Vector2.Distance(sourceStart, sourceEnd) *
                binding.displayedProxyRadiusToLength;
            bool matchesPrevious =
                hasPreviousState &&
                previousState.humanTrackId == human.trackId &&
                previousState.segmentId == binding.id;
            float releaseRadiusMultiplier =
                matchesPrevious
                    ? humanOtherReleaseRadiusMultiplier * 1.25f
                    : humanOtherReleaseRadiusMultiplier;
            float contactWeight =
                HumanOtherContactCorrectionMath.ResolveContactWeight(
                    sourceCenterToSegmentPixels,
                    sourceOtherRadiusPixels +
                    sourceProxyRadiusPixels,
                    humanOtherFullContactRadiusMultiplier,
                    releaseRadiusMultiplier);
            if (contactWeight <= 0f ||
                !TryResolveDisplayedSegment(
                    cache,
                    binding,
                    worldToCam,
                    camOrigin,
                    fx,
                    fy,
                    out Vector2 displayedStartPixel,
                    out Vector2 displayedEndPixel,
                    out Vector3 displayedStartCam,
                    out Vector3 displayedEndCam))
            {
                continue;
            }

            float displayedProxyRadiusPixels =
                Vector2.Distance(
                    displayedStartPixel,
                    displayedEndPixel) *
                binding.displayedProxyRadiusToLength;
            float minimumCenterToSegmentPixels =
                displayedOtherRadiusPixels +
                displayedProxyRadiusPixels +
                Mathf.Max(
                    0f,
                    humanOtherContactSurfacePaddingPixels);
            if (!HumanOtherContactCorrectionMath.TryResolveSourceSegmentContact(
                    sourceOtherPixel,
                    sourceStart,
                    sourceEnd,
                    out float segmentParameter,
                    out Vector2 localDirection))
            {
                continue;
            }

            HumanOtherContactCandidate current =
                new HumanOtherContactCandidate
                {
                    humanTrackId = human.trackId,
                    segmentId = binding.id,
                    matchesPreviousSegment = matchesPrevious,
                    contactWeight = contactWeight,
                    sourceCenterToSegmentPixels =
                        sourceCenterToSegmentPixels,
                    sourceSegmentParameter = segmentParameter,
                    sourceLocalDirection = localDirection,
                    displayedStartPixel = displayedStartPixel,
                    displayedEndPixel = displayedEndPixel,
                    displayedStartCam = displayedStartCam,
                    displayedEndCam = displayedEndCam,
                    minimumCenterToSegmentPixels =
                        minimumCenterToSegmentPixels
                };
            if (!hasBestCandidate ||
                current.sourceCenterToSegmentPixels <
                    bestCandidate.sourceCenterToSegmentPixels ||
                (Mathf.Approximately(
                     current.sourceCenterToSegmentPixels,
                     bestCandidate.sourceCenterToSegmentPixels) &&
                 current.contactWeight > bestCandidate.contactWeight))
            {
                hasBestCandidate = true;
                bestCandidate = current;
            }
            if (current.matchesPreviousSegment &&
                (!hasPreviousCandidate ||
                 current.sourceCenterToSegmentPixels <
                     previousCandidate.sourceCenterToSegmentPixels))
            {
                hasPreviousCandidate = true;
                previousCandidate = current;
            }
        }

        if (!hasBestCandidate)
        {
            return false;
        }

        candidate =
            hasPreviousCandidate &&
            previousCandidate.contactWeight >= 0.5f
                ? previousCandidate
                : bestCandidate;
        return true;
    }

    // meta.bin の keypoints3d（jointsCam: root 相対の camera xyz、HMR2 実寸）を、表示中の Human と
    // 同じ見た目サイズになるようスケールしてから eye pixel に投影し、接触判定用の
    // 「元映像相当の 2D pose」を作る。source/human_smpl_from_sam2.json には依存しない
    // （CLAUDE.md の鉄則: 配置・姿勢追従には meta.bin と manifest.json のみを使う）。
    private bool TryBuildHumanSourceContactPose(
        MetaObj human,
        float fx,
        float fy,
        out HumanSourcePose2D pose)
    {
        pose = null;
        if (manifest == null ||
            manifest.eye_w <= 0 ||
            manifest.eye_h <= 0 ||
            fx <= 0f ||
            fy <= 0f ||
            human.jointsCam == null ||
            human.jointsCam.Length < HumanSourceKeypointMinimumCount ||
            human.bboxH <= 0 ||
            !TryResolveHumanKeypointHeightMeters(
                human,
                out float keypointHeightMeters) ||
            !ResolveAnchorToScreen(human.anchorU, out _, out int uEye, out _))
        {
            return false;
        }

        // keypoints3d の推定身長を bbox の world 高さに合わせる。これで投影結果が
        // 元映像上の Human の見た目サイズと一致し、Other の anchor と同じ土俵で比較できる。
        float bboxWorldHeight =
            (2f * human.bboxH / manifest.eye_h) * (human.anchorZ / fy);
        float scale = bboxWorldHeight / keypointHeightMeters;
        if (!(scale > 0f) || float.IsInfinity(scale))
        {
            return false;
        }

        Vector3 rootCam = PinholePlacementSpace.ReconstructCamLocalFromEyePixel(
            manifest,
            Mathf.Clamp(uEye, 0f, manifest.eye_w - 1f),
            Mathf.Clamp(human.anchorV, 0f, manifest.eye_h - 1f),
            human.anchorZ,
            fx,
            fy,
            manifest.eye_w,
            manifest.eye_h);

        Vector2[] keypoints = new Vector2[human.jointsCam.Length];
        for (int i = 0; i < human.jointsCam.Length; i++)
        {
            Vector3 jointCam = rootCam + (human.jointsCam[i] * scale);
            if (!PinholePlacementSpace.TryProjectCamLocalToEyePixel(
                    manifest,
                    jointCam,
                    fx,
                    fy,
                    out Vector2 pixel))
            {
                return false;
            }

            keypoints[i] = pixel;
        }

        pose = new HumanSourcePose2D(
            HumanSourcePoseSidecar.Hmr2OpenPose25Extra19,
            new Rect(human.bboxX, human.bboxY, human.bboxW, human.bboxH),
            keypoints);
        return true;
    }

    // 骨長（大腿・下腿・胴）の合計から全身高を推定する。骨長は姿勢によらずほぼ一定なので
    // track ごとに一度だけ測ってキャッシュする。左右どちらかが不可視でも反対側で代替する。
    private bool TryResolveHumanKeypointHeightMeters(
        MetaObj human,
        out float heightMeters)
    {
        if (humanKeypointHeightMetersByTrack.TryGetValue(
                human.trackId,
                out heightMeters) &&
            heightMeters > 0f)
        {
            return true;
        }

        heightMeters = 0f;
        Vector3[] joints = human.jointsCam;
        if (joints == null ||
            joints.Length < HumanSourceKeypointMinimumCount)
        {
            return false;
        }

        byte[] vis = human.jointsVis;
        float thigh = ResolveVisibleSegmentLength(
            joints, vis, HumanSourceKeypointRightHip, HumanSourceKeypointRightKnee);
        if (thigh <= 0f)
        {
            thigh = ResolveVisibleSegmentLength(
                joints, vis, HumanSourceKeypointLeftHip, HumanSourceKeypointLeftKnee);
        }

        float shin = ResolveVisibleSegmentLength(
            joints, vis, HumanSourceKeypointRightKnee, HumanSourceKeypointRightAnkle);
        if (shin <= 0f)
        {
            shin = ResolveVisibleSegmentLength(
                joints, vis, HumanSourceKeypointLeftKnee, HumanSourceKeypointLeftAnkle);
        }

        float torso = ResolveVisibleSegmentLength(
            joints, vis, HumanSourceKeypointPelvis, HumanSourceKeypointNeck);
        if (thigh <= 0f || shin <= 0f || torso <= 0f)
        {
            return false;
        }

        heightMeters =
            (thigh + shin + torso) *
            HumanSourceKeypointHeightFromLimbTorsoRatio;
        if (!(heightMeters > 0f) || float.IsInfinity(heightMeters))
        {
            heightMeters = 0f;
            return false;
        }

        humanKeypointHeightMetersByTrack[human.trackId] = heightMeters;
        return true;
    }

    private static float ResolveVisibleSegmentLength(
        Vector3[] joints,
        byte[] vis,
        int startIndex,
        int endIndex)
    {
        if (startIndex < 0 ||
            endIndex < 0 ||
            startIndex >= joints.Length ||
            endIndex >= joints.Length)
        {
            return 0f;
        }

        if (vis != null &&
            startIndex < vis.Length &&
            endIndex < vis.Length &&
            (vis[startIndex] == 0 || vis[endIndex] == 0))
        {
            return 0f;
        }

        return Vector3.Distance(joints[startIndex], joints[endIndex]);
    }

    private bool TryResolveRenderedOtherRadiusPixels(
        GameObject otherInstance,
        Transform screen,
        float fx,
        float fy,
        out float radiusPixels)
    {
        radiusPixels = 0f;
        if (otherInstance == null ||
            !TryGetPinholeBasis(
                screen,
                out Vector3 camOrigin,
                out Quaternion camRotation))
        {
            return false;
        }

        Quaternion worldToCam = Quaternion.Inverse(camRotation);
        Renderer[] renderers =
            otherInstance.GetComponentsInChildren<Renderer>(true);
        float minU = float.MaxValue;
        float maxU = float.MinValue;
        float minV = float.MaxValue;
        float maxV = float.MinValue;
        bool hasPoint = false;
        for (int rendererIndex = 0;
             rendererIndex < renderers.Length;
             rendererIndex++)
        {
            Renderer renderer = renderers[rendererIndex];
            if (renderer == null || !renderer.enabled)
            {
                continue;
            }

            Bounds bounds = renderer.bounds;
            Vector3 min = bounds.min;
            Vector3 max = bounds.max;
            for (int corner = 0; corner < 8; corner++)
            {
                Vector3 world = new Vector3(
                    (corner & 1) == 0 ? min.x : max.x,
                    (corner & 2) == 0 ? min.y : max.y,
                    (corner & 4) == 0 ? min.z : max.z);
                Vector3 cam =
                    worldToCam * (world - camOrigin);
                if (!PinholePlacementSpace.TryProjectCamLocalToEyePixel(
                        manifest,
                        cam,
                        fx,
                        fy,
                        out Vector2 pixel))
                {
                    continue;
                }

                minU = Mathf.Min(minU, pixel.x);
                maxU = Mathf.Max(maxU, pixel.x);
                minV = Mathf.Min(minV, pixel.y);
                maxV = Mathf.Max(maxV, pixel.y);
                hasPoint = true;
            }
        }

        if (!hasPoint)
        {
            return false;
        }

        radiusPixels =
            Mathf.Max(maxU - minU, maxV - minV) * 0.5f;
        return radiusPixels > 0f;
    }

    private bool TryResolveDisplayedSegment(
        HumanoidRigCache cache,
        HumanOtherSourceSegmentBinding binding,
        Quaternion worldToCam,
        Vector3 camOrigin,
        float fx,
        float fy,
        out Vector2 segmentStartPixel,
        out Vector2 segmentEndPixel,
        out Vector3 segmentStartCam,
        out Vector3 segmentEndCam)
    {
        segmentStartPixel = Vector2.zero;
        segmentEndPixel = Vector2.zero;
        segmentStartCam = Vector3.zero;
        segmentEndCam = Vector3.zero;
        if (cache == null ||
            !cache.bones.TryGetValue(
                binding.displayedStart,
                out Transform start) ||
            !cache.bones.TryGetValue(
                binding.displayedEnd,
                out Transform end) ||
            start == null ||
            end == null)
        {
            return false;
        }

        segmentStartCam =
            worldToCam * (start.position - camOrigin);
        segmentEndCam =
            worldToCam * (end.position - camOrigin);
        return PinholePlacementSpace.TryProjectCamLocalToEyePixel(
                   manifest,
                   segmentStartCam,
                   fx,
                   fy,
                   out segmentStartPixel) &&
               PinholePlacementSpace.TryProjectCamLocalToEyePixel(
                   manifest,
                   segmentEndCam,
                   fx,
                   fy,
                   out segmentEndPixel);
    }
}
