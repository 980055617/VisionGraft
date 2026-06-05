using UnityEngine;

public static class AnimalPlacementBoneSelector
{
    public static Transform Select(Transform spine, Transform neck, Transform tailBase, Transform root)
    {
        return spine ?? neck ?? tailBase ?? root;
    }
}
