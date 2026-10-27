using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

// 左右の目の位置からの撮影（2026-10-16、-captureStereo）。S は片目の絵では違いが出ないので、目の位置から画面（窓）越しに 2 枚撮る。
// 窓の射影（画面の矩形 + 余白を通す off-axis）なので画面の面の視差は 0、1 画素 = 映像の 1 画素。
//   -captureStereo true  -captureStereoIpdMm 63（省略時はプレイヤーの IPD）  -captureStereoMarginPx 160  -captureStereoMarkers true（-captureViews の補助カメラに Zv・popout・画面の枠の印）
// 出力: fNNNNN_stereoL.png・_stereoR.png・_stereoSBS.png（L|R、平行法）・_stereoAnaglyph.png（赤 = 左の輝度、緑青 = 右）。
public static partial class BatchPlaybackLogger
{
    private const string KeyCaptureStereo = "BatchPlaybackLogger.CaptureStereo";
    private const string KeyCaptureStereoIpdMm = "BatchPlaybackLogger.CaptureStereoIpdMm";
    private const string KeyCaptureStereoMarginPx = "BatchPlaybackLogger.CaptureStereoMarginPx";
    private const string KeyCaptureStereoMarkers = "BatchPlaybackLogger.CaptureStereoMarkers";
    private static readonly List<Material> stereoMarkerMaterials = new List<Material>();

    internal static class StereoCaptureMath
    {
        // 目の camera 空間（+z 前）で、画面の中心 centerCam のまわり半幅 halfW・半高 halfH の窓を通す射影（OpenGL 形式）。
        public static Matrix4x4 WindowFrustum(Vector3 centerCam, float halfW, float halfH, float near, float far)
        {
            float d = centerCam.z;
            return Matrix4x4.Frustum((centerCam.x - halfW) * near / d, (centerCam.x + halfW) * near / d,
                (centerCam.y - halfH) * near / d, (centerCam.y + halfH) * near / d, near, far);
        }

        // 同じ窓での点の画素（左上原点 = PNG の向き）。
        public static Vector2 WindowPixel(Vector3 pointCam, Vector3 centerCam, float halfW, float halfH, int outW, int outH)
        {
            float d = centerCam.z;
            float x = pointCam.x * d / pointCam.z;
            float y = pointCam.y * d / pointCam.z;
            return new Vector2((x - (centerCam.x - halfW)) / (2f * halfW) * outW, ((centerCam.y + halfH) - y) / (2f * halfH) * outH);
        }
    }

    private static void StoreStereoCaptureArgs(bool stereo, float ipdMm, int marginPx, bool markers)
    {
        SessionState.SetBool(KeyCaptureStereo, stereo);
        SessionState.SetFloat(KeyCaptureStereoIpdMm, Mathf.Max(0f, ipdMm));
        SessionState.SetInt(KeyCaptureStereoMarginPx, Mathf.Clamp(marginPx, 0, 1024));
        SessionState.SetBool(KeyCaptureStereoMarkers, markers);
        if (stereo || markers)
        {
            Debug.Log($"[STEREOCAP] args stereo={stereo} ipdMm={ipdMm} marginPx={marginPx} markers={markers}");
        }
    }

    private static Renderer FindStereoFrameOccluderForCapture()
    {
        var player = UnityEngine.Object.FindFirstObjectByType<StreamingStereoVideoPlayer>();
        return player != null ? player.StereoFrameOccluderRendererForCapture : null;
    }

