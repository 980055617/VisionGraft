using System.Text;
using UnityEditor;
using UnityEngine;

// Animal の prefab を bind pose で採寸して、モデルを差し替えたときに大きさが揃うかを見る。
//
// **なぜ要るか**（2026-09-05）:
// `ReplaceableModel.CaptureSkeletonHeight` は Humanoid 専用（`animator.isHuman`）なので、
// Generic リグの Animal では `baseSkeletonHeightMeters = 0` になり、配置スケールは
// **renderer の AABB の高さ**で決まる（実測: Animal は modelH=aabbH、skelH=0.0000。
// Human は modelH=skelH で aabbH は 18% 大きい）。
//
// AABB の高さは角・耳・尻尾・頭の上げ下げを全部含むので、bind pose の作り方が
// モデルごとに違うぶんがそのままスケールの差になる。ここではその差を数値で出す。
//
// 実行:
//   Unity.exe -batchmode -projectPath <proj> -executeMethod AnimalModelGeometryAudit.Run
//             -logFile <log>
public static class AnimalModelGeometryAudit
{
    // 体の高さの基準にする「き甲高（withers）」。前脚の付け根〜前足の接地点。
    // 頭・角・尻尾を含まないので bind pose の姿勢差に強い。
    private const string SpineName = "spine";
    private const string HeadName = "head";
    private static readonly string[] FrontUpper = { "front_l_upper", "front_r_upper" };
    private static readonly string[] FrontPaw = { "front_l_paw", "front_r_paw" };
    private static readonly string[] RearUpper = { "rear_l_upper", "rear_r_upper" };
    private static readonly string[] RearPaw = { "rear_l_paw", "rear_r_paw" };

    // FK が要求する正規ボーン。欠けているモデルは姿勢が当たらない。
    private static readonly string[] Required =
    {
        "spine", "neck", "head",
        "front_l_upper", "front_l_lower", "front_l_paw",
        "front_r_upper", "front_r_lower", "front_r_paw",
        "rear_l_upper", "rear_l_lower", "rear_l_paw",
        "rear_r_upper", "rear_r_lower", "rear_r_paw",
    };

    [MenuItem("Tools/VisionGraft/Audit Animal Geometry")]
    public static void Run()
    {
        // `Resources/Models/Animal/Sources/` には index に載っていない生 fbx が 10 個あり
        // （bear / boar / deer_1 …）、LoadAll はそれも拾う。prefab だけを対象にする。
        GameObject[] all = Resources.LoadAll<GameObject>("Models/Animal");
        System.Collections.Generic.List<GameObject> list = new System.Collections.Generic.List<GameObject>();
        for (int i = 0; i < all.Length; i++)
        {
            if (all[i] != null &&
                AssetDatabase.GetAssetPath(all[i]).EndsWith(".prefab", System.StringComparison.OrdinalIgnoreCase))
            {
                list.Add(all[i]);
            }
        }

        GameObject[] prefabs = list.ToArray();
        System.Array.Sort(prefabs, (a, b) => string.CompareOrdinal(a.name, b.name));

        StringBuilder sb = new StringBuilder();
        sb.AppendLine($"[ANIMALGEO] n={prefabs.Length}");
        for (int i = 0; i < prefabs.Length; i++)
        {
            sb.AppendLine(Measure(prefabs[i]));
        }

        Debug.Log(sb.ToString());
        if (Application.isBatchMode)
        {
            EditorApplication.Exit(0);
        }
    }

