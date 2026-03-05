using UnityEngine;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    // Depends on: manifest/metaHeader fields in Core.cs and Meta partial
    // Provides: manifest fallback resolution, crop helpers, intrinsics/fov/quant accessors

    private bool TryGetManifestJointsSpace(out string jointsSpace)
    {
        jointsSpace = null;
        if (manifest == null || string.IsNullOrEmpty(manifest.joints_space))
        {
            return false;
        }

        if (manifest.joints_space == "camera_xyz_absolute" || manifest.joints_space == "camera_xyz_root_relative")
        {
            jointsSpace = manifest.joints_space;
            return true;
        }

        return false;
    }


    private string GetEffectiveJointsSpaceTag()
    {
        if (TryGetManifestJointsSpace(out string jointsSpace))
        {
            return jointsSpace;
        }

        // Fallback keeps legacy behavior assumptions.
        return "camera_xyz_root_relative";
    }


    private bool IsEffectiveJointsSpaceAbsolute()
    {
        return GetEffectiveJointsSpaceTag() == "camera_xyz_absolute";
    }


    private bool TryGetManifestNormalizedIntrinsics(out float fxNorm, out float fyNorm, out int eyeW, out int eyeH)
    {
        fxNorm = 0f;
        fyNorm = 0f;
        eyeW = 0;
        eyeH = 0;
        if (manifest == null)
        {
            return false;
        }

        eyeW = manifest.eye_w;
        eyeH = manifest.eye_h;
        if (eyeW <= 0 || eyeH <= 0)
        {
            return false;
        }

        if (manifest.fx_norm <= 0f || manifest.fy_norm <= 0f)
        {
            return false;
        }

        fxNorm = manifest.fx_norm;
        fyNorm = manifest.fy_norm;
        return true;
    }


    private int GetCropX()
    {
        if (manifest == null)
        {
            return 0;
        }

        return manifest.crop_x > 0 ? manifest.crop_x : manifest.crop_x0;
    }


    private int GetCropY()
    {
        if (manifest == null)
        {
            return 0;
        }

        return manifest.crop_y > 0 ? manifest.crop_y : manifest.crop_y0;
    }


    private int GetCropW()
    {
        if (manifest == null)
        {
            return 0;
        }

        return manifest.crop_w > 0 ? manifest.crop_w : 0;
    }


    private int GetCropH()
    {
        if (manifest == null)
        {
            return 0;
        }

        return manifest.crop_h > 0 ? manifest.crop_h : 0;
    }


    private int GetFullWidth()
    {
        if (manifest != null && manifest.width > 0)
        {
            return manifest.width;
        }

        return metaHeader.width;
    }


    private int GetMetaW()
    {
        if (manifest != null && manifest.meta_w > 0)
        {
            return manifest.meta_w;
        }

        return manifest != null ? manifest.eye_w : 0;
    }


    private int GetMetaH()
    {
        if (manifest != null && manifest.meta_h > 0)
        {
            return manifest.meta_h;
        }

        return manifest != null ? manifest.eye_h : 0;
    }


    private float GetManifestFovxDeg()
    {
        if (manifest == null)
        {
            return 0f;
        }

        if (manifest.fovx_deg > 0f)
        {
            return manifest.fovx_deg;
        }

        if (manifest.fovx > 0f)
        {
            return manifest.fovx;
        }

        if (manifest.fovxDeg > 0f)
        {
            return manifest.fovxDeg;
        }

        return 0f;
    }


    private float GetManifestQuantPosScale()
    {
        if (manifest == null)
        {
            return 0f;
        }

        if (manifest.quant_pos_scale > 0f)
        {
            return manifest.quant_pos_scale;
        }

        if (manifest.quantScale > 0f)
        {
            return manifest.quantScale;
        }

        if (manifest.quantPosScale > 0f)
        {
            return manifest.quantPosScale;
        }

        if (manifest.quant_pos > 0f)
        {
            return manifest.quant_pos;
        }

        if (manifest.quant > 0f)
        {
            return manifest.quant;
        }

        return 0f;
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

