using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;
using XRInputDevice = UnityEngine.XR.InputDevice;
using XRInputDevices = UnityEngine.XR.InputDevices;

// コントローラの指す向きとトリガー、および HMD のローカル姿勢を XR 入力から読む。
// 姿勢の world 変換は RuntimeXrRayPick が行う（こちらは Unity XR API の呼び出しだけ）。
public static class RuntimeXrRayPickReader
{
    // OpenXR の aim pose。コントローラを「指し棒」として扱ったときの向きで、
    // 握り位置の devicePosition/deviceRotation とは別物（実機で数十度ずれる）。
    // ランタイムが公開していない場合だけ device 側にフォールバックする。
    private static readonly InputFeatureUsage<Vector3> PointerPosition =
        new InputFeatureUsage<Vector3>("PointerPosition");
    private static readonly InputFeatureUsage<Quaternion> PointerRotation =
        new InputFeatureUsage<Quaternion>("PointerRotation");

    public static bool TryReadHeadPose(List<XRInputDevice> devices, out Vector3 position, out Quaternion rotation)
    {
        position = Vector3.zero;
        rotation = Quaternion.identity;
        if (devices == null)
        {
            return false;
        }

        devices.Clear();
        XRInputDevices.GetDevicesWithCharacteristics(InputDeviceCharacteristics.HeadMounted, devices);
        for (int i = 0; i < devices.Count; i++)
        {
            XRInputDevice device = devices[i];
            if (!device.isValid)
            {
                continue;
            }

            if (device.TryGetFeatureValue(CommonUsages.centerEyePosition, out position) &&
                device.TryGetFeatureValue(CommonUsages.centerEyeRotation, out rotation))
            {
                return true;
            }

            if (device.TryGetFeatureValue(CommonUsages.devicePosition, out position) &&
                device.TryGetFeatureValue(CommonUsages.deviceRotation, out rotation))
            {
                return true;
            }
        }

        return false;
    }

    // どちらの手か。0 = 不明、1 = 左、2 = 右。掴んだ手を固定するためと、perf の移動量を
    // 手の切り替えで水増ししないために使う（2026-09-30 の 5 回目の監査）。
    public const int HandUnknown = 0;
    public const int HandLeft = 1;
    public const int HandRight = 2;

    public static int ResolveHandCode(XRInputDevice device)
    {
        if ((device.characteristics & InputDeviceCharacteristics.Left) != 0)
        {
            return HandLeft;
        }

        return (device.characteristics & InputDeviceCharacteristics.Right) != 0 ? HandRight : HandUnknown;
    }

    // preferredHand を指定すると、その手が有効な間はその手だけを返す。
    // **掴んで回している最中に使う。**指定しないと「トリガーを引いている方」を返す実装なので、
    // 掴んだまま反対の手のトリガーを引くと基準の姿勢が入れ替わり、その 1 フレームで
    // モデルが両手の姿勢差ぶん飛ぶ（2026-09-30 の 5 回目の監査）。
    public static bool TryReadPointerPose(
        List<XRInputDevice> devices,
        int preferredHand,
        out Vector3 position,
        out Quaternion rotation,
        out bool triggerPressed,
        out int handCode)
    {
        position = Vector3.zero;
        rotation = Quaternion.identity;
        triggerPressed = false;
        handCode = HandUnknown;
        if (devices == null)
        {
            return false;
        }

        devices.Clear();
        XRInputDevices.GetDevicesWithCharacteristics(
            InputDeviceCharacteristics.Controller | InputDeviceCharacteristics.HeldInHand,
            devices);

        bool hasAny = false;
        for (int i = 0; i < devices.Count; i++)
        {
            XRInputDevice device = devices[i];
            if (!device.isValid)
            {
                continue;
            }

            int hand = ResolveHandCode(device);
            if (preferredHand != HandUnknown && hand != preferredHand)
            {
                continue;
            }

            if (!TryReadAimPose(device, out Vector3 devicePosition, out Quaternion deviceRotation))
            {
                continue;
            }

            device.TryGetFeatureValue(CommonUsages.triggerButton, out bool pressed);
            if (preferredHand != HandUnknown)
            {
                // 指定された手が見つかった。押していてもいなくてもこれを返す。
                position = devicePosition;
                rotation = deviceRotation;
                triggerPressed = pressed;
                handCode = hand;
                return true;
            }

            if (pressed)
            {
                position = devicePosition;
                rotation = deviceRotation;
                triggerPressed = true;
                handCode = hand;
                return true;
            }

            if (!hasAny)
            {
                position = devicePosition;
                rotation = deviceRotation;
                handCode = hand;
                hasAny = true;
            }
        }

        return hasAny;
    }

    // トリガーを引いているコントローラを優先して返す。両手とも引いていなければ
    // 最初の有効なコントローラの姿勢を返す（pressed = false）。押下エッジの判定は
    // RuntimeXrRayPick.ResolvePress 側で行う。
    public static bool TryReadPointerPose(
        List<XRInputDevice> devices,
        out Vector3 position,
        out Quaternion rotation,
        out bool triggerPressed)
    {
        position = Vector3.zero;
        rotation = Quaternion.identity;
        triggerPressed = false;
        if (devices == null)
        {
            return false;
        }

        devices.Clear();
        XRInputDevices.GetDevicesWithCharacteristics(
            InputDeviceCharacteristics.Controller | InputDeviceCharacteristics.HeldInHand,
            devices);

        bool hasAny = false;
        for (int i = 0; i < devices.Count; i++)
        {
            XRInputDevice device = devices[i];
            if (!device.isValid)
            {
                continue;
            }

            if (!TryReadAimPose(device, out Vector3 devicePosition, out Quaternion deviceRotation))
            {
                continue;
            }

            device.TryGetFeatureValue(CommonUsages.triggerButton, out bool pressed);
            if (pressed)
            {
                position = devicePosition;
                rotation = deviceRotation;
                triggerPressed = true;
                return true;
            }

            if (!hasAny)
            {
                position = devicePosition;
                rotation = deviceRotation;
                hasAny = true;
            }
        }

        return hasAny;
    }

    private static bool TryReadAimPose(XRInputDevice device, out Vector3 position, out Quaternion rotation)
    {
        // 短絡評価で PointerPosition が取れなかった場合 rotation が未代入のまま return に届くため、
        // 先に既定値を入れておく（呼び出し側は戻り値 false のとき中身を見ない）。
        position = Vector3.zero;
        rotation = Quaternion.identity;

        if (device.TryGetFeatureValue(PointerPosition, out position) &&
            device.TryGetFeatureValue(PointerRotation, out rotation))
        {
            LogPoseSourceOnce("aim");
            return true;
        }

        bool grip = device.TryGetFeatureValue(CommonUsages.devicePosition, out position) &&
                    device.TryGetFeatureValue(CommonUsages.deviceRotation, out rotation);
        if (grip)
        {
            // **握り位置は指し棒の向きと数十度ずれる。**ここへ落ちていると、
            // 自前のレイだけが ISDK のレイと違う始点・違う向きで出る
            // （2026-09-07 実機で「レイ 2 本・手元が違う始点」の報告）。
            LogPoseSourceOnce("grip(フォールバック)");
        }

        return grip;
    }

    // どちらの姿勢を読んでいるかは実機でしか分からないので、変わったときだけ 1 行出す。
    private static string loggedPoseSource;

    private static void LogPoseSourceOnce(string source)
    {
        if (loggedPoseSource == source)
        {
            return;
        }

        loggedPoseSource = source;
        Debug.Log($"[RAY] コントローラ姿勢の出どころ={source}");
    }
}
