using System.Collections.Generic;
using UnityEngine;

// SMAL の骨にそろえたリグ（2026-10-23 の試作。Blender で動物のモデルの骨を AniMer と同じ SMAL の 35 関節にし、脚・尾を SMAL の基準の
// 姿勢へ曲げ直したもの。ユーザー「元データ（animer）と同じ骨にしたい」）。
//   - 見分け方: 35 個の SMAL の関節名（root, pelvis0, spine, … , REar）の骨がそろっている。そろっていないモデル（今の 54 体）は何も変わらない
//   - 役（spine・neck・head・四肢・尾）は SMAL の関節に決め打ちで割り当てる。配置（⑦⑧）・頭合わせ・AimAt・ジェスチャは今までどおり役を使う
//   - FK は 35 関節すべてを体の写像で書く（SmalLeafBodyFrameWorld: worldFk0 · K⁻¹ · mirX(A_j) · K · bind_j。A_j は root から積んだ回転）。
//     どの骨もモデル固有の当てはめ（jointFrameMap・当てはめ表・F2・B など）を通らない
//   - 曲げ直していない区間（首→頭 約 101°、頭→口 約 9°。首は Blender で曲げ直すと首の後ろにこぶができた）は、写したあとの区間の向きを
//     SMAL が求める向き（worldFk0 · K⁻¹ · D(A_j · s_j)、smalAbsoluteDirection と同じ）へ最小の回転で合わせる。これは基準の姿勢で一定の
//     補正を掛けるのと同じ（最小の回転は回転で共役しても変わらない）。曲げ直した脚・尾ではほぼ 0°
// 骨は ApplyWorldRotation だけで書き、毎 tick bind の world から作る（今の回転を読まないので積み重ならない）。
public sealed partial class AnimalPoseApplier
{
    // false にすると SMAL の骨のリグを見分けない（そのモデルは役が付かず、姿勢が当たらない）。今の 54 体には効かない。
    public bool smalNativeRigFk = true;

    // SMAL の骨のリグと見分けたモデルの SkinnedMeshRenderer.quality（2026-10-23、確かめ用・既定 0 = 触らない）。
    // 1 / 2 / 4 = 1 頂点を動かす骨の数。Android の既定の品質 Mobile は skinWeights 2 なので、Auto（0）のままだと 2 本。
    // 明示した Bone4 が品質の上限より優先されるかは Unity の説明に無いので、バッチで撮って確かめる（Docs/tmp/smal_rig_20261023）。
    // 読まれるのはリグのキャッシュを作るとき（TryApplySmalNativeRoles）だけ。
    public int smalNativeSkinQuality;

    // 脚の横の寄せをモデルの肩・腰の幅に合わせる（2026-10-25、既定 OFF・試作。ユーザー「犬と猫はやはり結構おかしい」）。
    // 区間を SMAL の向きへ合わせるとき、狙いの向きの横（前脚は胸の関節 6、後脚は根の関節 0 の左右の軸）の成分だけを k 倍する。
    // k = (モデルの左右の付け根の半幅 / 脚の長さ) ÷ (SMAL の同じ比)、1 を超えない（広げない）。肩が SMAL より細いキツネ（k 0.34）は、
    // SMAL の「内へ寄せる」向きのままだと前足が中心線を越えて X に交差していた（714 フレーム中 524、犬 57/685・狼 66/685。データは交差しない）。
    // 骨のダンプでの試算: 交差 0、区間の向きの変化 中央値 4〜5°（Docs/tmp/smal_rig_20261023/README.md「前脚の交差の直しの試算」）。
    // 後脚は 3 体とも k > 1（腰は SMAL より広い）なので変わらない。読まれるのは k を求めるリグのキャッシュを作るときと毎 tick の FK。
    public bool smalNativeLimbWidthScale = true;

