using NUnit.Framework;
using UnityEngine;

// 寝ている動物の脚をたたむ（animalLyingPoseHardcoded、2026-10-28、既定 OFF、AnimalLying.partial.cs）と、大きさを測り終えるまでたたまない
// （animalLyingWaitsForSizeMeasure、10/28 午後、既定 true）。
public class AnimalLyingPoseTests
{
    private static AnimalSmalPose Pose()
    {
        var pose = new AnimalSmalPose { bodyPose = new Quaternion[34] };
        for (int i = 0; i < 34; i++)
        {
            pose.bodyPose[i] = Quaternion.Euler(3f * i, -2f * i, i);
        }

        return pose;
    }

    // 2026-10-28 夜から既定 ON（作り直した形と一緒、ユーザー「いいじゃない ビルとしてみるわ」）。
    [Test]
    public void OnByDefaultAndWaitsForTheSize()
    {
        GameObject host = new GameObject("AnimalLyingPoseTests");
        try
        {
            StreamingStereoVideoPlayer player = host.AddComponent<StreamingStereoVideoPlayer>();
            Assert.That(player.animalLyingPoseHardcoded, Is.True);
            Assert.That(player.animalLyingWaitsForSizeMeasure, Is.True);
        }
        finally
        {
            Object.DestroyImmediate(host);
        }
    }

    // 重み 1: 脚の関節はたたみの回転（SMAL の +Y まわり）、尾 7 関節は 0°、ほかの関節はデータのまま。元の配列は書き換えない。
    [Test]
    public void FullWeightReplacesTheLegsAndTailOnly()
    {
        AnimalSmalPose pose = Pose();
        Quaternion[] original = (Quaternion[])pose.bodyPose.Clone();
        Quaternion[] buffer = null;
        AnimalSmalPose folded = StreamingStereoVideoPlayer.ResolveAnimalLyingPose(pose, 1f, ref buffer);
        Assert.That(folded.bodyPose, Is.SameAs(buffer));
        Assert.That(Quaternion.Angle(folded.bodyPose[18 - 1], Quaternion.AngleAxis(150f, Vector3.up)), Is.LessThan(0.01f));
        Assert.That(Quaternion.Angle(folded.bodyPose[17 - 1], Quaternion.AngleAxis(-80f, Vector3.up)), Is.LessThan(0.01f));
        Assert.That(Quaternion.Angle(folded.bodyPose[8 - 1], Quaternion.AngleAxis(-120f, Vector3.up)), Is.LessThan(0.01f));
        for (int j = 25; j <= 31; j++)
        {
            Assert.That(Quaternion.Angle(folded.bodyPose[j - 1], Quaternion.identity), Is.LessThan(0.01f), $"tail joint {j}");
        }

        foreach (int j in new[] { 1, 6, 15, 16, 32, 33, 34 })
        {
            Assert.That(Quaternion.Angle(folded.bodyPose[j - 1], original[j - 1]), Is.LessThan(0.01f), $"joint {j}");
        }

        for (int i = 0; i < 34; i++)
        {
            Assert.That(Quaternion.Angle(pose.bodyPose[i], original[i]), Is.LessThan(0.0001f), $"input {i}");
        }
    }

    // 重み 0 は何もしない。0.5 はデータとたたみの真ん中（膝はデータから 150° の回転までの角の半分）。
    [Test]
    public void PartialWeightBlendsAndZeroDoesNothing()
    {
        AnimalSmalPose pose = Pose();
        Quaternion[] buffer = null;
        AnimalSmalPose same = StreamingStereoVideoPlayer.ResolveAnimalLyingPose(pose, 0f, ref buffer);
        Assert.That(same.bodyPose, Is.SameAs(pose.bodyPose));
        AnimalSmalPose half = StreamingStereoVideoPlayer.ResolveAnimalLyingPose(pose, 0.5f, ref buffer);
        Quaternion knee = Quaternion.AngleAxis(150f, Vector3.up);
        float full = Quaternion.Angle(pose.bodyPose[17], knee);
        Assert.That(Quaternion.Angle(pose.bodyPose[17], half.bodyPose[17]), Is.EqualTo(full * 0.5f).Within(0.05f));
        Assert.That(Quaternion.Angle(half.bodyPose[17], knee), Is.EqualTo(full * 0.5f).Within(0.05f));
    }

