using System.Collections.Generic;
using UnityEngine;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    // Generic リグの投影からスキンのウェイトが 0 のボーンを外す（2026-10-03、既定 OFF: excludeUnweightedProjectionBones）。
    //
    // ResolveProjectionBones の Generic 分岐は SkinnedMeshRenderer.bones を全部入れるので、メッシュを動かさない
    // 補助ボーン（36_LabradorDog の head_attach・Reference・TargetRef1/2・FootIKTarget ×4）が上端・下端になる。
    // ⑦ の下端合わせがそれを選ぶフレームで足が最大 62 px 浮いていた（Labrador で 176 フレーム中 10、
    // Docs/bundle-placement.md「2026-10-02: Animal の接近」）。
    //
    // 注意: ウェイトは Mesh.GetBonesPerVertex / GetAllBoneWeights で読む。Read/Write が無効なメッシュは実機ビルドで
    // CPU 側のデータが無く読めない可能性がある。読めないときはそのメッシュでは何も外さない（従来どおり）。
    private readonly Dictionary<Mesh, bool[]> weightedBonesByMesh = new Dictionary<Mesh, bool[]>();
    private readonly HashSet<Mesh> loggedUnreadableWeightMeshes = new HashSet<Mesh>();

    private bool IsSkinWeightedBone(SkinnedMeshRenderer smr, int boneIndex)
    {
        Mesh mesh = smr != null ? smr.sharedMesh : null;
        if (mesh == null)
        {
            return true;
        }

        if (!weightedBonesByMesh.TryGetValue(mesh, out bool[] weighted))
        {
            weighted = null;
            try
            {
                var bonesPerVertex = mesh.GetBonesPerVertex();
                var weights = mesh.GetAllBoneWeights();
                if (bonesPerVertex.Length > 0 && weights.Length > 0)
                {
                    int boneCount = smr.bones != null ? smr.bones.Length : 0;
                    weighted = new bool[Mathf.Max(boneCount, 1)];
                    for (int i = 0; i < weights.Length; i++)
                    {
                        BoneWeight1 w = weights[i];
                        if (w.weight > 0f && w.boneIndex >= 0 && w.boneIndex < weighted.Length)
                        {
                            weighted[w.boneIndex] = true;
                        }
                    }

                    int kept = 0;
                    for (int b = 0; b < weighted.Length; b++)
                    {
                        if (weighted[b]) kept++;
                    }

                    Debug.Log($"[PROJWEIGHT] mesh={mesh.name} bones={boneCount} weighted={kept} excluded={boneCount - kept}");
                }
            }
            catch (System.Exception ex)
            {
                if (loggedUnreadableWeightMeshes.Add(mesh))
                {
                    Debug.LogWarning($"[PROJWEIGHT] mesh={mesh.name} のウェイトを読めない（{ex.Message}）。このメッシュでは補助ボーンを外さない");
                }

                weighted = null;
            }

            weightedBonesByMesh[mesh] = weighted;
        }

        return weighted == null || boneIndex < 0 || boneIndex >= weighted.Length || weighted[boneIndex];
    }
}