    // 頭を視聴者へ向けるときの「頭の前」を、顔の骨（Nose）への向きではなく SMAL の頭の座標の前にする（2026-10-25、既定 OFF・試作）。
    // 作り直したキツネ・狼・犬は Nose の骨が鼻先（目→鼻の線の上）にあり、頭の骨からの向きは立ち姿の頭の前より 37〜43° 下。鼻を視聴者へ向けると
    // 頭が 37〜43° 反り、鼻先が天井を向いてあごの下が見えた（2026-10-25 の 12 通りの点検。猫 55 は頭の骨が低く、差が 2.3° なので正しく見えていた）。
    // SMAL の頭の座標の前 = bind の頭(16)→口(32) を、SMAL の基準の骨格でその区間が体の前から下がる角（36.85°）だけ頭の上へ回した向き。
    // 既定の姿勢ではちょうど体の水平の前になる（ApplySmalNativeRigFk の区間の合わせ）。読まれるのはリグのキャッシュを作るときだけ。今の 54 体には効かない。
    public bool smalNativeLookAtHeadFrame = true;

    // 頭のジェスチャ（anatomicalAxes の頭の軸。HeadShake・HeadTilt）の「頭の前」も SMAL の頭の座標の前にする（2026-10-25、既定 OFF・試作。
    // 検証役の指摘で smalNativeLookAtHeadFrame から分けた）。今の頭の軸 Z = 顔の骨（鼻）への向きなので、HeadShake（Z まわり ±35° のロール）・
    // HeadTilt（−25°）は顔が鼻の軸まわりに回り、鼻先は動かない。ON にすると軸が鼻の線より 37〜43° 上になり、鼻先が HeadShake の山で 21〜24°、
    // HeadTilt の保持で 15〜17° 振れる（キツネ・狼・犬の移植の値。猫は約 1°）。頭が反る直しには要らない。読まれるのはリグのキャッシュを作るときだけ。
    public bool smalNativeGestureHeadFrame = true;

    // SMAL の尾の 7 関節（25〜31 = Tail1〜Tail7）の平均累積角を保ったまま、滑らかな弾性曲線に配分する（2026-10-26、既定 ON。
    // bundle_cat f24・f130 等で SMAL 推定の尾が +/− 交互に折れて S 字にくねくね曲がるのを滑らかな弧にする）。
    public bool smalNativeSmoothTail = true;

    private static readonly float[] SmalNativeTailBeamWeights =
    {
        0.22f, 0.20f, 0.17f, 0.14f, 0.11f, 0.09f, 0.07f,
    };
    private const float SmalNativeTailBeamMeanCum = 0.6742857f;
    private const float SmalNativeTailMaxTotalDeg = 90f;

    private static Vector3 QuaternionToRotVecDeg(Quaternion q)
    {
        if (q.w < 0f)
        {
            q = new Quaternion(-q.x, -q.y, -q.z, -q.w);
        }
        Vector3 v = new Vector3(q.x, q.y, q.z);
        float sinHalf = v.magnitude;
        if (sinHalf < 1e-6f)
        {
            return Vector3.zero;
        }
        float deg = 2f * Mathf.Atan2(sinHalf, q.w) * Mathf.Rad2Deg;
        return (v / sinHalf) * deg;
    }

    private static Quaternion RotVecDegToQuaternion(Vector3 rvDeg)
    {
        float deg = rvDeg.magnitude;
        if (deg < 1e-5f)
        {
            return Quaternion.identity;
        }
        return Quaternion.AngleAxis(deg, rvDeg / deg);
    }

    private static readonly string[] SmalNativeJointNames =
    {
        "root", "pelvis0", "spine", "spine0", "spine1", "spine2", "spine3",
        "LLeg1", "LLeg2", "LLeg3", "LFoot", "RLeg1", "RLeg2", "RLeg3", "RFoot",
        "Neck", "Head",
        "LLegBack1", "LLegBack2", "LLegBack3", "LFootBack", "RLegBack1", "RLegBack2", "RLegBack3", "RFootBack",
        "Tail1", "Tail2", "Tail3", "Tail4", "Tail5", "Tail6", "Tail7",
        "Mouth", "LEar", "REar",
    };

