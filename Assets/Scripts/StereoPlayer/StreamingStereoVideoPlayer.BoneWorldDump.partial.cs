using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

// 検証用（既定 OFF、2026-10-04）: 姿勢と配置が全部終わった時点の骨の world の位置・回転をファイルへ書き出す。
// 割り当て（SMPL / SMAL → モデルの骨）の再調査で、オフラインの移植と runtime の骨の向きを突き合わせるため。
// Debug.Log には出さない（毎フレームの Debug.Log は負荷で動画が飛ぶ）。1 行 = 1 tick × 1 track の JSON:
//   {"k":"frame","tick":Time.frameCount,"t":Time.time,"ut":Time.unscaledTime,"vt":vp.time,"ct":vp.clockTime,"pl":isPlaying,
//    "vf":vp.frame,"f":meta frame,"track":..,"cat":..,
//    "model":"..","cam":[px,py,pz,qx,qy,qz,qw],"root":[px,py,pz,qx,qy,qz,qw,sx,sy,sz],"bones":{"Hips":[px,py,pz,qx,qy,qz,qw],..}}
// cam は TryGetPinholeBasis（bundle のカメラ座標 → world に使っている基底）。
// Humanoid は HumanBodyBones 名（UpperChest の代用を含む）、Animal は SkinnedMeshRenderer の骨を Transform 名で出す。
// インスタンスが初めて出たときに 1 回だけ、キャッシュが採った bind（基準姿勢）の行を出す:
//   {"k":"bind",..,"bindRotWorld":{name:[qx,qy,qz,qw]},"bindRotLocal":{..},"roles":{role:boneName},"parents":{name:parentName}}
public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    private StreamWriter boneWorldDumpWriter;
    private bool boneWorldDumpFailed;
    private readonly HashSet<int> boneWorldDumpBindLogged = new HashSet<int>();
    private readonly StringBuilder boneWorldDumpLine = new StringBuilder(16384);
    private string boneWorldDumpParsedSpec;
    private readonly List<Vector2Int> boneWorldDumpRanges = new List<Vector2Int>();

    // バッチの録画（Editor の BatchPlaybackLogger.Record）が役の骨（足先・尾など）の位置を tick ごとに書くための入口（2026-10-05）。
    internal AnimalRigCache PeekAnimalRigCacheForBatch(GameObject instance)
    {
        return animalPoseApplier.PeekAnimalRigCache(instance);
    }

    private void DumpBoneWorldIfEnabled(MetaObj target, GameObject instance, Transform screen, int frame)
    {
        if (string.IsNullOrEmpty(boneWorldDumpPath) || boneWorldDumpFailed || instance == null)
        {
            return;
        }

        if (!IsFrameInBoneWorldDumpWindows(frame) || !EnsureBoneWorldDumpWriter())
        {
            return;
        }

        bool isAnimal = IsCategoryAnimal(target.categoryId);
        Animator animator = instance.GetComponentInChildren<Animator>();
        HumanoidRigCache humanCache = null;
        if (!isAnimal && animator != null)
        {
            humanoidCaches.TryGetValue(animator, out humanCache);
        }

        if (boneWorldDumpBindLogged.Add(instance.GetInstanceID()))
        {
            WriteBoneWorldBindLine(target, instance, frame, isAnimal, humanCache);
        }

        StringBuilder sb = boneWorldDumpLine;
        sb.Length = 0;
        sb.Append("{\"k\":\"frame\"");
        AppendBoneWorldDumpHeader(sb, target, instance, frame);
        if (TryGetPinholeBasis(screen, out Vector3 camOrigin, out Quaternion camRotation))
        {
            sb.Append(",\"cam\":[");
            AppendVec(sb, camOrigin);
            sb.Append(',');
            AppendQuat(sb, camRotation);
            sb.Append(']');
        }

        Transform root = instance.transform;
        sb.Append(",\"root\":[");
        AppendVec(sb, root.position);
        sb.Append(',');
        AppendQuat(sb, root.rotation);
        sb.Append(',');
        AppendVec(sb, root.lossyScale);
        sb.Append("],\"bones\":{");
        bool first = true;
        foreach (KeyValuePair<string, Transform> kv in EnumerateBoneWorldDumpBones(instance, isAnimal, humanCache))
        {
            if (!first)
            {
                sb.Append(',');
            }

            first = false;
            sb.Append('"').Append(kv.Key).Append("\":[");
            AppendVec(sb, kv.Value.position);
            sb.Append(',');
            AppendQuat(sb, kv.Value.rotation);
            sb.Append(']');
        }

        sb.Append("}}");
        boneWorldDumpWriter.WriteLine(sb.ToString());
    }

    private void WriteBoneWorldBindLine(MetaObj target, GameObject instance, int frame, bool isAnimal, HumanoidRigCache humanCache)
    {
        StringBuilder sb = boneWorldDumpLine;
        sb.Length = 0;
        sb.Append("{\"k\":\"bind\"");
        AppendBoneWorldDumpHeader(sb, target, instance, frame);
        sb.Append(",\"bindRotWorld\":{");
        bool first = true;
        if (humanCache != null)
        {
            foreach (KeyValuePair<HumanBodyBones, Quaternion> kv in humanCache.bindRotWorld)
            {
                AppendNamedQuat(sb, kv.Key.ToString(), kv.Value, ref first);
            }

            sb.Append("},\"bindRotLocal\":{");
            first = true;
            foreach (KeyValuePair<HumanBodyBones, Quaternion> kv in humanCache.bindRotLocal)
            {
                AppendNamedQuat(sb, kv.Key.ToString(), kv.Value, ref first);
            }

            sb.Append("},\"roles\":{");
            first = true;
            foreach (KeyValuePair<HumanBodyBones, Transform> kv in humanCache.bones)
            {
                AppendNamedString(sb, kv.Key.ToString(), kv.Value != null ? kv.Value.name : "", ref first);
            }
        }
        else if (isAnimal)
        {
            AnimalRigCache cache = animalPoseApplier.PeekAnimalRigCache(instance);
            if (cache != null)
            {
                foreach (KeyValuePair<Transform, Quaternion> kv in cache.bindRotWorld)
                {
                    if (kv.Key != null)
                    {
                        AppendNamedQuat(sb, kv.Key.name, kv.Value, ref first);
                    }
                }

                sb.Append("},\"bindRotLocal\":{");
                first = true;
                foreach (KeyValuePair<Transform, Quaternion> kv in cache.bindRotLocal)
                {
                    if (kv.Key != null)
                    {
                        AppendNamedQuat(sb, kv.Key.name, kv.Value, ref first);
                    }
                }

                sb.Append("},\"aimChild\":{");
                first = true;
                foreach (KeyValuePair<Transform, Transform> kv in cache.aimChildByBone)
                {
                    if (kv.Key != null)
                    {
                        AppendNamedString(sb, kv.Key.name, kv.Value != null ? kv.Value.name : "", ref first);
                    }
                }

                sb.Append("},\"roles\":{");
                first = true;
                AppendRole(sb, "root", cache.root, ref first);
                AppendRole(sb, "spine", cache.spine, ref first);
                AppendRole(sb, "neck", cache.neck, ref first);
                AppendRole(sb, "head", cache.head, ref first);
                AppendRole(sb, "tailBase", cache.tailBase, ref first);
                AppendRole(sb, "tailMid", cache.tailMid, ref first);
                AppendRole(sb, "tailTip", cache.tailTip, ref first);
                AppendRole(sb, "frontLUpper", cache.leftFrontUpper, ref first);
                AppendRole(sb, "frontLLower", cache.leftFrontLower, ref first);
                AppendRole(sb, "frontLPaw", cache.leftFrontPaw, ref first);
                AppendRole(sb, "frontRUpper", cache.rightFrontUpper, ref first);
                AppendRole(sb, "frontRLower", cache.rightFrontLower, ref first);
                AppendRole(sb, "frontRPaw", cache.rightFrontPaw, ref first);
                AppendRole(sb, "rearLUpper", cache.leftRearUpper, ref first);
                AppendRole(sb, "rearLLower", cache.leftRearLower, ref first);
                AppendRole(sb, "rearLPaw", cache.leftRearPaw, ref first);
                AppendRole(sb, "rearLToe", cache.leftRearToe, ref first);
                AppendRole(sb, "rearRUpper", cache.rightRearUpper, ref first);
                AppendRole(sb, "rearRLower", cache.rightRearLower, ref first);
                AppendRole(sb, "rearRPaw", cache.rightRearPaw, ref first);
                AppendRole(sb, "rearRToe", cache.rightRearToe, ref first);
            }
        }

        // 骨の親子（dump する骨どうしとは限らない。Transform の直接の親の名前）。
        sb.Append("},\"parents\":{");
        first = true;
        foreach (KeyValuePair<string, Transform> kv in EnumerateBoneWorldDumpBones(instance, isAnimal, humanCache))
        {
            AppendNamedString(sb, kv.Key, kv.Value.parent != null ? kv.Value.parent.name : "", ref first);
        }

        sb.Append("}}");
        boneWorldDumpWriter.WriteLine(sb.ToString());
    }

    private IEnumerable<KeyValuePair<string, Transform>> EnumerateBoneWorldDumpBones(GameObject instance, bool isAnimal, HumanoidRigCache humanCache)
    {
        if (!isAnimal)
        {
            if (humanCache == null)
            {
                yield break;
            }

            foreach (KeyValuePair<HumanBodyBones, Transform> kv in humanCache.bones)
            {
                if (kv.Value != null)
                {
                    yield return new KeyValuePair<string, Transform>(kv.Key.ToString(), kv.Value);
                }
            }

            yield break;
        }

        var seen = new HashSet<Transform>();
        var names = new HashSet<string>();
        foreach (SkinnedMeshRenderer smr in instance.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            Transform[] smrBones = smr.bones;
            if (smrBones == null)
            {
                continue;
            }

            foreach (Transform bone in smrBones)
            {
                if (bone == null || !seen.Add(bone))
                {
                    continue;
                }

                // 同名の骨が別の階層にあるリグでも行を潰さないよう、2 本目以降は #番号を付ける。
                string name = bone.name.Replace("\"", "'");
                string key = name;
                for (int n = 2; !names.Add(key); n++)
                {
                    key = name + "#" + n;
                }

                yield return new KeyValuePair<string, Transform>(key, bone);
            }
        }
    }

    private void AppendBoneWorldDumpHeader(StringBuilder sb, MetaObj target, GameObject instance, int frame)
    {
        sb.Append(",\"tick\":").Append(Time.frameCount);
        sb.Append(",\"t\":").Append(Time.time.ToString("0.#####", CultureInfo.InvariantCulture));
        sb.Append(",\"ut\":").Append(Time.unscaledTime.ToString("0.#####", CultureInfo.InvariantCulture));
        if (vp != null)
        {
            // vt（vp.time）は texture にあるフレームの時刻で端数を持たない。ct（vp.clockTime）は VideoPlayer が従う時計で、
            // 30 fps のフレームの間を補間する時刻の候補（2026-10-04、調査役 TM の依頼 U1）。
            sb.Append(",\"vt\":").Append(vp.time.ToString("0.######", CultureInfo.InvariantCulture));
            sb.Append(",\"ct\":").Append(vp.clockTime.ToString("0.######", CultureInfo.InvariantCulture));
            sb.Append(",\"pl\":").Append(vp.isPlaying ? 1 : 0);
            sb.Append(",\"vf\":").Append(vp.frame);
        }

        sb.Append(",\"f\":").Append(frame);
        sb.Append(",\"track\":").Append(target.trackId);
        sb.Append(",\"cat\":").Append(target.categoryId);
        sb.Append(",\"model\":\"").Append(instance.name.Replace("\"", "'")).Append('"');
        // prefab 名（2026-10-04 追加。model は "Track_0" なので、どのモデルかを書き出しだけで分かるように）。
        sb.Append(",\"prefab\":\"")
            .Append(trackPrefabSources.TryGetValue(target.trackId, out GameObject dumpPrefab) && dumpPrefab != null
                ? dumpPrefab.name.Replace("\"", "'")
                : string.Empty)
            .Append('"');
    }

    private bool IsFrameInBoneWorldDumpWindows(int frame)
    {
        string spec = boneWorldDumpWindows ?? string.Empty;
        if (!string.Equals(spec, boneWorldDumpParsedSpec, System.StringComparison.Ordinal))
        {
            boneWorldDumpParsedSpec = spec;
            boneWorldDumpRanges.Clear();
            foreach (string raw in spec.Split(','))
            {
                string part = raw.Trim();
                int dash = part.IndexOf('-');
                if (dash > 0 &&
                    int.TryParse(part.Substring(0, dash), out int from) &&
                    int.TryParse(part.Substring(dash + 1), out int to))
                {
                    boneWorldDumpRanges.Add(new Vector2Int(from, to));
                }
            }
        }

        if (boneWorldDumpRanges.Count == 0)
        {
            return true;
        }

        foreach (Vector2Int range in boneWorldDumpRanges)
        {
            if (frame >= range.x && frame <= range.y)
            {
                return true;
            }
        }

        return false;
    }

    private bool EnsureBoneWorldDumpWriter()
    {
        if (boneWorldDumpWriter != null)
        {
            return true;
        }

        try
        {
            string dir = Path.GetDirectoryName(boneWorldDumpPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            boneWorldDumpWriter = new StreamWriter(boneWorldDumpPath, false, new UTF8Encoding(false)) { AutoFlush = true };
            Debug.Log($"[BONEWORLD] dump -> {boneWorldDumpPath} windows='{boneWorldDumpWindows}'");
            return true;
        }
        catch (System.Exception ex)
        {
            boneWorldDumpFailed = true;
            Debug.LogWarning($"[BONEWORLD] ファイルを開けない: {boneWorldDumpPath} ({ex.Message})");
            return false;
        }
    }

    private void CloseBoneWorldDump()
    {
        if (boneWorldDumpWriter == null)
        {
            return;
        }

        boneWorldDumpWriter.Dispose();
        boneWorldDumpWriter = null;
    }

    private static void AppendNamedQuat(StringBuilder sb, string name, Quaternion q, ref bool first)
    {
        if (!first)
        {
            sb.Append(',');
        }

        first = false;
        sb.Append('"').Append(name.Replace("\"", "'")).Append("\":[");
        AppendQuat(sb, q);
        sb.Append(']');
    }

    private static void AppendNamedString(StringBuilder sb, string name, string value, ref bool first)
    {
        if (!first)
        {
            sb.Append(',');
        }

        first = false;
        sb.Append('"').Append(name.Replace("\"", "'")).Append("\":\"").Append(value.Replace("\"", "'")).Append('"');
    }

    private static void AppendRole(StringBuilder sb, string role, Transform bone, ref bool first)
    {
        AppendNamedString(sb, role, bone != null ? bone.name : "", ref first);
    }

    private static void AppendVec(StringBuilder sb, Vector3 v)
    {
        sb.Append(v.x.ToString("0.#####", CultureInfo.InvariantCulture)).Append(',')
          .Append(v.y.ToString("0.#####", CultureInfo.InvariantCulture)).Append(',')
          .Append(v.z.ToString("0.#####", CultureInfo.InvariantCulture));
    }

    private static void AppendQuat(StringBuilder sb, Quaternion q)
    {
        sb.Append(q.x.ToString("0.######", CultureInfo.InvariantCulture)).Append(',')
          .Append(q.y.ToString("0.######", CultureInfo.InvariantCulture)).Append(',')
          .Append(q.z.ToString("0.######", CultureInfo.InvariantCulture)).Append(',')
          .Append(q.w.ToString("0.######", CultureInfo.InvariantCulture));
    }
}
