using NUnit.Framework;
using UnityEngine;
using UnityEngine.Playables;

public class SceneObjectWriterTests
{
    // ── from ScreenMeshWriter ───────────────────────────────────────────

    [Test]
    public void ApplySharedMeshSetsMesh()
    {
        GameObject go = new GameObject("ScreenMesh", typeof(MeshFilter));
        Mesh mesh = new Mesh();
        try
        {
            MeshFilter filter = go.GetComponent<MeshFilter>();

            SceneObjectWriter.ApplySharedMesh(filter, mesh);

            Assert.That(filter.sharedMesh, Is.EqualTo(mesh));
        }
        finally
        {
            Object.DestroyImmediate(mesh);
            Object.DestroyImmediate(go);
        }
    }

    [Test]
    public void ApplyRendererEnabledSetsEnabled()
    {
        GameObject go = new GameObject("ScreenRenderer", typeof(MeshRenderer));
        try
        {
            MeshRenderer renderer = go.GetComponent<MeshRenderer>();
            renderer.enabled = false;

            SceneObjectWriter.ApplyRendererEnabled(renderer, true);

            Assert.That(renderer.enabled, Is.True);
        }
        finally { Object.DestroyImmediate(go); }
    }

    // ── from ScreenMaterialWriter ───────────────────────────────────────

    [Test]
    public void ApplyMaterialIgnoresNullRenderer()
    {
        Assert.DoesNotThrow(() => SceneObjectWriter.ApplyMaterial(null, null));
    }

    [Test]
    public void ApplyTextureIgnoresNullInputs()
    {
        Assert.DoesNotThrow(() => SceneObjectWriter.ApplyTexture(null, "_MainTex", null));
    }

    [Test]
    public void ApplyTextureSetsExistingTextureProperty()
    {
        Shader shader = Shader.Find("Sprites/Default");
        if (shader == null)
        {
            Assert.Inconclusive("Sprites/Default shader is not available in this Unity environment.");
        }

        Material material = new Material(shader);
        Texture2D texture = new Texture2D(1, 1);
        try
        {
            SceneObjectWriter.ApplyTexture(material, "_MainTex", texture);

            Assert.That(material.GetTexture("_MainTex"), Is.EqualTo(texture));
        }
        finally
        {
            Object.DestroyImmediate(texture);
            Object.DestroyImmediate(material);
        }
    }

    // ── from ScreenColliderWriter ───────────────────────────────────────

    [Test]
    public void ApplyMeshColliderConfiguresSharedMeshAndFlags()
    {
        GameObject go = new GameObject("ScreenCollider", typeof(MeshCollider));
        Mesh mesh = new Mesh();
        try
        {
            MeshCollider collider = go.GetComponent<MeshCollider>();
            collider.convex = true;
            collider.isTrigger = true;

            SceneObjectWriter.ApplyMeshCollider(collider, mesh);

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
        Assert.DoesNotThrow(() => SceneObjectWriter.ApplyMeshCollider(null, null));
    }

    [Test]
    public void ApplyColliderEnabledSetsEnabledState()
    {
        GameObject go = new GameObject("Collider", typeof(BoxCollider));
        try
        {
            Collider collider = go.GetComponent<Collider>();

            SceneObjectWriter.ApplyColliderEnabled(collider, false);

            Assert.That(collider.enabled, Is.False);
        }
        finally { Object.DestroyImmediate(go); }
    }

    // ── from GameObjectLifecycleWriter ──────────────────────────────────

    [Test]
    public void ApplyActiveSetsActiveState()
    {
        GameObject go = new GameObject("LifecycleTarget");
        try
        {
            SceneObjectWriter.ApplyActive(go, false);

            Assert.That(go.activeSelf, Is.False);

            SceneObjectWriter.ApplyActive(go, true);

            Assert.That(go.activeSelf, Is.True);
        }
        finally { Object.DestroyImmediate(go); }
    }

    [Test]
    public void DestroyObjectIgnoresNull()
    {
        Assert.DoesNotThrow(() => SceneObjectWriter.DestroyObject(null));
    }

    // ── from PoseAnimatorWriter ─────────────────────────────────────────

    [Test]
    public void ApplyAnimatorEnabledSetsEnabled()
    {
        GameObject go = new GameObject("Animator", typeof(Animator));
        try
        {
            Animator animator = go.GetComponent<Animator>();

            SceneObjectWriter.ApplyAnimatorEnabled(animator, false);

            Assert.That(animator.enabled, Is.False);
        }
        finally { Object.DestroyImmediate(go); }
    }

    [Test]
    public void ApplyRootMotionSetsApplyRootMotion()
    {
        GameObject go = new GameObject("Animator", typeof(Animator));
        try
        {
            Animator animator = go.GetComponent<Animator>();
            animator.applyRootMotion = true;

            SceneObjectWriter.ApplyRootMotion(animator, false);

            Assert.That(animator.applyRootMotion, Is.False);
        }
        finally { Object.DestroyImmediate(go); }
    }

    [Test]
    public void ApplyPlayIgnoresInvalidGraph()
    {
        Assert.DoesNotThrow(() => SceneObjectWriter.ApplyPlay(default(PlayableGraph)));
    }
}
