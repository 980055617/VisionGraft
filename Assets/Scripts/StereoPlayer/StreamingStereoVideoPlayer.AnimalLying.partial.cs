using UnityEngine;

// 寝ている動物の脚をたたむ（2026-10-28、新しい振る舞い・既定 OFF: animalLyingPoseHardcoded）。ユーザー（10/28、o の動画 sizefit/cat_o_ba.mp4 を見て）
// 「猫に関して最初の数秒だけ姿勢が少し崩れるのもどうにかできる？」。
// 起きていたこと（Docs/tmp/survey_20261025/README.md「10/28: o の動画を見た感想」、scratchpad validate/combined/sizefit/look/cat_start_zoom.jpg）: 猫の動画の
//   0〜5 秒、猫はソファの上で丸まって寝ている（2.5 秒から頭を上げる）。AniMer の SMAL は丸まった猫で前の肘を 53〜59° 曲げるだけで、後ろ脚の膝 6〜8°・
//   飛節 8〜9°（立っている 8 秒とほぼ同じ）、尾は下へ巻く。モデルは足を下へ伸ばした立ち姿に近くなり、⑦ が一番下の骨（足先・尾）を箱の下端へ置くので、
//   体が足の長さだけ座面の上に浮き、斜めに傾いて見える。データからは寝ているかを決められない（箱の縦横比はソファで横を向いて立つ猫と同じ）ので、
//   ボールをくわえる（AnimalCarry.partial.cs）と同じく動画のフレームで決め打ちする（AnimalLyingEntries。この動画だけに効く）。
// しくみ: 表のフレームの間、FK に渡す前の SMAL の body_pose の脚と尾を、決めたたたみの回転へ重み w で寄せる（Quaternion.Slerp、毎 tick データの回転から作り直すので
//   積み重ならない。表のキャッシュの配列は書き換えず、写した配列を渡す）。回転は SMAL の局所（+X 頭・+Y 左・+Z 上）の +Y まわり（+ で下向きの区間の先が後ろへ）:
//   後ろ脚 股 −80°（大腿を前へ）・膝 +150°（下腿を後ろへたたむ）・飛節 −160°（足を前へ）、前脚 肩 +30°・肘 −120°・手根 0°、尾 7 関節は 0°（まっすぐ後ろ。
//   下へ巻いた尾が一番下の骨になって体が浮かないように）。首・頭・背骨・根はデータのまま（2.5 秒から頭を上げるのはそのまま見える）。
//   ⑦ はたたんだ後の骨で一番下を決めるので、体は座面まで下がる。表の終わりの rampFrames で w を 1 → 0（伸びをし始める所でデータの姿勢へ戻る）。
// 効くモデル: どのモデルでも SMAL の回転を変えるので、FK が当てる関節だけ動く（SMAL の骨のリグ 52〜55 は 35 関節すべて、ほかは股・膝・肩・肘）。
//
// 大きさを測り終えるまでたたまない（2026-10-28 午後、animalLyingWaitsForSizeMeasure、既定 true。animalLyingPoseHardcoded が ON のときだけ効く）。
// ユーザー（10/28 午後、新しい版を採った後）「猫最初の姿勢おかしいの直せる？1,2秒の時おかしいやつ」。最初の試し（sizefit/runs/cat_npl）は、たたんだ骨で
//   最初の大きさを測ったので、骨の縦の広がりが半分ほどになった分だけモデルが猫の約 1.8 倍になった（測り直しの倍率 0.253 → 0.449）。
//   大きさが決まるのは、shot の先頭の骨での測り直し（RefineLockedScaleFromProjectedBones、track ごとに 1 回）・V の最初の測り・足元の大きさの定数 c̄
//   （CommitVideoDepthFollowFeetSize、V の 2 回目の測り）の 3 つで、その後のその場では c̄ も f も止まり、骨の広がりは大きさに効かない
//   （np2 の記録: f0 の 1 tick 目に測り直し ×1.490 と f 1.3783、2 tick 目に c̄）。FK を 1 tick に 2 回当てる形は、FK が姿勢の平滑の状態を持つので取らない。
// しくみ: この 3 つがその track の今のインスタンスで済むまで重みを 0 にする（IsAnimalLyingSizeSettled）。動画の始まり・周回・shot の境目・Model の
//   切り替えのたびに 2〜3 tick（約 30 ms）はデータの姿勢のまま測り、その後たたむ。測りが済まない設定（手動の倍率で測り直しを飛ばす track・V の状態が
//   作られない track）ではたたまない。OFF は最初の試しのまま（1 tick 目からたたむ）。
public partial class StreamingStereoVideoPlayer
{
    // 2026-10-28 夜: 作り直した形（animalLyingPoseRemade）と一緒に既定 ON（ユーザー、Blender の台の案を見て「いいじゃない ビルとしてみるわ」。未コミット）。
    public bool animalLyingPoseHardcoded = true;
    public bool animalLyingWaitsForSizeMeasure = true;
    // [ANIMAL-LYING] を出す（効いているフレームの重み。1 秒に 1 回と戻す間、大きさの測りを待つフレーム）。
    public bool logAnimalLyingPose = true;
    // 伏せた前足の角度（2026-10-28 夕、SMAL の局所の +Y まわり°、左右同じ）。ユーザー（10/28 夕、p 版を Quest で見て）「猫について最初休んでいるときの
    // 前足もおかしいのでそこも直してほしい」。p 版は肩 +30°・肘 −120°・手根 0° で、足先（関節 10・14 = LFoot・RFoot）がデータの回転のままだったので、
    // 前へ伸ばした前腕の先で足先が手首から上・後ろへ反り返った（sizefit/look/cat_fold_legs_close.jpg）。元の猫は 3〜5 秒、前足を胸の前で折りたたみ、
    // 足先を座面に平らに置く（sizefit/look/cat_orig_paws.jpg）。足先も決めた角度へ寄せ（0° = 前腕・手根と同じ向きに平らに伸ばす）、肘を後ろへ下げて
    // 前足を胸の近くに収める（棒の絵 sizefit/look/cat12/stick_paw_variants.png の案 3）。p 版は 30 / −120 / 0 で足先なし（animalLyingFoldsFrontPaws = false）。
    public float animalLyingFrontShoulderDegrees = 50f;
    public float animalLyingFrontElbowDegrees = -140f;
    public float animalLyingFrontCarpusDegrees = 0f;
    public float animalLyingFrontPawDegrees = 0f;
    public bool animalLyingFoldsFrontPaws = true;
    // 前足だけをほどく時刻と長さ（2026-10-28 夜、-1 = 表の to・rampFrames のまま = 今と同じ）。ユーザー（10/28 夜、5.5 秒の前足の絵を見て）
    // 「猫の前足がそのように曲がることなくない？」。表のとおり 5.08 秒（f122）から 0.5 秒かけて肘 −140° のたたみからデータへ戻すと、
    // 姿勢のならし（半減期 0.12 秒）の遅れも足されて 5.1〜5.8 秒の間、肘が直角の L 字（5.5 秒 89°、データは 125°）を通る。元の動画の猫は
    // 5.25 秒まで前足をたたんで伏せたまま、5.5 秒にはもう前へ伸ばしている（scratchpad validate/combined/catrig/look/frontleg/）。
    // 前足だけ、たたみを end のフレームまで続け、frames のフレームで戻す（後ろ脚・尾は表のまま）。
    public int animalLyingFrontReleaseEndFrame = -1;
    public int animalLyingFrontReleaseFrames = -1;
    // 前足をほどく間（と直後の 1 フレーム）だけ、前足 8 関節の姿勢のならしを飛ばす（既定 OFF）。ならしが前の形を 0.1〜0.3 秒引きずり、
    // ほどく時刻を早めても 5.4〜5.7 秒の L 字が残った（ほどき終わりの 0.17 秒後でまだたたみの約 4 割）。伏せを切った走りは同じ時刻に前下へ伸ばす。
    public bool animalLyingFrontReleaseSkipsSmoothing = false;
    private const ulong AnimalLyingFrontJointsMask = (1UL << 7) | (1UL << 8) | (1UL << 9) | (1UL << 10) |
                                                     (1UL << 11) | (1UL << 12) | (1UL << 13) | (1UL << 14);

