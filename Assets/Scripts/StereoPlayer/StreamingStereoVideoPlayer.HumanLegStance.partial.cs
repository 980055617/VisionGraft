using UnityEngine;

// 人の足の開きをデータより狭くしない（2026-10-26 夜、既定 OFF・試作。ユーザー「テレビのお化けなんか足おかしいけど直せる？」）。
//
// 起きていたこと（Docs/tmp/survey_20261025/README.md「10/26 夜 足」、scratchpad validate/combined/legs）: テレビの動画の既定1（18_Female_Meshy）だけ、
// 歩いている間ずっと足首どうしがくっつき、すねが X に交差して見えた（既定2 のゾンビ 20 は離れている）。骨は交差していない（vf 370〜470 の骨のダンプで
// 足首の間 0/293 が負、足首の間 ÷ 股の幅は 18 も 20 も 0.92）。違いはリグの股の幅: Meshy の自動のリグが 18 の左右の股の関節を 9.7 cm しか離して置かず
// （20 は 13.5 cm、17 は 13.8 cm、ワンピースで脚の付け根が見えないため）、すねの太さ（約 6.4 cm）に対して足首の間が狭い。AimAt は区間の向きだけを
// データに合わせるので、同じ向きでも股の狭いモデルは足首が寄る（データの足首の間は股の幅の 1.06 倍 ≈ 0.14 m、元の動画でも足は離れている）。
//
// 直し: AimAt の後、足首（Foot の骨）の左右の間を、データの足首の間（keypoints、股の左右の軸の上）に「モデルの脚の長さ ÷ データの脚の長さ」を
// 掛けた値と比べ、モデルの方が狭いときだけ、足りない分の半分ずつ左右の太ももを股の関節のまわりに外へ回す（脚の形・膝の曲がりはそのまま、
// 足首が外へ動く）。広い（データより開いている）モデル・データの足が交差しているフレームは何もしない（狭めない）。1 本の回しは humanLegStanceMaxDegrees まで。
// keypoints は生成側の EMA で約 0.1 秒遅れるが、足の開きはゆっくり変わるので使う（向きは SMPL の目標のまま）。
// 読むのは追従の SMPL の経路だけ（PosePipeline の AimAt の後・足の高さ合わせの前）。動作・frame out の続きのクリップの脚には掛けない。
public partial class StreamingStereoVideoPlayer
{
    [Header("Human leg stance (2026-10-26)")]
    public bool humanLegStanceFromData = false;
    [Range(0f, 20f)] public float humanLegStanceMaxDegrees = 8f;
    public bool logHumanLegStance = false;
    [Min(1)] public int logHumanLegStanceEveryNFrames = 30;

    private int humanLegStanceLastLogFrame = -1;

    // 足りない足首の間（m、world）。データの足首の間 × (モデルの脚 ÷ データの脚) − モデルの足首の間。負・0（モデルの方が広い・同じ）は 0。
    internal static float ResolveHumanLegStanceShortfall(float dataAnkleSeparation, float dataLegLength, float modelAnkleSeparation, float modelLegLength)
    {
        if (!(dataLegLength > 1e-4f) || !(modelLegLength > 1e-4f) || !(dataAnkleSeparation > 0f))
        {
            return 0f;
        }

        return Mathf.Max(0f, dataAnkleSeparation * (modelLegLength / dataLegLength) - modelAnkleSeparation);
    }

