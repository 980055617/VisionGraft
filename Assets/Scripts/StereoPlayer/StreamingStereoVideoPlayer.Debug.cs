using UnityEngine;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
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


    private Transform GetViewOrHeadTransform()
    {
        Camera viewCam = GetViewCamera();
        return viewCam != null ? viewCam.transform : GetHeadTransform();
    }
}
