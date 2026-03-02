using System.Collections.Generic;
using UnityEngine;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    private const int TestModelLockFrames = 30;
    private const int MetaRangeFrameWindow = 60;
    private const float InvalidJointSqrMagnitudeEpsilon = 1e-10f;
    private const int MaxJointInvalidLogsPerFrame = 6;
    private const int MaxFrameApplySummaryLogsPerFrame = 4;
    private const int DiagFrameStart = 196;
    private const int DiagFrameEnd = 212;
    private const int MaxDiagLogsPerFrame = 24;
    private int lastAutoTrackId = int.MinValue;
    private int lastScreenPinholeLogFrame = -1;
    private float lastScreenPinholeSampleLogTime = -1f;
    private readonly Dictionary<uint, GameObject> trackInstances = new Dictionary<uint, GameObject>();
    private readonly Dictionary<uint, GameObject> trackPrefabSources = new Dictionary<uint, GameObject>();
    private readonly Dictionary<uint, SortedDictionary<int, float>> manualYawKeyframesByTrack = new Dictionary<uint, SortedDictionary<int, float>>();
    private int selectedManualRotationTrackId = -1;
    private GameObject manualYawGuideRoot;
    private Transform manualYawGuideShaft;
    private Transform manualYawGuideTip;
    private bool boneStatusLogged;
    private bool skeletonPresent;
    private bool metaRangeLogged;
    private bool boneAppliedLogged;
    private int metaRangeStartFrame = -1;
    private int metaRangeFrameCount;
    private int lastMetaRangeFrame = -1;
    private int metaRangeMinU = int.MaxValue;
    private int metaRangeMaxU = int.MinValue;
    private int metaRangeMinV = int.MaxValue;
    private int metaRangeMaxV = int.MinValue;
    private readonly HashSet<uint> outOfCropLoggedTracks = new HashSet<uint>();
    private readonly Dictionary<uint, Vector3[]> smoothedJointsByTrack = new Dictionary<uint, Vector3[]>();
    private readonly Dictionary<Transform, Vector3> debugAutoAxisByBone = new Dictionary<Transform, Vector3>();
    private readonly Dictionary<Transform, Quaternion> debugAutoRestLocalRotByBone = new Dictionary<Transform, Quaternion>();
    private readonly HashSet<Transform> debugAutoAxisPickLogged = new HashSet<Transform>();
    private readonly HashSet<Transform> dogBonesDumpLoggedRoots = new HashSet<Transform>();
    private readonly HashSet<Transform> dogMappingLoggedRoots = new HashSet<Transform>();
    private readonly HashSet<int> animatorMetaLockLogged = new HashSet<int>();
    private int debugJointContextFrame = -1;
    private uint debugJointContextTrackId = 0u;
    private int debugJointInvalidLogFrame = -1;
    private int debugJointInvalidLogCount = 0;
    private int debugFrameApplySummaryLogFrame = -1;
    private int debugFrameApplySummaryLogCount = 0;
    private int debugDiagLogFrame = -1;
    private int debugDiagLogCount = 0;
    private readonly List<AnimatorCheckSample> pendingAnimatorChecks = new List<AnimatorCheckSample>(8);
    private sealed class DebugDrawTrackState
    {
        public Vector3[] jointsWorld;
        public byte[] jointsVis;
        public float[] jointsCamZ;
        public int jointCount;
        public byte categoryId;
        public bool hasAnchor;
        public Vector3 anchorWorld;
        public bool hasAxisCompare;
        public string axisBoneName;
        public int axisIdxA = -1;
        public int axisIdxB = -1;
        public Vector3 axisBase;
        public Vector3 axisTargetDir;
        public Vector3 axisBoneDir;
        public float axisAngleDeg;
        public int skeletonSkipCount;
    }
    private sealed class Meta2DOverlayItem
    {
        public uint trackId;
        public Rect eyeRect;
        public Vector2 anchor;
        public Rect bbox;
    }
    private sealed class Joints2DOverlayPoint
    {
        public Vector2 pos;
        public Color color;
    }
    private sealed class DebugProcessedJointState
    {
        public int frame = -1;
        public Vector3[] jointsCamProcessed;
        public byte[] jointsVis;
    }
    private sealed class AnimatorCheckSample
    {
        public int frame;
        public uint trackId;
        public string boneName;
        public Transform bone;
        public Vector3 boneBeforeApply;
        public Vector3 boneAfterApply;
        public bool animatorEnabled;
        public AnimatorUpdateMode updateMode;
    }
    private readonly Dictionary<uint, DebugDrawTrackState> debugDrawStateByTrack = new Dictionary<uint, DebugDrawTrackState>();
    private readonly List<Meta2DOverlayItem> meta2DOverlayItems = new List<Meta2DOverlayItem>(64);
    private readonly List<Joints2DOverlayPoint> joints2DOverlayPoints = new List<Joints2DOverlayPoint>(256);
    private readonly Dictionary<uint, DebugProcessedJointState> debugProcessedJointsByTrack = new Dictionary<uint, DebugProcessedJointState>();
    private int lastMeta2DLogFrame = -1;
    private int lastJoints2DLogFrame = -1;
    private GameObject anchorPinholeCube;
    private GameObject anchorScreenCube;
    private readonly Dictionary<Animator, HumanoidRigCache> humanoidCaches = new Dictionary<Animator, HumanoidRigCache>();
    private readonly Dictionary<Transform, AnimalRigCache> animalRigCaches = new Dictionary<Transform, AnimalRigCache>();
    private static readonly int[] CocoEdges = new[]
    {
        0,1, 0,2, 1,3, 2,4, 0,5, 0,6, 5,6, 5,7, 7,9, 6,8, 8,10, 11,12, 11,13, 13,15, 12,14, 14,16, 5,11, 6,12
    };
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

    private static readonly SkeletonIndices Coco17Indices = new SkeletonIndices
    {
        nose = 0,
        leftEye = 1,
        rightEye = 2,
        leftShoulder = 5,
        rightShoulder = 6,
        leftElbow = 7,
        rightElbow = 8,
        leftWrist = 9,
        rightWrist = 10,
        leftHip = 11,
        rightHip = 12,
        leftKnee = 13,
        rightKnee = 14,
        leftAnkle = 15,
        rightAnkle = 16,
        leftFoot = 15,
        rightFoot = 16
    };

    private static readonly SkeletonIndices Blaze33Indices = new SkeletonIndices
    {
        nose = 0,
        leftEye = 2,
        rightEye = 5,
        leftShoulder = 11,
        rightShoulder = 12,
        leftElbow = 13,
        rightElbow = 14,
        leftWrist = 15,
        rightWrist = 16,
        leftHip = 23,
        rightHip = 24,
        leftKnee = 25,
        rightKnee = 26,
        leftAnkle = 27,
        rightAnkle = 28,
        leftFoot = 31,
        rightFoot = 32
    };

    private sealed class HumanoidRigCache
    {
        public readonly Dictionary<HumanBodyBones, Transform> bones = new Dictionary<HumanBodyBones, Transform>();
        public readonly Dictionary<HumanBodyBones, Vector3> bindDirWorld = new Dictionary<HumanBodyBones, Vector3>();
        public readonly Dictionary<HumanBodyBones, Quaternion> bindRotWorld = new Dictionary<HumanBodyBones, Quaternion>();
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