    // SMAL の基準の関節位置（SMAL の座標: +X 頭、+Y 左、+Z 上。Docs/smal-rest-skeleton.json、betas = 0）。
    private static readonly Vector3[] SmalNativeRestJoints =
    {
        new Vector3(-0.427900f, 0.000078f, 0.250160f), // 0 root
        new Vector3(-0.427900f, 0.000078f, 0.250160f), // 1 pelvis0
        new Vector3(-0.317754f, 0.000078f, 0.258802f), // 2 spine
        new Vector3(-0.222954f, 0.000078f, 0.080611f), // 3 spine0
        new Vector3(-0.094312f, 0.000078f, 0.057577f), // 4 spine1
        new Vector3(0.039631f, 0.000078f, 0.058787f), // 5 spine2
        new Vector3(0.169006f, 0.000078f, 0.228602f), // 6 spine3
        new Vector3(0.303444f, 0.166309f, 0.130277f), // 7 LLeg1
        new Vector3(0.312238f, 0.147535f, -0.065346f), // 8 LLeg2
        new Vector3(0.313690f, 0.105860f, -0.293238f), // 9 LLeg3
        new Vector3(0.361391f, 0.101509f, -0.427483f), // 10 LFoot
        new Vector3(0.303444f, -0.166154f, 0.130277f), // 11 RLeg1
        new Vector3(0.312238f, -0.147380f, -0.065346f), // 12 RLeg2
        new Vector3(0.313690f, -0.105705f, -0.293238f), // 13 RLeg3
        new Vector3(0.361391f, -0.101354f, -0.427483f), // 14 RFoot
        new Vector3(0.386663f, 0.000078f, 0.310618f), // 15 Neck
        new Vector3(0.571468f, 0.000078f, 0.289283f), // 16 Head
        new Vector3(-0.490809f, 0.095162f, 0.195902f), // 17 LLegBack1
        new Vector3(-0.405899f, 0.117017f, -0.039548f), // 18 LLegBack2
        new Vector3(-0.458089f, 0.094748f, -0.301260f), // 19 LLegBack3
        new Vector3(-0.421706f, 0.104206f, -0.500437f), // 20 LFootBack
        new Vector3(-0.490809f, -0.095007f, 0.195902f), // 21 RLegBack1
        new Vector3(-0.405899f, -0.116862f, -0.039548f), // 22 RLegBack2
        new Vector3(-0.458089f, -0.094593f, -0.301260f), // 23 RLegBack3
        new Vector3(-0.421706f, -0.104051f, -0.500437f), // 24 RFootBack
        new Vector3(-0.552748f, 0.000078f, 0.178512f), // 25 Tail1
        new Vector3(-0.709951f, 0.000078f, 0.159322f), // 26 Tail2
        new Vector3(-0.786790f, 0.000078f, 0.161705f), // 27 Tail3
        new Vector3(-0.876353f, 0.000078f, 0.169979f), // 28 Tail4
        new Vector3(-0.960203f, 0.000078f, 0.175930f), // 29 Tail5
        new Vector3(-1.085173f, 0.000078f, 0.190628f), // 30 Tail6
        new Vector3(-1.136759f, 0.000078f, 0.189637f), // 31 Tail7
        new Vector3(0.691495f, 0.000078f, 0.199336f), // 32 Mouth
        new Vector3(0.638578f, 0.110875f, 0.398463f), // 33 LEar
        new Vector3(0.638578f, -0.110720f, 0.398463f), // 34 REar
    };

    // 区間の向きを SMAL に合わせる関節と、その区間の子（−1 = 合わせない）。四肢・首・頭・尾。胴（0〜6）は SMAL の形のまま置いたので合わせない
    // （spine3 → 前脚・首、root → 後脚・尾は付け根の場所の違いで、回転の誤差ではない）。足先・尾の先・口・耳は区間が無い。
    private static readonly int[] SmalNativeAlignChild =
    {
        -1, -1, -1, -1, -1, -1, -1,
        8, 9, 10, -1, 12, 13, 14, -1,
        16, 32,
        18, 19, 20, -1, 22, 23, 24, -1,
        26, 27, 28, 29, 30, 31, -1,
        -1, -1, -1,
    };

