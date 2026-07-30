using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    private struct OtherObjectProxy
    {
        public bool hasAnchorCameraXyz;
        public bool hasProxy3d;
        public Vector3 anchorCameraXyz;
        public Vector3 proxyCenter;
        public Vector3 proxySize;
    }

    private readonly Dictionary<int, Dictionary<uint, AnimalControlPose>> animalControlPosesByFrame = new Dictionary<int, Dictionary<uint, AnimalControlPose>>();
    private readonly Dictionary<int, Dictionary<uint, OtherObjectProxy>> otherProxiesByFrame = new Dictionary<int, Dictionary<uint, OtherObjectProxy>>();
    private readonly Dictionary<uint, GameObject> otherProxyBoxesByTrack = new Dictionary<uint, GameObject>();
    private Material otherProxyBoxMaterial;

    private void LoadBundleSidecars(string animalControlTargetsPath, string otherObjectProxiesPath)
    {
        animalControlPosesByFrame.Clear();
        otherProxiesByFrame.Clear();
        LoadAnimalControlTargetsSidecar(animalControlTargetsPath);
        LoadOtherObjectProxiesSidecar(otherObjectProxiesPath);

        Debug.Log($"SVB sidecars loaded: animalControlFrames={animalControlPosesByFrame.Count}, otherProxyFrames={otherProxiesByFrame.Count}");
    }

    private void LoadAnimalControlTargetsSidecar(string path)
    {
        // source/animal_control_targets.json は debug/検証用。runtime 配置・姿勢追従には使用しない。
        // Animal の姿勢追従は meta.bin の skeleton (jointsCam/anchorZ) と SMAL block のみを参照する。
    }

    // source/other_object_proxies.json は debug/検証用。runtime 配置・スケールには使用しない。
    // Else の配置は meta.bin の anchor (u/v + anchorZ) と bbox のみを参照する。
    private void LoadOtherObjectProxiesSidecar(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return;
        }

        try
        {
            object rootObj = MiniJson.Parse(File.ReadAllText(path));
            Dictionary<string, object> root = rootObj as Dictionary<string, object>;
            if (root == null)
            {
                return;
            }

            List<object> frames = GetList(root, "frames");
            if (frames == null)
            {
                return;
            }

            for (int i = 0; i < frames.Count; i++)
            {
                Dictionary<string, object> frame = frames[i] as Dictionary<string, object>;
                if (frame == null)
                {
                    continue;
                }

                int frameIndex = GetInt(frame, "frameIndex", -1);
                uint trackId = GetUInt(frame, "trackId", uint.MaxValue);
                if (frameIndex < 0 || trackId == uint.MaxValue)
                {
                    continue;
                }

                OtherObjectProxy proxy = new OtherObjectProxy();
                proxy.hasAnchorCameraXyz = TryReadVector3(GetList(frame, "anchorCameraXyz"), 1f, out proxy.anchorCameraXyz);

                Dictionary<string, object> proxy3d = GetDict(frame, "proxy3d");
                Vector3 center = Vector3.zero;
                Vector3 size = Vector3.zero;
                proxy.hasProxy3d =
                    TryReadVector3(GetList(proxy3d, "center"), 1f, out center) &&
                    TryReadVector3(GetList(proxy3d, "size"), 1f, out size);
                proxy.proxyCenter = center;
                proxy.proxySize = size;

                if (!proxy.hasAnchorCameraXyz && !proxy.hasProxy3d)
                {
                    continue;
                }

                if (!otherProxiesByFrame.TryGetValue(frameIndex, out Dictionary<uint, OtherObjectProxy> byTrack))
                {
                    byTrack = new Dictionary<uint, OtherObjectProxy>();
                    otherProxiesByFrame[frameIndex] = byTrack;
                }

                byTrack[trackId] = proxy;
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"Failed to load SVB other_object_proxies sidecar: {ex.Message}");
        }
    }

    private void ApplySidecarsToMetaObject(ref MetaObj obj, int frameIndex)
    {
        if (otherProxiesByFrame.TryGetValue(frameIndex, out Dictionary<uint, OtherObjectProxy> proxies) &&
            proxies.TryGetValue(obj.trackId, out OtherObjectProxy proxy))
        {
            obj.hasOtherProxy = true;
            obj.hasOtherProxyCenter = proxy.hasProxy3d;
            obj.hasOtherProxySize = proxy.hasProxy3d;
            obj.otherAnchorCameraXyz = proxy.anchorCameraXyz;
            obj.otherProxyCenter = proxy.hasProxy3d ? proxy.proxyCenter : proxy.anchorCameraXyz;
            obj.otherProxySize = proxy.proxySize;
        }
    }

    private bool TryGetAnimalControlPose(int frameIndex, uint trackId, out AnimalControlPose pose)
    {
        pose = default(AnimalControlPose);
        return animalControlPosesByFrame.TryGetValue(frameIndex, out Dictionary<uint, AnimalControlPose> byTrack) &&
               byTrack.TryGetValue(trackId, out pose);
    }

    // debug 可視化 (showOtherProxyBoxes) 専用。runtime 配置には使わない。
    // sidecar の cameraXyz / proxy3d は units="same_as_depth_npz"、つまり 0=far/1=near の
    // 正規化深度でメートルではないため、この world 変換はスケールも前後関係も正しくない
    // （meta.bin の anchorZ は DecodeAnchorDepthMetersFromBundle で反転処理してから使う）。
    // sidecar 側のカメラは 1280x720 だが manifest の eye は 1280x640 で fy が異なるため、
    // 縦位置も一致しない。あくまで「sidecar に何が入っているか」を見るための箱として扱う。
    private bool TryOtherProxyWorld(MetaObj obj, Transform screen, out Vector3 centerWorld, out Vector3 sizeMeters)
    {
        centerWorld = Vector3.zero;
        sizeMeters = Vector3.zero;
        if (!obj.hasOtherProxy)
        {
            return false;
        }

        if (!TryGetPinholeBasis(screen, out Vector3 camOrigin, out Quaternion camRotation))
        {
            return false;
        }

        Vector3 center = obj.hasOtherProxyCenter ? obj.otherProxyCenter : obj.otherAnchorCameraXyz;
        centerWorld = camOrigin + (camRotation * center);
        sizeMeters = obj.hasOtherProxySize ? AbsVector(obj.otherProxySize) : Vector3.zero;
        return true;
    }

    private void ApplyOtherProxyBoxesForFrame(List<MetaObj> objs, int frame)
    {
        if (!showOtherProxyBoxes)
        {
            return;
        }

        HashSet<uint> visible = new HashSet<uint>();
        for (int i = 0; i < objs.Count; i++)
        {
            MetaObj obj = objs[i];
            if (!IsCategoryOther(obj.categoryId) || !obj.hasOtherProxy)
            {
                continue;
            }

            if (!ResolveAnchorToScreen(obj.anchorU, out Transform screen, out _, out _))
            {
                continue;
            }

            if (!TryOtherProxyWorld(obj, screen, out Vector3 centerWorld, out Vector3 sizeMeters))
            {
                continue;
            }

            if (sizeMeters.sqrMagnitude < 0.000001f)
            {
                sizeMeters = Vector3.one * 0.05f;
            }

            GameObject box = GetOrCreateOtherProxyBox(obj.trackId);
            SceneObjectWriter.ApplyActive(box, true);
            TransformWriter.ApplyWorldPoseAndScale(
                box.transform,
                centerWorld,
                GetPinholeBasisRotation(screen),
                sizeMeters);
            visible.Add(obj.trackId);
        }

        foreach (KeyValuePair<uint, GameObject> kv in otherProxyBoxesByTrack)
        {
            if (kv.Value != null && !visible.Contains(kv.Key))
            {
                SceneObjectWriter.ApplyActive(kv.Value, false);
            }
        }
    }

    private GameObject GetOrCreateOtherProxyBox(uint trackId)
    {
        if (otherProxyBoxesByTrack.TryGetValue(trackId, out GameObject existing) && existing != null)
        {
            return existing;
        }

        GameObject box = OtherProxyBoxFactory.Create(trackId, GetOtherProxyBoxMaterial());

        otherProxyBoxesByTrack[trackId] = box;
        return box;
    }

    private Material GetOtherProxyBoxMaterial()
    {
        if (otherProxyBoxMaterial != null)
        {
            return otherProxyBoxMaterial;
        }

        Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (shader == null)
        {
            shader = Shader.Find("Unlit/Color");
        }

        if (shader == null)
        {
            shader = Shader.Find("Standard");
        }
        if (shader == null)
        {
            return null;
        }

        otherProxyBoxMaterial = new Material(shader);
        if (otherProxyBoxMaterial != null)
        {
            if (otherProxyBoxMaterial.HasProperty("_BaseColor"))
            {
                otherProxyBoxMaterial.SetColor("_BaseColor", otherProxyBoxColor);
            }
            if (otherProxyBoxMaterial.HasProperty("_Color"))
            {
                otherProxyBoxMaterial.SetColor("_Color", otherProxyBoxColor);
            }
            if (otherProxyBoxMaterial.HasProperty("_Surface"))
            {
                otherProxyBoxMaterial.SetFloat("_Surface", 1f);
            }
            if (otherProxyBoxMaterial.HasProperty("_SrcBlend"))
            {
                otherProxyBoxMaterial.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            }
            if (otherProxyBoxMaterial.HasProperty("_DstBlend"))
            {
                otherProxyBoxMaterial.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            }
            if (otherProxyBoxMaterial.HasProperty("_ZWrite"))
            {
                otherProxyBoxMaterial.SetFloat("_ZWrite", 0f);
            }
            otherProxyBoxMaterial.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        }

        return otherProxyBoxMaterial;
    }

    private bool IsCategoryPerson(byte categoryId) => IsCategoryNamed(categoryId, "person") || IsCategoryNamed(categoryId, "human");
    private bool IsCategoryAnimal(byte categoryId) => IsCategoryNamed(categoryId, "animal");
    private bool IsCategoryOther(byte categoryId) => IsCategoryNamed(categoryId, "other") || IsCategoryNamed(categoryId, "else");

    private bool IsCategoryNamed(byte categoryId, string expected)
    {
        if (!categoryNames.TryGetValue(categoryId, out string name) || string.IsNullOrEmpty(name))
        {
            return false;
        }

        return StringEquals(name, expected);
    }

    private static bool StringEquals(string a, string b)
    {
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private static Vector3 AbsVector(Vector3 v)
    {
        return new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));
    }

    private static Dictionary<string, object> GetDict(Dictionary<string, object> dict, string key)
    {
        if (dict == null || key == null || !dict.TryGetValue(key, out object value))
        {
            return null;
        }

        return value as Dictionary<string, object>;
    }

    private static List<object> GetList(Dictionary<string, object> dict, string key)
    {
        if (dict == null || key == null || !dict.TryGetValue(key, out object value))
        {
            return null;
        }

        return value as List<object>;
    }

    private static string GetString(Dictionary<string, object> dict, string key)
    {
        if (dict == null || key == null || !dict.TryGetValue(key, out object value) || value == null)
        {
            return null;
        }

        return value as string ?? Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private static int GetInt(Dictionary<string, object> dict, string key, int fallback)
    {
        if (dict == null || key == null || !dict.TryGetValue(key, out object value))
        {
            return fallback;
        }

        return ToInt(value, fallback);
    }

    private static int GetInt(List<object> list, int index, int fallback)
    {
        if (list == null || index < 0 || index >= list.Count)
        {
            return fallback;
        }

        return ToInt(list[index], fallback);
    }

    private static uint GetUInt(Dictionary<string, object> dict, string key, uint fallback)
    {
        int value = GetInt(dict, key, -1);
        return value < 0 ? fallback : (uint)value;
    }

    private static int ToInt(object value, int fallback)
    {
        if (value is long l)
        {
            return (int)l;
        }
        if (value is double d)
        {
            return (int)d;
        }
        if (value is float f)
        {
            return (int)f;
        }
        if (value is int i)
        {
            return i;
        }

        return int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : fallback;
    }

    private static float GetFloat(List<object> list, int index)
    {
        if (list == null || index < 0 || index >= list.Count)
        {
            return 0f;
        }

        object value = list[index];
        if (value is double d)
        {
            return (float)d;
        }
        if (value is long l)
        {
            return l;
        }
        if (value is float f)
        {
            return f;
        }
        if (value is int i)
        {
            return i;
        }

        return float.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)
            ? parsed
            : 0f;
    }

    private static bool TryReadVector3(List<object> list, float unitScale, out Vector3 value)
    {
        value = Vector3.zero;
        if (list == null || list.Count < 3)
        {
            return false;
        }

        value = new Vector3(
            GetFloat(list, 0) * unitScale,
            GetFloat(list, 1) * unitScale,
            GetFloat(list, 2) * unitScale);
        return true;
    }

}
