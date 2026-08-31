using System.Collections.Generic;
using UnityEngine;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    private const int TestModelLockFrames = 30;
    private int lastAutoTrackId = int.MinValue;
    private readonly Dictionary<uint, GameObject> trackInstances = new Dictionary<uint, GameObject>();
    private readonly Dictionary<uint, GameObject> trackPrefabSources = new Dictionary<uint, GameObject>();
    private readonly Dictionary<uint, int> selectedModelIndexByTrack = new Dictionary<uint, int>();
    private readonly Dictionary<uint, Vector3> lockedModelLocalScaleByTrack = new Dictionary<uint, Vector3>();
    // RefineLockedScaleFromProjectedBones を通した track。ロックが新しく作られると外れる。
    private readonly HashSet<uint> scaleRefinedByTrack = new HashSet<uint>();
    // RefineDepthFromProjectedBones の補正比率を時間平滑化した値。shot 境界でクリアする。
    private readonly Dictionary<uint, float> smoothedProjectedDepthRatioByTrack = new Dictionary<uint, float>();
    // `disparity = a/Z + b` の b。bundle ごとに一度だけ推定する（shot 境界では変わらない）。
    private bool depthAffineBResolved;
    private float resolvedDepthAffineB;
    private const int DepthAffineSampleCount = 120;
    // 実距離の比がこの範囲を外れたら推定の破綻とみなす（実測では 0.5〜2.0 に収まる）。
    private const float MinMetricDepthRatio = 0.3f;
    // ⑨ が人の深度をどこで測るか。詳細は Core.cs の otherDepthSkeletonReference を参照。
    public enum HumanDepthReferenceMode
    {
        Root = 0,        // instance.transform.position（従来）
        Hips = 1,        // Humanoid の Hips ボーン
        MeshCenter = 2,  // SkinnedMeshRenderer の bounds 中心
        MeshFront = 3,   // bounds のカメラ側の面
    }

    private const float MaxMetricDepthRatio = 3.0f;
    private int metricRatioDiagCount;
    // ⑨ で骨格 track と Else の深度差を平滑化した値。shot 境界でクリアする。
    private readonly Dictionary<uint, float> otherDepthGapByTrack = new Dictionary<uint, float>();
    // 実測した boneRatio がこの範囲を外れたらスケールを測り直さない。bbox が画面端で
    // 切れている・検出が破綻しているケースで誤った基準を焼き付けないための保護。
    private const float MinProjectedBoneRatioForScaleRefine = 0.4f;
    private const float MaxProjectedBoneRatioForScaleRefine = 3.0f;
    private readonly Dictionary<uint, SortedDictionary<int, float>> manualYawKeyframesByTrack = new Dictionary<uint, SortedDictionary<int, float>>();
    // 自動フィットに対する倍率のキーフレーム。既定 1.0。ManualScale.partial.cs 参照。
    private readonly Dictionary<uint, SortedDictionary<int, float>> manualScaleKeyframesByTrack = new Dictionary<uint, SortedDictionary<int, float>>();
    private int selectedManualRotationTrackId = -1;
    private GameObject manualYawGuideRoot;
    private Transform manualYawGuideShaft;
    private Transform manualYawGuideTip;
    private readonly Dictionary<uint, Vector3[]> smoothedJointsByTrack = new Dictionary<uint, Vector3[]>();
    private readonly Dictionary<Transform, Vector3> personRootYawForwardByRoot = new Dictionary<Transform, Vector3>();
    private readonly Dictionary<string, Vector3> humanoidLimbBendDirectionByKey = new Dictionary<string, Vector3>();
    private readonly Dictionary<Animator, HumanoidRigCache> humanoidCaches = new Dictionary<Animator, HumanoidRigCache>();
    private readonly AnimalPoseApplier animalPoseApplier = new AnimalPoseApplier(AnimalFilterConfig.Default);
    // [ANIMALRIG] を 1 度だけ出すためのフラグ（診断用）。
    private bool loggedAnimalRigBoneNames;

    // 測定 B のフラグが実際に効いたことを 1 度だけログに出すためのフラグ。
    private bool loggedSmalBendDisabled;

    // 2 軸版 jointFrameMap が実際に効いたことを 1 度だけログに出すためのフラグ。
    private bool loggedTwoAxisFrameMap;

    // Animal AimAt が実際に効いたことを 1 度だけログに出すためのフラグ。
    private bool loggedAnimalAimAt;

    // 測定 B（2026-08-28）: SMAL の曲げを切って bind pose + globalOrient だけにする。
    // 診断専用。docs/smpl-retargeting.md「測定 B」参照。
    public void SetSmalBendDisabledForDiag(bool disabled)
    {
        disableSmalBendForDiag = disabled;
    }

    // jointFrameMap の 2 軸版を切り替える入口（A/B 用）。
    public void SetTwoAxisJointFrameMap(bool enabled)
    {
        useTwoAxisJointFrameMap = enabled;
    }

    // Animal の keypoint AimAt を切り替える入口（A/B 用）。
    public void SetAnimalKeypointAimAt(bool enabled)
    {
        enableAnimalKeypointAimAt = enabled;
    }
}
