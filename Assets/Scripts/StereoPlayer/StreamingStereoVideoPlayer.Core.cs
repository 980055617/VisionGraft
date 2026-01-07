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
    public Vector2Int testPixel = new Vector2Int(-1, -1);
    public float testDepthMeters = 0.5f;
    public bool spawnTestModelOnPrepared = false;
    public bool destroyPreviousTestModel = true; // Step2追従を考えるならfalse推奨
    public float testModelSizeMeters = 0.05f; // 5cm
    public Vector2 testModelOffsetMeters = new Vector2(0.10f, 0.0f); // screen右へ10cm

    [Header("Video Layout")]
    public bool sideBySide = true;
    public float baseHeight = 1f;

    [Header("Debug")]
    public bool forceScreensInFrontOfViewCamera = false;
    [SerializeField] private bool verboseLog = true;

    private VideoPlayer vp;
    private ManifestData manifest;
    private bool loggedFirstFrame;
    private Coroutine watchdogCoroutine;
    private bool fallbackApplied;
    private string leftTexProp = "_MainTex";
    private string rightTexProp = "_MainTex";
    private Material leftMat;
    private Material rightMat;
    private Mesh quadMesh;
    private GameObject spawnedTestModel;

    private void Awake()
    {
        VLog("StreamingStereoVideoPlayer Awake");
    }

    private void OnEnable()
    {
        VLog("StreamingStereoVideoPlayer OnEnable");
    }

    private IEnumerator Start()
    {
        VLog("StreamingStereoVideoPlayer Start");
        LogActiveCameras();
        VLog($"Screen refs at Start: leftScreen={(leftScreen != null ? leftScreen.name : "null")} rightScreen={(rightScreen != null ? rightScreen.name : "null")}");
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
                VLog($"FirstFrameReady: {frame}");
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
        if (watchdogCoroutine != null)
        {
            StopCoroutine(watchdogCoroutine);
        }
        watchdogCoroutine = StartCoroutine(PlaybackWatchdog());
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
        if (!TryPick(out PickResult pick))
        {
            return;
        }

        PlaceOrMoveTestModel(pick);
    }

    private IEnumerator PlaybackWatchdog()
    {
        float elapsed = 0f;
        float interval = 0.2f;
        bool sawFrame = false;

        while (elapsed < 5.0f)
        {
            if (vp == null)
            {
                yield break;
            }

            long frame = vp.frame;
            float time = (float)vp.time;
            bool textureNull = vp.texture == null;
            VLog($"PlaybackWatchdog: t={elapsed:F1}s frame={frame} time={time:F3} playing={vp.isPlaying} textureNull={textureNull}");

            if (frame >= 0)
            {
                sawFrame = true;
            }

            yield return new WaitForSeconds(interval);
            elapsed += interval;
        }

        if (!sawFrame)
        {
            Debug.LogWarning("PlaybackWatchdog: no frames decoded after 5s. Applying fallback.");
            ApplyScreenFallbackMagenta();
        }
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
