using NUnit.Framework;
using UnityEngine;

public class AnimalPlacementBoneSelectorTests
{
    [Test]
    public void SelectPrefersSpineBeforeOtherBones()
    {
        GameObject root = new GameObject("Root");
        GameObject tail = new GameObject("Tail");
        GameObject neck = new GameObject("Neck");
        GameObject spine = new GameObject("Spine");
        try
        {
            Transform selected = AnimalPlacementBoneSelector.Select(spine.transform, neck.transform, tail.transform, root.transform);

            Assert.That(selected, Is.EqualTo(spine.transform));
        }
        finally
        {
            Object.DestroyImmediate(root);
            Object.DestroyImmediate(tail);
            Object.DestroyImmediate(neck);
            Object.DestroyImmediate(spine);
        }
    }

    [Test]
    public void SelectFallsBackThroughNeckTailAndRoot()
    {
        GameObject root = new GameObject("Root");
        GameObject tail = new GameObject("Tail");
        GameObject neck = new GameObject("Neck");
        try
        {
            Assert.That(
                AnimalPlacementBoneSelector.Select(null, neck.transform, tail.transform, root.transform),
                Is.EqualTo(neck.transform));

            Assert.That(
                AnimalPlacementBoneSelector.Select(null, null, tail.transform, root.transform),
                Is.EqualTo(tail.transform));

            Assert.That(
                AnimalPlacementBoneSelector.Select(null, null, null, root.transform),
                Is.EqualTo(root.transform));
        }
        finally
        {
            Object.DestroyImmediate(root);
            Object.DestroyImmediate(tail);
            Object.DestroyImmediate(neck);
        }
    }
}