    // 正規ボーンを持たないモデルのリネーム検討用に、階層のボーン名をそのまま出す。
    // 実行: -executeMethod AnimalModelGeometryAudit.DumpBones -auditFilter Kangaroo
    [MenuItem("Tools/VisionGraft/Dump Animal Bone Names")]
    public static void DumpBones()
    {
        string filter = null;
        string[] args = System.Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "-auditFilter")
            {
                filter = args[i + 1];
            }
        }

        GameObject[] all = Resources.LoadAll<GameObject>("Models/Animal");
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < all.Length; i++)
        {
            GameObject p = all[i];
            if (p == null ||
                !AssetDatabase.GetAssetPath(p).EndsWith(".prefab", System.StringComparison.OrdinalIgnoreCase) ||
                (filter != null && p.name.IndexOf(filter, System.StringComparison.OrdinalIgnoreCase) < 0))
            {
                continue;
            }

            GameObject go = Object.Instantiate(p);
            try
            {
                Transform[] ts = go.GetComponentsInChildren<Transform>(true);
                sb.Append("[BONEDUMP] " + p.name + " n=" + ts.Length + " :");
                for (int k = 0; k < ts.Length && k < 90; k++)
                {
                    sb.Append(" " + ts[k].name);
                }

                sb.AppendLine();
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        Debug.Log(sb.ToString());
        if (Application.isBatchMode)
        {
            EditorApplication.Exit(0);
        }
    }

    private static string Measure(GameObject prefab)
    {
        GameObject go = Object.Instantiate(prefab);
        try
        {
            float lossyY = Mathf.Abs(go.transform.lossyScale.y);
            if (lossyY < 0.000001f)
            {
                lossyY = 1f;
            }

            // SkinnedMeshRenderer は画面外だと bounds が更新されないので明示的に開ける。
            SkinnedMeshRenderer[] skins = go.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int i = 0; i < skins.Length; i++)
            {
                skins[i].updateWhenOffscreen = true;
            }

            Renderer[] rs = go.GetComponentsInChildren<Renderer>(true);
            if (rs.Length == 0)
            {
                return $"[ANIMALGEO] {prefab.name} renderer なし";
            }

            Bounds b = rs[0].bounds;
            for (int i = 1; i < rs.Length; i++)
            {
                b.Encapsulate(rs[i].bounds);
            }

            float aabbH = b.size.y / lossyY;
            float groundY = b.min.y;

            Transform[] bones = go.GetComponentsInChildren<Transform>(true);
            Transform spine = Find(bones, SpineName);
            Transform head = Find(bones, HeadName);
            float frontUpperY = MeanY(bones, FrontUpper);
            float frontPawY = MeanY(bones, FrontPaw);
            float rearUpperY = MeanY(bones, RearUpper);
            float rearPawY = MeanY(bones, RearPaw);

            // き甲高: 前脚の付け根 - 前足。取れなければ spine - AABB 下端。
            float withers = float.NaN;
            if (!float.IsNaN(frontUpperY) && !float.IsNaN(frontPawY))
            {
                withers = (frontUpperY - frontPawY) / lossyY;
            }
            else if (spine != null)
            {
                withers = (spine.position.y - groundY) / lossyY;
            }

            // 胴の長さ: 前脚の付け根 - 後脚の付け根（水平距離）。
            float bodyLen = float.NaN;
            Transform fu = FindAny(bones, FrontUpper);
            Transform ru = FindAny(bones, RearUpper);
            if (fu != null && ru != null)
            {
                Vector3 d = fu.position - ru.position;
                bodyLen = new Vector2(d.x, d.z).magnitude / lossyY;
            }

            // 頭がどれだけ上にあるか。bind pose で頭を上げているモデルほど AABB が伸びる。
            float headAbove = spine != null && head != null
                ? (head.position.y - spine.position.y) / lossyY
                : float.NaN;

            // **これが見た目差を決める指標。**
            // ⑨ RefineLockedScaleFromProjectedBones は「骨の投影スパン = bbox 高」に
            // 合わせ込む（shot 先頭で boneRatio を 1.000 にする）。Generic リグでは
            // 投影に使う骨は全ボーンなので、角・耳・尻尾の先まで入る。
            // つまり **骨スパンはモデル間で揃う**が、その中で体が占める割合は揃わない。
            // withers / boneSpanY が小さいモデルほど、画面上で体が小さく見える。
            // **`ResolveProjectionBones` と同じ選び方で測る。**別の集合で測ると
            // 実行時の挙動を予測できない（過去に評価方法を実装と揃えず 3 回誤っている）。
            float boneSpanY = SpanY(ProjectionBones(go), lossyY);
            float bodyShare = (boneSpanY > 0.0001f && withers > 0.0001f) ? withers / boneSpanY : float.NaN;

            // 正規ボーンだけに絞ったらどうなるか。角・耳・尻尾の先・armature の根が
            // 落ちるので、モデル間のばらつきが縮むはず。
            float canonSpanY = SpanY(CanonicalBones(bones), lossyY);
            float canonShare = (canonSpanY > 0.0001f && withers > 0.0001f) ? withers / canonSpanY : float.NaN;

            // bind pose での肘・膝の曲がり（まっすぐからのズレ、度）。
            // `[ANIMALANG]` と**同じ定義**: Vector3.Angle(lo - up, paw - lo)。0° が直線。
            // FK が SMAL の曲げを bind の曲げに足し込んでいるなら、
            // ここが大きいモデルほど実行時に折り畳まれるはず。
            float bindLR = JointAngle(bones, "rear_l_upper", "rear_l_lower", "rear_l_paw");
            float bindRR = JointAngle(bones, "rear_r_upper", "rear_r_lower", "rear_r_paw");
            float bindLF = JointAngle(bones, "front_l_upper", "front_l_lower", "front_l_paw");
            float bindRF = JointAngle(bones, "front_r_upper", "front_r_lower", "front_r_paw");

            // 膝がどちら向きに折れているか。**曲げの大きさを揃えても向きが逆なら
            // SMAL の差分が加算ではなく減算になる。**
            // 体の前方（前脚の付け根 - 後脚の付け根）と world up から右方向を作り、
            // 曲げ軸 Cross(upperDir, lowerDir) をそれに射影した符号を見る。
            float kneeSign = float.NaN;
            {
                Transform fu2 = FindAny(bones, FrontUpper);
                Transform ru2 = FindAny(bones, RearUpper);
                Transform u = Find(bones, "rear_l_upper");
                Transform l = Find(bones, "rear_l_lower");
                Transform w2 = Find(bones, "rear_l_paw");
                if (fu2 != null && ru2 != null && u != null && l != null && w2 != null)
                {
                    Vector3 fwd = fu2.position - ru2.position;
                    fwd.y = 0f;
                    if (fwd.sqrMagnitude > 0.000001f)
                    {
                        fwd.Normalize();
                        Vector3 right = Vector3.Cross(Vector3.up, fwd).normalized;
                        Vector3 axis = Vector3.Cross((l.position - u.position).normalized,
                                                     (w2.position - l.position).normalized);
                        if (axis.sqrMagnitude > 0.000001f)
                        {
                            kneeSign = Vector3.Dot(axis.normalized, right);
                        }
                    }
                }
            }

            // 頭の向き。体の前方・上を基準にした角度なのでスケールに依らない。
            //   headPitch : 首→頭のベクトルが水平からどれだけ上下しているか（+ が上向き）
            //   headYaw   : 同じベクトルが体の正中面からどれだけ横へ振れているか
            //   headRoll  : 頭ボーンの up が体の up からどれだけ傾いているか
            float headPitch = float.NaN, headYaw = float.NaN, headRoll = float.NaN;
            {
                Transform neck = Find(bones, "neck");
                Transform hd = Find(bones, HeadName);
                Transform fu3 = FindAny(bones, FrontUpper);
                Transform ru3 = FindAny(bones, RearUpper);
                if (neck != null && hd != null && fu3 != null && ru3 != null)
                {
                    Vector3 fwd = fu3.position - ru3.position;
                    fwd.y = 0f;
                    if (fwd.sqrMagnitude > 0.000001f)
                    {
                        fwd.Normalize();
                        Vector3 right = Vector3.Cross(Vector3.up, fwd).normalized;
                        Vector3 nh = hd.position - neck.position;
                        if (nh.sqrMagnitude > 0.000001f)
                        {
                            nh.Normalize();
                            headPitch = 90f - Vector3.Angle(nh, Vector3.up);
                            Vector3 flat = nh - Vector3.up * Vector3.Dot(nh, Vector3.up);
                            if (flat.sqrMagnitude > 0.000001f)
                            {
                                flat.Normalize();
                                headYaw = Vector3.SignedAngle(fwd, flat, Vector3.up);
                            }
                        }

                        headRoll = Vector3.SignedAngle(Vector3.up, hd.up, fwd);
                    }
                }
            }

            // 頭が実際にどこを向いているか。頭ボーンの子（Nose / Jaw / Mouth）があれば
            // それで測る。**「首→頭の位置」とは別物。**位置のずれを直しても
            // ボーンの向きが斜めなら顔は斜めのまま。
            //   noseYaw  : 頭→鼻 が体の正中面からどれだけ横へ振れているか
            //   earSkew  : 左右の耳の中点が正中面からどれだけずれているか（度）
            float noseYaw = float.NaN, earSkew = float.NaN;
            {
                Transform hd2 = Find(bones, HeadName);
                Transform nose = Find(bones, "Nose") ?? Find(bones, "Jaw") ?? Find(bones, "Mouth");
                Transform earL = Find(bones, "EarL1") ?? Find(bones, "EarL");
                Transform earR = Find(bones, "EarR1") ?? Find(bones, "EarR");
                Transform fu4 = FindAny(bones, FrontUpper);
                Transform ru4 = FindAny(bones, RearUpper);
                if (hd2 != null && fu4 != null && ru4 != null)
                {
                    Vector3 fwd = fu4.position - ru4.position;
                    fwd.y = 0f;
                    if (fwd.sqrMagnitude > 0.000001f)
                    {
                        fwd.Normalize();
                        if (nose != null)
                        {
                            Vector3 d = nose.position - hd2.position;
                            d.y = 0f;
                            if (d.sqrMagnitude > 0.000001f)
                            {
                                noseYaw = Vector3.SignedAngle(fwd, d.normalized, Vector3.up);
                            }
                        }

                        if (earL != null && earR != null)
                        {
                            Vector3 mid = (earL.position + earR.position) * 0.5f - hd2.position;
                            mid.y = 0f;
                            if (mid.sqrMagnitude > 0.000001f)
                            {
                                earSkew = Vector3.SignedAngle(fwd, mid.normalized, Vector3.up);
                            }
                        }
                    }
                }
            }

            ReplaceableModel rm = go.GetComponent<ReplaceableModel>();
            float reference = rm != null ? rm.referenceHeightMeters : 0f;

            string missing = string.Empty;
            for (int i = 0; i < Required.Length; i++)
            {
                if (Find(bones, Required[i]) == null)
                {
                    missing += (missing.Length > 0 ? "," : string.Empty) + Required[i];
                }
            }

            float ratio = withers > 0.0001f ? aabbH / withers : float.NaN;
            return $"[ANIMALGEO] {prefab.name} aabbH={aabbH:F3} withers={withers:F3} " +
                   $"aabbH/withers={ratio:F3} boneSpanY={boneSpanY:F3} bodyShare={bodyShare:F4} " +
                   $"canonSpanY={canonSpanY:F3} canonShare={canonShare:F4} " +
                   $"bodyLen={bodyLen:F3} headAboveSpine={headAbove:F3} " +
                   $"rearUpperMinusFront={((rearUpperY - frontUpperY) / lossyY):F3} " +
                   $"bindLRkn={bindLR:F0} bindRRkn={bindRR:F0} kneeSign={kneeSign:F3} " +
                   $"headPitch={headPitch:F0} headYaw={headYaw:F0} headRoll={headRoll:F0} " +
                   $"noseYaw={noseYaw:F0} earSkew={earSkew:F0} " +
                   $"bindLFel={bindLF:F0} bindRFel={bindRF:F0} " +
                   $"refH={reference:F3} bones={bones.Length} " +
                   $"missing={(missing.Length > 0 ? missing : "-")}";
        }
        finally
        {
            Object.DestroyImmediate(go);
        }
    }

    // `StreamingStereoVideoPlayer.ResolveProjectionBones` の Generic 経路と同じ選び方。
    // SkinnedMeshRenderer のボーン配列。無ければ Renderer の transform
    // （00_Dog は MeshRenderer 22 個構成でこちらに落ちる）。
    private static System.Collections.Generic.List<Transform> ProjectionBones(GameObject go)
    {
        System.Collections.Generic.List<Transform> list = new System.Collections.Generic.List<Transform>();
        SkinnedMeshRenderer[] skinned = go.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        for (int r = 0; r < skinned.Length; r++)
        {
            Transform[] bs = skinned[r] != null ? skinned[r].bones : null;
            if (bs == null)
            {
                continue;
            }

            for (int b = 0; b < bs.Length; b++)
            {
                if (bs[b] != null)
                {
                    list.Add(bs[b]);
                }
            }
        }

        if (list.Count > 0)
        {
            return list;
        }

        Renderer[] all = go.GetComponentsInChildren<Renderer>(true);
        for (int r = 0; r < all.Length; r++)
        {
            if (all[r] != null && all[r].transform != null)
            {
                list.Add(all[r].transform);
            }
        }

        return list;
    }

    // FK が実際に駆動する正規ボーンだけ。
    private static readonly string[] Canonical =
    {
        "spine", "neck", "head", "tail_base",
        "front_l_upper", "front_l_lower", "front_l_paw",
        "front_r_upper", "front_r_lower", "front_r_paw",
        "rear_l_upper", "rear_l_lower", "rear_l_paw",
        "rear_r_upper", "rear_r_lower", "rear_r_paw",
    };

    private static System.Collections.Generic.List<Transform> CanonicalBones(Transform[] bones)
    {
        System.Collections.Generic.List<Transform> list = new System.Collections.Generic.List<Transform>();
        for (int i = 0; i < Canonical.Length; i++)
        {
            Transform t = Find(bones, Canonical[i]);
            if (t != null)
            {
                list.Add(t);
            }
        }

        return list;
    }

    private static float SpanY(System.Collections.Generic.List<Transform> list, float lossyY)
    {
        float min = float.MaxValue;
        float max = float.MinValue;
        for (int i = 0; i < list.Count; i++)
        {
            float y = list[i].position.y;
            if (y < min) { min = y; }
            if (y > max) { max = y; }
        }

        return max > min ? (max - min) / lossyY : float.NaN;
    }


    // `[ANIMALANG]` と同じ定義の関節角。まっすぐからのズレ（度）。
    private static float JointAngle(Transform[] bones, string up, string lo, string paw)
    {
        Transform u = Find(bones, up);
        Transform l = Find(bones, lo);
        Transform p = Find(bones, paw);
        if (u == null || l == null || p == null)
        {
            return float.NaN;
        }

        Vector3 a = l.position - u.position;
        Vector3 b = p.position - l.position;
        if (a.sqrMagnitude < 0.000001f || b.sqrMagnitude < 0.000001f)
        {
            return float.NaN;
        }

        return Vector3.Angle(a, b);
    }


    private static Transform Find(Transform[] bones, string name)
    {
        for (int i = 0; i < bones.Length; i++)
        {
            if (bones[i] != null &&
                string.Equals(bones[i].name, name, System.StringComparison.OrdinalIgnoreCase))
            {
                return bones[i];
            }
        }

        return null;
    }

    private static Transform FindAny(Transform[] bones, string[] names)
    {
        for (int i = 0; i < names.Length; i++)
        {
            Transform t = Find(bones, names[i]);
            if (t != null)
            {
                return t;
            }
        }

        return null;
    }

    private static float MeanY(Transform[] bones, string[] names)
    {
        float sum = 0f;
        int n = 0;
        for (int i = 0; i < names.Length; i++)
        {
            Transform t = Find(bones, names[i]);
            if (t != null)
            {
                sum += t.position.y;
                n++;
            }
        }

        return n > 0 ? sum / n : float.NaN;
    }
}
