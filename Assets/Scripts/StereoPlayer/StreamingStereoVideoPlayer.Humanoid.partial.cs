using UnityEngine;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    // Depends on: HumanoidRigCache and humanoid caches in Model.cs
    // Provides: humanoid cache build for the SMPL24 person pipeline

    // bind を採る間だけ Animator の Transform の world 回転を単位にする（humanBindRootRelative、2026-10-08、2026-10-09 に既定 ON、全関節の監査 J-01 ①）。
    // root が θ 回ったまま採ると muscles=0 の姿勢が 2θ 回る（調整役の撮影 queue_hx1: 60° → bind が +y まわり 120.00°。HumanPoseHandler の
    // GetHumanPose が体の回転を world で返し、SetHumanPose が root からの相対として当てる往復と推定。切り分けてはいない）。
    // 単位にしておけば往復は恒等で、bind は root の回転に依らない（FK の worldGO × bodyFk × bind が前提にする「root が単位のときの bind」）。
    // 戻すのは using を抜けるとき（例外でも戻る）。局所回転を控えて書き戻すので、戻した root は採る前とビット単位で同じ。
    // FK のループの外（キャッシュを作る 1 回だけ）。
    private readonly struct HumanBindRootScope : System.IDisposable
    {
        private readonly Transform root;
        private readonly Quaternion savedLocalRotation;

        public HumanBindRootScope(Transform root)
        {
            this.root = root;
            savedLocalRotation = root != null ? root.localRotation : Quaternion.identity;
            if (root != null)
            {
                Debug.Log($"[HUMAN-BIND] model={root.name} bind を root の world 回転を単位にして採る（外した回転 {Quaternion.Angle(root.rotation, Quaternion.identity):F2}°）");
                TransformWriter.ApplyWorldRotation(root, Quaternion.identity);
            }
        }

        public void Dispose()
        {
            if (root != null)
            {
                TransformWriter.ApplyLocalRotation(root, savedLocalRotation);
            }
        }
    }

    private HumanBindRootScope BeginHumanBindRootScope(Animator animator)
    {
        return new HumanBindRootScope(humanBindRootRelative && animator != null ? animator.transform : null);
    }

    private HumanoidRigCache GetOrBuildHumanoidCache(Animator animator)
    {
        if (humanoidCaches.TryGetValue(animator, out HumanoidRigCache existing))
        {
            return existing;
        }

        var cache = new HumanoidRigCache();
        cache.avatarName = animator.avatar != null ? animator.avatar.name : null;

        // First collect all bone transforms
        foreach (HumanBodyBones boneId in System.Enum.GetValues(typeof(HumanBodyBones)))
        {
            if (boneId == HumanBodyBones.LastBone)
            {
                continue;
            }

            Transform bone = animator.GetBoneTransform(boneId);
            if (bone == null)
            {
                continue;
            }

            cache.bones[boneId] = bone;
        }

        // UpperChest が Avatar 未登録の場合、LeftShoulder の親 hierarchy から Transform を取得する。
        // LeftShoulder.parent が Chest でなければその Transform が UpperChest にあたる。
        // SMPL FK の累積 local rotation 計算で UpperChest の T-pose rotation が必要なため。
        if (!cache.bones.ContainsKey(HumanBodyBones.UpperChest))
        {
            Transform chestBone = animator.GetBoneTransform(HumanBodyBones.Chest);
            Transform leftShoulderBone = animator.GetBoneTransform(HumanBodyBones.LeftShoulder);
            if (leftShoulderBone != null && chestBone != null
                && leftShoulderBone.parent != null
                && leftShoulderBone.parent != chestBone)
            {
                cache.bones[HumanBodyBones.UpperChest] = leftShoulderBone.parent;
            }
        }

        // 既定 ON（2026-10-08、J-01 ①。2026-10-09 に採用）: ここから return まで（シルエット・muscles=0・handBindCorrection・T ポーズの採取）は Animator の
        // Transform の world 回転を単位にして採る（HumanBindRootScope）。handBindCorrection は yaw には不変だが pitch・roll には不変でないので、
        // この区間に入れる。シルエットは root の回転に依らない（root.up とメッシュ・骨が一緒に回る）が、区間の始まりはその前に置く。採る順は今のまま。
        using HumanBindRootScope bindRootScope = BeginHumanBindRootScope(animator);

        // シルエット相当の投影点は既定姿勢で測る。下の muscles=0 は膝・肘が曲がった姿勢なので、その前に呼ぶ。
        CaptureHumanoidSilhouetteFrame(animator, cache);

        // Sample T-pose using HumanPoseHandler so bindRotLocal reflects true bind rotations,
        // not the animated pose that may be playing at cache-creation time.
        bool sampledTpose = false;
        if (animator.avatar != null && animator.avatar.isHuman && animator.avatar.isValid)
        {
            try
            {
                var handler = new HumanPoseHandler(animator.avatar, animator.transform);
                HumanPose savedPose = default(HumanPose);
                handler.GetHumanPose(ref savedPose);

                var tPose = new HumanPose();
                tPose.bodyPosition = savedPose.bodyPosition;
                tPose.bodyRotation = savedPose.bodyRotation;
                tPose.muscles = new float[savedPose.muscles.Length]; // all zeros = T-pose
                handler.SetHumanPose(ref tPose);

                foreach (var kv in cache.bones)
                {
                    cache.bindRotLocal[kv.Key] = kv.Value != null ? kv.Value.localRotation : Quaternion.identity;
                    cache.bindRotWorld[kv.Key] = kv.Value != null ? kv.Value.rotation : Quaternion.identity;
                }

                // T-pose 中（SetHumanPose で muscles=0 のまま）に bone.position を読む必要があるため
                // handler.SetHumanPose(ref savedPose) の前に呼ぶ。
                ComputeHandBindCorrection(cache, HumanBodyBones.LeftLowerArm,  HumanBodyBones.LeftHand);
                ComputeHandBindCorrection(cache, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand);

                handler.SetHumanPose(ref savedPose);
                sampledTpose = true;
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"Failed to sample T-pose for humanoid cache: {ex.Message}");
            }
        }

        if (!sampledTpose)
        {
            foreach (var kv in cache.bones)
            {
                cache.bindRotLocal[kv.Key] = kv.Value != null ? kv.Value.localRotation : Quaternion.identity;
                cache.bindRotWorld[kv.Key] = kv.Value != null ? kv.Value.rotation : Quaternion.identity;
            }
            // フォールバック: 現在の pose（T-pose でない可能性あり）で計算するが最善の近似とする。
            ComputeHandBindCorrection(cache, HumanBodyBones.LeftLowerArm,  HumanBodyBones.LeftHand);
            ComputeHandBindCorrection(cache, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand);
        }

        // T ポーズを使うフラグが立っているときだけ採る（既定の経路では avatar.humanDescription を読まない。Quest の IL2CPP で
        // 読めるかは未確認なので、既定の挙動に関わらないようにする）。
        if (fkReferenceFromAvatarTPose || handFkFromForearmFrame || footAimAtModelRestDirection || humanFingerRestFromAvatarTPose)
        {
            CaptureAvatarTPoseWorld(animator, cache);
        }

        cache.ready = cache.bones.Count > 0;
        humanoidCaches[animator] = cache;
        return cache;
    }

    // Avatar の T ポーズの world 回転を採る（2026-10-04、fkReferenceFromAvatarTPose 用）。transform は動かさず、
    // skeleton の局所回転（SkeletonBone.rotation = T ポーズの局所回転）を Animator の Transform から階層に沿って積む。
    // skeleton に無い中間の Transform は今の局所回転を使う。muscles=0 の基準（bindRotWorld）との差をログに出す:
    // 棚卸し（2026-10-04、16 体）では体幹 0°、上腕 48.6°、前腕・手 114.5°、大腿 30°、脛・足・つま先 50°（world）。
    private static void CaptureAvatarTPoseWorld(Animator animator, HumanoidRigCache cache)
    {
        cache.tposeRotWorld.Clear();
        if (animator == null || animator.avatar == null || !animator.avatar.isHuman)
        {
            return;
        }

        SkeletonBone[] skeleton;
        try
        {
            skeleton = animator.avatar.humanDescription.skeleton;
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"[TPOSE] {animator.name}: humanDescription を読めない（muscles=0 のまま）: {ex.Message}");
            return;
        }
        if (skeleton == null || skeleton.Length == 0)
        {
            Debug.LogWarning($"[TPOSE] {animator.name}: humanDescription.skeleton が空（muscles=0 のまま）");
            return;
        }

        var localTpose = new System.Collections.Generic.Dictionary<string, Quaternion>(skeleton.Length);
        foreach (SkeletonBone sb in skeleton)
        {
            if (!localTpose.ContainsKey(sb.name))
            {
                localTpose[sb.name] = sb.rotation;
            }
        }

        Transform root = animator.transform;
        var chain = new System.Collections.Generic.List<Transform>(16);
        foreach (var kv in cache.bones)
        {
            if (kv.Value == null)
            {
                continue;
            }

            chain.Clear();
            for (Transform t = kv.Value; t != null && t != root; t = t.parent)
            {
                chain.Add(t);
            }

            Quaternion world = root.rotation;
            for (int i = chain.Count - 1; i >= 0; i--)
            {
                world = world * (localTpose.TryGetValue(chain[i].name, out Quaternion local) ? local : chain[i].localRotation);
            }

            cache.tposeRotWorld[kv.Key] = world;
        }

        var sbLog = new System.Text.StringBuilder();
        foreach (HumanBodyBones b in new[] { HumanBodyBones.Hips, HumanBodyBones.Spine, HumanBodyBones.Head, HumanBodyBones.LeftShoulder,
                     HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand,
                     HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot, HumanBodyBones.LeftToes })
        {
            if (cache.tposeRotWorld.TryGetValue(b, out Quaternion tp) && cache.bindRotWorld.TryGetValue(b, out Quaternion m0))
            {
                sbLog.Append($" {b}={Quaternion.Angle(tp, m0):F1}");
            }
        }

        Debug.Log($"[TPOSE] model={animator.name} avatar={animator.avatar.name} bones={cache.tposeRotWorld.Count} angle(tp,m0):{sbLog}");
    }

    private static void ComputeHandBindCorrection(HumanoidRigCache cache, HumanBodyBones elbowBoneId, HumanBodyBones handBoneId)
    {
        if (!cache.bones.TryGetValue(elbowBoneId, out Transform elbowBone) || elbowBone == null) return;
        if (!cache.bones.TryGetValue(handBoneId,  out Transform handBone)  || handBone  == null) return;
        if (!cache.bindRotWorld.TryGetValue(handBoneId, out Quaternion bindHandWorld)) return;

        // T-pose サンプリング直後に呼ばれるため bone.position が T-pose 位置になっている。
        Vector3 armDir = handBone.position - elbowBone.position;
        if (armDir.sqrMagnitude < 0.0001f) return;
        armDir.Normalize();

        Vector3 up = Vector3.ProjectOnPlane(Vector3.up, armDir);
        if (up.sqrMagnitude < 0.001f)
            up = Vector3.ProjectOnPlane(Vector3.right, armDir);
        up.Normalize();

        Quaternion canonicalTpose = Quaternion.LookRotation(armDir, up);
        cache.handBindCorrection[handBoneId] = Quaternion.Inverse(canonicalTpose) * bindHandWorld;
    }

}

