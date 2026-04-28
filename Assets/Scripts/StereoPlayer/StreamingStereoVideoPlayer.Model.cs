using System.Collections.Generic;
using UnityEngine;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    private const int TestModelLockFrames = 30;
    private const float InvalidJointSqrMagnitudeEpsilon = 1e-10f;
    private const float AnimalRootOneEuroMinCutoffHz = 1.0f;
    private const float AnimalRootOneEuroBeta = 0.15f;
    private const float AnimalRootOneEuroDerivativeCutoffHz = 1.0f;
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
    private readonly Dictionary<Transform, Vector3> animalRootYawForwardByRoot = new Dictionary<Transform, Vector3>();
    private readonly Dictionary<Transform, int> animalRootYawLastSeenFrameByRoot = new Dictionary<Transform, int>();
    private readonly Dictionary<Transform, OneEuroVector3Filter> animalRootForwardFilters = new Dictionary<Transform, OneEuroVector3Filter>();
    private readonly Dictionary<Transform, OneEuroVector3Filter> animalRootUpFilters = new Dictionary<Transform, OneEuroVector3Filter>();
    private readonly Dictionary<Animator, HumanoidRigCache> humanoidCaches = new Dictionary<Animator, HumanoidRigCache>();
    private readonly Dictionary<Transform, AnimalRigCache> animalRigCaches = new Dictionary<Transform, AnimalRigCache>();
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

    private sealed class AnimalRigCache
    {
        public Transform root;
        public Transform neck;
        public Transform head;
        public Transform spine;
        public Transform tailBase;
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

    private sealed class LowPassFilter1D
    {
        private bool initialized;
        private float previousValue;

        public float Filter(float value, float alpha)
        {
            if (!initialized)
            {
                initialized = true;
                previousValue = value;
                return value;
            }

            previousValue = alpha * value + (1f - alpha) * previousValue;
            return previousValue;
        }

        public void Reset(float value)
        {
            initialized = true;
            previousValue = value;
        }
    }

    private sealed class OneEuroFilter1D
    {
        private readonly LowPassFilter1D valueFilter = new LowPassFilter1D();
        private readonly LowPassFilter1D derivativeFilter = new LowPassFilter1D();
        private readonly float minCutoff;
        private readonly float beta;
        private readonly float derivativeCutoff;
        private bool initialized;
        private float previousRawValue;

        public OneEuroFilter1D(float minCutoffHz, float betaValue, float derivativeCutoffHz)
        {
            minCutoff = Mathf.Max(0.0001f, minCutoffHz);
            beta = Mathf.Max(0f, betaValue);
            derivativeCutoff = Mathf.Max(0.0001f, derivativeCutoffHz);
        }

        public float Filter(float value, float deltaTime)
        {
            float dt = Mathf.Max(0.0001f, deltaTime);
            if (!initialized)
            {
                initialized = true;
                previousRawValue = value;
                valueFilter.Reset(value);
                derivativeFilter.Reset(0f);
                return value;
            }

            float derivative = (value - previousRawValue) / dt;
            previousRawValue = value;
            float filteredDerivative = derivativeFilter.Filter(derivative, ComputeAlpha(derivativeCutoff, dt));
            float cutoff = minCutoff + beta * Mathf.Abs(filteredDerivative);
            return valueFilter.Filter(value, ComputeAlpha(cutoff, dt));
        }

        public void Reset(float value)
        {
            initialized = true;
            previousRawValue = value;
            valueFilter.Reset(value);
            derivativeFilter.Reset(0f);
        }

        private static float ComputeAlpha(float cutoff, float deltaTime)
        {
            float tau = 1f / (2f * Mathf.PI * Mathf.Max(0.0001f, cutoff));
            return 1f / (1f + tau / Mathf.Max(0.0001f, deltaTime));
        }
    }

    private sealed class OneEuroVector3Filter
    {
        private readonly OneEuroFilter1D x;
        private readonly OneEuroFilter1D y;
        private readonly OneEuroFilter1D z;

        public OneEuroVector3Filter(float minCutoffHz, float betaValue, float derivativeCutoffHz)
        {
            x = new OneEuroFilter1D(minCutoffHz, betaValue, derivativeCutoffHz);
            y = new OneEuroFilter1D(minCutoffHz, betaValue, derivativeCutoffHz);
            z = new OneEuroFilter1D(minCutoffHz, betaValue, derivativeCutoffHz);
        }

        public Vector3 Filter(Vector3 value, float deltaTime)
        {
            return new Vector3(
                x.Filter(value.x, deltaTime),
                y.Filter(value.y, deltaTime),
                z.Filter(value.z, deltaTime));
        }

        public void Reset(Vector3 value)
        {
            x.Reset(value.x);
            y.Reset(value.y);
            z.Reset(value.z);
        }
    }
}

