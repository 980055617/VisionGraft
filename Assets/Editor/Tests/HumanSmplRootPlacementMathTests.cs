using NUnit.Framework;
using UnityEngine;

public class HumanSmplRootPlacementMathTests
{
    [Test]
    public void HumanSmplRootPlacementKeepsTheDisplayedRootUpright()
    {
        Quaternion hmr2GlobalOrient = Quaternion.LookRotation(
            new Vector3(0.929419875f, -0.289010435f, -0.229459435f).normalized,
            new Vector3(-0.22749956f, -0.938325167f, 0.260364801f).normalized);

        bool built = StreamingStereoVideoPlayer.TryBuildHumanSmplUprightRootRotation(
            Quaternion.identity,
            hmr2GlobalOrient,
            Vector3.up,
            out Quaternion rootRotation);

        Assert.That(built, Is.True);
        Assert.That(Vector3.Dot(rootRotation * Vector3.up, Vector3.up), Is.GreaterThan(0.999f));
        Assert.That(Mathf.Abs(Vector3.Dot(rootRotation * Vector3.forward, Vector3.up)), Is.LessThan(0.001f));
    }

    [Test]
    public void HumanSmplLocalRotationRetargetPreservesUnityReferencePose()
    {
        Quaternion referenceUnityLocal = Quaternion.Euler(0f, 30f, 0f);
        Quaternion referenceSmplLocal = Quaternion.Euler(0f, 90f, 0f);
        Quaternion currentSmplLocal = referenceSmplLocal;

        Quaternion target = StreamingStereoVideoPlayer.RetargetHumanSmplLocalRotation(
            referenceUnityLocal,
            referenceSmplLocal,
            currentSmplLocal);

        Assert.That(Quaternion.Angle(referenceUnityLocal, target), Is.LessThan(0.001f));
    }

    [Test]
    public void HumanSmplMotionAppliesHandRotation()
    {
        Assert.That(StreamingStereoVideoPlayer.ShouldApplyHumanSmplFullHandRotation(), Is.True);
    }

    [Test]
    public void HumanSmplMotionKeepsLowerArmIkBendAsGrossPoseAuthority()
    {
        Assert.That(StreamingStereoVideoPlayer.ShouldApplyHumanSmplLowerArmBendRotation(), Is.False);
    }

    [Test]
    public void HumanSmplMotionDoesNotOverrideTrackedRootOrientation()
    {
        Assert.That(StreamingStereoVideoPlayer.ShouldUseHumanSmplRootOrientation(), Is.False);
    }

    [Test]
    public void HumanSmplTranslationDoesNotDisableTrackedBBoxPlacement()
    {
        Assert.That(StreamingStereoVideoPlayer.ShouldUseHumanSmplRootPlacementPolicy(true, true), Is.False);
    }

    [Test]
    public void HumanoidTwoBoneIkPreservesSourceBendWhenTargetIsOutOfModelReach()
    {
        Vector3 sourceRoot = Vector3.zero;
        Vector3 sourceMid = Vector3.up;
        Vector3 sourceEnd = Vector3.up + Vector3.right;

        float resolvedDistance = StreamingStereoVideoPlayer.ResolveHumanoidTwoBoneTargetDistance(
            sourceRoot,
            sourceMid,
            sourceEnd,
            1f,
            1f,
            3f);

        Assert.That(resolvedDistance, Is.LessThan(1.5f));
        Assert.That(resolvedDistance, Is.GreaterThan(1.4f));
    }

    [Test]
    public void HumanoidTwoBoneIkUsesResolvedEndTargetWhenRawTargetIsOutOfReach()
    {
        Vector3 modelRoot = Vector3.zero;
        Vector3 rawTarget = Vector3.right * 3f;

        Vector3 resolvedEnd = StreamingStereoVideoPlayer.ResolveHumanoidTwoBoneEndTarget(
            modelRoot,
            rawTarget,
            1.4f);

        Assert.That(resolvedEnd.x, Is.EqualTo(1.4f).Within(0.001f));
        Assert.That(resolvedEnd.y, Is.EqualTo(0f).Within(0.001f));
        Assert.That(resolvedEnd.z, Is.EqualTo(0f).Within(0.001f));
    }