    private void ApplyHumanLegStanceFromData(HumanoidRigCache cache, Vector3[] jointsWorld, byte[] vis, uint trackId, int frame)
    {
        if (!humanLegStanceFromData || cache == null || !cache.ready ||
            !TrackedJointPoints.TryGet(jointsWorld, vis, SmplLeftHip, out Vector3 dLeftHip) ||
            !TrackedJointPoints.TryGet(jointsWorld, vis, SmplRightHip, out Vector3 dRightHip) ||
            !TrackedJointPoints.TryGet(jointsWorld, vis, SmplLeftKnee, out Vector3 dLeftKnee) ||
            !TrackedJointPoints.TryGet(jointsWorld, vis, SmplRightKnee, out Vector3 dRightKnee) ||
            !TrackedJointPoints.TryGet(jointsWorld, vis, SmplLeftAnkle, out Vector3 dLeftAnkle) ||
            !TrackedJointPoints.TryGet(jointsWorld, vis, SmplRightAnkle, out Vector3 dRightAnkle) ||
            !cache.bones.TryGetValue(HumanBodyBones.LeftUpperLeg, out Transform leftUpper) || leftUpper == null ||
            !cache.bones.TryGetValue(HumanBodyBones.RightUpperLeg, out Transform rightUpper) || rightUpper == null ||
            !cache.bones.TryGetValue(HumanBodyBones.LeftLowerLeg, out Transform leftLower) || leftLower == null ||
            !cache.bones.TryGetValue(HumanBodyBones.RightLowerLeg, out Transform rightLower) || rightLower == null ||
            !cache.bones.TryGetValue(HumanBodyBones.LeftFoot, out Transform leftFoot) || leftFoot == null ||
            !cache.bones.TryGetValue(HumanBodyBones.RightFoot, out Transform rightFoot) || rightFoot == null)
        {
            return;
        }

        Vector3 dLateral = dLeftHip - dRightHip;
        Vector3 mLateral = leftUpper.position - rightUpper.position;
        if (dLateral.sqrMagnitude < 1e-8f || mLateral.sqrMagnitude < 1e-10f)
        {
            return;
        }

        dLateral.Normalize();
        mLateral.Normalize();
        float dLeg = 0.5f * (Vector3.Distance(dLeftHip, dLeftKnee) + Vector3.Distance(dLeftKnee, dLeftAnkle) +
                             Vector3.Distance(dRightHip, dRightKnee) + Vector3.Distance(dRightKnee, dRightAnkle));
        float mLeg = 0.5f * (Vector3.Distance(leftUpper.position, leftLower.position) + Vector3.Distance(leftLower.position, leftFoot.position) +
                             Vector3.Distance(rightUpper.position, rightLower.position) + Vector3.Distance(rightLower.position, rightFoot.position));
        float dSeparation = Vector3.Dot(dLeftAnkle - dRightAnkle, dLateral);
        float mSeparation = Vector3.Dot(leftFoot.position - rightFoot.position, mLateral);
        float shortfall = ResolveHumanLegStanceShortfall(dSeparation, dLeg, mSeparation, mLeg);
        float leftDeg = 0f;
        float rightDeg = 0f;
        if (shortfall > 0f)
        {
            leftDeg = RotateHumanLegOutward(leftUpper, leftFoot, mLateral, 0.5f * shortfall);
            rightDeg = RotateHumanLegOutward(rightUpper, rightFoot, -mLateral, 0.5f * shortfall);
        }

        if (logHumanLegStance && frame != humanLegStanceLastLogFrame && frame % Mathf.Max(1, logHumanLegStanceEveryNFrames) == 0)
        {
            humanLegStanceLastLogFrame = frame;
            float after = Vector3.Dot(leftFoot.position - rightFoot.position, mLateral);
            Debug.Log($"[LEGSTANCE] f={frame} track={trackId} model={cache.sourcePrefabName ?? "?"} " +
                      $"dataSep={dSeparation:F4} dataLeg={dLeg:F4} dataHipW={Vector3.Distance(dLeftHip, dRightHip):F4} " +
                      $"modelSep={mSeparation:F4} modelLeg={mLeg:F4} modelHipW={Vector3.Distance(leftUpper.position, rightUpper.position):F4} " +
                      $"want={dSeparation * (mLeg / Mathf.Max(1e-4f, dLeg)):F4} shortfall={shortfall:F4} after={after:F4} deg L={leftDeg:F2} R={rightDeg:F2}");
        }
    }

    // 太ももを股の関節のまわりに回して、足首を outward へ meters だけ動かす（股 → 足首の向きを、その向きと outward の張る面の中で回す = 外への開き）。
    // 回した角（度）を返す。humanLegStanceMaxDegrees で止める。
    private float RotateHumanLegOutward(Transform upper, Transform foot, Vector3 outward, float meters)
    {
        Vector3 from = foot.position - upper.position;
        if (from.sqrMagnitude < 1e-10f || meters <= 0f)
        {
            return 0f;
        }

        Vector3 to = from + outward * meters;
        Quaternion turn = Quaternion.FromToRotation(from, to);
        float deg = Quaternion.Angle(Quaternion.identity, turn);
        float maxDeg = Mathf.Max(0f, humanLegStanceMaxDegrees);
        if (deg > maxDeg && deg > 1e-4f)
        {
            turn = Quaternion.Slerp(Quaternion.identity, turn, maxDeg / deg);
            deg = maxDeg;
        }

        TransformWriter.ApplyWorldRotation(upper, turn * upper.rotation);
        return deg;
    }
}
