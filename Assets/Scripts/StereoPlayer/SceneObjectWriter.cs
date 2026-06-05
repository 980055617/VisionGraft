using UnityEngine;
using UnityEngine.Playables;

public static class SceneObjectWriter
{
    // ── Mesh ────────────────────────────────────────────────────────────

    public static void ApplySharedMesh(MeshFilter meshFilter, Mesh mesh)
    {
        if (meshFilter == null || mesh == null) return;
        if (meshFilter.sharedMesh == null)
        {
            meshFilter.sharedMesh = mesh;
        }
    }

    public static void ApplyRendererEnabled(Renderer renderer, bool enabled)
    {
        if (renderer == null) return;
        renderer.enabled = enabled;
    }

    // ── Material ────────────────────────────────────────────────────────

    public static void ApplyMaterial(Renderer renderer, Material material)
    {
        if (renderer == null) return;
        renderer.material = material;
    }

    public static void ApplyTexture(Material material, string textureProperty, Texture texture)
    {
        if (material == null || texture == null || string.IsNullOrEmpty(textureProperty)) return;
        if (!material.HasProperty(textureProperty)) return;
        material.SetTexture(textureProperty, texture);
    }

    // ── Collider ────────────────────────────────────────────────────────

    public static void ApplyMeshCollider(MeshCollider collider, Mesh mesh)
    {
        if (collider == null || mesh == null) return;
        if (collider.sharedMesh != mesh)
        {
            collider.sharedMesh = mesh;
        }
        collider.convex = false;
        collider.isTrigger = false;
    }

    public static void ApplyColliderEnabled(Collider collider, bool enabled)
    {
        if (collider == null) return;
        collider.enabled = enabled;
    }

    // ── GameObject lifecycle ─────────────────────────────────────────────

    public static void ApplyActive(GameObject target, bool active)
    {
        if (target == null) return;
        target.SetActive(active);
    }

    public static void DestroyObject(Object target)
    {
        if (target == null) return;
        if (Application.isPlaying)
        {
            Object.Destroy(target);
        }
        else
        {
            Object.DestroyImmediate(target);
        }
    }

    // ── Animator ────────────────────────────────────────────────────────

    public static void ApplyAnimatorEnabled(Animator animator, bool enabled)
    {
        if (animator == null) return;
        animator.enabled = enabled;
    }

    public static void ApplyRootMotion(Animator animator, bool applyRootMotion)
    {
        if (animator == null) return;
        animator.applyRootMotion = applyRootMotion;
    }

    public static void ApplyPlay(PlayableGraph graph)
    {
        if (!graph.IsValid()) return;
        graph.Play();
    }
}
