using NUnit.Framework;
using UnityEngine;

public class DebugTransformWriterTests
{
    [Test]
    public void ApplyWorldPoseAndScaleWritesDebugObjectTransform()
    {
        GameObject debugObject = new GameObject("DebugObject");
        try
        {
            Vector3 position = new Vector3(1f, 2f, 3f);
            Quaternion rotation = Quaternion.Euler(10f, 20f, 30f);
            Vector3 scale = new Vector3(0.5f, 0.25f, 0.75f);

            DebugTransformWriter.ApplyWorldPoseAndScale(debugObject.transform, position, rotation, scale);

            AssertVector(debugObject.transform.position, position);
            Assert.That(Quaternion.Angle(debugObject.transform.rotation, rotation), Is.LessThan(0.001f));
            AssertVector(debugObject.transform.localScale, scale);
        }
        finally
        {
            Object.DestroyImmediate(debugObject);
        }
    }

    [Test]
    public void ApplyLocalTransformWritesDebugChildTransform()
    {
        GameObject debugObject = new GameObject("DebugChild");
        try
        {
            Vector3 position = new Vector3(0f, 0.2f, 0.4f);
            Quaternion rotation = Quaternion.Euler(0f, 45f, 0f);
            Vector3 scale = Vector3.one * 0.1f;

            DebugTransformWriter.ApplyLocalTransform(debugObject.transform, position, rotation, scale);

            AssertVector(debugObject.transform.localPosition, position);
            Assert.That(Quaternion.Angle(debugObject.transform.localRotation, rotation), Is.LessThan(0.001f));
            AssertVector(debugObject.transform.localScale, scale);
        }
        finally
        {
            Object.DestroyImmediate(debugObject);
        }
    }

    private static void AssertVector(Vector3 actual, Vector3 expected)
    {
        Assert.That(actual.x, Is.EqualTo(expected.x).Within(0.0001f));
        Assert.That(actual.y, Is.EqualTo(expected.y).Within(0.0001f));
        Assert.That(actual.z, Is.EqualTo(expected.z).Within(0.0001f));
    }
}
