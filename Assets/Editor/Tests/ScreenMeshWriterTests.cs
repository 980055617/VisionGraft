using NUnit.Framework;
using UnityEngine;

public class ScreenMeshWriterTests
{
    [Test]
    public void ApplySharedMeshSetsMissingMesh()
    {
        GameObject go = new GameObject("ScreenMesh", typeof(MeshFilter));
        Mesh mesh = new Mesh();
        try
        {
            MeshFilter filter = go.GetComponent<MeshFilter>();

            ScreenMeshWriter.ApplySharedMesh(filter, mesh);

            Assert.That(filter.sharedMesh, Is.EqualTo(mesh));
        }
        finally
        {
            Object.DestroyImmediate(mesh);
            Object.DestroyImmediate(go);
        }
    }

    [Test]
    public void ApplyRendererEnabledSetsRendererEnabled()
    {
        GameObject go = new GameObject("ScreenRenderer", typeof(MeshRenderer));
        try
        {
            MeshRenderer renderer = go.GetComponent<MeshRenderer>();
            renderer.enabled = false;

            ScreenMeshWriter.ApplyRendererEnabled(renderer, true);

            Assert.That(renderer.enabled, Is.True);
        }
        finally
        {
            Object.DestroyImmediate(go);
        }
    }
}
