using UnityEngine;

public static class AnimalSkeletonScaleResolver
{
    public static bool TryResolveUniform(
        float skeletonExtent,
        float modelExtent,
        float userScale,
        float referenceUniform,
        AnimalPoseSettings settings,
        out float uniform)
    {
        uniform = 0f;
        if (skeletonExtent <= 0.0001f || modelExtent <= 0.0001f)
        {
            return false;
        }

        uniform = ClampUniform((skeletonExtent / modelExtent) * userScale, referenceUniform, settings);
        return true;
    }

    public static float ClampUniform(float uniform, float referenceUniform, AnimalPoseSettings settings)
    {
        float min = Mathf.Max(0.0001f, settings.skeletonScaleMin);
        float max = Mathf.Max(min, settings.skeletonScaleMax);
        if (referenceUniform > 0.0001f)
        {
            float relMin = Mathf.Max(0.0001f, settings.skeletonScaleRelativeMin);
            float relMax = Mathf.Max(relMin, settings.skeletonScaleRelativeMax);
            min = Mathf.Max(min, referenceUniform * relMin);
            max = Mathf.Min(max, referenceUniform * relMax);
        }

        return Mathf.Clamp(uniform, min, max);
    }

    public static float ResolveUniformFromScale(Vector3 localScale, Vector3 baseLocalScale)
    {
        float sum = 0f;
        int count = 0;
        if (Mathf.Abs(baseLocalScale.x) > 0.0001f)
        {
            sum += Mathf.Abs(localScale.x / baseLocalScale.x);
            count++;
        }
        if (Mathf.Abs(baseLocalScale.y) > 0.0001f)
        {
            sum += Mathf.Abs(localScale.y / baseLocalScale.y);
            count++;
        }
        if (Mathf.Abs(baseLocalScale.z) > 0.0001f)
        {
            sum += Mathf.Abs(localScale.z / baseLocalScale.z);
            count++;
        }

        return count > 0 ? sum / count : 0f;
    }
}
