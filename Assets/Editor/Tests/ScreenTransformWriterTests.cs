using NUnit.Framework;
using UnityEngine;

public class ScreenTransformWriterTests
{
    [Test]
    public void ApplyPoseKeepsExistingScreenScale()
    {
        GameObject screen = new GameObject("Screen");
        try
        {
            screen.transform.localScale = new Vector3(1.2f, 0.8f, 1f);
            Vector3 position = new Vector3(0.1f, 1.5f, 2.0f);
            Quaternion rotation = Quaternion.Euler(0f, 180f, 0f);

            ScreenTransformWriter.ApplyPose(screen.transform, position, rotation);

            AssertVector(screen.transform.position, position);
            Assert.That(Quaternion.Angle(screen.transform.rotation, rotation), Is.LessThan(0.001f));
            AssertVector(screen.transform.localScale, new Vector3(1.2f, 0.8f, 1f));
        }
        finally
        {
            Object.DestroyImmediate(screen);
        }
    }

    [Test]
    public void ApplyLocalScaleKeepsExistingScreenPose()
    {
        GameObject screen = new GameObject("Screen");
        try
        {
            screen.transform.position = Vector3.forward;
            screen.transform.rotation = Quaternion.Euler(0f, 45f, 0f);
            Vector3 scale = new Vector3(2f, 1f, 1f);

            ScreenTransformWriter.ApplyLocalScale(screen.transform, scale);

            AssertVector(screen.transform.position, Vector3.forward);
            Assert.That(Quaternion.Angle(screen.transform.rotation, Quaternion.Euler(0f, 45f, 0f)), Is.LessThan(0.001f));
            AssertVector(screen.transform.localScale, scale);
        }
        finally
        {
            Object.DestroyImmediate(screen);
        }
    }

    [Test]
    public void RotateSelfTurnsAroundLocalAxis()
    {
        GameObject screen = new GameObject("Screen");
        try
        {
            ScreenTransformWriter.RotateSelf(screen.transform, 0f, 180f, 0f);

            Assert.That(Vector3.Dot(screen.transform.forward, Vector3.back), Is.GreaterThan(0.999f));
        }
        finally
        {
            Object.DestroyImmediate(screen);
        }
    }

    private static void AssertVector(Vector3 actual, Vector3 expected)
    {
        Assert.That(actual.x, Is.EqualTo(expected.x).Within(0.0001f));
        Assert.That(actual.y, Is.EqualTo(expected.y).Within(0.0001f));
        Assert.That(actual.z, Is.EqualTo(expected.z).Within(0.0001f));
    }
}
