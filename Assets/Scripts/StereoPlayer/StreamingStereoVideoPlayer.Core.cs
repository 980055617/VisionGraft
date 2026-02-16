using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Video;
using UnityEngine.XR;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    [Header("Bundle")]
    public string bundleFileName = "bundle.svb";
    public string bundleVideoEntryName = "video.mp4";   // zip entry name
    public string bundleManifestEntryName = "manifest.json";
    public string bundleMetaEntryName = "meta.bin";
    public bool reExtractAlways = false;                // true: always re-extract
    public string extractedVideoFileName = "video.mp4"; // extracted file name
    public string extractedManifestFileName = "manifest.json";
    public string extractedMetaFileName = "meta.bin";

    [Header("Screens")]
    public Transform leftScreen;
    public Transform rightScreen;

    [Header("Debug Marker")]
    public GameObject debugMarkerPrefab;
    public float debugMarkerScale = 0.03f;
    public Vector2Int debugPixel = new Vector2Int(-1, -1); // (-1,-1)なら中心
    public float markerOffset = 0.02f; // スクリーン手前に出す(m)
    public bool spawnMarkerOnPrepared = false;

    [Header("Placement")]
    public Transform headTransform;
    public float screenDistanceMeters = 2.0f;
    public Vector3 screenOffsetMeters = Vector3.zero;
    public bool fitScreenToFov = false;

    [Header("Test Model")]
    public GameObject testModelPrefab;
    public GameObject replacePrefab;
    public Vector2Int testPixel = new Vector2Int(-1, -1);
    public float testDepthMeters = 0.5f;
    public bool spawnTestModelOnPrepared = false;
    public bool destroyPreviousTestModel = true; // Step2追従を考えるならfalse推奨
    public float testModelSizeMeters = 0.05f; // 5cm
    public Vector2 testModelOffsetMeters = new Vector2(0.10f, 0.0f); // screen右へ10cm
    [Header("Follow (Meta)")]
    public bool useMetaFollow = true;
    public int followTrackId = -1; // -1 = auto
    public bool followNearestToClick = true;
    public float followSelectThresholdPixels = 80f;

    [Header("Follow (Debug Sin)")]
    public bool enableFollow = true;
    public float followAmplitudePixels = 30f;
    public float followSpeed = 1f;

    [Header("Video Layout")]
    public bool sideBySide = true;
    public float baseHeight = 1f;

    [Header("Bones")]
    public bool enableBoneApply = true;
    public float boneApplyAlpha = 1f;
    public float boneRootRelThreshold = 0.2f;
    public Vector3 boneAxisSign = Vector3.one;
    public float fallbackQuantJointScale = 1f;
    public bool alignFeetToAnkles = true;
    public float footAlignAlpha = 1f;

    [Header("Debug")]
    public bool forceScreensInFrontOfViewCamera = false;
    [SerializeField] private bool verboseLog = true;
    public bool logGeneral = true;
    public bool logBundle = true;
    public bool logMeta = true;
    public bool logPicking = true;
    public bool logFollow = true;
    public bool logScreens = true;
    public bool logVideo = true;
    public bool logModel = true;
    public bool showAnchorDebugCubes = false;
    public float anchorDebugCubeSize = 0.03f;
    public bool anchorDebugAlignBottom = true;
    public bool alignModelToBBoxBottom = true;
    public float bboxAnchorVToBottom = 0.5f;

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
    public Vector2 settingsPanelSizeMeters = new Vector2(0.42f, 0.26f);
    public Vector2 settingsPanelOffsetMeters = Vector2.zero;
    public float settingsPanelGapMeters = 0.08f;
    public float settingsPanelForwardOffsetMeters = 0.01f;

    private VideoPlayer vp;
    private ManifestData manifest;
    private bool loggedFirstFrame;
    private bool fallbackApplied;
    private bool loggedFovSource;
    private bool loggedQuantSource;
    private bool loggedManifestResolved;
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

    private void Awake()
    {
        LogGeneral("StreamingStereoVideoPlayer Awake");
    }

    private void OnEnable()
    {
        LogGeneral("StreamingStereoVideoPlayer OnEnable");
        SubscribeRecenterEvents();
    }

    private void OnDisable()
    {
        UnsubscribeRecenterEvents();
    }

    private void OnDestroy()
    {
        UnsubscribeRecenterEvents();
    }

    private IEnumerator Start()
    {
        LogGeneral("StreamingStereoVideoPlayer Start");
        LogActiveCameras();
        LogGeneral($"Screen refs at Start: leftScreen={(leftScreen != null ? leftScreen.name : "null")} rightScreen={(rightScreen != null ? rightScreen.name : "null")}");
        if (leftScreen == null || rightScreen == null)
        {
            Debug.LogWarning("One or more screen references are null at Start.");
        }

        vp = GetComponent<VideoPlayer>();
        if (vp == null)
        {
            Debug.LogError("VideoPlayer component not found on this GameObject.");
            yield break;
        }

        vp.source = VideoSource.Url;
        vp.isLooping = true;
        vp.renderMode = VideoRenderMode.APIOnly;
        vp.sendFrameReadyEvents = true;
        loggedFirstFrame = false;
        vp.errorReceived += (player, msg) => Debug.LogError($"VideoError: {msg}");
        vp.frameReady += (player, frame) =>
        {
            if (!loggedFirstFrame && frame >= 0)
            {
                loggedFirstFrame = true;
                LogVideo($"FirstFrameReady: {frame}");
            }
            ApplyVideoFrameTexture(player);
        };

        vp.prepareCompleted += OnPrepared;

        yield return EnsureBundleAndPrepareVideo();
    }

    private void OnPrepared(VideoPlayer source)
    {
        float w = source.width;
        float h = source.height;

        EnsureScreensExist();
        SetupScreensAndMaterials();
        LogStereoSetup("OnPrepared");
        LogVideoPlayerState("OnPrepared(start)");

        if (w <= 0 || h <= 0)
        {
            Debug.LogWarning($"Video size is invalid: {w}x{h}");
            vp.Play();
            LogVideoPlayerState("OnPrepared(after Play invalid)");
            return;
        }

        float perEyeWidth = sideBySide ? w * 0.5f : w;
        float aspect = perEyeWidth / h;
        Vector3 screenScale = new Vector3(aspect * baseHeight, baseHeight, 1f);

        if (leftScreen != null)
        {
            leftScreen.localScale = screenScale;
        }

        if (rightScreen != null)
        {
            rightScreen.localScale = screenScale;
        }

        PlaceScreens();
        EnsureRuntimeControls();
        DumpScreenState("after PlaceScreens");
        LogVideoPlayerState("OnPrepared");

        if (spawnMarkerOnPrepared)
        {
            TrySpawnDebugMarker();
        }

        if (spawnTestModelOnPrepared)
        {
            TrySpawnTestModel();
        }

        vp.Play();
        UpdatePauseButtonLabel();
        LogVideoPlayerState("after Play");
        vp.prepareCompleted -= OnPrepared;
    }

    private void LateUpdate()
    {
        if (!forceScreensInFrontOfViewCamera)
        {
            return;
        }

        Camera cam = GetViewCamera();
        if (cam == null)
        {
            return;
        }

        Vector3 camPos = cam.transform.position;
        Vector3 camFwd = cam.transform.forward;
        Vector3 screenPos = camPos + camFwd * screenDistanceMeters;
        Quaternion screenRot = Quaternion.LookRotation((camPos - screenPos).normalized, Vector3.up);

        if (leftScreen != null)
        {
            leftScreen.position = screenPos;
            leftScreen.rotation = screenRot;
        }

        if (rightScreen != null)
        {
            rightScreen.position = screenPos;
            rightScreen.rotation = screenRot;
        }
    }

    private void Update()
    {
        if (TryPick(out PickResult pick))
        {
            pickedPixel = pick.pixel;
            hasPickedPixel = true;
            pickedScreen = pick.screen;
            TrySelectFollowTrackFromPick(pick);
            PlaceOrMoveTestModel(pick);
        }

        FollowTick();
        DetectRuntimeRecenterFallback();
        HandleRuntimePauseInput();
    }

    private void SubscribeRecenterEvents()
    {
        UnsubscribeRecenterEvents();
        SubsystemManager.GetSubsystems(xrInputSubsystems);
        for (int i = 0; i < xrInputSubsystems.Count; i++)
        {
            XRInputSubsystem xr = xrInputSubsystems[i];
            if (xr == null)
            {
                continue;
            }

            xr.trackingOriginUpdated += OnTrackingOriginUpdated;
        }
    }

    private void UnsubscribeRecenterEvents()
    {
        for (int i = 0; i < xrInputSubsystems.Count; i++)
        {
            XRInputSubsystem xr = xrInputSubsystems[i];
            if (xr == null)
            {
                continue;
            }

            xr.trackingOriginUpdated -= OnTrackingOriginUpdated;
        }

        xrInputSubsystems.Clear();
    }

    private void OnTrackingOriginUpdated(XRInputSubsystem subsystem)
    {
        RecenterScreensToCurrentFacing();
    }

    private void DetectRuntimeRecenterFallback()
    {
        if (forceScreensInFrontOfViewCamera)
        {
            headPosePrimed = false;
            return;
        }

        Transform head = GetViewCamera() != null ? GetViewCamera().transform : GetHeadTransform();
        if (head == null)
        {
            headPosePrimed = false;
            return;
        }

        if (!headPosePrimed)
        {
            headPosePrimed = true;
            lastHeadPos = head.position;
            lastHeadRot = head.rotation;
            return;
        }

        float deltaPos = Vector3.Distance(lastHeadPos, head.position);
        float deltaRotDeg = Quaternion.Angle(lastHeadRot, head.rotation);
        if (deltaPos > 0.35f || deltaRotDeg > 35f)
        {
            RecenterScreensToCurrentFacing();
        }

        lastHeadPos = head.position;
        lastHeadRot = head.rotation;
    }

    private void RecenterScreensToCurrentFacing()
    {
        if (forceScreensInFrontOfViewCamera)
        {
            return;
        }

        if (leftScreen == null && rightScreen == null)
        {
            return;
        }

        PlaceScreens();
    }

    [System.Serializable]
    private class ManifestData
    {
        public int width;
        public int height;
        public int eye_w;
        public int eye_h;
        public int meta_w;
        public int meta_h;
        public int num_frames;
        public float fps;
        public float fovx_deg;
        public float fovx;
        public float fovxDeg;
        public float quant_pos_scale;
        public float quantScale;
        public float quantPosScale;
        public float quant;
        public float quant_pos;
        public int crop_x;
        public int crop_y;
        public int crop_x0;
        public int crop_y0;
        public int crop_w;
        public int crop_h;
        public bool has_crop;
    }

    private int GetCropX()
    {
        if (manifest == null)
        {
            return 0;
        }

        return manifest.crop_x > 0 ? manifest.crop_x : manifest.crop_x0;
    }

    private int GetCropY()
    {
        if (manifest == null)
        {
            return 0;
        }

        return manifest.crop_y > 0 ? manifest.crop_y : manifest.crop_y0;
    }

    private int GetCropW()
    {
        if (manifest == null)
        {
            return 0;
        }

        return manifest.crop_w > 0 ? manifest.crop_w : 0;
    }

    private int GetCropH()
    {
        if (manifest == null)
        {
            return 0;
        }

        return manifest.crop_h > 0 ? manifest.crop_h : 0;
    }

    private int GetFullWidth()
    {
        if (manifest != null && manifest.width > 0)
        {
            return manifest.width;
        }

        return metaHeader.width;
    }

    private int GetFullHeight()
    {
        if (manifest != null && manifest.height > 0)
        {
            return manifest.height;
        }

        return metaHeader.height;
    }

    private int GetMetaW()
    {
        if (manifest != null && manifest.meta_w > 0)
        {
            return manifest.meta_w;
        }

        return manifest != null ? manifest.eye_w : 0;
    }

    private int GetMetaH()
    {
        if (manifest != null && manifest.meta_h > 0)
        {
            return manifest.meta_h;
        }

        return manifest != null ? manifest.eye_h : 0;
    }

    private float GetManifestFovxDeg()
    {
        if (manifest == null)
        {
            return 0f;
        }

        if (manifest.fovx_deg > 0f)
        {
            return manifest.fovx_deg;
        }

        if (manifest.fovx > 0f)
        {
            return manifest.fovx;
        }

        if (manifest.fovxDeg > 0f)
        {
            return manifest.fovxDeg;
        }

        return 0f;
    }

    private float GetManifestQuantPosScale()
    {
        if (manifest == null)
        {
            return 0f;
        }

        if (manifest.quant_pos_scale > 0f)
        {
            return manifest.quant_pos_scale;
        }

        if (manifest.quantScale > 0f)
        {
            return manifest.quantScale;
        }

        if (manifest.quantPosScale > 0f)
        {
            return manifest.quantPosScale;
        }

        if (manifest.quant_pos > 0f)
        {
            return manifest.quant_pos;
        }

        if (manifest.quant > 0f)
        {
            return manifest.quant;
        }

        return 0f;
    }

    private void LogResolvedManifestOnce()
    {
        if (!verboseLog || !logMeta || loggedManifestResolved || manifest == null)
        {
            return;
        }

        loggedManifestResolved = true;
        // Intentional: manifest logs are disabled in the new category-only logger.
        float metaW = GetMetaW();
        float metaH = GetMetaH();
        float sx = metaW > 0 ? manifest.eye_w / metaW : 0f;
        float sy = metaH > 0 ? manifest.eye_h / metaH : 0f;
        _ = sx;
        _ = sy;
    }
}
