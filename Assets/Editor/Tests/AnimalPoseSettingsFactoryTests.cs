using NUnit.Framework;
using UnityEngine;

public class AnimalPoseSettingsFactoryTests
{
    [Test]
    public void CreateCopiesAnimalPoseTuningIntoSettings()
    {
        AnimalPoseSettings settings = AnimalPoseSettingsFactory.Create(
            boneApplyAlpha: 0.75f,
            enableAnimalLimbApply: true,
            stabilizeAnimalRootYaw: false,
            animalRootRotateAlpha: 0.6f,
            animalRootPitchRollBlend: 0.18f,
            animalModelForwardLocal: Vector3.back,
            animalModelUpLocal: Vector3.up,
            enableSkeletonScaleCorrection: true,
            skeletonScaleMin: 0.2f,
            skeletonScaleMax: 5f,
            skeletonScaleRelativeMin: 0.75f,
            skeletonScaleRelativeMax: 1.25f,
            animalSmalCanonicalCorrectionEuler: new Vector3(0f, 90f, 90f));

        Assert.That(settings.boneApplyAlpha, Is.EqualTo(0.75f));
        Assert.That(settings.enableAnimalLimbApply, Is.True);
        Assert.That(settings.stabilizeAnimalRootYaw, Is.False);
        Assert.That(settings.animalRootRotateAlpha, Is.EqualTo(0.6f));
        Assert.That(settings.animalRootPitchRollBlend, Is.EqualTo(0.18f));
        Assert.That(settings.animalModelForwardLocal, Is.EqualTo(Vector3.back));
        Assert.That(settings.animalModelUpLocal, Is.EqualTo(Vector3.up));
        Assert.That(settings.enableSkeletonScaleCorrection, Is.True);
        Assert.That(settings.skeletonScaleMin, Is.EqualTo(0.2f));
        Assert.That(settings.skeletonScaleMax, Is.EqualTo(5f));
        Assert.That(settings.skeletonScaleRelativeMin, Is.EqualTo(0.75f));
        Assert.That(settings.skeletonScaleRelativeMax, Is.EqualTo(1.25f));
        Assert.That(settings.animalSmalCanonicalCorrection, Is.EqualTo(Quaternion.Euler(0f, 90f, 90f)));
    }
}
