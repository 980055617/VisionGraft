using System.Collections.Generic;
using UnityEngine;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    // 投影の上端・下端をシルエット相当にする（2026-10-03、既定 OFF: useSilhouetteProjectionExtent）。
    //
    // スケールロック・⑧・⑦ は「Humanoid の全ボーンの投影の上端〜下端 = bbox の高さ」に合わせている。
    // bbox は人のシルエット（髪の上〜靴底）、ボーンは関節（眼・Jaw・Head 関節・指〜つま先の関節）なので、
    // モデルの骨格は人の関節より 1.13〜1.20 倍大きく置かれ、その量がリグ（眼ボーンの有無で 5.3%）と
    // 姿勢（どのボーンが上端か）で変わっていた（Docs/bundle-placement.md「2026-10-02: Human のモデルごとの誤差」）。
    // bbox と同じ定義（表面の上端〜下端）で測るため、眼と Jaw を外し、頭頂と足裏の代理点を足す。
    // 代理点の距離はモデル自身の形状（既定姿勢のメッシュの上端・下端）から求める。補正の係数は足さない。

    private static readonly HumanBodyBones[] SilhouetteSoleBones =
    {
        HumanBodyBones.LeftToes, HumanBodyBones.RightToes, HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot
    };

    private readonly List<KeyValuePair<string, Vector3>> silhouettePointBuffer = new List<KeyValuePair<string, Vector3>>(64);

    // 既定姿勢（GetOrBuildHumanoidCache が muscles=0 を当てる前）で頭頂・足裏の代理点の距離を測る。
    // メッシュの上端・下端は sharedMesh.bounds（bind 姿勢の頂点の AABB。Read/Write が無効でも読める）を
    // レンダラの変換で world に直して求める。既定姿勢 = bind 姿勢を前提にしている。
    // logSilhouetteExtent のときは Editor で BakeMesh の頂点から測った値も並べて出す（前提の確認用）。
    private void CaptureHumanoidSilhouetteFrame(Animator animator, HumanoidRigCache cache)
    {
        cache.hasSilhouette = false;
        cache.silhouetteSoles.Clear();
        if (animator == null || !cache.bones.TryGetValue(HumanBodyBones.Head, out Transform head) || head == null)
        {
            return;
        }

        Transform root = animator.transform;
        Vector3 up = root.up;
        float lossy = Mathf.Abs(root.lossyScale.y);
        if (lossy < 1e-6f)
        {
            return;
        }

        float top = float.MinValue;
        float bottom = float.MaxValue;
        Renderer[] renderers = root.GetComponentsInChildren<Renderer>(false);
        for (int r = 0; r < renderers.Length; r++)
        {
            Renderer renderer = renderers[r];
            if (renderer == null || !renderer.enabled)
            {
                continue;
            }

            Mesh mesh = null;
            if (renderer is SkinnedMeshRenderer smr)
            {
                mesh = smr.sharedMesh;
            }
            else
            {
                MeshFilter filter = renderer.GetComponent<MeshFilter>();
                mesh = filter != null ? filter.sharedMesh : null;
            }

            if (mesh == null)
            {
                continue;
            }

            Bounds b = mesh.bounds;
            Matrix4x4 m = renderer.transform.localToWorldMatrix;
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = b.center + Vector3.Scale(
                    b.extents,
                    new Vector3((i & 1) == 0 ? -1f : 1f, (i & 2) == 0 ? -1f : 1f, (i & 4) == 0 ? -1f : 1f));
                float h = Vector3.Dot(m.MultiplyPoint3x4(corner) - root.position, up);
                top = Mathf.Max(top, h);
                bottom = Mathf.Min(bottom, h);
            }
        }

        if (!(top > bottom))
        {
            return;
        }

        float headH = Vector3.Dot(head.position - root.position, up);
        cache.silhouetteCrownLocalDir = Quaternion.Inverse(head.rotation) * up;
        cache.silhouetteCrownDistance = (top - headH) / lossy;

        for (int i = 0; i < SilhouetteSoleBones.Length; i++)
        {
            if (!cache.bones.TryGetValue(SilhouetteSoleBones[i], out Transform bone) || bone == null)
            {
                continue;
            }

            float boneH = Vector3.Dot(bone.position - root.position, up);
            cache.silhouetteSoles.Add(new SilhouetteSolePoint
            {
                bone = SilhouetteSoleBones[i],
                localDir = Quaternion.Inverse(bone.rotation) * (-up),
                distance = Mathf.Max(0f, boneH - bottom) / lossy
            });
        }

        cache.hasSilhouette = cache.silhouetteCrownDistance > 0f && cache.silhouetteSoles.Count >= 2;

        if (logSilhouetteExtent)
        {
            string bake = Application.isEditor ? MeasureBakedMeshExtentForLog(root, up) : "bake=n/a";
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < cache.silhouetteSoles.Count; i++)
            {
                sb.Append($" {cache.silhouetteSoles[i].bone}={cache.silhouetteSoles[i].distance * lossy:F4}");
            }

            Debug.Log(
                $"[SILHOUETTE] model={root.name} use={cache.hasSilhouette} lossy={lossy:F4} " +
                $"boundsTop={top:F4} boundsBottom={bottom:F4} head={headH:F4} crown={cache.silhouetteCrownDistance * lossy:F4} " +
                $"soles(world m):{sb} {bake}");
        }
    }

    // 前提（既定姿勢 = bind 姿勢）の確認用。[MESH2D] と同じ BakeMesh(false)（倍率 1、world = 位置 + 回転 × 頂点）。
    private string MeasureBakedMeshExtentForLog(Transform root, Vector3 up)
    {
        float top = float.MinValue;
        float bottom = float.MaxValue;
        Mesh baked = new Mesh();
        try
        {
            foreach (SkinnedMeshRenderer smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(false))
            {
                if (smr == null || !smr.enabled || smr.sharedMesh == null)
                {
                    continue;
                }

                smr.BakeMesh(baked, false);
                Vector3[] verts = baked.vertices;
                Transform space = smr.transform;
                for (int i = 0; i < verts.Length; i++)
                {
                    float h = Vector3.Dot(space.position + space.rotation * verts[i] - root.position, up);
                    if (h > top) top = h;
                    if (h < bottom) bottom = h;
                }
            }
        }
        catch (System.Exception ex)
        {
            return $"bake=error({ex.Message})";
        }
        finally
        {
            Object.Destroy(baked);
        }

        return top > bottom ? $"bakeTop={top:F4} bakeBottom={bottom:F4}" : "bake=none";
    }

    // シルエット相当の投影点: Humanoid の全ボーンから眼と Jaw を除き、頭頂と足裏の代理点を足す。
    // 代理点を作れない（Head が無い等）ときは false を返し、呼び出し側は従来の全ボーンで測る。
    private bool TryBuildSilhouetteProjectionPoints(Animator animator, List<KeyValuePair<string, Vector3>> points)
    {
        points.Clear();
        if (animator == null || !animator.isHuman)
        {
            return false;
        }

        HumanoidRigCache cache = GetOrBuildHumanoidCache(animator);
        if (cache == null || !cache.ready || !cache.hasSilhouette ||
            !cache.bones.TryGetValue(HumanBodyBones.Head, out Transform head) || head == null)
        {
            return false;
        }

        foreach (KeyValuePair<HumanBodyBones, Transform> pair in cache.bones)
        {
            if (pair.Value == null ||
                pair.Key == HumanBodyBones.LeftEye || pair.Key == HumanBodyBones.RightEye || pair.Key == HumanBodyBones.Jaw)
            {
                continue;
            }

            points.Add(new KeyValuePair<string, Vector3>(pair.Key.ToString(), pair.Value.position));
        }

        float lossy = Mathf.Abs(animator.transform.lossyScale.y);
        points.Add(new KeyValuePair<string, Vector3>(
            "Crown", head.position + head.rotation * cache.silhouetteCrownLocalDir * (cache.silhouetteCrownDistance * lossy)));
        for (int i = 0; i < cache.silhouetteSoles.Count; i++)
        {
            SilhouetteSolePoint sole = cache.silhouetteSoles[i];
            if (!cache.bones.TryGetValue(sole.bone, out Transform bone) || bone == null)
            {
                continue;
            }

            points.Add(new KeyValuePair<string, Vector3>(
                "Sole" + sole.bone, bone.position + bone.rotation * sole.localDir * (sole.distance * lossy)));
        }

        return true;
    }
}
