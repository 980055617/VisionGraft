using UnityEngine;

public static class RuntimeScreenComponentFactory
{
    public static MeshFilter EnsureMeshFilter(GameObject screen)
    {
        if (screen == null)
        {
            return null;
        }

        MeshFilter filter = screen.GetComponent<MeshFilter>();
        return filter != null ? filter : screen.AddComponent<MeshFilter>();
    }

    public static MeshRenderer EnsureMeshRenderer(GameObject screen)
    {
        if (screen == null)
        {
            return null;
        }

        MeshRenderer renderer = screen.GetComponent<MeshRenderer>();
        return renderer != null ? renderer : screen.AddComponent<MeshRenderer>();
    }

    public static MeshCollider EnsureMeshCollider(GameObject screen)
    {
        if (screen == null)
        {
            return null;
        }

        MeshCollider collider = screen.GetComponent<MeshCollider>();
        return collider != null ? collider : screen.AddComponent<MeshCollider>();
    }
}
