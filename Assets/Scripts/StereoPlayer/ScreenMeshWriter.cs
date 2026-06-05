using UnityEngine;

public static class ScreenMeshWriter
{
    public static void ApplySharedMesh(MeshFilter meshFilter, Mesh mesh)
    {
        if (meshFilter == null || mesh == null)
        {
            return;
        }

        if (meshFilter.sharedMesh == null)
        {
            meshFilter.sharedMesh = mesh;
        }
    }

    public static void ApplyRendererEnabled(Renderer renderer, bool enabled)
    {
        if (renderer == null)
        {
            return;
        }

        renderer.enabled = enabled;
    }
}