    // 伏せの形を作り直した版（2026-10-28 夜、既定 OFF。animalLyingPoseHardcoded と一緒に ON にしたときだけ効く）。ユーザー（10/28 夜、伏せた猫の横の絵を付けて）
    // 「そういう意味じゃなくて，今作っているこの体勢自体ちょっとおかしくない？もっとうまい姿勢作ってほしいという」→ Blender の台の案を見て「いいじゃない」。
    // 今の形のおかしい所（Docs/tmp/survey_20261025/README.md「10/28 夜: 伏せた姿勢そのもの」）: 前腕を前へ出したまま足先を回していないので指が真上を向く鉤、
    //   体が横に 15〜34° 傾いたまま（データが寝ている猫を横倒しぎみに当てはめ、頭だけ傾きを打ち消している）、上腕が立って胸が座面から浮く、尾がまっすぐ後ろ。
    // 作り直した形（scratchpad validate/combined/catrig/lying/poses/v5b.json、Unity の FK を写した Blender の台 rig/bench/smal_pose_bench.py で決めた）:
    //   - 体を起こす（AnimalSmalPose.rootUprightWeight。FK が根の平滑の後に上下まわりだけの根へ寄せ、首から先は寄せる前の world の向きを保つ）
    //   - 脚は区間の向き（体を起こした体の座標 = SMAL の根の座標）で決め、基準の骨格の区間からの最小の回転で解く。前肩はデータの胸（関節 6）の今の回転から解くので、
    //     データの背骨が曲がっても脚の向きは変わらない。前: 上腕 後ろ 25° 下・前腕 前（少し内）・手の甲 前に平ら・足先 +70°。後ろ: 大腿 前 17° 下・下腿 後ろ・
    //     足の甲 前に平ら・足先 +80°。右は左の y を反転
    //   - 尾はお尻の後ろで下げてから左の横へ回す（AnimalSmalPose.tailOverrideLocal。尾のならしの後に混ぜる）
    //   座面からの高さ（Blender の台、胴の長さに対する割合）: 胸 6%・お腹 16%、脚は 0〜8%。残り: 肘と膝の外側に小さなとがり（皮膚が 145° 前後で折れる所）、
    //   尾は元の猫より短く前足まで届かない、キツネは太い尾がお尻の下で丸まる。
    // ほどき方: 前足は frontEnd まで保ち frontRamp フレームで、後ろ脚・尾・体を起こす補正は bodyEnd まで保ち bodyRamp で。重みは動画の時計（vp.clockTime）の
    //   連続な時刻で変える（コマごとに変えると前足が 3 段にカクッと動いた、10/28 夜の r2）。frontSkipsSmoothing なら前足をほどく間は前足のならしを飛ばす。
    // Unity で確かめた（scratchpad validate/combined/catrig/queue_remade.ps1、猫 53・キツネ 55）: 頭の world の回転は今の伏せと同じ tick で差 0.000°、
    //   体の傾き 15〜40° → 0°、大きさは差 0.00000%、伏せの間の揺れは今と同じ程度、ほどきは tick ごとになめらか（速い版は前が 0.15 秒で約 3 cm 持ち上がる、下の既定の説明）。既定 ON（上と同じ）。
    public bool animalLyingPoseRemade = true;
    // ほどき方の既定は今の伏せと同じ（5.08 秒 = f122 から 12 フレーム = 0.5 秒、前足も後ろ脚も、ならしはそのまま）。ユーザー（10/28 夜、元の動画・今の伏せ・
    //   作り直しの起き上がりの動画を見て）「３は今の伏せの方がいいじゃない？」: 前足だけ f126 から 4 フレームでならしを飛ばしてほどくと、胸が 0.22 秒で 3.5 cm
    //   ぱっと持ち上がる（胸の下から伸びた前足が座面に立ち、2 回目の ⑦ が体を上げる）。元の猫はこの間、体を低くしたまま前足を伸ばす。今の伏せと同じほどき方なら
    //   胸は 5.1〜5.56 秒に 3.5 cm（今の伏せは 5.2〜5.6 秒に 2.7 cm）でゆっくり起きる（scratchpad validate/combined/catrig/queue_remade2.ps1、
    //   catrig/lying/unity/cat_release4_seat.jpg）。速い版は 126・4・130・12・true。
    public int animalLyingRemadeFrontEndFrame = 122;
    public int animalLyingRemadeFrontRampFrames = 12;
    public int animalLyingRemadeBodyEndFrame = 122;
    public int animalLyingRemadeBodyRampFrames = 12;
    public bool animalLyingRemadeFrontSkipsSmoothing = false;
    public bool animalLyingRemadeContinuousTime = true;

