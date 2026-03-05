using UnityEngine;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    public enum LogCategory
    {
        META_RANGE,
        FOLLOW,
        SCALE,
        BONE,
        PINHOLE_ERR
    }

    private bool ShouldLog(LogCategory cat, int frame, int track)
    {
        return false;
    }


    private void Log(LogCategory cat, string msg, int frame = -1, int track = -1, float? metric = null)
    {
    }


    private void LogGeneral(string msg)
    {
    }


    private void LogBundle(string msg)
    {
    }


    private void LogMeta(string msg)
    {
    }


    private void LogPicking(string msg)
    {
    }


    private void LogFollow(string msg)
    {
    }


    private void LogScreens(string msg)
    {
    }


    private void LogVideo(string msg)
    {
    }


    private void LogModel(string msg)
    {
    }


    private void LogActiveCameras()
    {
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
        Camera[] cams = GetActiveCameras();
        Camera firstEnabled = null;
        Camera stereoEnabled = null;

        foreach (Camera cam in cams)
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

        return stereoEnabled ?? firstEnabled;
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
        _ = tag;
    }


    private void DumpScreenState(string tag)
    {
        _ = tag;
    }
}
