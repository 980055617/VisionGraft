using System.Collections.Generic;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif
using XRInputDevice = UnityEngine.XR.InputDevice;
using XRInputDevices = UnityEngine.XR.InputDevices;

public static class RuntimePauseInputReader
{
    public static bool ResolveHotkeyPressed(bool inputSystemPressed, bool legacyInputPressed)
    {
        return inputSystemPressed || legacyInputPressed;
    }

    public static bool IsPauseHotkeyPressed()
    {
        bool inputSystemPressed = false;
#if ENABLE_INPUT_SYSTEM
        Keyboard keyboard = Keyboard.current;
        inputSystemPressed =
            keyboard != null &&
            ((keyboard.spaceKey != null && keyboard.spaceKey.wasPressedThisFrame) ||
             (keyboard.pKey != null && keyboard.pKey.wasPressedThisFrame));
#endif

        bool legacyInputPressed = false;
#if ENABLE_LEGACY_INPUT_MANAGER
        legacyInputPressed = Input.GetKeyDown(KeyCode.Space) || Input.GetKeyDown(KeyCode.P);
#endif

        return ResolveHotkeyPressed(inputSystemPressed, legacyInputPressed);
    }

    public static bool TryReadPrimaryButtonPressed(List<XRInputDevice> devices, out bool pressed)
    {
        pressed = false;
        if (devices == null)
        {
            return false;
        }

        devices.Clear();
        XRInputDevices.GetDevices(devices);
        bool hasAny = false;
        for (int i = 0; i < devices.Count; i++)
        {
            XRInputDevice device = devices[i];
            if (!device.isValid)
            {
                continue;
            }

            if (device.TryGetFeatureValue(UnityEngine.XR.CommonUsages.primaryButton, out bool value))
            {
                hasAny = true;
                pressed |= value;
            }
        }

        return hasAny;
    }
}
