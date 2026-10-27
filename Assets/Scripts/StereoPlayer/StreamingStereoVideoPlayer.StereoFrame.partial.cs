using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// F2（2026-10-16、既定 OFF）: 画面の面に、画面の矩形の外だけを覆う見えない枠（深度だけ書く、ColorMask 0・ZWrite On・Queue 1999）を置く。
// 隠れるのは「画面の外に見え、かつ画面の面より奥」のモデルの部分だけ（窓の向こうの物が壁に隠れるのと同じ）。種別は問わない。
// 画面の面より奥の UI（実験のパネル 1.2 m・操作バー 1.2 m）は、頭から面へ写した矩形を穴として抜く。色も alpha も書かないのでパススルーはそのまま見える。
public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    public bool stereoFrameOccluder = false;
    // 帯の幅 = 画面の幅 × これ（3 なら 1 m の画面で縁から 4.2 m、画面の中心の視線から約 78° まで覆う。フィットした係数ではない）。
    [Min(0f)] public float stereoFrameOccluderWidthFraction = 3f;
    // UI の穴の余白（頭の位置のずれと左右の目の差 ±5 mm を覆う）。
    [Min(0f)] public float stereoFrameOccluderUiMarginMeters = 0.03f;

    private GameObject stereoFrameOccluderObject;
    private Mesh stereoFrameOccluderMesh;
    private MeshRenderer stereoFrameOccluderRenderer;
    private Material stereoFrameOccluderMaterial;
    private bool stereoFrameShaderMissingLogged;
    private readonly List<Canvas> stereoFrameCanvases = new List<Canvas>(16);
    private float stereoFrameCanvasesAt = -10f;
    private readonly Vector3[] stereoFrameCorners = new Vector3[4];
    private readonly List<Rect> stereoFrameBands = new List<Rect>(4);
    private readonly List<Rect> stereoFrameHoles = new List<Rect>(8);
    private readonly List<int> stereoFrameHoleIds = new List<int>(8);
    private readonly List<int> stereoFrameLastHoleIds = new List<int>(8);
    private readonly List<Rect> stereoFrameRects = new List<Rect>(32);
    private readonly List<Rect> stereoFrameWork = new List<Rect>(32);
    private readonly List<Rect> stereoFrameLastRects = new List<Rect>(32);
    private readonly List<Vector3> stereoFrameVertices = new List<Vector3>(128);
    private readonly List<int> stereoFrameTriangles = new List<int>(192);
    private Vector3 stereoFrameLastCenter;
    private Vector3 stereoFrameLastRight;
    private Vector3 stereoFrameLastUp;
    private Matrix4x4 stereoFrameLastParentMatrix;
    private bool stereoFrameHasMesh;

    // UpdateRuntimePlaybackTick（DisplayModelTick の後）と UpdateBundlePickerTick（消すだけ）。
    private void UpdateStereoFrameOccluder()
    {
        if (!stereoFrameOccluder || !displayModel || !metaLoaded || isNormalMode || bundlePickerActive ||
            !TryGetScreenFrame(out Vector3 center, out _, out _, out Vector2 size) ||
            !TryGetPinholeBasis(leftScreen != null ? leftScreen : rightScreen, out _, out Quaternion camRotation) ||
            !EnsureStereoFrameOccluderObject())
        {
            SetStereoFrameOccluderActive(false);
            return;
        }

        Vector3 right = camRotation * Vector3.right;   // 板の向き（FixFacingIfNeeded の反転）に依らない
        Vector3 up = camRotation * Vector3.up;
        Vector3 forward = camRotation * Vector3.forward;
        float w = size.x;
        float h = size.y;
        float band = Mathf.Max(0f, stereoFrameOccluderWidthFraction) * w;
        StereoFrameOccluderGeometry.BuildBands(w, h, band, stereoFrameBands);
        CollectStereoFrameUiHoles(center, right, up, forward, w, h);

        stereoFrameRects.Clear();
        stereoFrameRects.AddRange(stereoFrameBands);
        for (int i = 0; i < stereoFrameHoles.Count; i++)
        {
            stereoFrameWork.Clear();
            for (int r = 0; r < stereoFrameRects.Count; r++)
            {
                StereoFrameOccluderGeometry.Subtract(stereoFrameRects[r], stereoFrameHoles[i], stereoFrameWork);
            }

            stereoFrameRects.Clear();
            stereoFrameRects.AddRange(stereoFrameWork);
        }

        if (!stereoFrameHasMesh || StereoFrameChanged(center, right, up))
        {
            bool holesChanged = !stereoFrameHasMesh || !SameIds(stereoFrameHoleIds, stereoFrameLastHoleIds) ||
                                stereoFrameLastRects.Count != stereoFrameRects.Count;
            StereoFrameOccluderGeometry.BuildMesh(stereoFrameRects, center, right, up, transform.worldToLocalMatrix, stereoFrameVertices, stereoFrameTriangles);
            stereoFrameOccluderMesh.Clear();
            stereoFrameOccluderMesh.SetVertices(stereoFrameVertices);
            stereoFrameOccluderMesh.SetTriangles(stereoFrameTriangles, 0);
            stereoFrameOccluderMesh.RecalculateBounds();
            stereoFrameLastRects.Clear();
            stereoFrameLastRects.AddRange(stereoFrameRects);
            stereoFrameLastHoleIds.Clear();
            stereoFrameLastHoleIds.AddRange(stereoFrameHoleIds);
            stereoFrameLastCenter = center;
            stereoFrameLastRight = right;
            stereoFrameLastUp = up;
            stereoFrameLastParentMatrix = transform.localToWorldMatrix;
            stereoFrameHasMesh = true;
            if (holesChanged)
            {
                Debug.Log($"[STEREOFRAME] rects={stereoFrameRects.Count} holes={stereoFrameHoles.Count} names={DescribeStereoFrameHoles()} " +
                          $"screen={w:F3}x{h:F3} band={band:F3} margin={stereoFrameOccluderUiMarginMeters:F3}");
            }
        }

        SetStereoFrameOccluderActive(true);
    }

    private bool EnsureStereoFrameOccluderObject()
    {
        if (stereoFrameOccluderObject != null)
        {
            return true;
        }

        if (stereoFrameOccluderMaterial == null)
        {
            Shader shader = Resources.Load<Shader>("Shaders/StereoFrameDepthOccluder");
            if (shader == null || !shader.isSupported)
            {
                if (!stereoFrameShaderMissingLogged)
                {
                    stereoFrameShaderMissingLogged = true;
                    Debug.LogWarning("[STEREOFRAME] Resources/Shaders/StereoFrameDepthOccluder が無い・使えないので F2 は働かない");
                }

                return false;
            }

            stereoFrameOccluderMaterial = new Material(shader) { name = "StereoFrameDepthOccluder (runtime)", renderQueue = 1999 };
        }

        // プレイヤーの子にする（TrialScene と一緒に消える）。頂点はプレイヤーの局所座標で書く（BuildMesh の worldToLocal）。
        stereoFrameOccluderObject = new GameObject("StereoFrameOccluder_Runtime");
        stereoFrameOccluderObject.transform.SetParent(transform, false);
        stereoFrameOccluderMesh = new Mesh { name = "StereoFrameOccluder" };
        stereoFrameOccluderMesh.MarkDynamic();
        stereoFrameOccluderObject.AddComponent<MeshFilter>().sharedMesh = stereoFrameOccluderMesh;
        stereoFrameOccluderRenderer = stereoFrameOccluderObject.AddComponent<MeshRenderer>();
        stereoFrameOccluderRenderer.sharedMaterial = stereoFrameOccluderMaterial;
        stereoFrameOccluderRenderer.shadowCastingMode = ShadowCastingMode.Off;
        stereoFrameOccluderRenderer.receiveShadows = false;
        stereoFrameOccluderRenderer.lightProbeUsage = LightProbeUsage.Off;
        stereoFrameOccluderRenderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
        stereoFrameOccluderRenderer.allowOcclusionWhenDynamic = false;
        stereoFrameOccluderRenderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
        stereoFrameHasMesh = false;
        stereoFrameOccluderObject.SetActive(false);
        return true;
    }

    private void SetStereoFrameOccluderActive(bool on)
    {
        if (stereoFrameOccluderObject != null && stereoFrameOccluderObject.activeSelf != on)
        {
            stereoFrameOccluderObject.SetActive(on);
        }
    }

    // 画面の面より奥にある world space の root canvas を、今の頭の位置から面へ写した矩形（+余白）を穴にする。板そのものの canvas は除く。
    private void CollectStereoFrameUiHoles(Vector3 center, Vector3 right, Vector3 up, Vector3 forward, float w, float h)
    {
        stereoFrameHoles.Clear();
        stereoFrameHoleIds.Clear();
        Transform head = GetViewOrHeadTransform();
        if (head == null)
        {
            return;
        }

        Vector3 eye = head.position;
        float planeDistance = Vector3.Dot(center - eye, forward);
        if (planeDistance <= 0.01f)
        {
            return;
        }

        if (Time.unscaledTime - stereoFrameCanvasesAt > 1f)
        {
            stereoFrameCanvasesAt = Time.unscaledTime;
            stereoFrameCanvases.Clear();
            Canvas[] all = FindObjectsByType<Canvas>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (int i = 0; i < all.Length; i++)
            {
                Canvas c = all[i];
                if (c != null && c.isRootCanvas && c.renderMode == RenderMode.WorldSpace &&
                    !(leftScreen != null && c.transform.IsChildOf(leftScreen)) && !(rightScreen != null && c.transform.IsChildOf(rightScreen)))
                {
                    stereoFrameCanvases.Add(c);
                }
            }
        }

        float margin = Mathf.Max(0f, stereoFrameOccluderUiMarginMeters);
        for (int i = 0; i < stereoFrameCanvases.Count; i++)
        {
            Canvas canvas = stereoFrameCanvases[i];
            if (canvas == null || !canvas.isActiveAndEnabled || !(canvas.transform is RectTransform rt))
            {
                continue;
            }

            rt.GetWorldCorners(stereoFrameCorners);
            bool behind = false;
            bool projectable = true;
            float xMin = float.MaxValue, xMax = float.MinValue, yMin = float.MaxValue, yMax = float.MinValue;
            for (int c = 0; c < 4; c++)
            {
                Vector3 p = stereoFrameCorners[c];
                if (Vector3.Dot(p - center, forward) > -0.001f)
                {
                    behind = true;   // 面そのものとの比較（頭の位置に依らない。R2-8）
                }

                float along = Vector3.Dot(p - eye, forward);
                if (along <= 0.001f)
                {
                    projectable = false;
                    break;
                }

                Vector3 q = eye + (p - eye) * (planeDistance / along);
                float x = Vector3.Dot(q - center, right);
                float y = Vector3.Dot(q - center, up);
                xMin = Mathf.Min(xMin, x);
                xMax = Mathf.Max(xMax, x);
                yMin = Mathf.Min(yMin, y);
                yMax = Mathf.Max(yMax, y);
            }

            if (!behind || !projectable)
            {
                continue;
            }

            Rect pre = Rect.MinMaxRect(xMin, yMin, xMax, yMax);
            if (StereoFrameOccluderGeometry.IsInsideScreen(pre, w, h))
            {
                continue;
            }

            Rect hole = Rect.MinMaxRect(xMin - margin, yMin - margin, xMax + margin, yMax + margin);
            stereoFrameHoles.Add(StereoFrameOccluderGeometry.ExtendToScreenEdge(hole, w, h, 2f * margin));
            stereoFrameHoleIds.Add(canvas.GetInstanceID());
        }
    }

    private bool StereoFrameChanged(Vector3 center, Vector3 right, Vector3 up)
    {
        if (stereoFrameLastRects.Count != stereoFrameRects.Count || transform.localToWorldMatrix != stereoFrameLastParentMatrix ||
            (center - stereoFrameLastCenter).sqrMagnitude > 1e-10f || (right - stereoFrameLastRight).sqrMagnitude > 1e-10f ||
            (up - stereoFrameLastUp).sqrMagnitude > 1e-10f)
        {
            return true;
        }

        for (int i = 0; i < stereoFrameRects.Count; i++)
        {
            Rect a = stereoFrameRects[i];
            Rect b = stereoFrameLastRects[i];
            if (Mathf.Abs(a.xMin - b.xMin) > 0.0005f || Mathf.Abs(a.xMax - b.xMax) > 0.0005f ||
                Mathf.Abs(a.yMin - b.yMin) > 0.0005f || Mathf.Abs(a.yMax - b.yMax) > 0.0005f)
            {
                return true;
            }
        }

        return false;
    }

    private static bool SameIds(List<int> a, List<int> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++) if (a[i] != b[i]) return false;
        return true;
    }

    private string DescribeStereoFrameHoles()
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < stereoFrameCanvases.Count; i++)
        {
            Canvas c = stereoFrameCanvases[i];
            if (c != null && stereoFrameHoleIds.Contains(c.GetInstanceID()))
            {
                if (sb.Length > 0) sb.Append(',');
                sb.Append(c.name);
            }
        }

        return sb.Length > 0 ? sb.ToString() : "-";
    }

#if UNITY_EDITOR
    internal Renderer StereoFrameOccluderRendererForCapture =>
        stereoFrameOccluderRenderer != null && stereoFrameOccluderRenderer.gameObject.activeInHierarchy ? stereoFrameOccluderRenderer : null;
#endif
}
