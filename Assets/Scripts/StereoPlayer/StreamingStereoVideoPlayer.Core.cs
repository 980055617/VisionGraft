using System.Collections;
using UnityEngine;
using UnityEngine.Video;

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

    private VideoPlayer vp;
    private ManifestData manifest;
    private bool loggedFirstFrame;
    private bool fallbackApplied;
    private string leftTexProp = "_MainTex";
    private string rightTexProp = "_MainTex";
    private Material leftMat;
    private Material rightMat;
    private Mesh quadMesh;
    private GameObject spawnedTestModel;
    private Vector2Int pickedPixel;
    private bool hasPickedPixel;
    private Transform pickedScreen;

    private void Awake()
    {
        LogGeneral("StreamingStereoVideoPlayer Awake");
    }

    private void OnEnable()
    {
        LogGeneral("StreamingStereoVideoPlayer OnEnable");
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
    }

    [System.Serializable]
    private class ManifestData
    {
        public int eye_w;
        public int eye_h;
        public int num_frames;
        public float fps;
    }
}