    // 区間の向き（左の脚、体の座標 +X 頭・+Y 左・+Z 上）。関節 → 子の区間。右（11〜13・21〜23）は y を反転。
    internal static readonly int[] AnimalLyingRemadeLegFirstJoints = { 7, 11, 17, 21 };
    internal static readonly Vector3[] AnimalLyingRemadeLegDirs =
    {
        new Vector3(-0.90f, 0.12f, -0.42f), // 7 上腕
        new Vector3(0.97f, -0.15f, -0.10f), // 8 前腕
        new Vector3(1.00f, -0.08f, -0.02f), // 9 手の甲
        new Vector3(0.93f, 0.15f, -0.30f),  // 17 大腿
        new Vector3(-0.98f, 0.02f, -0.15f), // 18 下腿
        new Vector3(1.00f, -0.05f, 0.00f),  // 19 足の甲
    };
    internal const float AnimalLyingRemadeFrontPawDegrees = 70f;
    internal const float AnimalLyingRemadeHindFootDegrees = 80f;
    // 尾の区間（25→26 … 30→31）の向き。尾の先（31）は単位。
    internal static readonly Vector3[] AnimalLyingRemadeTailDirs =
    {
        new Vector3(-0.50f, 0.15f, -0.85f),
        new Vector3(-0.25f, 0.40f, -0.88f),
        new Vector3(0.10f, 0.80f, -0.60f),
        new Vector3(0.40f, 0.80f, -0.45f),
        new Vector3(0.85f, 0.45f, -0.30f),
        new Vector3(1.00f, 0.05f, -0.10f),
    };
    private static Quaternion[] animalLyingRemadeTailLocal;

