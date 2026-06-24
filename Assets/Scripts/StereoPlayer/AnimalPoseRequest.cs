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

    // Applied as an additive local-rotation overlay on the resolved canonical bones, after
    // either FK branch below has finished - this is what makes a gesture/walk clip work
    // regardless of whether the frame's pose came from SMAL or animal control targets. See
    // AnimalGesturePosePlayer.ApplyToRigCache.
    public AnimalGesturePose gestureOverlayClip;
    public float gestureOverlayNormalizedTime;
}
