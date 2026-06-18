using UnityEngine;

public struct AnimalPoseRequest
{
    public Transform instanceRoot;
    public Animator animator;
    public AnimalPoseWorldData pose;
    public AnimalPoseSettings settings;
    public RuntimeClock.TickContext tickContext;
    public bool freezeAnimalDistal;
    public bool enableBoneApply;
    public bool hasSmalPose;
    public AnimalSmalPose smalPose;
}