    [Test]
    public void HumanoidTwoBoneIkKeepsBendNormalSignContinuous()
    {
        Vector3 previous = Vector3.forward;
        Vector3 current = -Vector3.forward;

        Vector3 stabilized = StreamingStereoVideoPlayer.StabilizeHumanoidBendNormal(previous, current);

        Assert.That(Vector3.Dot(previous, stabilized), Is.GreaterThan(0f));
    }

    [Test]
    public void HumanoidTwoBoneIkBendsTowardTheSameSideAsTheSourceMidJoint()
    {
        Vector3 sourceRoot = Vector3.zero;
        Vector3 sourceMid = Vector3.up;
        Vector3 sourceEnd = Vector3.right + Vector3.up;
        Vector3 targetDirection = (sourceEnd - sourceRoot).normalized;

        bool resolved = StreamingStereoVideoPlayer.TryResolveHumanoidTwoBoneBendDirection(
            sourceRoot,
            sourceMid,
            sourceEnd,
            targetDirection,
            Vector3.up,
            Vector3.right,
            out Vector3 bendDirection);

        Assert.That(resolved, Is.True);
        Assert.That(Vector3.Dot(bendDirection, sourceMid - sourceRoot), Is.GreaterThan(0f));
    }

    [Test]
    public void HumanSmplMotionAppliesAfterKeypointIkGrossPose()
    {
        Assert.That(StreamingStereoVideoPlayer.ShouldApplyHumanSmplBeforeLimbIk(), Is.False);
        Assert.That(StreamingStereoVideoPlayer.ShouldApplyHumanSmplAfterLimbIk(), Is.True);
    }

    [Test]
    public void HumanSmplMotionKeepsKeypointIkForGrossPoseWhenSmplRotationExists()
    {
        Assert.That(
            StreamingStereoVideoPlayer.ShouldUseKeypointIkForHumanBone(true, true),
            Is.True);
    }

    [Test]
    public void HumanSmplMotionUsesKeypointIkWhenSmplRotationIsMissing()
    {
        Assert.That(
            StreamingStereoVideoPlayer.ShouldUseKeypointIkForHumanBone(true, false),
            Is.True);
        Assert.That(
            StreamingStereoVideoPlayer.ShouldUseKeypointIkForHumanBone(false, false),
            Is.True);
    }

    [Test]
    public void HumanSmplMotionUsesBoundedOrientationOverlay()
    {
        Assert.That(
            StreamingStereoVideoPlayer.ResolveHumanSmplOrientationOverlayAlpha(0.65f, 1.0f),
            Is.EqualTo(0.65f).Within(0.001f));
        Assert.That(
            StreamingStereoVideoPlayer.ResolveHumanSmplOrientationOverlayAlpha(0.65f, 0.5f),
            Is.EqualTo(0.325f).Within(0.001f));
    }

    [Test]
    public void HumanSkeletonPlacementPreservesRootScreenHeight()
    {
        Assert.That(StreamingStereoVideoPlayer.ShouldPreserveRootScreenHeightAfterHumanSkeletonPlacement(), Is.True);
    }

    [Test]
    public void HumanSkeletonPlacementRestoresScreenUpComponentAfterRootAlignment()
    {
        Vector3 currentPosition = new Vector3(3.0f, 5.0f, 7.0f);
        Vector3 referencePosition = new Vector3(1.0f, 2.0f, 4.0f);

        Vector3 resolved = StreamingStereoVideoPlayer.ResolveRootPositionPreservingScreenHeight(
            currentPosition,
            referencePosition,
            Vector3.up);

        Assert.That(resolved.x, Is.EqualTo(currentPosition.x).Within(0.001f));
        Assert.That(resolved.y, Is.EqualTo(referencePosition.y).Within(0.001f));
        Assert.That(resolved.z, Is.EqualTo(currentPosition.z).Within(0.001f));
    }

