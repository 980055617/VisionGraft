using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

// 骨（関節）が、その骨で動く肌・服の芯の上にあるかの棚卸し（2026-10-27、ユーザー「他にもそういう問題モデルあるか調べてほしい」）。
// 18_Female_Meshy は Meshy の自動のリグが膝の関節をすねの芯より 14.7 cm 前（ワンピースの真ん中）に置いていて、膝を曲げるとすねが前の点を中心に
// 回り後ろへぶら下がった（Docs/tmp/survey_20261025/README.md「10/27 未明」）。手足の区間（骨 → 鎖の次の骨）ごとに、その骨の重みが 0.9 以上の頂点
// （その区間だけで動く肌）の主軸（PCA）を求めて書く:
//   angle   骨の向きと主軸の角度（0〜90°）
//   head    区間の根の関節（股・膝・肩・肘）から主軸の線までの距離 ÷ 頂点の主軸からの半径（RMS）
//   tail    区間の先の関節から主軸の線までの距離 ÷ 半径
// 基準の姿勢（prefab を置いただけ）で、頂点は皮膚の式（骨の world × bindpose × 頂点、重みで和）で world に置く。
// 人は Humanoid の対応、動物は実行時と同じ役の骨（AnimalPoseApplier.DebugResolveRoleBones、AnimalLegMappingFix の表も付ける）から鎖をたどる。
// バッチ: -executeMethod RigJointAudit.Run [-auditAll true]（既定は実験で使う骨のある 12 体）。出力 Docs/tmp/survey_20261025/rig_audit/rig_joint_audit.json
public static class RigJointAudit
{
    private const string LogTag = "[RIGAUDIT]";
    private const float PureWeight = 0.9f;
    private const int MinVertices = 30;

    private static readonly string[] ExperimentModels =
    {
        "Models/Human/17_Male_Meshy", "Models/Human/18_Female_Meshy", "Models/Human/19_Female_Meshy_Soccer", "Models/Human/20_Female_Meshy_Zombie",
        "Models/Human/00_Female_A_01", "Models/Human/08_Male_A_01",
        "Models/Animal/52_MeshyDog", "Models/Animal/53_MeshyCat", "Models/Animal/54_MeshyWolf", "Models/Animal/55_MeshyFox",
        "Models/Animal/36_LabradorDog", "Models/Animal/39_Lynx",
    };

    private struct Segment
    {
        public string name;
        public Transform bone;
        public Transform child;
    }

