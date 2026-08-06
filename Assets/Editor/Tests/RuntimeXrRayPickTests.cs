using NUnit.Framework;
using UnityEngine;

public class RuntimeXrRayPickTests
{
    private const float Tolerance = 0.0001f;

    [Test]
    public void ResolvePress_NoPointer_DoesNotPickAndClearsPreviousState()
    {
        RuntimeXrRayPick.PressDecision decision = RuntimeXrRayPick.ResolvePress(false, true, true);

        Assert.That(decision.pick, Is.False);
        Assert.That(decision.previousPressed, Is.False);
    }

    [Test]
    public void ResolvePress_PressedThisFrame_Picks()
    {
        RuntimeXrRayPick.PressDecision decision = RuntimeXrRayPick.ResolvePress(true, true, false);

        Assert.That(decision.pick, Is.True);
        Assert.That(decision.previousPressed, Is.True);
    }

    [Test]
    public void ResolvePress_HeldDown_DoesNotPickAgain()
    {
        RuntimeXrRayPick.PressDecision decision = RuntimeXrRayPick.ResolvePress(true, true, true);

        Assert.That(decision.pick, Is.False);
        Assert.That(decision.previousPressed, Is.True);
    }

    [Test]
    public void ResolvePress_Released_ClearsPreviousState()
    {
        RuntimeXrRayPick.PressDecision decision = RuntimeXrRayPick.ResolvePress(true, false, true);

        Assert.That(decision.pick, Is.False);
        Assert.That(decision.previousPressed, Is.False);
    }

    // head の world 姿勢と tracking ローカル姿勢が一致していれば、tracking 座標がそのまま
    // world 座標になる（rig が原点に置かれている素の構成）。
    [Test]
    public void ResolveWorldRay_IdentityRig_PassesPointerPoseThrough()
    {
        Vector3 headPose = new Vector3(0f, 1.6f, 0f);
        Ray ray = RuntimeXrRayPick.ResolveWorldRay(
            headPose,
            Quaternion.identity,
            headPose,
            Quaternion.identity,
            new Vector3(0.3f, 1.2f, 0.2f),
            Quaternion.identity);

        AssertVector(ray.origin, new Vector3(0.3f, 1.2f, 0.2f));
        AssertVector(ray.direction, Vector3.forward);
    }

    // rig ごと移動・回転している場合。tracking 原点からの相対姿勢を、head の world 姿勢と
    // ローカル姿勢の差分で world へ移す。
    [Test]
    public void ResolveWorldRay_RotatedAndTranslatedRig_MapsPointerIntoWorld()
    {
        Ray ray = RuntimeXrRayPick.ResolveWorldRay(
            new Vector3(5f, 1.6f, 3f),
            Quaternion.Euler(0f, 90f, 0f),
            new Vector3(0f, 1.6f, 0f),
            Quaternion.identity,
            new Vector3(0.3f, 1.2f, 0.2f),
            Quaternion.identity);

        // Y+90 度で (x, y, z) → (z, y, -x)。原点オフセットは (5, 0, 3)。
        AssertVector(ray.origin, new Vector3(5.2f, 1.2f, 2.7f));
        AssertVector(ray.direction, Vector3.right);
    }

    // HMD 側にも回転が入っている場合、差分だけが変換に効く（head の向きを二重に掛けない）。
    [Test]
    public void ResolveWorldRay_HeadTurnedInTrackingSpace_UsesOnlyRigDifference()
    {
        Quaternion headLocalRotation = Quaternion.Euler(0f, 30f, 0f);
        Ray ray = RuntimeXrRayPick.ResolveWorldRay(
            new Vector3(0f, 1.6f, 0f),
            headLocalRotation,
            new Vector3(0f, 1.6f, 0f),
            headLocalRotation,
            new Vector3(0.3f, 1.2f, 0.2f),
            Quaternion.Euler(0f, 45f, 0f));

        AssertVector(ray.origin, new Vector3(0.3f, 1.2f, 0.2f));
        AssertVector(ray.direction, Quaternion.Euler(0f, 45f, 0f) * Vector3.forward);
    }

    private static void AssertVector(Vector3 actual, Vector3 expected)
    {
        Assert.That(actual.x, Is.EqualTo(expected.x).Within(Tolerance));
        Assert.That(actual.y, Is.EqualTo(expected.y).Within(Tolerance));
        Assert.That(actual.z, Is.EqualTo(expected.z).Within(Tolerance));
    }
}
