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

    private bool prevPickTriggerPressed;

    private bool TryPick(out PickResult pick)
    {
        pick = default;

        if (manifest == null || manifest.eye_w <= 0 || manifest.eye_h <= 0)
        {
            return false;
        }

        if (!TryResolvePickRay(out Ray ray))
        {
            return false;
        }

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

        // uv は左上原点（TryRaycastScreenPlane が meta.bin と同じ向きで返す）。
        // ここで v を反転すると上下がひっくり返り、80px の最近傍判定にまず当たらなくなる。
        int u = Mathf.Clamp(Mathf.RoundToInt(uv.x * (manifest.eye_w - 1)), 0, manifest.eye_w - 1);
        int v = Mathf.Clamp(Mathf.RoundToInt(uv.y * (manifest.eye_h - 1)), 0, manifest.eye_h - 1);
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

    // Editor はマウス、実機はコントローラのトリガー。PointerClickInput は Mouse.current しか
    // 読まないので、マウスのない Quest では対象を選ぶ手段が無い状態だった。
    private bool TryResolvePickRay(out Ray ray)
    {
        ray = default;

        if (TryGetClickPosition(out Vector2 mousePos))
        {
            Camera cam = GetViewCamera();
            if (cam != null)
            {
                ray = cam.ScreenPointToRay(mousePos);
                return true;
            }
        }

        return TryResolveXrPickRay(out ray);
    }


    private bool TryResolveXrPickRay(out Ray ray)
    {
        ray = default;

        // パネルを開いている間のトリガーは UI 操作なので、その裏にあるスクリーンを拾わない。
        if (runtimeSettingsOpen || runtimeModelPickerOpen || bundlePickerActive)
        {
            prevPickTriggerPressed = false;
            return false;
        }

        bool hasPointer = RuntimeXrRayPickReader.TryReadPointerPose(
            xrInputDevices,
            out Vector3 pointerLocalPosition,
            out Quaternion pointerLocalRotation,
            out bool triggerPressed);

        RuntimeXrRayPick.PressDecision decision =
            RuntimeXrRayPick.ResolvePress(hasPointer, triggerPressed, prevPickTriggerPressed);
        prevPickTriggerPressed = decision.previousPressed;
        if (!decision.pick)
        {
            return false;
        }

        Transform head = GetViewOrHeadTransform();
        if (head == null)
        {
            return false;
        }

        if (!RuntimeXrRayPickReader.TryReadHeadPose(
                xrInputDevices,
                out Vector3 headLocalPosition,
                out Quaternion headLocalRotation))
        {
            return false;
        }

        ray = RuntimeXrRayPick.ResolveWorldRay(
            head.position,
            head.rotation,
            headLocalPosition,
            headLocalRotation,
            pointerLocalPosition,
            pointerLocalRotation);
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

        // InverseTransformPoint は localScale を打ち消すので、local はメッシュ座標
        // （Quad なら ±0.5）であって「メートル」ではない。localScale で割ったり
        // localScale から半幅を作ったりすると、スクリーンを拡大するほど UV がずれ、
        // 範囲判定もほぼ素通しになる。EyePixelToWorldOnScreen（v = (0.5 - local.y) * eyeH）の
        // 逆変換になるよう、メッシュ bounds でそのまま正規化する。
        Vector3 local = screen.InverseTransformPoint(point);
        GetScreenMeshLocalBounds(screen, out Vector3 center, out Vector3 size);
        if (size.x <= 0f || size.y <= 0f)
        {
            return false;
        }

        float x = local.x - center.x;
        float y = local.y - center.y;
        if (Mathf.Abs(x) > size.x * 0.5f || Mathf.Abs(y) > size.y * 0.5f)
        {
            return false;
        }

        uv = new Vector2(x / size.x + 0.5f, 0.5f - y / size.y);
        return true;
    }
}
