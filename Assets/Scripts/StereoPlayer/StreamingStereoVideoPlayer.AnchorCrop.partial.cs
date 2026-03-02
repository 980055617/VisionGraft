using UnityEngine;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    // Depends on: manifest/crop settings, anchor debug cubes, screen projection helpers
    // Provides: anchor debug cube updates, meta range logs, crop/pinhole diagnostics, anchor-eye resolve

    private void UpdateAnchorDebugCubes(Transform screen, float uEye, float vEye, Vector3 worldPinhole, Camera viewCam, float bboxWorldH)
    {
        if (!showAnchorDebugCubes)
        {
            return;
        }

        if (screen == null || manifest == null || manifest.eye_w <= 0 || manifest.eye_h <= 0)
        {
            return;
        }

        if (anchorPinholeCube == null)
        {
            anchorPinholeCube = CreateAnchorCube("AnchorPinholeCube", Color.cyan);
        }

        if (anchorScreenCube == null)
        {
            anchorScreenCube = CreateAnchorCube("AnchorScreenCube", Color.yellow);
        }

        Vector3 worldOnPlane = EyePixelToWorldOnScreen(uEye, vEye, screen, manifest.eye_w, manifest.eye_h, 0f);
        Vector3 pinholePos = worldPinhole;
        Vector3 screenPos = worldOnPlane;
        if (anchorDebugAlignBottom && bboxWorldH > 0f)
        {
            Vector3 upCam = viewCam != null ? viewCam.transform.up : screen.up;
            pinholePos -= upCam * (bboxWorldH * 0.5f);
            screenPos -= screen.up * (bboxWorldH * 0.5f);
        }

        anchorPinholeCube.transform.position = pinholePos;
        anchorScreenCube.transform.position = screenPos;
    }


    private GameObject CreateAnchorCube(string name, Color color)
    {
        GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cube.name = name;
        cube.transform.localScale = Vector3.one * anchorDebugCubeSize;
        var collider = cube.GetComponent<Collider>();
        if (collider != null)
        {
            Destroy(collider);
        }

        var renderer = cube.GetComponent<Renderer>();
        if (renderer != null)
        {
            var mat = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
            if (mat == null)
            {
                mat = new Material(Shader.Find("Unlit/Color"));
            }
            if (mat != null)
            {
                mat.color = color;
                renderer.material = mat;
            }
        }

        return cube;
    }


    private void UpdateMetaRange(int frame)
    {
        if (metaRangeLogged || frame == lastMetaRangeFrame)
        {
            return;
        }

        lastMetaRangeFrame = frame;
        if (metaRangeStartFrame < 0)
        {
            metaRangeStartFrame = frame;
        }

        metaRangeFrameCount++;
        for (int i = 0; i < metaFrameObjects.Count; i++)
        {
            MetaObj obj = metaFrameObjects[i];
            metaRangeMinU = Mathf.Min(metaRangeMinU, obj.anchorU);
            metaRangeMaxU = Mathf.Max(metaRangeMaxU, obj.anchorU);
            metaRangeMinV = Mathf.Min(metaRangeMinV, obj.anchorV);
            metaRangeMaxV = Mathf.Max(metaRangeMaxV, obj.anchorV);
            if (obj.hasSkeleton)
            {
                skeletonPresent = true;
            }
        }

        if (metaRangeFrameCount >= MetaRangeFrameWindow)
        {
            Log(LogCategory.META_RANGE,
                $"u[{metaRangeMinU},{metaRangeMaxU}] v[{metaRangeMinV},{metaRangeMaxV}] eyeH={manifest.eye_h} metaH={GetMetaH()} crop_y0={GetCropY()} crop_h={GetCropH()}");
            metaRangeLogged = true;
            if (!boneStatusLogged && !skeletonPresent)
            {
                boneStatusLogged = true;
                Log(LogCategory.BONE, "BONE_STATUS no skeleton");
            }
        }
    }


    private bool ShouldLogBoneDetails(int frame, int track)
    {
        if (debugLog == null)
        {
            return false;
        }

        return debugLog.onlyFrame >= 0 && debugLog.onlyTrack >= 0
            && debugLog.onlyFrame == frame && debugLog.onlyTrack == track;
    }


    private void LogBoneDetails(MetaObj obj, int frame)
    {
        if (!obj.hasSkeleton || obj.jointsCam == null || obj.jointsVis == null || obj.skeletonKpCount == 0)
        {
            return;
        }

        int kp = obj.skeletonKpCount;
        int show = Mathf.Min(3, kp);
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        sb.Append("BONE_DETAIL f=");
        sb.Append(frame);
        sb.Append(" t=");
        sb.Append(obj.trackId);
        sb.Append(" kp=");
        sb.Append(kp);
        sb.Append(" joints=");
        for (int i = 0; i < show; i++)
        {
            Vector3 j = obj.jointsCam.Length > i ? obj.jointsCam[i] : Vector3.zero;
            if (i > 0)
            {
                sb.Append(",");
            }
            sb.Append("[");
            sb.Append(j.x.ToString("F3"));
            sb.Append(",");
            sb.Append(j.y.ToString("F3"));
            sb.Append(",");
            sb.Append(j.z.ToString("F3"));
            sb.Append("]");
        }
        Log(LogCategory.BONE, sb.ToString(), frame, (int)obj.trackId);
    }


    private void LogReprojectionError(uint trackId, float uEye, float vEye, float zMeters, Vector3 world, Camera viewCam, int frame)
    {
        return;
    }


    private bool ApplyCropToEyePixel(ref float uEye, ref float vEye)
    {
        if (manifest == null)
        {
            return false;
        }

        int cw = GetCropW();
        int ch = GetCropH();
        bool hasCrop = manifest.has_crop || (cw > 0 && ch > 0);
        if (!hasCrop)
        {
            return false;
        }

        int cx = GetCropX();
        int cy = GetCropY();
        if (cw <= 0 || ch <= 0)
        {
            return false;
        }

        uEye = (uEye - cx) / cw * manifest.eye_w;
        vEye = (vEye - cy) / ch * manifest.eye_h;
        uEye = Mathf.Clamp(uEye, 0f, manifest.eye_w - 1f);
        vEye = Mathf.Clamp(vEye, 0f, manifest.eye_h - 1f);
        return true;
    }


    private void LogCropMapping(float uBefore, float vBefore, float uAfter, float vAfter, float bboxH, float bboxHAdjusted)
    {
        return;
    }


    private void DebugScreenPinholeConsistency(Transform screen, float uEye, float vEye, int frame, int track)
    {
        if (!verboseLog)
        {
            return;
        }

        if (!ShouldLog(LogCategory.PINHOLE_ERR, frame, track))
        {
            return;
        }

        if (screen == null || manifest == null || manifest.eye_w <= 0 || manifest.eye_h <= 0)
        {
            return;
        }

        if (!TryGetFocalLengths(out float fx, out float fy))
        {
            return;
        }

        Camera viewCam = GetViewCamera() ?? Camera.main;
        if (viewCam == null)
        {
            return;
        }

        if (frame == lastScreenPinholeLogFrame)
        {
            return;
        }

        lastScreenPinholeLogFrame = frame;

        Vector3 worldOnPlane = EyePixelToWorldOnScreen(uEye, vEye, screen, manifest.eye_w, manifest.eye_h, 0f);

        float xNdc = (uEye / manifest.eye_w - 0.5f) * 2f;
        float yNdc = (0.5f - vEye / manifest.eye_h) * 2f;
        Vector3 dirCamLocal = new Vector3(xNdc / fx, yNdc / fy, 1f).normalized;
        Vector3 dirWorld = viewCam.transform.TransformDirection(dirCamLocal);

        GetScreenMeshLocalBounds(screen, out Vector3 center, out _);
        Vector3 planePoint = screen.TransformPoint(Vector3.Scale(center, screen.localScale));
        Plane plane = new Plane(screen.forward, planePoint);
        Ray ray = new Ray(viewCam.transform.position, dirWorld);
        if (!plane.Raycast(ray, out float t))
        {
            return;
        }

        Vector3 hit = ray.GetPoint(t);
        float err = Vector3.Distance(hit, worldOnPlane);
        float fovxDeg = 0f;
        TryGetFovxDeg(out fovxDeg);
        Log(LogCategory.PINHOLE_ERR,
            $"f={frame} t={track} err={err:F4} cam={viewCam.name} fov={fovxDeg:F1} screenDist={screenDistanceMeters:F3}",
            frame, track, err);

        LogScreenPinholeSamples(viewCam, screen, fx, fy, frame, track);
    }


    private void LogScreenPinholeSamples(Camera viewCam, Transform screen, float fx, float fy, int frame, int track)
    {
        if (!verboseLog)
        {
            return;
        }

        float now = Time.time;
        if (lastScreenPinholeSampleLogTime >= 0f && now - lastScreenPinholeSampleLogTime < 1f)
        {
            return;
        }

        lastScreenPinholeSampleLogTime = now;
        float w = manifest.eye_w;
        float h = manifest.eye_h;
        LogScreenPinholeSample(viewCam, screen, fx, fy, w * 0.5f, h * 0.5f, "center", frame, track);
        LogScreenPinholeSample(viewCam, screen, fx, fy, 0f, 0f, "tl", frame, track);
        LogScreenPinholeSample(viewCam, screen, fx, fy, w - 1f, 0f, "tr", frame, track);
        LogScreenPinholeSample(viewCam, screen, fx, fy, 0f, h - 1f, "bl", frame, track);
        LogScreenPinholeSample(viewCam, screen, fx, fy, w - 1f, h - 1f, "br", frame, track);
    }


    private void LogScreenPinholeSample(Camera viewCam, Transform screen, float fx, float fy, float uEye, float vEye, string label, int frame, int track)
    {
        if (viewCam == null || screen == null)
        {
            return;
        }

        Vector3 worldOnPlane = EyePixelToWorldOnScreen(uEye, vEye, screen, manifest.eye_w, manifest.eye_h, 0f);
        float xNdc = (uEye / manifest.eye_w - 0.5f) * 2f;
        float yNdc = (0.5f - vEye / manifest.eye_h) * 2f;
        Vector3 dirCamLocal = new Vector3(xNdc / fx, yNdc / fy, 1f).normalized;
        Vector3 dirWorld = viewCam.transform.TransformDirection(dirCamLocal);
        GetScreenMeshLocalBounds(screen, out Vector3 center, out _);
        Vector3 planePoint = screen.TransformPoint(Vector3.Scale(center, screen.localScale));
        Plane plane = new Plane(screen.forward, planePoint);
        Ray ray = new Ray(viewCam.transform.position, dirWorld);
        if (!plane.Raycast(ray, out float t))
        {
            return;
        }

        Vector3 hit = ray.GetPoint(t);
        float err = Vector3.Distance(hit, worldOnPlane);
        float fovxDeg = 0f;
        TryGetFovxDeg(out fovxDeg);
        Log(LogCategory.PINHOLE_ERR,
            $"f={frame} t={track} sample={label} err={err:F4} cam={viewCam.name} fov={fovxDeg:F1} screenDist={screenDistanceMeters:F3}",
            frame, track, err);
    }


    private bool ResolveAnchorToScreen(ushort anchorU, out Transform screen, out int uEye, out bool isRightEye)
    {
        screen = pickedScreen != null ? pickedScreen : leftScreen;
        uEye = anchorU;
        isRightEye = false;

        if (manifest == null || manifest.eye_w <= 0)
        {
            return false;
        }

        int fullWidth = GetFullWidth();
        if (fullWidth >= manifest.eye_w * 2 && rightScreen != null)
        {
            if (anchorU >= manifest.eye_w)
            {
                screen = rightScreen;
                uEye = anchorU - manifest.eye_w;
                isRightEye = true;
            }
            else
            {
                screen = leftScreen;
                uEye = anchorU;
            }
        }

        if (screen == null)
        {
            return false;
        }

        uEye = Mathf.Clamp(uEye, 0, manifest.eye_w - 1);
        return true;
    }

}

