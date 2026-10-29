using UnityEngine;

// 人の鎖骨の基準を上げる（2026-10-29、新しい振る舞い・既定 0 = OFF）。ユーザー「人モデルについて肩がすっごい斜め肩の気がするけど気のせいかな，それも直せる？直す必要ある？」。
// 起きていたこと（scratchpad validate/combined/shoulder/、Docs/tmp/survey_20261025/README.md「10/29 昼」）: Meshy の人 4 体（17・18・19・20）は動かしている間、
//   鎖骨（LeftShoulder → LeftArm の付け根）が胸の水平から 16〜26° 下がる（基準の姿勢 muscles=0 では 0〜2°）。両肩とも下がるなで肩で、サッカーの元動画の人は
//   肩が四角い。SMPL の collar（13・14）の回転は 20〜25° の下げで、SMPL の休止姿勢では鎖骨が肩へ向かって約 20° 上がっている分を自然な位置へ戻す回転
//   （meta.bin の回転を当てはめた値、推論を含む）。FK はこれを、もともと水平な Meshy の鎖骨にそのまま掛けるので、その分だけ下がりすぎる。AimAt は上腕から
//   なので関わらない。fkReferenceFromAvatarTPose でも鎖骨は T ポーズと muscles=0 が同じで直らない。
// しくみ: 13・14 だけ、FK の基準 bindW の左に「鎖骨の外の端（上腕の付け根の向き）を真上へ humanClavicleRestRaiseDeg 度近づける回転」を掛ける。向きは
//   bindW × 上腕の局所の位置、上は基準を採った区間（HumanBindRootScope、Animator の向きを単位にした world）の +Y。式 W·bodyFk·bindW と ApplyWorldRotation は
//   そのまま、IK なし。上腕の付け根が約 1 cm 上がり、手も同じだけ上がる、肩幅 +3.5〜7%（予測 shoulder/predict_fix_20.txt）。身ぶりのクリップの間は FK を通らない。
// 0 なら今のまま。
public partial class StreamingStereoVideoPlayer
{
    // 2026-10-29 から既定 20（見る用の版 r5 を Quest で見たユーザー「みたよ，いいと思う」）。上の既定の説明はそれまでの話。
    [Min(0f)] public float humanClavicleRestRaiseDeg = 20f;

    // 鎖骨の基準 bindW を、外の端（上腕の付け根の向き）が真上へ deg 度近づくように回したもの（純粋な計算、テスト HumanClavicleRestRaiseTests）。
    // 外の端がもう真上に近い（残りの角が deg 未満）ときは真上で止まる（RotateTowards）。
    internal static Quaternion ResolveHumanClavicleRestRaise(Quaternion bindW, Vector3 upperArmLocalPosition, float deg)
    {
        if (!(deg > 0f) || upperArmLocalPosition.sqrMagnitude < 1e-12f)
        {
            return bindW;
        }

        Vector3 d = (bindW * upperArmLocalPosition).normalized;
        Vector3 raised = Vector3.RotateTowards(d, Vector3.up, deg * Mathf.Deg2Rad, 0f);
        return Quaternion.FromToRotation(d, raised) * bindW;
    }

    // FK のループ（ApplyHumanSmplFk、13・14）から。上腕が鎖骨の直接の子でないリグは触らない（4 体とも直接の子、10/29 に確かめた）。
    private Quaternion ApplyHumanClavicleRestRaise(HumanoidRigCache cache, HumanBodyBones boneId, Transform shoulderBone, Quaternion bindW)
    {
        if (!(humanClavicleRestRaiseDeg > 0f) || (boneId != HumanBodyBones.LeftShoulder && boneId != HumanBodyBones.RightShoulder))
        {
            return bindW;
        }

        HumanBodyBones armId = boneId == HumanBodyBones.LeftShoulder ? HumanBodyBones.LeftUpperArm : HumanBodyBones.RightUpperArm;
        if (cache == null || !cache.bones.TryGetValue(armId, out Transform arm) || arm == null || arm.parent != shoulderBone)
        {
            return bindW;
        }

        return ResolveHumanClavicleRestRaise(bindW, arm.localPosition, humanClavicleRestRaiseDeg);
    }
}
