using UnityEngine;

public class ReplaceableModel : MonoBehaviour
{
    public Transform anchor;
    public float referenceHeightMeters = 0f;
    public float userScale = 1f;
    public Vector3 baseLocalScale;
    public float baseHeightMeters;
    public Vector2 baseBoundsSize;

    private void Awake()
    {
        baseLocalScale = transform.localScale;
        if (referenceHeightMeters > 0f)
        {
            baseHeightMeters = referenceHeightMeters;
            return;
        }

        var renderers = GetComponentsInChildren<Renderer>();
        if (renderers == null || renderers.Length == 0)
        {
            baseHeightMeters = 0f;
            return;
        }

        Bounds bounds = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
        {
            bounds.Encapsulate(renderers[i].bounds);
        }

        Vector3 lossy = transform.lossyScale;
        float lossyY = lossy.y;
        baseHeightMeters = lossyY > 0f ? bounds.size.y / lossyY : bounds.size.y;
        float lossyX = lossy.x;
        float baseW = lossyX > 0f ? bounds.size.x / lossyX : bounds.size.x;
        baseBoundsSize = new Vector2(baseW, baseHeightMeters);
    }

    public float GetModelHeightMeters()
    {
        return baseHeightMeters;
    }
}
