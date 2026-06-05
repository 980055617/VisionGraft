using UnityEngine;

public static class ScreenColliderWriter
{
    public static void ApplyMeshCollider(MeshCollider collider, Mesh mesh)
    {
        if (collider == null || mesh == null)
        {
            return;
        }

        if (collider.sharedMesh != mesh)
        {
            collider.sharedMesh = mesh;
        }

        collider.convex = false;
        collider.isTrigger = false;
    }

    public static void ApplyColliderEnabled(Collider collider, bool enabled)
    {
        if (collider == null)
        {
            return;
        }

        collider.enabled = enabled;
    }
}
