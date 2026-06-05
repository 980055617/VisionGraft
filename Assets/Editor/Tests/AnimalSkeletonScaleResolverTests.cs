using NUnit.Framework;
using UnityEngine;

public class AnimalSkeletonScaleResolverTests
{
    [Test]
    public void TryResolveUniformUsesSkeletonToModelExtentAndUserScale()
    {
        AnimalPoseSettings settings = CreateSettings(0.1f, 10f, 0.5f, 2f);

        bool resolved = AnimalSkeletonScaleResolver.TryResolveUniform(
            skeletonExtent: 4f,
            modelExtent: 2f,
            userScale: 1.5f,
            referenceUniform: 0f,
            settings: settings,
            uniform: out float uniform);

        Assert.That(resolved, Is.True);
        Assert.That(uniform, Is.EqualTo(3f).Within(0.0001f));
    }

    [Test]
    public void TryResolveUniformRejectsInvalidExtents()
    {
        AnimalPoseSettings settings = CreateSettings(0.1f, 10f, 0.5f, 2f);

        Assert.That(AnimalSkeletonScaleResolver.TryResolveUniform(0f, 2f, 1f, 0f, settings, out _), Is.False);
        Assert.That(AnimalSkeletonScaleResolver.TryResolveUniform(2f, 0f, 1f, 0f, settings, out _), Is.False);
    }

    [Test]
    public void ClampUniformAppliesRelativeBoundsWhenReferenceUniformExists()
    {
        AnimalPoseSettings settings = CreateSettings(0.1f, 10f, 0.75f, 1.25f);

        Assert.That(
            AnimalSkeletonScaleResolver.ClampUniform(10f, 2f, settings),
            Is.EqualTo(2.5f).Within(0.0001f));

        Assert.That(
            AnimalSkeletonScaleResolver.ClampUniform(0.1f, 2f, settings),
            Is.EqualTo(1.5f).Within(0.0001f));
    }

    [Test]
    public void ResolveUniformFromScaleAveragesNonZeroBaseAxes()
    {
        float uniform = AnimalSkeletonScaleResolver.ResolveUniformFromScale(
            new Vector3(2f, 6f, 12f),
            new Vector3(1f, 3f, 4f));

        Assert.That(uniform, Is.EqualTo((2f + 2f + 3f) / 3f).Within(0.0001f));
    }

    [Test]
    public void ResolveUniformFromScaleIgnoresZeroBaseAxes()
    {
        float uniform = AnimalSkeletonScaleResolver.ResolveUniformFromScale(
            new Vector3(10f, 6f, 12f),
            new Vector3(0f, 3f, 0f));

        Assert.That(uniform, Is.EqualTo(2f).Within(0.0001f));
    }

    private static AnimalPoseSettings CreateSettings(
        float min,
        float max,
        float relativeMin,
        float relativeMax)
    {
        return new AnimalPoseSettings
        {
            skeletonScaleMin = min,
            skeletonScaleMax = max,
            skeletonScaleRelativeMin = relativeMin,
            skeletonScaleRelativeMax = relativeMax
        };
    }
}
