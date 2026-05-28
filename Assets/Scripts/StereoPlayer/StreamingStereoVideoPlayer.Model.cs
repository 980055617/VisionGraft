using System.Collections.Generic;
using UnityEngine;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    private const int TestModelLockFrames = 30;
    private const float InvalidJointSqrMagnitudeEpsilon = 1e-10f;
    private int lastAutoTrackId = int.MinValue;
    private readonly Dictionary<uint, GameObject> trackInstances = new Dictionary<uint, GameObject>();
    private readonly Dictionary<uint, GameObject> trackPrefabSources = new Dictionary<uint, GameObject>();
    private readonly Dictionary<uint, Vector3> lockedModelLocalScaleByTrack = new Dictionary<uint, Vector3>();
    private readonly Dictionary<uint, SortedDictionary<int, float>> manualYawKeyframesByTrack = new Dictionary<uint, SortedDictionary<int, float>>();
    private int selectedManualRotationTrackId = -1;
    private GameObject manualYawGuideRoot;
    private Transform manualYawGuideShaft;
    private Transform manualYawGuideTip;
    private readonly Dictionary<uint, Vector3[]> smoothedJointsByTrack = new Dictionary<uint, Vector3[]>();
    private readonly Dictionary<Transform, Vector3> personRootYawForwardByRoot = new Dictionary<Transform, Vector3>();
    private readonly Dictionary<Animator, HumanoidRigCache> humanoidCaches = new Dictionary<Animator, HumanoidRigCache>();
    private readonly AnimalPoseApplier animalPoseApplier = new AnimalPoseApplier();
    private static readonly int[] AnimalLeftFrontChain = { 18, 13, 9, 15 };
    private static readonly int[] AnimalRightFrontChain = { 18, 12, 8, 14 };
    private static readonly int[] AnimalLeftRearChain = { 7, 11, 17, 6 };
    private static readonly int[] AnimalRightRearChain = { 7, 10, 16, 5 };
    private struct SkeletonIndices
    {
        public int nose;
        public int leftEye;
        public int rightEye;
        public int leftShoulder;
        public int rightShoulder;
        public int leftElbow;
        public int rightElbow;
        public int leftWrist;
        public int rightWrist;
        public int leftHip;
        public int rightHip;
        public int leftKnee;
        public int rightKnee;
        public int leftAnkle;
        public int rightAnkle;
        public int leftFoot;
        public int rightFoot;
    }

    private static readonly SkeletonIndices MetrabsSmpl24Indices = new SkeletonIndices
    {
        nose = 15,
        leftEye = -1,
        rightEye = -1,
        leftShoulder = 16,
        rightShoulder = 17,
        leftElbow = 18,
        rightElbow = 19,
        leftWrist = 20,
        rightWrist = 21,
        leftHip = 1,
        rightHip = 2,
        leftKnee = 4,
        rightKnee = 5,
        leftAnkle = 7,
        rightAnkle = 8,
        leftFoot = 10,
        rightFoot = 11
    };

    private sealed class HumanoidRigCache
    {
        public readonly Dictionary<HumanBodyBones, Transform> bones = new Dictionary<HumanBodyBones, Transform>();
        public readonly Dictionary<HumanBodyBones, Quaternion> bindRotLocal = new Dictionary<HumanBodyBones, Quaternion>();
        public bool ready;
    }

}

