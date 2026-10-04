using System;
using System.Collections.Generic;
using UnityEngine;

// 足の AimAt の点を揃える（2026-10-04、既定 OFF、調査役 HM の F1）。
//
// 足の AimAt の目標は「足首 → 親指の先」（keypoints の Body25 19/22 を SMPL24 の 10/11 に入れたもの。E の目標
// G7 × d_rest も同じ点から推定する）で、骨側は Foot → Toes（Toes ボーン = 指の付け根）。点が違うので、T ポーズで
// Foot→Toes が 30〜61° 下を向くモデルほど、目標（14〜16° 下）へ向けたときにつま先が上がる
// （HM のオフライン計算: A・B 系 17〜20°、Eric 20〜22°、Carla 35〜38°、Claudia 45〜46°）。
// 08-07 の「FK の足の誤差 20.5°/21.9°」はこの点の違いだった（Docs/smpl-retargeting.md の 2026-10-04 の節）。
//   footAimAtToeTipProxy: 骨側も「Foot → 足先の代理点」にする（AimAt は残す）。代理点は Editor で測って焼いた
//                         Resources/human_foot_tip.json（Renderpeople の FBX は isReadable: 0 で、実行時に頂点を読めない）。
//                         Foot→代理点は T ポーズで 19〜44° 下（ヒールの Carla・Claudia は 40〜44°）なので、ヒールでは差が残る
//   skipFootAimAt: 足の区間だけ AimAt を掛けない（足は FK のまま、脛の AimAt に付いて動く）
//   footAimAtModelRestDirection（反論役 C-HM の案 3）: 足の AimAt は残し、目標を「SMPL の足首の回転 × そのモデルの
//                         T ポーズの足の向き」にする: target = worldGO · bodyFk[7 / 8] · tposeRotWorld[Foot] · (Foot → Toes の局所方向)。
//                         T ポーズ基準の FK が足を置く向きへ AimAt で合わせるので、SMPL の足が静止姿勢（平らに立つ）なら、
//                         そのモデル本来の立ち足（ヒールならヒールの形）になる。骨側は Foot → Toes のまま（点の違いは目標の側で消える）
public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    [Serializable]
    private class FootTipTable
    {
        public FootTipEntry[] models;
    }

    [Serializable]
    private class FootTipEntry
    {
        public string avatar;
        public string model;
        public float[] LeftFoot;
        public float[] RightFoot;
    }

    private static Dictionary<string, FootTipEntry> footTipByAvatar;

    private static bool IsFootSegment(HumanBodyBones proxBone)
    {
        return proxBone == HumanBodyBones.LeftFoot || proxBone == HumanBodyBones.RightFoot;
    }

    // 足の区間の AimAt。keypoint 版（TryApplySmplLegsFromJointPositions）と SMPL 目標版（TryApplySmplLimbsFromSmplTarget）から呼ぶ。
    // 両方のフラグが OFF なら ApplyHumanoidSegmentDirection と同じ。
    private void ApplyFootSegmentDirection(HumanoidRigCache cache, HumanBodyBones proxBone, HumanBodyBones distBone, Vector3 targetDirection)
    {
        if (skipFootAimAt)
        {
            return;
        }

        if (footAimAtModelRestDirection && TryGetFootRestTarget(cache, proxBone, distBone, out Vector3 restTarget))
        {
            ApplyHumanoidSegmentDirection(cache, proxBone, distBone, restTarget);
            return;
        }

        if (footAimAtToeTipProxy &&
            TryGetFootTipLocal(cache, proxBone, out Vector3 tipLocal) &&
            cache.bones.TryGetValue(proxBone, out Transform proxT) && proxT != null)
        {
            RotateSegmentToward(proxT, proxT.TransformPoint(tipLocal) - proxT.position, targetDirection);
            return;
        }

        ApplyHumanoidSegmentDirection(cache, proxBone, distBone, targetDirection);
    }

    private void TryApplySmplFootSegment(HumanoidRigCache cache,
        HumanBodyBones proxBone, HumanBodyBones distBone,
        Vector3[] jointsWorld, byte[] vis, int idxA, int idxB)
    {
        if (!TrackedJointPoints.TryGet(jointsWorld, vis, idxA, out Vector3 posA)) return;
        if (!TrackedJointPoints.TryGet(jointsWorld, vis, idxB, out Vector3 posB)) return;

        ApplyFootSegmentDirection(cache, proxBone, distBone, posB - posA);
    }

    // 案 3 の目標の向き。T ポーズ（cache.tposeRotWorld）が無い、FK がまだ回っていないときは false（呼び出し側は従来の目標）。
    // Foot → Toes の局所方向は Toes が Foot の子なら姿勢に依らないので、今の姿勢から測る。
    private bool TryGetFootRestTarget(HumanoidRigCache cache, HumanBodyBones footBone, HumanBodyBones toesBone, out Vector3 target)
    {
        target = Vector3.zero;
        if (!humanSmplRetargetStateByCache.TryGetValue(cache, out HumanSmplRetargetState state) || state == null || !state.hasLastFk)
        {
            return false;
        }

        if (!cache.tposeRotWorld.TryGetValue(footBone, out Quaternion footTpose) ||
            !cache.bones.TryGetValue(footBone, out Transform foot) || foot == null ||
            !cache.bones.TryGetValue(toesBone, out Transform toes) || toes == null)
        {
            return false;
        }

        Vector3 local = Quaternion.Inverse(foot.rotation) * (toes.position - foot.position);
        if (local.sqrMagnitude < 1e-10f)
        {
            return false;
        }

        int smplJoint = footBone == HumanBodyBones.LeftFoot ? SmplLeftAnkle : SmplRightAnkle;
        target = state.lastWorldGlobalOrient * (state.smplFk[smplJoint] * (footTpose * local.normalized));
        return target.sqrMagnitude > 1e-10f;
    }

    private static bool TryGetFootTipLocal(HumanoidRigCache cache, HumanBodyBones footBone, out Vector3 local)
    {
        local = Vector3.zero;
        if (cache == null)
        {
            return false;
        }

        if (!cache.footTipResolved)
        {
            cache.footTipResolved = true;
            FootTipEntry entry = null;
            if (!string.IsNullOrEmpty(cache.avatarName))
            {
                LoadFootTipTable().TryGetValue(cache.avatarName, out entry);
            }

            if (entry != null && entry.LeftFoot != null && entry.LeftFoot.Length == 3 &&
                entry.RightFoot != null && entry.RightFoot.Length == 3)
            {
                cache.leftFootTipLocal = new Vector3(entry.LeftFoot[0], entry.LeftFoot[1], entry.LeftFoot[2]);
                cache.rightFootTipLocal = new Vector3(entry.RightFoot[0], entry.RightFoot[1], entry.RightFoot[2]);
                cache.hasFootTip = true;
            }

            Debug.Log($"[FOOTTIP] avatar={cache.avatarName} found={cache.hasFootTip} " +
                      $"L={cache.leftFootTipLocal:F4} R={cache.rightFootTipLocal:F4}");
        }

        if (!cache.hasFootTip)
        {
            return false;
        }

        local = footBone == HumanBodyBones.LeftFoot ? cache.leftFootTipLocal : cache.rightFootTipLocal;
        return true;
    }

    private static Dictionary<string, FootTipEntry> LoadFootTipTable()
    {
        if (footTipByAvatar != null)
        {
            return footTipByAvatar;
        }

        footTipByAvatar = new Dictionary<string, FootTipEntry>();
        TextAsset text = Resources.Load<TextAsset>("human_foot_tip");
        if (text == null)
        {
            Debug.LogWarning("[FOOTTIP] Resources/human_foot_tip.json が無い");
            return footTipByAvatar;
        }

        FootTipTable table = JsonUtility.FromJson<FootTipTable>(text.text);
        if (table != null && table.models != null)
        {
            foreach (FootTipEntry entry in table.models)
            {
                if (entry != null && !string.IsNullOrEmpty(entry.avatar) && !footTipByAvatar.ContainsKey(entry.avatar))
                {
                    footTipByAvatar[entry.avatar] = entry;
                }
            }
        }

        return footTipByAvatar;
    }
}
