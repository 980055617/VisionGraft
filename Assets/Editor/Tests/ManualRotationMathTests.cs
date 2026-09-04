using NUnit.Framework;
using UnityEngine;

// 手動回転の分解と合成が互いの逆であることを固定する。
//
// ここが崩れると、掴んで回している間にずれが積み上がってモデルが暴れる。
// 実機で起きたときは 1 回の掴みで roll が 40 → 103 → 106 と振れていた（2026-09-04）。
public class ManualRotationMathTests
{
    private static void AssertSameRotation(
        Quaternion actual, Quaternion expected, string message, float toleranceDeg = 0.05f)
    {
        // クォータニオンは q と -q が同じ回転なので、角度差で比べる。
        float angle = Quaternion.Angle(actual, expected);
        Assert.That(angle, Is.LessThan(toleranceDeg), $"{message}（角度差 {angle:F3} 度）");
    }


    [Test]
    public void ComposeAndDecomposeAreInverses()
    {
        var cases = new[]
        {
            new Vector3(0f, 0f, 0f),
            new Vector3(30f, 0f, 0f),
            new Vector3(0f, 25f, 0f),
            new Vector3(0f, 0f, -40f),
            new Vector3(120f, -35f, 75f),
            new Vector3(-170f, 20f, 160f),
        };

        foreach (Vector3 c in cases)
        {
            Quaternion offset = ManualRotationMath.ComposeOffset(c.x, c.y, c.z);
            ManualRotationMath.DecomposeOffset(offset, out float yaw, out float pitch, out float roll);
            Quaternion again = ManualRotationMath.ComposeOffset(yaw, pitch, roll);

            AssertSameRotation(again, offset, $"yaw={c.x} pitch={c.y} roll={c.z} の往復");
        }
    }


    // 掴んで回す操作そのものの不変条件。
    //
    // world の差分 D を掛けて保存し、その 3 数で組み立て直したとき、
    // 対象は元の姿勢からちょうど D だけ回っていなければならない。
    // 以前の合成（base を挟む式）ではこれが成り立っていなかった。
    [Test]
    public void GrabDeltaSurvivesTheRoundTrip()
    {
        Quaternion baseRotation = Quaternion.Euler(11f, 47f, -23f);
        float startYaw = 15f;
        float startPitch = -8f;
        float startRoll = 4f;

        var deltas = new[]
        {
            Quaternion.AngleAxis(20f, Vector3.up),
            Quaternion.AngleAxis(-35f, Vector3.right),
            Quaternion.AngleAxis(50f, new Vector3(0.3f, 0.5f, 0.8f).normalized),
            Quaternion.AngleAxis(140f, new Vector3(-0.6f, 0.2f, 0.77f).normalized),
        };

        foreach (Quaternion delta in deltas)
        {
            Quaternion before = ManualRotationMath.Apply(baseRotation, startYaw, startPitch, startRoll);

            // 掴んで回す側がやっていること
            Quaternion applied = delta * ManualRotationMath.ComposeOffset(startYaw, startPitch, startRoll);
            ManualRotationMath.DecomposeOffset(applied, out float yaw, out float pitch, out float roll);

            // 配置に重ねたときの結果
            Quaternion after = ManualRotationMath.Apply(baseRotation, yaw, pitch, roll);

            AssertSameRotation(after, delta * before, "掴んだぶんだけ回っていること");
        }
    }


    // 何度も掴み直しても積み上がらないこと。ずれがあると回数に比例して開く。
    [Test]
    public void RepeatedGrabsDoNotDrift()
    {
        Quaternion baseRotation = Quaternion.Euler(0f, 30f, 0f);
        float yaw = 0f;
        float pitch = 0f;
        float roll = 0f;

        Quaternion step = Quaternion.AngleAxis(9f, new Vector3(0.2f, 0.9f, 0.3f).normalized);
        Quaternion expected = ManualRotationMath.Apply(baseRotation, yaw, pitch, roll);

        for (int i = 0; i < 40; i++)
        {
            Quaternion applied = step * ManualRotationMath.ComposeOffset(yaw, pitch, roll);
            ManualRotationMath.DecomposeOffset(applied, out yaw, out pitch, out roll);
            expected = step * expected;
        }

        // **許容は float の丸めぶんだけ。**
        // 1 往復ごとに quaternion → euler → quaternion を通るので、float32 では
        // 1 回あたり 0.005 度ほど落ちる。40 回で 0.2 度前後は避けられない。
        // 一方、分解と合成が逆でない場合のずれは**桁が違う**（合成側が base を
        // 挟んでいた実装では 1 回の掴みで数十度振れていた）ので、
        // 1 度に置いても構造的な誤りは確実に捕まえられる。
        AssertSameRotation(
            ManualRotationMath.Apply(baseRotation, yaw, pitch, roll), expected,
            "40 回繰り返しても開かないこと", 1f);
    }


    // yaw だけのときは、これまでの式（world の上を軸に配置回転の前へ掛ける）と
    // 同じでなければならない。**保存済みの yaw の意味を変えないための固定。**
    [Test]
    public void YawOnlyMatchesTheHistoricalFormula()
    {
        Quaternion baseRotation = Quaternion.Euler(0f, 62f, 0f);
        foreach (float yaw in new[] { -150f, -30f, 0f, 45f, 175f })
        {
            Quaternion legacy = Quaternion.AngleAxis(yaw, Vector3.up) * baseRotation;
            Quaternion now = ManualRotationMath.Apply(baseRotation, yaw, 0f, 0f);
            AssertSameRotation(now, legacy, $"yaw={yaw} が従来と同じであること");
        }
    }
}