    private sealed class SmalNativeRig
    {
        public readonly Transform[] bones = new Transform[35];
        public readonly Quaternion[] bindWorld = new Quaternion[35];
        // bind の world での「この関節 → 区間の子」（bindWorld と同じ時に控える）。
        public readonly Vector3[] bindSegment = new Vector3[35];
        // 脚の横の寄せの倍率 k（smalNativeLimbWidthScale）。[0] = 前脚、[1] = 後脚。bind で 1 回だけ求める。
        public readonly float[] limbLateralScale = { 1f, 1f };
        public bool logged;
        public bool dumpedBind;
        // 根の骨（関節 0）から見た bind の位置（確かめ用のダンプの最初の行）。
        public readonly Vector3[] bindLocalPos = new Vector3[35];
    }

    private readonly Dictionary<AnimalRigCache, SmalNativeRig> smalNativeRigs = new Dictionary<AnimalRigCache, SmalNativeRig>();

    // GetOrBuildAnimalRigCache で役を名前の規則で決めた直後に呼ぶ。SMAL の骨のリグなら役を付け替え、bind を控える（まだ誰も骨を書いていない）。
    private bool TryApplySmalNativeRoles(AnimalRigCache cache, Transform[] bones)
    {
        if (!smalNativeRigFk || cache == null || bones == null)
        {
            return false;
        }

        var byName = new Dictionary<string, Transform>();
        foreach (Transform t in bones)
        {
            if (t != null && !byName.ContainsKey(t.name))
            {
                byName[t.name] = t;
            }
        }

        SmalNativeRig rig = new SmalNativeRig();
        for (int j = 0; j < SmalNativeJointNames.Length; j++)
        {
            if (!byName.TryGetValue(SmalNativeJointNames[j], out Transform bone))
            {
                return false;
            }

            rig.bones[j] = bone;
        }

        Transform[] b = rig.bones;
        cache.spine = b[0];
        cache.neck = b[15];
        cache.head = b[16];
        cache.tailBase = b[25];
        cache.tailMid = b[28];
        cache.tailTip = b[31];
        cache.leftFrontUpper = b[7];
        cache.leftFrontLower = b[8];
        cache.leftFrontPaw = b[9];
        cache.rightFrontUpper = b[11];
        cache.rightFrontLower = b[12];
        cache.rightFrontPaw = b[13];
        cache.leftRearUpper = b[17];
        cache.leftRearLower = b[18];
        cache.leftRearPaw = b[19];
        cache.leftRearToe = b[20];
        cache.rightRearUpper = b[21];
        cache.rightRearLower = b[22];
        cache.rightRearPaw = b[23];
        cache.rightRearToe = b[24];

        for (int j = 0; j < 35; j++)
        {
            rig.bindWorld[j] = b[j].rotation;
            rig.bindLocalPos[j] = b[0].InverseTransformPoint(b[j].position);
            int child = SmalNativeAlignChild[j];
            rig.bindSegment[j] = child >= 0 ? b[child].position - b[j].position : Vector3.zero;
        }

        rig.limbLateralScale[0] = SmalNativeLimbLateralScale(b, 7, 11);
        rig.limbLateralScale[1] = SmalNativeLimbLateralScale(b, 17, 21);
        smalNativeRigs[cache] = rig;
        // smalNativeLookAtHeadFrame・smalNativeGestureHeadFrame: 頭の前を求める材料（bind の頭→口）。求めるのは ResolveSmalHeadForward
        // （体の前・上・頭の bind の回転がそろった後の、まだ誰も骨を書いていない bind。CreateRigCache の EnsureGestureAnatomy の直後）。
        cache.smalNativeHeadFrameAim = smalNativeLookAtHeadFrame;
        cache.smalNativeGestureHeadFrame = smalNativeGestureHeadFrame;
        cache.smalNativeHeadMouthBindWorld = rig.bindSegment[16];
        Debug.Log($"[SMALNATIVE] model={cache.root?.name} SMAL の 35 関節の骨を見つけた。役を SMAL の関節に付け替え、全関節を体の写像で書く" +
                  $"（脚の横の寄せ k 前 {rig.limbLateralScale[0]:F3} 後 {rig.limbLateralScale[1]:F3}、smalNativeLimbWidthScale={smalNativeLimbWidthScale}）");
        ApplySmalNativeSkinQuality(cache);
        return true;
    }

