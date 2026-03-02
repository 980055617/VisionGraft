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


    private int GetFullHeight()
    {
        if (manifest != null && manifest.height > 0)
        {
            return manifest.height;
        }

        return metaHeader.height;
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


    private void LogResolvedManifestOnce()
    {
        if (!verboseLog || !logMeta || loggedManifestResolved || manifest == null)
        {
            return;
        }

        loggedManifestResolved = true;
        // Intentional: manifest logs are disabled in the new category-only logger.
        float metaW = GetMetaW();
        float metaH = GetMetaH();
        float sx = metaW > 0 ? manifest.eye_w / metaW : 0f;
        float sy = metaH > 0 ? manifest.eye_h / metaH : 0f;
        _ = sx;
        _ = sy;
    }
}