    // 動画の始まりの tick の順（np2 の記録）: 1 tick 目は測り直しの前、2 tick 目は測り直しと V の最初の測りの後で足元の大きさの定数がまだ、3 tick 目に定数が決まる。
    [Test]
    public void WaitsUntilTheRefineTheFirstFollowMeasureAndTheFeetConstant()
    {
        Assert.That(StreamingStereoVideoPlayer.IsAnimalLyingSizeSettled(true, false, true, false, false, false, true, float.NaN), Is.False, "tick 1");
        Assert.That(StreamingStereoVideoPlayer.IsAnimalLyingSizeSettled(true, true, true, true, true, true, true, float.NaN), Is.False, "tick 2");
        Assert.That(StreamingStereoVideoPlayer.IsAnimalLyingSizeSettled(true, true, true, true, true, true, true, 0.31f), Is.True, "tick 3");
        // 足元の大きさが効かないフレームは V の最初の測りまで、測りの状態が無い・前のインスタンスのもの（Model の切り替え）は待つ。
        Assert.That(StreamingStereoVideoPlayer.IsAnimalLyingSizeSettled(true, true, true, true, true, true, false, float.NaN), Is.True, "no feet size");
        Assert.That(StreamingStereoVideoPlayer.IsAnimalLyingSizeSettled(true, true, true, false, false, false, false, float.NaN), Is.False, "no state");
        Assert.That(StreamingStereoVideoPlayer.IsAnimalLyingSizeSettled(true, true, true, true, false, true, true, 0.31f), Is.False, "old instance");
        Assert.That(StreamingStereoVideoPlayer.IsAnimalLyingSizeSettled(true, true, true, true, true, false, true, 0.31f), Is.False, "not measured");
    }

    // 前足（2026-10-28 夕）: 既定は肩 +50°・肘 −140°・手根 0°・足先 0°（足先もたたむ）。
    [Test]
    public void FrontPawsFoldFlatByDefault()
    {
        GameObject host = new GameObject("AnimalLyingPoseTests");
        try
        {
            StreamingStereoVideoPlayer player = host.AddComponent<StreamingStereoVideoPlayer>();
            Assert.That(player.animalLyingFoldsFrontPaws, Is.True);
            Assert.That(player.animalLyingFrontShoulderDegrees, Is.EqualTo(50f));
            Assert.That(player.animalLyingFrontElbowDegrees, Is.EqualTo(-140f));
            Assert.That(player.animalLyingFrontCarpusDegrees, Is.EqualTo(0f));
            Assert.That(player.animalLyingFrontPawDegrees, Is.EqualTo(0f));
        }
        finally
        {
            Object.DestroyImmediate(host);
        }
    }

    // 角度を指定する形: 前足 4 関節は左右とも指定の角度、足先が NaN ならデータのまま（p 版の形）。後ろ脚・尾は今までどおり。
    [Test]
    public void FrontJointsTakeTheGivenAnglesAndNaNKeepsThePaw()
    {
        AnimalSmalPose pose = Pose();
        Quaternion[] original = (Quaternion[])pose.bodyPose.Clone();
        Quaternion[] buffer = null;
        AnimalSmalPose folded = StreamingStereoVideoPlayer.ResolveAnimalLyingPose(pose, 1f, ref buffer, 50f, -140f, 0f, 5f);
        foreach ((int joint, float deg) in new[] { (7, 50f), (8, -140f), (9, 0f), (10, 5f), (11, 50f), (12, -140f), (13, 0f), (14, 5f) })
        {
            Assert.That(Quaternion.Angle(folded.bodyPose[joint - 1], Quaternion.AngleAxis(deg, Vector3.up)), Is.LessThan(0.01f), $"joint {joint}");
        }

        Assert.That(Quaternion.Angle(folded.bodyPose[18 - 1], Quaternion.AngleAxis(150f, Vector3.up)), Is.LessThan(0.01f));
        Quaternion[] buffer2 = null;
        AnimalSmalPose keepPaw = StreamingStereoVideoPlayer.ResolveAnimalLyingPose(pose, 1f, ref buffer2, 30f, -120f, 0f, float.NaN);
        Assert.That(Quaternion.Angle(keepPaw.bodyPose[10 - 1], original[10 - 1]), Is.LessThan(0.01f));
        Assert.That(Quaternion.Angle(keepPaw.bodyPose[14 - 1], original[14 - 1]), Is.LessThan(0.01f));
        Assert.That(Quaternion.Angle(keepPaw.bodyPose[8 - 1], Quaternion.AngleAxis(-120f, Vector3.up)), Is.LessThan(0.01f));
    }