    private static void CaptureStereoPair(Camera mainCam, string dir, long frame)
    {
        if (!SessionState.GetBool(KeyCaptureStereo, false) || mainCam == null)
        {
            return;
        }

        var player = UnityEngine.Object.FindFirstObjectByType<StreamingStereoVideoPlayer>();
        if (player == null || !player.TryGetStereoCaptureFrame(out Vector3 center, out Vector3 right, out Vector3 up, out Vector3 forward, out Vector2 size,
                out Renderer leftPlate, out Renderer rightPlate, out float playerIpd, out string ipdSource, out int eyeW, out int eyeH))
        {
            Debug.Log($"[STEREOCAP] frame={frame} abort reason=noFrame");
            return;
        }

        float baselineDot = Vector3.Dot(right, mainCam.transform.right);
        if (baselineDot < 0.99f)
        {
            Debug.Log($"[STEREOCAP] frame={frame} abort baselineDot={baselineDot:F4}");
            return;
        }

        float argIpdMm = SessionState.GetFloat(KeyCaptureStereoIpdMm, 0f);
        float e = argIpdMm > 0f ? argIpdMm * 0.001f : playerIpd;
        int margin = SessionState.GetInt(KeyCaptureStereoMarginPx, 160);
        float pitch = size.x / eyeW;
        float halfW = size.x * 0.5f + margin * pitch;
        float halfH = size.y * 0.5f + margin * pitch;
        int outW = eyeW + 2 * margin;
        int outH = eyeH + 2 * margin;
        Quaternion rot = Quaternion.LookRotation(forward, up);
        Vector3 centerEye = mainCam.transform.position;
        Vector3 eyeL = centerEye - right * (0.5f * e);
        Vector3 eyeR = centerEye + right * (0.5f * e);

        var hidden = new List<Renderer>();
        Transform rig = mainCam.transform.root;
        if (rig != null)
        {
            foreach (Renderer r in rig.GetComponentsInChildren<Renderer>())
            {
                if (r.enabled && r != leftPlate && r != rightPlate) { r.enabled = false; hidden.Add(r); }
            }
        }

        Texture2D texL = null;
        Texture2D texR = null;
        try
        {
            // 2 枚の板が重なって両方描かれる（バッチは片目のカメラで、目ごとに捨てるのは stereo の変種だけ。PerEyeStereoVideoURP.shader:80-88）ので、もう片方の板を隠す。
            // 単眼条件（右の板が無い）は 1 枚を両目で見るので隠さない。
            texL = RenderStereoEye(mainCam, eyeL, rot, center, halfW, halfH, outW, outH, rightPlate);
            texR = RenderStereoEye(mainCam, eyeR, rot, center, halfW, halfH, outW, outH, rightPlate != null ? leftPlate : null);
            Directory.CreateDirectory(dir);
            string baseName = Path.Combine(dir, $"f{frame:D5}_stereo");
            File.WriteAllBytes(baseName + "L.png", texL.EncodeToPNG());
            File.WriteAllBytes(baseName + "R.png", texR.EncodeToPNG());
            WriteStereoComposites(texL, texR, baseName);
            Vector3 cL = Quaternion.Inverse(rot) * (center - eyeL);
            Debug.Log($"[STEREOCAP] frame={frame} ipdMm={e * 1000f:F1} ipdSrc={(argIpdMm > 0f ? "arg" : ipdSource)} eyeL={eyeL:F4} eyeR={eyeR:F4} " +
                      $"planeDist={cL.z:F4} window={2f * halfW:F4}x{2f * halfH:F4} pitchMm={pitch * 1000f:F5} marginPx={margin} out={outW}x{outH} -> {baseName}*.png");
        }
        finally
        {
            if (texL != null) UnityEngine.Object.DestroyImmediate(texL);
            if (texR != null) UnityEngine.Object.DestroyImmediate(texR);
            foreach (Renderer r in hidden)
            {
                if (r != null) r.enabled = true;
            }
        }
    }

