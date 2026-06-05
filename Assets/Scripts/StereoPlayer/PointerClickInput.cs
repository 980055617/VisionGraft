using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

public static class PointerClickInput
{
    public static bool ResolveClickPosition(bool pressedThisFrame, Vector2 position, out Vector2 clickPosition)
    {
        clickPosition = Vector2.zero;
        if (!pressedThisFrame)
        {
            return false;
        }

        clickPosition = position;
        return true;
    }

    public static bool TryReadPrimaryClickPosition(out Vector2 clickPosition)
    {
        clickPosition = Vector2.zero;
#if ENABLE_INPUT_SYSTEM
        Mouse mouse = Mouse.current;
        if (mouse == null)
        {
            return false;
        }

        return ResolveClickPosition(mouse.leftButton.wasPressedThisFrame, mouse.position.ReadValue(), out clickPosition);
#else
#if ENABLE_LEGACY_INPUT_MANAGER
        return ResolveClickPosition(Input.GetMouseButtonDown(0), Input.mousePosition, out clickPosition);
#else
        return false;
#endif
#endif
    }
}
