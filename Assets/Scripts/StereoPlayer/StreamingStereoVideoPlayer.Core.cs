using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.Video;
using UnityEngine.XR;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    public string bundleFileName = "bundle.svb";
    private const string BundleVideoEntryName = "video.mp4";
    private const string BundleManifestEntryName = "manifest.json";
    private const string BundleMetaEntryName = "meta.bin";
    private const string BundleAnimalControlTargetsEntryName = "source/animal_control_targets.json";
    private const string BundleOtherObjectProxiesEntryName = "source/other_object_proxies.json";
    private const string BundleHumanSmplEntryName = "source/human_smpl_from_sam2.json";
    private const string ExtractedVideoFileName = "video.mp4";
    private const string ExtractedManifestFileName = "manifest.json";
    private const string ExtractedMetaFileName = "meta.bin";
    private const string ExtractedAnimalControlTargetsFileName = "animal_control_targets.json";
    private const string ExtractedOtherObjectProxiesFileName = "other_object_proxies.json";
    private const string ExtractedHumanSmplFileName = "human_smpl_from_sam2.json";

    private Transform leftScreen;
    private Transform rightScreen;

    [Header("Screens")]
    public GameObject leftScreenPrefab;
    public GameObject rightScreenPrefab;

    [Header("Placement")]
    public Transform headTransform;
    public float screenDistanceMeters = 2.0f;
    public Vector3 screenOffsetMeters = Vector3.zero;
    public bool fitScreenToFov = false;

    [Header("Model Debug")]
    [FormerlySerializedAs("useMetaFollow")]
    public bool displayModel = true;
    public int[] displayTrackIds = { 0, 1 };
    public GameObject replacePrefab;
    public GameObject track0Prefab;
    public GameObject track1Prefab;
    public GameObject track2Prefab;

    [Header("Bones")]
    public bool enableBoneApply = true;
    public float boneApplyAlpha = 1f;
    public bool enableJointSmoothing = true;
    [Range(0f, 1f)] public float jointSmoothingAlpha = 0.35f;

    [Header("Other Proxy")]
    public bool showOtherProxyBoxes = true;
    public Color otherProxyBoxColor = new Color(1f, 0.78f, 0.18f, 0.32f);

    [Header("Runtime Controls")]
    public bool enableRuntimeControls = true;
    public GameObject runtimeControlsPrefab;

    [Header("Interactive Motion")]
    public bool enableInteractiveMotion = true;
    public AnimationClip[] humanInteractiveClips;
    public float interactiveMotionMinIntervalSeconds = 6f;
    public float interactiveMotionMaxIntervalSeconds = 14f;
    public float interactiveMotionDurationSeconds = 5.5f;
    public float interactiveMotionBlendSeconds = 1.2f;
    public float interactiveApproachDistanceMeters = 0.25f;

    private const float PopoutRangeMeters = 0.35f;
    private const float EpsilonMeters = 0.02f;
    private const float MinDistanceFromHeadMeters = 0.25f;
    private const float BaseHeight = 1f;
    private static readonly bool UseFrameReadySync = false;
    private static readonly bool SelectDisplayTrackFromClick = true;
    private const float DisplayTrackSelectThresholdPixels = 80f;

    private static readonly bool EnableSkeletonScaleCorrection = false;
    private const float SkeletonScaleMin = 0.2f;
    private const float SkeletonScaleMax = 5f;
    private const float SkeletonScaleRelativeMin = 0.75f;
    private const float SkeletonScaleRelativeMax = 1.25f;
    private static readonly bool StabilizePersonRootYaw = true;
    private const float PersonRootYawMaxDegreesPerSecond = 180f;
    private const float Smpl24RootRotateAlpha = 0.85f;
    private const float Smpl24LimbIkAlpha = 0.9f;
    private const float Smpl24SpineAlpha = 0.35f;
    private static readonly bool EnableHumanSmplMotion = true;
    private const float HumanSmplRotationAlpha = 0.65f;
    private static readonly bool HumanSmplFlipY = true;
    private static readonly bool EnableYawDepthDisambiguation = true;
    private const float YawDepthOffsetMeters = 0.045f;
    private const float YawDepthBlend = 1f;

    private static readonly bool EnableAnimalLimbApply = true;
    private static readonly bool StabilizeAnimalRootYaw = true;
    private const float AnimalRootRotateAlpha = 0.6f;
    private const float AnimalRootPitchRollBlend = 0.18f;
    private static readonly Vector3 AnimalModelForwardLocal = new Vector3(0f, 0f, -1f);
    private static readonly Vector3 AnimalModelUpLocal = Vector3.up;
    private static readonly bool DisableAnimalAnimatorController = true;
    private static readonly bool EnableAnimalDistalFreezeOnHighSkip = true;
    private const int AnimalDistalFreezeSkipThreshold = 6;

    private static readonly bool ForceScreensInFrontOfViewCamera = false;
    private static readonly bool ForceStationaryTrackingOrigin = true;
    private static readonly bool AlignModelToBBoxBottom = true;
    private const float ModelBottomExtraOffsetMeters = 0f;
    private static readonly bool BottomAlignVerticalOnly = true;

    private static readonly Vector2 ControlsBarOffsetMeters = Vector2.zero;
    private const float ControlsBarGapMeters = 0.06f;
    private const float ControlsBarForwardOffsetMeters = 0.01f;
    private static readonly Vector2 ControlsBarSizeMeters = new Vector2(0.6f, 0.1f);
    private static readonly bool EnablePauseHotkey = true;
    private const float RuntimeFovxMinDeg = 40f;
    private const float RuntimeFovxMaxDeg = 140f;
    private const float RuntimeFovxDefaultDeg = 90f;
    private const float RuntimeScreenDistanceMinMeters = 0.5f;
    private const float RuntimeScreenDistanceMaxMeters = 3.0f;
    private static readonly Vector2 SettingsPanelSizeMeters = new Vector2(0.78f, 0.5f);
    private static readonly Vector2 SettingsPanelOffsetMeters = Vector2.zero;
    private const float SettingsPanelGapMeters = 0.08f;
    private const float SettingsPanelForwardOffsetMeters = 0.01f;

    private VideoPlayer vp;
    private ManifestData manifest;
    private int lastFrameReadyFrame = -1;
    private string leftTexProp = "_MainTex";
    private string rightTexProp = "_MainTex";
    private Material leftMat;
    private Material rightMat;
    private Mesh quadMesh;
    private bool hasLockedPinholeBasis;
    private Vector3 lockedPinholeOrigin;
    private Quaternion lockedPinholeRotation = Quaternion.identity;
    private readonly List<XRInputSubsystem> xrInputSubsystems = new List<XRInputSubsystem>();
    private bool headPosePrimed;
    private Vector3 lastHeadPos;
    private Quaternion lastHeadRot = Quaternion.identity;
    private bool prevPrimaryButtonPressed;
    private bool useRuntimeFovxOverride;
    private float runtimeFovxDeg;
    private Camera cachedViewCamera;

}

