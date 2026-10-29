using System.Collections.Generic;
using UnityEngine;

// 投影の上端・下端に手先の点を足す（2026-10-29、新しい振る舞い・既定 OFF）。全体の洗い出しで「サッカーの 2 人が 19〜20.5 秒・24.5〜25.5 秒に腕をまっすぐな
// Y 字に上げ、手が元の人より約 35 px 高い」。
// 起きていたこと（scratchpad validate/combined/arms/、Docs/tmp/survey_20261025/README.md「人の腕と手の調査の結果」）: データは肘を曲げていて（右 43〜45°）、
//   モデルもそのとおり曲げている（全 720 フレームでモデル − SMPL 中央 +0.3〜+0.6°）。Meshy の人 4 体には指の骨が無い（Humanoid に 23 本だけ）ので、腕を
//   上げると投影の点（TryBuildSilhouetteProjectionPoints: 全ボーン − 眼・Jaw ＋ 頭頂・足裏の代理点）のいちばん上が手首になり、⑧（RefineDepthFromProjectedBones）
//   はそれを人の箱の上端（本物の指先・こぶし）に合わせるので、体全体が 11〜13% 大きく置かれた（モデル ÷ 人 0.93〜0.95 → vf 456〜480 で 1.04〜1.05）。
//   手首がいちばん上になるのは vf 442〜489 と 579〜604 だけ。
// しくみ: 頭頂・足裏と同じく「bbox と同じ定義（表面の端）」の欠けを埋める。手の骨の子孫で手の骨から一番遠い transform（Meshy の FBX の LeftHand_End・
//   RightHand_End、指の骨のあるリグでは指先の葉）を手先の点として足す。新しい係数は無い。手の骨の子孫に transform が無いリグは何も足さない。
//   **頭頂の代理点より上（体の上向き = Animator の root の up）にあるときだけ足す**（上端にだけ効かせる）: 13 秒のボールを拾う所（vf 313〜325）で
//   モデルの手先が足裏より最大 38 px（17）・29 px（19）下に来ていちばん下の点になり、⑦ がモデルを持ち上げ ⑧ が縮める（本物の人の箱の下端は足、
//   エージェントが撮影の前に見つけた）。手先の点は TryBuildSilhouetteProjectionPoints から入るので、上端・下端の記録・倍率の測り直し・⑧ の厳密解の 3 か所に効く。
// 予測（arms/predictions.txt、Unity を流す前に登録）: 19〜20 秒の右手首 17 |36| → |12| px・19 |35| → |14| px、肩 −21 → −2〜−5 px。24〜25 秒は
//   行き過ぎる（モデルの手は開き、本物はこぶし: 手首が 13〜16 px 高い → 12〜18 px 低い）。ほかのフレームは変わらない（手先がいちばん上・下にならない）。
// OFF なら今と全く同じ。
public partial class StreamingStereoVideoPlayer
{
    // 2026-10-29 から既定 ON（見る用の版 r5 を Quest で見たユーザー「みたよ，いいと思う」）。上の既定の説明はそれまでの話。
    public bool silhouetteHandTipPoints = true;

    private readonly Dictionary<HumanoidRigCache, Transform[]> silhouetteHandTipsByCache = new Dictionary<HumanoidRigCache, Transform[]>();

    // 手の骨の子孫で、手の骨から一番遠い transform（純粋な探索、テスト HandTipSilhouetteTests）。無ければ null。
    internal static Transform FindHandTipTransform(Transform hand)
    {
        if (hand == null)
        {
            return null;
        }

        Transform best = null;
        float bestDistance = 0f;
        foreach (Transform t in hand.GetComponentsInChildren<Transform>(true))
        {
            if (t == hand)
            {
                continue;
            }

            float d = (t.position - hand.position).sqrMagnitude;
            if (d > bestDistance)
            {
                bestDistance = d;
                best = t;
            }
        }

        return best;
    }

    // 手先の点を足すか（純粋な計算、テスト HandTipSilhouetteTests）: 体の上向きで頭頂より上のときだけ。
    internal static bool IsHandTipAboveCrown(Vector3 tip, Vector3 crown, Vector3 up)
    {
        return up.sqrMagnitude > 1e-12f && Vector3.Dot(tip - crown, up.normalized) > 0f;
    }

    // TryBuildSilhouetteProjectionPoints から（頭頂・足裏の後）。crown = 頭頂の代理点、up = 体の上向き（Animator の root の up）。
    private void AddSilhouetteHandTipPoints(HumanoidRigCache cache, List<KeyValuePair<string, Vector3>> points, Vector3 crown, Vector3 up)
    {
        if (!silhouetteHandTipPoints || cache == null)
        {
            return;
        }

        // キャッシュごとに 1 回探す。両方とも無い（手の子孫が無いリグ・インスタンスが壊れた）ときは探し直す（子孫の無い手なら安い）。
        bool known = silhouetteHandTipsByCache.TryGetValue(cache, out Transform[] tips) && tips != null;
        if (!known || (tips[0] == null && tips[1] == null))
        {
            cache.bones.TryGetValue(HumanBodyBones.LeftHand, out Transform left);
            cache.bones.TryGetValue(HumanBodyBones.RightHand, out Transform right);
            tips = new[] { FindHandTipTransform(left), FindHandTipTransform(right) };
            silhouetteHandTipsByCache[cache] = tips;
            if (!known)
            {
                Debug.Log($"[SILHOUETTE] hand tips L={(tips[0] != null ? tips[0].name : "none")} R={(tips[1] != null ? tips[1].name : "none")} " +
                          "(silhouetteHandTipPoints)");
            }
        }

        if (tips[0] != null && IsHandTipAboveCrown(tips[0].position, crown, up))
        {
            points.Add(new KeyValuePair<string, Vector3>("HandTipL", tips[0].position));
        }

        if (tips[1] != null && IsHandTipAboveCrown(tips[1].position, crown, up))
        {
            points.Add(new KeyValuePair<string, Vector3>("HandTipR", tips[1].position));
        }
    }
}
