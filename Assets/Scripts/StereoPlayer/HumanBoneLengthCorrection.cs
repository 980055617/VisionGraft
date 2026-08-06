using System.Collections.Generic;
using UnityEngine;

// 表示モデルと元映像の脚の骨長比を合わせる。
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

    public void Apply(Animator animator, float thighFactor, float shinFactor)
    {
        if (animator == null || !animator.isHuman)
        {
            return;
        }

        // 大腿の長さは UpperLeg→LowerLeg なので LowerLeg の localPosition が担う。
        // 同様に下腿は LowerLeg→Foot なので Foot の localPosition。
        ApplyToBone(animator, HumanBodyBones.LeftLowerLeg, thighFactor);
        ApplyToBone(animator, HumanBodyBones.RightLowerLeg, thighFactor);
        ApplyToBone(animator, HumanBodyBones.LeftFoot, shinFactor);
        ApplyToBone(animator, HumanBodyBones.RightFoot, shinFactor);

        hasApplied = true;
        appliedThighFactor = thighFactor;
        appliedShinFactor = shinFactor;
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
