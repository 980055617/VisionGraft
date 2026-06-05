using System.Collections.Generic;
using UnityEngine;

public static class ViewCameraSelection
{
    public static bool IsUsable(Camera camera)
    {
        return camera != null && camera.enabled && camera.gameObject.activeInHierarchy;
    }

    public static Camera Select(IReadOnlyList<Camera> cameras)
    {
        if (cameras == null)
        {
            return null;
        }

        Camera firstEnabled = null;
        Camera stereoEnabled = null;
        for (int i = 0; i < cameras.Count; i++)
        {
            Camera cam = cameras[i];
            if (!IsUsable(cam))
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
}