    // 脚の横の寄せの倍率（smalNativeLimbWidthScale）: 左右の付け根（left = 上の関節、right = その対）の半幅 / 脚の長さ（付け根 → 足先の 3 区間、左右の平均）を
    // SMAL の基準の骨格の同じ比で割る。1 を超えない。left から 3 つ先までが同じ脚の関節（7〜10・11〜14・17〜20・21〜24）。
    private static float SmalNativeLimbLateralScale(Transform[] b, int left, int right)
    {
        float width = 0.5f * Vector3.Distance(b[left].position, b[right].position);
        float length = 0f;
        float restLength = 0f;
        for (int i = 0; i < 3; i++)
        {
            length += 0.5f * (Vector3.Distance(b[left + i].position, b[left + i + 1].position) +
                              Vector3.Distance(b[right + i].position, b[right + i + 1].position));
            restLength += Vector3.Distance(SmalNativeRestJoints[left + i], SmalNativeRestJoints[left + i + 1]);
        }

        float restWidth = Mathf.Abs(SmalNativeRestJoints[left].y);
        if (length < 1e-6f || restLength < 1e-6f || restWidth < 1e-6f)
        {
            return 1f;
        }

        return Mathf.Clamp((width / length) / (restWidth / restLength), 0f, 1f);
    }

    // SMAL の基準の骨格（betas = 0）で頭(16)→口(32) の区間が体の前から下がる角（度）。SmalNativeRestJoints から 36.85°（smalNativeLookAtHeadFrame）。
    internal static float SmalNativeHeadMouthRestDownDegrees()
    {
        Vector3 s = SmalNativeRestJoints[32] - SmalNativeRestJoints[16];
        return Mathf.Atan2(-s.z, s.x) * Mathf.Rad2Deg;
    }

    // 伏せを作り直した形（2026-10-28 夜、StreamingStereoVideoPlayer.AnimalLying.partial.cs）が区間の向きから関節の回転を解くための、
    // SMAL の基準の骨格（betas = 0）の区間（joint → child）と親の関節。
    internal static Vector3 SmalNativeRestSegment(int joint, int child)
    {
        return SmalNativeRestJoints[child] - SmalNativeRestJoints[joint];
    }

    internal static int SmalJointParent(int joint)
    {
        return SmalJointParentArray[joint];
    }

    // 関節 joint の区間が脚なら、その脚の横の寄せの倍率と横の軸の元の関節（前脚 = 胸の 6、後脚 = 根の 0）。脚でなければ false。
    private static bool TryGetSmalNativeLimbLateral(SmalNativeRig rig, int joint, out float scale, out int axisJoint)
    {
        if ((joint >= 7 && joint <= 9) || (joint >= 11 && joint <= 13))
        {
            scale = rig.limbLateralScale[0];
            axisJoint = 6;
            return true;
        }

        if ((joint >= 17 && joint <= 19) || (joint >= 21 && joint <= 23))
        {
            scale = rig.limbLateralScale[1];
            axisJoint = 0;
            return true;
        }

        scale = 1f;
        axisJoint = -1;
        return false;
    }