    [Test]
    public void HumanoidInteractiveInPlaceClipPreservesHipsLocalPosition()
    {
        Vector3 basePosition = new Vector3(0.1f, 1.0f, -0.2f);
        Vector3 animatedPosition = new Vector3(2.0f, 1.2f, 3.0f);

        Vector3 resolved = StreamingStereoVideoPlayer.ResolveHumanoidInteractiveLocalPosition(
            HumanBodyBones.Hips,
            basePosition,
            animatedPosition,
            1.0f,
            false);

        Assert.That(resolved.x, Is.EqualTo(basePosition.x).Within(0.001f));
        Assert.That(resolved.y, Is.EqualTo(basePosition.y).Within(0.001f));
        Assert.That(resolved.z, Is.EqualTo(basePosition.z).Within(0.001f));
    }

    [Test]
    public void HumanoidInteractiveInPlaceClipDisablesPostAnimationBBoxFit()
    {
        Assert.That(StreamingStereoVideoPlayer.ShouldFitDisplayedModelToBBoxDuringInteractiveMotion(false, true), Is.False);
    }

    [Test]
    public void HumanoidInteractiveInPlaceClipFitsBBoxOnceBeforePinning()
    {
        Assert.That(
            StreamingStereoVideoPlayer.ShouldInitialFitHumanoidInPlaceRootBeforePinning(true, false, false),
            Is.True);
    }

    [Test]
    public void HumanoidInteractiveInPlaceClipDoesNotRefitBBoxAfterPinning()
    {
        Assert.That(
            StreamingStereoVideoPlayer.ShouldInitialFitHumanoidInPlaceRootBeforePinning(true, true, false),
            Is.False);
    }

    [Test]
    public void HumanoidInteractiveReplacementClipDisablesPostAnimationBBoxFit()
    {
        Assert.That(StreamingStereoVideoPlayer.ShouldFitDisplayedModelToBBoxDuringInteractiveMotion(true, false), Is.False);
    }

    [Test]
    public void HumanoidInteractiveIdleAllowsPostAnimationBBoxFit()
    {
        Assert.That(StreamingStereoVideoPlayer.ShouldFitDisplayedModelToBBoxDuringInteractiveMotion(false, false), Is.True);
    }

    [Test]
    public void HumanoidClipInPlaceAppliesViewerFacingRootTransform()
    {
        Assert.That(StreamingStereoVideoPlayer.ShouldApplyHumanFaceViewerRootTransform(false, true), Is.True);
    }

    [Test]
    public void HumanoidFaceViewerAppliesFaceViewerRootTransform()
    {
        Assert.That(StreamingStereoVideoPlayer.ShouldApplyHumanFaceViewerRootTransform(false, true), Is.True);
    }

    [Test]
    public void HumanoidInteractiveInPlaceClipPreservesRootTransformPosition()
    {
        Assert.That(StreamingStereoVideoPlayer.ShouldPreserveHumanoidInteractiveRootPosition(false), Is.True);
    }

    [Test]
    public void HumanoidInteractiveInPlaceClipKeepsPinnedStartRootPosition()
    {
        Vector3 currentPosition = new Vector3(3.0f, 2.0f, 1.0f);
        Vector3 startPosition = new Vector3(0.2f, 1.1f, -0.3f);

        Vector3 resolved = StreamingStereoVideoPlayer.ResolveHumanoidInteractiveRootPosition(
            currentPosition,
            startPosition,
            false);

        Assert.That(resolved.x, Is.EqualTo(startPosition.x).Within(0.001f));
        Assert.That(resolved.y, Is.EqualTo(startPosition.y).Within(0.001f));
        Assert.That(resolved.z, Is.EqualTo(startPosition.z).Within(0.001f));
    }

