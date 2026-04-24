using UnityEngine;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    // Depends on: manifest/crop settings and screen projection helpers
    // Provides: anchor-eye resolve


    private bool ResolveAnchorToScreen(ushort anchorU, out Transform screen, out int uEye, out bool isRightEye)
    {
        screen = leftScreen;
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

