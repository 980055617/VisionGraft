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
        if (ViewCameraSelection.IsUsable(cachedViewCamera))
        {
            return cachedViewCamera;
        }

        cachedViewCamera = ViewCameraSelection.Select(GetActiveCameras());
        return cachedViewCamera;
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