    [Test]
    public void HumanoidInteractiveInPlaceClipReleasesRootWhenEnvelopeIsZero()
    {
        Vector3 currentPosition = new Vector3(3.0f, 2.0f, 1.0f);
        Vector3 startPosition = new Vector3(0.2f, 1.1f, -0.3f);

        Vector3 resolved = StreamingStereoVideoPlayer.ResolveHumanoidInteractiveRootPosition(
            currentPosition,
            startPosition,
            false,
            0.0f);

        Assert.That(resolved.x, Is.EqualTo(currentPosition.x).Within(0.001f));
        Assert.That(resolved.y, Is.EqualTo(currentPosition.y).Within(0.001f));
        Assert.That(resolved.z, Is.EqualTo(currentPosition.z).Within(0.001f));
    }

    [Test]
    public void HumanoidInteractiveInPlaceClipBlendsRootDuringTransition()
    {
        Vector3 currentPosition = new Vector3(2.0f, 0.0f, 0.0f);
        Vector3 startPosition = Vector3.zero;

        Vector3 resolved = StreamingStereoVideoPlayer.ResolveHumanoidInteractiveRootPosition(
            currentPosition,
            startPosition,
            false,
            0.5f);

        Assert.That(resolved.x, Is.EqualTo(1.0f).Within(0.001f));
    }

    [Test]
    public void HumanoidInteractiveInPlaceClipPinsRootAtFullWeight()
    {
        Vector3 currentPosition = new Vector3(2.0f, 0.0f, 0.0f);
        Vector3 startPosition = Vector3.zero;

        Vector3 resolved = StreamingStereoVideoPlayer.ResolveHumanoidInteractiveRootPosition(
            currentPosition,
            startPosition,
            false,
            1.0f);

        Assert.That(resolved.x, Is.EqualTo(startPosition.x).Within(0.001f));
    }

    [Test]
    public void InteractiveReplacementPlacementCommandKeepsResolvedPoseAndCurrentScale()
    {
        Vector3 resolvedPosition = new Vector3(0.5f, 1.0f, -0.5f);
        Quaternion resolvedRotation = Quaternion.Euler(0.0f, 45.0f, 0.0f);
        Vector3 currentScale = new Vector3(1.2f, 1.3f, 1.4f);

        TrackPlacementCommand command = StreamingStereoVideoPlayer.ResolveInteractiveReplacementPlacementCommand(
            resolvedPosition,
            resolvedRotation,
            currentScale);

        Assert.That(command.position.x, Is.EqualTo(resolvedPosition.x).Within(0.001f));
        Assert.That(command.position.y, Is.EqualTo(resolvedPosition.y).Within(0.001f));
        Assert.That(command.position.z, Is.EqualTo(resolvedPosition.z).Within(0.001f));
        Assert.That(Quaternion.Angle(command.rotation, resolvedRotation), Is.LessThan(0.001f));
        Assert.That(command.localScale.x, Is.EqualTo(currentScale.x).Within(0.001f));
        Assert.That(command.localScale.y, Is.EqualTo(currentScale.y).Within(0.001f));
        Assert.That(command.localScale.z, Is.EqualTo(currentScale.z).Within(0.001f));
    }

    [Test]
    public void RotationOnlyPlacementCommandKeepsCurrentPositionAndScale()
    {
        Vector3 currentPosition = new Vector3(-1.0f, 0.5f, 2.0f);
        Quaternion resolvedRotation = Quaternion.Euler(0.0f, 90.0f, 0.0f);
        Vector3 currentScale = new Vector3(0.9f, 1.1f, 1.0f);

        TrackPlacementCommand command = StreamingStereoVideoPlayer.ResolveRotationOnlyPlacementCommand(
            currentPosition,
            resolvedRotation,
            currentScale);

        Assert.That(command.position.x, Is.EqualTo(currentPosition.x).Within(0.001f));
        Assert.That(command.position.y, Is.EqualTo(currentPosition.y).Within(0.001f));
        Assert.That(command.position.z, Is.EqualTo(currentPosition.z).Within(0.001f));
        Assert.That(Quaternion.Angle(command.rotation, resolvedRotation), Is.LessThan(0.001f));
        Assert.That(command.localScale.x, Is.EqualTo(currentScale.x).Within(0.001f));
        Assert.That(command.localScale.y, Is.EqualTo(currentScale.y).Within(0.001f));
        Assert.That(command.localScale.z, Is.EqualTo(currentScale.z).Within(0.001f));
    }

