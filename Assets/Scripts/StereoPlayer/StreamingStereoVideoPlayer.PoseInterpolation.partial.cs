using UnityEngine;

// 30 fps の姿勢を、表示の tick（実機 72 Hz）の時刻で隣り合う 2 フレームから補間する（2026-10-04、既定 OFF）。
//
// 調査役 TM の案 A を反論役 C-TM の修正つきで試作したもの（Docs/smpl-retargeting.md の 2026-10-04 の節）。
// 今は各 tick でその動画フレームの姿勢をそのまま当てる（ゼロ次ホールド）ので、72 Hz の tick の 58% で姿勢が止まり、
// フレームの境目の 1 tick でまとめて動く（E の蹴りで左つま先が最大 58 mm、視角 3.8°）。
//   時刻: vp.clockTime（VideoPlayer が従う時計。バッチの実測で ct×30 − vf ∈ [0,1) が 100%、Δct = Δunscaled）。
//         vp.time は texture にあるフレームの時刻で端数を持たないので使えない。
//   τ = ct × fps − 0.5（中央揃え。画面のフレームとの時刻のずれは平均 0）。fa = floor(τ)、fb = fa + 1、w = τ − fa。
//   ホールド（今と同じ）: 停止中、ct と vf が食い違う（ct×fps − vf が [−0.02, 1.02) の外）、shot 境界をまたぐ、データが無い。
// 補間するのは FK に入れる回転（SMPL / SMAL）だけ。FK ループの書き込み（ApplyWorldRotation）はそのまま。配置（bbox・anchor・⑦⑧）は補間しない。
// Human は centeredSmplRotationFilter（E）と組のときだけ効く（既定の Slerp EMA は動画フレーム刻みで、フレームの中では進まないため）。
// 効果は実機でしか見えない（-captureFrames は動画を止めて撮るので補間も止まる）。バッチで測れるのは骨の書き出しの tick ごとの動き。
public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    private int poseInterpTicks;
    private int poseInterpApplied;
    private int poseInterpHoldPaused;
    private int poseInterpHoldClock;
    private int poseInterpHoldShot;
    private int poseInterpHoldData;
    private float poseInterpLoggedAt = -1f;
    private int poseInterpLastTick = -1;

    private bool TryResolvePoseInterpolation(int frame, out int fa, out int fb, out float w)
    {
        fa = frame;
        fb = frame;
        w = 0f;
        if (!interpolatePoseBetweenFrames || vp == null)
        {
            return false;
        }

        // 同じ tick の 2 回目以降（人と動物で 2 回呼ばれる）は数えない。
        bool firstCallThisTick = poseInterpLastTick != Time.frameCount;
        if (firstCallThisTick)
        {
            poseInterpLastTick = Time.frameCount;
            poseInterpTicks++;
            LogPoseInterpolationIfDue();
        }

        if (!vp.isPlaying)
        {
            if (firstCallThisTick) poseInterpHoldPaused++;
            return false;
        }

        double fps = vp.frameRate > 0f ? vp.frameRate : 30.0;
        double clockFrames = vp.clockTime * fps;
        double frac = clockFrames - frame;
        if (frac < -0.02 || frac >= 1.02)
        {
            if (firstCallThisTick) poseInterpHoldClock++;
            return false;
        }

        double tau = clockFrames - 0.5;
        int a = (int)System.Math.Floor(tau);
        int b = a + 1;
        if (a < 0 || b >= (int)metaHeader.numFrames ||
            shotBoundaries.ResolveShotIndex(a) != shotBoundaries.ResolveShotIndex(b))
        {
            if (firstCallThisTick) poseInterpHoldShot++;
            return false;
        }

        fa = a;
        fb = b;
        w = Mathf.Clamp01((float)(tau - a));
        if (firstCallThisTick) poseInterpApplied++;
        return true;
    }

    // E（中心 5tap）を fa と fb のそれぞれに掛けてから、回転を Slerp する。どちらかが無ければ current のまま。
    private HumanSmplPose InterpolateCenteredSmplPose(uint trackId, int fa, int fb, float w, HumanSmplPose current)
    {
        if (!TryGetHumanSmplPoseLoaded(fa, trackId, out HumanSmplPose pa) ||
            !TryGetHumanSmplPoseLoaded(fb, trackId, out HumanSmplPose pb) ||
            !pa.hasGlobalOrient || !pb.hasGlobalOrient ||
            pa.bodyPose == null || pb.bodyPose == null || pa.bodyPose.Length != pb.bodyPose.Length)
        {
            poseInterpHoldData++;
            return current;
        }

        pa = BuildCenteredSmplPose(fa, trackId, pa);
        pb = BuildCenteredSmplPose(fb, trackId, pb);
        HumanSmplPose result = current;
        result.globalOrient = SlerpNear(pa.globalOrient, pb.globalOrient, w);
        result.bodyPose = new Quaternion[pa.bodyPose.Length];
        for (int i = 0; i < pa.bodyPose.Length; i++)
        {
            result.bodyPose[i] = SlerpNear(pa.bodyPose[i], pb.bodyPose[i], w);
        }

        return result;
    }

    private AnimalSmalPose InterpolateSmalPose(uint trackId, int fa, int fb, float w, AnimalSmalPose current)
    {
        if (!TryGetAnimalSmalPoseLoaded(fa, trackId, out AnimalSmalPose pa) ||
            !TryGetAnimalSmalPoseLoaded(fb, trackId, out AnimalSmalPose pb) ||
            !pa.hasGlobalOrient || !pb.hasGlobalOrient ||
            pa.bodyPose == null || pb.bodyPose == null || pa.bodyPose.Length != pb.bodyPose.Length)
        {
            poseInterpHoldData++;
            return current;
        }

        if (centeredSmalRootFilter)
        {
            pa.globalOrient = BuildCenteredSmalGlobalOrient(fa, trackId, pa.globalOrient);
            pb.globalOrient = BuildCenteredSmalGlobalOrient(fb, trackId, pb.globalOrient);
        }

        if (centeredSmalBodyPoseFilter)
        {
            pa = BuildCenteredSmalBodyPose(fa, trackId, pa);
            pb = BuildCenteredSmalBodyPose(fb, trackId, pb);
        }

        AnimalSmalPose result = current;
        result.globalOrient = SlerpNear(pa.globalOrient, pb.globalOrient, w);
        result.bodyPose = new Quaternion[pa.bodyPose.Length];
        for (int i = 0; i < pa.bodyPose.Length; i++)
        {
            result.bodyPose[i] = SlerpNear(pa.bodyPose[i], pb.bodyPose[i], w);
        }

        return result;
    }

    // SMAL の根（globalOrient）の中心 5tap [1,4,6,4,1]/16（半径 1 なら [1,2,1]/4）。窓は shot 境界と track の途切れをまたがない（BuildCenteredSmplPose と同じ）。
    private Quaternion BuildCenteredSmalGlobalOrient(int frame, uint trackId, Quaternion center)
    {
        if (!IsFinite(center))
        {
            return center;
        }

        int shot = shotBoundaries.ResolveShotIndex(frame);
        var q = new Quaternion[5];
        var has = new bool[5];
        q[2] = center;
        has[2] = true;
        for (int k = -2; k <= 2; k++)
        {
            int f = frame + k;
            if (k == 0 || f < 0 || f >= (int)metaHeader.numFrames || shotBoundaries.ResolveShotIndex(f) != shot)
            {
                continue;
            }

            if (TryGetAnimalSmalPoseLoaded(f, trackId, out AnimalSmalPose p) && p.hasGlobalOrient && IsFinite(p.globalOrient))
            {
                q[k + 2] = p.globalOrient;
                has[k + 2] = true;
            }
        }

        float[] weights = ResolveCenteredTaps(has);
        return weights == null ? center : WeightedQuaternionMean(q, weights, center);
    }

    // SMAL の body_pose（関節 1〜34）の中心 5tap（centeredSmalBodyPoseFilter、2026-10-04 第 3 ラウンド）。窓の規則は
    // BuildCenteredSmalGlobalOrient と同じ（shot 境界と track の途切れをまたがない、前後 2 フレームが揃わなければ 3tap、それも無ければそのまま）。
    // **新しい配列を返す**（TryGetAnimalSmalPose の配列はキャッシュそのものなので書き換えない）。
    private AnimalSmalPose BuildCenteredSmalBodyPose(int frame, uint trackId, AnimalSmalPose pose)
    {
        if (pose.bodyPose == null)
        {
            return pose;
        }

        int n = pose.bodyPose.Length;
        int shot = shotBoundaries.ResolveShotIndex(frame);
        var window = new Quaternion[5][];
        var has = new bool[5];
        window[2] = pose.bodyPose;
        has[2] = true;
        for (int k = -2; k <= 2; k++)
        {
            int f = frame + k;
            if (k == 0 || f < 0 || f >= (int)metaHeader.numFrames || shotBoundaries.ResolveShotIndex(f) != shot)
            {
                continue;
            }

            if (TryGetAnimalSmalPoseLoaded(f, trackId, out AnimalSmalPose p) && p.bodyPose != null && p.bodyPose.Length == n)
            {
                window[k + 2] = p.bodyPose;
                has[k + 2] = true;
            }
        }

        float[] weights = ResolveCenteredTaps(has);
        if (weights == null)
        {
            return pose;
        }

        var result = new Quaternion[n];
        var q = new Quaternion[5];
        for (int i = 0; i < n; i++)
        {
            for (int k = 0; k < 5; k++)
            {
                q[k] = has[k] ? window[k][i] : Quaternion.identity;
            }

            result[i] = IsFinite(pose.bodyPose[i]) ? WeightedQuaternionMean(q, weights, pose.bodyPose[i]) : pose.bodyPose[i];
        }

        pose.bodyPose = result;
        return pose;
    }

    private static readonly float[] CenteredTaps5 = { 1f, 4f, 6f, 4f, 1f };
    private static readonly float[] CenteredTaps3 = { 0f, 1f, 2f, 1f, 0f };

    private static float[] ResolveCenteredTaps(bool[] has)
    {
        if (has[0] && has[1] && has[3] && has[4]) return CenteredTaps5;
        if (has[1] && has[3]) return CenteredTaps3;
        return null;
    }

    // 重みつきの四元数の平均（近回りに符号を揃えてから足して正規化）。窓の幅（±2 フレーム）では回転の差が小さいので十分な近似。
    private static Quaternion WeightedQuaternionMean(Quaternion[] q, float[] weights, Quaternion center)
    {
        float x = 0f, y = 0f, z = 0f, wq = 0f;
        for (int k = 0; k < 5; k++)
        {
            if (weights[k] <= 0f) continue;
            float sign = Quaternion.Dot(q[k], center) < 0f ? -1f : 1f;
            x += weights[k] * sign * q[k].x;
            y += weights[k] * sign * q[k].y;
            z += weights[k] * sign * q[k].z;
            wq += weights[k] * sign * q[k].w;
        }

        float mag = Mathf.Sqrt(x * x + y * y + z * z + wq * wq);
        return mag < 1e-6f ? center : new Quaternion(x / mag, y / mag, z / mag, wq / mag);
    }

    // 隣り合うフレームの回転を近回りで補間する（行列から読んだ四元数の符号はフレームごとに任意なので、内積が負なら片方の符号を反す）。
    private static Quaternion SlerpNear(Quaternion a, Quaternion b, float t)
    {
        if (Quaternion.Dot(a, b) < 0f)
        {
            b = new Quaternion(-b.x, -b.y, -b.z, -b.w);
        }

        return Quaternion.Slerp(a, b, t);
    }

    // 前後のフレームはふつう RepairIsolatedBBoxSpikes が先読み済み。無ければ memo から読む（BuildCenteredSmplPose と同じ）。
    private bool TryGetHumanSmplPoseLoaded(int frame, uint trackId, out HumanSmplPose pose)
    {
        return TryGetHumanSmplPose(frame, trackId, out pose) ||
               (TryReadFrameObjectsMemo(frame, out _) && TryGetHumanSmplPose(frame, trackId, out pose));
    }

    private bool TryGetAnimalSmalPoseLoaded(int frame, uint trackId, out AnimalSmalPose pose)
    {
        return TryGetAnimalSmalPose(frame, trackId, out pose) ||
               (TryReadFrameObjectsMemo(frame, out _) && TryGetAnimalSmalPose(frame, trackId, out pose));
    }

    // 2 秒に 1 回、補間した tick とホールドの理由を出す（実機で時計の追従を確かめるため）。
    private void LogPoseInterpolationIfDue()
    {
        float now = Time.unscaledTime;
        if (poseInterpLoggedAt < 0f)
        {
            poseInterpLoggedAt = now;
            return;
        }

        if (now - poseInterpLoggedAt < 2f)
        {
            return;
        }

        poseInterpLoggedAt = now;
        Debug.Log($"[INTERP] ticks={poseInterpTicks} interpolated={poseInterpApplied} hold: paused={poseInterpHoldPaused} " +
                  $"clock={poseInterpHoldClock} shot={poseInterpHoldShot} data={poseInterpHoldData}");
        poseInterpTicks = 0;
        poseInterpApplied = 0;
        poseInterpHoldPaused = 0;
        poseInterpHoldClock = 0;
        poseInterpHoldShot = 0;
        poseInterpHoldData = 0;
    }
}