    private static Texture2D RenderStereoEye(Camera mainCam, Vector3 eye, Quaternion rot, Vector3 center, float halfW, float halfH,
        int outW, int outH, Renderer hidePlate)
    {
        var go = new GameObject("StereoCapCamera");
        RenderTexture rt = null;
        RenderTexture prevActive = RenderTexture.active;
        bool restorePlate = false;
        try
        {
            Camera cam = go.AddComponent<Camera>();
            cam.CopyFrom(mainCam);
            cam.enabled = false;
            cam.stereoTargetEye = StereoTargetEyeMask.None;
            cam.transform.SetPositionAndRotation(eye, rot);
            Vector3 c = Quaternion.Inverse(rot) * (center - eye);
            float near = Mathf.Max(0.01f, mainCam.nearClipPlane);
            cam.nearClipPlane = near;
            cam.farClipPlane = 100f;
            cam.projectionMatrix = StereoCaptureMath.WindowFrustum(c, halfW, halfH, near, 100f);
            if (hidePlate != null && !hidePlate.forceRenderingOff)
            {
                hidePlate.forceRenderingOff = true;
                restorePlate = true;
            }

            rt = new RenderTexture(outW, outH, 24) { antiAliasing = 8 };
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(outW, outH, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, outW, outH), 0, 0);
            tex.Apply();
            cam.targetTexture = null;
            return tex;
        }
        finally
        {
            RenderTexture.active = prevActive;
            if (restorePlate) hidePlate.forceRenderingOff = false;
            if (rt != null)
            {
                rt.Release();
                UnityEngine.Object.DestroyImmediate(rt);
            }

            UnityEngine.Object.DestroyImmediate(go);
        }
    }

    private static void WriteStereoComposites(Texture2D l, Texture2D r, string baseName)
    {
        int w = l.width;
        int h = l.height;
        Color32[] pl = l.GetPixels32();
        Color32[] pr = r.GetPixels32();
        var sbs = new Texture2D(2 * w, h, TextureFormat.RGB24, false);
        sbs.SetPixels32(0, 0, w, h, pl);
        sbs.SetPixels32(w, 0, w, h, pr);
        sbs.Apply();
        File.WriteAllBytes(baseName + "SBS.png", sbs.EncodeToPNG());
        UnityEngine.Object.DestroyImmediate(sbs);
        var ana = new Color32[pl.Length];
        for (int i = 0; i < pl.Length; i++)
        {
            byte luma = (byte)Mathf.Clamp(Mathf.RoundToInt(0.299f * pl[i].r + 0.587f * pl[i].g + 0.114f * pl[i].b), 0, 255);
            ana[i] = new Color32(luma, pr[i].g, pr[i].b, 255);
        }

        var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
        tex.SetPixels32(ana);
        tex.Apply();
        File.WriteAllBytes(baseName + "Anaglyph.png", tex.EncodeToPNG());
        UnityEngine.Object.DestroyImmediate(tex);
    }

    // 印: 画面の枠（黄、5 mm の棒）、骨格の track（人・動物）ごとの Zv の点（緑）と popout の点（赤、2 cm の球）。親なし・コライダー無しで、撮ったらすぐ消す。
    private static GameObject SpawnStereoCaptureMarkersIfRequested()
    {
        if (!SessionState.GetBool(KeyCaptureStereoMarkers, false))
        {
            return null;
        }

        var player = UnityEngine.Object.FindFirstObjectByType<StreamingStereoVideoPlayer>();
        Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (player == null || shader == null ||
            !player.TryGetStereoCaptureFrame(out Vector3 center, out Vector3 right, out Vector3 up, out Vector3 forward, out Vector2 size,
                out _, out _, out _, out _, out _, out _))
        {
            return null;
        }

        var root = new GameObject("StereoCapMarkers");
        Material frameMat = NewMarkerMaterial(shader, Color.yellow);
        Material zvMat = NewMarkerMaterial(shader, Color.green);
        Material popMat = NewMarkerMaterial(shader, Color.red);
        Quaternion rot = Quaternion.LookRotation(forward, up);
        const float t = 0.005f;
        AddMarker(root, PrimitiveType.Cube, frameMat, center + up * (size.y * 0.5f), rot, new Vector3(size.x, t, t));
        AddMarker(root, PrimitiveType.Cube, frameMat, center - up * (size.y * 0.5f), rot, new Vector3(size.x, t, t));
        AddMarker(root, PrimitiveType.Cube, frameMat, center - right * (size.x * 0.5f), rot, new Vector3(t, size.y, t));
        AddMarker(root, PrimitiveType.Cube, frameMat, center + right * (size.x * 0.5f), rot, new Vector3(t, size.y, t));
        var zv = new List<Vector3>();
        var pop = new List<Vector3>();
        player.CollectStereoCaptureMarkerPoints(zv, pop);
        foreach (Vector3 p in zv) AddMarker(root, PrimitiveType.Sphere, zvMat, p, Quaternion.identity, Vector3.one * 0.02f);
        foreach (Vector3 p in pop) AddMarker(root, PrimitiveType.Sphere, popMat, p, Quaternion.identity, Vector3.one * 0.02f);
        Debug.Log($"[STEREOCAP] markers zv={zv.Count} popout={pop.Count}");
        return root;
    }

    private static Material NewMarkerMaterial(Shader shader, Color color)
    {
        var m = new Material(shader);
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", color);
        stereoMarkerMaterials.Add(m);
        return m;
    }

    private static void AddMarker(GameObject root, PrimitiveType type, Material mat, Vector3 position, Quaternion rotation, Vector3 scale)
    {
        GameObject go = GameObject.CreatePrimitive(type);
        Collider col = go.GetComponent<Collider>();
        if (col != null) UnityEngine.Object.DestroyImmediate(col);
        go.transform.SetParent(root.transform, false);
        go.transform.SetPositionAndRotation(position, rotation);
        go.transform.localScale = scale;
        go.GetComponent<Renderer>().sharedMaterial = mat;
    }

    private static void DestroyStereoCaptureMarkers(GameObject root)
    {
        if (root != null) UnityEngine.Object.DestroyImmediate(root);
        foreach (Material m in stereoMarkerMaterials) if (m != null) UnityEngine.Object.DestroyImmediate(m);
        stereoMarkerMaterials.Clear();
    }
}
