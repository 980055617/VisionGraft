using UnityEngine;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    // 脚の骨長比を meta.bin の keypoints3d に合わせる補正。
    //
    // 胴（Pelvis→Neck）で正規化した比で合わせるため、全体スケール（bbox 由来）とは独立。
    // モデルを切り替えると TrackInstanceLifecycle が古いインスタンスを破棄して
    // 作り直すので、新しいモデルにも生成時に自動で掛かる。
    //
    // 補正しすぎると脚だけ不自然に伸びるため上限を設ける。実測の必要倍率は
    // 大腿 1.035 / 下腿 1.151 なので、通常はこの範囲に収まる。

    private const float HumanBoneLengthFactorMin = 0.7f;
    private const float HumanBoneLengthFactorMax = 1.5f;

    private void TryApplyHumanBoneLengthCorrection(GameObject instance, MetaObj human)
    {
        if (instance == null || !IsCategoryPerson(human.categoryId))
        {
            return;
        }

        HumanBoneLengthCorrection correction =
            instance.GetComponent<HumanBoneLengthCorrection>();

        // 再生中にフラグを切ったら元の骨長へ戻す。Inspector で ON/OFF を切り替えて
        // 見比べられるようにするため、適用と復元の両方をここで扱う。
        if (!enableHumanBoneLengthCorrection)
        {
            if (correction != null && correction.hasApplied)
            {
                correction.Revert();
            }

            return;
        }

        if (correction == null)
        {
            correction = instance.AddComponent<HumanBoneLengthCorrection>();
        }

        if (correction.hasApplied)
        {
            return;
        }

        Animator animator = instance.GetComponentInChildren<Animator>(true);
        if (animator == null || !animator.isHuman)
        {
            return;
        }

        if (!TryResolveLegBoneLengthFactors(
                human,
                animator,
                out float thighFactor,
                out float shinFactor))
        {
            return;
        }

        correction.Apply(animator, thighFactor, shinFactor);

        if (logHumanBoneLengthCorrection)
        {
            Debug.Log(
                $"[BONEFIX] track={human.trackId} thighFactor={thighFactor:F3} " +
                $"shinFactor={shinFactor:F3} model={instance.name}");
        }
    }


    // keypoints3d とモデル、それぞれの「胴で正規化した脚の骨長」を比べて倍率を出す。
    // 胴で割ることでカメラ距離やモデルのスケールに依存しない比較になる。
    private bool TryResolveLegBoneLengthFactors(
        MetaObj human,
        Animator animator,
        out float thighFactor,
        out float shinFactor)
    {
        thighFactor = 1f;
        shinFactor = 1f;

        Vector3[] joints = human.jointsCam;
        byte[] vis = human.jointsVis;
        if (joints == null || joints.Length < HumanSourceKeypointMinimumCount)
        {
            return false;
        }

        float sourceThigh = ResolveVisibleSegmentLength(
            joints, vis, HumanSourceKeypointRightHip, HumanSourceKeypointRightKnee);
        if (sourceThigh <= 0f)
        {
            sourceThigh = ResolveVisibleSegmentLength(
                joints, vis, HumanSourceKeypointLeftHip, HumanSourceKeypointLeftKnee);
        }

        float sourceShin = ResolveVisibleSegmentLength(
            joints, vis, HumanSourceKeypointRightKnee, HumanSourceKeypointRightAnkle);
        if (sourceShin <= 0f)
        {
            sourceShin = ResolveVisibleSegmentLength(
                joints, vis, HumanSourceKeypointLeftKnee, HumanSourceKeypointLeftAnkle);
        }

        float sourceTorso = ResolveVisibleSegmentLength(
            joints, vis, HumanSourceKeypointPelvis, HumanSourceKeypointNeck);
        if (sourceThigh <= 0f || sourceShin <= 0f || sourceTorso <= 0f)
        {
            return false;
        }

        HumanoidRigCache cache = GetOrBuildHumanoidCache(animator);
        if (cache == null || !cache.ready)
        {
            return false;
        }

        float modelThigh = ResolveBoneDistance(
            cache, HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg);
        float modelShin = ResolveBoneDistance(
            cache, HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot);
        float modelTorso = ResolveBoneDistance(
            cache, HumanBodyBones.Hips, HumanBodyBones.Neck);
        if (modelThigh <= 0f || modelShin <= 0f || modelTorso <= 0f)
        {
            return false;
        }

        thighFactor = Mathf.Clamp(
            (sourceThigh / sourceTorso) / (modelThigh / modelTorso),
            HumanBoneLengthFactorMin,
            HumanBoneLengthFactorMax);
        shinFactor = Mathf.Clamp(
            (sourceShin / sourceTorso) / (modelShin / modelTorso),
            HumanBoneLengthFactorMin,
            HumanBoneLengthFactorMax);
        return true;
    }


    private static float ResolveBoneDistance(
        HumanoidRigCache cache,
        HumanBodyBones a,
        HumanBodyBones b)
    {
        if (!cache.bones.TryGetValue(a, out Transform ta) || ta == null ||
            !cache.bones.TryGetValue(b, out Transform tb) || tb == null)
        {
            return 0f;
        }

        return Vector3.Distance(ta.position, tb.position);
    }
}
