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
    // 四肢のジェスチャを正規名の骨に乗せる（StreamingStereoVideoPlayer.animalGestureOnCanonicalLimbs、2026-10-04）。
    public bool gestureOnCanonicalLimbs;
    // 頭のジェスチャを解剖学的な軸で回す（StreamingStereoVideoPlayer.animalGestureAnatomicalHeadAxes、2026-10-05）。
    public bool gestureAnatomicalHeadAxes;
    // SMAL の FK の入力を既定の姿勢へ混ぜる重み（0〜1、StreamingStereoVideoPlayer.animalEventFromDefaultPose、2026-10-07）。追従は 0。
    public float defaultPoseWeight;
}