    [Test]
    public void PositionOnlyPlacementCommandKeepsCurrentRotationAndScale()
    {
        Vector3 resolvedPosition = new Vector3(2.0f, 3.0f, 4.0f);
        Quaternion currentRotation = Quaternion.Euler(0.0f, 30.0f, 0.0f);
        Vector3 currentScale = new Vector3(1.4f, 1.0f, 0.8f);

        TrackPlacementCommand command = StreamingStereoVideoPlayer.ResolvePositionOnlyPlacementCommand(
            resolvedPosition,
            currentRotation,
            currentScale);

        Assert.That(command.position.x, Is.EqualTo(resolvedPosition.x).Within(0.001f));
        Assert.That(command.position.y, Is.EqualTo(resolvedPosition.y).Within(0.001f));
        Assert.That(command.position.z, Is.EqualTo(resolvedPosition.z).Within(0.001f));
        Assert.That(Quaternion.Angle(command.rotation, currentRotation), Is.LessThan(0.001f));
        Assert.That(command.localScale.x, Is.EqualTo(currentScale.x).Within(0.001f));
        Assert.That(command.localScale.y, Is.EqualTo(currentScale.y).Within(0.001f));
        Assert.That(command.localScale.z, Is.EqualTo(currentScale.z).Within(0.001f));
    }

    [Test]
    public void HumanoidInteractiveInPlaceClipKeepsPinnedRootUpright()
    {
        Quaternion crouchedRotation = Quaternion.LookRotation(
            new Vector3(0.5f, -0.7f, 0.5f).normalized,
            new Vector3(0.0f, 0.7f, 0.7f).normalized);

        Quaternion resolved = StreamingStereoVideoPlayer.ResolveHumanoidInteractiveRootRotation(
            Quaternion.identity,
            crouchedRotation,
            false,
            Vector3.up,
            Vector3.forward);

        Assert.That(Vector3.Dot(resolved * Vector3.up, Vector3.up), Is.GreaterThan(0.999f));
        Assert.That(Mathf.Abs(Vector3.Dot(resolved * Vector3.forward, Vector3.up)), Is.LessThan(0.001f));
    }

    [Test]
    public void HumanoidInteractiveInPlaceClipFacesViewerFromPinnedRoot()
    {
        Quaternion resolved = StreamingStereoVideoPlayer.ResolveHumanoidViewerFacingRotation(
            Vector3.zero,
            Quaternion.identity,
            Vector3.up,
            Vector3.right,
            true,
            Vector3.forward);

        Assert.That(Vector3.Dot(resolved * Vector3.forward, Vector3.right), Is.GreaterThan(0.999f));
        Assert.That(Vector3.Dot(resolved * Vector3.up, Vector3.up), Is.GreaterThan(0.999f));
    }

    [Test]
    public void HumanoidInteractivePlaybackDoesNotRestoreAnimatorLocalPositionWhenAnimatorIsTrackRoot()
    {
        GameObject root = new GameObject("TrackRoot");
        try
        {
            Assert.That(
                StreamingStereoVideoPlayer.ShouldRestoreAnimatorLocalTransformSeparately(root.transform, root.transform),
                Is.False);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(root);
        }
    }

    [Test]
    public void HumanoidInteractivePlaybackRestoresChildAnimatorLocalPositionSeparately()
    {
        GameObject root = new GameObject("TrackRoot");
        GameObject child = new GameObject("AnimatorRoot");
        try
        {
            child.transform.SetParent(root.transform);

            Assert.That(
                StreamingStereoVideoPlayer.ShouldRestoreAnimatorLocalTransformSeparately(child.transform, root.transform),
                Is.True);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(child);
            UnityEngine.Object.DestroyImmediate(root);
        }
    }

