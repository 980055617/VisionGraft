using System.Collections.Generic;
using UnityEngine;

// 人の指（2026-10-08、全関節の監査 J-12。元 HL-2・HX-7）。フラグは Core.cs の humanFingerRestFromAvatarTPose・humanFingersFromSmplHand・
// humanFingersFromSmplHandThumb（どれも既定 OFF、新しい振る舞い）。
//
// 今の扱い: 指 30 本は誰も書かず、毎 tick ResetHumanoidLocalRotations で muscles=0 の局所回転に戻る（ダンプ 4 体で指の局所 = m0 が 0.0003° 以内）。
// muscles=0 の指は 1 関節 34〜37°（親指 20〜23°）曲がった半握りで、元動画で指を伸ばして床に手をつく f605・f862 でも握ったまま。
// SMPL の 22・23（手。指の付け根の塊）は StoreSmplBlockFromBin が bodyPose[21]・[22] に読むが、FK の配列が 22 枠で順番も 21 で止まるので使っていなかった。
//
// (a) humanFingerRestFromAvatarTPose: 指 30 本だけ Avatar の T ポーズの局所回転に戻す（ResetHumanoidLocalRotations。FK ループの外で、今も局所回転を
//     書いている所）。T ポーズの局所 = tposeRotWorld[Humanoid の親]⁻¹ × tposeRotWorld[指]（CaptureAvatarTPoseWorld が skeleton の局所回転から積んだもの。
//     指の Transform の親がその Humanoid の骨のときだけ。ダンプのある 4 体（A 系・Beta・Renderpeople）は 30 本とも基節 → 手・中節 → 基節・末節 → 中節）。T ポーズの指はほぼまっすぐ
//     （16 体で PIP 0〜4.3°・MCP 0〜9.4°、HL_verify/v03）。
//     **前提「SMPL のゼロ姿勢の手は平ら」は確かめていない**（この PC に SMPL のモデルファイルが無い）。平らでなければ、平らな手もデータの手ではない。
//     予測（Eric、scratchpad/impl_joints/I5/i5_predict_out.txt。HL_verify/v06 の再現）: 中指の先が視聴者の目で p50 24 px・p95 30〜32 px 動いて開いた手になる
//     （画面の手の長さ 46〜48 px）。
// (b) humanFingersFromSmplHand（(a) が前提）: 手を書き終えた後に、指の付け根（人差し指〜小指の Proximal）の world 回転を
//       手の world × tposeRotWorld[手]⁻¹ × 平滑後の R22 / R23 × tposeRotWorld[指の付け根]
//     にして ApplyWorldRotation で書く（捨てていたデータを写す。係数なし）。FK の手 = W·bodyFk[20]·T_手、指 = W·bodyFk[20]·R22·T_指 から
//     W·bodyFk[20] を消した式で、手に対する指の局所は T_手⁻¹·R22·T_指 になり、手がどの経路（canonical 枠・前腕の枠・純 FK）で書かれたかに依らない。
//     R22 = 恒等なら (a) と同じ。中節・末節は (a) の T ポーズの局所のまま付いて回る。SMPL の手の関節は 1 つで指を塊で回すので、各指の付け根で回すのは
//     近似（向きは同じで、回転の中心が SMPL の関節と各指の付け根でずれる）。R22 / R23 は |R| p50 15°、1 フレームの変化 p50 0.4° とほぼ一定で、
//     本物の動きでなく HMR2 の事前分布の可能性がある。予測: (a) の上にさらに中指の先が p50 4.4〜4.6 px（p95 6.8〜6.9 px）。
//     humanFingersFromSmplHandThumb: 親指の付け根にも掛ける（SMPL の手の関節が親指を動かすかは未確認なので分けた。予測は親指の先 p50 1.7〜2.3 px）。
// T ポーズが採れない（Quest の IL2CPP で avatar.humanDescription が読めない等。[TPOSE] の警告で分かる）とき・その指の T ポーズの局所が作れないときは、
// その指は今の経路（muscles=0）のまま。使ったか戻したかは [FINGER] の行に状態が変わったときだけ出す。
// 副作用（i5_side_extent_out.txt、移植）: 倍率のロック・⑦・⑧ は指を含む全 Humanoid 骨の投影で測る（ResolveProjectionBones、
// useSilhouetteProjectionExtent = false）。Eric では今も 17% のフレームで指が投影の上端、25% で下端になっていて、(a) で投影の高さが約半分のフレームで
// 変わる（変わったフレームで p50 +1.6%、p95 +3.2%、最大 +7.2%。腕を上げる f200〜206 で +2.8〜3.5%）。⑧ はその分モデルを奥へ置く（小さく見える）。
public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    // SMPL の手（22 = 左手、23 = 右手。親は手首 20・21）。bodyPose[21]・[22] に入っている。
    private const int SmplLeftHandJoint = 22;
    private const int SmplRightHandJoint = 23;

    // 指 30 本と、T ポーズの局所を作るときの Humanoid の親（基節 → 手、中節 → 基節、末節 → 中節）。同じ並び。
    private static readonly HumanBodyBones[] HumanFingerBones =
    {
        HumanBodyBones.LeftThumbProximal, HumanBodyBones.LeftThumbIntermediate, HumanBodyBones.LeftThumbDistal,
        HumanBodyBones.LeftIndexProximal, HumanBodyBones.LeftIndexIntermediate, HumanBodyBones.LeftIndexDistal,
        HumanBodyBones.LeftMiddleProximal, HumanBodyBones.LeftMiddleIntermediate, HumanBodyBones.LeftMiddleDistal,
        HumanBodyBones.LeftRingProximal, HumanBodyBones.LeftRingIntermediate, HumanBodyBones.LeftRingDistal,
        HumanBodyBones.LeftLittleProximal, HumanBodyBones.LeftLittleIntermediate, HumanBodyBones.LeftLittleDistal,
        HumanBodyBones.RightThumbProximal, HumanBodyBones.RightThumbIntermediate, HumanBodyBones.RightThumbDistal,
        HumanBodyBones.RightIndexProximal, HumanBodyBones.RightIndexIntermediate, HumanBodyBones.RightIndexDistal,
        HumanBodyBones.RightMiddleProximal, HumanBodyBones.RightMiddleIntermediate, HumanBodyBones.RightMiddleDistal,
        HumanBodyBones.RightRingProximal, HumanBodyBones.RightRingIntermediate, HumanBodyBones.RightRingDistal,
        HumanBodyBones.RightLittleProximal, HumanBodyBones.RightLittleIntermediate, HumanBodyBones.RightLittleDistal,
    };

    private static readonly HumanBodyBones[] HumanFingerParents =
    {
        HumanBodyBones.LeftHand, HumanBodyBones.LeftThumbProximal, HumanBodyBones.LeftThumbIntermediate,
        HumanBodyBones.LeftHand, HumanBodyBones.LeftIndexProximal, HumanBodyBones.LeftIndexIntermediate,
        HumanBodyBones.LeftHand, HumanBodyBones.LeftMiddleProximal, HumanBodyBones.LeftMiddleIntermediate,
        HumanBodyBones.LeftHand, HumanBodyBones.LeftRingProximal, HumanBodyBones.LeftRingIntermediate,
        HumanBodyBones.LeftHand, HumanBodyBones.LeftLittleProximal, HumanBodyBones.LeftLittleIntermediate,
        HumanBodyBones.RightHand, HumanBodyBones.RightThumbProximal, HumanBodyBones.RightThumbIntermediate,
        HumanBodyBones.RightHand, HumanBodyBones.RightIndexProximal, HumanBodyBones.RightIndexIntermediate,
        HumanBodyBones.RightHand, HumanBodyBones.RightMiddleProximal, HumanBodyBones.RightMiddleIntermediate,
        HumanBodyBones.RightHand, HumanBodyBones.RightRingProximal, HumanBodyBones.RightRingIntermediate,
        HumanBodyBones.RightHand, HumanBodyBones.RightLittleProximal, HumanBodyBones.RightLittleIntermediate,
    };

    // (b) で SMPL の手を掛ける指の付け根（親指は humanFingersFromSmplHandThumb のときだけ）。
    private static readonly HumanBodyBones[] LeftFingerRootBones =
    {
        HumanBodyBones.LeftIndexProximal, HumanBodyBones.LeftMiddleProximal, HumanBodyBones.LeftRingProximal, HumanBodyBones.LeftLittleProximal
    };

    private static readonly HumanBodyBones[] RightFingerRootBones =
    {
        HumanBodyBones.RightIndexProximal, HumanBodyBones.RightMiddleProximal, HumanBodyBones.RightRingProximal, HumanBodyBones.RightLittleProximal
    };

    // (b) は (a) が前提（付け根だけ T ポーズ基準で書き、中節・末節が muscles=0 の曲がりのままだと形が混ざる）。
    private bool HumanFingersFromSmplHandActive => humanFingerRestFromAvatarTPose && humanFingersFromSmplHand;

    // (a) の T ポーズの局所。最初に使うときに作ってキャッシュに置く（tposeRotWorld は GetOrBuildHumanoidCache で一度だけ採るので、作り直しは要らない）。
    // T ポーズを採っていない（キャッシュを作ったときに T ポーズのフラグが無かった・humanDescription が読めなかった）ときは null。
    private static Dictionary<HumanBodyBones, Quaternion> GetHumanFingerTposeLocals(HumanoidRigCache cache)
    {
        if (cache.fingerTposeRotLocal != null)
        {
            return cache.fingerTposeRotLocal;
        }

        if (cache.tposeRotWorld.Count == 0)
        {
            return null;
        }

        var locals = new Dictionary<HumanBodyBones, Quaternion>(HumanFingerBones.Length);
        for (int i = 0; i < HumanFingerBones.Length; i++)
        {
            HumanBodyBones finger = HumanFingerBones[i];
            HumanBodyBones parent = HumanFingerParents[i];
            // 親が Humanoid の骨でない（間に別の Transform がある）指は、tposeRotWorld の比が局所にならないので入れない（muscles=0 のまま）。
            if (!cache.bones.TryGetValue(finger, out Transform bone) || bone == null ||
                !cache.bones.TryGetValue(parent, out Transform parentBone) || parentBone == null || bone.parent != parentBone ||
                !cache.tposeRotWorld.TryGetValue(finger, out Quaternion fingerWorld) ||
                !cache.tposeRotWorld.TryGetValue(parent, out Quaternion parentWorld))
            {
                continue;
            }

            Quaternion local = Quaternion.Normalize(Quaternion.Inverse(parentWorld) * fingerWorld);
            if (IsFinite(local))
            {
                locals[finger] = local;
            }
        }

        cache.fingerTposeRotLocal = locals;
        return locals;
    }

    // ResetHumanoidLocalRotations（humanFingerRestFromAvatarTPose のとき）から呼ぶ。指の T ポーズの局所（無ければ null）を返し、[FINGER] を出す。
    private Dictionary<HumanBodyBones, Quaternion> ResolveHumanFingerRestLocals(HumanoidRigCache cache)
    {
        if (cache == null)
        {
            return null;
        }

        Dictionary<HumanBodyBones, Quaternion> locals = GetHumanFingerTposeLocals(cache);
        int logState = locals != null && locals.Count > 0 ? 1 : 2;
        if (cache.fingerRestLogState != logState)
        {
            cache.fingerRestLogState = logState;
            if (logState == 1)
            {
                // muscles=0 との差（1 関節 33.7〜36.5°、親指 20.0〜22.6° の見込み、HL_verify/v06）。
                float sum = 0f;
                float max = 0f;
                var missing = new System.Text.StringBuilder();
                foreach (HumanBodyBones finger in HumanFingerBones)
                {
                    if (locals.TryGetValue(finger, out Quaternion tp) && cache.bindRotLocal.TryGetValue(finger, out Quaternion m0))
                    {
                        float angle = Quaternion.Angle(tp, m0);
                        sum += angle;
                        max = Mathf.Max(max, angle);
                    }
                    else if (cache.bones.ContainsKey(finger))
                    {
                        missing.Append(' ').Append(finger);
                    }
                }

                Debug.Log($"[FINGER] avatar={cache.avatarName} rest=tpose fingers={locals.Count}/{HumanFingerBones.Length} " +
                          $"angle(tp,m0) mean={(locals.Count > 0 ? sum / locals.Count : 0f):F1} max={max:F1} muscles0Kept:{(missing.Length > 0 ? missing.ToString() : " none")}");
            }
            else
            {
                Debug.LogWarning($"[FINGER] avatar={cache.avatarName} rest=muscles0（T ポーズが無い: キャッシュを作ったときにフラグが無かったか、humanDescription が読めなかった）");
            }
        }

        return locals != null && locals.Count > 0 ? locals : null;
    }

    // (b) の平滑。TryApplyHumanSmplRotationOverlay の FK の主ループの後から、同じ smoothAlpha で呼ぶ（ResolveSmoothingSeconds をもう一度呼ぶと、
    // 同じ tick の 2 回目は 0 を返して止まるので、刻み幅はもらう）。22・23 は bodyFk にも積む（骨には書かない。0〜21 の値は変えない）。
    // OFF なら 22・23 の平滑の印を下ろすだけ（途中で入れたときに、0 の四元数から Slerp しないため）。
    private void SmoothHumanSmplHandJoints(HumanSmplRetargetState state, HumanSmplPose pose, Quaternion[] bodyFk, float smoothAlpha)
    {
        if (!HumanFingersFromSmplHandActive)
        {
            state.smplHandSmoothingInitialized = false;
            return;
        }

        // rotation_count < 24 の bundle では bodyPose[21]・[22] が 0 の四元数のまま（IsFinite は通る）。そのときは (b) を掛けない（指は (a) のまま）。
        if (!TryGetHumanSmplHandRotation(pose, SmplLeftHandJoint, out Quaternion rawLeft) ||
            !TryGetHumanSmplHandRotation(pose, SmplRightHandJoint, out Quaternion rawRight))
        {
            state.smplHandSmoothingInitialized = false;
            return;
        }

        // 0〜21 と同じ EMA。平滑の履歴が無い（最初・shot の切れ目）か 22・23 をまだ回していなければ、今の値から始める。
        bool warm = state.smoothingInitialized && state.smplHandSmoothingInitialized;
        state.smoothedSmplLocal[SmplLeftHandJoint] = warm
            ? Quaternion.Slerp(state.smoothedSmplLocal[SmplLeftHandJoint], rawLeft, smoothAlpha)
            : rawLeft;
        state.smoothedSmplLocal[SmplRightHandJoint] = warm
            ? Quaternion.Slerp(state.smoothedSmplLocal[SmplRightHandJoint], rawRight, smoothAlpha)
            : rawRight;
        bodyFk[SmplLeftHandJoint] = bodyFk[SmplLeftWrist] * state.smoothedSmplLocal[SmplLeftHandJoint];
        bodyFk[SmplRightHandJoint] = bodyFk[SmplRightWrist] * state.smoothedSmplLocal[SmplRightHandJoint];
        state.smplHandSmoothingInitialized = true;
    }

    private static bool TryGetHumanSmplHandRotation(HumanSmplPose pose, int smplJoint, out Quaternion rotation)
    {
        if (!TryGetHumanSmplLocalRotation(pose, smplJoint, out rotation))
        {
            return false;
        }

        // 読んだ枠は単位四元数（LookRotation）。0 の四元数（読んでいない枠）を外す。
        float sq = rotation.x * rotation.x + rotation.y * rotation.y + rotation.z * rotation.z + rotation.w * rotation.w;
        return sq > 0.25f;
    }

    // (b) 本体。PosePipeline で手が書き終わった後（keypoint の AimAt・SMPL 目標の AimAt・純 FK のどれでも）に呼ぶ。OFF なら何もしない。
    private void TryApplyHumanFingersFromSmplHand(HumanoidRigCache cache)
    {
        if (!HumanFingersFromSmplHandActive || cache == null || !cache.ready)
        {
            return;
        }

        // (a) と同じ T ポーズの局所が作れていること（作れていなければ (a) も muscles=0 のまま）と、この tick の 22・23 の平滑が済んでいること。
        // 手の T ポーズが無い側は ApplyHumanFingerRootsFromSmplHand が飛ばす。
        Dictionary<HumanBodyBones, Quaternion> rest = GetHumanFingerTposeLocals(cache);
        if (rest == null || rest.Count == 0 ||
            !humanSmplRetargetStateByCache.TryGetValue(cache, out HumanSmplRetargetState state) || state == null ||
            !state.smplHandSmoothingInitialized)
        {
            LogHumanFingerSmplHandState(cache, 2, 0, null);
            return;
        }

        int written = ApplyHumanFingerRootsFromSmplHand(cache, state, rest, HumanBodyBones.LeftHand, SmplLeftHandJoint,
            LeftFingerRootBones, HumanBodyBones.LeftThumbProximal);
        written += ApplyHumanFingerRootsFromSmplHand(cache, state, rest, HumanBodyBones.RightHand, SmplRightHandJoint,
            RightFingerRootBones, HumanBodyBones.RightThumbProximal);
        LogHumanFingerSmplHandState(cache, written > 0 ? 1 : 2, written, state);
    }

    private int ApplyHumanFingerRootsFromSmplHand(HumanoidRigCache cache, HumanSmplRetargetState state,
        Dictionary<HumanBodyBones, Quaternion> rest, HumanBodyBones handId, int smplHandJoint,
        HumanBodyBones[] fingerRoots, HumanBodyBones thumbRoot)
    {
        if (!cache.bones.TryGetValue(handId, out Transform hand) || hand == null ||
            !cache.tposeRotWorld.TryGetValue(handId, out Quaternion handTpose) || !IsFinite(handTpose))
        {
            return 0;
        }

        Quaternion handLocal = state.smoothedSmplLocal[smplHandJoint];
        if (!IsFinite(handLocal))
        {
            return 0;
        }

        // W·bodyFk[20]（SMPL の手首の枠）を、実際の手の world から T ポーズの手を外して作る。
        Quaternion frame = hand.rotation * Quaternion.Inverse(handTpose) * handLocal;
        int written = 0;
        foreach (HumanBodyBones finger in fingerRoots)
        {
            written += WriteHumanFingerRootFromSmplHand(cache, rest, finger, frame);
        }

        if (humanFingersFromSmplHandThumb)
        {
            written += WriteHumanFingerRootFromSmplHand(cache, rest, thumbRoot, frame);
        }

        return written;
    }

    private static int WriteHumanFingerRootFromSmplHand(HumanoidRigCache cache, Dictionary<HumanBodyBones, Quaternion> rest,
        HumanBodyBones finger, Quaternion frame)
    {
        // (a) で T ポーズの局所が作れなかった指（muscles=0 のまま）は触らない。
        if (!rest.ContainsKey(finger) ||
            !cache.bones.TryGetValue(finger, out Transform bone) || bone == null ||
            !cache.tposeRotWorld.TryGetValue(finger, out Quaternion fingerTpose) || !IsFinite(fingerTpose))
        {
            return 0;
        }

        Quaternion tw = frame * fingerTpose;
        if (!IsFinite(tw))
        {
            return 0;
        }

        TransformWriter.ApplyWorldRotation(bone, tw);
        return 1;
    }

    private static void LogHumanFingerSmplHandState(HumanoidRigCache cache, int logState, int written, HumanSmplRetargetState state)
    {
        if (cache.fingerSmplHandLogState == logState)
        {
            return;
        }

        cache.fingerSmplHandLogState = logState;
        if (logState == 1 && state != null)
        {
            Debug.Log($"[FINGER] avatar={cache.avatarName} smplHand=on roots={written} " +
                      $"|R22|={Quaternion.Angle(Quaternion.identity, state.smoothedSmplLocal[SmplLeftHandJoint]):F1} " +
                      $"|R23|={Quaternion.Angle(Quaternion.identity, state.smoothedSmplLocal[SmplRightHandJoint]):F1}");
        }
        else
        {
            Debug.LogWarning($"[FINGER] avatar={cache.avatarName} smplHand=off（T ポーズの指が無い、または SMPL の 22・23 が無い。指は (a) のまま）");
        }
    }
}
