using System.Collections.Generic;
using UnityEngine;

// 人の地面についている足を平らに置く（2026-10-27、新しい振る舞い: humanFootFlatOnStance。プレビュー k2 を見たユーザーが採用して既定 ON）。
// ユーザー（10/27、プレビュー j を実機で見て）「女性のお化け，歩いているときと立っているときにつま先が上向ている気がする．直せる？」。
// 測ったこと（Docs/tmp/survey_20261025/README.md「10/27: j 版を Quest で見た感想」、scratchpad validate/combined/toes/）: 足の向き（Foot → Toes = 足首 → 指の付け根）の、
//   モデル本来の立ち足（FBX の基準の姿勢。18・99 は 43.6° 下、17 は 44.0°、19 は 33.7°、20 は 35.1°）からのずれは、今（足の AimAt が「足首 → 親指の先」へ向ける）で
//   テレビの女性の立っている所 約 +15°（つま先が上がる）、サッカーの 17 は +9°。案 3（footAimAtModelRestDirection、SMPL の足首の回転に合わせる）では逆に
//   約 −15° / −19°（下がりすぎ）。データ（4D-Humans）は立っている間も膝が 21〜25° 曲がり、足首はほぼ曲がっていない（足が床に平らでない推定）ので、
//   どちらの足の向きもデータ由来のずれを持ち、元の動画の平らな足と合わない。
// しくみ: AimAt（どの経路でも）の後、左右の足ごとに、足首（Foot の骨）が低い方の足首からどれだけ上か（脚の長さに対する割合）で「地面についている度合い」
//   w（0〜1）を決め、足の向き（Foot → Toes）の上下の角度を、モデル本来の立ち足の角度へ w だけ寄せる。回すのは足首のまわりに、足の向きの左右の軸でだけ
//   （つま先の左右の向き・ねじれ・脚の骨はそのまま。位置は解かないので IK ではない）。立ち足の角度は SkinnedMeshRenderer の bindposes（基準の姿勢の骨の位置）を
//   model の root の上向きで測る（Avatar の humanDescription は使わない。bindposes は実行時の API だが、Quest の上で読めたかはまだ確かめていない:
//   logHumanFootFlat の [FOOTFLAT] rest の行で確かめる。読めなければ NaN で何もしない）。
// 掛けるのは追従の SMPL の経路だけ（PosePipeline の AimAt の後）。動作・frame out の続きのクリップの脚には掛けない。種別・モデルで分けない（人のモデル全部）。
public partial class StreamingStereoVideoPlayer
{
    [Header("Human foot flat on stance (2026-10-27)")]
    // 既定 ON（2026-10-27、プレビュー k2 を実機で見たユーザー「テレビ女性は結構よくなったからそれでいい」。k2 ではビルドの間だけ ON にしていた）。
    public bool humanFootFlatOnStance = true;
    // 地面についているとみなす高さ（低い方の足首からの高さ ÷ 脚の長さ）。これ以下で w = 1、この 2 倍で w = 0（間はまっすぐに）。
    [Range(0.01f, 0.5f)] public float humanFootFlatStanceHeightFraction = 0.06f;
    public bool logHumanFootFlat = false;
    [Min(1)] public int logHumanFootFlatEveryNFrames = 30;

    private const float HumanFootFlatUnknown = float.NaN;
    // モデル本来の立ち足の Foot → Toes の上下の角度（度、root の上向きに対して。下向きが負）。左・右。測れなければ NaN。
    private readonly Dictionary<HumanoidRigCache, Vector2> humanFootRestElevationByCache = new Dictionary<HumanoidRigCache, Vector2>();
    private int humanFootFlatLastLogFrame = -1;

    // 足の向き v を、上向き up に対する上下の角度が targetElevationDeg になるよう、足の向きの左右の軸で回す回転（純粋な計算、テスト HumanFootFlatTests）。
    // v が上向きと平行なら単位回転。
    internal static Quaternion ResolveFootPitchRotation(Vector3 v, Vector3 up, float targetElevationDeg)
    {
        float len = v.magnitude;
        if (len < 1e-8f || up.sqrMagnitude < 1e-8f)
        {
            return Quaternion.identity;
        }

        up = up.normalized;
        Vector3 heading = v - up * Vector3.Dot(v, up);
        if (heading.sqrMagnitude < 1e-10f)
        {
            return Quaternion.identity;
        }

        heading.Normalize();
        float now = Mathf.Asin(Mathf.Clamp(Vector3.Dot(v, up) / len, -1f, 1f)) * Mathf.Rad2Deg;
        // heading を cross(heading, up) のまわりに +θ 回すと up へ寄る（(h × up) × h = up）。
        return Quaternion.AngleAxis(targetElevationDeg - now, Vector3.Cross(heading, up));
    }

    // 地面についている度合い（純粋な計算）: 低い方の足首からの高さ ÷ (脚の長さ × fraction) が 1 以下で 1、2 以上で 0。
    internal static float ResolveFootStanceWeight(float heightAboveLowest, float legLength, float fraction)
    {
        float unit = Mathf.Max(1e-6f, legLength * Mathf.Max(1e-4f, fraction));
        return Mathf.Clamp01(2f - Mathf.Max(0f, heightAboveLowest) / unit);
    }

    private static float ElevationDegrees(Vector3 v, Vector3 up)
    {
        float len = v.magnitude;
        return len < 1e-8f ? 0f : Mathf.Asin(Mathf.Clamp(Vector3.Dot(v, up.normalized) / len, -1f, 1f)) * Mathf.Rad2Deg;
    }

