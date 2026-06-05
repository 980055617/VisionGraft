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
                if (frameIndex < 0)
                {
                    continue;
                }

                List<object> objects = GetList(frame, "objects");
                if (objects == null)
                {
                    continue;
                }

                Dictionary<uint, AnimalControlPose> byTrack = null;
                for (int o = 0; o < objects.Count; o++)
                {
                    Dictionary<string, object> obj = objects[o] as Dictionary<string, object>;
                    if (obj == null || !StringEquals(GetString(obj, "category"), "animal"))
                    {
                        continue;
                    }

                    uint trackId = GetUInt(obj, "trackId", uint.MaxValue);
                    if (trackId == uint.MaxValue)
                    {
                        continue;
                    }

                    Dictionary<string, object> targets = GetDict(obj, "targets");
                    Dictionary<string, object> rootTarget = GetDict(targets, "root");
                    if (!TryReadVector3(GetList(rootTarget, "position"), 1f, out Vector3 rootCamAbs))
                    {
                        continue;
                    }

                    Vector3[] jointsCamAbs = new Vector3[26];
                    byte[] jointsVis = new byte[26];
                    Dictionary<string, object> chains = GetDict(obj, "chains");
                    if (chains == null)
                    {
                        continue;
                    }

                    TryReadAnimalControlTarget(targets, "withers", rootCamAbs, out bool hasWithersCamAbs, out Vector3 withersCamAbs);
                    TryReadAnimalControlTarget(targets, "headRoot", rootCamAbs, out bool hasHeadRootCamAbs, out Vector3 headRootCamAbs);
                    TryReadAnimalControlTarget(targets, "headTip", rootCamAbs, out bool hasHeadTipCamAbs, out Vector3 headTipCamAbs);
                    TryReadAnimalControlTarget(targets, "tailBase", rootCamAbs, out bool hasTailBaseCamAbs, out Vector3 tailBaseCamAbs);
                    TryReadAnimalControlTarget(targets, "tailTip", rootCamAbs, out bool hasTailTipCamAbs, out Vector3 tailTipCamAbs);
                    TryReadAnimalControlTarget(targets, "forwardHint", rootCamAbs, out bool hasForwardHintCamAbs, out Vector3 forwardHintCamAbs);
                    TryReadAnimalControlTarget(targets, "upHint", rootCamAbs, out bool hasUpHintCamAbs, out Vector3 upHintCamAbs);

                    Vector3[] frontLeftLegChainCamAbs = ReadAnimalControlChain(chains, "frontLeftLeg", rootCamAbs);
                    Vector3[] frontRightLegChainCamAbs = ReadAnimalControlChain(chains, "frontRightLeg", rootCamAbs);
                    Vector3[] rearLeftLegChainCamAbs = ReadAnimalControlChain(chains, "rearLeftLeg", rootCamAbs);
                    Vector3[] rearRightLegChainCamAbs = ReadAnimalControlChain(chains, "rearRightLeg", rootCamAbs);
                    Vector3[] headChainCamAbs = ReadAnimalControlChain(chains, "head", rootCamAbs);
                    Vector3[] tailChainCamAbs = ReadAnimalControlChain(chains, "tail", rootCamAbs);

                    foreach (KeyValuePair<string, object> chainEntry in chains)
                    {
                        Dictionary<string, object> chain = chainEntry.Value as Dictionary<string, object>;
                        List<object> jointIndices = GetList(chain, "jointIndices");
                        List<object> positions = GetList(chain, "positions");
                        if (jointIndices == null || positions == null)
                        {
                            continue;
                        }

                        int count = Mathf.Min(jointIndices.Count, positions.Count);
                        for (int c = 0; c < count; c++)
                        {
                            int jointIndex = GetInt(jointIndices, c, -1);
                            if (jointIndex < 0 || jointIndex >= jointsCamAbs.Length)
                            {
                                continue;
                            }

                            if (!TryReadVector3(positions[c] as List<object>, 1f, out Vector3 position))
                            {
                                continue;
                            }

                            jointsCamAbs[jointIndex] = NormalizeAnimalControlCam(position, rootCamAbs);
                            jointsVis[jointIndex] = 1;
                        }
                    }

                    if (byTrack == null)
                    {
                        byTrack = new Dictionary<uint, AnimalControlPose>();
                        animalControlPosesByFrame[frameIndex] = byTrack;
                    }

                    byTrack[trackId] = new AnimalControlPose
                    {
                        kpCount = (ushort)jointsCamAbs.Length,
                        jointsCamAbs = jointsCamAbs,
                        jointsVis = jointsVis,
                        hasWithersCamAbs = hasWithersCamAbs,
                        withersCamAbs = withersCamAbs,
                        hasHeadRootCamAbs = hasHeadRootCamAbs,
                        headRootCamAbs = headRootCamAbs,
                        hasHeadTipCamAbs = hasHeadTipCamAbs,
                        headTipCamAbs = headTipCamAbs,
                        rootCamAbs = rootCamAbs,
                        hasTailBaseCamAbs = hasTailBaseCamAbs,
                        tailBaseCamAbs = tailBaseCamAbs,
                        hasTailTipCamAbs = hasTailTipCamAbs,
                        tailTipCamAbs = tailTipCamAbs,
                        hasForwardHintCamAbs = hasForwardHintCamAbs,
                        forwardHintCamAbs = forwardHintCamAbs,
                        hasUpHintCamAbs = hasUpHintCamAbs,
                        upHintCamAbs = upHintCamAbs,
                        frontLeftLegChainCamAbs = frontLeftLegChainCamAbs,
                        frontRightLegChainCamAbs = frontRightLegChainCamAbs,
                        rearLeftLegChainCamAbs = rearLeftLegChainCamAbs,
                        rearRightLegChainCamAbs = rearRightLegChainCamAbs,
                        headChainCamAbs = headChainCamAbs,
                        tailChainCamAbs = tailChainCamAbs
                    };
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"Failed to load SVB animal_control_targets sidecar: {ex.Message}");
        }
    }

    private static void TryReadAnimalControlTarget(Dictionary<string, object> targets, string key, Vector3 rootCamAbs, out bool hasPosition, out Vector3 position)
    {
        hasPosition = false;
        position = Vector3.zero;
        Dictionary<string, object> target = GetDict(targets, key);
        if (target == null)
        {
            return;
        }

        hasPosition = TryReadVector3(GetList(target, "position"), 1f, out position);
        if (hasPosition)
        {
            position = NormalizeAnimalControlCam(position, rootCamAbs);
        }
    }

    private static Vector3[] ReadAnimalControlChain(Dictionary<string, object> chains, string key, Vector3 rootCamAbs)
    {
        Dictionary<string, object> chain = GetDict(chains, key);
        List<object> positions = GetList(chain, "positions");
        if (positions == null || positions.Count == 0)
        {
            return null;
        }

        List<Vector3> parsed = new List<Vector3>(positions.Count);
        for (int i = 0; i < positions.Count; i++)
        {
            if (TryReadVector3(positions[i] as List<object>, 1f, out Vector3 point))
            {
                parsed.Add(NormalizeAnimalControlCam(point, rootCamAbs));
            }
        }

        return parsed.Count > 0 ? parsed.ToArray() : null;
    }

    private static Vector3 NormalizeAnimalControlCam(Vector3 value, Vector3 rootCamAbs)
    {
        Vector3 relative = value - rootCamAbs;
        relative.y = -relative.y;
        return rootCamAbs + relative;
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
