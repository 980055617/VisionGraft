using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.Video;
using UnityEngine.XR;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    [Header("Bundle")]
    public string bundleFileName = "bundle.svb";
    public string bundleVideoEntryName = "video.mp4";   // zip entry name
    public string bundleManifestEntryName = "manifest.json";
    public string bundleMetaEntryName = "meta.bin";
    public string bundleKeypoints3dEntryName = "source/keypoints3d.json";
    public string bundleOtherObjectProxiesEntryName = "source/other_object_proxies.json";
    public string extractedVideoFileName = "video.mp4"; // extracted file name
    public string extractedManifestFileName = "manifest.json";
    public string extractedMetaFileName = "meta.bin";
    public string extractedKeypoints3dFileName = "keypoints3d.json";
    public string extractedOtherObjectProxiesFileName = "other_object_proxies.json";

    [Header("Screens")]
    private Transform leftScreen;
    private Transform rightScreen;
    public GameObject leftScreenPrefab;
    public GameObject rightScreenPrefab;

    public float markerOffset = 0.02f; // 繧ｹ繧ｯ繝ｪ繝ｼ繝ｳ謇句燕縺ｫ蜃ｺ縺・m)

    [Header("Placement")]
    public Transform headTransform;
    public float screenDistanceMeters = 2.0f;
    public Vector3 screenOffsetMeters = Vector3.zero;
    public bool fitScreenToFov = false;
    [Header("Depth Popout")]
    public float popoutRangeMeters = 0.35f;
    public float epsilonMeters = 0.02f;
    public float minDistanceFromHeadMeters = 0.25f;

    [Header("Test Model")]
    public GameObject testModelPrefab;
    public GameObject replacePrefab;
    [Header("Track Prefab Overrides")]
    public bool useTrackPrefabOverrides = true;
    public GameObject track0Prefab;
    public GameObject track1Prefab;
    public Vector2Int testPixel = new Vector2Int(-1, -1);
    public float testDepthMeters = 0.5f;
    public bool spawnTestModelOnPrepared = false;
    public bool destroyPreviousTestModel = true; // Step2霑ｽ蠕薙ｒ閠・∴繧九↑繧映alse謗ｨ螂ｨ
    public float testModelSizeMeters = 0.05f; // 5cm
    public Vector2 testModelOffsetMeters = new Vector2(0.10f, 0.0f); // screen蜿ｳ縺ｸ10cm
    [Header("Follow (Meta)")]
    public bool useMetaFollow = true;
    public bool useFrameReadySync = false;
    public int followTrackId = -1; // -1 = auto
    public bool followNearestToClick = true;
    public float followSelectThresholdPixels = 80f;

    [Header("Follow (Sine Motion)")]
    public bool enableFollow = true;
    public float followAmplitudePixels = 30f;
    public float followSpeed = 1f;

    [Header("Video Layout")]
    public bool sideBySide = true;
    public float baseHeight = 1f;

    [Header("Bones")]
    public bool enableBoneApply = true;
    public float boneApplyAlpha = 1f;
    public bool enableJointSmoothing = true;
    [Range(0f, 1f)] public float jointSmoothingAlpha = 0.35f;
    [Header("Pose Pipelines")]
    public Vector3 personBoneAxisSign = new Vector3(1f, -1f, 1f);
    public Vector3 animalBoneAxisSign = Vector3.one;
    public bool remapSkeletonDepthToScreenRange = true;
    public bool enableSkeletonScaleCorrection = false;
    public float skeletonScaleMin = 0.2f;
    public float skeletonScaleMax = 5f;
    public float skeletonScaleRelativeMin = 0.75f;
    public float skeletonScaleRelativeMax = 1.25f;
    public bool stabilizePersonRootYaw = true;
    public float personRootYawMaxDegreesPerSecond = 180f;
    [Range(0f, 1f)] public float smpl24RootRotateAlpha = 0.85f;
    [Range(0f, 1f)] public float smpl24LimbIkAlpha = 0.9f;
    [Range(0f, 1f)] public float smpl24SpineAlpha = 0.35f;
    [Header("Bones Depth Assist")]
    public bool enableYawDepthDisambiguation = true;
    public float yawDepthOffsetMeters = 0.045f;
    [Range(0f, 1f)] public float yawDepthBlend = 1f;
    [Header("Animal Bones")]
    public bool enableAnimalLimbApply = false;
    public bool stabilizeAnimalRootYaw = true;
    public float animalRootYawMaxDegreesPerSecond = 180f;
    [Range(0f, 1f)] public float animalRootRotateAlpha = 0.6f;
    public Vector3 animalModelForwardLocal = Vector3.right;
    public Vector3 animalModelUpLocal = Vector3.up;
    [FormerlySerializedAs("enableDogDistalFreezeOnHighSkip")]
    public bool enableAnimalDistalFreezeOnHighSkip = true;
    [FormerlySerializedAs("dogDistalFreezeSkipThreshold")]
    [Range(0, 16)] public int animalDistalFreezeSkipThreshold = 6;

    [Header("Runtime Flags")]
    public bool forceScreensInFrontOfViewCamera = false;
    public bool forceStationaryTrackingOrigin = true;
    public bool alignModelToBBoxBottom = true;
    public float bboxAnchorVToBottom = 0.5f;
    public float modelBottomExtraOffsetMeters = 0f;
    public bool bottomAlignVerticalOnly = true;
    [Header("Other Proxy")]
    public bool showOtherProxyBoxes = true;
    public Color otherProxyBoxColor = new Color(1f, 0.78f, 0.18f, 0.32f);
    [Header("Humanoid Height Fit")]
    public bool enableHeadHeightScaleCorrection = true;
    [Range(0f, 1f)] public float headHeightScaleAlpha = 0.35f;
    public float headHeightScaleMin = 0.75f;
    public float headHeightScaleMax = 1.35f;

    [Header("Runtime Controls")]
    public bool enableRuntimeControls = true;
    public GameObject runtimeControlsPrefab;
    public Vector2 controlsBarOffsetMeters = Vector2.zero;
    public float controlsBarGapMeters = 0.06f;
    public float controlsBarForwardOffsetMeters = 0.01f;
    public Vector2 controlsBarSizeMeters = new Vector2(0.6f, 0.1f);
    public bool enablePauseHotkey = true;

    [Header("Runtime FOVx Tuning")]
    public bool useRuntimeFovxOverride = false;
    public float runtimeFovxDeg = 90f;
    public float runtimeFovxMinDeg = 40f;
    public float runtimeFovxMaxDeg = 140f;
    public float runtimeFovxDefaultDeg = 90f;
    public float runtimeScreenDistanceMinMeters = 0.5f;
    public float runtimeScreenDistanceMaxMeters = 3.0f;
    public Vector2 settingsPanelSizeMeters = new Vector2(0.78f, 0.5f);
    public Vector2 settingsPanelOffsetMeters = Vector2.zero;
    public float settingsPanelGapMeters = 0.08f;
    public float settingsPanelForwardOffsetMeters = 0.01f;

    private VideoPlayer vp;
    private ManifestData manifest;
    private int lastFrameReadyFrame = -1;
    private string leftTexProp = "_MainTex";
    private string rightTexProp = "_MainTex";
    private Material leftMat;
    private Material rightMat;
    private Mesh quadMesh;
    private GameObject spawnedTestModel;
    private Vector2Int pickedPixel;
    private bool hasPickedPixel;
    private Transform pickedScreen;
    private bool hasLockedPinholeBasis;
    private Vector3 lockedPinholeOrigin;
    private Quaternion lockedPinholeRotation = Quaternion.identity;
    private readonly List<XRInputSubsystem> xrInputSubsystems = new List<XRInputSubsystem>();
    private bool headPosePrimed;
    private Vector3 lastHeadPos;
    private Quaternion lastHeadRot = Quaternion.identity;
    private bool prevPrimaryButtonPressed;

    [System.Serializable]
    private class ManifestData
    {
        public int width;
        public int height;
        public int eye_w;
        public int eye_h;
        public int num_frames;
        public float fps;
        public float fovx_deg;
        public float quant_pos_scale;
        public float quant_joint_scale;
        public string joints_space;
        public string joints_source;
        public string camera_axes;
        public string uv_origin;
        public float joints_quant_scale;
        public float fx_norm;
        public float fy_norm;
        public float cx;
        public float cy;
        public float fovy_deg;
        public float fovy;
    }
}

