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
    // 身ぶりを左右を入れ替えて足す（StreamingStereoVideoPlayer.animalGestureTurnTowardBodyFront、2026-10-18、既定 OFF。イベントごとに BeginGesturePhase が決める）。
    public bool gestureMirror;
    // SMAL の FK の入力を既定の姿勢へ混ぜる重み（0〜1、StreamingStereoVideoPlayer.animalEventFromDefaultPose、2026-10-07）。追従は 0。
    public float defaultPoseWeight;
    // S（2026-10-16）: root の位置の平滑の beta に掛ける倍率（1/k）。0 以下は 1（インタラクティブモーションの経路は渡さない）。
    public float rootFilterBetaScale;
}
