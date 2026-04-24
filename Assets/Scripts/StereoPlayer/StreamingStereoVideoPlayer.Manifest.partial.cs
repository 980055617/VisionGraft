using UnityEngine;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    // Depends on: manifest/metaHeader fields in Core.cs and Meta partial
    // Provides: manifest fallback resolution, crop helpers, intrinsics/fov/quant accessors

    private bool IsManifestJointsSpaceRootRelative()
    {
        return manifest != null && manifest.joints_space == "camera_xyz_root_relative";
    }


    private int GetFullWidth()
    {
        if (manifest != null && manifest.width > 0)
        {
            return manifest.width;
        }

        return metaHeader.width;
    }


    private float GetManifestFovxDeg()
    {
        return manifest != null && manifest.fovx_deg > 0f
            ? manifest.fovx_deg
            : 0f;
    }


    private float GetManifestQuantPosScale()
    {
        return manifest != null && manifest.quant_pos_scale > 0f
            ? manifest.quant_pos_scale
            : 0f;
    }

    private float DecodeAnchorDepthMetersFromBundle(float zRaw01)
    {
        if (float.IsNaN(zRaw01) || float.IsInfinity(zRaw01))
        {
            zRaw01 = 0f;
        }

        float z01 = Mathf.Clamp01(zRaw01);
        float screenDist = Mathf.Max(0.001f, screenDistanceMeters);
        float eps = Mathf.Max(0f, epsilonMeters);
        float popout = Mathf.Max(0f, popoutRangeMeters) * z01;

        float zPlacement = screenDist - eps - popout;
        zPlacement = Mathf.Max(zPlacement, Mathf.Max(0.001f, minDistanceFromHeadMeters));
        zPlacement = Mathf.Min(zPlacement, screenDist - 0.0001f);
        return Mathf.Max(0.001f, zPlacement);
    }

    private Vector3 DecodeJointCamFromBundle(Vector3 bundleCam)
    {
        if (float.IsNaN(bundleCam.x) || float.IsInfinity(bundleCam.x) ||
            float.IsNaN(bundleCam.y) || float.IsInfinity(bundleCam.y) ||
            float.IsNaN(bundleCam.z) || float.IsInfinity(bundleCam.z))
        {
            return Vector3.zero;
        }

        return bundleCam;
    }

}

