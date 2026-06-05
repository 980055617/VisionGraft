using NUnit.Framework;
using UnityEngine;

public class AnimalControlPoseMapperTests
{
    [Test]
    public void ToWorldDataTransformsTargetsAndChainsWithCameraBasis()
    {
        AnimalControlPose pose = new AnimalControlPose
        {
            rootCamAbs = new Vector3(1f, 2f, 3f),
            hasWithersCamAbs = true,
            withersCamAbs = new Vector3(2f, 2f, 3f),
            hasForwardHintCamAbs = true,
            forwardHintCamAbs = new Vector3(1f, 2f, 4f),
            frontLeftLegChainCamAbs = new[]
            {
                new Vector3(0f, 0f, 0f),
                new Vector3(0f, 1f, 0f)
            }
        };
        Vector3 origin = new Vector3(10f, 20f, 30f);
        Quaternion rotation = Quaternion.Euler(0f, 90f, 0f);
        Vector3 axisSign = new Vector3(1f, -1f, 1f);

        AnimalControlWorldData world = AnimalControlPoseMapper.ToWorldData(pose, origin, rotation, axisSign);

        AssertVector(world.rootWorld, new Vector3(13f, 18f, 29f));
        Assert.That(world.hasWithers, Is.True);
        AssertVector(world.withersWorld, new Vector3(13f, 18f, 28f));
        Assert.That(world.hasForwardHint, Is.True);
        AssertVector(world.forwardHintWorld, new Vector3(14f, 18f, 29f));
        Assert.That(world.frontLeftLegWorld, Has.Length.EqualTo(2));
        AssertVector(world.frontLeftLegWorld[0], origin);
        AssertVector(world.frontLeftLegWorld[1], new Vector3(10f, 19f, 30f));
    }

    private static void AssertVector(Vector3 actual, Vector3 expected)
    {
        Assert.That(actual.x, Is.EqualTo(expected.x).Within(0.001f));
        Assert.That(actual.y, Is.EqualTo(expected.y).Within(0.001f));
        Assert.That(actual.z, Is.EqualTo(expected.z).Within(0.001f));
    }
}
