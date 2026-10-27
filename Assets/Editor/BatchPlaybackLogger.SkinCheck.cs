using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

// 皮膚の確かめ（2026-10-23、-recordSkinCheck "24,106,258"）。動物の骨を SMAL の 35 関節にそろえる試作で、Blender の道具（bench）が
// 比べた「1 頂点を 2 本の骨で動かす」「4 本」が、Unity の描画と同じかを確かめるため（Docs/tmp/smal_rig_20261023/README.md）。
// -recordFrames の区間に入っている、指定の動画フレームの最初の tick で、撮る track（-recordViews の最初の Track_N）について:
//   skin_f<vf>_<view>.png        モデルだけを 3 方向から（全身の横・首の寄りの横・首の寄りの斜め前）。GPU の皮膚のまま
//   skin_f<vf>_<view>_baked.png  同じカメラで、BakeMesh(false) の結果を MeshRenderer で置いたもの（CPU の皮膚が GPU と同じか）
//   skin_f<vf>_smr<k>_bake.npy   BakeMesh の頂点（world、float32 N×3）
//   skin_f<vf>_smr<k>_bones.npy  smr.bones の localToWorldMatrix（float32 B×4×4、行が先）
//   skin_f<vf>.json              quality・品質の設定・frameCount・骨の名前・レンダラの位置と回転
//   skin_mesh_smr<k>_*.npy       （走りで 1 回）sharedMesh の頂点（N×3）・bindposes（B×4×4）・頂点ごとの骨の数（N、uint8）・重み（M×2: 骨の番号, 重み）
// 画素の比べは同じ引数の別の走りどうしで行う（同じ tick の中で quality を変えても、皮膚の計算はフレームに 1 回なので絵が変わらないおそれがある）。
public static partial class BatchPlaybackLogger
{
    private static HashSet<long> skinCheckFrames;
    private static bool skinCheckResolved;
    private static readonly HashSet<long> skinCheckDone = new HashSet<long>();
    private static readonly HashSet<int> skinCheckMeshWritten = new HashSet<int>();
    // -recordSkinEvery N（2026-10-23）: 撮る tick の N 個ごとにも書く（イベント中は動画が止まり vf が変わらないので、tick で数える）。
    // -recordSkinLight true: そのとき絵と BakeMesh を省いて、骨の行列と json だけ書く（姿勢の組を多く取るため）。
    private static int skinCheckEvery;
    private static bool skinCheckLight;
    private static int skinCheckCalls;
    // -recordSkinBack true（2026-10-24）: 人に後ろからの絵を足す（全身の後ろ、上半身の右後ろ・左後ろ、腰の右後ろ）。
    // 肩の後ろ・脇の後ろの切れ目は前・横からの絵に写らない（新しい女性の皮膚の判定・反論役が見つけた崩れ）。
    private static bool skinCheckBack;

    private static void WriteSkinCheck(string dir, string name, Camera mainCam, long vf)
    {
        if (!skinCheckResolved)
        {
            skinCheckResolved = true;
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "-recordSkinEvery") { int.TryParse(args[i + 1], out skinCheckEvery); }
                if (args[i] == "-recordSkinLight") { bool.TryParse(args[i + 1], out skinCheckLight); }
                if (args[i] == "-recordSkinBack") { bool.TryParse(args[i + 1], out skinCheckBack); }
                if (args[i] != "-recordSkinCheck")
                {
                    continue;
                }

                skinCheckFrames = new HashSet<long>();
                foreach (string s in args[i + 1].Split(','))
                {
                    if (long.TryParse(s.Trim(), out long f))
                    {
                        skinCheckFrames.Add(f);
                    }
                }

                Debug.Log($"[SKINCHECK] frames={args[i + 1]}");
            }

