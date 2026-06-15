using System.Collections.Generic;
using UnityEngine;

internal sealed class HumanoidRigCache
{
    public readonly Dictionary<HumanBodyBones, Transform> bones = new Dictionary<HumanBodyBones, Transform>();
    public readonly Dictionary<HumanBodyBones, Quaternion> bindRotLocal = new Dictionary<HumanBodyBones, Quaternion>();
    public readonly Dictionary<HumanBodyBones, Quaternion> bindRotWorld = new Dictionary<HumanBodyBones, Quaternion>();
    // T-pose での canonical 親フレームから手ボーンの bindRotWorld への補正量。
    // 手のFK式: canonicalParent * smplLocal * handBindCorrection
    // smplLocal=identity のとき bindRotWorld[hand] に一致し、キャラクター間の軸差を吸収する。
    public readonly Dictionary<HumanBodyBones, Quaternion> handBindCorrection = new Dictionary<HumanBodyBones, Quaternion>();
    public bool ready;
}