    private void ApplySmalNativeSkinQuality(AnimalRigCache cache)
    {
        if (cache.root == null)
        {
            return;
        }

        var sb = new System.Text.StringBuilder();
        foreach (SkinnedMeshRenderer smr in cache.root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            SkinQuality before = smr.quality;
            if (smalNativeSkinQuality == 1 || smalNativeSkinQuality == 2 || smalNativeSkinQuality == 4)
            {
                smr.quality = (SkinQuality)smalNativeSkinQuality;
            }

            sb.Append(' ').Append(smr.name).Append(':').Append(before).Append("->").Append(smr.quality);
        }

        Debug.Log($"[SMALNATIVE] model={cache.root.name} skinWeights(global)={QualitySettings.skinWeights} " +
                  $"quality={QualitySettings.GetQualityLevel()}({QualitySettings.names[QualitySettings.GetQualityLevel()]}) " +
                  $"smalNativeSkinQuality={smalNativeSkinQuality} renderers:{sb}");
    }

#if UNITY_EDITOR
    // 棚卸し用（SmalRigPilotTools.DumpAnimalRigInventory、2026-10-23）: 実行時と同じ名前の規則・上書き（AnimalBoneMappingOverride）で役の骨を決めて返す。
    // キャッシュは作るが FK は走らせない（名簿のモデルは skin 姿勢を書くことがあるので、位置は呼ぶ前の bind で採ること）。
    internal Dictionary<string, Transform> DebugResolveRoleBones(Transform root, AnimalPoseSettings settings)
    {
        var roles = new Dictionary<string, Transform>();
        AnimalRigCache c = GetOrBuildAnimalRigCache(root, root, settings);
        if (c == null)
        {
            return roles;
        }

        roles["spine"] = c.spine;
        roles["neck"] = c.neck;
        roles["head"] = c.head;
        roles["tailBase"] = c.tailBase;
        roles["tailMid"] = c.tailMid;
        roles["tailTip"] = c.tailTip;
        roles["frontLUpper"] = c.leftFrontUpper;
        roles["frontLLower"] = c.leftFrontLower;
        roles["frontLPaw"] = c.leftFrontPaw;
        roles["frontRUpper"] = c.rightFrontUpper;
        roles["frontRLower"] = c.rightFrontLower;
        roles["frontRPaw"] = c.rightFrontPaw;
        roles["rearLUpper"] = c.leftRearUpper;
        roles["rearLLower"] = c.leftRearLower;
        roles["rearLPaw"] = c.leftRearPaw;
        roles["rearLToe"] = c.leftRearToe;
        roles["rearRUpper"] = c.rightRearUpper;
        roles["rearRLower"] = c.rightRearLower;
        roles["rearRPaw"] = c.rightRearPaw;
        roles["rearRToe"] = c.rightRearToe;
        roles["_nonQuadruped"] = c.smalNonQuadruped ? root : null;
        return roles;
    }
#endif

    private bool TryGetSmalNativeRig(AnimalRigCache cache, out SmalNativeRig rig)
    {
        rig = null;
        return smalNativeRigFk && cache != null && smalNativeRigs.TryGetValue(cache, out rig) && rig != null;
    }

