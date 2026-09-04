using UnityEngine;

// 手動回転（yaw / pitch / roll）と姿勢の相互変換。
//
// **分解と合成は必ず互いの逆でなければならない。**
// 掴んで回す操作は毎フレーム「いまの姿勢を 3 数に分解して保存 → 次のフレームで
// その 3 数から姿勢を組み立てる」を繰り返す。片道でもずれると、そのずれが
// 掴んでいる間ずっと積み上がって暴れる（2026-09-04 実機: 1 回の掴みで
// roll が 40 → 103 → 106 と振れていた）。
//
// 以前は合成側が
//   AngleAxis(yaw, up) * base * AngleAxis(pitch, right) * AngleAxis(roll, forward)
// で base を挟んでおり、1 つの向きとしての意味を持たなかった。分解側は
// Quaternion.Euler(pitch, yaw, roll) を前提にしていたので噛み合っていなかった。
//
// TrackKeyframeCurve と同じ理由でここに切り出してある。MonoBehaviour の中に
// 置いたままだと EditMode テストから触れない。
public static class ManualRotationMath
{
    // 3 数から「配置回転に重ねる world 空間のずらし」を作る。
    //
    // yaw だけのときは AngleAxis(yaw, Vector3.up) に等しい。スクリーンは
    // ヨーのみの基準で置かれている（screen.up は常に Vector3.up）ので、
    // これまで保存した yaw はそのままの意味で効く。
    public static Quaternion ComposeOffset(float yawDeg, float pitchDeg, float rollDeg)
    {
        return Quaternion.Euler(pitchDeg, yawDeg, rollDeg);
    }


    // ComposeOffset の逆。返る角度は -180..180。
    public static void DecomposeOffset(Quaternion offset, out float yawDeg, out float pitchDeg, out float rollDeg)
    {
        Vector3 euler = offset.eulerAngles;
        yawDeg = Mathf.DeltaAngle(0f, euler.y);
        pitchDeg = Mathf.DeltaAngle(0f, euler.x);
        rollDeg = Mathf.DeltaAngle(0f, euler.z);
    }


    public static Quaternion Apply(Quaternion baseRotation, float yawDeg, float pitchDeg, float rollDeg)
    {
        return ComposeOffset(yawDeg, pitchDeg, rollDeg) * baseRotation;
    }
}