    [Test]
    public void HumanoidInteractiveReplacementClipAllowsRootTransformPosition()
    {
        Assert.That(StreamingStereoVideoPlayer.ShouldPreserveHumanoidInteractiveRootPosition(true), Is.False);
    }

    [Test]
    public void AnimalFrameOutLoopMovesForwardThenTurnsBackTowardFrame()
    {
        Vector3 startPosition = Vector3.zero;
        Vector3 frameInPosition = new Vector3(0.2f, 0f, 0f);
        Vector3 frameOutDirection = Vector3.right;
        Quaternion startRotation = Quaternion.LookRotation(Vector3.right, Vector3.up);

        StreamingStereoVideoPlayer.AnimalFrameOutLoopPose outward = StreamingStereoVideoPlayer.ResolveAnimalFrameOutLoopPose(
            startPosition,
            startRotation,
            frameOutDirection,
            frameInPosition,
            true,
            0.25f,
            1.0f,
            1.0f,
            Vector3.up);

        StreamingStereoVideoPlayer.AnimalFrameOutLoopPose returning = StreamingStereoVideoPlayer.ResolveAnimalFrameOutLoopPose(
            startPosition,
            startRotation,
            frameOutDirection,
            frameInPosition,
            true,
            0.9f,
            1.0f,
            1.0f,
            Vector3.up);
        StreamingStereoVideoPlayer.AnimalFrameOutLoopPose completed = StreamingStereoVideoPlayer.ResolveAnimalFrameOutLoopPose(
            startPosition,
            startRotation,
            frameOutDirection,
            frameInPosition,
            true,
            1.0f,
            1.0f,
            1.0f,
            Vector3.up);

        Assert.That(outward.position.x, Is.GreaterThan(0.4f));
        Assert.That(returning.position.x, Is.GreaterThan(0.4f));
        Assert.That(completed.position.x, Is.EqualTo(frameInPosition.x).Within(0.001f));
        Assert.That(Vector3.Dot(outward.rotation * Vector3.forward, Vector3.right), Is.GreaterThan(0.7f));
        Assert.That(Vector3.Dot(returning.rotation * Vector3.forward, Vector3.left), Is.GreaterThan(0.7f));
        Assert.That(Vector3.Dot(completed.rotation * Vector3.forward, Vector3.left), Is.GreaterThan(0.7f));
    }

    [Test]
    public void AnimalFrameOutLoopKeepsMovingOutBeforeLateReturnWindow()
    {
        StreamingStereoVideoPlayer.AnimalFrameOutLoopPose pose = StreamingStereoVideoPlayer.ResolveAnimalFrameOutLoopPose(
            Vector3.zero,
            Quaternion.LookRotation(Vector3.right, Vector3.up),
            Vector3.right,
            Vector3.zero,
            true,
            0.70f,
            1.0f,
            1.0f,
            Vector3.up);

        Assert.That(Vector3.Dot(pose.rotation * Vector3.forward, Vector3.right), Is.GreaterThan(0.7f));
    }

    [Test]
    public void AnimalFrameOutLoopWithoutFrameInKeepsMovingOut()
    {
        StreamingStereoVideoPlayer.AnimalFrameOutLoopPose pose = StreamingStereoVideoPlayer.ResolveAnimalFrameOutLoopPose(
            Vector3.zero,
            Quaternion.LookRotation(Vector3.right, Vector3.up),
            Vector3.right,
            Vector3.zero,
            false,
            1.0f,
            1.0f,
            1.0f,
            Vector3.up);

        Assert.That(pose.position.x, Is.GreaterThan(0.9f));
        Assert.That(Vector3.Dot(pose.rotation * Vector3.forward, Vector3.right), Is.GreaterThan(0.7f));
    }

