using System.Collections.Generic;
using UnityEngine;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    private const int TestModelLockFrames = 30;
    private const float InvalidJointSqrMagnitudeEpsilon = 1e-10f;
    private int lastAutoTrackId = int.MinValue;
    private readonly Dictionary<uint, GameObject> trackInstances = new Dictionary<uint, GameObject>();
    private readonly Dictionary<uint, GameObject> trackPrefabSources = new Dictionary<uint, GameObject>();
    private readonly Dictionary<uint, SortedDictionary<int, float>> manualYawKeyframesByTrack = new Dictionary<uint, SortedDictionary<int, float>>();
    private int selectedManualRotationTrackId = -1;
    private GameObject manualYawGuideRoot;
    private Transform manualYawGuideShaft;
    private Transform manualYawGuideTip;
    private readonly Dictionary<uint, Vector3[]> smoothedJointsByTrack = new Dictionary<uint, Vector3[]>();
    private readonly Dictionary<Transform, Vector3> personRootYawForwardByRoot = new Dictionary<Transform, Vector3>();
    private readonly Dictionary<Animator, HumanoidRigCache> humanoidCaches = new Dictionary<Animator, HumanoidRigCache>();
    private readonly Dictionary<Transform, AnimalRigCache> animalRigCaches = new Dictionary<Transform, AnimalRigCache>();
    private static readonly int[] DogLeftFrontChain = { 7, 8, 12, 16 };
    private static readonly int[] DogRightFrontChain = { 7, 9, 13, 17 };
    private static readonly int[] DogLeftRearChain = { 6, 10, 14, 18 };
    private static readonly int[] DogRightRearChain = { 6, 11, 15, 19 };
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

    private sealed class AnimalRigCache
    {
        public Transform root;
        public Transform neck;
        public Transform head;
        public Transform leftEar;
        public Transform rightEar;
        public Transform spine;
        public Transform tailBase;
        public Transform tailMid;
        public Transform tailTip;
        public Transform leftFrontUpper;
        public Transform leftFrontLower;
        public Transform leftFrontPaw;
        public Transform rightFrontUpper;
        public Transform rightFrontLower;
        public Transform rightFrontPaw;
        public Transform leftRearUpper;
        public Transform leftRearLower;
        public Transform leftRearPaw;
        public Transform rightRearUpper;
        public Transform rightRearLower;
        public Transform rightRearPaw;
        public readonly Dictionary<Transform, Vector3> bindDirLocal = new Dictionary<Transform, Vector3>();
        public readonly Dictionary<Transform, Quaternion> bindRotLocal = new Dictionary<Transform, Quaternion>();
        public readonly Dictionary<Transform, Transform> aimChildByBone = new Dictionary<Transform, Transform>();
        public bool ready;
    }
}