    // ほどく重み: 区間の中は 1、終わりの後 ramp フレームで 1 → 0、始まりの前は 0。前足だけ別の時刻にできる設定は既定で表のまま（-1）。
    [Test]
    public void ReleaseWeightRampsAfterTheEndAndFrontDefaultsToTheTable()
    {
        Assert.That(StreamingStereoVideoPlayer.ResolveAnimalLyingReleaseWeight(0, 0, 122, 12), Is.EqualTo(1f));
        Assert.That(StreamingStereoVideoPlayer.ResolveAnimalLyingReleaseWeight(122, 0, 122, 12), Is.EqualTo(1f));
        Assert.That(StreamingStereoVideoPlayer.ResolveAnimalLyingReleaseWeight(128, 0, 122, 12), Is.EqualTo(0.5f).Within(1e-5f));
        Assert.That(StreamingStereoVideoPlayer.ResolveAnimalLyingReleaseWeight(134, 0, 122, 12), Is.EqualTo(0f));
        Assert.That(StreamingStereoVideoPlayer.ResolveAnimalLyingReleaseWeight(129, 0, 126, 6), Is.EqualTo(0.5f).Within(1e-5f));
        Assert.That(StreamingStereoVideoPlayer.ResolveAnimalLyingReleaseWeight(-1, 0, 122, 12), Is.EqualTo(0f));
        GameObject host = new GameObject("AnimalLyingPoseTests_release");
        try
        {
            StreamingStereoVideoPlayer player = host.AddComponent<StreamingStereoVideoPlayer>();
            Assert.That(player.animalLyingFrontReleaseEndFrame, Is.EqualTo(-1));
            Assert.That(player.animalLyingFrontReleaseFrames, Is.EqualTo(-1));
        }
        finally
        {
            Object.DestroyImmediate(host);
        }
    }

    // 前足をほどいている間: 終わりの後 ramp フレームと直後の 1 フレーム。ならしを飛ばす切り替えは既定 OFF。
    [Test]
    public void FrontReleaseWindowAndSkipSmoothingOffByDefault()
    {
        Assert.That(StreamingStereoVideoPlayer.IsAnimalLyingFrontReleasing(124, 124, 4), Is.False);
        Assert.That(StreamingStereoVideoPlayer.IsAnimalLyingFrontReleasing(125, 124, 4), Is.True);
        Assert.That(StreamingStereoVideoPlayer.IsAnimalLyingFrontReleasing(129, 124, 4), Is.True);
        Assert.That(StreamingStereoVideoPlayer.IsAnimalLyingFrontReleasing(130, 124, 4), Is.False);
        GameObject host = new GameObject("AnimalLyingPoseTests_skip");
        try
        {
            StreamingStereoVideoPlayer player = host.AddComponent<StreamingStereoVideoPlayer>();
            Assert.That(player.animalLyingFrontReleaseSkipsSmoothing, Is.False);
        }
        finally
        {
            Object.DestroyImmediate(host);
        }
    }

    // 前足だけ別の重み: 前足 4 関節は frontWeight、後ろ脚・尾は weight。重み 0 の側はデータのまま。
    [Test]
    public void FrontLegsTakeTheirOwnWeight()
    {
        AnimalSmalPose pose = Pose();
        Quaternion[] original = (Quaternion[])pose.bodyPose.Clone();
        Quaternion[] buffer = null;
        AnimalSmalPose a = StreamingStereoVideoPlayer.ResolveAnimalLyingPose(pose, 0f, 1f, ref buffer, 50f, -140f, 0f, 0f);
        Assert.That(Quaternion.Angle(a.bodyPose[8 - 1], Quaternion.AngleAxis(-140f, Vector3.up)), Is.LessThan(0.01f));
        Assert.That(Quaternion.Angle(a.bodyPose[18 - 1], original[18 - 1]), Is.LessThan(0.01f));
        Assert.That(Quaternion.Angle(a.bodyPose[25 - 1], original[25 - 1]), Is.LessThan(0.01f));
        Quaternion[] buffer2 = null;
        AnimalSmalPose b = StreamingStereoVideoPlayer.ResolveAnimalLyingPose(pose, 1f, 0f, ref buffer2, 50f, -140f, 0f, 0f);
        Assert.That(Quaternion.Angle(b.bodyPose[8 - 1], original[8 - 1]), Is.LessThan(0.01f));
        Assert.That(Quaternion.Angle(b.bodyPose[18 - 1], Quaternion.AngleAxis(150f, Vector3.up)), Is.LessThan(0.01f));
    }

