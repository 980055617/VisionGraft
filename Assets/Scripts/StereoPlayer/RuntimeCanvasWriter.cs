using UnityEngine;

public static class RuntimeCanvasWriter
{
    public static void ApplyWorldSpaceCamera(Canvas canvas, Camera camera)
    {
        if (canvas == null)
        {
            return;
        }

        canvas.renderMode = RenderMode.WorldSpace;
        canvas.worldCamera = camera;
    }

    public static void ApplyWorldCameraIfMissing(Canvas canvas, Camera camera)
    {
        if (canvas == null || canvas.worldCamera != null)
        {
            return;
        }

        canvas.worldCamera = camera;
    }

    public static void ApplyWorldCamera(Canvas canvas, Camera camera)
    {
        if (canvas == null)
        {
            return;
        }

        canvas.worldCamera = camera;
    }
}