    [Test]
    public void AnimalFrameOutControlMovesBeyondForwardFrameInBeforeReturning()
    {
        Vector3 control = StreamingStereoVideoPlayer.ResolveAnimalFrameOutControlPoint(
            Vector3.zero,
            new Vector3(2.0f, 0.0f, 0.0f),
            true,
            Vector3.right,
            0.5f,
            Vector3.up);

        Assert.That(control.x, Is.GreaterThan(2.0f));
    }

    [Test]
    public void AnimalFrameOutRotationStartsFromLastTrackedRotation()
    {
        Quaternion startRotation = Quaternion.Euler(0f, 45f, 0f);

        StreamingStereoVideoPlayer.AnimalFrameOutLoopPose pose = StreamingStereoVideoPlayer.ResolveAnimalFrameOutLoopPose(
            Vector3.zero,
            startRotation,
            Vector3.right,
            Vector3.zero,
            false,
            0f,
            1f,
            1f,
            Vector3.up);

        Assert.That(Quaternion.Angle(startRotation, pose.rotation), Is.LessThan(0.001f));
    }

    [Test]
    public void AnimalFrameOutDirectionUsesRecentScreenPlaneMotionBeforeModelForward()
    {
        Vector3 previous = Vector3.zero;
        Vector3 current = Vector3.right * 0.2f;
        Quaternion modelFacingLeft = Quaternion.LookRotation(Vector3.left, Vector3.up);

        Vector3 resolved = StreamingStereoVideoPlayer.ResolveAnimalFrameOutDirection(
            previous,
            current,
            true,
            modelFacingLeft,
            Vector3.up);

        Assert.That(Vector3.Dot(resolved, Vector3.right), Is.GreaterThan(0.99f));
    }

    [Test]
    public void AnimalFrameOutDirectionUsesScreenPixelMotionBeforeWorldMotion()
    {
        Vector3 resolved = StreamingStereoVideoPlayer.ResolveAnimalFrameOutDirectionFromScreenMotion(
            new Vector2(100f, 100f),
            new Vector2(130f, 100f),
            true,
            Vector3.right,
            Vector3.up,
            Vector3.left);

        Assert.That(Vector3.Dot(resolved, Vector3.right), Is.GreaterThan(0.99f));
    }

    [Test]
    public void AnimalFrameOutDirectionMapsImageDownToNegativeScreenUp()
    {
        Vector3 resolved = StreamingStereoVideoPlayer.ResolveAnimalFrameOutDirectionFromScreenMotion(
            new Vector2(100f, 100f),
            new Vector2(100f, 130f),
            true,
            Vector3.right,
            Vector3.up,
            Vector3.left);

        Assert.That(Vector3.Dot(resolved, Vector3.down), Is.GreaterThan(0.99f));
    }

    [Test]
    public void AnimalFrameOutDirectionUsesRightScreenEdgeBeforeMotionFallback()
    {
        Vector3 resolved = StreamingStereoVideoPlayer.ResolveAnimalFrameOutDirectionFromScreenExit(
            new Rect(930f, 200f, 90f, 120f),
            1000f,
            600f,
            Vector3.right,
            Vector3.up,
            Vector3.left);

        Assert.That(Vector3.Dot(resolved, Vector3.right), Is.GreaterThan(0.99f));
    }

    [Test]
    public void AnimalFrameOutDirectionUsesLeftScreenEdgeBeforeMotionFallback()
    {
        Vector3 resolved = StreamingStereoVideoPlayer.ResolveAnimalFrameOutDirectionFromScreenExit(
            new Rect(-20f, 200f, 90f, 120f),
            1000f,
            600f,
            Vector3.right,
            Vector3.up,
            Vector3.right);

        Assert.That(Vector3.Dot(resolved, Vector3.left), Is.GreaterThan(0.99f));
    }

    [Test]
    public void AnimalFrameOutTravelDistanceUsesSpeedAndHiddenDuration()
    {
        float distance = StreamingStereoVideoPlayer.ResolveAnimalFrameOutTravelDistance(3.0f, 0.2f);

        Assert.That(distance, Is.EqualTo(0.6f).Within(0.001f));
    }

