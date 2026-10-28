using UnityEngine;

public struct AnimalSmalPose
{
    public bool hasGlobalOrient;
    public Quaternion globalOrient;
    public Quaternion[] bodyPose; // length 34: SMAL joints 1-34 mapped to indices 0-33
    public float[] betas;
    public bool hasTransl;
    public Vector3 transl;
    public Quaternion camRotation;
    // この tick だけ姿勢のならしを飛ばす SMAL の関節（ビット j = 関節 j、2026-10-28 夜）。0 なら全関節をならす（今まで）。
    // 伏せの決め打ちの前足をほどく間に使う（StreamingStereoVideoPlayer.AnimalLying.partial.cs の animalLyingFrontReleaseSkipsSmoothing）。
    public ulong skipSmoothingMask;
    // 体を起こす重み（2026-10-28 夜、伏せを作り直した形 animalLyingPoseRemade）。0 なら今まで。平滑した根を、上下まわりだけの根
    // （ResolveDefaultPoseRoot）へこの重みで寄せ、首（関節 15）は寄せる前の world の向きを保つ（頭は動画の猫の向きのまま）。SMAL の骨のリグだけ。
    public float rootUprightWeight;
    // 尾（関節 25〜31）を tailOverrideLocal（7 個、SMAL の局所）へ寄せる重み。尾のならし（smalNativeSmoothTail）の後に混ぜるので、0 へ戻ると今の尾に続く。
    public float tailOverrideWeight;
    public Quaternion[] tailOverrideLocal;
}