    private sealed class AnimalLyingEntry
    {
        public string bundle;
        public uint track;
        public int from;
        public int to;
        public int rampFrames;
    }

    // 動画のフレーム（24 fps）。猫の動画: 0〜2.5 秒 丸まって寝る、2.5〜5.1 秒 頭を上げて伏せたまま、5.2 秒から前へ伸びをして 5.6〜6.5 秒に前足を座面の縁から
    // 下へ伸ばす（元の動画のコマ送り、sizefit/look/orig_cat_00_06.jpg・orig_cat_04_11.jpg）。
    private static readonly AnimalLyingEntry[] AnimalLyingEntries =
    {
        new AnimalLyingEntry { bundle = "bundle_cat.svb", track = 0, from = 0, to = 122, rampFrames = 12 },
    };

    // (SMAL の関節, +Y まわりの角度°)。左右の対はどちらも同じ角度（+Y は左右どちらの脚でも横の軸）。前足は ResolveAnimalLyingPose の引数（上の設定）。
    private static readonly int[] AnimalLyingFoldJoints = { 17, 18, 19, 21, 22, 23 };
    private static readonly float[] AnimalLyingFoldDegrees = { -80f, 150f, -160f, -80f, 150f, -160f };
    // 前足の肩・肘・手根・足先（左 7〜10、右 11〜14）。
    private static readonly int[] AnimalLyingFrontLeftJoints = { 7, 8, 9, 10 };
    private static readonly int[] AnimalLyingFrontRightJoints = { 11, 12, 13, 14 };
    private const int AnimalLyingTailFirstJoint = 25;
    private const int AnimalLyingTailLastJoint = 31;

    // 写した body_pose（同じ tick の中で FK に渡すだけなので 1 つを使い回す）。
    private Quaternion[] animalLyingBodyPoseBuffer;
    private int animalLyingLoggedFrame = -1;
    private int animalLyingWaitLoggedFrame = -1;

    // 表の区間のたたみの重み（純粋な計算、テスト AnimalLyingPoseTests）: from〜to は 1、その後 ramp フレームで 1 → 0、区間の外は 0。
    internal static float ResolveAnimalLyingReleaseWeight(int frame, int from, int to, int ramp)
    {
        if (frame < from)
        {
            return 0f;
        }

        if (frame <= to)
        {
            return 1f;
        }

        return Mathf.Clamp01(1f - (frame - to) / (float)Mathf.Max(1, ramp));
    }

    // 前足をほどいている間か（純粋な計算）: 終わりの後 ramp フレームと、その直後の 1 フレーム（ならしの状態をデータの回転で終える）。
    internal static bool IsAnimalLyingFrontReleasing(int frame, int end, int ramp)
    {
        return frame > end && frame <= end + Mathf.Max(1, ramp) + 1;
    }

    // 連続な時刻（フレーム単位、コマの間も進む）の重み（純粋な計算、テスト AnimalLyingPoseTests）: from より前は 0、to までは 1、その後 ramp フレームで 1 → 0。
    internal static float ResolveAnimalLyingReleaseWeightAt(float frameTime, int from, int to, int ramp)
    {
        if (frameTime < from)
        {
            return 0f;
        }

        if (frameTime <= to)
        {
            return 1f;
        }

        return Mathf.Clamp01(1f - (frameTime - to) / Mathf.Max(1, ramp));
    }

