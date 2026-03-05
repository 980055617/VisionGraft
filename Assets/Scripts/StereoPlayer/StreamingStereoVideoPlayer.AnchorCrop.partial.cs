using UnityEngine;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    // Depends on: manifest/crop settings and screen projection helpers
    // Provides: meta-range tracking, crop remap, and anchor-eye resolve

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

