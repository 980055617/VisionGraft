using UnityEngine;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    // 四肢（脚・腕）の骨長比を meta.bin の keypoints3d に合わせる補正。
    //
    // 胴（Pelvis→Neck）で正規化した比で合わせるため、全体スケール（bbox 由来）とは独立。
    // モデルを切り替えると TrackInstanceLifecycle が古いインスタンスを破棄して
    // 作り直すので、新しいモデルにも生成時に自動で掛かる。
    //
    // 補正しすぎると四肢だけ不自然に伸びるため上限を設ける。既定 Human モデル
    // （00_Female_A_01）+ bundle_human.svb での実測（2026-08-19、Pelvis(39) 基準）は
    // 大腿 1.045 / 下腿 1.164 / 上腕 1.060 / 前腕 1.090 で、通常はこの範囲に収まる。

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

        if (!TryResolveLimbBoneLengthFactors(
                human,
                animator,
                out float thighFactor,
                out float shinFactor,
                out float upperArmFactor,
                out float foreArmFactor))
        {
            return;
        }

        correction.Apply(animator, thighFactor, shinFactor, upperArmFactor, foreArmFactor);

        if (logHumanBoneLengthCorrection)
        {
            Debug.Log(
                $"[BONEFIX] track={human.trackId} thighFactor={thighFactor:F3} " +
                $"shinFactor={shinFactor:F3} upperArmFactor={upperArmFactor:F3} " +
                $"foreArmFactor={foreArmFactor:F3} model={instance.name}");
            LogLimbFactorInputs(human, animator, instance);
        }
    }


    // keypoints3d とモデル、それぞれの「胴で正規化した四肢の骨長」を比べて倍率を出す。
    // 胴で割ることでカメラ距離やモデルのスケールに依存しない比較になる。
    //
    // 胴の基準は Pelvis(39)。keypoints は 44 点の hmr2_openpose25_extra19 で、先頭 25 点が
    // OpenPose BODY_25、26 点目以降が SMPL 由来の extra19。BODY_25 側にも MidHip(8) があり
    // 意味が近いが、両者は 1.048 倍違う（2026-08-19 実測）。取り違えるとすべての比が
    // 5% ずれるので、必ず HumanSourceKeypointPelvis を使うこと。
    private bool TryResolveLimbBoneLengthFactors(
        MetaObj human,
        Animator animator,
        out float thighFactor,
        out float shinFactor,
        out float upperArmFactor,
        out float foreArmFactor)
    {
        thighFactor = 1f;
        shinFactor = 1f;
        upperArmFactor = 1f;
        foreArmFactor = 1f;

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

        // モデル側は ResolveBoneDistance(LowerLeg, Foot) を測るが、Unity Humanoid の Foot
        // ボーンは Ankle ではなく Heel の位置にある（2026-08-21 実測）。keypoints 側も
        // 膝→踵で測らないと別々の区間を比べることになり、倍率が系統的にずれる。
        float sourceShin = ResolveVisibleSegmentLength(
            joints, vis, HumanSourceKeypointRightKnee, HumanSourceKeypointRightHeel);
        if (sourceShin <= 0f)
        {
            sourceShin = ResolveVisibleSegmentLength(
                joints, vis, HumanSourceKeypointLeftKnee, HumanSourceKeypointLeftHeel);
        }

        // 踵が取れないフレームは従来どおり足首で代用する（無補正よりはまし）。
        if (sourceShin <= 0f)
        {
            sourceShin = ResolveVisibleSegmentLength(
                joints, vis, HumanSourceKeypointRightKnee, HumanSourceKeypointRightAnkle);
        }

        float sourceUpperArm = ResolveVisibleSegmentLength(
            joints, vis, HumanSourceKeypointRightShoulder, HumanSourceKeypointRightElbow);
        if (sourceUpperArm <= 0f)
        {
            sourceUpperArm = ResolveVisibleSegmentLength(
                joints, vis, HumanSourceKeypointLeftShoulder, HumanSourceKeypointLeftElbow);
        }

        float sourceForeArm = ResolveVisibleSegmentLength(
            joints, vis, HumanSourceKeypointRightElbow, HumanSourceKeypointRightWrist);
        if (sourceForeArm <= 0f)
        {
            sourceForeArm = ResolveVisibleSegmentLength(
                joints, vis, HumanSourceKeypointLeftElbow, HumanSourceKeypointLeftWrist);
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
        float modelUpperArm = ResolveBoneDistance(
            cache, HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm);
        float modelForeArm = ResolveBoneDistance(
            cache, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand);
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

        // 腕は既定 OFF（enableHumanArmLengthCorrection のコメント参照）。
        if (!enableHumanArmLengthCorrection)
        {
            return true;
        }

        // 腕は脚と違って手首より先（指）が keypoints に無い。上腕・前腕だけを合わせる。
        // 片腕でも取れなければ倍率 1（無補正）のままにして、脚だけ補正した状態にする。
        if (sourceUpperArm > 0f && modelUpperArm > 0f)
        {
            upperArmFactor = Mathf.Clamp(
                (sourceUpperArm / sourceTorso) / (modelUpperArm / modelTorso),
                HumanBoneLengthFactorMin,
                HumanBoneLengthFactorMax);
        }

        if (sourceForeArm > 0f && modelForeArm > 0f)
        {
            foreArmFactor = Mathf.Clamp(
                (sourceForeArm / sourceTorso) / (modelForeArm / modelTorso),
                HumanBoneLengthFactorMin,
                HumanBoneLengthFactorMax);
        }

        return true;
    }


    // factor の根拠になっている生の測定値をそのまま出す。prefab を直接測った値と
    // 食い違うケース（2026-08-19 の前腕 1.33 倍）を切り分けるための診断。
    private void LogLimbFactorInputs(MetaObj human, Animator animator, GameObject instance)
    {
        HumanoidRigCache cache = GetOrBuildHumanoidCache(animator);
        if (cache == null || !cache.ready)
        {
            return;
        }

        Vector3[] j = human.jointsCam;
        byte[] v = human.jointsVis;
        float sTorso = ResolveVisibleSegmentLength(j, v, HumanSourceKeypointPelvis, HumanSourceKeypointNeck);
        float sUArm = ResolveVisibleSegmentLength(j, v, HumanSourceKeypointRightShoulder, HumanSourceKeypointRightElbow);
        float sFArm = ResolveVisibleSegmentLength(j, v, HumanSourceKeypointRightElbow, HumanSourceKeypointRightWrist);
        float sThigh = ResolveVisibleSegmentLength(j, v, HumanSourceKeypointRightHip, HumanSourceKeypointRightKnee);

        float mTorso = ResolveBoneDistance(cache, HumanBodyBones.Hips, HumanBodyBones.Neck);
        float mUArm = ResolveBoneDistance(cache, HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm);
        float mFArm = ResolveBoneDistance(cache, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand);
        float mThigh = ResolveBoneDistance(cache, HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg);

        Vector3 lossy = instance.transform.lossyScale;
        string names = string.Empty;
        if (cache.bones.TryGetValue(HumanBodyBones.LeftLowerArm, out Transform la) && la != null &&
            cache.bones.TryGetValue(HumanBodyBones.LeftHand, out Transform lh) && lh != null)
        {
            names = $" lowerArm={la.name} hand={lh.name} handParent={(lh.parent != null ? lh.parent.name : "-")}" +
                    $" handLocalMag={lh.localPosition.magnitude:F4}";
        }

        Debug.Log(
            $"[BONEIN] source(m) torso={sTorso:F4} uArm={sUArm:F4} fArm={sFArm:F4} thigh={sThigh:F4}" +
            $" | model(world) torso={mTorso:F4} uArm={mUArm:F4} fArm={mFArm:F4} thigh={mThigh:F4}" +
            $" | lossyScale={lossy.x:F4} localScale={instance.transform.localScale.x:F4}" +
            $" | model/lossy torso={mTorso / Mathf.Max(1e-6f, lossy.x):F4} fArm={mFArm / Mathf.Max(1e-6f, lossy.x):F4}" +
            names);
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