    [Test]
    public void AnimalFrameOutWalkAnimationDoesNotAddVerticalBobToRootPosition()
    {
        Vector3 posePosition = new Vector3(1.0f, 2.0f, 3.0f);

        Vector3 resolved = StreamingStereoVideoPlayer.ResolveAnimalFrameOutWalkPosition(posePosition);

        Assert.That(resolved.y, Is.EqualTo(posePosition.y).Within(0.001f));
    }

    [Test]
    public void AnimalFrameOutPosePreservesStartWorldHeight()
    {
        Vector3 startPosition = new Vector3(0.0f, 1.0f, 0.0f);
        Vector3 frameInPosition = new Vector3(1.0f, 4.0f, 0.0f);

        StreamingStereoVideoPlayer.AnimalFrameOutLoopPose pose = StreamingStereoVideoPlayer.ResolveAnimalFrameOutLoopPose(
            startPosition,
            Quaternion.identity,
            Vector3.right,
            frameInPosition,
            true,
            1.0f,
            1.0f,
            1.0f,
            Vector3.up);

        Assert.That(pose.position.y, Is.EqualTo(startPosition.y).Within(0.001f));
    }

    [Test]
    public void AnimalFrameOutHeightPreserverRestoresStartHeight()
    {
        Vector3 resolved = StreamingStereoVideoPlayer.PreserveAnimalFrameOutStartHeight(
            new Vector3(2.0f, 5.0f, 0.0f),
            new Vector3(0.0f, 1.5f, 0.0f),
            Vector3.up);

        Assert.That(resolved.y, Is.EqualTo(1.5f).Within(0.001f));
    }

    [Test]
    public void AnimalFrameOutPreservesLastDisplayedBoundsBottom()
    {
        Vector3 resolved = StreamingStereoVideoPlayer.ResolvePositionPreservingBoundsBottom(
            new Vector3(1.0f, 3.0f, 2.0f),
            4.0f,
            true,
            1.25f);

        Assert.That(resolved.x, Is.EqualTo(1.0f).Within(0.001f));
        Assert.That(resolved.y, Is.EqualTo(0.25f).Within(0.001f));
        Assert.That(resolved.z, Is.EqualTo(2.0f).Within(0.001f));
    }

    [Test]
    public void AnimalFrameOutStartsFromLastDisplayedRootAfterBBoxFit()
    {
        Vector3 preFitRoot = new Vector3(0.0f, 2.0f, 0.0f);
        Vector3 displayedRoot = new Vector3(0.0f, 1.2f, 0.0f);

        Vector3 resolved = StreamingStereoVideoPlayer.ResolveInteractiveFrameOutStartPosition(
            preFitRoot,
            true,
            displayedRoot);

        Assert.That(resolved.y, Is.EqualTo(displayedRoot.y).Within(0.001f));
    }

    [Test]
    public void AnimalRegisteredAimChildUsesPivotDirectionForHeadAndNeck()
    {
        Assert.That(AnimalPoseApplier.ShouldUseAnimalAimChildPivotDirection(true, false), Is.True);
    }

    [Test]
    public void AnimalControlBodyBasisIgnoresWithersAsUpHint()
    {
        AnimalControlWorldData control = new AnimalControlWorldData
        {
            hasRoot = true,
            rootWorld = Vector3.zero,
            hasWithers = true,
            withersWorld = Vector3.forward,
            hasForwardHint = true,
            forwardHintWorld = Vector3.forward * 2.0f,
            hasUpHint = true,
            upHintWorld = Vector3.forward
        };

        bool resolved = AnimalPoseApplier.TryGetAnimalBodyBasisFromControl(
            control,
            out Vector3 forward,
            out Vector3 up,
            out _);

        Assert.That(resolved, Is.True);
        Assert.That(Vector3.Dot(forward, Vector3.forward), Is.GreaterThan(0.99f));
        Assert.That(Vector3.Dot(up, Vector3.up), Is.GreaterThan(0.99f));
    }
}
