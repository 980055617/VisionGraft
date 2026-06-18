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
            animalModelUpLocal: Vector3.up);

        Assert.That(settings.boneApplyAlpha, Is.EqualTo(0.75f));
        Assert.That(settings.enableAnimalLimbApply, Is.True);
        Assert.That(settings.stabilizeAnimalRootYaw, Is.False);
        Assert.That(settings.animalRootRotateAlpha, Is.EqualTo(0.6f));
        Assert.That(settings.animalRootPitchRollBlend, Is.EqualTo(0.18f));
        Assert.That(settings.animalModelForwardLocal, Is.EqualTo(Vector3.back));
        Assert.That(settings.animalModelUpLocal, Is.EqualTo(Vector3.up));
    }
}