    internal static bool IsAnimalLyingFrontReleasingAt(float frameTime, int end, int ramp)
    {
        return frameTime > end && frameTime <= end + Mathf.Max(1, ramp) + 1;
    }

    // 動画の時計の連続な時刻（フレーム単位）。止まっている・時計と表示のフレームが食い違う（PoseInterpolation と同じ見分け）ときは frame のまま。
    private float ResolveAnimalLyingFrameTime(int frame)
    {
        if (!animalLyingRemadeContinuousTime || vp == null || !vp.isPlaying)
        {
            return frame;
        }

        double fps = vp.frameRate > 0f ? vp.frameRate : ResolveMetaFps();
        double frac = vp.clockTime * fps - frame;
        if (frac < -0.02 || frac >= 1.02)
        {
            return frame;
        }

        return frame + Mathf.Clamp((float)frac, 0f, 0.999f);
    }

    // 作り直した形の尾の局所の回転（7 個、定数）。尾の区間の向きから最小の回転で解き、根から積んだ回転で割る（Tail1 の親は根 = 単位）。尾の先は単位。
    internal static Quaternion[] ResolveAnimalLyingRemadeTailLocal()
    {
        if (animalLyingRemadeTailLocal != null)
        {
            return animalLyingRemadeTailLocal;
        }

        var local = new Quaternion[7];
        Quaternion accum = Quaternion.identity;
        for (int k = 0; k < 6; k++)
        {
            int j = 25 + k;
            Quaternion target = Quaternion.FromToRotation(AnimalPoseApplier.SmalNativeRestSegment(j, j + 1), AnimalLyingRemadeTailDirs[k]);
            local[k] = Quaternion.Inverse(accum) * target;
            accum = target;
        }

        local[6] = Quaternion.identity;
        animalLyingRemadeTailLocal = local;
        return local;
    }

    // 作り直した形の脚（純粋な計算、テスト AnimalLyingPoseTests）。pose.bodyPose[j − 1] が関節 j の局所。区間の向きから最小の回転で解き、
    // 親（前脚 = 今の胸 = 関節 1〜6 を積んだ回転、後ろ脚 = 根）で割って、データの局所から重みで寄せる。足先は局所の +Y まわり。
    // wFront = 前脚 7〜14、wBody = 後ろ脚 17〜24。buffer に写して返す（pose の配列は書き換えない）。尾・体を起こす補正は呼ぶ側が渡す。
    internal static AnimalSmalPose ResolveAnimalLyingRemadePose(AnimalSmalPose pose, float wFront, float wBody, ref Quaternion[] buffer)
    {
        if (pose.bodyPose == null || pose.bodyPose.Length < 24 || !(wFront > 0f || wBody > 0f))
        {
            return pose;
        }

        if (buffer == null || buffer.Length != pose.bodyPose.Length)
        {
            buffer = new Quaternion[pose.bodyPose.Length];
        }

        System.Array.Copy(pose.bodyPose, buffer, pose.bodyPose.Length);
        Quaternion chest = Quaternion.identity;
        for (int j = 1; j <= 6; j++)
        {
            chest = chest * buffer[j - 1];
        }

        for (int leg = 0; leg < AnimalLyingRemadeLegFirstJoints.Length; leg++)
        {
            bool front = leg < 2;
            float w = Mathf.Clamp01(front ? wFront : wBody);
            if (w <= 0f)
            {
                continue;
            }

            bool right = (leg % 2) == 1;
            int first = AnimalLyingRemadeLegFirstJoints[leg];
            Quaternion parent = front ? chest : Quaternion.identity;
            for (int k = 0; k < 3; k++)
            {
                int j = first + k;
                Vector3 d = AnimalLyingRemadeLegDirs[(front ? 0 : 3) + k];
                if (right)
                {
                    d.y = -d.y;
                }

                Quaternion target = Quaternion.FromToRotation(AnimalPoseApplier.SmalNativeRestSegment(j, j + 1), d);
                buffer[j - 1] = Quaternion.Slerp(buffer[j - 1], Quaternion.Inverse(parent) * target, w);
                parent = parent * buffer[j - 1];
            }

            int leaf = first + 3;
            float leafDegrees = front ? AnimalLyingRemadeFrontPawDegrees : AnimalLyingRemadeHindFootDegrees;
            buffer[leaf - 1] = Quaternion.Slerp(buffer[leaf - 1], Quaternion.AngleAxis(leafDegrees, Vector3.up), w);
        }

        pose.bodyPose = buffer;
        return pose;
    }