    // TryApplyAnimalSmalFk から（worldFk0 と root の書き込みの後）。平滑・既定の姿勢への混ぜ・尾の縮めは主ループと同じ。
    // neckKeep: 体を起こしたとき（AnimalSmalPose.rootUprightWeight）に首（関節 15）の積んだ回転へ左から掛ける回転（起こす前の world の向きを保つ）。
    private void ApplySmalNativeRigFk(AnimalRigCache cache, SmalNativeRig rig, AnimalSmalPose pose, AnimalSmalRetargetState state,
        Quaternion worldFk0, Quaternion kMap, float bodySmoothAlpha, float defaultPoseWeight, Quaternion[] tw, Quaternion[] smalAccum,
        Quaternion neckKeep)
    {
        bool keepNeck = Quaternion.Angle(Quaternion.identity, neckKeep) > 1e-4f;
        float tailOverrideWeight = pose.tailOverrideLocal != null && pose.tailOverrideLocal.Length >= 7 ? Mathf.Clamp01(pose.tailOverrideWeight) : 0f;
        Vector3 tailSmoothOmega = Vector3.zero;
        bool useSmoothTail = smalNativeSmoothTail && pose.bodyPose != null && pose.bodyPose.Length >= 31;
        if (useSmoothTail)
        {
            Vector3 meanCum = Vector3.zero;
            for (int k = 0; k < 7; k++)
            {
                Quaternion qk = IsFiniteQ(pose.bodyPose[24 + k]) ? pose.bodyPose[24 + k] : Quaternion.identity;
                meanCum += ((7 - k) / 7f) * QuaternionToRotVecDeg(qk);
            }
            tailSmoothOmega = meanCum / SmalNativeTailBeamMeanCum;
            float totalDeg = tailSmoothOmega.magnitude;
            if (totalDeg > SmalNativeTailMaxTotalDeg)
            {
                tailSmoothOmega *= SmalNativeTailMaxTotalDeg / totalDeg;
            }
        }

        float maxAlignDeg = 0f;
        int maxAlignJoint = -1;
        for (int i = 0; i < SmalJointTopologicalOrder.Length; i++)
        {
            int joint = SmalJointTopologicalOrder[i];
            int parentJoint = SmalJointParentArray[joint];

            Quaternion rawLocal = Quaternion.identity;
            int bodyPoseIdx = joint - 1;
            if (useSmoothTail && joint >= 25 && joint <= 31)
            {
                rawLocal = RotVecDegToQuaternion(SmalNativeTailBeamWeights[joint - 25] * tailSmoothOmega);
            }
            else if (bodyPoseIdx >= 0 && bodyPoseIdx < pose.bodyPose.Length && IsFiniteQ(pose.bodyPose[bodyPoseIdx]))
            {
                rawLocal = pose.bodyPose[bodyPoseIdx];
            }

            if ((joint == 25 || joint == 26) && TailBodyPoseScale < 0.999f)
            {
                rawLocal = Quaternion.Slerp(Quaternion.identity, rawLocal, TailBodyPoseScale);
            }

            // 伏せを作り直した形の尾（2026-10-28 夜）: ならしの後の尾から決めた尾へ。重みが 0 へ戻ると今の尾に続く。
            if (tailOverrideWeight > 0f && joint >= 25 && joint <= 31 && IsFiniteQ(pose.tailOverrideLocal[joint - 25]))
            {
                rawLocal = Quaternion.Slerp(rawLocal, pose.tailOverrideLocal[joint - 25], tailOverrideWeight);
            }

            if (defaultPoseWeight > 0f)
            {
                rawLocal = Quaternion.Slerp(rawLocal, Quaternion.identity, defaultPoseWeight);
            }

            bool skipSmooth = (pose.skipSmoothingMask & (1UL << joint)) != 0;
            Quaternion smalLocal = state.smoothingInitialized && !skipSmooth
                ? Quaternion.Slerp(state.smoothedLocal[joint], rawLocal, bodySmoothAlpha)
                : rawLocal;
            state.smoothedLocal[joint] = smalLocal;
            smalAccum[joint] = smalAccum[parentJoint] * smalLocal;
            if (keepNeck && joint == 15)
            {
                // 体を起こしても首から先（頭・口・耳）は起こす前の world の向き（TryApplyAnimalSmalFk の smalNeckKeep）
                smalAccum[joint] = neckKeep * smalAccum[joint];
            }

            Transform bone = rig.bones[joint];
            Quaternion accum = smalAccum[joint];
            Quaternion mirrored = new Quaternion(accum.x, -accum.y, -accum.z, accum.w);
            Quaternion wA = worldFk0 * Quaternion.Inverse(kMap) * mirrored * kMap;
            Quaternion world = wA * rig.bindWorld[joint];

            int child = SmalNativeAlignChild[joint];
            if (child >= 0 && rig.bindSegment[joint].sqrMagnitude > 1e-10f)
            {
                Vector3 current = wA * rig.bindSegment[joint];
                Vector3 s = accum * (SmalNativeRestJoints[child] - SmalNativeRestJoints[joint]);
                Vector3 target = worldFk0 * (Quaternion.Inverse(kMap) * new Vector3(-s.x, s.y, s.z));
                if (smalNativeLimbWidthScale && TryGetSmalNativeLimbLateral(rig, joint, out float lateralScale, out int axisJoint) && lateralScale < 0.999f)
                {
                    // 横の軸 = SMAL の +Y（左）を付け根の関節の回転で運び、target と同じ写像で world へ
                    Vector3 l = smalAccum[axisJoint] * Vector3.up;
                    Vector3 lateral = worldFk0 * (Quaternion.Inverse(kMap) * new Vector3(-l.x, l.y, l.z));
                    if (lateral.sqrMagnitude > 1e-10f)
                    {
                        lateral.Normalize();
                        target -= (1f - lateralScale) * Vector3.Dot(target, lateral) * lateral;
                    }
                }

                if (current.sqrMagnitude > 1e-10f && target.sqrMagnitude > 1e-10f)
                {
                    Quaternion align = Quaternion.FromToRotation(current, target);
                    world = align * world;
                    float deg = Quaternion.Angle(Quaternion.identity, align);
                    if (deg > maxAlignDeg)
                    {
                        maxAlignDeg = deg;
                        maxAlignJoint = joint;
                    }
                }
            }

            if (IsFiniteQ(world))
            {
                TransformWriter.ApplyWorldRotation(bone, world);
                tw[joint] = world;
            }
        }

        if (!rig.logged)
        {
            rig.logged = true;
            Debug.Log($"[SMALNATIVE] model={cache.root?.name} first tick: 35 joints written, largest segment alignment {maxAlignDeg:F1}° at joint {maxAlignJoint}");
        }

        DumpSmalNativeTick(cache, rig, smalAccum);
    }

