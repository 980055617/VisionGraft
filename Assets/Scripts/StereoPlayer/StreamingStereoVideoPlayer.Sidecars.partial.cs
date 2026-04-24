using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    private struct SidecarSkeleton
    {
        public ushort kpCount;
        public Vector3[] jointsCam;
        public byte[] jointsVis;
        public Vector3 rootCam;
    }

    private struct OtherObjectProxy
    {
        public bool hasAnchorCameraXyz;
        public bool hasProxy3d;
        public Vector3 anchorCameraXyz;
        public Vector3 proxyCenter;
        public Vector3 proxySize;
    }

    private readonly Dictionary<int, Dictionary<uint, SidecarSkeleton>> sidecarSkeletonsByFrame = new Dictionary<int, Dictionary<uint, SidecarSkeleton>>();
    private readonly Dictionary<int, Dictionary<uint, OtherObjectProxy>> otherProxiesByFrame = new Dictionary<int, Dictionary<uint, OtherObjectProxy>>();
    private readonly Dictionary<uint, GameObject> otherProxyBoxesByTrack = new Dictionary<uint, GameObject>();
    private Material otherProxyBoxMaterial;

    private void LoadBundleSidecars(string keypoints3dPath, string otherObjectProxiesPath)
    {
        sidecarSkeletonsByFrame.Clear();
        otherProxiesByFrame.Clear();

        LoadKeypoints3dSidecar(keypoints3dPath);
        LoadOtherObjectProxiesSidecar(otherObjectProxiesPath);

        Debug.Log($"SVB sidecars loaded: keypointFrames={sidecarSkeletonsByFrame.Count}, otherProxyFrames={otherProxiesByFrame.Count}");
    }

    private void LoadKeypoints3dSidecar(string path)
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

            ReadSidecarCategories(root);

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
                if (frameIndex < 0)
                {
                    continue;
                }

                List<object> objects = GetList(frame, "objects");
                if (objects == null)
                {
                    continue;
                }

                Dictionary<uint, SidecarSkeleton> byTrack = null;
                for (int o = 0; o < objects.Count; o++)
                {
                    Dictionary<string, object> obj = objects[o] as Dictionary<string, object>;
                    if (obj == null)
                    {
                        continue;
                    }

                    uint trackId = GetUInt(obj, "trackId", uint.MaxValue);
                    if (trackId == uint.MaxValue)
                    {
                        continue;
                    }

                    string category = GetString(obj, "category");
                    Dictionary<string, object> pose = GetDict(obj, "pose");
                    List<object> keypoints = pose != null ? GetList(pose, "keypoints3d") : null;
                    if (keypoints == null || keypoints.Count == 0)
                    {
                        continue;
                    }

                    float unitScale = ResolveSidecarUnitScale(pose);
                    Vector3[] joints = new Vector3[keypoints.Count];
                    byte[] vis = new byte[keypoints.Count];
                    for (int k = 0; k < keypoints.Count; k++)
                    {
                        List<object> tuple = keypoints[k] as List<object>;
                        if (tuple == null || tuple.Count < 3)
                        {
                            joints[k] = Vector3.zero;
                            vis[k] = 0;
                            continue;
                        }

                        joints[k] = new Vector3(
                            GetFloat(tuple, 0) * unitScale,
                            GetFloat(tuple, 1) * unitScale,
                            GetFloat(tuple, 2) * unitScale);
                        vis[k] = tuple.Count >= 4 && GetFloat(tuple, 3) <= 0f ? (byte)0 : (byte)1;
                    }

                    Vector3 rootOffset = ResolveSidecarRootOffset(category, pose, joints, unitScale);
                    for (int k = 0; k < joints.Length; k++)
                    {
                        joints[k] -= rootOffset;
                    }

                    if (byTrack == null)
                    {
                        byTrack = new Dictionary<uint, SidecarSkeleton>();
                        sidecarSkeletonsByFrame[frameIndex] = byTrack;
                    }

                    byTrack[trackId] = new SidecarSkeleton
                    {
                        kpCount = (ushort)Mathf.Min(ushort.MaxValue, joints.Length),
                        jointsCam = joints,
                        jointsVis = vis,
                        rootCam = rootOffset
                    };
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"Failed to load SVB keypoints3d sidecar: {ex.Message}");
        }
    }

    private void ReadSidecarCategories(Dictionary<string, object> root)
    {
        Dictionary<string, object> categories = GetDict(root, "categories");
        if (categories == null)
        {
            return;
        }

        foreach (KeyValuePair<string, object> kv in categories)
        {
            Dictionary<string, object> cat = kv.Value as Dictionary<string, object>;
            if (cat == null)
            {
                continue;
            }

            byte id = (byte)Mathf.Clamp(GetInt(cat, "id", -1), 0, byte.MaxValue);
            if (id == 0 && GetInt(cat, "id", -1) < 0)
            {
                continue;
            }

            categoryNames[id] = kv.Key;

            List<object> keypoints = GetList(cat, "keypoints");
            if (keypoints != null)
            {
                categoryKpCounts[id] = (ushort)Mathf.Min(ushort.MaxValue, keypoints.Count);
            }
        }
    }

    private float ResolveSidecarUnitScale(Dictionary<string, object> pose)
    {
        string units = pose != null ? GetString(pose, "units") : null;
        if (StringEquals(units, "millimeters"))
        {
            return 0.001f;
        }

        return 1f;
    }

    private Vector3 ResolveSidecarRootOffset(string category, Dictionary<string, object> pose, Vector3[] joints, float unitScale)
    {
        if (joints == null || joints.Length == 0)
        {
            return Vector3.zero;
        }

        if (pose != null && TryReadVector3(GetList(pose, "skeletonRoot3d"), unitScale, out Vector3 skeletonRoot3d))
        {
            return skeletonRoot3d;
        }

        if (StringEquals(category, "person"))
        {
            return joints[0];
        }

        if (StringEquals(category, "animal"))
        {
            List<object> rootIndices = pose != null ? GetList(pose, "rootJointIndices") : null;
            if (rootIndices != null && rootIndices.Count >= 2)
            {
                int idxA = GetInt(rootIndices, 0, -1);
                int idxB = GetInt(rootIndices, 1, -1);
                if (idxA >= 0 && idxB >= 0 && idxA < joints.Length && idxB < joints.Length)
                {
                    return (joints[idxA] + joints[idxB]) * 0.5f;
                }
            }

            if (joints.Length > 7)
            {
                return (joints[6] + joints[7]) * 0.5f;
            }
        }

        return Vector3.zero;
    }

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
        if (sidecarSkeletonsByFrame.TryGetValue(frameIndex, out Dictionary<uint, SidecarSkeleton> skeletons) &&
            skeletons.TryGetValue(obj.trackId, out SidecarSkeleton skeleton))
        {
            obj.hasSkeleton = skeleton.kpCount > 0;
            obj.skeletonKpCount = skeleton.kpCount;
            obj.jointsCam = skeleton.jointsCam;
            obj.jointsVis = skeleton.jointsVis;
            obj.hasSkeletonRootCam = true;
            obj.skeletonRootCam = skeleton.rootCam;
        }
        else if (IsCategoryPerson(obj.categoryId) || IsCategoryAnimal(obj.categoryId))
        {
            obj.hasSkeleton = false;
            obj.skeletonKpCount = 0;
            obj.jointsCam = null;
            obj.jointsVis = null;
            obj.hasSkeletonRootCam = false;
            obj.skeletonRootCam = Vector3.zero;
        }

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
            box.SetActive(true);
            box.transform.SetPositionAndRotation(centerWorld, GetPinholeBasisRotation(screen));
            box.transform.localScale = sizeMeters;
            visible.Add(obj.trackId);
        }

        foreach (KeyValuePair<uint, GameObject> kv in otherProxyBoxesByTrack)
        {
            if (kv.Value != null && !visible.Contains(kv.Key))
            {
                kv.Value.SetActive(false);
            }
        }
    }

    private GameObject GetOrCreateOtherProxyBox(uint trackId)
    {
        if (otherProxyBoxesByTrack.TryGetValue(trackId, out GameObject existing) && existing != null)
        {
            return existing;
        }

        GameObject box = GameObject.CreatePrimitive(PrimitiveType.Cube);
        box.name = $"OtherProxy_{trackId}";
        Collider collider = box.GetComponent<Collider>();
        if (collider != null)
        {
            Destroy(collider);
        }

        Renderer renderer = box.GetComponent<Renderer>();
        if (renderer != null)
        {
            Material material = GetOtherProxyBoxMaterial();
            if (material != null)
            {
                renderer.material = material;
            }
        }

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

    private static class MiniJson
    {
        public static object Parse(string json)
        {
            if (string.IsNullOrEmpty(json))
            {
                return null;
            }

            Parser parser = new Parser(json);
            return parser.ParseValue();
        }

        private sealed class Parser
        {
            private readonly string json;
            private int index;

            public Parser(string json)
            {
                this.json = json;
            }

            public object ParseValue()
            {
                SkipWhitespace();
                if (index >= json.Length)
                {
                    return null;
                }

                char c = json[index];
                if (c == '{') return ParseObject();
                if (c == '[') return ParseArray();
                if (c == '"') return ParseString();
                if (c == '-' || char.IsDigit(c)) return ParseNumber();
                if (Match("true")) return true;
                if (Match("false")) return false;
                if (Match("null")) return null;
                return null;
            }

            private Dictionary<string, object> ParseObject()
            {
                Dictionary<string, object> obj = new Dictionary<string, object>();
                index++;
                while (true)
                {
                    SkipWhitespace();
                    if (index >= json.Length)
                    {
                        return obj;
                    }
                    if (json[index] == '}')
                    {
                        index++;
                        return obj;
                    }

                    string key = ParseString();
                    SkipWhitespace();
                    if (index < json.Length && json[index] == ':')
                    {
                        index++;
                    }

                    obj[key] = ParseValue();
                    SkipWhitespace();
                    if (index < json.Length && json[index] == ',')
                    {
                        index++;
                        continue;
                    }
                    if (index < json.Length && json[index] == '}')
                    {
                        index++;
                        return obj;
                    }
                }
            }

            private List<object> ParseArray()
            {
                List<object> array = new List<object>();
                index++;
                while (true)
                {
                    SkipWhitespace();
                    if (index >= json.Length)
                    {
                        return array;
                    }
                    if (json[index] == ']')
                    {
                        index++;
                        return array;
                    }

                    array.Add(ParseValue());
                    SkipWhitespace();
                    if (index < json.Length && json[index] == ',')
                    {
                        index++;
                        continue;
                    }
                    if (index < json.Length && json[index] == ']')
                    {
                        index++;
                        return array;
                    }
                }
            }

            private string ParseString()
            {
                if (index >= json.Length || json[index] != '"')
                {
                    return string.Empty;
                }

                index++;
                StringBuilder sb = new StringBuilder();
                while (index < json.Length)
                {
                    char c = json[index++];
                    if (c == '"')
                    {
                        break;
                    }
                    if (c != '\\' || index >= json.Length)
                    {
                        sb.Append(c);
                        continue;
                    }

                    char esc = json[index++];
                    if (esc == '"' || esc == '\\' || esc == '/') sb.Append(esc);
                    else if (esc == 'b') sb.Append('\b');
                    else if (esc == 'f') sb.Append('\f');
                    else if (esc == 'n') sb.Append('\n');
                    else if (esc == 'r') sb.Append('\r');
                    else if (esc == 't') sb.Append('\t');
                    else if (esc == 'u' && index + 4 <= json.Length)
                    {
                        string hex = json.Substring(index, 4);
                        if (ushort.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort code))
                        {
                            sb.Append((char)code);
                        }
                        index += 4;
                    }
                }

                return sb.ToString();
            }

            private object ParseNumber()
            {
                int start = index;
                if (json[index] == '-')
                {
                    index++;
                }
                while (index < json.Length && char.IsDigit(json[index]))
                {
                    index++;
                }

                bool isFloat = false;
                if (index < json.Length && json[index] == '.')
                {
                    isFloat = true;
                    index++;
                    while (index < json.Length && char.IsDigit(json[index]))
                    {
                        index++;
                    }
                }

                if (index < json.Length && (json[index] == 'e' || json[index] == 'E'))
                {
                    isFloat = true;
                    index++;
                    if (index < json.Length && (json[index] == '+' || json[index] == '-'))
                    {
                        index++;
                    }
                    while (index < json.Length && char.IsDigit(json[index]))
                    {
                        index++;
                    }
                }

                string token = json.Substring(start, index - start);
                if (!isFloat && long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out long longValue))
                {
                    return longValue;
                }

                if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double doubleValue))
                {
                    return doubleValue;
                }

                return 0L;
            }

            private bool Match(string token)
            {
                if (index + token.Length > json.Length)
                {
                    return false;
                }

                for (int i = 0; i < token.Length; i++)
                {
                    if (json[index + i] != token[i])
                    {
                        return false;
                    }
                }

                index += token.Length;
                return true;
            }

            private void SkipWhitespace()
            {
                while (index < json.Length && char.IsWhiteSpace(json[index]))
                {
                    index++;
                }
            }
        }
    }
}
