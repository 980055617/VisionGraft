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
            return true;
        }

        return device.TryGetFeatureValue(CommonUsages.devicePosition, out position) &&
               device.TryGetFeatureValue(CommonUsages.deviceRotation, out rotation);
    }
}
