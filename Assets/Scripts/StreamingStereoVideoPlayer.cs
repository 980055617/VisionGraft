using UnityEngine;
using UnityEngine.Video;
using UnityEngine.Networking;
using System.Collections;
using System.IO;
using System.IO.Compression;

public class StreamingStereoVideoPlayer : MonoBehaviour
{
    public string bundleFileName = "bundle.svb";
    public string bundleVideoEntryName = "video.mp4";   // zip entry name
    public string bundleManifestEntryName = "manifest.json";
    public string bundleMetaEntryName = "meta.bin";
    public bool reExtractAlways = false;                // true: always re-extract
    public string extractedVideoFileName = "video.mp4"; // extracted file name
    public string extractedManifestFileName = "manifest.json";
    public string extractedMetaFileName = "meta.bin";

    public Transform leftScreen;
    public Transform rightScreen;
    public GameObject debugMarkerPrefab;
    public float debugMarkerScale = 0.03f;
    public Vector2Int debugPixel = new Vector2Int(-1, -1); // (-1,-1)なら中心
    public float markerOffset = 0.02f; // スクリーン手前に出す(m)
    public bool spawnMarkerOnPrepared = false;
    public Transform headTransform;
    public float screenDistanceMeters = 2.0f;
    public Vector3 screenOffsetMeters = Vector3.zero;
    public GameObject testModelPrefab;
    public Vector2Int testPixel = new Vector2Int(-1, -1);
    public float testDepthMeters = 0.5f;
    public bool spawnTestModelOnPrepared = false;
    public bool destroyPreviousTestModel = true;
    public float testModelSizeMeters = 0.05f; // 5cm
    public Vector2 testModelOffsetMeters = new Vector2(0.10f, 0.0f); // screen右へ10cm
    public bool forceScreensInFrontOfViewCamera = false;

    public bool sideBySide = true;
    public float baseHeight = 1f;

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

    private void Awake()
    {
        Debug.Log("StreamingStereoVideoPlayer Awake");
    }

    private void OnEnable()
    {
        Debug.Log("StreamingStereoVideoPlayer OnEnable");
    }