    [MenuItem("Tools/VisionGraft/Rig Joint Audit")]
    public static void Run()
    {
        bool all = false;
        string[] args = System.Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "-auditAll")
            {
                all = args[i + 1] == "true";
            }
        }

        var models = new List<string>();
        if (all)
        {
            TextAsset index = AssetDatabase.LoadAssetAtPath<TextAsset>("Assets/Resources/Models/model_index.txt");
            foreach (string raw in index.text.Split('\n'))
            {
                string p = raw.Trim();
                if (p.StartsWith("Models/Human/") || p.StartsWith("Models/Animal/"))
                {
                    models.Add(p);
                }
            }
        }
        else
        {
            models.AddRange(ExperimentModels);
        }

        string outDir = Path.Combine(Directory.GetCurrentDirectory(), "Docs/tmp/survey_20261025/rig_audit");
        Directory.CreateDirectory(outDir);
        var sb = new StringBuilder("[\n");
        bool firstModel = true;
        foreach (string resPath in models)
        {
            string assetPath = "Assets/Resources/" + resPath + ".prefab";
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (prefab == null)
            {
                Debug.LogWarning($"{LogTag} missing {assetPath}");
                continue;
            }

            GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            try
            {
                instance.transform.position = Vector3.zero;
                instance.transform.rotation = Quaternion.identity;
                bool isHuman = resPath.StartsWith("Models/Human/");
                List<Segment> segments = isHuman ? HumanSegments(instance) : AnimalSegments(instance, prefab.name);
                List<(Vector3 p, BoneWeight w, SkinnedMeshRenderer r)> verts = SkinnedRestVertices(instance);
                float height = MeasureHeight(verts);
                var lines = new List<string>();
                foreach (Segment s in segments)
                {
                    string line = AuditSegment(prefab.name, s, verts, height);
                    if (line != null)
                    {
                        lines.Add(line);
                    }
                }

                if (!firstModel)
                {
                    sb.Append(",\n");
                }

                firstModel = false;
                sb.Append($"{{\"prefab\":{Q(prefab.name)},\"human\":{(isHuman ? "true" : "false")},\"height\":{F(height)},\"verts\":{verts.Count},\"segments\":[\n  {string.Join(",\n  ", lines)}]}}");
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"{LogTag} {prefab.name} failed: {ex}");
            }
            finally
            {
                Object.DestroyImmediate(instance);
            }
        }

        sb.Append("\n]\n");
        File.WriteAllText(Path.Combine(outDir, all ? "rig_joint_audit_all.json" : "rig_joint_audit.json"), sb.ToString(), new UTF8Encoding(false));
        Debug.Log($"{LogTag} written {outDir} models={models.Count}");
    }

    private static List<Segment> HumanSegments(GameObject instance)
    {
        var list = new List<Segment>();
        Animator a = instance.GetComponentInChildren<Animator>(true);
        if (a == null || !a.isHuman)
        {
            Debug.LogWarning($"{LogTag} {instance.name}: not humanoid");
            return list;
        }

        void Add(string name, HumanBodyBones b, HumanBodyBones c)
        {
            Transform tb = a.GetBoneTransform(b);
            Transform tc = a.GetBoneTransform(c);
            if (tb != null && tc != null)
            {
                list.Add(new Segment { name = name, bone = tb, child = tc });
            }
        }

        Add("L thigh", HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg);
        Add("L shin", HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot);
        Add("R thigh", HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg);
        Add("R shin", HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot);
        Add("L upper arm", HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm);
        Add("L forearm", HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand);
        Add("R upper arm", HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm);
        Add("R forearm", HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand);
        return list;
    }

    // 動物: 実行時の役（肩・股 = Upper）から、皮膚に使われる子のうち子孫の多い方へたどった鎖の各区間。
    private static List<Segment> AnimalSegments(GameObject instance, string prefabName)
    {
        var list = new List<Segment>();
        Animator animator = instance.GetComponentInChildren<Animator>(true);
        Transform host = animator != null ? animator.transform : instance.transform;
        string key = AnimalLegMappingFix.Key(prefabName);
        if (AnimalLegMappingFix.Table.TryGetValue(key, out string tableSpec))
        {
            AnimalBoneMappingOverride ov = host.gameObject.AddComponent<AnimalBoneMappingOverride>();
            foreach (string pair in tableSpec.Split(';'))
            {
                int eq = pair.IndexOf('=');
                if (eq > 0)
                {
                    AnimalLegMappingFix.TrySetOverrideField(ov, pair.Substring(0, eq).Trim(), pair.Substring(eq + 1).Trim());
                }
            }
        }

        var skinBones = new HashSet<Transform>();
        foreach (SkinnedMeshRenderer smr in instance.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            foreach (Transform b in smr.bones)
            {
                if (b != null)
                {
                    skinBones.Add(b);
                }
            }
        }

        var applier = new AnimalPoseApplier();
        AnimalPoseSettings settings = AnimalPoseSettingsFactory.Create(1f, true, true, 1f, 1f, Vector3.zero, Vector3.zero);
        Dictionary<string, Transform> roles = applier.DebugResolveRoleBones(host, settings);
        foreach (string limb in new[] { "frontL", "frontR", "rearL", "rearR" })
        {
            if (!roles.TryGetValue(limb + "Upper", out Transform upper) || upper == null)
            {
                continue;
            }

            Transform b = upper;
            int guard = 0;
            while (b != null && guard++ < 8)
            {
                Transform next = null;
                int best = -1;
                for (int i = 0; i < b.childCount; i++)
                {
                    Transform c = b.GetChild(i);
                    if (!skinBones.Contains(c) && c.GetComponentsInChildren<Transform>(true).All(t => !skinBones.Contains(t)))
                    {
                        continue;
                    }

                    int n = c.GetComponentsInChildren<Transform>(true).Length;
                    if (n > best)
                    {
                        best = n;
                        next = c;
                    }
                }

                if (next == null)
                {
                    break;
                }

                list.Add(new Segment { name = $"{limb} {b.name}", bone = b, child = next });
                b = next;
            }
        }

        return list;
    }

    private static List<(Vector3 p, BoneWeight w, SkinnedMeshRenderer r)> SkinnedRestVertices(GameObject instance)
    {
        var result = new List<(Vector3, BoneWeight, SkinnedMeshRenderer)>();
        foreach (SkinnedMeshRenderer smr in instance.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            Mesh mesh = smr.sharedMesh;
            if (mesh == null || smr.bones == null || smr.bones.Length == 0)
            {
                continue;
            }

            Vector3[] v = mesh.vertices;
            BoneWeight[] w = mesh.boneWeights;
            Matrix4x4[] bind = mesh.bindposes;
            Transform[] bones = smr.bones;
            var skin = new Matrix4x4[bones.Length];
            for (int i = 0; i < bones.Length; i++)
            {
                skin[i] = bones[i] != null && i < bind.Length ? bones[i].localToWorldMatrix * bind[i] : smr.transform.localToWorldMatrix;
            }

            for (int i = 0; i < v.Length && i < w.Length; i++)
            {
                BoneWeight bw = w[i];
                Vector3 p = skin[bw.boneIndex0].MultiplyPoint3x4(v[i]) * bw.weight0 + skin[bw.boneIndex1].MultiplyPoint3x4(v[i]) * bw.weight1 +
                            skin[bw.boneIndex2].MultiplyPoint3x4(v[i]) * bw.weight2 + skin[bw.boneIndex3].MultiplyPoint3x4(v[i]) * bw.weight3;
                float sum = bw.weight0 + bw.weight1 + bw.weight2 + bw.weight3;
                if (sum > 1e-6f)
                {
                    result.Add((p / sum, bw, smr));
                }
            }
        }

        return result;
    }

    private static float MeasureHeight(List<(Vector3 p, BoneWeight w, SkinnedMeshRenderer r)> verts)
    {
        if (verts.Count == 0)
        {
            return 0f;
        }

        float lo = float.MaxValue;
        float hi = float.MinValue;
        foreach (var v in verts)
        {
            lo = Mathf.Min(lo, v.p.y);
            hi = Mathf.Max(hi, v.p.y);
        }

        return hi - lo;
    }

    private static string AuditSegment(string model, Segment s, List<(Vector3 p, BoneWeight w, SkinnedMeshRenderer r)> verts, float height)
    {
        var pts = new List<Vector3>();
        foreach (var v in verts)
        {
            Transform[] bones = v.r.bones;
            float wb = 0f;
            if (bones[v.w.boneIndex0] == s.bone) { wb += v.w.weight0; }
            if (bones[v.w.boneIndex1] == s.bone) { wb += v.w.weight1; }
            if (bones[v.w.boneIndex2] == s.bone) { wb += v.w.weight2; }
            if (bones[v.w.boneIndex3] == s.bone) { wb += v.w.weight3; }
            if (wb >= PureWeight)
            {
                pts.Add(v.p);
            }
        }

        Vector3 a = s.bone.position;
        Vector3 b = s.child.position;
        float len = Vector3.Distance(a, b);
        if (pts.Count < MinVertices || len < 1e-5f)
        {
            Debug.Log($"{LogTag} {model} {s.name}: skip n={pts.Count} len={len:F4}");
            return $"{{\"name\":{Q(s.name)},\"bone\":{Q(s.bone.name)},\"child\":{Q(s.child.name)},\"n\":{pts.Count},\"skip\":true}}";
        }

        Vector3 c = Vector3.zero;
        foreach (Vector3 p in pts)
        {
            c += p;
        }

        c /= pts.Count;
        // 共分散の最大の固有ベクトル（べき乗法）。初期値は骨の向き（同じ向きに寄せる）。
        float xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;
        foreach (Vector3 p in pts)
        {
            Vector3 d = p - c;
            xx += d.x * d.x; xy += d.x * d.y; xz += d.x * d.z; yy += d.y * d.y; yz += d.y * d.z; zz += d.z * d.z;
        }

        Vector3 axis = (b - a).normalized;
        for (int it = 0; it < 64; it++)
        {
            Vector3 n = new Vector3(xx * axis.x + xy * axis.y + xz * axis.z, xy * axis.x + yy * axis.y + yz * axis.z, xz * axis.x + yz * axis.y + zz * axis.z);
            if (n.sqrMagnitude < 1e-20f)
            {
                break;
            }

            axis = n.normalized;
        }

        float r2 = 0f;
        float tMin = float.MaxValue;
        float tMax = float.MinValue;
        foreach (Vector3 p in pts)
        {
            Vector3 d = p - c;
            float t = Vector3.Dot(d, axis);
            r2 += (d - axis * t).sqrMagnitude;
            tMin = Mathf.Min(tMin, t);
            tMax = Mathf.Max(tMax, t);
        }

        float radius = Mathf.Sqrt(r2 / pts.Count);
        float angle = Vector3.Angle(b - a, axis);
        if (angle > 90f)
        {
            angle = 180f - angle;
        }

        float DistToAxis(Vector3 p)
        {
            Vector3 d = p - c;
            return (d - axis * Vector3.Dot(d, axis)).magnitude;
        }

        float headDist = DistToAxis(a);
        float tailDist = DistToAxis(b);
        float elong = (tMax - tMin) / Mathf.Max(1e-5f, 2f * radius);
        bool flag = angle > 15f || headDist > 2f * radius || tailDist > 2f * radius;
        Debug.Log($"{LogTag} {model} {s.name} ({s.bone.name}->{s.child.name}): n={pts.Count} angle={angle:F1} head={headDist * 100f:F1}cm ({headDist / Mathf.Max(1e-5f, radius):F2}r) " +
                  $"tail={tailDist * 100f:F1}cm ({tailDist / Mathf.Max(1e-5f, radius):F2}r) radius={radius * 100f:F1}cm len={len * 100f:F1}cm elong={elong:F1}{(flag ? " FLAG" : "")}");
        return $"{{\"name\":{Q(s.name)},\"bone\":{Q(s.bone.name)},\"child\":{Q(s.child.name)},\"n\":{pts.Count},\"angle\":{F(angle)},\"head\":{F(headDist)},\"tail\":{F(tailDist)}," +
               $"\"radius\":{F(radius)},\"len\":{F(len)},\"elong\":{F(elong)},\"heightRel\":{F(height > 0 ? len / height : 0)},\"flag\":{(flag ? "true" : "false")}," +
               $"\"a\":[{F(a.x)},{F(a.y)},{F(a.z)}],\"b\":[{F(b.x)},{F(b.y)},{F(b.z)}],\"c\":[{F(c.x)},{F(c.y)},{F(c.z)}],\"axis\":[{F(axis.x)},{F(axis.y)},{F(axis.z)}]}}";
    }

    private static string Q(string s)
    {
        return "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    private static string F(float v)
    {
        return v.ToString("0.#####", CultureInfo.InvariantCulture);
    }
}
