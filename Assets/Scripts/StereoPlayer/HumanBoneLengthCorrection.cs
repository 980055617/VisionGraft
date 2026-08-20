using System.Collections.Generic;
using UnityEngine;

// 表示モデルと元映像の四肢（脚・腕）の骨長比を合わせる。
//
// 実測（2026-08-06）で、既定の Human モデルは胴で正規化した脚が映像より 8.3% 短く
// （大腿 3.5% / 下腿 15.1%）、その結果 足首が bbox 高さの約 10% 上にずれていた。
// ボールとの接触距離は bbox 高さの 5% 程度しかないため、この差は接触の見た目を直接壊す。
//
// Humanoid Avatar の bind pose には触らず、脚ボーンの localPosition だけを伸縮させる。
// FK は ApplyWorldRotation で回転のみを与えており localPosition を読まないので、
// ここで長さを変えても姿勢計算（関節角度）には影響しない。実際 [ANGLE] の比較では
// 膝・肘の角度が meta.bin と完全一致しており、その性質は補正後も保たれる。
//
// 元の localPosition を保持しているので、係数を変えて何度適用しても結果は同じになる。
public sealed class HumanBoneLengthCorrection : MonoBehaviour
{
    private readonly Dictionary<Transform, Vector3> originalLocalPositions =
        new Dictionary<Transform, Vector3>();

    public bool hasApplied;
    public float appliedThighFactor = 1f;
    public float appliedShinFactor = 1f;
    public float appliedUpperArmFactor = 1f;
    public float appliedForeArmFactor = 1f;

    public void Apply(
        Animator animator,
        float thighFactor,
        float shinFactor,
        float upperArmFactor,
        float foreArmFactor)
    {
        if (animator == null || !animator.isHuman)
        {
            return;
        }

        // 区間の長さは「終点ボーンの localPosition」が担う。大腿は UpperLeg→LowerLeg なので
        // LowerLeg、下腿は LowerLeg→Foot なので Foot。腕も同じ関係（上腕→LowerArm、
        // 前腕→Hand）。既定 Human モデルには twist ボーンが挟まっていないことを確認済み
        // （2026-08-19、hops=1 / localPosition と world 距離の比 1.000）なので、
        // localPosition の倍率がそのまま区間長の倍率になる。
        ApplyToBone(animator, HumanBodyBones.LeftLowerLeg, thighFactor);
        ApplyToBone(animator, HumanBodyBones.RightLowerLeg, thighFactor);
        ApplyToBone(animator, HumanBodyBones.LeftFoot, shinFactor);
        ApplyToBone(animator, HumanBodyBones.RightFoot, shinFactor);
        ApplyToBone(animator, HumanBodyBones.LeftLowerArm, upperArmFactor);
        ApplyToBone(animator, HumanBodyBones.RightLowerArm, upperArmFactor);
        ApplyToBone(animator, HumanBodyBones.LeftHand, foreArmFactor);
        ApplyToBone(animator, HumanBodyBones.RightHand, foreArmFactor);

        hasApplied = true;
        appliedThighFactor = thighFactor;
        appliedShinFactor = shinFactor;
        appliedUpperArmFactor = upperArmFactor;
        appliedForeArmFactor = foreArmFactor;
    }

    // 実行中に enableHumanBoneLengthCorrection を切ったときに元の骨長へ戻す。
    // 保存済みの localPosition をそのまま書き戻すだけなので、何度呼んでも安全。
    public void Revert()
    {
        foreach (KeyValuePair<Transform, Vector3> kv in originalLocalPositions)
        {
            if (kv.Key != null)
            {
                kv.Key.localPosition = kv.Value;
            }
        }

        hasApplied = false;
        appliedThighFactor = 1f;
        appliedShinFactor = 1f;
        appliedUpperArmFactor = 1f;
        appliedForeArmFactor = 1f;
    }


    private void ApplyToBone(Animator animator, HumanBodyBones bone, float factor)
    {
        Transform boneTransform = animator.GetBoneTransform(bone);
        if (boneTransform == null)
        {
            return;
        }

        if (!originalLocalPositions.TryGetValue(boneTransform, out Vector3 original))
        {
            original = boneTransform.localPosition;
            originalLocalPositions[boneTransform] = original;
        }

        boneTransform.localPosition = original * factor;
    }
}
