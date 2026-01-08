using UnityEngine;

public class ReplaceableModel : MonoBehaviour
{
    public Transform anchor;
    public float referenceHeightMeters = 0f;
    public float userScale = 1f;

    public float GetModelHeightMeters()
    {
        if (referenceHeightMeters > 0f)
        {
            return referenceHeightMeters;
        }

        var renderers = GetComponentsInChildren<Renderer>();
        if (renderers == null || renderers.Length == 0)
        {
            return 0f;
        }

        Bounds bounds = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
        {
            bounds.Encapsulate(renderers[i].bounds);
        }

        float lossyY = transform.lossyScale.y;
        if (lossyY > 0f)
        {
            return bounds.size.y / lossyY;
        }

        return bounds.size.y;
    }
}