    // 作り直した形（animalLyingPoseRemade、2026-10-28 夜）の既定: ON、ほどき方は今の伏せと同じ（前足も後ろ脚・尾・体を起こすのも f122 まで保ち 12 フレーム、
    // ならしはそのまま。ユーザー「３は今の伏せの方がいいじゃない？」）、重みは連続な時刻。
    [Test]
    public void RemadeIsOnByDefaultWithItsReleaseSettings()
    {
        GameObject host = new GameObject("AnimalLyingPoseTests_remade");
        try
        {
            StreamingStereoVideoPlayer player = host.AddComponent<StreamingStereoVideoPlayer>();
            Assert.That(player.animalLyingPoseRemade, Is.True);
            Assert.That(player.animalLyingRemadeFrontEndFrame, Is.EqualTo(122));
            Assert.That(player.animalLyingRemadeFrontRampFrames, Is.EqualTo(12));
            Assert.That(player.animalLyingRemadeBodyEndFrame, Is.EqualTo(122));
            Assert.That(player.animalLyingRemadeBodyRampFrames, Is.EqualTo(12));
            Assert.That(player.animalLyingRemadeFrontSkipsSmoothing, Is.False);
            Assert.That(player.animalLyingRemadeContinuousTime, Is.True);
        }
        finally
        {
            Object.DestroyImmediate(host);
        }
    }

    // 連続な時刻の重み: コマの間も進む（コマごとに 1/4 ずつ跳ばない）。ほどく間はならしを飛ばす区間。
    [Test]
    public void RemadeReleaseWeightIsContinuousInTime()
    {
        Assert.That(StreamingStereoVideoPlayer.ResolveAnimalLyingReleaseWeightAt(125.9f, 0, 126, 4), Is.EqualTo(1f));
        Assert.That(StreamingStereoVideoPlayer.ResolveAnimalLyingReleaseWeightAt(126.5f, 0, 126, 4), Is.EqualTo(0.875f).Within(1e-5f));
        Assert.That(StreamingStereoVideoPlayer.ResolveAnimalLyingReleaseWeightAt(127.25f, 0, 126, 4), Is.EqualTo(0.6875f).Within(1e-5f));
        Assert.That(StreamingStereoVideoPlayer.ResolveAnimalLyingReleaseWeightAt(130f, 0, 126, 4), Is.EqualTo(0f));
        Assert.That(StreamingStereoVideoPlayer.ResolveAnimalLyingReleaseWeightAt(-0.5f, 0, 126, 4), Is.EqualTo(0f));
        Assert.That(StreamingStereoVideoPlayer.IsAnimalLyingFrontReleasingAt(126f, 126, 4), Is.False);
        Assert.That(StreamingStereoVideoPlayer.IsAnimalLyingFrontReleasingAt(126.01f, 126, 4), Is.True);
        Assert.That(StreamingStereoVideoPlayer.IsAnimalLyingFrontReleasingAt(131f, 126, 4), Is.True);
        Assert.That(StreamingStereoVideoPlayer.IsAnimalLyingFrontReleasingAt(131.01f, 126, 4), Is.False);
    }

    private static Quaternion[] Accumulate(Quaternion[] bodyPose)
    {
        var a = new Quaternion[35];
        a[0] = Quaternion.identity;
        for (int j = 1; j < 35; j++)
        {
            a[j] = a[AnimalPoseApplier.SmalJointParent(j)] * bodyPose[j - 1];
        }

        return a;
    }

    // 重み 1: 脚の区間は決めた向き（体の座標、右は y を反転）。前脚は胸がどう曲がっていても同じ向き。足先は局所の +Y まわり、
    // 背骨・首・頭・尾・口・耳はデータのまま、元の配列は書き換えない。
    [Test]
    public void RemadeLegsPointTheDesignedDirections()
    {
        AnimalSmalPose pose = Pose();
        Quaternion[] original = (Quaternion[])pose.bodyPose.Clone();
        Quaternion[] buffer = null;
        AnimalSmalPose lying = StreamingStereoVideoPlayer.ResolveAnimalLyingRemadePose(pose, 1f, 1f, ref buffer);
        Assert.That(lying.bodyPose, Is.SameAs(buffer));
        Quaternion[] a = Accumulate(lying.bodyPose);
        for (int leg = 0; leg < 4; leg++)
        {
            int first = StreamingStereoVideoPlayer.AnimalLyingRemadeLegFirstJoints[leg];
            for (int k = 0; k < 3; k++)
            {
                int j = first + k;
                Vector3 want = StreamingStereoVideoPlayer.AnimalLyingRemadeLegDirs[(leg < 2 ? 0 : 3) + k];
                if (leg % 2 == 1)
                {
                    want.y = -want.y;
                }

                Vector3 got = a[j] * AnimalPoseApplier.SmalNativeRestSegment(j, j + 1);
                Assert.That(Vector3.Angle(got, want), Is.LessThan(0.05f), $"segment {j}");
            }
        }

        foreach ((int joint, float deg) in new[] { (10, 70f), (14, 70f), (20, 80f), (24, 80f) })
        {
            Assert.That(Quaternion.Angle(lying.bodyPose[joint - 1], Quaternion.AngleAxis(deg, Vector3.up)), Is.LessThan(0.01f), $"leaf {joint}");
        }

        foreach (int j in new[] { 1, 2, 3, 4, 5, 6, 15, 16, 25, 28, 31, 32, 33, 34 })
        {
            Assert.That(Quaternion.Angle(lying.bodyPose[j - 1], original[j - 1]), Is.LessThan(0.01f), $"joint {j}");
        }

        for (int i = 0; i < 34; i++)
        {
            Assert.That(Quaternion.Angle(pose.bodyPose[i], original[i]), Is.LessThan(0.0001f), $"input {i}");
        }
    }