    IEnumerator Start()
    {
        Debug.Log("StreamingStereoVideoPlayer Start");
        LogActiveCameras();
        Debug.Log($"Screen refs at Start: leftScreen={(leftScreen != null ? leftScreen.name : "null")} rightScreen={(rightScreen != null ? rightScreen.name : "null")}");
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
                Debug.Log($"FirstFrameReady: {frame}");
            }
            ApplyVideoFrameTexture(player);
        };

        vp.prepareCompleted += OnPrepared;

        string streamingBundleUrl = Path.Combine(Application.streamingAssetsPath, bundleFileName);
        streamingBundleUrl = streamingBundleUrl.Replace("\\", "/");
        string persistentBundlePath = Path.Combine(Application.persistentDataPath, bundleFileName);
        Debug.Log($"Streaming bundle url: {streamingBundleUrl}");
        Debug.Log($"Persistent bundle path: {persistentBundlePath}");
        bool needsBundleCopy = reExtractAlways || !File.Exists(persistentBundlePath);

        if (needsBundleCopy)
        {
            using (UnityWebRequest request = UnityWebRequest.Get(streamingBundleUrl))
            {
                yield return request.SendWebRequest();
                if (request.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogError($"Failed to read bundle from StreamingAssets. url={streamingBundleUrl} result={request.result} error={request.error}");
                    yield break;
                }

                try
                {
                    string bundleDir = Path.GetDirectoryName(persistentBundlePath);
                    if (!string.IsNullOrEmpty(bundleDir))
                    {
                        Directory.CreateDirectory(bundleDir);
                    }

                    File.WriteAllBytes(persistentBundlePath, request.downloadHandler.data);
                }
                catch (System.Exception ex)
                {
                    Debug.LogError($"Failed to write bundle. path={persistentBundlePath} ({ex.Message})");
                    yield break;
                }
            }
        }

        if (!File.Exists(persistentBundlePath))
        {
            Debug.LogError($"Bundle not found after copy. path={persistentBundlePath}");
            yield break;
        }

        string cacheDir = Path.Combine(Application.persistentDataPath, "svb_cache");
        string extractedVideoPath = Path.Combine(cacheDir, extractedVideoFileName);
        string extractedManifestPath = Path.Combine(cacheDir, extractedManifestFileName);
        string extractedMetaPath = Path.Combine(cacheDir, extractedMetaFileName);

        bool needsExtractVideo = reExtractAlways || !File.Exists(extractedVideoPath);
        bool needsExtractManifest = reExtractAlways || !File.Exists(extractedManifestPath);
        bool needsExtractMeta = reExtractAlways || !File.Exists(extractedMetaPath);
        bool needsExtractAny = needsExtractVideo || needsExtractManifest || needsExtractMeta;

        Debug.Log($"Extracted paths: video={extractedVideoPath} exists={File.Exists(extractedVideoPath)} needsExtract={needsExtractVideo}");
        Debug.Log($"Extracted paths: manifest={extractedManifestPath} exists={File.Exists(extractedManifestPath)} needsExtract={needsExtractManifest}");
        Debug.Log($"Extracted paths: meta={extractedMetaPath} exists={File.Exists(extractedMetaPath)} needsExtract={needsExtractMeta}");

        if (needsExtractAny)
        {
            try
            {
                Directory.CreateDirectory(cacheDir);
                using (var fs = new FileStream(persistentBundlePath, FileMode.Open, FileAccess.Read))
                using (var za = new ZipArchive(fs, ZipArchiveMode.Read))
                {
                    if (needsExtractVideo && !ExtractZipEntry(za, bundleVideoEntryName, extractedVideoPath))
                    {
                        yield break;
                    }

                    if (needsExtractManifest && !ExtractZipEntry(za, bundleManifestEntryName, extractedManifestPath))
                    {
                        yield break;
                    }

                    if (needsExtractMeta && !ExtractZipEntry(za, bundleMetaEntryName, extractedMetaPath))
                    {
                        yield break;
                    }
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"Failed to extract files. cacheDir={cacheDir} ({ex.Message})");
                yield break;
            }
        }

        LogExtractedFileStatus(extractedVideoPath, "video");
        LogExtractedFileStatus(extractedManifestPath, "manifest");
        LogExtractedFileStatus(extractedMetaPath, "meta");

        if (!File.Exists(extractedVideoPath))
        {
            Debug.LogError($"Extracted video missing. path={extractedVideoPath}");
            yield break;
        }

        TryLoadManifest(extractedManifestPath);

        Debug.Log($"Extracted video path: {extractedVideoPath}");
        string normalizedVideoPath = extractedVideoPath.Replace("\\", "/");
        vp.url = normalizedVideoPath;

        vp.Prepare();
    }

    private void OnPrepared(VideoPlayer source)
    {
        float w = source.width;
        float h = source.height;

        EnsureScreensExist();
        CacheMats();
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

    private bool ExtractZipEntry(ZipArchive za, string entryName, string outPath)
    {
        var entry = za.GetEntry(entryName);
        if (entry == null)
        {
            Debug.LogError($"Entry not found in bundle. entry={entryName}");
            return false;
        }

        using (var entryStream = entry.Open())
        using (var outStream = new FileStream(outPath, FileMode.Create, FileAccess.Write))
        {
            entryStream.CopyTo(outStream);
        }

        Debug.Log($"Extracted entry. entry={entryName} outPath={outPath} size={new FileInfo(outPath).Length} bytes");
        return true;
    }

    private void LogExtractedFileStatus(string path, string label)
    {
        if (!File.Exists(path))
        {
            Debug.LogWarning($"Extracted file missing. label={label} path={path}");
            return;
        }

        long size = new FileInfo(path).Length;
        Debug.Log($"Extracted file exists. label={label} size={size} bytes path={path}");
    }

    private void TryLoadManifest(string manifestPath)
    {
        if (!File.Exists(manifestPath))
        {
            Debug.LogError($"Manifest not found. path={manifestPath}");
            return;
        }

        try
        {
            string json = File.ReadAllText(manifestPath);
            manifest = JsonUtility.FromJson<ManifestData>(json);
            if (manifest == null)
            {
                Debug.LogError($"Manifest parse failed (null). path={manifestPath}");
                return;
            }

            Debug.Log($"Manifest parsed. eye_w={manifest.eye_w} eye_h={manifest.eye_h} num_frames={manifest.num_frames} fps={manifest.fps}");
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"Manifest load failed. path={manifestPath} ({ex.Message})");
        }
    }

    private void PlaceScreens()
    {
        Camera viewCam = GetViewCamera();
        Transform head = viewCam != null ? viewCam.transform : GetHeadTransform();
        Vector3 headPos = head.position;
        Vector3 headFwd = head.forward;
        Vector3 center = headPos + headFwd * screenDistanceMeters + head.TransformVector(screenOffsetMeters);
        Vector3 toHead = (headPos - center).normalized;
        Quaternion rotation = Quaternion.LookRotation(toHead, head.up) * Quaternion.Euler(0f, 180f, 0f);


        Debug.Log($"HeadSource: {(headTransform != null ? "headTransform" : (Camera.main != null ? "Camera.main" : "self"))} headPos={headPos} headFwd={headFwd}");
        Debug.Log($"PlaceScreens: viewCamera={(viewCam != null ? viewCam.name : "null")} center={center} toHead={toHead}");

        Vector3 rightOffset = head.right * 0.001f;
        if (leftScreen != null)
        {
            leftScreen.position = center - rightOffset;
            leftScreen.rotation = rotation;
            Debug.Log($"PlaceScreens: leftScreenFwd={leftScreen.forward}");
        }

        if (rightScreen != null)
        {
            rightScreen.position = center + rightOffset;
            rightScreen.rotation = rotation;
        }
    }

    private void EnsureScreensExist()
    {
        if (leftScreen == null)
        {
            var leftObj = GameObject.CreatePrimitive(PrimitiveType.Quad);
            leftObj.name = "LeftScreen_Runtime";
            leftObj.transform.SetParent(transform, false);
            leftScreen = leftObj.transform;
        }

        if (rightScreen == null)
        {
            var rightObj = GameObject.CreatePrimitive(PrimitiveType.Quad);
            rightObj.name = "RightScreen_Runtime";
            rightObj.transform.SetParent(transform, false);
            rightScreen = rightObj.transform;
        }

        EnsureScreenRenderer(leftScreen, "leftScreen");
        EnsureScreenRenderer(rightScreen, "rightScreen");
    }

    private void EnsureScreenRenderer(Transform screen, string label)
    {
        if (screen == null)
        {
            return;
        }

        var meshFilter = screen.GetComponent<MeshFilter>();
        if (meshFilter == null)
        {
            meshFilter = screen.gameObject.AddComponent<MeshFilter>();
        }

        if (meshFilter.sharedMesh == null)
        {
            if (quadMesh == null)
            {
                quadMesh = Resources.GetBuiltinResource<Mesh>("Quad.fbx");
            }

            if (quadMesh != null)
            {
                meshFilter.sharedMesh = quadMesh;
            }
            else
            {
                Debug.LogWarning($"Quad mesh not found for {label}.");
            }
        }

        var renderer = screen.GetComponent<MeshRenderer>();
        if (renderer == null)
        {
            renderer = screen.gameObject.AddComponent<MeshRenderer>();
        }

        renderer.enabled = true;
        EnsureUnlitMaterial(renderer, label);
    }

    private void EnsureUnlitMaterial(Renderer renderer, string label)
    {
        if (renderer == null)
        {
            return;
        }

        var mat = renderer.material;
        if (mat != null)
        {
            return;
        }

        if (mat == null)
        {
            var shader = Shader.Find("Custom/PerEyeStereoVideoURP");
            if (shader == null)
            {
                shader = Shader.Find("pereyestereovideoURP");
            }

            if (shader == null)
            {
                shader = Shader.Find("Universal Render Pipeline/Unlit");
            }

            if (shader == null)
            {
                shader = Shader.Find("Unlit/Texture");
            }

            if (shader != null)
            {
                mat = new Material(shader);
                renderer.material = mat;
            }
            else
            {
                Debug.LogWarning($"Fallback shader not found for {label}.");
            }
        }
    }

    private void CacheMats()
    {
        leftMat = GetScreenMaterial(leftScreen, "left");
        rightMat = GetScreenMaterial(rightScreen, "right");
        leftTexProp = ResolveTexProp(leftMat, "left");
        rightTexProp = ResolveTexProp(rightMat, "right");
        Debug.Log($"CacheMats: leftMat={(leftMat != null ? leftMat.shader.name : "null")} leftProp={leftTexProp} rightMat={(rightMat != null ? rightMat.shader.name : "null")} rightProp={rightTexProp}");
    }

    private Material GetScreenMaterial(Transform screen, string label)
    {
        if (screen == null)
        {
            return null;
        }

        var renderer = screen.GetComponent<Renderer>();
        if (renderer == null)
        {
            Debug.LogWarning($"CacheMats: {label} renderer missing.");
            return null;
        }

        return renderer.material;
    }

    private string ResolveTexProp(Material mat, string label)
    {
        if (mat == null)
        {
            return "_MainTex";
        }

        if (mat.HasProperty("_MainTex"))
        {
            return "_MainTex";
        }

        if (mat.HasProperty("_BaseMap"))
        {
            return "_BaseMap";
        }

        Debug.LogWarning($"ResolveTexProp: no known property on {label} material.");
        return "_MainTex";
    }

    private void ApplyVideoFrameTexture(VideoPlayer player)
    {
        if (player == null || player.texture == null)
        {
            return;
        }

        if (leftMat != null && leftMat.HasProperty(leftTexProp))
        {
            leftMat.SetTexture(leftTexProp, player.texture);
        }

        if (rightMat != null && rightMat.HasProperty(rightTexProp))
        {
            rightMat.SetTexture(rightTexProp, player.texture);
        }
    }

    private void DumpScreenState(string tag)
    {
        DumpOneScreenState("left", leftScreen, tag);
        DumpOneScreenState("right", rightScreen, tag);
    }

    private void DumpOneScreenState(string label, Transform screen, string tag)
    {
        if (screen == null)
        {
            Debug.LogWarning($"DumpScreenState({tag}): {label} screen is null.");
            return;
        }

        var renderer = screen.GetComponent<Renderer>();
        string rendererEnabled = renderer != null ? renderer.enabled.ToString() : "no renderer";
        string shaderName = renderer != null && renderer.sharedMaterial != null && renderer.sharedMaterial.shader != null
            ? renderer.sharedMaterial.shader.name
            : "null";
        bool textureNull = renderer == null || renderer.sharedMaterial == null || renderer.sharedMaterial.mainTexture == null;
        Debug.Log(
            $"DumpScreenState({tag}) {label}: active={screen.gameObject.activeInHierarchy} " +
            $"pos={screen.position} rot={screen.rotation.eulerAngles} scale={screen.localScale} lossyScale={screen.lossyScale} " +
            $"renderer={rendererEnabled} shader={shaderName} texNull={textureNull} layer={screen.gameObject.layer}");
    }

    private Transform GetHeadTransform()
    {
        if (headTransform != null)
        {
            return headTransform;
        }

        if (Camera.main != null)
        {
            return Camera.main.transform;
        }

        return transform;
    }

    private void LogActiveCameras()
    {
        var cams = FindObjectsOfType<Camera>();
        foreach (var cam in cams)
        {
            if (cam == null || !cam.enabled || !cam.gameObject.activeInHierarchy)
            {
                continue;
            }

            Debug.Log(
                $"ActiveCamera: name={cam.name} tag={cam.tag} pos={cam.transform.position} fwd={cam.transform.forward} " +
                $"near={cam.nearClipPlane} far={cam.farClipPlane} cullingMask={cam.cullingMask} stereoTargetEye={cam.stereoTargetEye}");
        }
    }

    private Camera GetViewCamera()
    {
        var cams = FindObjectsOfType<Camera>();
        Camera firstEnabled = null;
        Camera stereoEnabled = null;

        foreach (var cam in cams)
        {
            if (cam == null || !cam.enabled || !cam.gameObject.activeInHierarchy)
            {
                continue;
            }

            if (firstEnabled == null)
            {
                firstEnabled = cam;
            }

            if (cam.stereoTargetEye != StereoTargetEyeMask.None && stereoEnabled == null)
            {
                stereoEnabled = cam;
            }

            if (cam.CompareTag("MainCamera"))
            {
                return cam;
            }
        }

        if (stereoEnabled != null)
        {
            return stereoEnabled;
        }

        return firstEnabled;
    }

    private void LogVideoPlayerState(string tag)
    {
        if (vp == null)
        {
            Debug.LogWarning($"VideoPlayerState({tag}): vp is null.");
            return;
        }

        bool textureNull = vp.texture == null;
        bool targetTextureNull = vp.targetTexture == null;
        Debug.Log(
            $"VideoPlayerState({tag}): prepared={vp.isPrepared} playing={vp.isPlaying} frame={vp.frame} " +
            $"textureNull={textureNull} targetTextureNull={targetTextureNull} url={vp.url}");
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
            Debug.Log($"PlaybackWatchdog: t={elapsed:F1}s frame={frame} time={time:F3} playing={vp.isPlaying} textureNull={textureNull}");

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

    private void ApplyScreenFallbackMagenta()
    {
        if (fallbackApplied)
        {
            return;
        }

        fallbackApplied = true;
        ApplyFallbackToScreen(leftScreen, "left");
        ApplyFallbackToScreen(rightScreen, "right");
    }

    private void ApplyFallbackToScreen(Transform screen, string label)
    {
        if (screen == null)
        {
            Debug.LogWarning($"Fallback skipped: {label} screen is null.");
            return;
        }

        var renderer = screen.GetComponent<Renderer>();
        if (renderer == null || renderer.material == null)
        {
            Debug.LogWarning($"Fallback skipped: {label} renderer/material missing.");
            return;
        }

        var mat = renderer.material;
        if (mat.HasProperty("_BaseColor"))
        {
            mat.SetColor("_BaseColor", Color.magenta);
        }

        if (mat.HasProperty("_BaseMap"))
        {
            mat.SetTexture("_BaseMap", null);
        }

        Debug.Log($"Fallback applied: {label} screen set to magenta.");
    }

    private void TrySpawnDebugMarker()
    {
        if (leftScreen == null)
        {
            Debug.LogWarning("Debug marker skipped: leftScreen is null.");
            return;
        }

        if (manifest == null || manifest.eye_w <= 0 || manifest.eye_h <= 0)
        {
            Debug.LogWarning("Debug marker skipped: manifest eye_w/eye_h invalid or not loaded.");
            return;
        }

        Vector2Int finalPixel = debugPixel;
        if (finalPixel.x < 0 || finalPixel.y < 0)
        {
            finalPixel = new Vector2Int(manifest.eye_w / 2, manifest.eye_h / 2);
        }

        Vector3 world = EyePixelToWorldOnScreen(finalPixel.x, finalPixel.y, leftScreen, manifest.eye_w, manifest.eye_h, markerOffset);

        Debug.Log(
            $"SpawnDebugMarker: eye_w={manifest.eye_w} eye_h={manifest.eye_h} " +
            $"debugPixel=({finalPixel.x},{finalPixel.y}) " +
            $"leftScreen scale={leftScreen.localScale} pos={leftScreen.position} rot={leftScreen.rotation.eulerAngles} " +
            $"world={world}");

        GameObject marker = debugMarkerPrefab != null
            ? Instantiate(debugMarkerPrefab, world, leftScreen.rotation)
            : GameObject.CreatePrimitive(PrimitiveType.Sphere);

        if (debugMarkerPrefab == null)
        {
            marker.name = "DebugMarker(auto)";
            var collider = marker.GetComponent<Collider>();
            if (collider != null)
            {
                Destroy(collider);
            }
        }

        marker.transform.position = world;
        marker.transform.rotation = leftScreen.rotation;
        marker.transform.localScale = Vector3.one * debugMarkerScale;
    }

    private Vector3 EyePixelToWorldOnScreen(int u, int v, Transform screen, float eyeW, float eyeH, float offsetMeters)
    {
        float screenW = screen.localScale.x;
        float screenH = screen.localScale.y;

        float xLocal = (u / eyeW - 0.5f) * screenW;
        float yLocal = (0.5f - v / eyeH) * screenH;
        Vector3 worldOnPlane = screen.TransformPoint(new Vector3(xLocal, yLocal, 0f));
        Vector3 world = worldOnPlane + screen.forward * offsetMeters;

        Debug.Log($"EyePixelToWorldOnScreen: u={u} v={v} eyeW={eyeW} eyeH={eyeH} screenW={screenW} screenH={screenH} xLocal={xLocal} yLocal={yLocal} worldOnPlane={worldOnPlane} world={world}");
        return world;
    }

    private void TrySpawnTestModel()
    {
        if (leftScreen == null)
        {
            Debug.LogWarning("Test model skipped: leftScreen is null.");
            return;
        }

        if (manifest == null || manifest.eye_w <= 0 || manifest.eye_h <= 0)
        {
            Debug.LogWarning("Test model skipped: manifest eye_w/eye_h invalid or not loaded.");
            return;
        }

        Vector2Int finalPixel = testPixel;
        if (finalPixel.x < 0 || finalPixel.y < 0)
        {
            finalPixel = new Vector2Int(manifest.eye_w / 2, manifest.eye_h / 2);
        }

        Vector3 worldOnPlane = EyePixelToWorldOnScreen(finalPixel.x, finalPixel.y, leftScreen, manifest.eye_w, manifest.eye_h, 0f);
        Vector3 world = worldOnPlane
            + leftScreen.right * testModelOffsetMeters.x
            + leftScreen.up * testModelOffsetMeters.y
            + leftScreen.forward * testDepthMeters;
        Quaternion rotation = Quaternion.LookRotation(-leftScreen.forward, leftScreen.up);

        if (destroyPreviousTestModel)
        {
            var previous = GameObject.Find("TestModel(auto)");
            if (previous != null)
            {
                Destroy(previous);
            }
        }

        GameObject model = testModelPrefab != null
            ? Instantiate(testModelPrefab, world, rotation)
            : GameObject.CreatePrimitive(PrimitiveType.Cube);

        if (testModelPrefab == null)
        {
            model.name = "TestModel(auto)";
        }

        model.transform.position = world;
        model.transform.rotation = rotation;
        model.transform.localScale = Vector3.one * testModelSizeMeters;

        Debug.Log($"SpawnTestModel: eye_w={manifest.eye_w} eye_h={manifest.eye_h} testPixel=({finalPixel.x},{finalPixel.y}) worldOnPlane={worldOnPlane} world={world} depth={testDepthMeters}");
        Debug.Log($"SpawnTestModel: size={testModelSizeMeters} depth={testDepthMeters} offset={testModelOffsetMeters}");
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