    // 作り直した形の重み（前足 wFront、後ろ脚・尾・体を起こす補正 wBody）。表の区間の track でなければ false。
    private bool TryResolveAnimalLyingRemadeWeights(MetaObj obj, int frame, out float frameTime, out float wFront, out float wBody, out bool frontReleasing)
    {
        frameTime = frame;
        wFront = 0f;
        wBody = 0f;
        frontReleasing = false;
        if (!animalLyingPoseHardcoded || !IsCategoryAnimal(obj.categoryId))
        {
            return false;
        }

        string bundle = System.IO.Path.GetFileName(bundleFileName);
        for (int i = 0; i < AnimalLyingEntries.Length; i++)
        {
            AnimalLyingEntry e = AnimalLyingEntries[i];
            if (e.bundle != bundle || e.track != obj.trackId)
            {
                continue;
            }

            frameTime = ResolveAnimalLyingFrameTime(frame);
            wFront = ResolveAnimalLyingReleaseWeightAt(frameTime, e.from, animalLyingRemadeFrontEndFrame, animalLyingRemadeFrontRampFrames);
            wBody = ResolveAnimalLyingReleaseWeightAt(frameTime, e.from, animalLyingRemadeBodyEndFrame, animalLyingRemadeBodyRampFrames);
            frontReleasing = IsAnimalLyingFrontReleasingAt(frameTime, animalLyingRemadeFrontEndFrame, animalLyingRemadeFrontRampFrames);
            return wFront > 0f || wBody > 0f || frontReleasing;
        }

        return false;
    }

    private AnimalSmalPose ApplyAnimalLyingRemadePoseIfAny(MetaObj obj, GameObject instance, int frame, AnimalSmalPose pose)
    {
        if (!TryResolveAnimalLyingRemadeWeights(obj, frame, out float frameTime, out float wFront, out float wBody, out bool frontReleasing))
        {
            return pose;
        }

        if (animalLyingWaitsForSizeMeasure && !IsAnimalLyingSizeMeasured(obj, instance, frame, out string state))
        {
            if (logAnimalLyingPose && frame != animalLyingWaitLoggedFrame)
            {
                animalLyingWaitLoggedFrame = frame;
                Debug.Log($"[ANIMAL-LYING] remade track={obj.trackId} f={frame} w=0 waiting for the size measure ({state}) model={(instance != null ? instance.name : "-")}");
            }

            return pose;
        }

        if (logAnimalLyingPose && frame != animalLyingLoggedFrame && (frame % 24 == 0 || wFront < 1f || wBody < 1f || frame == animalLyingWaitLoggedFrame))
        {
            animalLyingLoggedFrame = frame;
            Debug.Log($"[ANIMAL-LYING] remade track={obj.trackId} f={frame} t={frameTime:F3} front={wFront:F3} body={wBody:F3} skipSmooth={(animalLyingRemadeFrontSkipsSmoothing && frontReleasing ? 1 : 0)}");
        }

        AnimalSmalPose lying = ResolveAnimalLyingRemadePose(pose, wFront, wBody, ref animalLyingBodyPoseBuffer);
        if (wBody > 0f)
        {
            lying.rootUprightWeight = wBody;
            lying.tailOverrideWeight = wBody;
            lying.tailOverrideLocal = ResolveAnimalLyingRemadeTailLocal();
        }

        if (animalLyingRemadeFrontSkipsSmoothing && frontReleasing)
        {
            lying.skipSmoothingMask |= AnimalLyingFrontJointsMask;
        }

        return lying;
    }

    // この tick の track の重み（後ろ脚・尾の weight と前足の frontWeight。どちらも 0 で前足をほどき終えた後なら false）。
    private bool TryResolveAnimalLyingWeight(MetaObj obj, int frame, out float weight, out float frontWeight, out bool frontReleasing)
    {
        weight = 0f;
        frontWeight = 0f;
        frontReleasing = false;
        if (!animalLyingPoseHardcoded || !IsCategoryAnimal(obj.categoryId))
        {
            return false;
        }

        string bundle = System.IO.Path.GetFileName(bundleFileName);
        for (int i = 0; i < AnimalLyingEntries.Length; i++)
        {
            AnimalLyingEntry e = AnimalLyingEntries[i];
            if (e.bundle != bundle || e.track != obj.trackId)
            {
                continue;
            }

            int frontEnd = animalLyingFrontReleaseEndFrame >= 0 ? animalLyingFrontReleaseEndFrame : e.to;
            int frontRamp = animalLyingFrontReleaseFrames >= 0 ? animalLyingFrontReleaseFrames : e.rampFrames;
            weight = ResolveAnimalLyingReleaseWeight(frame, e.from, e.to, e.rampFrames);
            frontWeight = ResolveAnimalLyingReleaseWeight(frame, e.from, frontEnd, frontRamp);
            frontReleasing = IsAnimalLyingFrontReleasing(frame, frontEnd, frontRamp);
            return weight > 0f || frontWeight > 0f || frontReleasing;
        }

        return false;
    }

