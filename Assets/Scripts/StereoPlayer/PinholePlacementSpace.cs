using UnityEngine;

public static class PinholePlacementSpace
{
    public static bool TryResolveProjectionIntrinsics(
        ManifestData manifest,
        bool hasFovxDeg,
        float fovxDeg,
        out float fx,
        out float fy,
        out float cxPixels,
        out float cyPixels)
    {
        fx = 0f;
        fy = 0f;
        cxPixels = 0f;
        cyPixels = 0f;
        if (manifest == null || manifest.eye_w <= 0 || manifest.eye_h <= 0)
        {
            return false;
        }

        float w = manifest.eye_w;
        float h = manifest.eye_h;
        float cxNorm = ResolvePrincipalPointNorm(manifest.cx, w);
        float cyNorm = ResolvePrincipalPointNorm(manifest.cy, h);
        cxPixels = Mathf.Clamp01(cxNorm) * w;
        cyPixels = Mathf.Clamp01(cyNorm) * h;

        if (manifest.fx_norm > 0f && manifest.fy_norm > 0f)
        {
            fx = manifest.fx_norm;
            fy = manifest.fy_norm;
            return true;
        }

        if (!hasFovxDeg)
        {
            return false;
        }

        float fovxRad = fovxDeg * Mathf.Deg2Rad;
        fx = 1f / Mathf.Tan(fovxRad * 0.5f);
        if (manifest.fovy_deg > 0f || manifest.fovy > 0f)
        {
            float fovyDeg = manifest.fovy_deg > 0f ? manifest.fovy_deg : manifest.fovy;
            float fovyRad = fovyDeg * Mathf.Deg2Rad;
            fy = 1f / Mathf.Tan(fovyRad * 0.5f);
        }
        else
        {
            fy = fx * (manifest.eye_w / (float)manifest.eye_h);
        }
        return fx > 0f && fy > 0f;
    }

    public static Vector3 ReconstructCamLocalFromEyePixel(
        ManifestData manifest,
        float uEye,
        float vEye,
        float zMeters,
        float fx,
        float fy,
        int eyeW,
        int eyeH)
    {
        float cxNorm = 0.5f;
        float cyNorm = 0.5f;
        if (manifest != null && manifest.eye_w > 0 && manifest.eye_h > 0)
        {
            cxNorm = ResolvePrincipalPointNorm(manifest.cx, manifest.eye_w);
            cyNorm = ResolvePrincipalPointNorm(manifest.cy, manifest.eye_h);
        }

        float xNdc = ((uEye / (float)eyeW) - cxNorm) * 2f;
        float yNdc = (cyNorm - (vEye / (float)eyeH)) * 2f;
        return new Vector3(xNdc * zMeters / fx, yNdc * zMeters / fy, zMeters);
    }

    public static Vector3 EyePixelDepthToWorld(
        Vector3 camOrigin,
        Quaternion camRotation,
        ManifestData manifest,
        float uEye,
        float vEye,
        float zMeters,
        float fx,
        float fy)
    {
        Vector3 camLocal = ReconstructCamLocalFromEyePixel(
            manifest,
            uEye,
            vEye,
            zMeters,
            fx,
            fy,
            manifest.eye_w,
            manifest.eye_h);
        return camOrigin + (camRotation * camLocal);
    }

    public static bool TryProjectCamLocalToEyePixel(
        ManifestData manifest,
        Vector3 camLocal,
        float fx,
        float fy,
        out Vector2 eyePixel)
    {
        eyePixel = Vector2.zero;
        if (manifest == null ||
            manifest.eye_w <= 0 ||
            manifest.eye_h <= 0 ||
            fx <= 0f ||
            fy <= 0f ||
            camLocal.z <= 0.001f)
        {
            return false;
        }

        float cxNorm = ResolvePrincipalPointNorm(manifest.cx, manifest.eye_w);
        float cyNorm = ResolvePrincipalPointNorm(manifest.cy, manifest.eye_h);
        eyePixel = new Vector2(
            (cxNorm + (camLocal.x * fx / camLocal.z) * 0.5f) * manifest.eye_w,
            (cyNorm - (camLocal.y * fy / camLocal.z) * 0.5f) * manifest.eye_h);
        return true;
    }

    private static float ResolvePrincipalPointNorm(float value, float dimension)
    {
        if (value <= 0f)
        {
            return 0.5f;
        }

        return value > 1f ? value / dimension : value;
    }
}
