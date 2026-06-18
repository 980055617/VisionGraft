using UnityEngine;

public struct AnimalPoseSettings
{
    public float boneApplyAlpha;
    public bool enableAnimalLimbApply;
    public bool stabilizeAnimalRootYaw;
    public float animalRootRotateAlpha;
    public float animalRootPitchRollBlend;
    public Vector3 animalModelForwardLocal;
    public Vector3 animalModelUpLocal;
    public bool enableSkeletonScaleCorrection;
    public float skeletonScaleMin;
    public float skeletonScaleMax;
    public float skeletonScaleRelativeMin;
    public float skeletonScaleRelativeMax;
    public Quaternion animalSmalCanonicalCorrection;
}
