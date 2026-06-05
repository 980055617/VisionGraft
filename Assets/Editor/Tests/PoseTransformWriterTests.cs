using NUnit.Framework;
using UnityEngine;

public class PoseTransformWriterTests
{
    [Test]
    public void ApplyWorldRotationKeepsPositionAndScale()
    {
        GameObject bone = new GameObject("Bone");
        try
        {
            bone.transform.position = new Vector3(1f, 2f, 3f);
            bone.transform.localScale = Vector3.one * 2f;
            Quaternion rotation = Quaternion.Euler(20f, 30f, 40f);

            PoseTransformWriter.ApplyWorldRotation(bone.transform, rotation);

            AssertVector(bone.transform.position, new Vector3(1f, 2f, 3f));
            Assert.That(Quaternion.Angle(bone.transform.rotation, rotation), Is.LessThan(0.001f));
            AssertVector(bone.transform.localScale, Vector3.one * 2f);
        }
        finally
        {
            Object.DestroyImmediate(bone);
        }
    }

    [Test]
    public void ApplyLocalPoseWritesLocalPositionAndRotation()
    {
        GameObject bone = new GameObject("Bone");
        try
        {
            Vector3 localPosition = new Vector3(0.1f, 0.2f, 0.3f);
            Quaternion localRotation = Quaternion.Euler(0f, 90f, 0f);

            PoseTransformWriter.ApplyLocalPose(bone.transform, localPosition, localRotation);

            AssertVector(bone.transform.localPosition, localPosition);
            Assert.That(Quaternion.Angle(bone.transform.localRotation, localRotation), Is.LessThan(0.001f));
        }
        finally
        {
            Object.DestroyImmediate(bone);
        }
    }

    private static void AssertVector(Vector3 actual, Vector3 expected)
    {
        Assert.That(actual.x, Is.EqualTo(expected.x).Within(0.0001f));
        Assert.That(actual.y, Is.EqualTo(expected.y).Within(0.0001f));
        Assert.That(actual.z, Is.EqualTo(expected.z).Within(0.0001f));
    }
}
