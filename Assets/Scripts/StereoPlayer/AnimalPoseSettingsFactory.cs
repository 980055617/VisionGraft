using UnityEngine;

public static class AnimalPoseSettingsFactory
{
    public static AnimalPoseSettings Create(
        float boneApplyAlpha,
        bool enableAnimalLimbApply,
        bool stabilizeAnimalRootYaw,
        float animalRootRotateAlpha,
        float animalRootPitchRollBlend,
        Vector3 animalModelForwardLocal,
        Vector3 animalModelUpLocal)
    {
        return new AnimalPoseSettings
        {
            boneApplyAlpha = boneApplyAlpha,
            enableAnimalLimbApply = enableAnimalLimbApply,
            stabilizeAnimalRootYaw = stabilizeAnimalRootYaw,
            animalRootRotateAlpha = animalRootRotateAlpha,
            animalRootPitchRollBlend = animalRootPitchRollBlend,
            animalModelForwardLocal = animalModelForwardLocal,
            animalModelUpLocal = animalModelUpLocal
        };
    }
}
