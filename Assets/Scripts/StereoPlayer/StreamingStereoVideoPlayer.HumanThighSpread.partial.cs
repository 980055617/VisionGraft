using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

// 人の太ももの向きの目標を外へ少し回す（2026-10-29、新しい振る舞い・既定 OFF）。ユーザー「女の足とかもめっちゃくっついているけど仕様？どうしてなの？」。
// 起きていたこと（scratchpad validate/combined/legs/spread/、Docs/tmp/survey_20261025/README.md「女性の脚がくっつく件の結果」）: テレビの女性 18 は
//   立っている間ずっと足首がほぼ付く（左目の動画のカメラで 6 フレームおきの中央値: 裾より下の一番狭い所 5 px・足首 6 px、元動画の人 13 px・17 px）。
//   脚の向きは AimAt の SMPL 目標版（TryApplySmplLimbsFromSmplTarget）が人の向きにそのまま合わせている（目標との差 中央 0.03°）ので、脚の間は股の関節の
//   幅で決まる。18 の股の関節の間は 9.7 cm（身長の 6.0%、元動画の人 7.5〜7.7%）: ワンピースの下に太もものメッシュが無く、Meshy の自動のリグが股を狭く
//   置いた（モデルを入れた時から）。男性 17 は股の関節が低く太ももが短い（膝 7 px・足首 8 px、人 13 px・17 px）。19・20 は人と同じか広い。
// しくみ: その AimAt の太もも（LThigh・RThigh）の目標の向きだけ、表のモデルの角度だけ外へ回す。回す軸は、左右の股を結ぶ向きと上向きに直交する水平な軸
//   （体の前後の軸）。すねは膝の新しい位置から自分の目標へ向くので、すねの向きは人のまま、足首が外へ動く（18 で片側 約 2.9 cm、bind の単位）。
//   目標の向きを一定の角度回すだけで、足の位置を解く IK ではない（骨は今までどおり ApplyWorldRotation で書く）。
// 予測（legs/spread/predict_legs.txt、Unity を流す前に登録）: 18 δ7 裾より下 16 px・足首 18 px、δ5 13 px・17 px。17 δ5 膝 13 px・すね 17 px・足首 15 px。
// 副作用（予測）: 画面の外へ歩くクリップと身ぶりのクリップは AimAt を通らないので、切り替わりで今の幅に戻る。18 は裾がすねの重みで付いているので
//   裾が釣鐘のように広がる。足が 1.7 mm 浮く（足の高さ合わせが戻す見込み）。
// 表に無いモデル・OFF は今と全く同じ。
public partial class StreamingStereoVideoPlayer
{
    // 2026-10-29 から既定 ON（見る用の版 r5 を Quest で見たユーザー「みたよ，いいと思う」）。上の既定の説明はそれまでの話。
    public bool humanThighSpreadFix = true;
    // 回す量の表: 「prefab 名:度」をカンマで区切る（-setFields の区切りの ; と = は使わない）。prefab 名は ReplaceableModel.sourcePrefabName と完全一致。
    // 正の値は外へ、負の値は内へ。2026-10-29 から 18 は 7 → 5（7 は裾が釣鐘のように広がる。r5 の値）。
    public string humanThighSpreadTable = "18_Female_Meshy:5,17_Male_Meshy:5";

    private string humanThighSpreadParsedTable;
    private readonly Dictionary<string, float> humanThighSpreadByPrefab = new Dictionary<string, float>();

    private float HumanThighSpreadDegFor(HumanoidRigCache cache)
    {
        if (!humanThighSpreadFix || cache == null || string.IsNullOrEmpty(cache.sourcePrefabName))
        {
            return 0f;
        }

        if (humanThighSpreadParsedTable != humanThighSpreadTable)
        {
            humanThighSpreadParsedTable = humanThighSpreadTable;
            humanThighSpreadByPrefab.Clear();
            if (!string.IsNullOrEmpty(humanThighSpreadTable))
            {
                foreach (string entry in humanThighSpreadTable.Split(','))
                {
                    string[] p = entry.Trim().Split(':');
                    if (p.Length == 2 && float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float deg))
                    {
                        humanThighSpreadByPrefab[p[0].Trim()] = deg;
                    }
                }
            }

            Debug.Log($"[THIGHSPREAD] table={humanThighSpreadTable} models={humanThighSpreadByPrefab.Count}");
        }

        return humanThighSpreadByPrefab.TryGetValue(cache.sourcePrefabName, out float d) ? d : 0f;
    }

    // 太ももの目標の向き targetDir を、体の前後の軸（lateral と上向きに直交する水平な軸）のまわりに |deg| 度回したもの（純粋な計算、テスト HumanThighSpreadTests）。
    // lateral = 右の股 → 左の股（world）、outwardSign = 左の太もも +1・右 −1。deg > 0 は外へ（side = lateral × outwardSign に近づく向き）、deg < 0 は内へ。
    internal static Vector3 ResolveHumanThighSpread(Vector3 targetDir, Vector3 lateral, float outwardSign, float deg)
    {
        if (deg == 0f || lateral.sqrMagnitude < 1e-12f || targetDir.sqrMagnitude < 1e-12f)
        {
            return targetDir;
        }

        Vector3 axis = Vector3.Cross(Vector3.up, lateral);
        if (axis.sqrMagnitude < 1e-12f)
        {
            return targetDir;
        }

        axis.Normalize();
        Vector3 side = lateral.normalized * outwardSign;
        float mag = Mathf.Abs(deg);
        Vector3 a = Quaternion.AngleAxis(mag, axis) * targetDir;
        Vector3 b = Quaternion.AngleAxis(-mag, axis) * targetDir;
        bool aIsOutward = Vector3.Dot(a, side) >= Vector3.Dot(b, side);
        return (deg > 0f) == aIsOutward ? a : b;
    }

    // AimAt（SMPL の目標版）の太ももの区間から。左右の股は骨盤（Hips）について動くので、太ももを回しても lateral は変わらない。
    private Vector3 ApplyHumanThighSpread(HumanoidRigCache cache, HumanBodyBones prox, Vector3 targetDir)
    {
        float deg = HumanThighSpreadDegFor(cache);
        if (deg == 0f ||
            !cache.bones.TryGetValue(HumanBodyBones.LeftUpperLeg, out Transform left) || left == null ||
            !cache.bones.TryGetValue(HumanBodyBones.RightUpperLeg, out Transform right) || right == null)
        {
            return targetDir;
        }

        float sign = prox == HumanBodyBones.LeftUpperLeg ? 1f : -1f;
        return ResolveHumanThighSpread(targetDir, left.position - right.position, sign, deg);
    }
}
