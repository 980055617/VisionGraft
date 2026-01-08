using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Video;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    public enum LogCategory
    {
        // META_RANGE: meta anchor(u,v) min/max over 30-60 frames to verify crop-space.
        META_RANGE,
        // FOLLOW: per-track follow updates (frame/track/u,v/z/world).
        FOLLOW,
        // SCALE: bbox->world size and applied scale.
        SCALE,
        // BONE: skeleton decode/apply status and pose debug.
        BONE,
        // PINHOLE_ERR: screen-plane vs pinhole error.
        PINHOLE_ERR
    }

    [System.Serializable]
    public class DebugLogConfig
    {
        // Master switch for all debug logs.
        public bool enableLogs = true;
        // Enable specific categories to get one-line logs for that topic.
        public List<LogCategory> enabled = new List<LogCategory>();
        // If set, only emit logs for this frame (matching current meta frame).
        public int onlyFrame = -1;
        // If set, only emit logs for this track id.
        public int onlyTrack = -1;
        // Emit logs every N frames (1 = every frame).
        public int logEveryNFrames = 1;
        // Stop after this many log lines (0 = no limit).
        public int maxLines = 50;
        public bool dedup = true;
    }

    [SerializeField] private DebugLogConfig debugLog = new DebugLogConfig();
    private readonly Dictionary<(LogCategory, int, int), string> lastLogCache = new Dictionary<(LogCategory, int, int), string>();
    private int logCount;

    private bool ShouldLog(LogCategory cat, int frame, int track)
    {
        if (!verboseLog || debugLog == null || !debugLog.enableLogs)
        {
            return false;
        }

        if (debugLog.enabled != null && debugLog.enabled.Count > 0 && !debugLog.enabled.Contains(cat))
        {
            return false;
        }

        if (debugLog.onlyFrame >= 0 && frame >= 0 && frame != debugLog.onlyFrame)
        {
            return false;
        }

        if (debugLog.onlyTrack >= 0 && track >= 0 && track != debugLog.onlyTrack)
        {
            return false;
        }

        if (debugLog.logEveryNFrames > 1 && frame >= 0 && (frame % debugLog.logEveryNFrames) != 0)
        {
            return false;
        }

        return true;
    }

    private void Log(LogCategory cat, string msg, int frame = -1, int track = -1, float? metric = null)
    {
        if (!ShouldLog(cat, frame, track))
        {
            return;
        }

        if (debugLog.dedup)
        {
            var key = (cat, frame, track);
            if (lastLogCache.TryGetValue(key, out string last) && last == msg)
            {
                return;
            }

            lastLogCache[key] = msg;
        }

        if (debugLog.maxLines > 0 && logCount >= debugLog.maxLines)
        {
            return;
        }

        logCount++;
        Debug.Log($"{cat} {msg}");
    }

    private void LogGeneral(string msg)
    {
        return;
    }

    private void LogBundle(string msg)
    {
        return;
    }

    private void LogMeta(string msg)
    {
        return;
    }

    private void LogPicking(string msg)
    {
        return;
    }

    private void LogFollow(string msg)
    {
        return;
    }

    private void LogScreens(string msg)
    {
        return;
    }

    private void LogVideo(string msg)
    {
        return;
    }

    private void LogModel(string msg)
    {
        return;
    }

    private void LogActiveCameras()
    {
        var cams = GetActiveCameras();
        foreach (var cam in cams)
        {
            if (cam == null || !cam.enabled || !cam.gameObject.activeInHierarchy)
            {
                continue;
            }

            LogGeneral(
                $"ActiveCamera: name={cam.name} tag={cam.tag} pos={cam.transform.position} fwd={cam.transform.forward} " +
                $"near={cam.nearClipPlane} far={cam.farClipPlane} cullingMask={cam.cullingMask} stereoTargetEye={cam.stereoTargetEye}");
        }
    }

    private Camera[] GetActiveCameras()
    {
#if UNITY_2023_1_OR_NEWER
        return FindObjectsByType<Camera>(FindObjectsSortMode.None);
#else
        return FindObjectsOfType<Camera>();
#endif
    }

    private Camera GetViewCamera()
    {
        var cams = GetActiveCameras();
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

    private void LogVideoPlayerState(string tag)
    {
        if (vp == null)
        {
            Debug.LogWarning($"VideoPlayerState({tag}): vp is null.");
            return;
        }

        bool textureNull = vp.texture == null;
        bool targetTextureNull = vp.targetTexture == null;
        LogVideo(
            $"VideoPlayerState({tag}): prepared={vp.isPrepared} playing={vp.isPlaying} frame={vp.frame} " +
            $"textureNull={textureNull} targetTextureNull={targetTextureNull} url={vp.url}");
    }

    private void LogStereoSetup(string tag)
    {
        return;
    }

    private void LogOneScreenSetup(string tag, string label, Transform screen, Material mat, string texProp)
    {
        return;
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
        LogScreens(
            $"DumpScreenState({tag}) {label}: active={screen.gameObject.activeInHierarchy} " +
            $"pos={screen.position} rot={screen.rotation.eulerAngles} scale={screen.localScale} lossyScale={screen.lossyScale} " +
            $"renderer={rendererEnabled} shader={shaderName} texNull={textureNull} layer={screen.gameObject.layer}");
    }
}
