using UnityEngine;

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
        public Ray ray;
        public float hitDistance;
        public Vector3 hitPoint;
        public bool hasHitDistance;
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
            return false;
        }

        Camera cam = GetViewCamera();
        if (cam == null)
        {
            return false;
        }

        Ray ray = cam.ScreenPointToRay(mousePos);
        if (!TryPickScreenByRay(ray, out Transform pickedScreen, out Vector2 uv, out string hitName, out float hitDistance, out Vector3 hitPoint, out bool hasHitDistance))
        {
            return false;
        }

        bool isLeft = leftScreen != null && (pickedScreen == leftScreen || pickedScreen.IsChildOf(leftScreen));
        bool isRight = rightScreen != null && (pickedScreen == rightScreen || pickedScreen.IsChildOf(rightScreen));
        if (!isLeft && !isRight)
        {
            return false;
        }

        int u = Mathf.Clamp(Mathf.RoundToInt(uv.x * (manifest.eye_w - 1)), 0, manifest.eye_w - 1);
        int v = Mathf.Clamp(Mathf.RoundToInt((1f - uv.y) * (manifest.eye_h - 1)), 0, manifest.eye_h - 1);
        pick = new PickResult
        {
            eye = isLeft ? PickResult.Eye.Left : PickResult.Eye.Right,
            screen = isLeft ? leftScreen : rightScreen,
            uv = uv,
            pixel = new Vector2Int(u, v),
            hitName = hitName,
            ray = ray,
            hitDistance = hitDistance,
            hitPoint = hitPoint,
            hasHitDistance = hasHitDistance
        };

        return true;
    }

    private bool TryGetClickPosition(out Vector2 mousePos)
    {
        return PointerClickInput.TryReadPrimaryClickPosition(out mousePos);
    }

    private bool TryPickScreenByRay(Ray ray, out Transform screen, out Vector2 uv, out string hitName, out float hitDistance, out Vector3 hitPoint, out bool hasHitDistance)
    {
        screen = null;
        uv = Vector2.zero;
        hitName = "none";
        hitDistance = 0f;
        hitPoint = Vector3.zero;
        hasHitDistance = false;

        bool leftHit = TryRaycastScreenPlane(leftScreen, ray, out Vector2 leftUv, out float leftDist, out Vector3 leftPoint);
        bool rightHit = TryRaycastScreenPlane(rightScreen, ray, out Vector2 rightUv, out float rightDist, out Vector3 rightPoint);
        if (leftHit && (!rightHit || leftDist <= rightDist))
        {
            screen = leftScreen;
            uv = leftUv;
            hitName = leftScreen != null ? leftScreen.name : "leftScreen";
            hitDistance = leftDist;
            hitPoint = leftPoint;
            hasHitDistance = true;
            return true;
        }

        if (rightHit)
        {
            screen = rightScreen;
            uv = rightUv;
            hitName = rightScreen != null ? rightScreen.name : "rightScreen";
            hitDistance = rightDist;
            hitPoint = rightPoint;
            hasHitDistance = true;
            return true;
        }

        return false;
    }

    private bool TryRaycastScreenPlane(Transform screen, Ray ray, out Vector2 uv, out float distance, out Vector3 point)
    {
        uv = Vector2.zero;
        distance = 0f;
        point = Vector3.zero;
        if (screen == null)
        {
            return false;
        }

        Plane plane = new Plane(screen.forward, screen.position);
        if (!plane.Raycast(ray, out distance))
        {
            return false;
        }

        point = ray.GetPoint(distance);
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