            if (skinCheckEvery > 0)
            {
                Debug.Log($"[SKINCHECK] every={skinCheckEvery} ticks light={skinCheckLight}");
            }
        }

        int call = skinCheckCalls++;
        bool byFrame = skinCheckFrames != null && skinCheckFrames.Contains(vf) && !skinCheckDone.Contains(vf);
        bool byEvery = skinCheckEvery > 0 && call % skinCheckEvery == 0;
        if (!byFrame && !byEvery)
        {
            return;
        }

        if (byFrame)
        {
            skinCheckDone.Add(vf);
        }

        bool lightOnly = !byFrame && skinCheckLight;
        string track = ResolveRecordPoseTrack();
        GameObject trackObject = GameObject.Find(track);
        var player = UnityEngine.Object.FindFirstObjectByType<StreamingStereoVideoPlayer>();
        AnimalRigCache cache = player != null && trackObject != null ? player.PeekAnimalRigCacheForBatch(trackObject) : null;
        // 人（Humanoid）は動物のリグのキャッシュが無いので、Animator の骨で撮る向きを決める（2026-10-23、人のリグの確かめ）。
        Transform headT = cache != null ? cache.head : null;
        Transform neckT = cache != null ? cache.neck : null;
        Transform spineT = cache != null ? cache.spine : null;
        Animator humanAnimator = null;
        if (trackObject != null && headT == null)
        {
            humanAnimator = trackObject.GetComponentInChildren<Animator>(true);
            if (humanAnimator != null && humanAnimator.isHuman)
            {
                headT = humanAnimator.GetBoneTransform(HumanBodyBones.Head);
                neckT = humanAnimator.GetBoneTransform(HumanBodyBones.Neck);
                if (neckT == null) { neckT = humanAnimator.GetBoneTransform(HumanBodyBones.UpperChest); }
                if (neckT == null) { neckT = humanAnimator.GetBoneTransform(HumanBodyBones.Chest); }
                spineT = humanAnimator.GetBoneTransform(HumanBodyBones.Hips);
            }
            else
            {
                humanAnimator = null;
            }
        }

        if (trackObject == null || headT == null || neckT == null || spineT == null)
        {
            Debug.LogWarning($"[SKINCHECK] vf={vf} {track}: track か骨（spine・neck・head。動物のリグのキャッシュか Humanoid）が無い");
            return;
        }

        SkinnedMeshRenderer[] smrs = trackObject.GetComponentsInChildren<SkinnedMeshRenderer>(false);
        var inv = CultureInfo.InvariantCulture;
        string prefix = byFrame ? Path.Combine(dir, $"skin_f{vf:D4}") : Path.Combine(dir, $"skin_t{call:D5}");

        // メッシュの元のデータ（走りで 1 回）と、この tick の BakeMesh・骨の行列
        var meta = new StringBuilder();
        meta.Append("{\"vf\":").Append(vf).Append(",\"tick\":\"").Append(name).Append("\",\"frameCount\":").Append(Time.frameCount)
            .Append(",\"track\":\"").Append(track).Append("\",\"skinWeights\":\"").Append(QualitySettings.skinWeights)
            .Append("\",\"qualityLevel\":\"").Append(QualitySettings.names[QualitySettings.GetQualityLevel()]).Append("\",\"renderers\":[");
        for (int k = 0; k < smrs.Length; k++)
        {
            SkinnedMeshRenderer smr = smrs[k];
            Mesh shared = smr.sharedMesh;
            if (shared == null)
            {
                continue;
            }

            if (skinCheckMeshWritten.Add(k))
            {
                string mp = Path.Combine(dir, $"skin_mesh_smr{k}");
                WriteNpyFloat(mp + "_vertices.npy", Flatten(shared.vertices), shared.vertexCount, 3);
                WriteNpyFloat(mp + "_bindposes.npy", Flatten(shared.bindposes), shared.bindposes.Length, 4, 4);
                byte[] bpv = shared.GetBonesPerVertex().ToArray();
                WriteNpyBytes(mp + "_bonespervertex.npy", bpv, bpv.Length);
                BoneWeight1[] bw = shared.GetAllBoneWeights().ToArray();
                var w = new float[bw.Length * 2];
                for (int i = 0; i < bw.Length; i++)
                {
                    w[2 * i] = bw[i].boneIndex;
                    w[2 * i + 1] = bw[i].weight;
                }

                WriteNpyFloat(mp + "_weights.npy", w, bw.Length, 2);
            }

            if (!lightOnly)
            {
                var baked = new Mesh();
                smr.BakeMesh(baked, false);
                Matrix4x4 trs = Matrix4x4.TRS(smr.transform.position, smr.transform.rotation, Vector3.one);
                Vector3[] bv = baked.vertices;
                for (int i = 0; i < bv.Length; i++)
                {
                    bv[i] = trs.MultiplyPoint3x4(bv[i]);
                }

                WriteNpyFloat($"{prefix}_smr{k}_bake.npy", Flatten(bv), bv.Length, 3);
                UnityEngine.Object.DestroyImmediate(baked);
            }

            Transform[] bones = smr.bones;
            var mats = new Matrix4x4[bones.Length];
            for (int b = 0; b < bones.Length; b++)
            {
                mats[b] = bones[b] != null ? bones[b].localToWorldMatrix : Matrix4x4.zero;
            }

            WriteNpyFloat($"{prefix}_smr{k}_bones.npy", Flatten(mats), bones.Length, 4, 4);

            meta.Append(k > 0 ? "," : "").Append("{\"name\":\"").Append(smr.name).Append("\",\"quality\":\"").Append(smr.quality)
                .Append("\",\"vertexCount\":").Append(shared.vertexCount).Append(",\"pos\":[").Append(F3(smr.transform.position, inv))
                .Append("],\"rot\":[").Append(F4(smr.transform.rotation, inv)).Append("],\"rootBone\":\"")
                .Append(smr.rootBone != null ? smr.rootBone.name : "").Append("\",\"bones\":[");
            for (int b = 0; b < bones.Length; b++)
            {
                meta.Append(b > 0 ? "," : "").Append('"').Append(bones[b] != null ? bones[b].name : "").Append('"');
            }

            meta.Append("]}");
        }

        meta.Append("]}\n");
        File.WriteAllText(prefix + ".json", meta.ToString());
        if (lightOnly)
        {
            return;
        }

        // 3 方向から撮る（人は 5 方向）。GPU の皮膚と、BakeMesh を置いたものを同じカメラで。
        Bounds bounds = default(Bounds);
        bool hasBounds = false;
        foreach (SkinnedMeshRenderer smr in smrs)
        {
            if (!hasBounds) { bounds = smr.bounds; hasBounds = true; }
            else { bounds.Encapsulate(smr.bounds); }
        }

        Vector3 fwd;
        if (humanAnimator != null)
        {
            // 人の前: 左右の大腿の付け根から（右 − 左 と上の外積。TryResolveBodyForward と同じ）
            Transform lUp = humanAnimator.GetBoneTransform(HumanBodyBones.LeftUpperLeg);
            Transform rUp = humanAnimator.GetBoneTransform(HumanBodyBones.RightUpperLeg);
            fwd = lUp != null && rUp != null ? Vector3.Cross(rUp.position - lUp.position, Vector3.up) : trackObject.transform.forward;
        }
        else
        {
            fwd = neckT.position - spineT.position;
        }

        fwd.y = 0f;
        if (fwd.sqrMagnitude < 1e-10f)
        {
            fwd = trackObject.transform.forward;
            fwd.y = 0f;
        }

        fwd.Normalize();
        Vector3 left = -Vector3.Cross(Vector3.up, fwd).normalized;
        Transform neckBase = humanAnimator == null && neckT.parent != null ? neckT.parent : neckT;
        Vector3 neckAim = (neckBase.position + headT.position) * 0.5f;
        float neckSize = Vector3.Distance(neckBase.position, headT.position) * 1.8f;
        const float fov = 30f;
        float tanHalf = Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad);
        Vector3 Elevate(Vector3 d, float deg) => (d * Mathf.Cos(deg * Mathf.Deg2Rad) + Vector3.up * Mathf.Sin(deg * Mathf.Deg2Rad)).normalized;
        var views = new List<(string label, Vector3 aim, Vector3 dir, float dist)>
        {
            ("body", hasBounds ? bounds.center : neckAim, Elevate(left, 5f), (hasBounds ? bounds.extents.magnitude : neckSize) * 1.05f / tanHalf),
        };
        if (humanAnimator != null)
        {
            // 人: 全身の正面・斜め前、上半身（肩・肘）と腰・膝の寄り（斜め前）
            Transform chest = humanAnimator.GetBoneTransform(HumanBodyBones.UpperChest) ?? humanAnimator.GetBoneTransform(HumanBodyBones.Chest) ?? neckT;
            Transform lShoulder = humanAnimator.GetBoneTransform(HumanBodyBones.LeftUpperArm);
            Transform rShoulder = humanAnimator.GetBoneTransform(HumanBodyBones.RightUpperArm);
            Transform lKnee = humanAnimator.GetBoneTransform(HumanBodyBones.LeftLowerLeg);
            float shoulderW = lShoulder != null && rShoulder != null ? Vector3.Distance(lShoulder.position, rShoulder.position) : neckSize;
            float bodyExt = hasBounds ? bounds.extents.magnitude : shoulderW * 4f;
            Vector3 hipsAim = lKnee != null ? (spineT.position + lKnee.position) * 0.5f : spineT.position;
            views.Add(("front", hasBounds ? bounds.center : spineT.position, Elevate(fwd, 5f), bodyExt * 1.05f / tanHalf));
            views.Add(("front34", hasBounds ? bounds.center : spineT.position, Elevate((left + fwd).normalized, 10f), bodyExt * 1.05f / tanHalf));
            views.Add(("upperfront34", chest.position, Elevate((left + fwd).normalized, 10f), shoulderW * 1.6f / tanHalf));
            views.Add(("hipsfront34", hipsAim, Elevate((left + fwd).normalized, 5f), shoulderW * 1.6f / tanHalf));
            if (skinCheckBack)
            {
                views.Add(("back", hasBounds ? bounds.center : spineT.position, Elevate(-fwd, 5f), bodyExt * 1.05f / tanHalf));
                views.Add(("upperback34R", chest.position, Elevate((-left - fwd).normalized, 10f), shoulderW * 1.6f / tanHalf));
                views.Add(("upperback34L", chest.position, Elevate((left - fwd).normalized, 10f), shoulderW * 1.6f / tanHalf));
                views.Add(("hipsback34R", hipsAim, Elevate((-left - fwd).normalized, 5f), shoulderW * 1.6f / tanHalf));
            }
        }
        else
        {
            views.Add(("neckside", neckAim, Elevate(left, 5f), neckSize * 0.5f / tanHalf));
            views.Add(("neckfront34", neckAim, Elevate((left + fwd).normalized, 10f), neckSize * 0.5f / tanHalf));
        }

        foreach (var v in views)
        {
            Vector3 pos = v.aim + v.dir * v.dist;
            Quaternion rot = Quaternion.LookRotation(-v.dir, Vector3.up);
            RenderRecordView(mainCam, pos, rot, fov, Mathf.Max(0.001f, v.dist * 0.05f), 100f, 1024, 768,
                $"{prefix}_{v.label}.png", headT);
        }

        // BakeMesh の結果を MeshRenderer で置いて、同じカメラで撮る（SkinnedMeshRenderer は撮る間だけ隠す）
        var temps = new List<GameObject>();
        try
        {
            foreach (SkinnedMeshRenderer smr in smrs)
            {
                var baked = new Mesh();
                smr.BakeMesh(baked, false);
                var go = new GameObject("SkinCheckBaked");
                go.transform.SetParent(trackObject.transform, false);
                go.transform.SetPositionAndRotation(smr.transform.position, smr.transform.rotation);
                Vector3 ps = trackObject.transform.lossyScale;
                go.transform.localScale = new Vector3(1f / ps.x, 1f / ps.y, 1f / ps.z);
                go.AddComponent<MeshFilter>().sharedMesh = baked;
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterials = smr.sharedMaterials;
                mr.shadowCastingMode = smr.shadowCastingMode;
                mr.receiveShadows = smr.receiveShadows;
                smr.enabled = false;
                temps.Add(go);
            }

            foreach (var v in views)
            {
                Vector3 pos = v.aim + v.dir * v.dist;
                Quaternion rot = Quaternion.LookRotation(-v.dir, Vector3.up);
                RenderRecordView(mainCam, pos, rot, fov, Mathf.Max(0.001f, v.dist * 0.05f), 100f, 1024, 768,
                    $"{prefix}_{v.label}_baked.png", headT);
            }
        }
        finally
        {
            foreach (GameObject go in temps)
            {
                MeshFilter mf = go.GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh != null) { UnityEngine.Object.DestroyImmediate(mf.sharedMesh); }
                UnityEngine.Object.DestroyImmediate(go);
            }

            foreach (SkinnedMeshRenderer smr in smrs)
            {
                smr.enabled = true;
            }
        }

        Debug.Log($"[SKINCHECK] vf={vf} {track} renderers={smrs.Length} quality={(smrs.Length > 0 ? smrs[0].quality.ToString() : "-")} " +
                  $"skinWeights={QualitySettings.skinWeights} → {prefix}_*");
    }

    private static string F3(Vector3 v, CultureInfo inv) =>
        v.x.ToString("R", inv) + "," + v.y.ToString("R", inv) + "," + v.z.ToString("R", inv);

    private static string F4(Quaternion q, CultureInfo inv) =>
        q.x.ToString("R", inv) + "," + q.y.ToString("R", inv) + "," + q.z.ToString("R", inv) + "," + q.w.ToString("R", inv);

    private static float[] Flatten(Vector3[] v)
    {
        var f = new float[v.Length * 3];
        for (int i = 0; i < v.Length; i++)
        {
            f[3 * i] = v[i].x;
            f[3 * i + 1] = v[i].y;
            f[3 * i + 2] = v[i].z;
        }

        return f;
    }

    private static float[] Flatten(Matrix4x4[] m)
    {
        var f = new float[m.Length * 16];
        for (int i = 0; i < m.Length; i++)
        {
            for (int r = 0; r < 4; r++)
            {
                for (int c = 0; c < 4; c++)
                {
                    f[16 * i + 4 * r + c] = m[i][r, c];
                }
            }
        }

        return f;
    }

    // numpy の .npy（v1.0）。Windows は little endian。
    private static void WriteNpyFloat(string path, float[] data, params int[] shape)
    {
        var bytes = new byte[data.Length * 4];
        Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
        WriteNpy(path, "<f4", bytes, shape);
    }

    private static void WriteNpyBytes(string path, byte[] data, params int[] shape)
    {
        WriteNpy(path, "|u1", data, shape);
    }

    private static void WriteNpy(string path, string descr, byte[] data, int[] shape)
    {
        string shapeText = shape.Length == 1 ? $"({shape[0]},)" : "(" + string.Join(", ", shape) + ")";
        string header = "{'descr': '" + descr + "', 'fortran_order': False, 'shape': " + shapeText + ", }";
        int total = 10 + header.Length + 1;
        int pad = (64 - total % 64) % 64;
        header = header + new string(' ', pad) + "\n";
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
        using (var bw = new BinaryWriter(fs))
        {
            bw.Write((byte)0x93);
            bw.Write(Encoding.ASCII.GetBytes("NUMPY"));
            bw.Write((byte)1);
            bw.Write((byte)0);
            bw.Write((ushort)header.Length);
            bw.Write(Encoding.ASCII.GetBytes(header));
            bw.Write(data);
        }
    }
}
