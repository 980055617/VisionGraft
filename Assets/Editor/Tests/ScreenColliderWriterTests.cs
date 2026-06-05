using NUnit.Framework;
using UnityEngine;

public class ScreenColliderWriterTests
{
    [Test]
    public void ApplyMeshColliderConfiguresSharedMeshAndTriggerFlags()
    {
        GameObject go = new GameObject("ScreenCollider", typeof(MeshCollider));
        Mesh mesh = new Mesh();
        try
        {
            MeshCollider collider = go.GetComponent<MeshCollider>();
            collider.convex = true;
            collider.isTrigger = true;

            ScreenColliderWriter.ApplyMeshCollider(collider, mesh);

            Assert.That(collider.sharedMesh, Is.EqualTo(mesh));
            Assert.That(collider.convex, Is.False);
            Assert.That(collider.isTrigger, Is.False);
        }
        finally
        {
            Object.DestroyImmediate(mesh);
            Object.DestroyImmediate(go);
        }
    }

    [Test]
    public void ApplyMeshColliderIgnoresNullInputs()
    {
        Assert.DoesNotThrow(() => ScreenColliderWriter.ApplyMeshCollider(null, null));
    }

    [Test]
    public void ApplyColliderEnabledSetsEnabledState()
    {
        GameObject go = new GameObject("Collider", typeof(BoxCollider));
        try
        {
            Collider collider = go.GetComponent<Collider>();

            ScreenColliderWriter.ApplyColliderEnabled(collider, false);

            Assert.That(collider.enabled, Is.False);
        }
        finally
        {
            Object.DestroyImmediate(go);
        }
    }
}