    // 前脚と後ろ脚は別の重み。重み 0 の側はデータのまま、両方 0 なら何もしない。
    [Test]
    public void RemadeFrontAndHindTakeTheirOwnWeights()
    {
        AnimalSmalPose pose = Pose();
        Quaternion[] original = (Quaternion[])pose.bodyPose.Clone();
        Quaternion[] buffer = null;
        AnimalSmalPose front = StreamingStereoVideoPlayer.ResolveAnimalLyingRemadePose(pose, 1f, 0f, ref buffer);
        Assert.That(Quaternion.Angle(front.bodyPose[18 - 1], original[18 - 1]), Is.LessThan(0.01f));
        Assert.That(Quaternion.Angle(front.bodyPose[8 - 1], original[8 - 1]), Is.GreaterThan(1f));
        Quaternion[] buffer2 = null;
        AnimalSmalPose hind = StreamingStereoVideoPlayer.ResolveAnimalLyingRemadePose(pose, 0f, 1f, ref buffer2);
        Assert.That(Quaternion.Angle(hind.bodyPose[8 - 1], original[8 - 1]), Is.LessThan(0.01f));
        Assert.That(Quaternion.Angle(hind.bodyPose[18 - 1], original[18 - 1]), Is.GreaterThan(1f));
        Quaternion[] buffer3 = null;
        AnimalSmalPose none = StreamingStereoVideoPlayer.ResolveAnimalLyingRemadePose(pose, 0f, 0f, ref buffer3);
        Assert.That(none.bodyPose, Is.SameAs(pose.bodyPose));
    }

    // 尾の局所の回転（定数）: 根から積むと尾の区間が決めた向き、尾の先は単位。
    [Test]
    public void RemadeTailLocalsPointTheDesignedDirections()
    {
        Quaternion[] local = StreamingStereoVideoPlayer.ResolveAnimalLyingRemadeTailLocal();
        Assert.That(local.Length, Is.EqualTo(7));
        Quaternion accum = Quaternion.identity;
        for (int k = 0; k < 6; k++)
        {
            accum = accum * local[k];
            Vector3 got = accum * AnimalPoseApplier.SmalNativeRestSegment(25 + k, 26 + k);
            Assert.That(Vector3.Angle(got, StreamingStereoVideoPlayer.AnimalLyingRemadeTailDirs[k]), Is.LessThan(0.05f), $"tail segment {25 + k}");
        }

        Assert.That(Quaternion.Angle(local[6], Quaternion.identity), Is.LessThan(0.01f));
    }

    // V が効かない track は shot の先頭の測り直しだけ待つ。測り直しが無い設定なら待たない。
    [Test]
    public void WithoutTheFollowOnlyTheRefineIsAwaited()
    {
        Assert.That(StreamingStereoVideoPlayer.IsAnimalLyingSizeSettled(true, false, false, false, false, false, false, float.NaN), Is.False);
        Assert.That(StreamingStereoVideoPlayer.IsAnimalLyingSizeSettled(true, true, false, false, false, false, false, float.NaN), Is.True);
        Assert.That(StreamingStereoVideoPlayer.IsAnimalLyingSizeSettled(false, false, false, false, false, false, false, float.NaN), Is.True);
        Assert.That(StreamingStereoVideoPlayer.IsAnimalLyingSizeSettled(true, true, true, true, true, true, true, float.PositiveInfinity), Is.False);
    }
}