    // モデル本来の立ち足の角度（bindposes から。一度だけ測る）。
    private Vector2 ResolveHumanFootRestElevation(HumanoidRigCache cache, GameObject instance)
    {
        if (humanFootRestElevationByCache.TryGetValue(cache, out Vector2 known))
        {
            return known;
        }

        Vector2 result = new Vector2(HumanFootFlatUnknown, HumanFootFlatUnknown);
        Transform root = instance.transform;
        for (int s = 0; s < 2; s++)
        {
            HumanBodyBones footBone = s == 0 ? HumanBodyBones.LeftFoot : HumanBodyBones.RightFoot;
            HumanBodyBones toesBone = s == 0 ? HumanBodyBones.LeftToes : HumanBodyBones.RightToes;
            if (!cache.bones.TryGetValue(footBone, out Transform foot) || foot == null ||
                !cache.bones.TryGetValue(toesBone, out Transform toes) || toes == null)
            {
                continue;
            }

            foreach (SkinnedMeshRenderer smr in instance.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr == null || smr.sharedMesh == null)
                {
                    continue;
                }

                Transform[] smrBones = smr.bones;
                Matrix4x4[] binds = smr.sharedMesh.bindposes;
                int fi = System.Array.IndexOf(smrBones, foot);
                int ti = System.Array.IndexOf(smrBones, toes);
                if (fi < 0 || ti < 0 || fi >= binds.Length || ti >= binds.Length)
                {
                    continue;
                }

                // bindpose = (骨の基準の姿勢の行列)⁻¹ × (メッシュの行列)。メッシュ（smr の transform）は root の動かない子なので、root の局所へ直してよい。
                Vector3 footMesh = binds[fi].inverse.MultiplyPoint3x4(Vector3.zero);
                Vector3 toesMesh = binds[ti].inverse.MultiplyPoint3x4(Vector3.zero);
                Vector3 footRoot = root.InverseTransformPoint(smr.transform.TransformPoint(footMesh));
                Vector3 toesRoot = root.InverseTransformPoint(smr.transform.TransformPoint(toesMesh));
                float elevation = ElevationDegrees(toesRoot - footRoot, Vector3.up);
                if (s == 0)
                {
                    result.x = elevation;
                }
                else
                {
                    result.y = elevation;
                }

                break;
            }
        }

        humanFootRestElevationByCache[cache] = result;
        if (logHumanFootFlat)
        {
            Debug.Log($"[FOOTFLAT] rest model={cache.sourcePrefabName} left={result.x:F1} right={result.y:F1} (deg, Foot->Toes from the bindposes)");
        }

        return result;
    }

    // PosePipeline の AimAt の後（足の開きの直しの後）に呼ぶ。OFF なら何もしない。
    private void ApplyHumanFootFlatOnStance(HumanoidRigCache cache, GameObject instance, uint trackId, int frame)
    {
        if (!humanFootFlatOnStance || cache == null || !cache.ready || instance == null ||
            !cache.bones.TryGetValue(HumanBodyBones.LeftFoot, out Transform leftFoot) || leftFoot == null ||
            !cache.bones.TryGetValue(HumanBodyBones.RightFoot, out Transform rightFoot) || rightFoot == null ||
            !cache.bones.TryGetValue(HumanBodyBones.LeftToes, out Transform leftToes) || leftToes == null ||
            !cache.bones.TryGetValue(HumanBodyBones.RightToes, out Transform rightToes) || rightToes == null ||
            !cache.bones.TryGetValue(HumanBodyBones.LeftUpperLeg, out Transform leftUpper) || leftUpper == null ||
            !cache.bones.TryGetValue(HumanBodyBones.LeftLowerLeg, out Transform leftLower) || leftLower == null)
        {
            return;
        }

        Vector2 rest = ResolveHumanFootRestElevation(cache, instance);
        Vector3 up = instance.transform.up;
        float legLength = Vector3.Distance(leftUpper.position, leftLower.position) + Vector3.Distance(leftLower.position, leftFoot.position);
        float hLeft = Vector3.Dot(leftFoot.position, up);
        float hRight = Vector3.Dot(rightFoot.position, up);
        float hMin = Mathf.Min(hLeft, hRight);
        bool log = logHumanFootFlat && (humanFootFlatLastLogFrame < 0 || frame - humanFootFlatLastLogFrame >= logHumanFootFlatEveryNFrames || frame < humanFootFlatLastLogFrame);
        System.Text.StringBuilder sb = log ? new System.Text.StringBuilder() : null;
        for (int s = 0; s < 2; s++)
        {
            Transform foot = s == 0 ? leftFoot : rightFoot;
            Transform toes = s == 0 ? leftToes : rightToes;
            float restElevation = s == 0 ? rest.x : rest.y;
            if (float.IsNaN(restElevation))
            {
                continue;
            }

            Vector3 v = toes.position - foot.position;
            float now = ElevationDegrees(v, up);
            float w = ResolveFootStanceWeight((s == 0 ? hLeft : hRight) - hMin, legLength, humanFootFlatStanceHeightFraction);
            float target = now + w * (restElevation - now);
            if (w > 0f && Mathf.Abs(target - now) > 0.01f)
            {
                foot.rotation = ResolveFootPitchRotation(v, up, target) * foot.rotation;
            }

            if (sb != null)
            {
                sb.Append(s == 0 ? " L" : " R").Append(" rest=").Append(restElevation.ToString("F1")).Append(" now=").Append(now.ToString("F1"))
                  .Append(" w=").Append(w.ToString("F2")).Append(" set=").Append(target.ToString("F1"));
            }
        }

        if (sb != null)
        {
            humanFootFlatLastLogFrame = frame;
            Debug.Log($"[FOOTFLAT] track={trackId} f={frame} model={cache.sourcePrefabName}{sb}");
        }
    }
}