    // 脚と尾をたたみの回転へ寄せた姿勢（純粋な計算、テスト AnimalLyingPoseTests）。pose.bodyPose[j − 1] が SMAL の関節 j の局所の回転。
    // buffer に写して返す（pose の配列は書き換えない）。前足は p 版の角度（肩 +30°・肘 −120°・手根 0°、足先はデータのまま）。
    internal static AnimalSmalPose ResolveAnimalLyingPose(AnimalSmalPose pose, float weight, ref Quaternion[] buffer)
    {
        return ResolveAnimalLyingPose(pose, weight, ref buffer, 30f, -120f, 0f, float.NaN);
    }

    // 前足の i 番目の関節（0 肩・1 肘・2 手根・3 足先）を左右とも決めた角度へ寄せる。NaN ならデータのまま。
    private static void FoldAnimalLyingFrontJoint(Quaternion[] buffer, int i, float degrees, float w)
    {
        if (float.IsNaN(degrees))
        {
            return;
        }

        Quaternion q = Quaternion.AngleAxis(degrees, Vector3.up);
        int left = AnimalLyingFrontLeftJoints[i] - 1;
        int right = AnimalLyingFrontRightJoints[i] - 1;
        if (left >= 0 && left < buffer.Length)
        {
            buffer[left] = Quaternion.Slerp(buffer[left], q, w);
        }

        if (right >= 0 && right < buffer.Length)
        {
            buffer[right] = Quaternion.Slerp(buffer[right], q, w);
        }
    }

    // 前足の肩・肘・手根・足先の角度を指定する形。paw が NaN なら足先はデータのまま。
    internal static AnimalSmalPose ResolveAnimalLyingPose(AnimalSmalPose pose, float weight, ref Quaternion[] buffer,
        float shoulder, float elbow, float carpus, float paw)
    {
        return ResolveAnimalLyingPose(pose, weight, weight, ref buffer, shoulder, elbow, carpus, paw);
    }

    // 前足だけ別の重み（frontWeight）でたたむ形。後ろ脚・尾は weight。
    internal static AnimalSmalPose ResolveAnimalLyingPose(AnimalSmalPose pose, float weight, float frontWeight, ref Quaternion[] buffer,
        float shoulder, float elbow, float carpus, float paw)
    {
        if (pose.bodyPose == null || !(weight > 0f || frontWeight > 0f))
        {
            return pose;
        }

        if (buffer == null || buffer.Length != pose.bodyPose.Length)
        {
            buffer = new Quaternion[pose.bodyPose.Length];
        }

        System.Array.Copy(pose.bodyPose, buffer, pose.bodyPose.Length);
        float w = Mathf.Clamp01(weight);
        for (int i = 0; i < AnimalLyingFoldJoints.Length; i++)
        {
            int index = AnimalLyingFoldJoints[i] - 1;
            if (index >= 0 && index < buffer.Length)
            {
                buffer[index] = Quaternion.Slerp(buffer[index], Quaternion.AngleAxis(AnimalLyingFoldDegrees[i], Vector3.up), w);
            }
        }

        float wf = Mathf.Clamp01(frontWeight);
        if (wf > 0f)
        {
            FoldAnimalLyingFrontJoint(buffer, 0, shoulder, wf);
            FoldAnimalLyingFrontJoint(buffer, 1, elbow, wf);
            FoldAnimalLyingFrontJoint(buffer, 2, carpus, wf);
            FoldAnimalLyingFrontJoint(buffer, 3, paw, wf);
        }

        for (int j = AnimalLyingTailFirstJoint; j <= AnimalLyingTailLastJoint; j++)
        {
            int index = j - 1;
            if (index >= 0 && index < buffer.Length)
            {
                buffer[index] = Quaternion.Slerp(buffer[index], Quaternion.identity, w);
            }
        }

        pose.bodyPose = buffer;
        return pose;
    }

