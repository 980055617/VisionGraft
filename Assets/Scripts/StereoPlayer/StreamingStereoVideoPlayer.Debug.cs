using UnityEngine;
using UnityEngine.Video;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    private void LogGeneral(string msg)
    {
        if (verboseLog && logGeneral)
        {
            Debug.Log(msg);
        }
    }

    private void LogBundle(string msg)
    {
        if (verboseLog && logBundle)
        {
            Debug.Log(msg);
        }
    }

    private void LogMeta(string msg)
    {
        if (verboseLog && logMeta)
        {
            Debug.Log(msg);
        }
    }

    private void LogPicking(string msg)
    {
        if (verboseLog && logPicking)
        {
            Debug.Log(msg);
        }
    }

    private void LogFollow(string msg)
    {
        if (verboseLog && logFollow)
        {
            Debug.Log(msg);
        }
    }

    private void LogScreens(string msg)
    {
        if (verboseLog && logScreens)
        {
            Debug.Log(msg);
        }
    }

    private void LogVideo(string msg)
    {
        if (verboseLog && logVideo)
        {
            Debug.Log(msg);
        }
    }

    private void LogModel(string msg)
    {
        if (verboseLog && logModel)
        {
            Debug.Log(msg);
        }
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
        LogOneScreenSetup(tag, "left", leftScreen, leftMat, leftTexProp);
        LogOneScreenSetup(tag, "right", rightScreen, rightMat, rightTexProp);
        bool sameInstance = leftMat != null && rightMat != null && ReferenceEquals(leftMat, rightMat);
        LogScreens($"LogStereoSetup({tag}): leftMatId={(leftMat != null ? leftMat.GetInstanceID().ToString() : "null")} rightMatId={(rightMat != null ? rightMat.GetInstanceID().ToString() : "null")} sameInstance={sameInstance}");
    }

    private void LogOneScreenSetup(string tag, string label, Transform screen, Material mat, string texProp)
    {
        if (screen == null)
        {
            Debug.LogWarning($"LogStereoSetup({tag}) {label}: screen is null.");
            return;
        }

        var renderer = screen.GetComponent<Renderer>();
        string rendererEnabled = renderer != null ? renderer.enabled.ToString() : "no renderer";
        string shaderName = mat != null && mat.shader != null ? mat.shader.name : "null";
        LogScreens(
            $"LogStereoSetup({tag}) {label}: name={screen.name} active={screen.gameObject.activeInHierarchy} layer={screen.gameObject.layer} " +
            $"renderer={rendererEnabled} shader={shaderName} texProp={texProp}");

        if (mat != null)
        {
            if (mat.HasProperty("_EyeMode"))
            {
                LogScreens($"LogStereoSetup({tag}) {label}: _EyeMode={mat.GetInt("_EyeMode")}");
            }

            if (mat.HasProperty("_UVScale"))
            {
                LogScreens($"LogStereoSetup({tag}) {label}: _UVScale={mat.GetVector("_UVScale")}");
            }

            if (mat.HasProperty("_UVOffset"))
            {
                LogScreens($"LogStereoSetup({tag}) {label}: _UVOffset={mat.GetVector("_UVOffset")}");
            }
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
        LogScreens(
            $"DumpScreenState({tag}) {label}: active={screen.gameObject.activeInHierarchy} " +
            $"pos={screen.position} rot={screen.rotation.eulerAngles} scale={screen.localScale} lossyScale={screen.lossyScale} " +
            $"renderer={rendererEnabled} shader={shaderName} texNull={textureNull} layer={screen.gameObject.layer}");
    }
}
