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
    public void HumanSmplMotionUsesLowerArmBendRotation()
    {
        Assert.That(StreamingStereoVideoPlayer.ShouldApplyHumanSmplLowerArmBendRotation(), Is.True);
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
    public void HumanSmplMotionLetsIkRemainFinalLimbAuthority()
    {
        Assert.That(StreamingStereoVideoPlayer.ShouldApplyHumanSmplBeforeLimbIk(), Is.True);
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
    public void HumanoidInteractiveInPlaceClipPreservesRootTransformPosition()
    {
        Assert.That(StreamingStereoVideoPlayer.ShouldPreserveHumanoidInteractiveRootPosition(false), Is.True);
    }

    [Test]
    public void HumanoidInteractiveInPlaceClipLocksRootToStartPosition()
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
    public void HumanoidInteractiveReplacementClipAllowsRootTransformPosition()
    {
        Assert.That(StreamingStereoVideoPlayer.ShouldPreserveHumanoidInteractiveRootPosition(true), Is.False);
    }
}
