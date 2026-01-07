using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    public struct PickResult
    {
        public enum Eye
        {
            Left,
            Right
        }

        public Eye eye;
        public Transform screen;
        public Vector2 uv;
        public Vector2Int pixel;
        public string hitName;
    }

    private bool TryPick(out PickResult pick)
    {
        pick = default;

        if (!TryGetClickPosition(out Vector2 mousePos))
        {
            return false;
        }

        if (manifest == null || manifest.eye_w <= 0 || manifest.eye_h <= 0)
        {
            Debug.LogWarning("ClickPick: manifest not ready.");
            return false;
        }

        EnsureScreenCollider(leftScreen, "leftScreen");
        EnsureScreenCollider(rightScreen, "rightScreen");

        Camera cam = GetViewCamera();
        if (cam == null)
        {
            Debug.LogWarning("ClickPick: view camera not found.");
            return false;
        }

        Ray ray = cam.ScreenPointToRay(mousePos);
        if (!TryPickScreenByRay(ray, out Transform pickedScreen, out Vector2 uv, out string hitName))
        {
            VLog("ClickPick: no hit.");
            return false;
        }

        bool isLeft = leftScreen != null && (pickedScreen == leftScreen || pickedScreen.IsChildOf(leftScreen));
        bool isRight = rightScreen != null && (pickedScreen == rightScreen || pickedScreen.IsChildOf(rightScreen));
        if (!isLeft && !isRight)
        {
            VLog($"ClickPick: hit other object {hitName}");
            return false;
        }

        int u = Mathf.Clamp(Mathf.RoundToInt(uv.x * (manifest.eye_w - 1)), 0, manifest.eye_w - 1);
        int v = Mathf.Clamp(Mathf.RoundToInt((1f - uv.y) * (manifest.eye_h - 1)), 0, manifest.eye_h - 1);
        testPixel = new Vector2Int(u, v);
        pick = new PickResult
        {
            eye = isLeft ? PickResult.Eye.Left : PickResult.Eye.Right,
            screen = isLeft ? leftScreen : rightScreen,
            uv = uv,
            pixel = new Vector2Int(u, v),
            hitName = hitName
        };

        VLog($"ClickPick: screen={(isLeft ? "left" : "right")} uv={uv} pixel=({u},{v}) hit={hitName}");
        return true;
    }

    private bool TryGetClickPosition(out Vector2 mousePos)
    {
        mousePos = Vector2.zero;
#if ENABLE_INPUT_SYSTEM
        if (Mouse.current == null || !Mouse.current.leftButton.wasPressedThisFrame)
        {
            return false;
        }

        mousePos = Mouse.current.position.ReadValue();
        return true;
#else
        if (!Input.GetMouseButtonDown(0))
        {
            return false;
        }

        mousePos = Input.mousePosition;
        return true;
#endif
    }

    private bool TryPickScreenByRay(Ray ray, out Transform screen, out Vector2 uv, out string hitName)
    {
        screen = null;
        uv = Vector2.zero;
        hitName = "none";

        if (Physics.Raycast(ray, out RaycastHit hit, 20f, Physics.AllLayers, QueryTriggerInteraction.Collide))
        {
            screen = hit.transform;
            uv = hit.textureCoord;
            hitName = hit.transform.name;
            return true;
        }

        bool leftHit = TryRaycastScreenPlane(leftScreen, ray, out Vector2 leftUv, out float leftDist);
        bool rightHit = TryRaycastScreenPlane(rightScreen, ray, out Vector2 rightUv, out float rightDist);
        if (leftHit && (!rightHit || leftDist <= rightDist))
        {
            screen = leftScreen;
            uv = leftUv;
            hitName = leftScreen != null ? leftScreen.name : "leftScreen";
            VLog("ClickPick: plane fallback hit leftScreen.");
            return true;
        }

        if (rightHit)
        {
            screen = rightScreen;
            uv = rightUv;
            hitName = rightScreen != null ? rightScreen.name : "rightScreen";
            VLog("ClickPick: plane fallback hit rightScreen.");
            return true;
        }

        return false;
    }

    private bool TryRaycastScreenPlane(Transform screen, Ray ray, out Vector2 uv, out float distance)
    {
        uv = Vector2.zero;
        distance = 0f;
        if (screen == null)
        {
            return false;
        }

        Plane plane = new Plane(screen.forward, screen.position);
        if (!plane.Raycast(ray, out distance))
        {
            return false;
        }

        Vector3 point = ray.GetPoint(distance);
        Vector3 local = screen.InverseTransformPoint(point);
        float halfW = screen.localScale.x * 0.5f;
        float halfH = screen.localScale.y * 0.5f;
        if (Mathf.Abs(local.x) > halfW || Mathf.Abs(local.y) > halfH)
        {
            return false;
        }

        uv = new Vector2(local.x / screen.localScale.x + 0.5f, 0.5f - local.y / screen.localScale.y);
        return true;
    }
}