    // その track の大きさの測りが済んだか（純粋な計算、テスト AnimalLyingPoseTests）。refineOn = shot の先頭の骨での測り直しがある設定、refined = 済んだ。
    // followOn = V がこの track に効く。V の状態（今のインスタンスのもの）の最初の測りが済み、足元の大きさが効くフレームなら定数 c̄ も決まっていること。
    internal static bool IsAnimalLyingSizeSettled(bool refineOn, bool refined, bool followOn, bool hasFollowState, bool sameInstance, bool hasMeasure,
        bool feetSizeApplies, float feetSizeC)
    {
        if (refineOn && !refined)
        {
            return false;
        }

        if (!followOn)
        {
            return true;
        }

        if (!hasFollowState || !sameInstance || !hasMeasure)
        {
            return false;
        }

        return !feetSizeApplies || (!float.IsNaN(feetSizeC) && !float.IsInfinity(feetSizeC));
    }

    private bool IsAnimalLyingSizeMeasured(MetaObj obj, GameObject instance, int frame, out string state)
    {
        bool followOn = placementFollowVideoDepth && IsStereoDepthSkeletonTrack(obj.categoryId) && IsPlacementFollowDepthAvailable(out _);
        bool hasState = videoDepthFollowByTrack.TryGetValue(obj.trackId, out VideoDepthFollowState st) && st != null;
        bool sameInstance = hasState && instance != null && st.instanceId == instance.GetInstanceID();
        bool hasMeasure = hasState && st.hasMeasure;
        float feetSizeC = hasState ? st.feetSizeC : float.NaN;
        bool feetSizeApplies = placementFollowVideoDepthFromFeet && placementFollowVideoDepthSizeFromFeet && videoDepthFollowFeetBoxH != null &&
                               videoDepthFollowFeetBoxH.ContainsKey(VideoDepthFollowStillKey(frame, obj.trackId));
        bool refined = scaleRefinedByTrack.Contains(obj.trackId);
        state = $"refined={(refined ? 1 : 0)} follow={(followOn ? 1 : 0)} state={(hasState ? 1 : 0)} same={(sameInstance ? 1 : 0)} " +
                $"measured={(hasMeasure ? 1 : 0)} feet={(feetSizeApplies ? 1 : 0)} c={feetSizeC:F5}";
        return IsAnimalLyingSizeSettled(refineScaleFromProjectedBones, refined, followOn, hasState, sameInstance, hasMeasure, feetSizeApplies, feetSizeC);
    }

    // PosePipeline の animalPoseApplier.Apply の直前に呼ぶ（SMAL の姿勢があるときだけ）。
    private AnimalSmalPose ApplyAnimalLyingPoseIfAny(MetaObj obj, GameObject instance, int frame, AnimalSmalPose pose)
    {
        if (animalLyingPoseRemade)
        {
            return ApplyAnimalLyingRemadePoseIfAny(obj, instance, frame, pose);
        }

        if (!TryResolveAnimalLyingWeight(obj, frame, out float weight, out float frontWeight, out bool frontReleasing))
        {
            return pose;
        }

        if (animalLyingWaitsForSizeMeasure && !IsAnimalLyingSizeMeasured(obj, instance, frame, out string state))
        {
            if (logAnimalLyingPose && frame != animalLyingWaitLoggedFrame)
            {
                animalLyingWaitLoggedFrame = frame;
                Debug.Log($"[ANIMAL-LYING] track={obj.trackId} f={frame} w=0 waiting for the size measure ({state}) model={(instance != null ? instance.name : "-")}");
            }

            return pose;
        }

        if (logAnimalLyingPose && frame != animalLyingLoggedFrame && (frame % 24 == 0 || weight < 1f || frontWeight < 1f || frame == animalLyingWaitLoggedFrame))
        {
            animalLyingLoggedFrame = frame;
            Debug.Log($"[ANIMAL-LYING] track={obj.trackId} f={frame} w={weight:F3} front={frontWeight:F3}");
        }

        AnimalSmalPose folded = ResolveAnimalLyingPose(pose, weight, frontWeight, ref animalLyingBodyPoseBuffer, animalLyingFrontShoulderDegrees,
            animalLyingFrontElbowDegrees, animalLyingFrontCarpusDegrees, animalLyingFoldsFrontPaws ? animalLyingFrontPawDegrees : float.NaN);
        if (animalLyingFrontReleaseSkipsSmoothing && frontReleasing)
        {
            folded.skipSmoothingMask |= AnimalLyingFrontJointsMask;
        }

        return folded;
    }
}