    // 確かめ用のダンプ（2026-10-23、Blender の中で姿勢を当てる道具を Unity と突き合わせるため）。バッチの -smalNativeDump <path> のときだけ。
    // tick ごとに 1 行（JSON）: frameCount・モデル名、35 関節の「根の骨（関節 0）から見た」位置と回転（AimAt の前、この FK が書いた直後）、
    // 積んだ SMAL の回転 A_j（SMAL の座標）。最初の行の前に bind（同じく根の骨から見た）を 1 行書く。
    private static string smalNativeDumpPath;
    private static bool smalNativeDumpResolved;

    private static void DumpSmalNativeTick(AnimalRigCache cache, SmalNativeRig rig, Quaternion[] smalAccum)
    {
#if UNITY_EDITOR
        if (!smalNativeDumpResolved)
        {
            smalNativeDumpResolved = true;
            if (Application.isBatchMode)
            {
                string[] args = System.Environment.GetCommandLineArgs();
                for (int i = 0; i < args.Length - 1; i++)
                {
                    if (args[i] == "-smalNativeDump")
                    {
                        smalNativeDumpPath = args[i + 1];
                    }
                }
            }
        }

        if (string.IsNullOrEmpty(smalNativeDumpPath))
        {
            return;
        }

        Transform root = rig.bones[0];
        var sb = new System.Text.StringBuilder();
        if (!rig.dumpedBind)
        {
            rig.dumpedBind = true;
            sb.Append("{\"bind\":true,\"model\":\"").Append(cache.root != null ? cache.root.name : "").Append("\",\"joints\":[");
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            for (int j = 0; j < 35; j++)
            {
                Quaternion q = Quaternion.Inverse(rig.bindWorld[0]) * rig.bindWorld[j];
                Vector3 p = rig.bindLocalPos[j];
                sb.Append(j > 0 ? "," : "").Append("[").Append(p.x.ToString("F5", ci)).Append(",").Append(p.y.ToString("F5", ci)).Append(",")
                  .Append(p.z.ToString("F5", ci)).Append(",").Append(FormatQ(q)).Append("]");
            }

            sb.Append("]}\n");
        }

        sb.Append("{\"frameCount\":").Append(Time.frameCount).Append(",\"model\":\"").Append(cache.root != null ? cache.root.name : "").Append("\",\"joints\":[");
        for (int j = 0; j < 35; j++)
        {
            Vector3 p = root.InverseTransformPoint(rig.bones[j].position);
            Quaternion q = Quaternion.Inverse(root.rotation) * rig.bones[j].rotation;
            sb.Append(j > 0 ? "," : "").Append("[")
              .Append(p.x.ToString("F5", System.Globalization.CultureInfo.InvariantCulture)).Append(",")
              .Append(p.y.ToString("F5", System.Globalization.CultureInfo.InvariantCulture)).Append(",")
              .Append(p.z.ToString("F5", System.Globalization.CultureInfo.InvariantCulture)).Append(",")
              .Append(FormatQ(q)).Append("]");
        }

        sb.Append("],\"accum\":[");
        for (int j = 0; j < 35; j++)
        {
            sb.Append(j > 0 ? "," : "").Append(FormatQ(smalAccum[j]));
        }

        sb.Append("]}\n");
        System.IO.File.AppendAllText(smalNativeDumpPath, sb.ToString());
#endif
    }

    private static string FormatQ(Quaternion q)
    {
        var c = System.Globalization.CultureInfo.InvariantCulture;
        return "[" + q.x.ToString("F6", c) + "," + q.y.ToString("F6", c) + "," + q.z.ToString("F6", c) + "," + q.w.ToString("F6", c) + "]";
    }
}
