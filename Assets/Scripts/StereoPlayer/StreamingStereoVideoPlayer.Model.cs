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
    // 実測した boneRatio がこの範囲を外れたらスケールを測り直さない。bbox が画面端で
    // 切れている・検出が破綻しているケースで誤った基準を焼き付けないための保護。
    private const float MinProjectedBoneRatioForScaleRefine = 0.4f;
    private const float MaxProjectedBoneRatioForScaleRefine = 3.0f;
    private readonly Dictionary<uint, SortedDictionary<int, float>> manualYawKeyframesByTrack = new Dictionary<uint, SortedDictionary<int, float>>();
    private int selectedManualRotationTrackId = -1;
    private GameObject manualYawGuideRoot;
    private Transform manualYawGuideShaft;
    private Transform manualYawGuideTip;
    private readonly Dictionary<uint, Vector3[]> smoothedJointsByTrack = new Dictionary<uint, Vector3[]>();
    private readonly Dictionary<Transform, Vector3> personRootYawForwardByRoot = new Dictionary<Transform, Vector3>();
    private readonly Dictionary<string, Vector3> humanoidLimbBendDirectionByKey = new Dictionary<string, Vector3>();
    private readonly Dictionary<Animator, HumanoidRigCache> humanoidCaches = new Dictionary<Animator, HumanoidRigCache>();
    private readonly AnimalPoseApplier animalPoseApplier = new AnimalPoseApplier(AnimalFilterConfig.Default);
}
