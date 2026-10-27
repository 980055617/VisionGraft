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
                CaptureHandRollAxes(cache);

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
            CaptureHandRollAxes(cache);
        }

        ResolveHumanHandRoll(animator, cache);

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

    // humanHandRollFixMode 用（2026-10-25）: ComputeHandBindCorrection と同じ時点（muscles=0）の肘→手首の向きを、手と前腕それぞれの局所で控える。
    // 手の局所の軸は handBindCorrection⁻¹ × canonical の前と同じ（C の左に canonical の前の回りの回転を掛けることと、手の world の右に
    // この軸の回りの回転を掛けることが等しい。クリップの後に当てるのは後者）。
    private static void CaptureHandRollAxes(HumanoidRigCache cache)
    {
        CaptureHandRollAxis(cache, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand, out cache.leftForearmRollAxisLocal, out cache.leftHandRollAxisLocal);
        CaptureHandRollAxis(cache, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand, out cache.rightForearmRollAxisLocal, out cache.rightHandRollAxisLocal);
    }

    private static void CaptureHandRollAxis(HumanoidRigCache cache, HumanBodyBones elbowBoneId, HumanBodyBones handBoneId,
        out Vector3 forearmAxisLocal, out Vector3 handAxisLocal)
    {
        forearmAxisLocal = Vector3.zero;
        handAxisLocal = Vector3.zero;
        if (!cache.bones.TryGetValue(elbowBoneId, out Transform elbowBone) || elbowBone == null) return;
        if (!cache.bones.TryGetValue(handBoneId, out Transform handBone) || handBone == null) return;

        Vector3 armDir = handBone.position - elbowBone.position;
        if (armDir.sqrMagnitude < 1e-10f) return;
        armDir.Normalize();
        forearmAxisLocal = Quaternion.Inverse(elbowBone.rotation) * armDir;
        handAxisLocal = Quaternion.Inverse(handBone.rotation) * armDir;
    }

    // 回す量を表から引く（prefab 名 = ReplaceableModel.sourcePrefabName と完全一致）。表に無いモデル・prefab 名の取れないモデルは 0。
    // 切り替え（humanHandRollFixMode）は使うときに読むので、ここでは量だけ控える。モデルごとに 1 行ログを出す。
    private void ResolveHumanHandRoll(Animator animator, HumanoidRigCache cache)
    {
        ReplaceableModel model = animator != null ? animator.GetComponentInParent<ReplaceableModel>() : null;
        cache.sourcePrefabName = model != null ? model.sourcePrefabName : null;
        cache.leftHandRollDeg = 0f;
        cache.rightHandRollDeg = 0f;
        if (!string.IsNullOrEmpty(cache.sourcePrefabName) && !string.IsNullOrEmpty(humanHandRollFixTable))
        {
            foreach (string entry in humanHandRollFixTable.Split(','))
            {
                string[] p = entry.Trim().Split(':');
                if (p.Length != 3 || p[0].Trim() != cache.sourcePrefabName)
                {
                    continue;
                }

                if (float.TryParse(p[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float left) &&
                    float.TryParse(p[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float right))
                {
                    cache.leftHandRollDeg = left;
                    cache.rightHandRollDeg = right;
                }

                break;
            }
        }

        Debug.Log($"[HANDROLL] model={(animator != null ? animator.name : "")} prefab={cache.sourcePrefabName} mode={humanHandRollFixMode} " +
                  $"left={cache.leftHandRollDeg:F1} right={cache.rightHandRollDeg:F1} axisHandL={cache.leftHandRollAxisLocal:F3} " +
                  $"axisHandR={cache.rightHandRollAxisLocal:F3} axisForearmL={cache.leftForearmRollAxisLocal:F3} axisForearmR={cache.rightForearmRollAxisLocal:F3}");
    }

    // Humanoid のクリップ（自分から動く動作・画面の外へ歩く所）を評価した直後に、追従の手の FK と同じ回転を当てる（humanHandRollFixMode）。
    // クリップは avatar を通るので Meshy の手のひら前向きのまま残り、当てないと動作の始まりに手首がくるっと回り、終わりに戻る（確かめ役 V_H1）。
    // クリップは毎 tick 全部の骨を書き直すので積み重ならない。前腕を先に回し、手は回す前の world に手の局所の軸の回りの回転を掛けた値で書く。
    // 受け渡しの混ぜ（始めの 0.3 秒・終わりの 0.45 秒）より前に呼ぶ（混ぜの両端がどちらも回した姿勢になる）。
    private void ApplyHumanHandRollFixAfterClip(Animator animator)
    {
        if (humanHandRollFixMode == 0 || animator == null || !humanoidCaches.TryGetValue(animator, out HumanoidRigCache cache) || cache == null)
        {
            return;
        }

        int mode = HumanHandRollModeFor(cache);
        if (mode == 0)
        {
            return;
        }

        ApplyHumanHandRollToCurrentPose(cache, HumanBodyBones.LeftHand, mode);
        ApplyHumanHandRollToCurrentPose(cache, HumanBodyBones.RightHand, mode);
    }

    // 追従の手とクリップの手を回すかどうかを同じ条件で決める（2026-10-25、コードの点検 2 体の指摘）。追従の手を回すのは
    // ApplyHandFkWithCanonicalArmDirection だけ（keypoint の AimAt が ON で、handFkFromForearmFrame の T ポーズの経路でないとき）。
    // 条件がずれると追従の手は回らずクリップの手だけ回り、動作の始まりに手首がひねられ、終わりに戻る（実験の設定では起きない）。
    private int HumanHandRollModeFor(HumanoidRigCache cache)
    {
        if (humanHandRollFixMode == 0 || !EnableHumanSmplMotion || !enableBoneApply || !enableKeypointAimAt ||
            (handFkFromForearmFrame && cache != null && cache.tposeRotWorld.Count > 0))
        {
            return 0;
        }

        return humanHandRollFixMode;
    }

    private static void ApplyHumanHandRollToCurrentPose(HumanoidRigCache cache, HumanBodyBones handBoneId, int mode)
    {
        float deg = HumanHandRollDeg(cache, handBoneId, mode);
        if (deg == 0f || !cache.bones.TryGetValue(handBoneId, out Transform hand) || hand == null)
        {
            return;
        }

        Vector3 handAxis = handBoneId == HumanBodyBones.LeftHand ? cache.leftHandRollAxisLocal : cache.rightHandRollAxisLocal;
        if (handAxis.sqrMagnitude < 1e-8f)
        {
            return;
        }

        Quaternion handWorld = hand.rotation;
        RollHumanForearmForHandFix(cache, handBoneId, mode, deg);
        Quaternion rolled = handWorld * Quaternion.AngleAxis(deg, handAxis);
        if (IsFinite(rolled))
        {
            TransformWriter.ApplyWorldRotation(hand, rolled);
        }
    }

    // 切り替えが ON で表に載ったモデルなら、その手を回す量（度）。OFF・表に無いモデルは 0。
    private static float HumanHandRollDeg(HumanoidRigCache cache, HumanBodyBones handBoneId, int mode)
    {
        if (mode == 0 || cache == null)
        {
            return 0f;
        }

        return handBoneId == HumanBodyBones.LeftHand ? cache.leftHandRollDeg : handBoneId == HumanBodyBones.RightHand ? cache.rightHandRollDeg : 0f;
    }

    // 前腕も回す案（2 = 手と同じだけ、3 = 半分）: 前腕をその局所の肘→手首の軸の回りに回す。呼ぶ側は前腕をこの tick に書き直した後
    // （追従は FK と AimAt、クリップは評価）に 1 回だけ呼ぶので積み重ならない。手は呼ぶ側が後から world で書く。
    private static void RollHumanForearmForHandFix(HumanoidRigCache cache, HumanBodyBones handBoneId, int mode, float handDeg)
    {
        float forearmDeg = mode == 2 ? handDeg : mode == 3 ? 0.5f * handDeg : 0f;
        if (forearmDeg == 0f)
        {
            return;
        }

        HumanBodyBones forearmId = handBoneId == HumanBodyBones.LeftHand ? HumanBodyBones.LeftLowerArm : HumanBodyBones.RightLowerArm;
        Vector3 axis = handBoneId == HumanBodyBones.LeftHand ? cache.leftForearmRollAxisLocal : cache.rightForearmRollAxisLocal;
        if (axis.sqrMagnitude < 1e-8f || !cache.bones.TryGetValue(forearmId, out Transform forearm) || forearm == null)
        {
            return;
        }

        Quaternion rolled = forearm.rotation * Quaternion.AngleAxis(forearmDeg, axis);
        if (IsFinite(rolled))
        {
            TransformWriter.ApplyWorldRotation(forearm, rolled);
        }
    }

}

